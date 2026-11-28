using System.Text.Json;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Api;
using ImagingAI.Web.Services.Mock;
using Microsoft.JSInterop;
using static ImagingAI.Web.Models.AuthApiStatus;
using static ImagingAI.Web.Models.UserRole;

var tests = new List<(string, Func<Task>)>();
void Test(string name, Func<Task> run) => tests.Add((name, run));
var synthetic = "auth-transport-synthetic";
var time = new FixedClock(new DateTimeOffset(2026, 11, 26, 4, 0, 0, TimeSpan.Zero));
var validAdmin = new { userId = 7L, username = "synthetic-admin", activeRoleCode = "SYSTEM_ADMIN" };
var validReviewer = new { userId = 8L, username = "synthetic-reviewer", activeRoleCode = "MEDICAL_REVIEWER" };
var dual = new { state = "roleSelectionRequired", user = (object?)null,
    availableRoleCodes = new[] { "MEDICAL_REVIEWER", "SYSTEM_ADMIN" }, expiresInSeconds = 120 };
static JsonElement Reply(int status, object? body = null, string? retryAfter = null, bool duringCsrf = false) =>
    JsonSerializer.SerializeToElement(new { status, json = body, retryAfter, duringCsrf });
static object Success(object identity) => new { state = "authenticated", user = identity,
    availableRoleCodes = (object?)null, expiresInSeconds = (int?)null };
static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("assertion failed");
}

