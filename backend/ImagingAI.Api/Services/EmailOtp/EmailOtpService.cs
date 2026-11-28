using System.Net;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ImagingAI.Api.Services;

internal sealed class EmailOtpService(
    Func<long, CancellationToken, Task<EmailOtpAccount?>> lookup,
    EmailOtpChallengeStore store, IEmailOtpSender? sender, TimeProvider clock, EmailOtpOptions options)
{
    internal static Task<EmailOtpAccount?> ReadAccountAsync(ImagingAiDbContext database, long id,
        CancellationToken cancellationToken) =>
        database.UserAccounts.AsNoTracking().Where(user => user.UserId == id)
            .Select(user => new EmailOtpAccount(user.UserId, user.Username, user.IsActive,
                user.Email, user.EmailVerifiedAt, user.Role.RoleCode, user.SecondaryRoleId,
                user.SecondaryRole == null ? null : user.SecondaryRole.RoleCode))
            .SingleOrDefaultAsync(cancellationToken);

    internal async Task<EmailOtpResult> CreateAsync(VerifiedAccount verified, IPAddress source,
        CancellationToken cancellationToken = default)
    {
        if (sender is null) return new(EmailOtpStatus.Unavailable);
        source = Normalize(source);
        var (ok, account) = await ReadAsync(verified.UserId, cancellationToken);
        if (!ok) return new(EmailOtpStatus.Unavailable);
        if (account is not { IsEligible: true } || account.Id != verified.UserId ||
            account.Username != verified.Username || !account.MatchesRoles(verified.RoleCodes))
            return new(EmailOtpStatus.Rejected);
        var (status, delivery) = store.Create(account, source);
        return delivery is null ? new(status) : await SendAsync(account, delivery, cancellationToken);
    }

    internal async Task<EmailOtpResult> ResendAsync(string id, IPAddress source,
        CancellationToken cancellationToken = default)
    {
        if (sender is null) return new(EmailOtpStatus.Unavailable);
        source = Normalize(source);
        if (store.FindAccount(id, source) is not long accountId) return new(EmailOtpStatus.Rejected);
        var (ok, account) = await ReadAsync(accountId, cancellationToken);
        if (!ok)
        {
            store.Revoke(id, source);
            return new(EmailOtpStatus.Unavailable);
        }
        var (status, delivery) = store.Resend(id, source, account);
        return delivery is null
            ? new(status) { RetryAfterSeconds = status == EmailOtpStatus.RateLimited ? store.ResendRetryAfter(id, source) : 0 }
            : await SendAsync(account!, delivery, cancellationToken);
    }

    internal async Task<EmailOtpResult> VerifyAsync(string id, IPAddress source, string? code,
        CancellationToken cancellationToken = default)
    {
        source = Normalize(source);
        if (store.FindAccount(id, source) is not long accountId) return new(EmailOtpStatus.Rejected);
        var (ok, account) = await ReadAsync(accountId, cancellationToken);
        if (!ok)
        {
            store.Revoke(id, source);
            return new(EmailOtpStatus.Unavailable);
        }
        return store.Verify(id, source, account, code);
    }

    private async Task<(bool, EmailOtpAccount?)> ReadAsync(long id, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await lookup(id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return (true, result);
        }
        catch (Exception) { return (false, null); }
    }

    internal EmailOtpAccount? ConsumeProof(object? proof) => store.ConsumeProof(proof);

    internal void Revoke(string id, IPAddress source) => store.Revoke(id, Normalize(source));

    internal async Task<RoleConfirmationStatus> RevalidateAsync(EmailOtpAccount binding, CancellationToken token)
    {
        var (ok, account) = await ReadAsync(binding.Id, token);
        return !ok ? RoleConfirmationStatus.AccountStoreUnavailable
            : account is { IsEligible: true } && account == binding
                ? RoleConfirmationStatus.RoleConfirmed : RoleConfirmationStatus.InvalidSelection;
    }

    private async Task<EmailOtpResult> SendAsync(EmailOtpAccount account, EmailOtpChallengeStore.Delivery delivery,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.SendTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await sender!.SendAsync(account.Email!, delivery.Code, linked.Token).WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            // Delivery may have happened. Never make this version usable in that case.
            return store.Complete(delivery, sent: false);
        }
        return store.Complete(delivery, sent: true);
    }

    private static IPAddress Normalize(IPAddress source) =>
        source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source;
}
