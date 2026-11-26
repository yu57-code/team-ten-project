using System.Text.Json.Serialization;

namespace ImagingAI.Web.Models;

public enum AuthApiStatus
{
    CsrfReady,
    Authenticated,
    RoleSelectionRequired,
    LoggedOut,
    InvalidCredentials,
    InvalidRequest,
    InvalidCsrf,
    LogoutRequired,
    RoleNotAllowed,
    InvalidRoleSelection,
    InvalidSession,
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    ServiceUnavailable,
    ProxyError,
    NetworkError,
    ProtocolError,
    BrowserInteropUnavailable
}

/// <summary>Identity information from browser transport. It is not a trusted server principal.</summary>
public sealed record AuthApiIdentity(long UserId, string Username, UserRole ActiveRole);

public sealed class AuthCsrfToken
{
    internal AuthCsrfToken(string value) => Value = value;

    [JsonIgnore]
    public string Value { get; }
}

public sealed record AuthApiResult(
    AuthApiStatus Status,
    int? HttpStatus = null,
    string? ErrorCode = null,
    TimeSpan? RetryAfter = null,
    AuthApiIdentity? Identity = null,
    IReadOnlyList<UserRole>? AvailableRoles = null,
    int? ExpiresInSeconds = null,
    [property: JsonIgnore] AuthCsrfToken? CsrfToken = null);
