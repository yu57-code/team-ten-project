using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>
/// 單一入口登入＋MFA。
/// 目前由 MockAuthService 實作；之後串接 ASP.NET Core Identity / Web API 時，
/// 只要新增另一個實作並在 Program.cs 替換註冊即可，頁面不需修改。
/// </summary>
public interface IAuthService
{
    /// <summary>目前登入的使用者；尚未登入時為 null。</summary>
    UserAccount? CurrentUser { get; }

    /// <summary>登入狀態改變時觸發（側邊選單用來更新使用者資訊）。</summary>
    event Action? CurrentUserChanged;

    /// <summary>第 1 步：驗證帳號密碼。</summary>
    Task<AuthResult> ValidateCredentialsAsync(string username, string password);

    /// <summary>第 2 步：驗證 6 位數 MFA 驗證碼。</summary>
    Task<AuthResult> VerifyMfaAsync(string code);

    /// <summary>
    /// 第 3 步：完成登入。
    /// 原型中由使用者選擇角色；正式版會直接依帳號的角色決定。
    /// </summary>
    Task<UserAccount> CompleteSignInAsync(UserRole role);

    Task SignOutAsync();

    /// <summary>未登入就直接開啟某個頁面時，用來展示的示範使用者。</summary>
    UserAccount GetDemoUser(UserRole role);
}
