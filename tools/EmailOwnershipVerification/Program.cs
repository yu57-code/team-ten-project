using System.Security.Cryptography;
using System.Text.Json;
using EmailOwnershipVerification;
using ImagingAI.Api.Services;
using Microsoft.Extensions.Configuration;

if (args is ["offline"])
{
    var sample = new Account(1, "413570359", 2, null, "MEDICAL_REVIEWER", null, true,
        "ownership@example.invalid", null, "20");
    string? captured = null;
    using var demo = new Ownership(sample, "offline", false, TimeProvider.System, _ => Task.FromResult<Account?>(sample),
        (_, code, _) => { captured = code; return Task.CompletedTask; });
    var sent = await demo.SendAsync(true);
    var result = await demo.VerifyAsync(captured ?? "");
    if (sent != Outcome.Pending || result.Evidence is null || demo.ReleaseForMaintenance(result.Evidence)) return 1;
    Console.WriteLine("PASS offline-only ownership challenge; no network or maintenance evidence.");
    return 0;
}

if (args is not [("check" or "verify"), _] || !Account.Allowed(args[1]) ||
    Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("Usage: offline | check <allowed-username> | verify <allowed-username> (interactive)");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var live = args[0] == "verify";
try
{
    GmailSmtpOptions? smtp = null;
    if (live)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        smtp = GmailSmtpOptions.Read(config);
        if (smtp is null) { Console.WriteLine("SMTP unavailable. No connection attempted."); return 2; }
    }

    // Exclusive file handle covers all accounts in this OS user profile for the lifetime of the command.
    using var lease = live ? AcquireLocalLease() : null;
    var password = Hidden("PostgreSQL password");
    var db = new ReadOnlyAccounts(password);
    password = "";
    var account = await db.ReadAsync(args[1], stop.Token);
    if (account is { VersionReadByTool: false })
    {
        Console.WriteLine("The app account cannot read xmin. A maintenance snapshot is necessary before submitting SQL.");
        if (live && (account with { Version = "0" }).Eligible)
        {
            Console.WriteLine("Enter the xmin freshly obtained in pgAdmin for this exact account and bound address; Enter cancels.");
            var version = ReadCommand(stop.Token);
            if (!uint.TryParse(version, out var parsed)) return 3;
            db.MaintenanceVersion = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            account = await db.ReadAsync(args[1], stop.Token);
        }
        else if (!live && (account with { Version = "0" }).Eligible)
        {
            Console.WriteLine($"Eligible apart from maintenance version handoff: {account.Username} | {account.Email![0]}***@***");
            return 0;
        }
    }
    if (account is not { Eligible: true })
    {
        Console.WriteLine("Account is missing or ineligible.");
        return 2;
    }
    Console.WriteLine($"Eligible: {account.Username} | {account.Email![0]}***@*** | {account.PrimaryRole} {account.SecondaryRole}");
    if (!live) return 0;

    Console.WriteLine("Confirm the enrolled account with the recipient. Type SEND to send ownership mail, or Enter to cancel.");
    if (ReadCommand(stop.Token) != "SEND") return 3;
    var sender = new OwnershipMailer(smtp!);
    using var flow = new Ownership(account, ReadOnlyAccounts.Target, true, TimeProvider.System,
        token => db.ReadAsync(account.Username, token), sender.SendAsync);
    var status = await flow.SendAsync(true, stop.Token);
    while (status is Outcome.Pending or Outcome.Invalid or Outcome.Wait)
    {
        Console.WriteLine("V = enter recipient's code, R = resend, Enter = cancel. Codes expire after 300 seconds.");
        switch (ReadCommand(stop.Token))
        {
            case "R":
                status = await flow.SendAsync(true, stop.Token);
                if (status == Outcome.Wait) Console.WriteLine("Resend unavailable before 60 seconds.");
                break;
            case "V":
                var result = await flow.VerifyAsync(Hidden("Ownership code", 32), stop.Token);
                status = result.Status;
                if (result.Evidence is { } evidence && flow.ReleaseForMaintenance(evidence))
                {
                    Console.WriteLine("Private maintenance handoff follows. Do not log, redirect or share this output.");
                    Console.WriteLine(JsonSerializer.Serialize(evidence, new JsonSerializerOptions
                        { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
                    return 0;
                }
                Console.WriteLine($"Verification result: {status}");
                break;
            default: return 3;
        }
    }
    Console.WriteLine($"Stopped: {status}. Do not use any previously mailed code.");
    return 2;
}
catch (OperationCanceledException) { Console.WriteLine("Cancelled."); return 3; }
catch (Exception error)
{
    Console.Error.WriteLine(error is IOException ? "Local tool already running or local storage unavailable."
        : "Operation unavailable. No verification was recorded.");
    return 2;
}

static FileStream AcquireLocalLease()
{
    var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrEmpty(root)) throw new IOException();
    var directory = Directory.CreateDirectory(Path.Combine(root, "ImagingAI", "EmailOwnership"));
    return new FileStream(Path.Combine(directory.FullName, "active.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);
}

static string? ReadCommand(CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    var value = Console.ReadLine();
    token.ThrowIfCancellationRequested();
    return value;
}

static string Hidden(string label, int limit = 512)
{
    Console.Write($"{label} (hidden): ");
    var buffer = new char[limit];
    var length = 0;
    try
    {
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(buffer, 0, length);
            }
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace && length > 0) buffer[--length] = '\0';
            else if (!char.IsControl(key.KeyChar))
            {
                if (length == limit) throw new InvalidOperationException();
                buffer[length++] = key.KeyChar;
            }
        }
    }
    finally { CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan())); }
}
