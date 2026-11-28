using MimeKit;

namespace ImagingAI.Api.Services;

internal sealed class GmailSmtpOptions
{
    internal const string Section = "EmailOtp:Smtp";
    internal const string GmailHost = "smtp.gmail.com";
    internal const int GmailPort = 587;

    public bool Enabled { get; set; }
    public string Host { get; set; } = GmailHost;
    public int Port { get; set; } = GmailPort;
    public string Username { get; set; } = "";
    public string AppPassword { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromDisplayName { get; set; } = "影像病例結構化平台";
    public int TimeoutSeconds { get; set; } = 10;

    internal static GmailSmtpOptions? Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        if (!bool.TryParse(section[nameof(Enabled)], out var enabled) || !enabled) return null;
        try
        {
            return section.Get<GmailSmtpOptions>() is { IsValid: true } options ? options : null;
        }
        catch (InvalidOperationException)
        {
            // Binder errors may include the configured value. Never expose them.
            return null;
        }
    }

    internal bool IsValid =>
        Enabled &&
        string.Equals(Host, GmailHost, StringComparison.OrdinalIgnoreCase) &&
        Port == GmailPort && TimeoutSeconds is >= 1 and <= 14 &&
        Mailbox(Username) is not null && Mailbox(FromAddress) is not null &&
        string.Equals(Username, FromAddress, StringComparison.OrdinalIgnoreCase) &&
        AppPassword is { Length: 16 } && AppPassword.All(char.IsAsciiLetterOrDigit) &&
        FromDisplayName is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(FromDisplayName) &&
        !FromDisplayName.Any(char.IsControl);

    internal static MailboxAddress? Mailbox(string? address) =>
        address is { Length: > 0 and <= 254 } &&
        !address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) &&
        MailboxAddress.TryParse(address, out var mailbox) &&
        string.Equals(mailbox.Address, address, StringComparison.Ordinal) &&
        address.LastIndexOf('@') > 0 && address[^1] != '@'
            ? mailbox : null;
}
