using System.Data;
using System.Text.Json;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

internal static class AuthServiceTests
{
    private const string Dual = "413570452";
    private const string Admin = "413570438";
    private const string Reviewer = "413570359";
    private const string Disabled = "413570309";
    private const string AdminRole = "SYSTEM_ADMIN";
    private const string ReviewerRole = "MEDICAL_REVIEWER";
    // Synthetic secrets for isolation tests, independent of the database accounts' passwords.
    private const string Password = " Test Password ";
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly string TestHash = Hasher.HashPassword(new object(), Password);

    public static void Register(Action<string, Func<Task>> test)
    {
        test("Auth_dual_role_credentials_verified", () => ExpectRoles(Dual, ReviewerRole, AdminRole));
        test("Auth_admin_credentials_verified", () => ExpectRoles(Admin, AdminRole));
        test("Auth_reviewer_credentials_verified", () => ExpectRoles(Reviewer, ReviewerRole));
        test("Auth_disabled_account_rejected", () => ExpectRejected(Disabled, Account(Disabled)));
        foreach (var username in new[] { Dual, Admin, Reviewer })
        {
            test($"Auth_wrong_password_{username}", () => ExpectRejected(username, Account(username), "wrong"));
        }

        test("Auth_missing_allowed_account_rejected", () => ExpectRejected(Dual, null));
        test("Auth_dev_uploader_is_excluded_without_lookup", () => ExpectExcluded("dev_uploader"));
        test("Auth_unapproved_account_is_excluded_without_lookup", () => ExpectExcluded("outside-list"));
        test("Auth_production_is_excluded_without_lookup", () => ExpectExcluded(Admin, "Production"));
        test("Auth_blank_username_rejected", () => ExpectInvalid("  ", Password));
        test("Auth_blank_password_rejected", () => ExpectInvalid(Admin, " \t"));
        test("Auth_oversized_username_rejected", () => ExpectInvalid(new string('a', 101), Password));
        test("Auth_oversized_password_rejected", () => ExpectInvalid(Admin, new string('a', 257)));
        test("Auth_unknown_primary_role_rejected",
            () => ExpectRejected(Admin, Account(Admin) with { PrimaryRoleCode = "system_admin" }));
        test("Auth_unknown_secondary_role_rejected",
            () => ExpectRejected(Dual, Account(Dual) with { SecondaryRoleCode = "Admin" }));
        test("Auth_missing_primary_role_rejected",
            () => ExpectRejected(Admin, Account(Admin) with { PrimaryRoleCode = null }));
        test("Auth_missing_referenced_secondary_role_rejected",
            () => ExpectRejected(Dual, Account(Dual) with { SecondaryRoleCode = null }));
        test("Auth_duplicate_roles_rejected",
            () => ExpectRejected(Dual, Account(Dual) with { SecondaryRoleCode = ReviewerRole }));
        test("Auth_corrupted_hash_rejected",
            () => ExpectRejected(Admin, Account(Admin) with { PasswordHash = "not base64!" }));
        test("Auth_missing_hash_rejected",
            () => ExpectRejected(Admin, Account(Admin) with { PasswordHash = null }));
        test("Auth_rehash_needed_succeeds_without_write", RehashMetadataOnly);
        test("Auth_store_failure_is_sanitized", StoreFailure);
        test("Auth_result_contains_no_hash_or_session", SafeResult);
        test("Auth_password_is_not_trimmed_or_case_folded", PasswordIsExact);
        test("Auth_request_rejects_user_id_and_role", RequestRejectsIdentity);
        test("Auth_Npgsql_query_is_minimal_and_offline", QueryShape);
    }

    private static AuthService.AccountCredentials Account(string username) =>
        new(42, username, TestHash, username != Disabled, username == Admin ? AdminRole : ReviewerRole,
            username == Dual, username == Dual ? AdminRole : null);

    private static AuthService Service(Func<string, CancellationToken, Task<AuthService.AccountCredentials?>> read,
        string environment = "Development", ILogger<AuthService>? logger = null) =>
        new(read, new TestEnvironment(Path.GetTempPath()) { EnvironmentName = environment },
            logger ?? NullLogger<AuthService>.Instance);

    private static Task<CredentialVerificationResult> Verify(AuthService service, string username,
        string password = Password) =>
        service.VerifyCredentialsAsync(new LoginRequest { Username = username, Password = password });

    private static void Check(bool pass)
    {
        if (!pass) throw new InvalidOperationException("Auth test assertion failed.");
    }

    private static async Task ExpectRoles(string username, params string[] roles)
    {
        var result = await Verify(Service((name, _) =>
        {
            Check(name == username);
            return Task.FromResult<AuthService.AccountCredentials?>(Account(name));
        }), username);
        Check(result.Status == CredentialVerificationStatus.CredentialsVerified);
        Check(result.Account is { UserId: 42 } account && account.Username == username &&
              account.RoleCodes.SequenceEqual(roles) && account.RequiresRoleSelection == (roles.Length > 1));
    }

