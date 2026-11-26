using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ImagingAI.Api.Controllers;
using ImagingAI.Api.Services;
using ImagingAI.Api.Models;
using static ImagingAI.Api.Services.AuthSessionStore;

namespace ImagingAI.Api.Auth;

internal static class AuthRegistration
{
    internal const string Scheme = "ImagingAI.DevAuth";
    internal const string CookieName = "__Host-ImagingAI.DevAuth";
    internal const string PendingCookieName = "__Host-ImagingAI.DevRole";
    internal const string CsrfCookieName = "__Host-ImagingAI.DevCsrf";
    internal const string CsrfHeaderName = "X-CSRF-TOKEN";
    internal const string SessionClaim = "imagingai:session_id";
    private const string DevelopmentClaim = "imagingai:development";
    private const string PathPrefix = "/api/auth";
    private static readonly object StoreUnavailable = new();

    internal static bool AddDevelopmentAuth(IMvcBuilder mvc, IHostEnvironment environment,
        IConfiguration configuration)
    {
        var enabled = environment.IsDevelopment() &&
                      string.Equals(configuration["Auth:EnableDevelopmentLogin"], "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
        {
            mvc.ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new RemoveAuthController()));
            return false;
        }

        var services = mvc.Services;
        // The auth controller requests this scheme explicitly. No implicit scheme for business APIs.
        AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(services => new AuthProtection(CreateLoginProtection()));
        services.AddSingleton<AuthSessionStore>(services => new(services.GetRequiredService<TimeProvider>(), 256));
        services.AddScoped<AuthService>();
        services.AddSingleton<DevelopmentAuthRateLimiter>();
        services.AddSingleton(services => new DevelopmentAntiforgery(services.GetRequiredService<AuthProtection>()));
        services.AddAuthentication().AddCookie(Scheme);
        services.AddOptions<CookieAuthenticationOptions>(Scheme)
            .Configure<AuthProtection, TimeProvider>(ConfigureCookie);
        return true;
    }

    internal static void ConfigureCookie(CookieAuthenticationOptions options, AuthProtection protection, TimeProvider clock)
    {
        options.Cookie.Name = CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.Cookie.Domain = null;
        options.Cookie.IsEssential = true;
        options.TimeProvider = clock;
        options.ExpireTimeSpan = SessionLifetime;
        options.SlidingExpiration = false;
        options.TicketDataFormat = new TicketDataFormat(protection.Provider.CreateProtector(
            "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", Scheme, "v2"));
        options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
        options.Events.OnRedirectToLogin = context => WriteProblemAsync(context.HttpContext, 401, "invalid_session");
        options.Events.OnRedirectToAccessDenied = context => WriteProblemAsync(context.HttpContext, 403, "forbidden");
    }

    internal static void UseDevelopmentAuthBoundary(this IApplicationBuilder app, bool enabled)
    {
        app.Use((context, next) => InvokeBoundaryAsync(context, enabled, next));
    }

    internal static async Task InvokeBoundaryAsync(HttpContext context, bool enabled, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        if (!enabled || !IsLocalHttps(context))
        {
            await WriteProblemAsync(context, 404, "not_found");
            return;
        }

        var operation = IsPost(context, "login") ? "login" : IsPost(context, "select-role") ? "select-role" : null;
        if (operation is not null)
        {
            var limiter = context.RequestServices.GetRequiredService<DevelopmentAuthRateLimiter>();
            if (!limiter.TryAcquire(operation, SourceKey(context), out var retryAfter))
            {
                SetRetryAfter(context, retryAfter);
                await WriteProblemAsync(context, 429, "rate_limited");
                return;
            }
        }

        var auth = await context.AuthenticateAsync(Scheme);
        if (context.Items.ContainsKey(StoreUnavailable))
        {
            await WriteProblemAsync(context, 503, "account_store_unavailable");
            return;
        }
        if (auth.Succeeded)
            context.User = auth.Principal!;

        await next(context);
    }

    internal static ClaimsPrincipal CreatePrincipal(DevelopmentSession session) => new(new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, session.Username),
            new Claim(ClaimTypes.Role, session.ActiveRoleCode),
            new Claim(SessionClaim, session.Id),
            new Claim(DevelopmentClaim, "true"),
            new Claim("amr", "pwd")
        }, Scheme));

    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var store = services.GetRequiredService<AuthSessionStore>();
        var principal = context.Principal!;
        var session = store.FindSession(principal.FindFirstValue(SessionClaim));
        if (session is null || !IsLocalHttps(context.HttpContext) ||
            principal.Identity?.AuthenticationType != Scheme ||
            principal.FindFirstValue(DevelopmentClaim) != "true" ||
            principal.FindFirstValue(ClaimTypes.NameIdentifier) != session.UserId.ToString(CultureInfo.InvariantCulture) ||
            principal.FindFirstValue(ClaimTypes.Name) != session.Username ||
            !principal.FindAll(ClaimTypes.Role).Select(role => role.Value).SequenceEqual(new[] { session.ActiveRoleCode }, StringComparer.Ordinal))
        {
            context.RejectPrincipal();
            return;
        }

        // Logout only needs the verified ticket and registry entry for antiforgery and revocation.
        if (IsPost(context.HttpContext, "logout"))
            return;

        var auth = services.GetRequiredService<AuthService>();
        var status = await auth.RevalidateSessionAsync(session, context.HttpContext.RequestAborted);
        if (status == RoleConfirmationStatus.AccountStoreUnavailable)
        {
            context.HttpContext.Items[StoreUnavailable] = true;
            context.RejectPrincipal();
        }
        else if (status != RoleConfirmationStatus.RoleConfirmed)
        {
            store.RevokeSession(session.Id);
            context.RejectPrincipal();
        }
        else if (!ReferenceEquals(store.FindSession(session.Id), session))
        {
            context.RejectPrincipal();
        }
    }

    internal static bool IsLocalHttps(HttpContext context) =>
        context.Request.IsHttps && IsLoopback(context.Connection.RemoteIpAddress) &&
        IsLoopback(context.Connection.LocalIpAddress) &&
        (string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(context.Request.Host.Host.Trim('[', ']'), out var host) && IsLoopback(host)));

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    internal static string SourceKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4().ToString() : address?.ToString() ?? "";
    }

    private static bool IsPost(HttpContext context, string operation) =>
        HttpMethods.IsPost(context.Request.Method) &&
        string.Equals(context.Request.Path.Value?.TrimEnd('/'), $"{PathPrefix}/{operation}", StringComparison.OrdinalIgnoreCase);

    internal static CookieOptions AuxiliaryCookie(TimeSpan? maxAge = null) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true, MaxAge = maxAge
    };

    internal static ProblemDetails Problem(int status, string code) =>
        new() { Status = status, Title = code, Extensions = { ["code"] = code } };

    private static Task WriteProblemAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Problem(status, code), options: null,
            contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }

    internal static void SetRetryAfter(HttpContext context, int seconds) =>
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

    private static IDataProtectionProvider CreateLoginProtection()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localData))
            throw new InvalidOperationException("A local user data directory is required for development authentication.");
        var keyPath = Path.Combine(localData, "ImagingAI", "DevelopmentAuth", "Keys");
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(keyPath);
        else
        {
            Directory.CreateDirectory(keyPath, privateMode);
            if ((File.GetUnixFileMode(keyPath) & ~privateMode) != 0)
                throw new InvalidOperationException("The development authentication key directory must be private.");
        }

        return DataProtectionProvider.Create(new DirectoryInfo(keyPath), options =>
        {
            options.SetApplicationName("ImagingAI.DevelopmentAuth");
            if (OperatingSystem.IsWindows())
                options.ProtectKeysWithDpapi();
        });
    }

    private sealed class RemoveAuthController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var controller = feature.Controllers.SingleOrDefault(type => type.AsType() == typeof(AuthController));
            if (controller is not null)
                feature.Controllers.Remove(controller);
        }
    }
}

