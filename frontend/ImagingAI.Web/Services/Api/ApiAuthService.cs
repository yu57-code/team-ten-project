using System.Globalization;
using System.Text.Json;
using ImagingAI.Web.Models;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;

namespace ImagingAI.Web.Services.Api;

/// <summary>Browser transport only. Keep one instance per interactive circuit.</summary>
public sealed class ApiAuthService(IJSRuntime js, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private IJSObjectReference? module;
    private IReadOnlyList<UserRole>? pendingRoles;
    private volatile bool browserInteropEnabled;
    private bool disposed;

    /// <summary>Call from interactive OnAfterRender or a browser event, never during prerender.</summary>
    public void EnableBrowserInterop() => browserInteropEnabled = true;

    public Task<AuthApiResult> GetCsrfAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(Operation.Csrf, [], cancellationToken);

    public Task<AuthApiResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default) =>
        ExecuteAsync(Operation.Login, [username, password], cancellationToken);

    public Task<AuthApiResult> LoginWithFormAsync(ElementReference form, CancellationToken cancellationToken = default) =>
        ExecuteAsync(Operation.Login, [form], cancellationToken);

    public Task<AuthApiResult> SelectRoleAsync(UserRole role, CancellationToken cancellationToken = default) =>
        ExecuteAsync(Operation.SelectRole, [role], cancellationToken);

    public Task<AuthApiResult> MeAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(Operation.Me, [], cancellationToken);

    public Task<AuthApiResult> LogoutAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(Operation.Logout, [], cancellationToken);

    private async Task<AuthApiResult> ExecuteAsync(Operation operation, object?[] arguments,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (disposed || !browserInteropEnabled)
                return new(AuthApiStatus.BrowserInteropUnavailable);

            UserRole? selectedRole = null;
            if (operation == Operation.SelectRole)
            {
                if (pendingRoles is null)
                    return new(AuthApiStatus.InvalidRoleSelection);

                var role = (UserRole)arguments[0]!;
                if (!pendingRoles.Contains(role))
                    return new(AuthApiStatus.RoleNotAllowed);

                selectedRole = role;
                arguments[0] = role.ToString();
                // An attempted selection may have reached the server even if the response is lost.
                pendingRoles = null;
            }
            else if (operation is Operation.Login or Operation.Logout)
            {
                pendingRoles = null;
            }

            module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/devAuth.js");
            var export = operation switch
            {
                Operation.Csrf => "getCsrf",
                Operation.Login => "login",
                Operation.SelectRole => "selectRole",
                Operation.Me => "me",
                _ => "logout"
            };
            var response = await module.InvokeAsync<JsonElement>(export, cancellationToken, arguments);
            var result = Interpret(operation, response);
            if (selectedRole is { } requested && result.Status == AuthApiStatus.Authenticated &&
                result.Identity?.ActiveRole != requested)
                return new(AuthApiStatus.ProtocolError, result.HttpStatus);
            if (result.Status == AuthApiStatus.RoleSelectionRequired)
                pendingRoles = result.AvailableRoles;
            return result;
        }
        catch (JSDisconnectedException)
        {
            pendingRoles = null;
            return new(AuthApiStatus.NetworkError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(AuthApiStatus.NetworkError);
        }
        catch (JSException)
        {
            return new(AuthApiStatus.ProtocolError);
        }
        finally
        {
            gate.Release();
        }
    }

    private AuthApiResult Interpret(Operation operation, JsonElement response)
    {
        var status = Property(response, "status");
        int? httpStatus = status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var number)
            ? number : null;
        if (Text(response, "transportError") is { } transportError)
            return new(transportError == "network_error" ? AuthApiStatus.NetworkError : AuthApiStatus.ProtocolError, httpStatus);

        var body = Property(response, "json");
        if (httpStatus is >= 400 and <= 599)
        {
            var code = Text(body, "code");
            var (error, knownCode) = MapError(httpStatus.Value, code);
            return new(error, httpStatus, knownCode ? code : null,
                ReadRetryAfter(Text(response, "retryAfter")));
        }

        if (Property(response, "duringCsrf").ValueKind == JsonValueKind.True)
            return new(AuthApiStatus.ProtocolError, httpStatus);
        if (operation == Operation.Logout && httpStatus == 204)
            return new(AuthApiStatus.LoggedOut, httpStatus);
        if (httpStatus != 200)
            return new(AuthApiStatus.ProtocolError, httpStatus);

        return operation switch
        {
            Operation.Csrf when Text(body, "requestToken") is { } token && !string.IsNullOrWhiteSpace(token) =>
                new(AuthApiStatus.CsrfReady, httpStatus, CsrfToken: new AuthCsrfToken(token)),
            Operation.Me when ReadIdentity(body) is { } identity =>
                new(AuthApiStatus.Authenticated, httpStatus, Identity: identity),
            Operation.Login or Operation.SelectRole when Text(body, "state") == "authenticated" &&
                IsNullOrAbsent(body, "availableRoleCodes") && IsNullOrAbsent(body, "expiresInSeconds") &&
                ReadIdentity(Property(body, "user")) is { } identity =>
                new(AuthApiStatus.Authenticated, httpStatus, Identity: identity),
            Operation.Login when Text(body, "state") == "roleSelectionRequired" && IsNullOrAbsent(body, "user") =>
                ReadPendingRoles(body),
            _ => new(AuthApiStatus.ProtocolError, httpStatus)
        };
    }

    private static AuthApiResult ReadPendingRoles(JsonElement body)
    {
        var roles = Property(body, "availableRoleCodes");
        var expiry = Property(body, "expiresInSeconds");
        if (roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() != 2 ||
            expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt32(out var seconds) || seconds <= 0 ||
            ReadRole(roles[0]) is not { } first || ReadRole(roles[1]) is not { } second || first == second)
        {
            return new(AuthApiStatus.ProtocolError, 200);
        }

        return new(AuthApiStatus.RoleSelectionRequired, 200,
            AvailableRoles: Array.AsReadOnly(new[] { first, second }), ExpiresInSeconds: seconds);
    }

    private static AuthApiIdentity? ReadIdentity(JsonElement body)
    {
        var id = Property(body, "userId");
        // JS JSON numbers have already passed through the browser's exact-integer range.
        return id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var userId) &&
               userId is > 0 and <= 9_007_199_254_740_991 &&
               Text(body, "username") is { } username && !string.IsNullOrWhiteSpace(username) &&
               ReadRole(Property(body, "activeRoleCode")) is { } role
            ? new(userId, username, role)
            : null;
    }

    private static UserRole? ReadRole(JsonElement role) =>
        (role.ValueKind == JsonValueKind.String ? role.GetString() : null) switch
        {
            nameof(UserRole.MEDICAL_REVIEWER) => UserRole.MEDICAL_REVIEWER,
            nameof(UserRole.SYSTEM_ADMIN) => UserRole.SYSTEM_ADMIN,
            _ => null
        };

    private static (AuthApiStatus Status, bool KnownCode) MapError(int status, string? code) => (status, code) switch
    {
        (400, "invalid_request") => (AuthApiStatus.InvalidRequest, true),
        (400, "invalid_csrf") => (AuthApiStatus.InvalidCsrf, true),
        (401, "invalid_credentials") => (AuthApiStatus.InvalidCredentials, true),
        (401, "invalid_role_selection") => (AuthApiStatus.InvalidRoleSelection, true),
        (401, "invalid_session") => (AuthApiStatus.InvalidSession, true),
        (403, "logout_required") => (AuthApiStatus.LogoutRequired, true),
        (403, "role_not_allowed") => (AuthApiStatus.RoleNotAllowed, true),
        (404, "not_found") => (AuthApiStatus.NotFound, true),
        (429, "rate_limited") => (AuthApiStatus.RateLimited, true),
        (503, "account_store_unavailable" or "pending_store_unavailable" or
            "session_store_unavailable" or "session_unavailable") => (AuthApiStatus.ServiceUnavailable, true),
        (400, _) => (AuthApiStatus.InvalidRequest, false),
        (401, _) => (AuthApiStatus.Unauthorized, false),
        (403, _) => (AuthApiStatus.Forbidden, false),
        (404, _) => (AuthApiStatus.NotFound, false),
        (429, _) => (AuthApiStatus.RateLimited, false),
        (502, _) => (AuthApiStatus.ProxyError, false),
        (503, _) => (AuthApiStatus.ServiceUnavailable, false),
        _ => (AuthApiStatus.ProtocolError, false)
    };

    private TimeSpan? ReadRetryAfter(string? value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        if (!DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date))
            return null;
        var delay = date - clock.GetUtcNow();
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private static JsonElement Property(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var property) ? property : default;

    private static string? Text(JsonElement json, string name) =>
        Property(json, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

    private static bool IsNullOrAbsent(JsonElement json, string name) =>
        Property(json, name).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed)
                return;

            disposed = true;
            pendingRoles = null;
            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The browser already released its references when the circuit disconnected.
        }
        finally
        {
            module = null;
            gate.Release();
        }
    }

    private enum Operation { Csrf, Login, SelectRole, Me, Logout }
}