    private static async Task ExpectRejected(string username, AuthService.AccountCredentials? account,
        string password = Password)
    {
        var result = await Verify(Service((_, _) => Task.FromResult(account)), username, password);
        Check(result is { Status: CredentialVerificationStatus.InvalidCredentials, Account: null });
    }

    private static async Task ExpectExcluded(string username, string environment = "Development")
    {
        var result = await Verify(Service((_, _) => throw new InvalidOperationException("Unexpected lookup."),
            environment), username);
        Check(result is { Status: CredentialVerificationStatus.InvalidCredentials, Account: null });
    }

    private static async Task ExpectInvalid(string username, string password)
    {
        var result = await Verify(Service((_, _) => throw new InvalidOperationException("Unexpected lookup.")),
            username, password);
        Check(result is { Status: CredentialVerificationStatus.InvalidRequest, Account: null });
    }

    private static async Task RehashMetadataOnly()
    {
        var legacy = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2
        }));
        var hash = legacy.HashPassword(new object(), Password);
        Check(Hasher.VerifyHashedPassword(new object(), hash, Password) ==
              PasswordVerificationResult.SuccessRehashNeeded);
        var account = Account(Admin) with { PasswordHash = hash };
        var logger = new CaptureLogger();
        var reads = 0;
        var result = await Verify(Service((_, _) =>
        {
            reads++;
            return Task.FromResult<AuthService.AccountCredentials?>(account);
        }, logger: logger), Admin);
        Check(result.Status == CredentialVerificationStatus.CredentialsVerified && reads == 1 &&
              account.PasswordHash == hash);
        Check(logger.Events.Count == 1 && logger.Events[0].Id == 3202 &&
              logger.Events[0].Exception is null && !logger.Events[0].Message.Contains(hash) &&
              !logger.Events[0].Message.Contains(Password));
    }

    private static async Task StoreFailure()
    {
        const string sensitive = "sensitive simulated database details";
        var logger = new CaptureLogger();
        var result = await Verify(Service((_, _) => Task.FromException<AuthService.AccountCredentials?>(
            new InvalidOperationException("Provider retry wrapper", new NpgsqlException(sensitive))),
            logger: logger), Admin);
        Check(result is { Status: CredentialVerificationStatus.AccountStoreUnavailable, Account: null });
        Check(logger.Events.Count == 1 && logger.Events[0].Id == 3201 &&
              logger.Events[0].Exception is null && !logger.Events[0].Message.Contains(sensitive));
    }

    private static async Task SafeResult()
    {
        var result = await Verify(Service((_, _) =>
            Task.FromResult<AuthService.AccountCredentials?>(Account(Dual))), Dual);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var root = json.RootElement;
        Check(root.EnumerateObject().Select(p => p.Name)
            .SequenceEqual(new[] { "Status", "Account" }));
        Check(root.GetProperty("Account").EnumerateObject().Select(p => p.Name)
            .SequenceEqual(new[] { "UserId", "Username", "RoleCodes", "RequiresRoleSelection" }));
        Check(result.Status == CredentialVerificationStatus.CredentialsVerified);
    }

    private static async Task PasswordIsExact()
    {
        await ExpectRoles(Admin, AdminRole);
        await ExpectRejected(Admin, Account(Admin), Password.Trim());
        await ExpectRejected(Admin, Account(Admin), Password.ToLowerInvariant());
    }

    private static Task RequestRejectsIdentity()
    {
        foreach (var field in new[] { "userId", "roleCode" })
        {
            try
            {
                _ = JsonSerializer.Deserialize<LoginRequest>(
                    $$"""{"username":"example","password":"example","{{field}}":null}""",
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                continue;
            }
            throw new InvalidOperationException("Unexpected identity field accepted.");
        }
        return Task.CompletedTask;
    }

    private static Task QueryShape()
    {
        using var db = new ImagingAiDbContext(new DbContextOptionsBuilder<ImagingAiDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline_model;Username=offline_model").Options);
        var sql = AuthService.CredentialsQuery(db, Admin).ToQueryString();
        Check(sql.Contains("LEFT JOIN system.role", StringComparison.Ordinal) &&
              sql.Contains("password_hash", StringComparison.Ordinal) &&
              sql.Contains("WHERE", StringComparison.Ordinal) &&
              !sql.Contains("created_at", StringComparison.Ordinal) &&
              !sql.Contains("role_name", StringComparison.Ordinal) &&
              db.Database.GetDbConnection().State == ConnectionState.Closed);
        return Task.CompletedTask;
    }

    private sealed class CaptureLogger : ILogger<AuthService>
    {
        public List<(int Id, string Message, Exception? Exception)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Events.Add((eventId.Id, formatter(state, exception), exception));
    }
}
