using System.Net;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;

namespace ImagingAI.Api.Tests;

public static class EmailOtpTests
{
    internal static void Register(Action<string, Func<Task>> register)
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("ascii format and leading zero", () => {
                Check(EmailOtpCode.Format(9) == "000009" && !EmailOtpCode.IsValid("１２３４５６"));
                Check(Enumerable.Range(0, 32).All(_ => EmailOtpCode.IsValid(EmailOtpCode.Generate())));
                return Task.CompletedTask;
            }),
            ("correct OTP and replay", async () => {
                using var f = new Fixture(); var id = await f.Start();
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Verified);
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Rejected);
            }),
            ("five wrong guesses and account retry prevention", async () => {
                using var f = new Fixture(); var id = await f.Start();
                for (var i = 0; i < 4; i++) await f.Verify(id, "bad", EmailOtpStatus.Rejected);
                await f.Verify(id, "bad", EmailOtpStatus.AttemptsExhausted);
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.AttemptsExhausted);
                Check((await f.Create()).Status == EmailOtpStatus.RateLimited);
                Check((await f.Service.ResendAsync(id, Source)).Status == EmailOtpStatus.AttemptsExhausted);
            }),
            ("lifetime just before 300 seconds", async () => {
                using var f = new Fixture(); var id = await f.Start();
                f.Clock.Advance(299.999);
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Verified);
            }),
            ("exact expiry and capacity reclamation", async () => {
                using var f = new Fixture(new() { ChallengeCapacity = 1 });
                var id = await f.Start();
                Check((await f.Create()).Status == EmailOtpStatus.Unavailable);
                f.Clock.Advance(300);
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Expired);
                await f.Start();
            }),
            ("resend interval quota and all prior OTPs invalid", async () => {
                using var f = new Fixture(); var id = await f.Start(); var original = f.Sender.Last;
                f.Clock.Advance(59.999);
                Check((await f.Service.ResendAsync(id, Source)).Status == EmailOtpStatus.RateLimited);
                f.Clock.Advance(.001);
                Check((await f.Service.ResendAsync(id, Source)).Status == EmailOtpStatus.Pending);
                var second = f.Sender.Last;
                f.Clock.Advance(60);
                Check((await f.Service.ResendAsync(id, Source)).Status == EmailOtpStatus.Pending);
                Check((await f.Service.ResendAsync(id, Source)).Status == EmailOtpStatus.RateLimited);
                await f.Verify(id, original, EmailOtpStatus.Rejected);
                await f.Verify(id, second, EmailOtpStatus.Rejected);
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Verified);
            }),
            ("resend does not extend lifetime", async () => {
                using var f = new Fixture(); var id = await f.Start(); f.Clock.Advance(299);
                Check((await f.Service.ResendAsync(id, Source)).Status == EmailOtpStatus.Pending);
                f.Clock.Advance(1);
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Expired);
            }),
            ("resend does not reset wrong guess budget", async () => {
                using var f = new Fixture(); var id = await f.Start();
                for (var i = 0; i < 4; i++) await f.Verify(id, "bad", EmailOtpStatus.Rejected);
                f.Clock.Advance(60);
                Check((await f.Service.ResendAsync(id, Source)).Status == EmailOtpStatus.Pending);
                await f.Verify(id, "bad", EmailOtpStatus.AttemptsExhausted);
            }),
            ("parallel verification consumes at most once", async () => {
                using var f = new Fixture(); var id = await f.Start(); var code = f.Sender.Last;
                var results = await Task.WhenAll(Enumerable.Range(0, 24)
                    .Select(_ => Task.Run(() => f.Service.VerifyAsync(id, Source, code))));
                Check(results.Count(r => r.Status == EmailOtpStatus.Verified) == 1);
            }),
            ("verify cannot use a version while resend in flight", async () => {
                using var f = new Fixture(); var id = await f.Start(); var old = f.Sender.Last;
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Sender.Send = _ => pending.Task; f.Clock.Advance(60);
                var resend = f.Service.ResendAsync(id, Source);
                await f.Verify(id, old, EmailOtpStatus.Busy);
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Busy);
                pending.SetResult();
                Check((await resend).Status == EmailOtpStatus.Pending);
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Verified);
            }),
            ("send failure and unknown outcomes allow no challenge", async () => {
                foreach (var error in new Exception[] { new IOException(), new TimeoutException(), new OperationCanceledException() })
                {
                    using var f = new Fixture();
                    f.Sender.Send = _ => Task.FromException(error);
                    var result = await f.Create();
                    Check(result.Status == EmailOtpStatus.DeliveryFailed && result.ChallengeId == null);
                }
            }),
            ("caller cancellation and sender timeout fail closed", async () => {
                using var f = new Fixture(new() { SendTimeout = TimeSpan.FromMilliseconds(80) });
                f.Sender.Send = _ => new TaskCompletionSource().Task;
                var result = await f.Create().WaitAsync(TimeSpan.FromSeconds(3));
                Check(result.Status == EmailOtpStatus.DeliveryFailed && result.ChallengeId == null);
                using var cts = new CancellationTokenSource();
                var create = f.Service.CreateAsync(f.Verified, Source, cts.Token);
                cts.Cancel();
                Check((await create).Status == EmailOtpStatus.DeliveryFailed);
            }),
            ("absent email, unverified, disabled and service account", async () => {
                using var f = new Fixture();
                var original = f.Account;
                foreach (var account in new[] {
                    original with { Email = null }, original with { VerifiedAt = null },
                    original with { Active = false }, original with { Username = "dev_uploader" } })
                {
                    f.Account = account;
                    Check((await f.Create()).Status == EmailOtpStatus.Rejected);
                }
                Check(f.Sender.Sent == 0);
            }),
            ("Email, confirmation time, role and account changes revoke", async () => {
                foreach (var change in new Func<EmailOtpAccount, EmailOtpAccount>[] {
                    a => a with { Email = "changed@example.invalid" }, a => a with { VerifiedAt = a.VerifiedAt!.Value.AddSeconds(1) },
                    a => a with { PrimaryRole = "SYSTEM_ADMIN" }, a => a with { Active = false } })
                {
                    using var f = new Fixture(); var id = await f.Start(); var original = f.Account;
                    f.Account = change(original);
                    await f.Verify(id, f.Sender.Last, EmailOtpStatus.Rejected);
                    f.Account = original;
                    await f.Verify(id, f.Sender.Last, EmailOtpStatus.Rejected);
                }
            }),
            ("database error rejects and revokes", async () => {
                using var f = new Fixture(); var id = await f.Start(); f.LookupFails = true;
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Unavailable);
                f.LookupFails = false;
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Rejected);
            }),
            ("store replacement cannot validate old challenge", async () => {
                using var f = new Fixture(); var id = await f.Start();
                using var replacement = new Fixture();
                await replacement.Verify(id, f.Sender.Last, EmailOtpStatus.Rejected);
            }),
            ("account, source and global creation limits", async () => {
                using var a = new Fixture(new() { AccountCreates = 1 }); await a.Start();
                Check((await a.Create()).Status == EmailOtpStatus.RateLimited);
                using var s = new Fixture(new() { SourceCreates = 1 }); await s.Start(); s.NextAccount();
                Check((await s.Create()).Status == EmailOtpStatus.RateLimited);
                using var g = new Fixture(new() { GlobalCreates = 1 }); await g.Start(); g.NextAccount();
                Check((await g.Service.CreateAsync(g.Verified, IPAddress.IPv6Loopback)).Status == EmailOtpStatus.RateLimited);
            }),
            ("account, source and global sending limits", async () => {
                foreach (var options in new EmailOtpOptions[] { new() { AccountSends = 1 }, new() { SourceSends = 1 }, new() { GlobalSends = 1 } })
                {
                    using var f = new Fixture(options); var id = await f.Start(); f.Clock.Advance(60);
                    Check((await f.Service.ResendAsync(id, Source)).Status == EmailOtpStatus.RateLimited);
                }
            }),
            ("limiter capacity fails closed", async () => {
                using var f = new Fixture(new() { LimitCapacity = 6 }); var id = await f.Start();
                await f.Verify(id, f.Sender.Last, EmailOtpStatus.Unavailable);
            }),
            ("cross-challenge and cross-account code isolation", async () => {
                using var f = new Fixture();
                var first = await f.Start(); var firstCode = f.Sender.Last;
                f.NextAccount(); var other = await f.Start();
                if (firstCode == f.Sender.Last)
                {
                    f.Clock.Advance(60);
                    Check((await f.Service.ResendAsync(other, Source)).Status == EmailOtpStatus.Pending);
                }
                await f.Verify(other, firstCode, EmailOtpStatus.Rejected);
                Check((await f.Service.VerifyAsync(first, IPAddress.IPv6Loopback, firstCode)).Status == EmailOtpStatus.Rejected);
                await f.Verify(other, f.Sender.Last, EmailOtpStatus.Verified);
            }),
            ("no sender fallback or OTP in result", async () => {
                using var f = new Fixture(); var unavailable = new EmailOtpService((_, _) => throw new Exception(), f.Store, null, f.Clock, new());
                Check((await unavailable.CreateAsync(f.Verified, Source)).Status == EmailOtpStatus.Unavailable);
                var result = await f.Create();
                var json = System.Text.Json.JsonSerializer.Serialize(result);
                Check(result.Status == EmailOtpStatus.Pending && !json.Contains(f.Account.Email!) &&
                    typeof(EmailOtpResult).GetProperties().All(p => p.Name is "Status" or "ChallengeId"));
            })
        };

        foreach (var (name, run) in tests)
            register("OTP " + name, run);
    }


    private static readonly IPAddress Source = IPAddress.Loopback;
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(double seconds) => now = now.AddSeconds(seconds);
    }

    private sealed class FakeEmailOtpSender : IEmailOtpSender
    {
        internal string Last { get; private set; } = "";
        internal int Sent { get; private set; }
        internal Func<CancellationToken, Task> Send { get; set; } = _ => Task.CompletedTask;
        public Task SendAsync(string email, string code, CancellationToken cancellationToken)
        {
            Last = code;
            Sent++;
            return Send(cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal Clock Clock { get; } = new();
        internal FakeEmailOtpSender Sender { get; } = new();
        internal EmailOtpAccount Account { get; set; } =
            new(1, "synthetic", true, "otp@example.invalid", DateTime.UnixEpoch, "MEDICAL_REVIEWER", null, null);
        internal bool LookupFails { get; set; }
        internal VerifiedAccount Verified => new(Account.Id, Account.Username, [Account.PrimaryRole!]);
        internal EmailOtpChallengeStore Store { get; }
        internal EmailOtpService Service { get; }

        internal Fixture(EmailOtpOptions? options = null)
        {
            options ??= new();
            Store = new(Clock, options);
            Service = new((id, _) => LookupFails ? throw new IOException() :
                Task.FromResult(id == Account.Id ? Account : null), Store, Sender, Clock, options);
        }

        internal void NextAccount() => Account = Account with { Id = Account.Id + 1, Username = "synthetic-other" };
        internal Task<EmailOtpResult> Create() => Service.CreateAsync(Verified, Source);
        internal async Task<string> Start()
        {
            var result = await Create();
            Check(result.Status == EmailOtpStatus.Pending && result.ChallengeId is { Length: 64 });
            return result.ChallengeId!;
        }
        internal async Task Verify(string id, string code, EmailOtpStatus expected) =>
            Check((await Service.VerifyAsync(id, Source, code)).Status == expected);
        public void Dispose() => Store.Dispose();
    }
}