Test("prerender avoids JavaScript entirely", async () =>
{
    var js = new FakeRuntime();
    await using var svc = new ApiAuthService(js);
    Check((await svc.MeAsync()).Status == BrowserInteropUnavailable && js.Imports == 0);
    Check((await svc.LoginAsync("test", "demo")).Status == BrowserInteropUnavailable && js.Imports == 0);
});
Test("csrf parsed, never serialized or printed by result", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, new { requestToken = synthetic }));
    await using var svc = Start(js);
    var r = await svc.GetCsrfAsync();
    Check(r.Status == CsrfReady && r.CsrfToken?.Value == synthetic &&
          !JsonSerializer.Serialize(r).Contains(synthetic) && !r.ToString().Contains(synthetic) &&
          js.Module.Calls.Single().Name == "getCsrf");
});
Test("single-role login identity and logout", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, Success(validAdmin)));
    js.Enqueue(Reply(204));
    await using var svc = Start(js);
    var r = await svc.VerifyOtpAsync(default);
    Check(r.Status == Authenticated && r.Identity is { UserId: 7, ActiveRole: SYSTEM_ADMIN } &&
          r.AvailableRoles is null && r.ExpiresInSeconds is null);
    Check((await svc.LogoutAsync()).Status == LoggedOut);
    Check(js.Imports == 1 && js.Module.Calls.Select(c => c.Name).SequenceEqual(new[] { "verifyOtp", "logout" }));
});
Test("dual roles are pending and selection sends only exact role", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, dual));
    js.Enqueue(Reply(200, Success(validReviewer)));
    await using var svc = Start(js);
    var r = await svc.VerifyOtpAsync(default);
    Check(r.Status == RoleSelectionRequired && r.Identity is null && r.ExpiresInSeconds == 120 &&
          r.AvailableRoles!.SequenceEqual(new[] { MEDICAL_REVIEWER, SYSTEM_ADMIN }));
    Check((await svc.SelectRoleAsync((UserRole)999)).Status == RoleNotAllowed);
    Check((await svc.SelectRoleAsync(MEDICAL_REVIEWER)).Identity?.ActiveRole == MEDICAL_REVIEWER);
    var call = js.Module.Calls.Last();
    Check(call.Name == "selectRole" && call.Args.Length == 1 && Equals(call.Args[0], "MEDICAL_REVIEWER"));
    Check((await svc.SelectRoleAsync(SYSTEM_ADMIN)).Status == InvalidRoleSelection);
    Check(js.Module.Calls.Count == 2);
});
Test("reject role response that contradicts submitted selection", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, dual));
    js.Enqueue(Reply(200, Success(validReviewer)));
    await using var svc = Start(js);
    _ = await svc.VerifyOtpAsync(default);
    Check((await svc.SelectRoleAsync(SYSTEM_ADMIN)).Status == ProtocolError);
});
Test("losing selection response cannot re-submit pending challenge", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, dual));
    js.Enqueue(JsonSerializer.SerializeToElement(new { transportError = "network_error" }));
    await using var svc = Start(js);
    _ = await svc.VerifyOtpAsync(default);
    Check((await svc.SelectRoleAsync(SYSTEM_ADMIN)).Status == NetworkError);
    Check((await svc.SelectRoleAsync(SYSTEM_ADMIN)).Status == InvalidRoleSelection);
    Check(js.Module.Calls.Count(x => x.Name == "selectRole") == 1);
});
Test("user A pending state does not authorize user B", async () =>
{
    var firstRuntime = new FakeRuntime();
    firstRuntime.Enqueue(Reply(200, dual));
    var secondRuntime = new FakeRuntime();
    await using var first = Start(firstRuntime);
    await using var second = Start(secondRuntime);
    _ = await first.VerifyOtpAsync(default);
    Check((await second.SelectRoleAsync(SYSTEM_ADMIN)).Status == InvalidRoleSelection && secondRuntime.Imports == 0);
});
Test("me uses correct browser export and strict role", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, validReviewer));
    await using var svc = Start(js);
    Check((await svc.MeAsync()).Identity?.ActiveRole == MEDICAL_REVIEWER && js.Module.Calls.Single().Name == "me");
});
foreach (var bad in new object[] { "Admin", "Reviewer", "system_admin", "UNKNOWN", 2 })
{
    var badRole = bad;
    Test("unknown/legacy/numeric role rejected " + badRole, async () =>
    {
        var js = new FakeRuntime();
        js.Enqueue(Reply(200, Success(new { userId = 1, username = "synthetic", activeRoleCode = badRole })));
        await using var svc = Start(js);
        Check((await svc.LoginAsync("test", synthetic)).Status == ProtocolError);
    });
}
Test("unknown role in pending array is rejected", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, new { state = "roleSelectionRequired",
        availableRoleCodes = new object[] { "SYSTEM_ADMIN", 1 }, expiresInSeconds = 120 }));
    await using var svc = Start(js);
    Check((await svc.LoginAsync("test", synthetic)).Status == ProtocolError);
});
Test("duplicate or mixed-state pending result is rejected", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, new { state = "roleSelectionRequired",
        availableRoleCodes = new[] { "SYSTEM_ADMIN", "SYSTEM_ADMIN" }, expiresInSeconds = 120 }));
    js.Enqueue(Reply(200, new { state = "roleSelectionRequired", user = validAdmin,
        availableRoleCodes = new[] { "SYSTEM_ADMIN", "MEDICAL_REVIEWER" }, expiresInSeconds = 120 }));
    await using var svc = Start(js);
    Check((await svc.LoginAsync("test", synthetic)).Status == ProtocolError);
    Check((await svc.LoginAsync("test", synthetic)).Status == ProtocolError);
});
Test("malformed JSON envelope cannot become success", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, null));
    js.Enqueue(Reply(200, "not-json-object"));
    js.Enqueue(Reply(200, new { requestToken = " " }));
    await using var svc = Start(js);
    Check((await svc.LoginAsync("test", synthetic)).Status == ProtocolError);
    Check((await svc.MeAsync()).Status == ProtocolError);
    Check((await svc.GetCsrfAsync()).Status == ProtocolError);
});
Test("unsafe browser integer identifier is rejected", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, new { userId = 9007199254740992L, username = "test", activeRoleCode = "SYSTEM_ADMIN" }));
    await using var svc = Start(js);
    Check((await svc.MeAsync()).Status == ProtocolError);
});
foreach (var (http, code, outcome) in new (int, string?, AuthApiStatus)[]
{
    (400, "invalid_request", InvalidRequest), (400, "invalid_csrf", InvalidCsrf),
    (401, "invalid_credentials", InvalidCredentials), (401, "invalid_session", InvalidSession),
    (401, "invalid_role_selection", InvalidRoleSelection), (401, null, Unauthorized),
    (403, "logout_required", LogoutRequired), (403, "role_not_allowed", RoleNotAllowed), (403, null, Forbidden),
    (404, "not_found", NotFound), (429, "rate_limited", RateLimited),
    (503, "account_store_unavailable", ServiceUnavailable), (503, "session_store_unavailable", ServiceUnavailable),
    (503, null, ServiceUnavailable), (502, null, ProxyError)
})
{
    Test($"HTTP status {http} {code ?? "no code"}", async () =>
    {
        var js = new FakeRuntime();
        js.Enqueue(Reply(http, code is null ? null : new { code }, http == 429 ? "17" : null));
        await using var svc = Start(js);
        var r = await svc.MeAsync();
        Check(r.Status == outcome && r.HttpStatus == http && r.ErrorCode == code && r.Identity is null &&
              (http != 429 || r.RetryAfter == TimeSpan.FromSeconds(17)));
    });
}
Test("Retry-After HTTP date uses server interval", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(429, new { code = "rate_limited" }, time.GetUtcNow().AddSeconds(45).ToString("r")));
    await using var svc = new ApiAuthService(js, time);
    svc.EnableBrowserInterop();
    Check((await svc.MeAsync()).RetryAfter == TimeSpan.FromSeconds(45));
});
Test("unknown error code and details are never surfaced", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(503, new { code = synthetic, detail = synthetic }));
    await using var svc = Start(js);
    var r = await svc.MeAsync();
    Check(r.Status == ServiceUnavailable && r.ErrorCode is null && !r.ToString().Contains(synthetic));
});
Test("only actual logout 204 is accepted", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, new { state = "authenticated" }));
    js.Enqueue(Reply(204, duringCsrf: true));
    js.Enqueue(Reply(400, new { code = "invalid_csrf" }));
    js.Enqueue(Reply(401, new { code = "invalid_session" }));
    js.Enqueue(Reply(503, new { code = "account_store_unavailable" }));
    js.Enqueue(JsonSerializer.SerializeToElement(new { transportError = "network_error" }));
    js.Enqueue(Reply(204));
    await using var svc = Start(js);
    foreach (var expected in new[] { ProtocolError, ProtocolError, InvalidCsrf, InvalidSession, ServiceUnavailable, NetworkError, LoggedOut })
        Check((await svc.LogoutAsync()).Status == expected);
});
Test("redirect and network error do not become authenticated", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(JsonSerializer.SerializeToElement(new { transportError = "protocol_error" }));
    js.Enqueue(JsonSerializer.SerializeToElement(new { transportError = "network_error" }));
    await using var svc = Start(js);
    Check((await svc.LoginAsync("test", synthetic)).Status == ProtocolError);
    Check((await svc.LoginAsync("test", synthetic)).Status == NetworkError);
    Check(js.Module.Calls.Count == 2);
});
Test("module is lazy, reused, disposed once, and unavailable after disposal", async () =>
{
    var js = new FakeRuntime();
    js.Enqueue(Reply(200, validAdmin));
    js.Enqueue(Reply(200, validAdmin));
    var svc = Start(js);
    Check(js.Imports == 0);
    _ = await svc.MeAsync();
    _ = await svc.MeAsync();
    await svc.DisposeAsync();
    await svc.DisposeAsync();
    Check(js.Imports == 1 && js.ImportPath == "./js/devAuth.js" && js.Module.Disposals == 1 &&
          (await svc.MeAsync()).Status == BrowserInteropUnavailable);
});
Test("JS disconnect during operation and disposal does not escape", async () =>
{
    var js = new FakeRuntime();
    js.Module.DisconnectOnDispose = true;
    js.Module.Enqueue(() => throw new JSDisconnectedException("private synthetic message"));
    await using var svc = Start(js);
    var r = await svc.MeAsync();
    Check(r.Status == NetworkError && r.Identity is null && !r.ToString().Contains("private"));
});
Test("mock credentials, OTP, role, logout and guest remain intact", async () =>
{
    var auth = new MockAuthService(new MockDataStore());
    Check((await auth.ValidateCredentialsAsync("synthetic-user", synthetic)).Succeeded);
    Check((await auth.VerifyMfaAsync("123456")).Succeeded);
    Check((await auth.CompleteSignInAsync(MEDICAL_REVIEWER)).Role == MEDICAL_REVIEWER &&
          auth.CurrentUser is { MfaEnabled: true });
    await auth.SignOutAsync();
    Check(auth.CurrentUser is null && auth.GetDemoUser(SYSTEM_ADMIN).Role == SYSTEM_ADMIN);
});

