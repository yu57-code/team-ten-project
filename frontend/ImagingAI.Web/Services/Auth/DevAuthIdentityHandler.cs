using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using static ImagingAI.Web.Services.Auth.DevAuthIdentityClient;

namespace ImagingAI.Web.Services.Auth;

public sealed class DevAuthIdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, DevAuthIdentityClient identityClient)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ImagingAI.Web.DevAuth";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!IsLocalHttps(Context) || !Request.Cookies.TryGetValue(CookieName, out var cookie))
            return AuthenticateResult.NoResult();

        var verification = await identityClient.VerifyAsync(cookie, Context.RequestAborted);
        if (verification.Identity is { } identity)
        {
            return AuthenticateResult.Success(new AuthenticationTicket(
                new RequestPrincipal(identity, cookie), SchemeName));
        }

        // Keep failure details and header values off authentication logs.
        return AuthenticateResult.Fail(verification.Status switch
        {
            CheckStatus.Disabled => "Development identity endpoint unavailable.",
            CheckStatus.RateLimited => "Development identity verification rate-limited.",
            CheckStatus.Unavailable => "Development identity verification temporarily unavailable.",
            CheckStatus.InvalidResponse => "Invalid development identity response.",
            _ => "Development identity rejected."
        });
    }

    internal static bool IsLocalHttps(HttpContext context) => context.Request.IsHttps &&
        IsLoopback(context.Connection.LocalIpAddress) && IsLoopback(context.Connection.RemoteIpAddress) &&
        (string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(context.Request.Host.Host.Trim('[', ']'), out var host) && IsLoopback(host)));

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    internal static ClaimsIdentity CreateIdentity(VerifiedWebIdentity account) => new(
    [
        new Claim(ClaimTypes.NameIdentifier, account.UserId.ToString(CultureInfo.InvariantCulture)),
        new Claim(ClaimTypes.Name, account.Username),
        new Claim(ClaimTypes.Role, account.RoleCode)
    ], SchemeName);

    // Blazor hands HttpContext.User to its scoped provider in-process. The credential is a
    // private, one-use handoff, separate from Claims and from all public serializable members.
    internal sealed class RequestPrincipal(VerifiedWebIdentity account, string cookie)
        : ClaimsPrincipal(CreateIdentity(account))
    {
        private string? credential = cookie;
        internal VerifiedWebIdentity Account { get; } = account;
        internal string? TakeCredential() => Interlocked.Exchange(ref credential, null);
    }
}
