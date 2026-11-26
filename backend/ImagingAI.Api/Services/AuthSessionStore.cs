using System.Security.Cryptography;
using System.Text;
using ImagingAI.Api.Models;

namespace ImagingAI.Api.Services;

public sealed class AuthSessionStore
{
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private const int TokenByteCount = 32;
    private const int DefaultCapacity = 256;

    private readonly object gate = new();
    private readonly Dictionary<string, PendingRoleSelection> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DevelopmentSession> sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly int capacity;

    public AuthSessionStore() : this(TimeProvider.System, DefaultCapacity)
    {
    }

    internal AuthSessionStore(TimeProvider clock, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.clock = clock;
        this.capacity = capacity;
    }

    internal string? TryCreate(VerifiedAccount account)
    {
        lock (gate)
        {
            var timestamp = clock.GetTimestamp();
            RemoveExpired(timestamp);
            if (pending.Count >= capacity)
                return null;

            var createdAt = clock.GetUtcNow();
            var snapshot = account with { RoleCodes = Array.AsReadOnly(account.RoleCodes.ToArray()) };
            var state = new PendingRoleSelection(snapshot, createdAt, createdAt + PendingLifetime, timestamp);
            while (true)
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenByteCount));
                if (pending.TryAdd(Digest(token)!, state))
                    return token;
            }
        }
    }

    internal PendingRoleSelection? Find(string? token)
    {
        var digest = Digest(token);
        if (digest is null)
            return null;

        lock (gate)
        {
            RemoveExpired(clock.GetTimestamp());
            return pending.GetValueOrDefault(digest);
        }
    }

    internal bool TryConsume(string token, PendingRoleSelection expected)
    {
        var digest = Digest(token)!;
        lock (gate)
        {
            RemoveExpired(clock.GetTimestamp());
            return pending.TryGetValue(digest, out var actual) &&
                   ReferenceEquals(actual, expected) && pending.Remove(digest);
        }
    }

    public bool Cancel(string? token)
    {
        var digest = Digest(token);
        if (digest is null)
            return false;

        lock (gate)
        {
            RemoveExpired(clock.GetTimestamp());
            return pending.Remove(digest);
        }
    }

    internal DevelopmentSession? TryCreateSession(RoleConfirmationResult confirmation)
    {
        if (confirmation is not { Status: RoleConfirmationStatus.RoleConfirmed, UserId: { } userId,
                Username: { } username, ActiveRoleCode: { } activeRole } ||
            !confirmation.ConfirmedRoleCodes.Contains(activeRole, StringComparer.Ordinal))
        {
            return null;
        }

        lock (gate)
        {
            var timestamp = clock.GetTimestamp();
            RemoveExpiredSessions(timestamp);
            if (sessions.Count >= capacity)
                return null;

            var now = clock.GetUtcNow();
            var roles = Array.AsReadOnly(confirmation.ConfirmedRoleCodes.ToArray());
            while (true)
            {
                var session = new DevelopmentSession(
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenByteCount)), userId,
                    username, activeRole, roles, now, now + SessionLifetime, timestamp);
                if (sessions.TryAdd(session.Id, session))
                    return session;
            }
        }
    }

    internal DevelopmentSession? FindSession(string? id)
    {
        if (id is not { Length: TokenByteCount * 2 })
            return null;

        lock (gate)
        {
            RemoveExpiredSessions(clock.GetTimestamp());
            return sessions.GetValueOrDefault(id);
        }
    }

    internal bool RevokeSession(string id)
    {
        lock (gate)
        {
            RemoveExpiredSessions(clock.GetTimestamp());
            return sessions.Remove(id);
        }
    }

    private void RemoveExpired(long now)
    {
        // The dictionary is bounded. Sweep on access without a background timer.
        foreach (var (key, value) in pending)
        {
            if (clock.GetElapsedTime(value.CreatedTimestamp, now) >= PendingLifetime)
                pending.Remove(key);
        }
    }

    private static string? Digest(string? token) =>
        token is { Length: TokenByteCount * 2 } && token.All(char.IsAsciiHexDigitUpper)
            ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)))
            : null;

    private void RemoveExpiredSessions(long now)
    {
        foreach (var (key, value) in sessions)
        {
            if (clock.GetElapsedTime(value.CreatedTimestamp, now) >= SessionLifetime)
                sessions.Remove(key);
        }
    }

    internal sealed record PendingRoleSelection(
        VerifiedAccount Account, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, long CreatedTimestamp)
    {
        public RoleConfirmationStatus Stage => RoleConfirmationStatus.RoleSelectionRequired;
    }

    internal sealed class DevelopmentSession(string id, long userId, string username, string activeRoleCode,
        IReadOnlyList<string> roleCodes, DateTimeOffset createdAt, DateTimeOffset expiresAt, long createdTimestamp)
    {
        internal string Id { get; } = id;
        internal long UserId { get; } = userId;
        internal string Username { get; } = username;
        internal string ActiveRoleCode { get; } = activeRoleCode;
        internal IReadOnlyList<string> RoleCodes { get; } = roleCodes;
        internal DateTimeOffset CreatedAt { get; } = createdAt;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal long CreatedTimestamp { get; } = createdTimestamp;
    }
}
