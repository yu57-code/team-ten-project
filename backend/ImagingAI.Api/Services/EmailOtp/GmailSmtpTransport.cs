using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ImagingAI.Api.Services;

internal interface IGmailSmtpTransport : IDisposable
{
    bool IsSecure { get; }
    Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken token);
    Task AuthenticateAsync(string username, string password, CancellationToken token);
    Task SendAsync(MimeMessage message, CancellationToken token);
}

internal sealed class MailKitGmailSmtpTransport(int timeoutMilliseconds) : IGmailSmtpTransport
{
    // Keep MailKit's certificate and hostname validation. Use its default null protocol logger.
    private readonly SmtpClient client = new() { Timeout = timeoutMilliseconds };

    public bool IsSecure => client.IsSecure;
    public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken token) =>
        client.ConnectAsync(host, port, security, token);
    public Task AuthenticateAsync(string username, string password, CancellationToken token) =>
        client.AuthenticateAsync(new SaslMechanismPlain(username, password), token);
    public async Task SendAsync(MimeMessage message, CancellationToken token)
    {
        var result = await client.SendAsync(new SendRequest(message), token);
        if (result.AcceptedRecipients.Count != 1 || result.RejectedRecipients.Count != 0)
            throw new GmailSmtpException(GmailSmtpFailure.AcceptanceUnknown);
    }
    public void Dispose() => client.Dispose();
}
