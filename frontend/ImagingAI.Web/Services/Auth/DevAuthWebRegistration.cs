using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using ImagingAI.Web.Services.Api;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static ImagingAI.Web.Services.Auth.DevAuthIdentityHandler;

namespace ImagingAI.Web.Services.Auth;

public static class DevAuthWebRegistration
{
    public sealed class EnabledMode;

    public const string IdentityPath = "/login?view=identity";

    private static bool IsEnabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() &&
        string.Equals(configuration["Auth:EnableDevelopmentLogin"], "true", StringComparison.OrdinalIgnoreCase);

    public static bool AddDevelopmentWebIdentity(this IServiceCollection services, IHostEnvironment environment,
        IConfiguration configuration)
    {
        if (!IsEnabled(environment, configuration))
            return false;

        // Interactive component descriptors from Mock instances must not be accepted by this host.
        // The backend owns and validates its own login cookie and uses its own key ring.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<EnabledMode>();
        services.AddScoped<ApiAuthService>();
        services.AddScoped<DevAuthFlow>();
        services.AddAuthentication(options =>
        {
            // The named scheme is selected explicitly by the Web identity boundary.
            AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);
        }).AddScheme<AuthenticationSchemeOptions, DevAuthIdentityHandler>(SchemeName, _ => { });
        services.AddCascadingAuthenticationState();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<DevAuthStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(scope => scope.GetRequiredService<DevAuthStateProvider>());
        services.AddScoped<CircuitHandler, DevAuthCircuitHandler>();
        services.AddHttpClient<DevAuthIdentityClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.MaxResponseContentBufferSize = 16 * 1024;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        })
        .RemoveAllLoggers();
        return true;
    }

    public static void UseDevelopmentWebLoopback(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsLocalHttps(context))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });

    public static void UseDevelopmentWebIdentity(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var page = DevAuthPagePolicy.FromHttpRequest(request);
            if (page.Kind != DevAuthPagePolicy.PageKind.Unknown &&
                (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
            {
                await AuthorizePage(context, next, page);
                return;
            }

            if (IsInteractiveTransport(context) && IsSameOrigin(request))
            {
                if (request.Path == "/_blazor")
                    await ContinueWithIdentity(context, next);
                else
                    await next(context);
                return;
            }

            // .NET 8 maps this script as an endpoint, after static-file middleware.
            if (request.Path == "/_framework/blazor.web.js" &&
                (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
            {
                await next(context);
                return;
            }

            if (IsAuthProxyRequest(context.Request))
            {
                await next(context);
                return;
            }

            // This stage exposes only the isolated root and the authentication endpoints.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });

    private static async Task AuthorizePage(HttpContext context, RequestDelegate next, DevAuthPagePolicy.Target page)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!page.IsOpen)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var identity = await context.AuthenticateAsync(SchemeName);
        var verified = identity.Principal as RequestPrincipal;
        var unavailable = identity.Failure is { Message: not "Development identity rejected." };
        switch (DevAuthPagePolicy.Evaluate(page, verified, identityUnavailable: unavailable))
        {
            case DevAuthPagePolicy.Decision.Allow:
                if (verified is not null && DevAuthPagePolicy.TrustedRole(verified) is not null)
                {
                    context.User = verified;
                    if (page.Kind == DevAuthPagePolicy.PageKind.Identity)
                    {
                        var destination = GetPostLoginDestination(verified);
                        if (destination != IdentityPath)
                        {
                            context.Response.Redirect(destination);
                            return;
                        }
                    }
                }
                context.Items[typeof(DevAuthPagePolicy)] =
                    page.Kind == DevAuthPagePolicy.PageKind.Identity ? IdentityPath : page.Path;
                await next(context);
                break;
            case DevAuthPagePolicy.Decision.LoginRequired:
                context.Response.Redirect("/login");
                break;
            case DevAuthPagePolicy.Decision.Forbidden:
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                break;
            case DevAuthPagePolicy.Decision.Unavailable:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    public static string GetPostLoginDestination(ClaimsPrincipal user)
    {
        var home = DevAuthPagePolicy.TrustedRole(user) switch
        {
            nameof(Models.UserRole.MEDICAL_REVIEWER) => "/reviewer",
            nameof(Models.UserRole.SYSTEM_ADMIN) => "/admin",
            _ => null
        };
        if (home is null)
            return IdentityPath;

        var target = DevAuthPagePolicy.Resolve(home, null);
        return DevAuthPagePolicy.Evaluate(target, user) == DevAuthPagePolicy.Decision.Allow
            ? target.Path : IdentityPath;
    }

    private static async Task ContinueWithIdentity(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await context.AuthenticateAsync(SchemeName);
        if (result.Succeeded)
            context.User = result.Principal!;

        await next(context);
    }

    private static bool IsInteractiveTransport(HttpContext context)
    {
        var request = context.Request;
        return (request.Path == "/_blazor/negotiate" && HttpMethods.IsPost(request.Method)) ||
            (request.Path == "/_blazor/initializers" && HttpMethods.IsGet(request.Method)) ||
            (request.Path == "/_blazor" && (HttpMethods.IsGet(request.Method) ||
                HttpMethods.IsPost(request.Method) || HttpMethods.IsDelete(request.Method) ||
                (HttpMethods.IsConnect(request.Method) &&
                    context.Features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true, Protocol: "websocket" })));
    }

    private static bool IsSameOrigin(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Origin", out var origin))
        {
            return origin.Count == 1 && Uri.TryCreate(origin[0], UriKind.Absolute, out var source) &&
                source.Scheme == Uri.UriSchemeHttps && source.UserInfo.Length == 0 &&
                source.PathAndQuery == "/" && source.Fragment.Length == 0 &&
                new HostString(source.Host, source.Port).Equals(
                    new HostString(request.Host.Host, request.Host.Port ?? 443));
        }

        // Browser same-origin GETs can omit Origin, including the SignalR initializer request.
        return request.Headers.TryGetValue("Sec-Fetch-Site", out var site) &&
            site.Count == 1 && site[0] == "same-origin";
    }

    private static bool IsAuthProxyRequest(HttpRequest request)
    {
        var path = request.Path.Value?.ToLowerInvariant();
        return HttpMethods.IsGet(request.Method) && path is ("/api/auth/csrf" or "/api/auth/me") ||
               HttpMethods.IsPost(request.Method) && path is ("/api/auth/login" or "/api/auth/otp/verify" or
                   "/api/auth/otp/resend" or "/api/auth/select-role" or "/api/auth/logout");
    }
}
