using System.Globalization;
using System.Security.Cryptography;

namespace ImagingAI.Api.Services;

internal interface IEmailOtpSender
{
    Task SendAsync(string email, string code, CancellationToken cancellationToken);
}

internal sealed record EmailOtpOptions
{
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ResendInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan LimitWindow { get; init; } = TimeSpan.FromMinutes(15);
    public int Attempts { get; init; } = 5;
    public int TotalSends { get; init; } = 3;
    public int ChallengeCapacity { get; init; } = 256;
    public int LimitCapacity { get; init; } = 2048;
    public int AccountCreates { get; init; } = 3;
    public int SourceCreates { get; init; } = 15;
    public int GlobalCreates { get; init; } = 120;
    public int AccountSends { get; init; } = 3;
    public int SourceSends { get; init; } = 30;
    public int GlobalSends { get; init; } = 240;
}

internal enum EmailOtpStatus
{
    Pending, Verified, Rejected, Expired, AttemptsExhausted, RateLimited,
    Busy, Unavailable, DeliveryFailed
}

internal sealed record EmailOtpResult(EmailOtpStatus Status, string? ChallengeId = null)
{
    internal object? Proof { get; init; }
    internal int ExpiresInSeconds { get; init; }
    internal int RetryAfterSeconds { get; init; }
}

internal sealed record EmailOtpAccount(long Id, string Username, bool Active, string? Email,
    DateTime? VerifiedAt, string? PrimaryRole, long? SecondaryRoleId, string? SecondaryRole)
{
    internal bool IsEligible => Id > 0 && Active && !string.IsNullOrWhiteSpace(Username) &&
        !string.Equals(Username, "dev_uploader", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(Email) && VerifiedAt.HasValue && IsRole(PrimaryRole) &&
        (SecondaryRoleId.HasValue
            ? IsRole(SecondaryRole) && PrimaryRole != SecondaryRole
            : SecondaryRole == null);

    internal bool MatchesRoles(IReadOnlyList<string> roles) =>
        roles.Count == (SecondaryRoleId.HasValue ? 2 : 1) &&
        roles.Contains(PrimaryRole, StringComparer.Ordinal) &&
        (!SecondaryRoleId.HasValue || roles.Contains(SecondaryRole, StringComparer.Ordinal));

    private static bool IsRole(string? role) => role is "MEDICAL_REVIEWER" or "SYSTEM_ADMIN";

    public override string ToString() => nameof(EmailOtpAccount);
}

internal static class EmailOtpCode
{
    internal static string Generate() => Format(RandomNumberGenerator.GetInt32(1_000_000));
    internal static string Format(int number) => number.ToString("D6", CultureInfo.InvariantCulture);
    internal static bool IsValid(string? value) =>
        value is { Length: 6 } && value.All(c => c is >= '0' and <= '9');
}
