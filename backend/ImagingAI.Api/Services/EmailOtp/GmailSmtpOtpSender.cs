using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ImagingAI.Api.Services;

internal enum GmailSmtpFailure
{
    InvalidConfiguration, InvalidMessage, Connection, Tls, Authentication,
    TemporaryRejection, PermanentRejection, Timeout, AcceptanceUnknown
}

internal sealed class GmailSmtpException(GmailSmtpFailure reason) : Exception(reason.ToString())
{
    internal GmailSmtpFailure Reason { get; } = reason;
}

internal sealed class GmailSmtpOtpSender(
    GmailSmtpOptions options, Func<int, IGmailSmtpTransport> createTransport,
    TimeProvider clock, ILogger<GmailSmtpOtpSender> logger) : IEmailOtpSender
{
    private enum Stage { Connect, Authenticate, Submit }

    public async Task SendAsync(string email, string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.IsValid) throw new GmailSmtpException(GmailSmtpFailure.InvalidConfiguration);
        if (GmailSmtpOptions.Mailbox(email) is not { } recipient || !EmailOtpCode.IsValid(code))
            throw new GmailSmtpException(GmailSmtpFailure.InvalidMessage);

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromDisplayName, options.FromAddress));
        message.To.Add(recipient);
        message.Subject = "【影像病例結構化平台】登入驗證碼";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                您好：

                您正在進行影像病例結構化平台的登入身分驗證。

                您的登入驗證碼為：{code}

                請於驗證碼有效期限內完成驗證，請勿將驗證碼提供給他人。

                若您未嘗試登入，請忽略此郵件。

                此為系統自動發送的郵件，請勿直接回覆。
                """
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var token = linked.Token;
        IGmailSmtpTransport? transport = null;
        var stage = Stage.Connect;
        try
        {
            transport = createTransport(options.TimeoutSeconds * 1000);
            await transport.ConnectAsync(GmailSmtpOptions.GmailHost, GmailSmtpOptions.GmailPort,
                SecureSocketOptions.StartTls, token);
            token.ThrowIfCancellationRequested();
            if (!transport.IsSecure) throw new GmailSmtpException(GmailSmtpFailure.Tls);

            stage = Stage.Authenticate;
            await transport.AuthenticateAsync(options.Username, options.AppPassword, token);
            token.ThrowIfCancellationRequested();

            stage = Stage.Submit;
            await transport.SendAsync(message, token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("SMTP OTP operation cancelled.");
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception error)
        {
            var reason = Classify(error, stage, deadline.IsCancellationRequested);
            logger.LogWarning("SMTP OTP delivery failed ({Failure}).", reason);
            throw new GmailSmtpException(reason);
        }
        finally
        {
            if (transport is not null)
            {
                try { transport.Dispose(); }
                catch (Exception) { logger.LogWarning("SMTP OTP transport cleanup failed."); }
            }
        }
        logger.LogInformation("SMTP server accepted the OTP message.");
    }

    private static GmailSmtpFailure Classify(Exception error, Stage stage, bool deadline) => error switch
    {
        GmailSmtpException safe => safe.Reason,
        SslHandshakeException or System.Security.Authentication.AuthenticationException => GmailSmtpFailure.Tls,
        TimeoutException or OperationCanceledException => stage == Stage.Submit
            ? GmailSmtpFailure.AcceptanceUnknown : GmailSmtpFailure.Timeout,
        _ when deadline => stage == Stage.Submit ? GmailSmtpFailure.AcceptanceUnknown : GmailSmtpFailure.Timeout,
        _ when stage == Stage.Authenticate => GmailSmtpFailure.Authentication,
        SmtpCommandException command when stage == Stage.Submit && (int)command.StatusCode is >= 400 and < 500
            => GmailSmtpFailure.TemporaryRejection,
        SmtpCommandException command when stage == Stage.Submit && (int)command.StatusCode is >= 500 and < 600
            => GmailSmtpFailure.PermanentRejection,
        _ => stage == Stage.Submit ? GmailSmtpFailure.AcceptanceUnknown : GmailSmtpFailure.Connection
    };
}
