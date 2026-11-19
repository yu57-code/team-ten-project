using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

/// <summary>
/// 模擬登入：任何非空白的帳號密碼都能通過，驗證碼只要是 6 位數字即可。
/// - 第一次登入的帳號會自動建立在「使用者與角色權限」清單中（沒有預設的假帳號）。
/// - 被管理員停用的帳號無法登入。
/// - 每次登入都會寫入稽核軌跡（LOGIN_MFA）。
/// 以 Scoped 註冊，在 Interactive Server 模式下每個瀏覽器分頁（circuit）各自保存登入狀態。
/// </summary>
public sealed class MockAuthService(MockDataStore store) : IAuthService
{
    private string? _pendingUsername;

    public UserAccount? CurrentUser { get; private set; }

    public event Action? CurrentUserChanged;

    public async Task<AuthResult> ValidateCredentialsAsync(string username, string password)
    {
        await Task.Delay(300); // 模擬網路延遲

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return AuthResult.Fail("請輸入帳號與密碼。");
        }

        lock (store.Sync)
        {
            var existing = store.Users.FirstOrDefault(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is { IsActive: false })
            {
                return AuthResult.Fail("此帳號已被停用，請聯絡系統管理人員。");
            }
        }

        _pendingUsername = username.Trim();
        return AuthResult.Success();
    }

    public async Task<AuthResult> VerifyMfaAsync(string code)
    {
        await Task.Delay(300);

        if (_pendingUsername is null)
        {
            return AuthResult.Fail("請先輸入帳號與密碼。");
        }

        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            return AuthResult.Fail("請輸入 6 位數驗證碼。");
        }

        return AuthResult.Success();
    }

    public Task<UserAccount> CompleteSignInAsync(UserRole role)
    {
        var username = _pendingUsername ?? "user";

        lock (store.Sync)
        {
            var user = store.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                // 第一次登入：建立帳號（原型中角色由登入頁選擇）
                user = new ManagedUser { Username = username, DisplayName = username, Role = role };
                store.Users.Add(user);
            }

            user.MfaBound = true;
            user.LastLoginAt = store.Clock();

            CurrentUser = new UserAccount(user.Username, user.DisplayName, role,
                user.DisplayName[..1].ToUpperInvariant(), MfaEnabled: true);

            store.AddAudit(user.DisplayName, AuditKind.Login, "LOGIN_MFA", null, "—",
                "單一入口登入，MFA 驗證成功。", new { }, new { username = user.Username, role = role.ToString(), mfa = "SUCCESS" });
        }

        _pendingUsername = null;
        CurrentUserChanged?.Invoke();
        return Task.FromResult(CurrentUser);
    }

    public Task SignOutAsync()
    {
        CurrentUser = null;
        _pendingUsername = null;
        CurrentUserChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>未登入就直接開啟頁面（例如重新整理）時的顯示用身分。</summary>
    public UserAccount GetDemoUser(UserRole role) => new("guest", "未登入", role, "?", MfaEnabled: false);
}
