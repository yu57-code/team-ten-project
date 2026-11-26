using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

[assembly: InternalsVisibleTo("ImagingAI.Api.Tests")]

namespace ImagingAI.Api.Services;

public sealed class AuthService
{
    private const string MedicalReviewer = "MEDICAL_REVIEWER";
    private const string SystemAdmin = "SYSTEM_ADMIN";

    private static readonly HashSet<string> DevelopmentAccounts = new(StringComparer.Ordinal)
    {
        "413570452", "413570438", "413570359", "413570309"
    };

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object HashSubject = new();
    private static readonly string DummyHash =
        Hasher.HashPassword(HashSubject, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    private static readonly AuthSessionStore ProcessPendingRoles = new();

    private readonly Func<string, CancellationToken, Task<AccountCredentials?>> readAccount;
    private readonly Func<long, CancellationToken, Task<AccountRoles?>> readRoles;
    private readonly AuthSessionStore pendingRoles;
    private readonly IHostEnvironment environment;
    private readonly ILogger<AuthService> logger;

    public AuthService(ImagingAiDbContext database, IHostEnvironment environment, ILogger<AuthService> logger)
        : this(database, environment, logger, ProcessPendingRoles)
    {
    }

    public AuthService(ImagingAiDbContext database, IHostEnvironment environment, ILogger<AuthService> logger,
        AuthSessionStore pendingRoles)
        : this((username, token) => CredentialsQuery(database, username).SingleOrDefaultAsync(token),
            environment, logger,
            (userId, token) => RolesQuery(database, userId).SingleOrDefaultAsync(token), pendingRoles)
    {
    }

    internal AuthService(Func<string, CancellationToken, Task<AccountCredentials?>> readAccount,
        IHostEnvironment environment, ILogger<AuthService> logger,
        Func<long, CancellationToken, Task<AccountRoles?>>? readRoles = null,
        AuthSessionStore? pendingRoles = null)
    {
        this.readAccount = readAccount;
        this.readRoles = readRoles ?? ((_, _) => throw new InvalidOperationException("Role lookup is not configured."));
        this.pendingRoles = pendingRoles ?? ProcessPendingRoles;
        this.environment = environment;
        this.logger = logger;
    }

    public async Task<CredentialVerificationResult> VerifyCredentialsAsync(
        LoginRequest? request, CancellationToken cancellationToken = default)
    {
        if (request?.Username is not { Length: <= 100 } username || string.IsNullOrWhiteSpace(username) ||
            request.Password is not { Length: <= 256 } password || string.IsNullOrWhiteSpace(password))
        {
            return new(CredentialVerificationStatus.InvalidRequest);
        }

        AccountCredentials? account = null;
        if (IsEligible(username))
        {
            try
            {
                account = await readAccount(username, cancellationToken);
            }
            catch (Exception error) when (IsAccountStoreFailure(error))
            {
                logger.LogWarning(new EventId(3201, "AuthAccountStoreUnavailable"),
                    "Credential verification account store unavailable.");
                return new(CredentialVerificationStatus.AccountStoreUnavailable);
            }
        }

        if (account is not { IsActive: true } ||
            !string.Equals(username, account.Username, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(account.PasswordHash))
        {
            return RejectWithDummyVerification(password);
        }

        PasswordVerificationResult verification;
        try
        {
            verification = Hasher.VerifyHashedPassword(HashSubject, account.PasswordHash, password);
        }
        catch (FormatException)
        {
            return RejectWithDummyVerification(password);
        }

        if (verification == PasswordVerificationResult.Failed ||
            account.PrimaryRoleCode is not { } primary || !IsLegalRole(primary))
        {
            return new(CredentialVerificationStatus.InvalidCredentials);
        }

        var secondary = account.SecondaryRoleCode;
        if (account.HasSecondaryRole &&
            (secondary is null || !IsLegalRole(secondary) ||
             string.Equals(primary, secondary, StringComparison.Ordinal)))
        {
            return new(CredentialVerificationStatus.InvalidCredentials);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            logger.LogInformation(new EventId(3202, "AuthPasswordRehashNeeded"),
                "Verified credentials require a password hash upgrade.");
        }

        var roles = account.HasSecondaryRole && secondary is not null
            ? Array.AsReadOnly(new[] { primary, secondary })
            : Array.AsReadOnly(new[] { primary });
        return new(CredentialVerificationStatus.CredentialsVerified,
            new VerifiedAccount(account.UserId, account.Username, roles));
    }

    public async Task<RoleConfirmationResult> BeginRoleConfirmationAsync(
        LoginRequest? request, CancellationToken cancellationToken = default)
    {
        var credentials = await VerifyCredentialsAsync(request, cancellationToken);
        if (credentials.Status != CredentialVerificationStatus.CredentialsVerified || credentials.Account is not { } account)
        {
            return new(credentials.Status switch
            {
                CredentialVerificationStatus.InvalidRequest => RoleConfirmationStatus.InvalidRequest,
                CredentialVerificationStatus.AccountStoreUnavailable => RoleConfirmationStatus.AccountStoreUnavailable,
                _ => RoleConfirmationStatus.InvalidCredentials
            });
        }

        if (account.RequiresRoleSelection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = pendingRoles.TryCreate(account);
            return token is null
                ? new(RoleConfirmationStatus.PendingSelectionUnavailable)
                : new(RoleConfirmationStatus.RoleSelectionRequired, account, pendingRoleToken: token);
        }

        return await ConfirmCurrentRoleAsync(account, account.RoleCodes[0], cancellationToken);
    }

    public async Task<RoleConfirmationResult> ConfirmSelectedRoleAsync(
        string? pendingToken, string? selectedRoleCode, CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment() || pendingToken is null || pendingRoles.Find(pendingToken) is not { } pending)
            return new(RoleConfirmationStatus.InvalidSelection);

        try
        {
            if (selectedRoleCode is null || !pending.Account.RoleCodes.Contains(selectedRoleCode, StringComparer.Ordinal))
                return new(RoleConfirmationStatus.InvalidSelection, roleNotAllowed: true);

            var result = await ConfirmCurrentRoleAsync(pending.Account, selectedRoleCode, cancellationToken);
            if (result.Status != RoleConfirmationStatus.RoleConfirmed)
                return result;

            cancellationToken.ThrowIfCancellationRequested();
            return pendingRoles.TryConsume(pendingToken, pending)
                ? result
                : new(RoleConfirmationStatus.InvalidSelection);
        }
        finally
        {
            // Failed attempts using a valid challenge require fresh credential verification.
            pendingRoles.Cancel(pendingToken);
        }
    }

    public bool CancelPendingRoleSelection(string? pendingToken) => pendingRoles.Cancel(pendingToken);

    private async Task<RoleConfirmationResult> ConfirmCurrentRoleAsync(
        VerifiedAccount account, string role, CancellationToken cancellationToken)
    {
        var (status, current) = await ReadCurrentAccountAsync(account.UserId, account.Username, cancellationToken);
        if (current is null)
            return new(status);

        return current.RoleCodes.Contains(role, StringComparer.Ordinal)
            ? new(RoleConfirmationStatus.RoleConfirmed, current, activeRoleCode: role)
            : new(RoleConfirmationStatus.InvalidSelection, roleNotAllowed: true);
    }

    internal async Task<RoleConfirmationStatus> RevalidateSessionAsync(
        AuthSessionStore.DevelopmentSession session, CancellationToken cancellationToken)
    {
        var (status, current) = await ReadCurrentAccountAsync(session.UserId, session.Username, cancellationToken);
        if (current is null)
            return status;

        return current.RoleCodes.Count == session.RoleCodes.Count &&
               session.RoleCodes.All(role => current.RoleCodes.Contains(role, StringComparer.Ordinal)) &&
               current.RoleCodes.Contains(session.ActiveRoleCode, StringComparer.Ordinal)
            ? RoleConfirmationStatus.RoleConfirmed
            : RoleConfirmationStatus.InvalidSelection;
    }

    private async Task<(RoleConfirmationStatus Status, VerifiedAccount? Account)> ReadCurrentAccountAsync(
        long userId, string username, CancellationToken cancellationToken)
    {
        if (!IsEligible(username))
            return (RoleConfirmationStatus.InvalidSelection, null);

        AccountRoles? current;
        try
        {
            current = await readRoles(userId, cancellationToken);
        }
        catch (Exception error) when (IsAccountStoreFailure(error))
        {
            logger.LogWarning(new EventId(3203, "AuthRoleStoreUnavailable"), "Role confirmation account store unavailable.");
            return (RoleConfirmationStatus.AccountStoreUnavailable, null);
        }

        if (current is not { IsActive: true, PrimaryRoleCode: { } primary } ||
            current.UserId != userId || !string.Equals(current.Username, username, StringComparison.Ordinal) ||
            !IsLegalRole(primary) ||
            (current.HasSecondaryRole && (current.SecondaryRoleCode is not { } secondary ||
                !IsLegalRole(secondary) || string.Equals(primary, secondary, StringComparison.Ordinal))))
        {
            return (RoleConfirmationStatus.InvalidSelection, null);
        }

        var roles = current.HasSecondaryRole
            ? Array.AsReadOnly(new[] { primary, current.SecondaryRoleCode! })
            : Array.AsReadOnly(new[] { primary });
        return (RoleConfirmationStatus.RoleConfirmed, new VerifiedAccount(userId, username, roles));
    }

    private bool IsEligible(string username) => environment.IsDevelopment() && DevelopmentAccounts.Contains(username);

    internal static IQueryable<AccountRoles> RolesQuery(ImagingAiDbContext database, long userId) =>
        database.UserAccounts.AsNoTracking()
            .Where(account => account.UserId == userId)
            .Select(account => new AccountRoles(account.UserId, account.Username, account.IsActive,
                account.Role.RoleCode, account.SecondaryRoleId != null,
                account.SecondaryRole == null ? null : account.SecondaryRole.RoleCode));

    internal static IQueryable<AccountCredentials> CredentialsQuery(ImagingAiDbContext database, string username) =>
        database.UserAccounts
            .AsNoTracking()
            .Where(account => account.Username == username)
            .Select(account => new AccountCredentials(
                account.UserId, account.Username, account.PasswordHash, account.IsActive,
                account.Role.RoleCode, account.SecondaryRoleId != null,
                account.SecondaryRole == null ? null : account.SecondaryRole.RoleCode));

    private static CredentialVerificationResult RejectWithDummyVerification(string password)
    {
        _ = Hasher.VerifyHashedPassword(HashSubject, DummyHash, password);
        return new(CredentialVerificationStatus.InvalidCredentials);
    }

    private static bool IsLegalRole(string role) =>
        string.Equals(role, MedicalReviewer, StringComparison.Ordinal) ||
        string.Equals(role, SystemAdmin, StringComparison.Ordinal);

    private static bool IsAccountStoreFailure(Exception error) =>
        error is DbException or TimeoutException ||
        error is InvalidOperationException { InnerException: DbException or TimeoutException };

    internal sealed record AccountCredentials(
        long UserId,
        string Username,
        string? PasswordHash,
        bool IsActive,
        string? PrimaryRoleCode,
        bool HasSecondaryRole,
        string? SecondaryRoleCode);

    internal sealed record AccountRoles(long UserId, string Username, bool IsActive,
        string? PrimaryRoleCode, bool HasSecondaryRole, string? SecondaryRoleCode);
}
