using System.Text.Json.Serialization;

namespace ImagingAI.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LoginRequest
{
    public string? Username { get; init; }

    public string? Password { get; init; }
}

public enum CredentialVerificationStatus
{
    InvalidRequest,
    InvalidCredentials,
    AccountStoreUnavailable,
    CredentialsVerified
}

public sealed record VerifiedAccount(
    long UserId,
    string Username,
    IReadOnlyList<string> RoleCodes)
{
    public bool RequiresRoleSelection => RoleCodes.Count > 1;
}

public sealed record CredentialVerificationResult(
    CredentialVerificationStatus Status,
    VerifiedAccount? Account = null);

public enum RoleConfirmationStatus
{
    InvalidRequest,
    InvalidCredentials,
    AccountStoreUnavailable,
    PendingSelectionUnavailable,
    InvalidSelection,
    RoleSelectionRequired,
    RoleConfirmed
}

public sealed class RoleConfirmationResult
{
    internal RoleConfirmationResult(RoleConfirmationStatus status, VerifiedAccount? account = null,
        string? activeRoleCode = null, string? pendingRoleToken = null, bool roleNotAllowed = false)
    {
        Status = status;
        UserId = account?.UserId;
        Username = account?.Username;
        AvailableRoleCodes = status == RoleConfirmationStatus.RoleSelectionRequired
            ? account!.RoleCodes : Array.Empty<string>();
        ActiveRoleCode = activeRoleCode;
        PendingRoleToken = pendingRoleToken;
        ConfirmedRoleCodes = status == RoleConfirmationStatus.RoleConfirmed
            ? account!.RoleCodes : Array.Empty<string>();
        RoleNotAllowed = roleNotAllowed;
    }

    public RoleConfirmationStatus Status { get; }
    public long? UserId { get; }
    public string? Username { get; }
    public IReadOnlyList<string> AvailableRoleCodes { get; }
    public string? ActiveRoleCode { get; }

    // For in-process use. Phase 3.4 will define how to transport it.
    [JsonIgnore]
    public string? PendingRoleToken { get; }

    internal IReadOnlyList<string> ConfirmedRoleCodes { get; }
    internal bool RoleNotAllowed { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelectRoleRequest(string? RoleCode);

public sealed record AuthCsrfResponse(string RequestToken);

public sealed record AuthIdentityResponse(long UserId, string Username, string ActiveRoleCode);

public sealed record AuthLoginResponse(string State, AuthIdentityResponse? User = null,
    IReadOnlyList<string>? AvailableRoleCodes = null, int? ExpiresInSeconds = null);
