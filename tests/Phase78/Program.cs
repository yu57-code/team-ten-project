using EmailOwnershipVerification;
using ImagingAI.Api.Services;
using MimeKit;

var tests = new (string Name, Func<Task> Run)[]
{
    ("eligible allowlist, both role orders and rejection cases", async () =>
    {
        using var f = new Fixture();
        Check(f.Row.Eligible);
        Check((f.Row with { Username = "413570452", RoleId = 3, PrimaryRole = "SYSTEM_ADMIN",
            SecondaryRole = "MEDICAL_REVIEWER", SecondaryRoleId = 2 }).Eligible);
        foreach (var row in new[] {
            f.Row with { Active = false }, f.Row with { Username = "dev_uploader" },
            f.Row with { Username = "other" }, f.Row with { Email = null },
            f.Row with { VerifiedAt = DateTime.UnixEpoch }, f.Row with { PrimaryRole = "SYSTEM_ADMIN" },
            f.Row with { Email = "first@example.invalid,second@example.invalid" },
            f.Row with { Version = "" }
        })
        {
            Check(!row.Eligible);
            using var invalid = new Fixture(row);
            Check(await invalid.Flow.SendAsync(true) == Outcome.Invalid && invalid.Sends == 0);
        }
    }),
    ("no confirmation or missing source does not send", async () =>
    {
        using var f = new Fixture();
        Check(await f.Flow.SendAsync(false) == Outcome.Invalid);
        f.Read = _ => throw new IOException("synthetic-private-detail");
        Check(await f.Flow.SendAsync(true) == Outcome.Unavailable && f.Sends == 0);
    }),
    ("ASCII format and leading zero", () =>
    {
        Check(EmailOtpCode.Format(123) == "000123" && !EmailOtpCode.IsValid("０12345"));
        return Task.CompletedTask;
    }),
    ("OTP acceptance, replay and evidence one-time release", async () =>
    {
        using var f = new Fixture();
        var c = await f.Start();
        var result = await f.Flow.VerifyAsync(c);
        Check(result is { Status: Outcome.Verified, Evidence: not null });
        var e = result.Evidence!;
        Check(e.Email == f.Row.Email && e.Purpose == Ownership.Purpose && e.ExpiresUtc - e.ConfirmedUtc == TimeSpan.FromMinutes(5));
        Check(!f.Flow.ReleaseForMaintenance(e with { Version = "50" }));
        Check(!f.Flow.ReleaseForMaintenance(e with { Purpose = "login-otp" }));
        Check(f.Flow.ReleaseForMaintenance(e) && !f.Flow.ReleaseForMaintenance(e));
        Check((await f.Flow.VerifyAsync(c)).Evidence is null);
    }),
    ("five wrong attempts including malformed input", async () =>
    {
        using var f = new Fixture();
        var code = await f.Start();
        for (var i = 0; i < 4; i++) Check((await f.Flow.VerifyAsync("invalid")).Status == Outcome.Invalid);
        Check((await f.Flow.VerifyAsync("bad")).Status == Outcome.Locked);
        Check((await f.Flow.VerifyAsync(code)).Status == Outcome.Locked && await f.Flow.SendAsync(true) == Outcome.Invalid);
    }),
    ("resend minimum interval, cap and all old codes denied", async () =>
    {
        using var f = new Fixture();
        var old = await f.Start();
        f.Clock.Advance(59); Check(await f.Flow.SendAsync(true) == Outcome.Wait);
        f.Clock.Advance(1); var second = await f.Start();
        f.Clock.Advance(60); var current = await f.Start();
        Check(new[] { old, second, current }.Distinct().Count() == 3);
        f.Clock.Advance(60); Check(await f.Flow.SendAsync(true) == Outcome.Locked && f.Sends == 3);
        foreach (var code in new[] { old, second }) Check((await f.Flow.VerifyAsync(code)).Status == Outcome.Invalid);
        Check((await f.Flow.VerifyAsync(current)).Status == Outcome.Verified);
    }),
    ("resend preserves both deadline and wrong attempts", async () =>
    {
        using var budget = new Fixture();
        await budget.Start();
        for (var i = 0; i < 4; i++) Check((await budget.Flow.VerifyAsync("bad")).Status == Outcome.Invalid);
        budget.Clock.Advance(60); await budget.Start();
        Check((await budget.Flow.VerifyAsync("bad")).Status == Outcome.Locked);
        using var lifetime = new Fixture();
        await lifetime.Start(); lifetime.Clock.Advance(299);
        var code = await lifetime.Start(); lifetime.Clock.Advance(1);
        Check((await lifetime.Flow.VerifyAsync(code)).Status == Outcome.Expired);
    }),
    ("parallel verification consumes once", async () =>
    {
        using var f = new Fixture();
        var code = await f.Start();
        var result = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => f.Flow.VerifyAsync(code))));
        Check(result.Count(x => x.Status == Outcome.Verified) == 1);
    }),
    ("in-flight send rejects verification and resend", async () =>
    {
        using var f = new Fixture();
        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Delivery = _ => wait.Task;
        var send = f.Flow.SendAsync(true);
        Check((await f.Flow.VerifyAsync(f.Code)).Status == Outcome.Busy && await f.Flow.SendAsync(true) == Outcome.Busy);
        wait.SetResult();
        Check(await send == Outcome.Pending);
    }),
    ("delivery failure, cancel, timeout and unknown results stay unusable", async () =>
    {
        foreach (var error in new Exception[] { new InvalidOperationException(), new OperationCanceledException(),
                     new TimeoutException(), new IOException("synthetic-unknown-acceptance") })
        {
            using var f = new Fixture();
            f.Delivery = _ => Task.FromException(error);
            Check(await f.Flow.SendAsync(true) == Outcome.Unavailable);
            Check((await f.Flow.VerifyAsync(f.Code)).Evidence is null && f.Sends == 1);
        }
        using var resend = new Fixture();
        var old = await resend.Start();
        resend.Clock.Advance(60); resend.Delivery = _ => Task.FromException(new IOException());
        Check(await resend.Flow.SendAsync(true) == Outcome.Unavailable && (await resend.Flow.VerifyAsync(old)).Evidence is null);
    }),
    ("restart, cross-account, cross-email and offline evidence isolation", async () =>
    {
        using var first = new Fixture();
        var code = await first.Start();
        using var restart = new Fixture();
        Check((await restart.Flow.VerifyAsync(code)).Evidence is null);
        using var newAccount = new Fixture(first.Row with { Username = "413570438", PrimaryRole = "SYSTEM_ADMIN", Email = "other@example.invalid" });
        var different = await newAccount.Start();
        if (different == code) { newAccount.Clock.Advance(60); different = await newAccount.Start(); }
        Check(different != code && (await newAccount.Flow.VerifyAsync(code)).Evidence is null);
        using var offline = new Fixture(live: false);
        var offlineEvidence = (await offline.Flow.VerifyAsync(await offline.Start())).Evidence!;
        Check(offlineEvidence.Purpose == "offline-test-only" &&
            !offline.Flow.ReleaseForMaintenance(offlineEvidence) && !first.Flow.ReleaseForMaintenance(offlineEvidence));
    }),
    ("snapshot changes revoke including Email ABA through xmin", async () =>
    {
        foreach (var change in new Func<Account, Account?>[] {
            a => a with { Email = "new@example.invalid" }, a => a with { Version = "21" },
            a => a with { Active = false }, a => a with { RoleId = 3 },
            a => a with { VerifiedAt = DateTime.UnixEpoch }, _ => null
        })
        {
            using var f = new Fixture();
            var code = await f.Start();
            f.Read = _ => Task.FromResult(change(f.Row));
            Check((await f.Flow.VerifyAsync(code)).Status == Outcome.Unavailable);
            Check((await f.Flow.VerifyAsync(code)).Evidence is null);
        }
    }),
    ("expired evidence and foreign-process evidence are not released", async () =>
    {
        using var first = new Fixture(); using var other = new Fixture();
        var e = (await first.Flow.VerifyAsync(await first.Start())).Evidence!;
        _ = await other.Flow.VerifyAsync(await other.Start());
        Check(!other.Flow.ReleaseForMaintenance(e));
        first.Clock.Advance(300);
        Check(!first.Flow.ReleaseForMaintenance(e));
    }),
    ("formal login store cannot accept an ownership challenge or evidence", async () =>
    {
        using var f = new Fixture();
        var code = await f.Start();
        var evidence = (await f.Flow.VerifyAsync(code)).Evidence!;
        using var loginStore = new EmailOtpChallengeStore(f.Clock, new EmailOtpOptions());
        var source = System.Net.IPAddress.Loopback;
        var loginAccount = new EmailOtpAccount(f.Row.UserId, f.Row.Username, true,
            f.Row.Email, DateTime.UnixEpoch, f.Row.PrimaryRole, null, null);
        var delivery = loginStore.Create(loginAccount, source).Delivery!;
        Check(loginStore.Complete(delivery, true).Status == EmailOtpStatus.Pending);
        Check(loginStore.Verify(evidence.ChallengeId.ToString("N"), source, loginAccount, code).Status == EmailOtpStatus.Rejected);
        Check(loginStore.ConsumeProof(evidence) is null);
        Check(loginStore.Verify(delivery.Id, source, loginAccount, delivery.Code).Status == EmailOtpStatus.Verified);
    }),
    ("SMTP opt-in and ownership-only single-recipient message", () =>
    {
        Check(!new GmailSmtpOptions().IsValid);
        var settings = new GmailSmtpOptions { Enabled = true, Username = "sender@example.invalid",
            FromAddress = "sender@example.invalid" };
        Check(!settings.IsValid);
        using var message = OwnershipMailer.Compose(settings, MailboxAddress.Parse("receiver@example.invalid"), "000123");
        Check(message.To.Mailboxes.Select(x => x.Address).SequenceEqual(["receiver@example.invalid"]) &&
            message.Cc.Count == 0 && message.Bcc.Count == 0 && message.Subject is { } subject && !subject.Contains("000123") &&
            message.TextBody is { } text && text.Contains("000123") && text.Contains("不能用於登入") &&
            !text.Contains("password") && !text.Contains("Session"));
        return Task.CompletedTask;
    })
};

