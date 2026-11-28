using System.Net;
using System.Net.Sockets;
using ImagingAI.Api.Auth;
using ImagingAI.Api.Controllers;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

internal static class GmailSmtpTests
{
    private const string Sender = "smtp-test@example.invalid";
    private const string Recipient = "otp-test@example.invalid";
    private const string Secret = "synthetic0000000";
    private const string Code = "012345";

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Smtp_registration_requires_explicit_valid_development_options", Registration);
        test("Smtp_invalid_recipient_or_otp_never_creates_transport", InvalidInput);
        test("Smtp_plain_Chinese_single_recipient_and_confirmed_acceptance", Acceptance);
        test("Smtp_downgrade_and_invalid_certificate_fail_before_credentials", TlsRejection);
        test("Smtp_real_MailKit_refuses_missing_STARTTLS_on_loopback", LoopbackWithoutStartTls);
        test("Smtp_real_MailKit_rejects_untrusted_loopback_certificate", LoopbackUntrustedTls);
        test("Smtp_connection_authentication_and_rejection_are_sanitized", Failures);
        test("Smtp_timeout_and_unknown_acceptance_are_not_retried", TimeoutAndUnknown);
        test("Smtp_cancellation_releases_transport_and_does_not_submit", Cancellation);
        test("Smtp_accepted_message_survives_cleanup_failure_without_resend", CleanupFailure);
        test("Smtp_failure_does_not_activate_OTP_or_expose_exception_details", FailedChallenge);
    }

    private static GmailSmtpOptions Valid() => new()
    {
        Enabled = true, Username = Sender, FromAddress = Sender, AppPassword = Secret
    };

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("SMTP check failed.");
    }

    private static Task Registration()
    {
        var enabled = new Dictionary<string, string?> {
            ["Auth:EnableDevelopmentLogin"] = "true",
            ["EmailOtp:Smtp:Enabled"] = "true",
            ["EmailOtp:Smtp:Username"] = Sender,
            ["EmailOtp:Smtp:FromAddress"] = Sender,
            ["EmailOtp:Smtp:AppPassword"] = Secret
        };
        bool HasSender(Dictionary<string, string?> values, string environment)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection().AddLogging();
            var mvc = services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            AuthRegistration.AddDevelopmentAuth(mvc, new TestEnvironment(Path.GetTempPath())
                { EnvironmentName = environment }, config);
            using var provider = services.BuildServiceProvider();
            var sender = provider.GetService<IEmailOtpSender>();
            Check(sender is null or GmailSmtpOtpSender);
            return sender is not null;
        }

        Check(!new GmailSmtpOptions().Enabled && !HasSender(new(), "Development"));
        Check(HasSender(enabled, "Development") && !HasSender(enabled, "Production"));
        foreach (var (key, value) in new (string, string?)[]
        {
            ("Auth:EnableDevelopmentLogin", "false"), ("EmailOtp:Smtp:Enabled", "false"),
            ("EmailOtp:Smtp:Enabled", "invalid"), ("EmailOtp:Smtp:Username", null),
            ("EmailOtp:Smtp:AppPassword", ""), ("EmailOtp:Smtp:Host", "smtp.example.invalid"),
            ("EmailOtp:Smtp:Port", "465"), ("EmailOtp:Smtp:Port", "invalid"),
            ("EmailOtp:Smtp:FromAddress", Recipient), ("EmailOtp:Smtp:TimeoutSeconds", "15")
        })
        {
            var config = new Dictionary<string, string?>(enabled) { [key] = value };
            Check(!HasSender(config, "Development"));
        }
        return Task.CompletedTask;
    }

    private static async Task InvalidInput()
    {
        var transport = new Transport();
        var sender = Make(transport);
        await Reject(() => sender.SendAsync("target@example.invalid,second@example.invalid", Code, default),
            GmailSmtpFailure.InvalidMessage);
        await Reject(() => sender.SendAsync(Recipient, "１２３４５６", default), GmailSmtpFailure.InvalidMessage);
        var disabled = new GmailSmtpOtpSender(new(), _ => throw new InvalidOperationException(),
            TimeProvider.System, new Logs());
        await Reject(() => disabled.SendAsync(Recipient, Code, default), GmailSmtpFailure.InvalidConfiguration);
        Check(transport.Calls.Count == 0 && !transport.Disposed);
    }

    private static async Task Acceptance()
    {
        var transport = new Transport();
        await Make(transport).SendAsync(Recipient, Code, default);
        Check(transport.Calls.SequenceEqual(new[] { "connect", "authenticate", "submit" }) && transport.Disposed);
        Check(transport.Subject == "【影像病例結構化平台】登入驗證碼" && !transport.Subject.Contains(Code));
        Check(transport.Body is { } body && body.Contains("您的登入驗證碼為：" + Code) &&
            body.Contains("請於驗證碼有效期限內完成驗證") && !body.Contains("5 分鐘") &&
            !body.Contains(Sender) && !body.Contains(Recipient) && !body.Contains(Secret) &&
            !body.Contains("https://"));
    }

    private static async Task TlsRejection()
    {
        var downgrade = new Transport { IsSecure = false };
        await Reject(() => Make(downgrade).SendAsync(Recipient, Code, default), GmailSmtpFailure.Tls);
        Check(downgrade.Calls.SequenceEqual(new[] { "connect" }) && downgrade.Disposed);
        var invalidCert = new Transport { ConnectError = new System.Security.Authentication.AuthenticationException(Secret) };
        await Reject(() => Make(invalidCert).SendAsync(Recipient, Code, default), GmailSmtpFailure.Tls);
        Check(invalidCert.Calls.SequenceEqual(new[] { "connect" }) && invalidCert.Disposed);
    }

    private static Task LoopbackWithoutStartTls() => Loopback(tls: false);
    private static Task LoopbackUntrustedTls() => Loopback(tls: true);

    private static async Task Loopback(bool tls)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = limit.Token;
        async Task Server()
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            await using var stream = peer.GetStream();
            using var input = new StreamReader(stream, leaveOpen: true);
            using var output = new StreamWriter(stream, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await output.WriteLineAsync("220 localhost SMTP");
            Check((await input.ReadLineAsync(token))?.StartsWith("EHLO ", StringComparison.Ordinal) == true);
            await output.WriteLineAsync(tls ? "250-localhost\r\n250 STARTTLS" : "250 localhost");
            if (!tls) return;
            Check(await input.ReadLineAsync(token) == "STARTTLS");
            await output.WriteLineAsync("220 Start TLS");
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(3));
            var pfx = issued.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(pfx);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(pfx);
            using var ssl = new System.Net.Security.SslStream(stream, leaveInnerStreamOpen: true);
            try
            {
                await ssl.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
                    { ServerCertificate = certificate }, token);
            }
            catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException) { }
        }
        var server = Server();
        using var client = new MailKitGmailSmtpTransport(2000);
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.StartTls, token);
            throw new InvalidOperationException("Connect accepted an insecure SMTP fixture.");
        }
        catch (NotSupportedException) when (!tls) { }
        catch (SslHandshakeException) when (tls) { }
        await server;
    }

    private static async Task Failures()
    {
        var logs = new Logs();
        var cases = new (Transport Transport, GmailSmtpFailure Reason)[] {
            (new() { ConnectError = new SocketException((int)SocketError.HostNotFound) }, GmailSmtpFailure.Connection),
            (new() { AuthenticateError = new MailKit.Security.AuthenticationException(Secret) }, GmailSmtpFailure.Authentication),
            (new() { Submit = _ => throw new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, (SmtpStatusCode)550, Sender) },
                GmailSmtpFailure.PermanentRejection),
            (new() { Submit = _ => throw new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, (SmtpStatusCode)450, Recipient) },
                GmailSmtpFailure.TemporaryRejection)
        };
        foreach (var (transport, reason) in cases)
        {
            await Reject(() => Make(transport, logs).SendAsync(Recipient, Code, default), reason);
            Check(transport.Disposed && transport.Calls.Count(x => x == "submit") <= 1);
        }
        Check(logs.Messages.Count == cases.Length &&
            !logs.Messages.Any(message => new[] { Secret, Sender, Recipient, Code }.Any(message.Contains)));
    }

    private static async Task TimeoutAndUnknown()
    {
        var timeout = new Transport { Submit = token => Task.Delay(Timeout.Infinite, token) };
        var options = Valid();
        options.TimeoutSeconds = 1;
        await Reject(() => Make(timeout, settings: options).SendAsync(Recipient, Code, default),
            GmailSmtpFailure.AcceptanceUnknown);
        var unknown = new Transport { Submit = _ => throw new IOException(Recipient) };
        await Reject(() => Make(unknown).SendAsync(Recipient, Code, default), GmailSmtpFailure.AcceptanceUnknown);
        foreach (var transport in new[] { timeout, unknown })
            Check(transport.Disposed && transport.Calls.Count(x => x == "submit") == 1);
    }

    private static async Task Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new Transport { AfterConnect = cancellation.Cancel };
        try { await Make(transport).SendAsync(Recipient, Code, cancellation.Token); throw new InvalidOperationException(); }
        catch (OperationCanceledException error) { Check(error.CancellationToken == cancellation.Token); }
        Check(transport.Disposed && transport.Calls.SequenceEqual(new[] { "connect" }));
    }

    private static async Task CleanupFailure()
    {
        var transport = new Transport { DisposalError = true };
        await Make(transport).SendAsync(Recipient, Code, default);
        Check(transport.Disposed && transport.Calls.Count(x => x == "submit") == 1);
    }

    private static async Task FailedChallenge()
    {
        var options = new EmailOtpOptions();
        using var store = new EmailOtpChallengeStore(TimeProvider.System, options);
        var transport = new Transport { Submit = _ => throw new IOException(Secret) };
        var account = new EmailOtpAccount(71, "synthetic", true, Recipient, DateTime.UnixEpoch,
            "SYSTEM_ADMIN", null, null);
        var service = new EmailOtpService((_, _) => Task.FromResult<EmailOtpAccount?>(account), store,
            Make(transport), TimeProvider.System, options);
        var result = await service.CreateAsync(new VerifiedAccount(account.Id, account.Username, ["SYSTEM_ADMIN"]),
            IPAddress.Loopback);
        Check(result is { Status: EmailOtpStatus.DeliveryFailed, ChallengeId: null, Proof: null } &&
            transport.Calls.Count(x => x == "submit") == 1);
    }

    private static GmailSmtpOtpSender Make(Transport transport, Logs? logs = null, GmailSmtpOptions? settings = null) =>
        new(settings ?? Valid(), timeout => { Check(timeout is >= 1000 and <= 14000); return transport; },
            TimeProvider.System, logs ?? new());

    private static async Task Reject(Func<Task> action, GmailSmtpFailure reason)
    {
        try { await action(); throw new InvalidOperationException(); }
        catch (GmailSmtpException error) { Check(error.Reason == reason && error.InnerException is null); }
    }

    private sealed class Transport : IGmailSmtpTransport
    {
        public bool IsSecure { get; init; } = true;
        internal List<string> Calls { get; } = [];
        internal Action? AfterConnect { get; init; }
        internal Exception? ConnectError { get; init; }
        internal Exception? AuthenticateError { get; init; }
        internal bool DisposalError { get; init; }
        internal Func<CancellationToken, Task> Submit { get; init; } = _ => Task.CompletedTask;
        internal bool Disposed { get; private set; }
        internal string? Subject { get; private set; }
        internal string? Body { get; private set; }

        public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken token)
        {
            Check(host == GmailSmtpOptions.GmailHost && port == 587 && security == SecureSocketOptions.StartTls);
            Calls.Add("connect");
            if (ConnectError is not null) throw ConnectError;
            AfterConnect?.Invoke();
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string username, string password, CancellationToken token)
        {
            Check(IsSecure && username == Sender && password == Secret);
            Calls.Add("authenticate");
            if (AuthenticateError is not null) throw AuthenticateError;
            return Task.CompletedTask;
        }

        public Task SendAsync(MimeMessage message, CancellationToken token)
        {
            Calls.Add("submit");
            Check(message.To.Mailboxes.Single().Address == Recipient &&
                message.From.Mailboxes.Single().Address == Sender && message.Cc.Count == 0 && message.Bcc.Count == 0 &&
                message.Body is TextPart { IsPlain: true } && !message.Attachments.Any());
            Body = message.TextBody;
            Subject = message.Subject;
            return Submit(token);
        }

        public void Dispose()
        {
            Disposed = true;
            if (DisposalError) throw new IOException(Secret);
        }
    }

    private sealed class Logs : ILogger<GmailSmtpOtpSender>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Check(exception is null);
            Messages.Add(formatter(state, exception));
        }
    }
}
