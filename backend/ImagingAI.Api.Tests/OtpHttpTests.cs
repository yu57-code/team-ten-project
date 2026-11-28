using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ImagingAI.Api.Auth;
using ImagingAI.Api.Controllers;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static ImagingAI.Api.Auth.AuthRegistration;

internal static class OtpHttpTests
{
    internal static async Task Run()
    {
        const string name = "413570438";
        const string password = "isolated test password";
        const string role = "SYSTEM_ADMIN";
        var id = long.Parse(name);
        var hash = new PasswordHasher<object>().HashPassword(new object(), password);
        var credentials = new AuthService.AccountCredentials(id, name, hash, true, role, false, null);
        var sender = new TestOtpSender();
        using var log = new CapturedLog();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(15));
        // Windows Schannel needs the normal transient PFX import to use this test key.
        var pfx = issued.Export(X509ContentType.Pfx);
        using var certificate = new X509Certificate2(pfx);
        CryptographicOperations.ZeroMemory(pfx);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        var mvc = builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        mvc.ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new AuthOnly()));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:EnableDevelopmentLogin"] = "true" }).Build();
        builder.Services.AddSingleton(new AuthProtection(new EphemeralDataProtectionProvider()));
        Check(AddDevelopmentAuth(mvc, builder.Environment, configuration));
        builder.Services.AddSingleton<IEmailOtpSender>(sender);
        builder.Services.RemoveAll<AuthService>();
        builder.Services.RemoveAll<EmailOtpService>();
        builder.Services.AddScoped(services => new AuthService((_, _) => Task.FromResult<AuthService.AccountCredentials?>(credentials),
            builder.Environment, services.GetRequiredService<ILogger<AuthService>>(),
            (_, _) => Task.FromResult<AuthService.AccountRoles?>(new(id, name, true, role, false, null)),
            services.GetRequiredService<AuthSessionStore>()));
        builder.Services.AddScoped(services => new EmailOtpService(
            (_, _) => Task.FromResult<EmailOtpAccount?>(TestOtpSender.Binding(credentials)),
            services.GetRequiredService<EmailOtpChallengeStore>(), sender, TimeProvider.System, services.GetRequiredService<EmailOtpOptions>()));
        await using var app = builder.Build();
        app.UseDevelopmentAuthBoundary(true);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/protected-probe", () => Results.NoContent())
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = Scheme });
        await app.StartAsync();
        using var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            // Trust only this ephemeral test certificate, never an arbitrary certificate.
            ServerCertificateCustomValidationCallback = (_, peer, _, _) =>
                peer is not null && CryptographicOperations.FixedTimeEquals(certificate.RawData, peer.RawData)
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };

        async Task<string> Csrf()
        {
            using var reply = await client.GetAsync("/api/auth/csrf");
            Check(reply.IsSuccessStatusCode && reply.Headers.CacheControl?.NoStore == true);
            using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("requestToken").GetString()!;
        }

        async Task<HttpResponseMessage> Post(string path, string csrf, object? value = null)
        {
            using var call = new HttpRequestMessage(HttpMethod.Post, "/api/auth/" + path);
            call.Headers.Add(CsrfHeaderName, csrf);
            if (value is not null) call.Content = JsonContent.Create(value);
            return await client.SendAsync(call);
        }

        var anonymous = await Csrf();
        using (var login = await Post("login", anonymous, new { username = name, password }))
        {
            Check(login.StatusCode == HttpStatusCode.OK && login.Headers.CacheControl?.NoStore == true);
            using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            Check(json.RootElement.GetProperty("state").GetString() == "otpRequired" &&
                  json.RootElement.EnumerateObject().Count() == 3);
        }
        var cookies = handler.CookieContainer.GetCookies(client.BaseAddress);
        Check(cookies[OtpCookieName]?.HttpOnly == true && cookies[OtpCookieName]?.Secure == true &&
              cookies[CookieName] is null && cookies[PendingCookieName] is null);
        var otpCookie = cookies[OtpCookieName]!.Value;
        using (var me = await client.GetAsync("/api/auth/me")) Check(me.StatusCode == HttpStatusCode.Unauthorized);
        using (var business = await client.GetAsync("/protected-probe"))
            Check(business.StatusCode == HttpStatusCode.Unauthorized && business.Headers.Location is null);
        using (var oldToken = await Post("otp/verify", anonymous, new { code = sender.Code }))
            Check(oldToken.StatusCode == HttpStatusCode.BadRequest);
        var pending = await Csrf();
        using (var forbidden = await Post("select-role", pending, new { roleCode = role }))
            Check(forbidden.StatusCode == HttpStatusCode.Unauthorized);
        using (var resend = await Post("otp/resend", pending))
            Check((int)resend.StatusCode == 429 && resend.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        using (var malformed = await Post("otp/verify", pending, new { code = sender.Code, userId = id }))
            Check(malformed.StatusCode == HttpStatusCode.BadRequest);
        using (var verify = await Post("otp/verify", pending, new { code = sender.Code }))
        {
            Check(verify.StatusCode == HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
            Check(json.RootElement.GetProperty("state").GetString() == "authenticated" &&
                  json.RootElement.GetProperty("user").GetProperty("activeRoleCode").GetString() == role);
        }
        using (var business = await client.GetAsync("/protected-probe")) Check(business.StatusCode == HttpStatusCode.NoContent);
        using (var logout = await Post("logout", await Csrf())) Check(logout.StatusCode == HttpStatusCode.NoContent);
        using (var me = await client.GetAsync("/api/auth/me")) Check(me.StatusCode == HttpStatusCode.Unauthorized);
        await app.StopAsync();
        var email = TestOtpSender.Binding(credentials).Email!;
        Check(!log.Entries.Any(entry => entry.Message.Contains(password, StringComparison.Ordinal) ||
              entry.Message.Contains(otpCookie, StringComparison.Ordinal) ||
              entry.Message.Contains(email, StringComparison.Ordinal) ||
              (entry.Category == typeof(AuthController).FullName &&
               entry.Message.Contains(sender.Code, StringComparison.Ordinal))));
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Isolated HTTPS OTP assertion failed.");
    }

    private sealed class AuthOnly : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.Where(c => c.AsType() != typeof(AuthController)).ToArray())
                feature.Controllers.Remove(controller);
        }
    }

    private sealed class CapturedLog : ILoggerProvider
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<(string Category, string Message)> Entries = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, this);
        public void Dispose() { }

        private sealed class Capture(string category, CapturedLog owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((category, formatter(state, exception)));
        }
    }
}