var failed = 0;
foreach (var item in tests)
{
    try { await item.Run(); Console.WriteLine("PASS " + item.Name); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {item.Name} ({error.GetType().Name})"); }
}
Console.WriteLine($"Total={tests.Length}; PASS={tests.Length - failed}; FAIL={failed}");
return failed == 0 ? 0 : 1;

static void Check(bool condition) { if (!condition) throw new InvalidOperationException(); }

sealed class TestClock : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
    public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
}

sealed class Fixture : IDisposable
{
    public TestClock Clock { get; } = new();
    public Account Row { get; }
    public Ownership Flow { get; }
    public Func<CancellationToken, Task<Account?>> Read { get; set; }
    public Func<CancellationToken, Task> Delivery { get; set; } = _ => Task.CompletedTask;
    public string Code { get; private set; } = "";
    public int Sends { get; private set; }

    public Fixture(Account? account = null, bool live = true)
    {
        Row = account ?? new(11, "413570359", 2, null, "MEDICAL_REVIEWER", null, true,
            "ownership@example.invalid", null, "20");
        Read = _ => Task.FromResult<Account?>(Row);
        Flow = new(Row, "synthetic", live, Clock, token => Read(token), (recipient, code, token) =>
        {
            if (recipient != Row.Email) throw new InvalidOperationException();
            Code = code;
            Sends++;
            return Delivery(token);
        });
    }

    public async Task<string> Start()
    {
        if (await Flow.SendAsync(true) != Outcome.Pending) throw new InvalidOperationException();
        return Code;
    }

    public void Dispose() => Flow.Dispose();
}
