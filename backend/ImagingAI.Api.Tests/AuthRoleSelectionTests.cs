using System.Data;
using System.Reflection;
using System.Text.Json;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

internal static class AuthRoleSelectionTests
{
    private const string Dual = "413570452";
    private const string Admin = "413570438";
    private const string Reviewer = "413570359";
    private const string Disabled = "413570309";
    private const string AdminRole = "SYSTEM_ADMIN";
    private const string ReviewerRole = "MEDICAL_REVIEWER";
    private const string Password = "Synthetic role selection test password";
    private static readonly string Hash = new PasswordHasher<object>().HashPassword(new object(), Password);

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Role_admin_auto_confirms", () => AutoConfirm(Admin, AdminRole));
        test("Role_reviewer_auto_confirms", () => AutoConfirm(Reviewer, ReviewerRole));
        test("Role_dual_requires_selection_without_active_role", DualRequiresSelection);
        test("Role_dual_can_choose_reviewer", () => SelectRole(ReviewerRole));
        test("Role_dual_can_choose_admin", () => SelectRole(AdminRole));
        test("Role_single_cannot_select_other_role", SingleCannotSelect);
        test("Role_unknown_role_revokes_challenge", () => InvalidRole("UNKNOWN"));
        test("Role_case_mismatch_revokes_challenge", () => InvalidRole("system_admin"));
        test("Role_wrong_credentials_cannot_create_pending_state", () => RejectStart(Dual, "wrong"));
        test("Role_forged_credentials_result_cannot_create_state", ForgedCredentials);
        test("Role_unknown_pending_token_is_rejected", UnknownToken);
        test("Role_tampered_pending_token_is_rejected", TamperedToken);
        test("Role_pending_works_just_before_deadline", () => TestDeadline(TimeSpan.FromMilliseconds(119999), true));
        test("Role_pending_rejected_exactly_at_deadline", () => TestDeadline(TimeSpan.FromSeconds(120), false));
        test("Role_pending_rejected_after_deadline", () => TestDeadline(TimeSpan.FromSeconds(121), false));
        test("Role_pending_expiring_during_lookup_cannot_succeed", ExpiresDuringLookup);
        test("Role_success_cannot_be_replayed", Replay);
        test("Role_concurrent_lookup_allows_one_confirmation", ConcurrentSelection);
        test("Role_cancelled_token_cannot_be_used", Cancelled);
        test("Role_expired_entries_free_capacity", ExpiredEntriesFreeCapacity);
        test("Role_store_capacity_fails_closed", Capacity);
        test("Role_two_users_keep_distinct_pending_identities", DistinctUsers);
        test("Role_account_disabled_after_password_is_rejected", () => RejectChanged(r => r with { IsActive = false }));
        test("Role_account_deleted_after_password_is_rejected", () => RejectChanged(_ => null));
        test("Role_removed_after_password_is_rejected", () => RejectChanged(r =>
            r with { HasSecondaryRole = false, SecondaryRoleCode = null }));
        test("Role_invalid_current_role_set_is_rejected", () => RejectChanged(r =>
            r with { SecondaryRoleCode = ReviewerRole }));
        test("Role_replaced_username_is_rejected", () => RejectChanged(r => r with { Username = "other" }));
        test("Role_added_after_password_does_not_expand_grant", AddedRole);
        test("Role_database_failure_revokes_pending_without_details", DatabaseFailure);
        test("Role_disabled_account_cannot_create_pending_state", () => RejectStart(Disabled));
        test("Role_dev_uploader_cannot_create_pending_state", () => RejectStart("dev_uploader"));
        test("Role_production_cannot_start_or_confirm", Production);
        test("Role_pending_snapshot_is_immutable_and_has_no_credentials", SafePendingSnapshot);
        test("Role_confirmed_result_has_no_authentication_artifacts", SafeConfirmedResult);
        test("Role_Npgsql_revalidation_query_is_read_only_and_omits_hash", QueryShape);
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Role selection test assertion failed.");
    }

    private static async Task AutoConfirm(string username, string role)
    {
        var fixture = new Fixture();
        var lookups = 0;
        fixture.ReadRoles = (id, _) =>
        {
            lookups++;
            return Task.FromResult<AuthService.AccountRoles?>(Roles(Credentials(username)));
        };
        var result = await fixture.Start(username);
        Check(lookups == 1 && result is { Status: RoleConfirmationStatus.RoleConfirmed, PendingRoleToken: null } &&
              result.ActiveRoleCode == role);
    }

    private static async Task DualRequiresSelection()
    {
        var fixture = new Fixture();
        var result = await fixture.Start();
        Check(result is { Status: RoleConfirmationStatus.RoleSelectionRequired, ActiveRoleCode: null } &&
              result.AvailableRoleCodes.SequenceEqual(new[] { ReviewerRole, AdminRole }) &&
              result.PendingRoleToken is { Length: 64 });
        Check(!JsonSerializer.Serialize(result).Contains(result.PendingRoleToken!, StringComparison.Ordinal));
    }

    private static async Task SelectRole(string role)
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        var result = await fixture.Service().ConfirmSelectedRoleAsync(token, role);
        Check(result is { Status: RoleConfirmationStatus.RoleConfirmed, PendingRoleToken: null } &&
              result.ActiveRoleCode == role && result.AvailableRoleCodes.Count == 0);
    }

    private static async Task SingleCannotSelect()
    {
        var fixture = new Fixture();
        var result = await fixture.Start(Admin);
        Check(result.PendingRoleToken is null);
        await ExpectInvalid(fixture, result.PendingRoleToken, ReviewerRole);
    }

    private static async Task InvalidRole(string role)
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        await ExpectInvalid(fixture, token, role);
        await ExpectInvalid(fixture, token, AdminRole);
    }

    private static async Task RejectStart(string username, string password = Password)
    {
        var fixture = new Fixture(capacity: 1);
        var result = await fixture.Start(username, password);
        Check(result is { Status: RoleConfirmationStatus.InvalidCredentials, PendingRoleToken: null });
        _ = await fixture.Challenge();
    }

    private static async Task ForgedCredentials()
    {
        var fixture = new Fixture();
        var forged = new CredentialVerificationResult(CredentialVerificationStatus.CredentialsVerified,
            new VerifiedAccount(1, Dual, new[] { ReviewerRole, AdminRole }));
        Check(forged.Account is not null);
        Check(!typeof(AuthService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetParameters())
            .Any(p => p.ParameterType == typeof(CredentialVerificationResult) ||
                      p.ParameterType == typeof(VerifiedAccount)));
        Check(typeof(AuthSessionStore).GetMethod("TryCreate") is null);
        await ExpectInvalid(fixture, null, AdminRole);
    }

    private static Task UnknownToken() => ExpectInvalid(new Fixture(), new string('A', 64), AdminRole);

    private static async Task TamperedToken()
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        var tampered = (token[0] == 'A' ? "B" : "A") + token[1..];
        await ExpectInvalid(fixture, tampered, AdminRole);
        Check((await fixture.Service().ConfirmSelectedRoleAsync(token, AdminRole)).ActiveRoleCode == AdminRole);
    }

    private static async Task TestDeadline(TimeSpan delay, bool valid)
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        fixture.Clock.Advance(delay);
        var result = await fixture.Service().ConfirmSelectedRoleAsync(token, ReviewerRole);
        Check(result.Status == (valid ? RoleConfirmationStatus.RoleConfirmed : RoleConfirmationStatus.InvalidSelection));
    }

    private static async Task ExpiresDuringLookup()
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        fixture.ReadRoles = (_, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(120));
            return Task.FromResult<AuthService.AccountRoles?>(Roles(Credentials(Dual)));
        };
        await ExpectInvalid(fixture, token, ReviewerRole);
    }

    private static async Task Replay()
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        Check((await fixture.Service().ConfirmSelectedRoleAsync(token, ReviewerRole)).ActiveRoleCode == ReviewerRole);
        await ExpectInvalid(fixture, token, ReviewerRole);
    }

    private static async Task ConcurrentSelection()
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        var bothReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;
        fixture.ReadRoles = async (_, ct) =>
        {
            if (Interlocked.Increment(ref readers) == 2)
                bothReading.SetResult();
            await release.Task.WaitAsync(ct);
            return Roles(Credentials(Dual));
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = Task.Run(() => fixture.Service().ConfirmSelectedRoleAsync(token, ReviewerRole, timeout.Token));
        var second = Task.Run(() => fixture.Service().ConfirmSelectedRoleAsync(token, AdminRole, timeout.Token));
        await bothReading.Task.WaitAsync(timeout.Token);
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Check(readers == 2 && results.Count(r => r.Status == RoleConfirmationStatus.RoleConfirmed) == 1 &&
              results.Count(r => r.Status == RoleConfirmationStatus.InvalidSelection) == 1);
    }

    private static async Task Cancelled()
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        Check(fixture.Service().CancelPendingRoleSelection(token));
        await ExpectInvalid(fixture, token, ReviewerRole);
    }

    private static async Task ExpiredEntriesFreeCapacity()
    {
        var fixture = new Fixture(capacity: 1);
        var stale = await fixture.Challenge();
        fixture.Clock.Advance(TimeSpan.FromSeconds(120));
        var fresh = await fixture.Challenge();
        Check(stale != fresh);
        await ExpectInvalid(fixture, stale, ReviewerRole);
        Check((await fixture.Service().ConfirmSelectedRoleAsync(fresh, ReviewerRole)).ActiveRoleCode == ReviewerRole);
    }

    private static async Task Capacity()
    {
        var fixture = new Fixture(capacity: 1);
        var first = await fixture.Challenge();
        Check(await fixture.Start() is { Status: RoleConfirmationStatus.PendingSelectionUnavailable, PendingRoleToken: null });
        Check((await fixture.Service().ConfirmSelectedRoleAsync(first, AdminRole)).ActiveRoleCode == AdminRole);
    }

    private static async Task DistinctUsers()
    {
        var fixture = new Fixture();
        var firstAccount = Credentials(Dual);
        var secondAccount = Credentials(Admin) with { HasSecondaryRole = true, SecondaryRoleCode = ReviewerRole };
        fixture.ReadCredentials = (name, _) => Task.FromResult<AuthService.AccountCredentials?>(
            name == Dual ? firstAccount : secondAccount);
        fixture.ReadRoles = (id, _) => Task.FromResult<AuthService.AccountRoles?>(
            Roles(id == firstAccount.UserId ? firstAccount : secondAccount));
        var first = await fixture.Challenge();
        var second = await fixture.Challenge(Admin);
        var firstResult = await fixture.Service().ConfirmSelectedRoleAsync(first, ReviewerRole);
        var secondResult = await fixture.Service().ConfirmSelectedRoleAsync(second, AdminRole);
        Check(first != second && firstResult.UserId == firstAccount.UserId && firstResult.Username == Dual &&
              secondResult.UserId == secondAccount.UserId && secondResult.Username == Admin);
    }

    private static async Task RejectChanged(Func<AuthService.AccountRoles, AuthService.AccountRoles?> change)
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        fixture.ReadRoles = (_, _) => Task.FromResult(change(Roles(Credentials(Dual))));
        await ExpectInvalid(fixture, token, AdminRole);
        fixture.ReadRoles = (_, _) => Task.FromResult<AuthService.AccountRoles?>(Roles(Credentials(Dual)));
        await ExpectInvalid(fixture, token, AdminRole);
    }

    private static async Task AddedRole()
    {
        var fixture = new Fixture();
        fixture.ReadRoles = (_, _) => Task.FromResult<AuthService.AccountRoles?>(
            Roles(Credentials(Admin)) with { HasSecondaryRole = true, SecondaryRoleCode = ReviewerRole });
        var result = await fixture.Start(Admin);
        Check(result is { Status: RoleConfirmationStatus.RoleConfirmed, PendingRoleToken: null } &&
              result.ActiveRoleCode == AdminRole);
        await ExpectInvalid(fixture, result.PendingRoleToken, ReviewerRole);
    }

    private static async Task DatabaseFailure()
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        fixture.ReadRoles = (_, _) => throw new NpgsqlException("simulated private detail");
        var result = await fixture.Service().ConfirmSelectedRoleAsync(token, AdminRole);
        Check(result is { Status: RoleConfirmationStatus.AccountStoreUnavailable, UserId: null, ActiveRoleCode: null } &&
              !JsonSerializer.Serialize(result).Contains("private detail", StringComparison.Ordinal));
        await ExpectInvalid(fixture, token, AdminRole);
    }

    private static async Task Production()
    {
        var fixture = new Fixture();
        var token = await fixture.Challenge();
        fixture.Environment.EnvironmentName = "Production";
        Check(await fixture.Start() is { Status: RoleConfirmationStatus.InvalidCredentials, PendingRoleToken: null });
        await ExpectInvalid(fixture, token, AdminRole);
    }

    private static Task SafePendingSnapshot()
    {
        var fixture = new Fixture();
        var original = new[] { ReviewerRole, AdminRole };
        var token = fixture.Store.TryCreate(new VerifiedAccount(42, Dual, original))!;
        var state = fixture.Store.Find(token)!;
        original[0] = "CHANGED";
        Check(state.Account.RoleCodes[0] == ReviewerRole && state.Account.RoleCodes is IList<string> { IsReadOnly: true } &&
              state.ExpiresAt - state.CreatedAt == TimeSpan.FromSeconds(120) &&
              state.Stage == RoleConfirmationStatus.RoleSelectionRequired);
        var json = JsonSerializer.Serialize(state);
        Check(!json.Contains("password", StringComparison.OrdinalIgnoreCase) &&
              !json.Contains(Hash, StringComparison.Ordinal) && !json.Contains(token, StringComparison.Ordinal));
        Check(new Fixture().Store.Find(token) is null);
        return Task.CompletedTask;
    }

    private static async Task SafeConfirmedResult()
    {
        var fixture = new Fixture();
        var result = await fixture.Service().ConfirmSelectedRoleAsync(await fixture.Challenge(), AdminRole);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Check(result.Status == RoleConfirmationStatus.RoleConfirmed && result.AvailableRoleCodes.Count == 0 &&
              json.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(
                  new[] { "Status", "UserId", "Username", "AvailableRoleCodes", "ActiveRoleCode" }));
    }

    private static Task QueryShape()
    {
        using var db = new ImagingAiDbContext(new DbContextOptionsBuilder<ImagingAiDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline_model;Username=offline_model").Options);
        var sql = AuthService.RolesQuery(db, 42).ToQueryString();
        Check(sql.Contains("LEFT JOIN system.role", StringComparison.Ordinal) &&
              sql.Contains("WHERE", StringComparison.Ordinal) && sql.Contains("user_id", StringComparison.Ordinal) &&
              sql.Contains("is_active", StringComparison.Ordinal) && sql.Contains("role_code", StringComparison.Ordinal) &&
              !sql.Contains("password_hash", StringComparison.Ordinal) &&
              !sql.Contains("created_at", StringComparison.Ordinal) &&
              db.Database.GetDbConnection().State == ConnectionState.Closed);
        return Task.CompletedTask;
    }

    private static async Task ExpectInvalid(Fixture fixture, string? token, string role)
    {
        var result = await fixture.Service().ConfirmSelectedRoleAsync(token, role);
        Check(result is { Status: RoleConfirmationStatus.InvalidSelection, ActiveRoleCode: null });
    }

    private static AuthService.AccountCredentials Credentials(string username) =>
        new(long.Parse(username), username, Hash, username != Disabled, username == Admin ? AdminRole : ReviewerRole,
            username == Dual, username == Dual ? AdminRole : null);

    private static AuthService.AccountRoles Roles(AuthService.AccountCredentials account) =>
        new(account.UserId, account.Username, account.IsActive, account.PrimaryRoleCode,
            account.HasSecondaryRole, account.SecondaryRoleCode);

    private sealed class Fixture
    {
        public ManualClock Clock { get; } = new();
        public AuthSessionStore Store { get; }
        public TestEnvironment Environment { get; } = new(Path.GetTempPath());
        public Func<string, CancellationToken, Task<AuthService.AccountCredentials?>> ReadCredentials { get; set; } =
            (username, _) => Task.FromResult<AuthService.AccountCredentials?>(Credentials(username));
        public Func<long, CancellationToken, Task<AuthService.AccountRoles?>> ReadRoles { get; set; } =
            (id, _) => Task.FromResult<AuthService.AccountRoles?>(Roles(Credentials(id.ToString())));

        public Fixture(int capacity = 16) => Store = new(Clock, capacity);

        public AuthService Service() => new(ReadCredentials, Environment, NullLogger<AuthService>.Instance, ReadRoles, Store);

        public Task<RoleConfirmationResult> Start(string username = Dual, string password = Password) =>
            Service().BeginRoleConfirmationAsync(new LoginRequest { Username = username, Password = password });

        public async Task<string> Challenge(string username = Dual)
        {
            var result = await Start(username);
            Check(result.Status == RoleConfirmationStatus.RoleSelectionRequired && result.PendingRoleToken is not null);
            return result.PendingRoleToken!;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan delay) => ticks += delay.Ticks;
    }
}