var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}"); }
}
Console.WriteLine($"CSharp tests={tests.Count} passed={tests.Count - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static ApiAuthService Start(FakeRuntime js)
{
    var service = new ApiAuthService(js);
    service.EnableBrowserInterop();
    return service;
}
sealed class FixedClock(DateTimeOffset time) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => time;
}
sealed class FakeRuntime : IJSRuntime
{
    public FakeModule Module { get; } = new();
    public int Imports { get; private set; }
    public string? ImportPath { get; private set; }
    public void Enqueue(JsonElement r) => Module.Enqueue(() => r);

    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
        InvokeAsync<T>(identifier, default, args);
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (identifier != "import") throw new InvalidOperationException();
        Imports++;
        ImportPath = (string?)args?.Single();
        return ValueTask.FromResult((T)(object)Module);
    }
}
sealed class FakeModule : IJSObjectReference
{
    private readonly Queue<Func<JsonElement>> replies = new();
    public List<(string Name, object?[] Args)> Calls { get; } = [];
    public bool DisconnectOnDispose { get; set; }
    public int Disposals { get; private set; }
    public void Enqueue(Func<JsonElement> reply) => replies.Enqueue(reply);
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
        InvokeAsync<T>(identifier, default, args);
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((identifier, args ?? []));
        return ValueTask.FromResult((T)(object)replies.Dequeue()());
    }
    public ValueTask DisposeAsync()
    {
        Disposals++;
        if (DisconnectOnDispose) throw new JSDisconnectedException("test");
        return ValueTask.CompletedTask;
    }
}

