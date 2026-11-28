using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace ImagingAI.Api.Services;

internal sealed class EmailOtpChallengeStore : IDisposable
{
    private enum State { Sending, Pending, Consumed, Locked, Failed, Revoked }
    private enum Limit { Create, Send, Wrong }
    private sealed class VerifiedProof(EmailOtpChallengeStore owner, string id, EmailOtpAccount account)
    {
        internal EmailOtpChallengeStore Owner { get; } = owner;
        internal EmailOtpAccount Account { get; } = account;
        internal string Id { get; } = id;
    }
    private readonly record struct LimitKey(Limit Kind, long Account = 0, IPAddress? Source = null);
    private sealed class Counter(DateTimeOffset until)
    {
        internal DateTimeOffset Until { get; } = until;
        internal int Count { get; set; }
    }

    private sealed class Challenge(string id, EmailOtpAccount account, IPAddress source,
        DateTimeOffset created, DateTimeOffset expires, int attempts)
    {
        internal string Id { get; } = id;
        internal EmailOtpAccount Account { get; } = account;
        internal IPAddress Source { get; } = source;
        internal DateTimeOffset Created { get; } = created;
        internal DateTimeOffset Expires { get; } = expires;
        internal DateTimeOffset LastSend { get; set; }
        internal int Remaining { get; set; } = attempts;
        internal int Version { get; set; }
        internal State State { get; set; }
        internal byte[]? Digest { get; set; }
        internal List<byte[]> PreviousDigests { get; } = [];
    }

    internal sealed class Delivery(string id, int version, string code)
    {
        internal string Id { get; } = id;
        internal int Version { get; } = version;
        internal string Code { get; } = code;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Challenge> challenges = new(StringComparer.Ordinal);
    private readonly Dictionary<LimitKey, Counter> counters = [];
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider clock;
    private readonly EmailOtpOptions options;
    private readonly ITimer timer;
    private bool disposed;

    internal EmailOtpChallengeStore(TimeProvider clock, EmailOtpOptions options)
    {
        this.clock = clock;
        this.options = options;
        timer = clock.CreateTimer(_ =>
        {
            lock (gate)
            {
                if (!disposed) Prune(clock.GetUtcNow());
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal (EmailOtpStatus Status, Delivery? Delivery) Create(EmailOtpAccount account, IPAddress source)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            if (disposed || challenges.Count >= options.ChallengeCapacity)
                return (EmailOtpStatus.Unavailable, null);
            if (AtWrongLimit(account.Id) ||
                !Take(now, Limits(Limit.Create, account.Id, source).Concat(Limits(Limit.Send, account.Id, source))))
                return (EmailOtpStatus.RateLimited, null);

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var item = new Challenge(id, account, source, now, now + options.Lifetime, options.Attempts);
            challenges.Add(id, item);
            return (EmailOtpStatus.Busy, Prepare(item, now));
        }
    }

    internal long? FindAccount(string? id, IPAddress source)
    {
        lock (gate)
        {
            if (!disposed && ValidId(id) && challenges.TryGetValue(id!, out var item) &&
                item.Source.Equals(source))
                return item.Account.Id;
            return null;
        }
    }

    internal (EmailOtpStatus Status, Delivery? Delivery) Resend(string id, IPAddress source, EmailOtpAccount? account)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var status = Check(id, source, account, now, out var item);
            if (status != EmailOtpStatus.Pending) return (status, null);
            if (item!.Version >= options.TotalSends || now - item.LastSend < options.ResendInterval ||
                !Take(now, Limits(Limit.Send, item.Account.Id, source)))
                return (EmailOtpStatus.RateLimited, null);
            return (EmailOtpStatus.Busy, Prepare(item, now));
        }
    }

    internal EmailOtpResult Verify(string id, IPAddress source, EmailOtpAccount? account, string? code)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var status = Check(id, source, account, now, out var item);
            if (status != EmailOtpStatus.Pending) return new(status);

