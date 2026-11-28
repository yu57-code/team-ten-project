using ImagingAI.Api.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace EmailOwnershipVerification;

internal sealed class OwnershipMailer(GmailSmtpOptions settings)
{
    private enum Stage { Connect, Authenticate, Submit }

    internal async Task SendAsync(string email, string code, CancellationToken cancellationToken)
    {
        if (!settings.IsValid || GmailSmtpOptions.Mailbox(email) is not { } recipient ||
            !EmailOtpCode.IsValid(code)) throw new InvalidOperationException();
        using var message = Compose(settings, recipient, code);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var token = deadline.Token;
        var client = new MailKitGmailSmtpTransport(settings.TimeoutSeconds * 1000);
        var stage = Stage.Connect;
        try
        {
            await client.ConnectAsync(GmailSmtpOptions.GmailHost, GmailSmtpOptions.GmailPort,
                SecureSocketOptions.StartTls, token);
            token.ThrowIfCancellationRequested();
            if (!client.IsSecure) throw new GmailSmtpException(GmailSmtpFailure.Tls);
            stage = Stage.Authenticate;
            await client.AuthenticateAsync(settings.Username, settings.AppPassword, token);
            token.ThrowIfCancellationRequested();
            stage = Stage.Submit;
            await client.SendAsync(message, token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception error)
        {
            var reason = error switch
            {
                GmailSmtpException safe => safe.Reason,
                SslHandshakeException or System.Security.Authentication.AuthenticationException => GmailSmtpFailure.Tls,
                SmtpCommandException command when stage == Stage.Submit && (int)command.StatusCode is >= 400 and < 500
                    => GmailSmtpFailure.TemporaryRejection,
                SmtpCommandException command when stage == Stage.Submit && (int)command.StatusCode is >= 500 and < 600
                    => GmailSmtpFailure.PermanentRejection,
                _ when stage == Stage.Submit => GmailSmtpFailure.AcceptanceUnknown,
                _ when token.IsCancellationRequested || error is TimeoutException => GmailSmtpFailure.Timeout,
                _ when stage == Stage.Authenticate => GmailSmtpFailure.Authentication,
                _ => GmailSmtpFailure.Connection
            };
            throw new GmailSmtpException(reason);
        }
        finally
        {
            try { client.Dispose(); }
            catch (Exception) { /* Cleanup cannot turn an accepted submission into another send. */ }
        }
    }

    internal static MimeMessage Compose(GmailSmtpOptions settings, MailboxAddress recipient, string code)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromDisplayName, settings.FromAddress));
        message.To.Add(recipient);
        message.Subject = "【影像病例結構化平台】Email 所有權確認";
        message.Body = new TextPart("plain") { Text = $"""
            您好：

            此確認碼只用於證明您可以收取此電子郵件，不能用於登入系統。
            您的 Email 所有權確認碼為：{code}

            請在有效期限內，透過已約定的維護流程完成確認。
            若您未要求綁定此地址，請忽略這封郵件。
            """ };
        return message;
    }
}
