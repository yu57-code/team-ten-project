using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ImagingAI.Api.Auth;
using ImagingAI.Api.Controllers;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Npgsql;
using static ImagingAI.Api.Auth.AuthRegistration;

internal static class DevAuthTests
{
    private const string Admin = "413570438";
    private const string Reviewer = "413570359";
    private const string Dual = "413570452";
    private const string Disabled = "413570309";
    private const string AdminRole = "SYSTEM_ADMIN";
    private const string ReviewerRole = "MEDICAL_REVIEWER";
    private const string Password = "Synthetic Phase 3.4 test password";
    private static readonly string Hash = new PasswordHasher<object>().HashPassword(new object(), Password);

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("OtpApi_password_only_cannot_authenticate_or_select", PasswordOnly);
        test("OtpApi_cookie_missing_tampered_expired_and_wrong_code", InvalidOtp);
        test("OtpApi_phase_csrf_resend_and_replay", OtpResend);
        test("OtpApi_parallel_verification_issues_one_session", ParallelOtp);
        test("OtpApi_logout_restart_and_relogin_invalidate_challenges", RevokeOtp);
        test("OtpApi_account_binding_and_delivery_fail_closed", OtpUnavailable);
        test("OtpApi_email_changes_revoke_selection_and_session", ChangesAfterOtp);
        test("OtpApi_legacy_cookie_and_pending_proof_rejected", LegacyCredentials);
        test("DevAuth_admin_signs_in", () => SignInSingle(Admin, AdminRole));
        test("DevAuth_reviewer_signs_in", () => SignInSingle(Reviewer, ReviewerRole));
        test("DevAuth_dual_pending_cookie_cannot_use_me", PendingCannotUseMe);
        test("DevAuth_disabled_dev_uploader_wrong_and_unknown_share_401", InvalidCredentials);
        test("DevAuth_production_removes_controller_and_services", () => DisabledRegistration("Production", true));
        test("DevAuth_opt_out_removes_controller_and_services", () => DisabledRegistration("Development", false));
        test("DevAuth_non_loopback_http_and_external_host_are_rejected", RemoteBoundary);
        test("DevAuth_dual_selects_reviewer", () => Select(ReviewerRole));
        test("DevAuth_dual_selects_admin", () => Select(AdminRole));
        test("DevAuth_unauthorized_role_is_403_and_revokes_pending", UnauthorizedRole);
        test("DevAuth_select_without_pending_is_401", NoPending);
        test("DevAuth_expired_pending_is_rejected", ExpiredPending);
        test("DevAuth_pending_replay_is_401", Replay);
        test("DevAuth_parallel_selection_creates_one_session", ConcurrentSelection);
        test("DevAuth_cookie_is_secure_session_only_and_claims_have_one_role", CookieAndClaims);
        test("DevAuth_tampered_cookie_is_401", TamperedCookie);
        test("DevAuth_expired_cookie_is_401", ExpiredCookie);
        test("DevAuth_signed_cookie_with_unknown_session_is_401", UnknownSession);
        test("DevAuth_revoked_session_cookie_is_401", RevokedSession);
        test("DevAuth_signed_cookie_cannot_change_claims", ClaimMismatch);
        test("DevAuth_restart_invalidates_session_even_with_same_keys", Restart);
        test("DevAuth_disable_revokes_session", () => Invalidated(r => r with { IsActive = false }, Dual));
        test("DevAuth_delete_revokes_session", () => Invalidated(_ => null, Dual));
        test("DevAuth_remove_active_role_revokes_session", () => Invalidated(r => r with
            { PrimaryRoleCode = AdminRole, SecondaryRoleCode = null, HasSecondaryRole = false }, Dual));
        test("DevAuth_change_role_set_revokes_session", () => Invalidated(r => r with
            { SecondaryRoleCode = AdminRole, HasSecondaryRole = true }, Reviewer));
        test("DevAuth_database_unavailable_is_503_and_does_not_accept_session", Unavailable);
        test("DevAuth_logout_revokes_and_clears_cookies_and_is_idempotent", Logout);
        test("DevAuth_logout_cancels_pending", LogoutPending);
        test("DevAuth_login_requires_logout_before_switching", NoSwitch);
        test("DevAuth_missing_and_bad_csrf_are_400", BadCsrf);
        test("DevAuth_old_identity_csrf_is_rejected", CrossIdentityCsrf);
        test("DevAuth_old_session_csrf_is_rejected_for_same_user", CrossSessionCsrf);
        test("DevAuth_login_pair_limit_is_429", PairLimit);
        test("DevAuth_login_source_limit_is_429", () => SourceLimit("login", 20));
        test("DevAuth_role_source_limit_is_429", () => SourceLimit("select-role", 12));
        test("DevAuth_rate_limit_caps_state_and_reclaims_expired_buckets", CounterCapacity);
        test("DevAuth_session_capacity_and_expiry_are_bounded", SessionCapacity);
        test("DevAuth_cookie_issue_failure_revokes_session", CookieIssuanceFailure);
        test("DevAuth_api_challenge_and_forbid_do_not_redirect", NoRedirect);
        test("DevAuth_scheme_and_keys_are_isolated_from_business_endpoints", Isolation);
        test("DevAuth_existing_review_and_exception_actions_stay_closed", ExistingActions);
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Development auth test assertion failed.");
    }

    private static async Task SignInSingle(string username, string role)
    {
        using var f = new Fixture();
        var browser = new Browser();
        var login = await f.Login(browser, username);
        Check(login is { Status: 200, Value: AuthLoginResponse { State: "authenticated", User: { } } } &&
              browser.Cookies.ContainsKey(CookieName) && !browser.Cookies.ContainsKey(PendingCookieName));
        var me = await f.Invoke(browser, "me");
        Check(me is { Status: 200, Value: AuthIdentityResponse identity } &&
              identity.Username == username && identity.ActiveRoleCode == role);
    }

    private static async Task PendingCannotUseMe()
    {
        using var f = new Fixture();
        var browser = new Browser();
        var result = await f.Login(browser, Dual);
        Check(result is { Status: 200, Value: AuthLoginResponse { State: "roleSelectionRequired", User: null, ExpiresInSeconds: 120 } pending } &&
              pending.AvailableRoleCodes!.SequenceEqual(new[] { ReviewerRole, AdminRole }) &&
              browser.Cookies.ContainsKey(PendingCookieName) && !browser.Cookies.ContainsKey(CookieName));
        var cookie = result.Cookies.Single(c => c.Name.Value == PendingCookieName);
        Check(Secure(cookie) && cookie.MaxAge == TimeSpan.FromSeconds(120));
        var json = JsonSerializer.Serialize(result.Value);
        Check(!json.Contains(browser.Cookies[PendingCookieName], StringComparison.Ordinal) &&
              !json.Contains("password", StringComparison.OrdinalIgnoreCase));
        Check((await f.Invoke(browser, "me")).Status == 401);
    }

    private static async Task InvalidCredentials()
    {
        using var f = new Fixture();
        foreach (var (username, password) in new[] {
            (Disabled, Password), ("dev_uploader", Password), (Admin, "wrong"), ("missing", Password) })
        {
            var result = await f.Login(new Browser(), username, password);
            Check(result is { Status: 401, Code: "invalid_credentials" });
        }
        f.ReadCredentials = (_, _) => Task.FromResult<AuthService.AccountCredentials?>(null);
        Check(await f.Login(new Browser(), Admin) is { Status: 401, Code: "invalid_credentials" });
    }

    private static async Task DisabledRegistration(string environment, bool optIn)
    {
        var services = new ServiceCollection().AddLogging();
        var mvc = services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        var config = Config(optIn);
        var enabled = AddDevelopmentAuth(mvc, new TestEnvironment(Path.GetTempPath()) { EnvironmentName = environment }, config);
        var feature = new ControllerFeature();
        mvc.PartManager.PopulateFeature(feature);
        Check(!enabled && !feature.Controllers.Contains(typeof(AuthController).GetTypeInfo()) &&
              feature.Controllers.Contains(typeof(ReviewsController).GetTypeInfo()) &&
              !services.Any(s => s.ServiceType == typeof(AuthService) || s.ServiceType == typeof(AuthSessionStore)));
        var context = NewContext("me");
        await InvokeBoundaryAsync(context, enabled, _ => throw new InvalidOperationException("Unexpected endpoint."));
        Check(context.Response.StatusCode == 404 && context.Response.Headers.CacheControl == "no-store");
    }

    private static async Task RemoteBoundary()
    {
        var changes = new Action<HttpContext>[]
        {
            ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.3"),
            ctx => ctx.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.3"),
            ctx => ctx.Request.Scheme = "http",
            ctx => ctx.Request.Host = new HostString("public.example")
        };
        foreach (var change in changes)
        {
            var context = NewContext("login");
            context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
            change(context);
            await InvokeBoundaryAsync(context, true, _ => throw new InvalidOperationException("Unexpected endpoint."));
            Check(context.Response.StatusCode == 404);
        }
    }

    private static async Task Select(string role)
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Dual);
        var selected = await f.Select(browser, role);
        Check(selected is { Status: 200, Value: AuthLoginResponse { User: { } user } } &&
              user.ActiveRoleCode == role && !browser.Cookies.ContainsKey(PendingCookieName));
        Check((await f.Invoke(browser, "me")).Value is AuthIdentityResponse identity && identity.ActiveRoleCode == role);
    }

    private static async Task UnauthorizedRole()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Dual);
        var old = browser.Clone();
        Check(await f.Select(browser, "system_admin") is { Status: 403, Code: "role_not_allowed" });
        Check((await f.Select(old, AdminRole)).Status == 401);
    }

    private static async Task NoPending()
    {
        using var f = new Fixture();
        Check((await f.Select(new Browser(), AdminRole)).Status == 401);
    }

    private static async Task ExpiredPending()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Dual);
        f.Clock.Advance(TimeSpan.FromSeconds(120));
        Check((await f.Select(browser, AdminRole)).Status == 401);
    }

    private static async Task Replay()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Dual);
        var old = browser.Clone();
        Check((await f.Select(browser, ReviewerRole)).Status == 200);
        Check((await f.Select(old, AdminRole)).Status == 401);
    }

    private static async Task ConcurrentSelection()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Dual);
        var requestToken = await f.Csrf(browser);
        var secondBrowser = browser.Clone();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        f.ReadRoles = async (id, _) =>
        {
            if (Interlocked.Increment(ref readers) == 2) arrived.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            return Roles(Dual, id);
        };
        var first = Task.Run(() => f.Invoke(browser, "select-role", requestToken, ReviewerRole));
        var second = Task.Run(() => f.Invoke(secondBrowser, "select-role", requestToken, AdminRole));
        await arrived.Task.WaitAsync(timeout.Token);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(timeout.Token);
        Check(readers == 2 && results.Count(r => r.Status == 200) == 1 && results.Count(r => r.Status == 401) == 1 &&
              results.Sum(r => r.Cookies.Count(c => c.Name.Value == CookieName && !string.IsNullOrEmpty(c.Value.Value))) == 1);
    }

    private static async Task CookieAndClaims()
    {
        using var f = new Fixture();
        var browser = new Browser();
        var login = await f.Login(browser, Admin);
        var cookie = login.Cookies.Single(c => c.Name.Value == CookieName);
        var ticket = f.Options.TicketDataFormat.Unprotect(browser.Cookies[CookieName])!;
        Check(Secure(cookie) && cookie.Expires is null && cookie.MaxAge is null && !ticket.Properties.IsPersistent &&
              ticket.Properties.ExpiresUtc - ticket.Properties.IssuedUtc == TimeSpan.FromMinutes(30) &&
              !f.Options.SlidingExpiration && f.Options.Cookie.SecurePolicy == CookieSecurePolicy.Always);
        Check(ticket.Principal.FindAll(ClaimTypes.Role).Select(c => c.Value).SequenceEqual(new[] { AdminRole }) &&
              ticket.Principal.FindAll("amr").Select(c => c.Value).SequenceEqual(new[] { "pwd", "email_otp" }) &&
              !ticket.Principal.Claims.Any(c => c.Value.Contains("mfa", StringComparison.OrdinalIgnoreCase)));
        var json = JsonSerializer.Serialize(login.Value);
        Check(!json.Contains(browser.Cookies[CookieName], StringComparison.Ordinal) &&
              !json.Contains(ticket.Principal.FindFirstValue(SessionClaim)!, StringComparison.Ordinal) &&
              !json.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Secure(SetCookieHeaderValue cookie) => cookie.Secure && cookie.HttpOnly &&
        cookie.SameSite == Microsoft.Net.Http.Headers.SameSiteMode.Lax && cookie.Path.Value == "/" &&
        !cookie.Domain.HasValue;

    private static async Task TamperedCookie()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        var cookie = browser.Cookies[CookieName];
        browser.Cookies[CookieName] = (cookie[0] == 'A' ? "B" : "A") + cookie[1..];
        Check((await f.Invoke(browser, "me")).Status == 401);
    }

    private static async Task ExpiredCookie()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        Check((await f.Invoke(browser, "me")).Status == 401);
    }

    private static async Task UnknownSession()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        var ticket = f.Options.TicketDataFormat.Unprotect(browser.Cookies[CookieName])!;
        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        identity.RemoveClaim(identity.FindFirst(SessionClaim)!);
        identity.AddClaim(new Claim(SessionClaim, new string('A', 64)));
        browser.Cookies[CookieName] = f.Options.TicketDataFormat.Protect(ticket);
        Check((await f.Invoke(browser, "me")).Status == 401);
    }

    private static async Task RevokedSession()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        var ticket = f.Options.TicketDataFormat.Unprotect(browser.Cookies[CookieName])!;
        Check(f.Store.RevokeSession(ticket.Principal.FindFirstValue(SessionClaim)!));
        Check((await f.Invoke(browser, "me")).Status == 401);
    }

    private static async Task ClaimMismatch()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        var ticket = f.Options.TicketDataFormat.Unprotect(browser.Cookies[CookieName])!;
        ((ClaimsIdentity)ticket.Principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, ReviewerRole));
        browser.Cookies[CookieName] = f.Options.TicketDataFormat.Protect(ticket);
        Check((await f.Invoke(browser, "me")).Status == 401);
    }

    private static async Task Restart()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        using var restarted = new Fixture(f.Protection);
        Check(restarted.Options.TicketDataFormat.Unprotect(browser.Cookies[CookieName]) is not null &&
              (await restarted.Invoke(browser, "me")).Status == 401);
    }

    private static async Task Invalidated(Func<AuthService.AccountRoles, AuthService.AccountRoles?> change, string name)
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, name);
        if (name == Dual)
            Check((await f.Select(browser, ReviewerRole)).Status == 200);
        f.ReadRoles = (id, _) => Task.FromResult(change(Roles(name, id)));
        Check((await f.Invoke(browser, "me")).Status == 401);
        f.ReadRoles = (id, _) => Task.FromResult<AuthService.AccountRoles?>(Roles(name, id));
        Check((await f.Invoke(browser, "me")).Status == 401);
    }

    private static async Task Unavailable()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        var requestToken = await f.Csrf(browser);
        f.ReadRoles = (_, _) => throw new NpgsqlException("internal synthetic database detail");
        var me = await f.Invoke(browser, "me");
        Check(me.Status == 503 && me.Body.Contains("account_store_unavailable", StringComparison.Ordinal) &&
              !me.Body.Contains("database detail", StringComparison.Ordinal));
        Check((await f.Invoke(browser, "logout", requestToken)).Status == 204);
        f.ReadRoles = (id, _) => Task.FromResult<AuthService.AccountRoles?>(Roles(Admin, id));
        Check((await f.Invoke(browser, "me")).Status == 401);
    }

    private static async Task Logout()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        var stale = browser.Clone();
        var result = await f.Invoke(browser, "logout", await f.Csrf(browser));
        Check(result.Status == 204 && result.Cookies.Any(c => c.Name.Value == CookieName && string.IsNullOrEmpty(c.Value.Value)) &&
              !browser.Cookies.ContainsKey(CookieName));
        Check((await f.Invoke(stale, "me")).Status == 401);
        Check((await f.Invoke(browser, "logout", await f.Csrf(browser))).Status == 204);
    }

    private static async Task LogoutPending()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Dual);
        var old = browser.Clone();
        var result = await f.Invoke(browser, "logout", await f.Csrf(browser));
        Check(result.Status == 204 && result.Cookies.Any(c => c.Name.Value == PendingCookieName && string.IsNullOrEmpty(c.Value.Value)) &&
              !browser.Cookies.ContainsKey(PendingCookieName) && (await f.Select(old, AdminRole)).Status == 401);
    }

    private static async Task NoSwitch()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        Check(await f.Login(browser, Dual) is { Status: 403, Code: "logout_required" });
    }

    private static async Task BadCsrf()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Csrf(browser);
        foreach (var action in new[] { "login", "otp/verify", "otp/resend", "select-role", "logout" })
        {
            Check(await f.Invoke(browser, action) is { Status: 400, Code: "invalid_csrf" });
            Check(await f.Invoke(browser, action, "not a valid token") is { Status: 400, Code: "invalid_csrf" });
        }
    }

    private static async Task CrossIdentityCsrf()
    {
        using var f = new Fixture();
        var browser = new Browser();
        var anonymousCsrf = await f.Csrf(browser);
        var originalCookie = browser.Cookies[CsrfCookieName];
        Check((await f.Begin(browser, Admin, csrf: anonymousCsrf)).Status == 200);
        Check((await f.Finish(browser)).Status == 200);
        browser.Cookies[CsrfCookieName] = originalCookie;
        Check(await f.Invoke(browser, "logout", anonymousCsrf) is { Status: 400, Code: "invalid_csrf" });
        Check((await f.Invoke(browser, "logout", await f.Csrf(browser))).Status == 204);
    }

    private static async Task CrossSessionCsrf()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        var oldToken = await f.Csrf(browser);
        var oldCookie = browser.Cookies[CsrfCookieName];
        Check((await f.Invoke(browser, "logout", oldToken)).Status == 204);
        Check((await f.Login(browser, Admin)).Status == 200);
        browser.Cookies[CsrfCookieName] = oldCookie;
        Check(await f.Invoke(browser, "logout", oldToken) is { Status: 400, Code: "invalid_csrf" });
    }

    private static async Task PairLimit()
    {
        using var f = new Fixture();
        for (var i = 0; i < 5; i++)
            Check((await f.Login(new Browser(), Admin, "wrong")).Status == 401);
        Check(await f.Login(new Browser(), Admin) is { Status: 429, RetryAfter: "60" });
        Check((await f.Login(new Browser(), Reviewer)).Status == 200);
    }

    private static async Task SourceLimit(string action, int limit)
    {
        using var f = new Fixture();
        for (var i = 0; i < limit; i++)
            Check((await f.Invoke(new Browser(), action)).Status == 400);
        var result = await f.Invoke(new Browser(), action);
        Check(result is { Status: 429, RetryAfter: "60" });
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Check((await f.Invoke(new Browser(), action)).Status == 400);
    }

    private static Task CounterCapacity()
    {
        var clock = new ManualClock();
        var limiter = new DevelopmentAuthRateLimiter(clock);
        for (var i = 0; i < DevelopmentAuthRateLimiter.Capacity; i++)
            Check(limiter.TryAcquire("login-account", $"test-{i}", out _));
        Check(!limiter.TryAcquire("login-account", "new", out var retry) && retry == 60);
        clock.Advance(TimeSpan.FromMinutes(1));
        Check(limiter.TryAcquire("login-account", "new", out _));
        return Task.CompletedTask;
    }

    private static async Task SessionCapacity()
    {
        using var f = new Fixture(sessionCapacity: 1);
        var first = new Browser();
        Check((await f.Login(first, Admin)).Status == 200);
        Check(await f.Login(new Browser(), Reviewer) is { Status: 503, Code: "session_store_unavailable" });
        f.Clock.Advance(TimeSpan.FromMinutes(30));
        Check((await f.Invoke(first, "me")).Status == 401);
        Check((await f.Login(new Browser(), Reviewer)).Status == 200);
    }


    private static async Task CookieIssuanceFailure()
    {
        using var f = new Fixture(sessionCapacity: 1);
        var browser = new Browser();
        // Force the real cookie handler to fail while issuing a ticket.
        f.Options.Events.OnSigningIn = _ => throw new InvalidOperationException("synthetic failure");
        Check(await f.Login(browser, Admin) is { Status: 503, Code: "session_unavailable" });
        f.Options.Events.OnSigningIn = _ => Task.CompletedTask;
        Check((await f.Login(browser, Admin)).Status == 200);
    }

    private static async Task NoRedirect()
    {
        using var f = new Fixture();
        using var scope = f.Services.CreateScope();
        foreach (var expected in new[] { 401, 403 })
        {
            var context = NewContext("me");
            context.RequestServices = scope.ServiceProvider;
            if (expected == 401) await context.ChallengeAsync(Scheme);
            else
            {
                using var forbidScope = f.Services.CreateScope();
                context.RequestServices = forbidScope.ServiceProvider;
                await context.ForbidAsync(Scheme);
            }
            Check(context.Response.StatusCode == expected && !context.Response.Headers.ContainsKey("Location") &&
                  context.Response.ContentType == "application/problem+json");
        }
    }

    private static async Task Isolation()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Login(browser, Admin);
        var context = NewContext("me");
        context.Request.Path = "/api/reviews/42/fields";
        context.Request.Headers.Cookie = $"{CookieName}={browser.Cookies[CookieName]}";
        using var scope = f.Services.CreateScope();
        context.RequestServices = scope.ServiceProvider;
        var schemes = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();
        Check(await schemes.GetDefaultAuthenticateSchemeAsync() is null &&
              await schemes.GetDefaultChallengeSchemeAsync() is null &&
              !ReferenceEquals(scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), f.Protection));
        var reached = false;
        await InvokeBoundaryAsync(context, true, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        Check(reached && context.User.Identity?.IsAuthenticated != true);
    }

    private static Task ExistingActions()
    {
        var review = new ReviewsController(null!) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Check(review.GetFields(42) is IStatusCodeActionResult { StatusCode: 403 } &&
              review.GetPreview(42, 1) is IStatusCodeActionResult { StatusCode: 403 } &&
              review.GetDraft(42) is IStatusCodeActionResult { StatusCode: 403 } &&
              review.PutDraft(42) is IStatusCodeActionResult { StatusCode: 403 } &&
              review.Confirm(42) is IStatusCodeActionResult { StatusCode: 403 });
        var exceptions = new ExceptionsController(null!, NullLogger<ExceptionsController>.Instance);
        Check(exceptions.ReadOnlyOperation("42") is IStatusCodeActionResult { StatusCode: 501 });
        return Task.CompletedTask;
    }

    private static async Task PasswordOnly()
    {
        using var f = new Fixture();
        Check((await f.Begin(new Browser(), Admin, "wrong")).Status == 401 && f.Sender.Count == 0);
        var browser = new Browser();
        var result = await f.Begin(browser, Admin);
        Check(result.Value is AuthOtpRequiredResponse { State: "otpRequired", ExpiresInSeconds: 300 });
        var cookie = result.Cookies.Single(c => c.Name.Value == OtpCookieName);
        Check(Secure(cookie) && cookie.Expires is null && cookie.MaxAge is null);
        Check(!browser.Cookies.ContainsKey(CookieName) && !browser.Cookies.ContainsKey(PendingCookieName));
        Check((await f.Invoke(browser, "me")).Status == 401 && (await f.Select(browser, AdminRole)).Status == 401);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Check(json.RootElement.EnumerateObject().Select(x => x.Name)
            .SequenceEqual(new[] { "State", "ExpiresInSeconds", "ResendAfterSeconds" }));
    }

    private static async Task InvalidOtp()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Begin(browser, Admin);
        var code = f.Sender.Code;
        Check((await f.Finish(new Browser(), code)).Status == 401);
        var tampered = browser.Clone();
        var id = tampered.Cookies[OtpCookieName];
        tampered.Cookies[OtpCookieName] = (id[0] == 'A' ? "B" : "A") + id[1..];
        Check((await f.Finish(tampered, code)).Status == 401);
        Check((await f.Finish(browser, code == "000000" ? "000001" : "000000")).Status == 401);
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Check((await f.Finish(browser, code)).Status == 401);
    }

    private static async Task OtpResend()
    {
        using var f = new Fixture();
        var browser = new Browser();
        var oldToken = await f.Csrf(browser);
        var oldCookie = browser.Cookies[CsrfCookieName];
        await f.Begin(browser, Dual, csrf: oldToken);
        browser.Cookies[CsrfCookieName] = oldCookie;
        Check((await f.Invoke(browser, "otp/verify", oldToken, f.Sender.Code)).Status == 400);
        var otpToken = await f.Csrf(browser);
        var otpCookie = browser.Cookies[CsrfCookieName];
        var oldCode = f.Sender.Code;
        Check(await f.Invoke(browser, "otp/resend", otpToken) is { Status: 429, RetryAfter: "60" });
        f.Clock.Advance(TimeSpan.FromSeconds(60));
        Check(await f.Invoke(browser, "otp/resend", otpToken) is
            { Status: 200, Value: AuthOtpRequiredResponse { ExpiresInSeconds: 240 } });
        Check((await f.Finish(browser, oldCode)).Status == 401);
        var replay = browser.Clone();
        Check((await f.Finish(browser)).Value is AuthLoginResponse { State: "roleSelectionRequired" });
        Check((await f.Finish(replay)).Status == 401);
        browser.Cookies[CsrfCookieName] = otpCookie;
        Check((await f.Invoke(browser, "select-role", otpToken, ReviewerRole)).Status == 400);
        Check((await f.Select(browser, ReviewerRole)).Status == 200);
    }

    private static async Task ParallelOtp()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Begin(browser, Admin);
        var csrf = await f.Csrf(browser);
        var other = browser.Clone();
        var results = await Task.WhenAll(
            Task.Run(() => f.Invoke(browser, "otp/verify", csrf, f.Sender.Code)),
            Task.Run(() => f.Invoke(other, "otp/verify", csrf, f.Sender.Code)));
        Check(results.Count(r => r.Status == 200) == 1 && results.Count(r => r.Status == 401) == 1);
        Check(results.Sum(r => r.Cookies.Count(c => c.Name.Value == CookieName && !string.IsNullOrEmpty(c.Value.Value))) == 1);
    }

    private static async Task RevokeOtp()
    {
        using var f = new Fixture();
        var browser = new Browser();
        await f.Begin(browser, Admin);
        var old = browser.Clone();
        using var restarted = new Fixture(f.Protection);
        Check((await restarted.Finish(old.Clone(), f.Sender.Code)).Status == 401);
        Check((await f.Invoke(browser, "logout", await f.Csrf(browser))).Status == 204);
        Check(!browser.Cookies.ContainsKey(OtpCookieName) && (await f.Finish(old)).Status == 401);
        await f.Begin(browser, Admin);
        var replaced = browser.Clone();
        await f.Begin(browser, Admin);
        Check((await f.Finish(replaced)).Status == 401 && (await f.Finish(browser)).Status == 200);
    }

    private static EmailOtpAccount Binding(string user) =>
        TestOtpSender.Binding(new(long.Parse(user), user, Hash, true,
            user == Admin ? AdminRole : ReviewerRole, user == Dual, user == Dual ? AdminRole : null));

    private static async Task OtpUnavailable()
    {
        using var missingSender = new Fixture(configureSender: false);
        Check(await missingSender.Begin(new Browser(), Admin) is { Status: 503, Code: "verification_unavailable" });
        foreach (var binding in new[] { Binding(Admin) with { Email = null }, Binding(Admin) with { VerifiedAt = null } })
        {
            using var f = new Fixture();
            f.ReadOtp = (_, _) => Task.FromResult<EmailOtpAccount?>(binding);
            Check(await f.Begin(new Browser(), Admin) is { Status: 503, Code: "verification_unavailable" } && f.Sender.Count == 0);
        }
        foreach (var failure in new Exception[] { new IOException(), new TimeoutException(), new OperationCanceledException() })
        {
            using var f = new Fixture();
            f.Sender.Delivery = _ => Task.FromException(failure);
            var browser = new Browser();
            Check(await f.Begin(browser, Admin) is { Status: 503, Code: "verification_unavailable" });
            Check(!browser.Cookies.ContainsKey(OtpCookieName) && !browser.Cookies.ContainsKey(CookieName));
        }
        using var outage = new Fixture();
        var pending = new Browser();
        await outage.Begin(pending, Admin);
        outage.ReadOtp = (_, _) => throw new IOException();
        Check((await outage.Finish(pending)).Status == 503);
        outage.ReadOtp = null;
        Check((await outage.Finish(pending)).Status == 401);
    }

    private static async Task ChangesAfterOtp()
    {
        using var f = new Fixture();
        var selection = new Browser();
        await f.Login(selection, Dual);
        f.ReadOtp = (_, _) => Task.FromResult<EmailOtpAccount?>(Binding(Dual) with { VerifiedAt = DateTime.UnixEpoch.AddSeconds(1) });
        Check((await f.Select(selection, ReviewerRole)).Status == 401);
        f.ReadOtp = null;
        var signedIn = new Browser();
        await f.Login(signedIn, Admin);
        f.ReadOtp = (_, _) => Task.FromResult<EmailOtpAccount?>(Binding(Admin) with { Email = "changed@example.invalid" });
        Check((await f.Invoke(signedIn, "me")).Status == 401);
        f.ReadOtp = null;
        Check((await f.Invoke(signedIn, "me")).Status == 401);
    }

    private static async Task LegacyCredentials()
    {
        using var f = new Fixture();
        var oldPending = new Browser();
        var oldToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(oldToken)));
        var pending = (Dictionary<string, AuthSessionStore.PendingRoleSelection>)typeof(AuthSessionStore)
            .GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Store)!;
        pending.Add(digest, new(new VerifiedAccount(long.Parse(Dual), Dual, new[] { ReviewerRole, AdminRole }),
            f.Clock.GetUtcNow(), f.Clock.GetUtcNow() + AuthSessionStore.PendingLifetime, f.Clock.GetTimestamp(), null));
        oldPending.Cookies[PendingCookieName] = oldToken;
        Check((await f.Select(oldPending, ReviewerRole)).Status == 401);
        Check(f.Store.TryCreateSession(new RoleConfirmationResult(RoleConfirmationStatus.RoleConfirmed,
            new VerifiedAccount(long.Parse(Admin), Admin, new[] { AdminRole }), activeRoleCode: AdminRole)) is null);
        var signedIn = new Browser();
        await f.Login(signedIn, Admin);
        var ticket = f.Options.TicketDataFormat.Unprotect(signedIn.Cookies[CookieName])!;
        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        identity.RemoveClaim(identity.FindAll("amr").Single(c => c.Value == "email_otp"));
        signedIn.Cookies[CookieName] = f.Options.TicketDataFormat.Protect(ticket);
        Check((await f.Invoke(signedIn, "me")).Status == 401);
    }

    private static IConfiguration Config(bool enabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:EnableDevelopmentLogin"] = enabled.ToString() }).Build();

    private static DefaultHttpContext NewContext(string action)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 7090);
        context.Request.Path = "/api/auth/" + action;
        context.Request.Method = action is "csrf" or "me" ? "GET" : "POST";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static AuthService.AccountRoles Roles(string username, long id) =>
        new(id, username, username != Disabled, username == Admin ? AdminRole : ReviewerRole,
            username == Dual, username == Dual ? AdminRole : null);

    private sealed class Browser
    {
        internal Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);
        internal Browser Clone()
        {
            var copy = new Browser();
            foreach (var (key, value) in Cookies) copy.Cookies.Add(key, value);
            return copy;
        }
    }

    private sealed record Result(int Status, object? Value, string? Code, string? RetryAfter,
        string Body, IReadOnlyList<SetCookieHeaderValue> Cookies);

    private sealed class Fixture : IDisposable
    {
        internal ServiceProvider Services { get; }
        internal IDataProtectionProvider Protection { get; }
        internal ManualClock Clock { get; } = new();
        internal AuthSessionStore Store => Services.GetRequiredService<AuthSessionStore>();
        internal CookieAuthenticationOptions Options =>
            Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(Scheme);

        internal Func<long, CancellationToken, Task<AuthService.AccountRoles?>> ReadRoles { get; set; } =
            (id, _) => Task.FromResult<AuthService.AccountRoles?>(Roles(id.ToString(CultureInfo.InvariantCulture), id));
        internal Func<string, CancellationToken, Task<AuthService.AccountCredentials?>> ReadCredentials { get; set; } =
            (name, _) =>
            {
                var roles = Roles(name, long.Parse(name, CultureInfo.InvariantCulture));
                return Task.FromResult<AuthService.AccountCredentials?>(new(roles.UserId, name, Hash, roles.IsActive,
                    roles.PrimaryRoleCode, roles.HasSecondaryRole, roles.SecondaryRoleCode));
            };

        internal TestOtpSender Sender { get; } = new();
        internal Func<long, CancellationToken, Task<EmailOtpAccount?>>? ReadOtp { get; set; }

        internal Fixture(IDataProtectionProvider? protection = null, int sessionCapacity = 256, bool configureSender = true)
        {
            Protection = protection ?? new EphemeralDataProtectionProvider();
            var environment = new TestEnvironment(Path.GetTempPath());
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton(new AuthProtection(Protection));
            services.AddSingleton<TimeProvider>(Clock);
            var mvc = services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            Check(AddDevelopmentAuth(mvc, environment, Config(true)));
            services.AddSingleton(new AuthSessionStore(Clock, sessionCapacity));
            services.AddScoped(provider => new AuthService(ReadCredentials, environment, NullLogger<AuthService>.Instance,
                ReadRoles, provider.GetRequiredService<AuthSessionStore>()));
            if (configureSender) services.AddSingleton<IEmailOtpSender>(Sender);
            services.AddScoped(provider => new EmailOtpService(
                async (id, token) => ReadOtp is { } read ? await read(id, token) :
                    await ReadCredentials(id.ToString(CultureInfo.InvariantCulture), token) is { } row
                        ? TestOtpSender.Binding(row) : null,
                provider.GetRequiredService<EmailOtpChallengeStore>(), provider.GetService<IEmailOtpSender>(),
                Clock, provider.GetRequiredService<EmailOtpOptions>()));
            Services = services.BuildServiceProvider();
        }

        internal async Task<string> Csrf(Browser browser)
        {
            var result = await Invoke(browser, "csrf");
            Check(result is { Status: 200, Value: AuthCsrfResponse });
            return ((AuthCsrfResponse)result.Value!).RequestToken;
        }

        internal async Task<Result> Begin(Browser browser, string name, string password = Password, string? csrf = null) =>
            await Invoke(browser, "login", csrf ?? await Csrf(browser), name, password);

        internal async Task<Result> Finish(Browser browser, string? code = null) =>
            await Invoke(browser, "otp/verify", await Csrf(browser), code ?? Sender.Code);

        internal async Task<Result> Login(Browser browser, string name, string password = Password)
        {
            var result = await Begin(browser, name, password);
            return result is { Status: 200, Value: AuthOtpRequiredResponse } ? await Finish(browser) : result;
        }

        internal async Task<Result> Select(Browser browser, string role) =>
            await Invoke(browser, "select-role", await Csrf(browser), role);

        internal async Task<Result> Invoke(Browser browser, string action,
            string? csrf = null, string? input = null, string password = Password)
        {
            using var scope = Services.CreateScope();
            var provider = scope.ServiceProvider;
            var context = NewContext(action);
            context.RequestServices = provider;
            context.Request.Headers.Cookie = string.Join("; ", browser.Cookies.Select(c => $"{c.Key}={c.Value}"));
            if (csrf is not null)
                context.Request.Headers[CsrfHeaderName] = csrf;

            IActionResult? result = null;
            await InvokeBoundaryAsync(context, true, async _ =>
            {
                var controller = ActivatorUtilities.CreateInstance<AuthController>(provider);
                controller.ControllerContext = new() { HttpContext = context };
                result = action switch
                {
                    "csrf" => controller.Csrf(),
                    "login" => await controller.Login(new LoginRequest { Username = input, Password = password }, default),
                    "otp/verify" => await controller.VerifyOtp(new VerifyEmailOtpRequest(input), default),
                    "otp/resend" => await controller.ResendOtp(default),
                    "select-role" => await controller.SelectRole(new SelectRoleRequest(input), default),
                    "me" => controller.Me(),
                    "logout" => await controller.Logout(),
                    _ => throw new InvalidOperationException()
                };
            });

            var cookies = context.Response.Headers.SetCookie.Select(value => SetCookieHeaderValue.Parse(value!)).ToArray();
            foreach (var cookie in cookies)
            {
                if (string.IsNullOrEmpty(cookie.Value.Value))
                    browser.Cookies.Remove(cookie.Name.Value!);
                else
                    browser.Cookies[cookie.Name.Value!] = cookie.Value.Value!;
            }
            var status = (result as IStatusCodeActionResult)?.StatusCode ?? context.Response.StatusCode;
            var value = (result as ObjectResult)?.Value;
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            return new Result(status, value, (value as ProblemDetails)?.Title, context.Response.Headers.RetryAfter,
                await reader.ReadToEndAsync(), cookies);
        }

        public void Dispose() => Services.Dispose();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks);
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}
