using System.Globalization;
using System.Security.Claims;
using ImagingAI.Api.Auth;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using static ImagingAI.Api.Auth.AuthRegistration;

namespace ImagingAI.Api.Controllers;

[Route("api/auth")]
public sealed class AuthController(AuthService auth, AuthSessionStore sessions,
    DevelopmentAntiforgery antiforgery, DevelopmentAuthRateLimiter limiter, ILogger<AuthController> logger)
    : ControllerBase
{
    [HttpGet("csrf")]
    public IActionResult Csrf() =>
        Ok(new AuthCsrfResponse(antiforgery.Service.GetAndStoreTokens(HttpContext).RequestToken!));

    [HttpPost("login")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Login([FromBody] LoginRequest? request, CancellationToken cancellationToken)
    {
        if (!await HasValidCsrfAsync())
            return Failure(400, "invalid_csrf");
        if (User.Identity?.IsAuthenticated == true)
            return Failure(403, "logout_required");
        if (!ModelState.IsValid || request?.Username is not { Length: <= 100 } username ||
            string.IsNullOrWhiteSpace(username) || request.Password is not { Length: <= 256 } password ||
            string.IsNullOrWhiteSpace(password))
        {
            return Failure(400, "invalid_request");
        }
        if (!limiter.TryAcquire("login-account", SourceKey(HttpContext) + "|" + username, out var retryAfter))
        {
            SetRetryAfter(HttpContext, retryAfter);
            return Failure(429, "rate_limited");
        }

        CancelOtp();
        CancelPending();
        if (Request.Cookies.ContainsKey(CookieName))
            await HttpContext.SignOutAsync(Scheme);

        var credentials = await auth.VerifyCredentialsAsync(request, cancellationToken);
        if (credentials.Account is not { } account)
            return credentials.Status == CredentialVerificationStatus.AccountStoreUnavailable
                ? Failure(503, "account_store_unavailable") : Failure(401, "invalid_credentials");

        var result = await Otp.CreateAsync(account, HttpContext.Connection.RemoteIpAddress!, cancellationToken);
        if (result is not { Status: EmailOtpStatus.Pending, ChallengeId: { } challenge })
            return OtpFailure(result, starting: true);

        Response.Cookies.Append(OtpCookieName, challenge, AuxiliaryCookie());
        RotateCsrf();
        return OtpRequired(result);
    }

    [HttpPost("otp/verify")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyEmailOtpRequest? request,
        CancellationToken cancellationToken)
    {
        if (!await HasValidCsrfAsync()) return Failure(400, "invalid_csrf");
        if (User.Identity?.IsAuthenticated == true) return Failure(403, "logout_required");
        if (!ModelState.IsValid || !EmailOtpCode.IsValid(request?.Code)) return Failure(400, "invalid_request");
        if (Request.Cookies[OtpCookieName] is not { } challenge || Request.Cookies.ContainsKey(PendingCookieName))
            return Failure(401, "invalid_otp");

        var result = await Otp.VerifyAsync(challenge, HttpContext.Connection.RemoteIpAddress!, request!.Code, cancellationToken);
        if (result.Status != EmailOtpStatus.Verified)
            return OtpFailure(result);

        Response.Cookies.Delete(OtpCookieName, AuxiliaryCookie());
        RotateCsrf();
        var role = await auth.CompleteOtpAsync(Otp, result.Proof, cancellationToken);
        if (role.Status == RoleConfirmationStatus.RoleSelectionRequired)
        {
            Response.Cookies.Append(PendingCookieName, role.PendingRoleToken!,
                AuxiliaryCookie(AuthSessionStore.PendingLifetime));
            Record("role_selection_required", role.UserId);
            return Ok(new AuthLoginResponse("roleSelectionRequired",
                AvailableRoleCodes: role.AvailableRoleCodes, ExpiresInSeconds: (int)AuthSessionStore.PendingLifetime.TotalSeconds));
        }
        return role.Status == RoleConfirmationStatus.RoleConfirmed
            ? await SignInAsync(role) : RoleFailure(role);
    }

    [HttpPost("otp/resend")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> ResendOtp(CancellationToken cancellationToken)
    {
        if (!await HasValidCsrfAsync()) return Failure(400, "invalid_csrf");
        if (User.Identity?.IsAuthenticated == true) return Failure(403, "logout_required");
        if (Request.Cookies[OtpCookieName] is not { } challenge || Request.Cookies.ContainsKey(PendingCookieName))
            return Failure(401, "invalid_otp");

        var result = await Otp.ResendAsync(challenge, HttpContext.Connection.RemoteIpAddress!, cancellationToken);
        if (result.Status is EmailOtpStatus.DeliveryFailed or EmailOtpStatus.Unavailable or EmailOtpStatus.Expired)
        {
            Response.Cookies.Delete(OtpCookieName, AuxiliaryCookie());
            RotateCsrf();
        }
        return result.Status == EmailOtpStatus.Pending ? OtpRequired(result) : OtpFailure(result);
    }

    private EmailOtpService Otp => HttpContext.RequestServices.GetRequiredService<EmailOtpService>();

    private IActionResult OtpRequired(EmailOtpResult result) =>
        Ok(new AuthOtpRequiredResponse("otpRequired", result.ExpiresInSeconds, result.RetryAfterSeconds));

    private IActionResult OtpFailure(EmailOtpResult result, bool starting = false)
    {
        if (result.Status == EmailOtpStatus.RateLimited)
        {
            SetRetryAfter(HttpContext, result.RetryAfterSeconds > 0 ? result.RetryAfterSeconds : 900);
            return Failure(429, "rate_limited");
        }
        if (result.Status == EmailOtpStatus.Busy) return Failure(409, "verification_pending");
        if (starting || result.Status is EmailOtpStatus.Unavailable or EmailOtpStatus.DeliveryFailed)
            return Failure(503, "verification_unavailable");
        return Failure(401, "invalid_otp");
    }

    [HttpPost("select-role")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> SelectRole([FromBody] SelectRoleRequest? request,
        CancellationToken cancellationToken)
    {
        if (!await HasValidCsrfAsync())
            return Failure(400, "invalid_csrf");
        if (User.Identity?.IsAuthenticated == true)
            return Failure(403, "logout_required");
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(request?.RoleCode) || request.RoleCode.Length > 50)
        {
            CancelPending();
            return Failure(400, "invalid_request");
        }

        if (Request.Cookies.ContainsKey(OtpCookieName)) return Failure(401, "invalid_role_selection");
        var pending = Request.Cookies[PendingCookieName];
        var result = await auth.ConfirmSelectedRoleAsync(Otp, pending, request.RoleCode, cancellationToken);
        if (pending is not null)
            Response.Cookies.Delete(PendingCookieName, AuxiliaryCookie());
        return result.Status == RoleConfirmationStatus.RoleConfirmed
            ? await SignInAsync(result)
            : RoleFailure(result);
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (User.Identity is not { IsAuthenticated: true, AuthenticationType: Scheme } ||
            !long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) ||
            User.FindFirstValue(ClaimTypes.Name) is not { } name ||
            User.FindFirstValue(ClaimTypes.Role) is not { } role)
        {
            return Failure(401, "invalid_session");
        }

        return Ok(new AuthIdentityResponse(id, name, role));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (!await HasValidCsrfAsync())
            return Failure(400, "invalid_csrf");

        if (User.FindFirstValue(SessionClaim) is { } sessionId)
            sessions.RevokeSession(sessionId);
        CancelOtp();
        CancelPending();
        await HttpContext.SignOutAsync(Scheme);
        Response.Cookies.Delete(CsrfCookieName, AuxiliaryCookie());
        Record("signed_out", null);
        return NoContent();
    }

    private async Task<IActionResult> SignInAsync(RoleConfirmationResult result)
    {
        var session = sessions.TryCreateSession(result);
        if (session is null)
            return Failure(503, "session_store_unavailable");

        try
        {
            await HttpContext.SignInAsync(Scheme, CreatePrincipal(session), new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                IssuedUtc = session.CreatedAt,
                ExpiresUtc = session.ExpiresAt
            });
        }
        catch (Exception) when (!Response.HasStarted)
        {
            sessions.RevokeSession(session.Id);
            Response.Cookies.Delete(CookieName, AuxiliaryCookie());
            return Failure(503, "session_unavailable");
        }
        catch
        {
            sessions.RevokeSession(session.Id);
            throw;
        }

        Response.Cookies.Delete(CsrfCookieName, AuxiliaryCookie());
        Record("signed_in", session.UserId);
        return Ok(new AuthLoginResponse("authenticated",
            new AuthIdentityResponse(session.UserId, session.Username, session.ActiveRoleCode)));
    }

    private async Task<bool> HasValidCsrfAsync()
    {
        if (!Request.Headers.TryGetValue(CsrfHeaderName, out var tokens) ||
            tokens.Count != 1 || string.IsNullOrWhiteSpace(tokens[0]))
        {
            return false;
        }
        return await antiforgery.Service.IsRequestValidAsync(HttpContext);
    }

    private void RotateCsrf() => Response.Cookies.Delete(CsrfCookieName, AuxiliaryCookie());

    private void CancelOtp()
    {
        if (Request.Cookies[OtpCookieName] is not { } challenge) return;
        Otp.Revoke(challenge, HttpContext.Connection.RemoteIpAddress!);
        Response.Cookies.Delete(OtpCookieName, AuxiliaryCookie());
        RotateCsrf();
    }

    private void CancelPending()
    {
        if (Request.Cookies[PendingCookieName] is not { } pending)
            return;
        auth.CancelPendingRoleSelection(pending);
        Response.Cookies.Delete(PendingCookieName, AuxiliaryCookie());
    }

    private IActionResult RoleFailure(RoleConfirmationResult result) => result.Status switch
    {
        RoleConfirmationStatus.InvalidRequest => Failure(400, "invalid_request"),
        RoleConfirmationStatus.AccountStoreUnavailable => Failure(503, "account_store_unavailable"),
        RoleConfirmationStatus.PendingSelectionUnavailable => Failure(503, "pending_store_unavailable"),
        RoleConfirmationStatus.InvalidSelection when result.RoleNotAllowed => Failure(403, "role_not_allowed"),
        RoleConfirmationStatus.InvalidSelection => Failure(401, "invalid_role_selection"),
        _ => Failure(401, "invalid_credentials")
    };

    private IActionResult Failure(int status, string code)
    {
        Record(code, null);
        return new ObjectResult(AuthRegistration.Problem(status, code))
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };
    }

    private void Record(string result, long? userId) =>
        logger.LogInformation(new EventId(3401, "DevelopmentAuth"),
            "Development authentication result {Result}; trace {TraceId}; user {UserId}.",
            result, HttpContext.TraceIdentifier, userId);
}