internal sealed record AuthProtection(IDataProtectionProvider Provider);

public sealed class DevelopmentAntiforgery : IDisposable
{
    private readonly ServiceProvider resources;
    internal IAntiforgery Service { get; }

    internal DevelopmentAntiforgery(AuthProtection protection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(protection.Provider);
        services.AddSingleton<IAntiforgeryAdditionalDataProvider>(_ => new SessionAntiforgeryBinding());
        services.AddAntiforgery(options =>
        {
            options.HeaderName = AuthRegistration.CsrfHeaderName;
            options.Cookie.Name = AuthRegistration.CsrfCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
        });
        resources = services.BuildServiceProvider();
        Service = resources.GetRequiredService<IAntiforgery>();
    }

    public void Dispose() => resources.Dispose();

    private sealed class SessionAntiforgeryBinding : IAntiforgeryAdditionalDataProvider
    {
        public string GetAdditionalData(HttpContext context) =>
            context.User.FindFirstValue(AuthRegistration.SessionClaim) is { } id
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) : "";

        public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
            string.Equals(GetAdditionalData(context), additionalData, StringComparison.Ordinal);
    }
}

public sealed class DevelopmentAuthRateLimiter(TimeProvider clock)
{
    internal const int Capacity = 512;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private readonly Dictionary<(string Operation, string Key), Counter> counters = new();

    internal bool TryAcquire(string operation, string key, out int retryAfter)
    {
        var limit = operation switch { "login-account" => 5, "select-role" => 12, _ => 20 };
        lock (gate)
        {
            var now = clock.GetTimestamp();
            foreach (var (bucket, counter) in counters)
            {
                if (clock.GetElapsedTime(counter.Start, now) >= Window)
                    counters.Remove(bucket);
            }

            var partition = (operation, key);
            if (!counters.TryGetValue(partition, out var current))
            {
                if (counters.Count >= Capacity)
                {
                    retryAfter = (int)Window.TotalSeconds;
                    return false;
                }
                current = new Counter(now);
                counters.Add(partition, current);
            }
            if (current.Count >= limit)
            {
                retryAfter = Math.Max(1, (int)Math.Ceiling((Window - clock.GetElapsedTime(current.Start, now)).TotalSeconds));
                return false;
            }
            current.Count++;
            retryAfter = 0;
            return true;
        }
    }

    private sealed class Counter(long start)
    {
        internal long Start { get; } = start;
        internal int Count { get; set; }
    }
}
