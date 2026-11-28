using System.Security.Cryptography;
using System.Text.Json;
using ImagingAI.Api.Services;

namespace EmailOwnershipVerification;

public sealed record Account(long UserId, string Username, long RoleId, long? SecondaryRoleId,
    string PrimaryRole, string? SecondaryRole, bool Active, string? Email, DateTime? VerifiedAt, string Version)
{
    public bool VersionReadByTool { get; init; } = true;
    public static bool Allowed(string user) => user is "413570452" or "413570438" or "413570359";

    public bool Eligible => UserId > 0 && RoleId > 0 && Active && VerifiedAt is null &&
        uint.TryParse(Version, out _) && GmailSmtpOptions.Mailbox(Email) is not null &&
        (Username, PrimaryRole, SecondaryRole, SecondaryRoleId) switch
        {
            ("413570452", "MEDICAL_REVIEWER", "SYSTEM_ADMIN", > 0) => SecondaryRoleId != RoleId,
            ("413570452", "SYSTEM_ADMIN", "MEDICAL_REVIEWER", > 0) => SecondaryRoleId != RoleId,
            ("413570438", "SYSTEM_ADMIN", null, null) => true,
            ("413570359", "MEDICAL_REVIEWER", null, null) => true,
            _ => false
        };

    public override string ToString() => nameof(Account);
}

public enum Outcome { Pending, Invalid, Locked, Expired, Wait, Busy, Unavailable, Verified }

public sealed record Evidence(Guid EvidenceId, Guid ChallengeId, long UserId, string Username, string Email,
    DateTimeOffset ConfirmedUtc, DateTimeOffset ExpiresUtc, string Target, string Purpose,
    long RoleId, long? SecondaryRoleId, string PrimaryRole, string? SecondaryRole, bool Active, string Version)
{
    public bool VersionReadByTool { get; init; }
    public override string ToString() => nameof(Evidence);
}

public sealed class Ownership(
    Account account, string target, bool live, TimeProvider clock,
    Func<CancellationToken, Task<Account?>> read, Func<string, string, CancellationToken, Task> send)
    : IDisposable
{
    public const string Purpose = "email-ownership-confirmation-v1";
    private readonly Guid challenge = Guid.NewGuid();
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim gate = new(1);
    private readonly long created = clock.GetTimestamp();
    private readonly List<byte[]> digests = [];
    private long lastSend;
    private int attempts;
    private bool pending, terminal;
    private Evidence? evidence;
    private long evidenceCreated;
    private byte[]? seal;

    public async Task<Outcome> SendAsync(bool confirmed, CancellationToken token = default)
    {
        if (!gate.Wait(0)) return Outcome.Busy;
        try
        {
            if (!confirmed || terminal || !account.Eligible) return Outcome.Invalid;
            if (Expired()) return Outcome.Expired;
            if (digests.Count >= 3) return Outcome.Locked;
            if (digests.Count > 0 && clock.GetElapsedTime(lastSend) < TimeSpan.FromSeconds(60)) return Outcome.Wait;
            pending = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (await read(token) != account || Expired()) return Reject();
                string code;
                do { code = EmailOtpCode.Generate(); }
                while (digests.Where((digest, index) => Matches(code, index, digest)).Any());
                digests.Add(Digest(code, digests.Count));
                lastSend = clock.GetTimestamp();
                await send(account.Email!, code, token);
                token.ThrowIfCancellationRequested();
                if (Expired()) return Reject();
                pending = true;
                return Outcome.Pending;
            }
            catch (Exception) { return Reject(); }
        }
        finally { gate.Release(); }
    }

    public async Task<(Outcome Status, Evidence? Evidence)> VerifyAsync(string code, CancellationToken token = default)
    {
        if (!gate.Wait(0)) return (Outcome.Busy, null);
        try
        {
            if (Expired()) return (Outcome.Expired, null);
            if (attempts >= 5) return (Outcome.Locked, null);
            if (terminal || !pending) return (Outcome.Invalid, null);
            var version = digests.Count - 1;
            if (!EmailOtpCode.IsValid(code) || !Matches(code, version, digests[version]))
            {
                if (++attempts == 5) { terminal = true; pending = false; return (Outcome.Locked, null); }
                return (Outcome.Invalid, null);
            }
            terminal = true;
            pending = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (await read(token) != account) return (Outcome.Unavailable, null);
                token.ThrowIfCancellationRequested();
                if (Expired()) return (Outcome.Expired, null);
                var now = clock.GetUtcNow();
                evidenceCreated = clock.GetTimestamp();
                evidence = new(Guid.NewGuid(), challenge, account.UserId, account.Username, account.Email!,
                    now, now.AddMinutes(5), target, live ? Purpose : "offline-test-only",
                    account.RoleId, account.SecondaryRoleId, account.PrimaryRole, account.SecondaryRole, account.Active, account.Version)
                    { VersionReadByTool = account.VersionReadByTool };
                seal = HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(evidence));
                return (Outcome.Verified, evidence);
            }
            catch (Exception) { return (Outcome.Unavailable, null); }
        }
        finally { gate.Release(); }
    }

    public bool ReleaseForMaintenance(Evidence candidate)
    {
        if (!gate.Wait(0)) return false;
        try
        {
            if (!live || candidate.Purpose != Purpose || seal is null || evidence is null ||
                clock.GetUtcNow() >= evidence.ExpiresUtc ||
                clock.GetElapsedTime(evidenceCreated) >= TimeSpan.FromMinutes(5)) return false;
            var actual = HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(candidate));
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actual, seal)) return false;
                CryptographicOperations.ZeroMemory(seal);
                seal = null;
                evidence = null;
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally { gate.Release(); }
    }

    private bool Expired() => clock.GetElapsedTime(created) >= TimeSpan.FromSeconds(300);
    private Outcome Reject() { terminal = true; pending = false; return Outcome.Unavailable; }

    private byte[] Digest(string code, int version)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Purpose, challenge, account.UserId, account.Email, version, code });
        try { return HMACSHA256.HashData(key, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private bool Matches(string code, int version, byte[] expected)
    {
        var digest = Digest(code, version);
        try { return CryptographicOperations.FixedTimeEquals(expected, digest); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    public void Dispose()
    {
        foreach (var digest in digests) CryptographicOperations.ZeroMemory(digest);
        if (seal is not null) CryptographicOperations.ZeroMemory(seal);
        CryptographicOperations.ZeroMemory(key);
        gate.Dispose();
    }
}