            // Reserve the shared account error bucket before checking the secret.
            // A full limiter must reject attempts, including guesses against outstanding challenges.
            var bucket = new LimitKey(Limit.Wrong, item!.Account.Id);
            if (!counters.ContainsKey(bucket) && counters.Count >= options.LimitCapacity)
                return new(EmailOtpStatus.Unavailable);

            if (EmailOtpCode.IsValid(code) && Matches(item, item.Version, code!))
            {
                End(item, State.Consumed);
                return new(EmailOtpStatus.Verified) { Proof = new VerifiedProof(this, item.Id, item.Account) };
            }

            Take(now, [(bucket, options.Attempts)]);
            if (--item.Remaining == 0 || AtWrongLimit(item.Account.Id))
            {
                End(item, State.Locked);
                return new(EmailOtpStatus.AttemptsExhausted);
            }
            return new(EmailOtpStatus.Rejected);
        }
    }

    internal EmailOtpResult Complete(Delivery delivery, bool sent)
    {
        lock (gate)
        {
            if (disposed || !challenges.TryGetValue(delivery.Id, out var item) ||
                item.Version != delivery.Version || item.State != State.Sending)
                return new(EmailOtpStatus.Rejected);
            if (clock.GetUtcNow() >= item.Expires)
            {
                Remove(item.Id);
                return new(EmailOtpStatus.Expired);
            }
            if (!sent)
            {
                End(item, State.Failed);
                return new(EmailOtpStatus.DeliveryFailed);
            }
            item.State = State.Pending;
            return new(EmailOtpStatus.Pending, item.Id)
            {
                ExpiresInSeconds = Math.Max(0, (int)Math.Floor((item.Expires - clock.GetUtcNow()).TotalSeconds)),
                RetryAfterSeconds = Math.Max(0, (int)Math.Ceiling(
                    (item.LastSend + options.ResendInterval - clock.GetUtcNow()).TotalSeconds))
            };
        }
    }

    internal void Revoke(string id, IPAddress source)
    {
        lock (gate)
        {
            if (ValidId(id) && challenges.TryGetValue(id, out var item) && item.Source.Equals(source))
                End(item, State.Revoked);
        }
    }

    internal EmailOtpAccount? ConsumeProof(object? proof)
    {
        lock (gate)
        {
            if (disposed || proof is not VerifiedProof issued || !ReferenceEquals(issued.Owner, this) ||
                !challenges.TryGetValue(issued.Id, out var item) || item.State != State.Consumed ||
                item.Expires <= clock.GetUtcNow())
                return null;
            Remove(issued.Id);
            return issued.Account;
        }
    }

    internal int ResendRetryAfter(string id, IPAddress source)
    {
        lock (gate)
        {
            if (challenges.TryGetValue(id, out var item) && item.Source.Equals(source))
            {
                if (item.Version >= options.TotalSends)
                    return Math.Max(1, (int)Math.Ceiling((item.Expires - clock.GetUtcNow()).TotalSeconds));
                var wait = item.LastSend + options.ResendInterval - clock.GetUtcNow();
                if (wait > TimeSpan.Zero) return (int)Math.Ceiling(wait.TotalSeconds);
            }
            return (int)Math.Ceiling(options.LimitWindow.TotalSeconds);
        }
    }

    private EmailOtpStatus Check(string id, IPAddress source, EmailOtpAccount? account,
        DateTimeOffset now, out Challenge? item)
    {
        challenges.TryGetValue(id, out item);
        var expired = item is not null && now >= item.Expires;
        Prune(now);
        if (disposed || item is null || !item.Source.Equals(source)) return EmailOtpStatus.Rejected;
        if (expired) return EmailOtpStatus.Expired;
        if (account is not { IsEligible: true } || account != item.Account)
        {
            End(item, State.Revoked);
            return EmailOtpStatus.Rejected;
        }
        if (item.State == State.Locked || AtWrongLimit(account.Id))
        {
            End(item, State.Locked);
            return EmailOtpStatus.AttemptsExhausted;
        }
        return item.State switch
        {
            State.Sending => EmailOtpStatus.Busy,
            State.Pending => EmailOtpStatus.Pending,
            _ => EmailOtpStatus.Rejected
        };
    }

    private Delivery Prepare(Challenge item, DateTimeOffset now)
    {
        string code;
        do { code = EmailOtpCode.Generate(); }
        while ((item.Digest != null && Matches(item, item.Version, code, item.Digest)) ||
               item.PreviousDigests.Where((digest, index) => Matches(item, index + 1, code, digest)).Any());
        if (item.Digest != null) item.PreviousDigests.Add(item.Digest);
        item.State = State.Sending;
        item.Version++;
        item.LastSend = now;
        item.Digest = Digest(item, item.Version, code);
        return new(item.Id, item.Version, code);
    }

    private byte[] Digest(Challenge item, int version, string code)
    {
        // 32-byte binary challenge ID + 8-byte account + 4-byte version + six ASCII bytes.
        Span<byte> input = stackalloc byte[50];
        Convert.FromHexString(item.Id).CopyTo(input);
        BinaryPrimitives.WriteInt64BigEndian(input[32..], item.Account.Id);
        BinaryPrimitives.WriteInt32BigEndian(input[40..], version);
        for (var i = 0; i < 6; i++) input[44 + i] = (byte)code[i];
        try { return HMACSHA256.HashData(key, input); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    private bool Matches(Challenge item, int version, string code, byte[]? expected = null)
    {
        var digest = Digest(item, version, code);
        try { return CryptographicOperations.FixedTimeEquals(expected ?? item.Digest!, digest); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private IEnumerable<(LimitKey, int)> Limits(Limit kind, long account, IPAddress source) =>
    [
        (new(kind, account), kind == Limit.Create ? options.AccountCreates : options.AccountSends),
        (new(kind, Source: source), kind == Limit.Create ? options.SourceCreates : options.SourceSends),
        (new(kind), kind == Limit.Create ? options.GlobalCreates : options.GlobalSends)
    ];

    private bool Take(DateTimeOffset now, IEnumerable<(LimitKey Key, int Max)> limits)
    {
        var entries = limits.ToArray();
        if (counters.Count + entries.Count(x => !counters.ContainsKey(x.Key)) > options.LimitCapacity ||
            entries.Any(x => counters.TryGetValue(x.Key, out var count) && count.Count >= x.Max))
            return false;
        foreach (var (bucket, _) in entries)
        {
            if (!counters.TryGetValue(bucket, out var count))
                counters.Add(bucket, count = new(now + options.LimitWindow));
            count.Count++;
        }
        return true;
    }

    private bool AtWrongLimit(long account) =>
        counters.TryGetValue(new(Limit.Wrong, account), out var count) && count.Count >= options.Attempts;

    private static bool ValidId(string? id) => id is { Length: 64 };

    private void Prune(DateTimeOffset now)
    {
        foreach (var item in challenges.Values.Where(x => x.Expires <= now).ToArray()) Remove(item.Id);
        foreach (var entry in counters.Where(x => x.Value.Until <= now).ToArray()) counters.Remove(entry.Key);
    }

    private void Remove(string id)
    {
        if (challenges.Remove(id, out var item)) End(item, State.Revoked);
    }

    private static void End(Challenge item, State state)
    {
        if (item.Digest != null) CryptographicOperations.ZeroMemory(item.Digest);
        foreach (var previous in item.PreviousDigests) CryptographicOperations.ZeroMemory(previous);
        item.PreviousDigests.Clear();
        item.Digest = null;
        item.State = state;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var item in challenges.Values) End(item, State.Revoked);
            challenges.Clear();
            counters.Clear();
            CryptographicOperations.ZeroMemory(key);
        }
        timer.Dispose();
    }
}
