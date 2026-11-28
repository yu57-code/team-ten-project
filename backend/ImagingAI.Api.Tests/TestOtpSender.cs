using ImagingAI.Api.Services;

internal sealed class TestOtpSender : IEmailOtpSender
{
    internal string Code { get; private set; } = "";
    internal int Count { get; private set; }
    internal Func<CancellationToken, Task> Delivery { get; set; } = _ => Task.CompletedTask;

    public Task SendAsync(string email, string code, CancellationToken cancellationToken)
    {
        Code = code;
        Count++;
        return Delivery(cancellationToken);
    }

    internal static EmailOtpAccount Binding(AuthService.AccountCredentials row) =>
        new(row.UserId, row.Username, row.IsActive, $"synthetic-{row.UserId}@example.invalid", DateTime.UnixEpoch,
            row.PrimaryRoleCode, row.HasSecondaryRole ? 2 : null, row.SecondaryRoleCode);
}
