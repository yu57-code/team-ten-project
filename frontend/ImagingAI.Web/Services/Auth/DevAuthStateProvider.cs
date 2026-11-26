using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using static ImagingAI.Web.Services.Auth.DevAuthIdentityClient;

namespace ImagingAI.Web.Services.Auth;

public sealed class DevAuthStateProvider(DevAuthIdentityClient identityClient, TimeProvider clock)
    : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider, IAsyncDisposable
{
    private static readonly TimeSpan RevalidationInterval = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private AuthenticationState current = Anonymous();
    private VerifiedWebIdentity? originalIdentity;
    private byte[]? originalFingerprint;
    private string? credential;
    private CancellationTokenSource? activeCheck;
    private Task? poll;
    private bool initialized;
    private bool reloadRequired;
    private bool disposed;
    private int version;

    public bool RequiresFullReload
    {
        get { lock (gate) return reloadRequired; }
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        lock (gate) return Task.FromResult(current);
    }

    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        // ASP.NET Core .NET 8 supplies a completed task for prerender, circuit start and reconnect.
        var source = authenticationStateTask.IsCompletedSuccessfully
            ? authenticationStateTask.Result.User as DevAuthIdentityHandler.RequestPrincipal : null;
        var cookie = source?.TakeCredential();
        lock (gate)
        {
            if (disposed || reloadRequired)
                return;

            if (source is null || cookie is null)
            {
                if (initialized)
                    Invalidate();
                initialized = true;
                return;
            }

            var fingerprint = SHA256.HashData(Encoding.ASCII.GetBytes(cookie));
            if (initialized && (originalIdentity != source.Account || originalFingerprint is null ||
                                !CryptographicOperations.FixedTimeEquals(originalFingerprint, fingerprint)))
            {
                Invalidate();
                return;
            }

            initialized = true;
            originalIdentity = source.Account;
            originalFingerprint = fingerprint;
            credential = cookie;
            version++;
            activeCheck?.Cancel();
            Publish(new AuthenticationState(new ClaimsPrincipal(DevAuthIdentityHandler.CreateIdentity(source.Account))));
            poll ??= PollAsync(lifetime.Token);
        }
    }

    internal void SuspendConnection()
    {
        lock (gate)
        {
            if (disposed || reloadRequired || credential is null)
                return;

            // Reconnect must provide a new server-validated handoff matching the original session.
            credential = null;
            version++;
            activeCheck?.Cancel();
            Publish(Anonymous());
        }
    }

    private async Task PollAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(RevalidationInterval, clock, stopping);
                string? cookie;
                int checkVersion;
                CancellationTokenSource check;
                lock (gate)
                {
                    if (disposed || reloadRequired)
                        return;
                    cookie = credential;
                    if (cookie is null)
                        continue;
                    checkVersion = version;
                    check = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    activeCheck = check;
                }

                try
                {
                    var verified = await identityClient.VerifyAsync(cookie, check.Token);
                    lock (gate)
                    {
                        if (!disposed && version == checkVersion && verified.Identity != originalIdentity)
                        {
                            Invalidate();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (check.IsCancellationRequested) { }
                finally
                {
                    lock (gate)
                    {
                        activeCheck = null;
                        check.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private void Invalidate()
    {
        reloadRequired = true;
        credential = null;
        originalFingerprint = null;
        originalIdentity = null;
        version++;
        lifetime.Cancel();
        Publish(Anonymous());
    }

    private void Publish(AuthenticationState state)
    {
        current = state;
        NotifyAuthenticationStateChanged(Task.FromResult(state));
    }

    private static AuthenticationState Anonymous() => new(new ClaimsPrincipal(new ClaimsIdentity()));

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            credential = null;
            originalIdentity = null;
            originalFingerprint = null;
            current = Anonymous();
            lifetime.Cancel();
            running = poll;
        }
        if (running is not null)
            await running;
        lifetime.Dispose();
    }
}

internal sealed class DevAuthCircuitHandler(DevAuthStateProvider provider) : CircuitHandler
{
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        provider.SuspendConnection();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        provider.DisposeAsync().AsTask();
}
