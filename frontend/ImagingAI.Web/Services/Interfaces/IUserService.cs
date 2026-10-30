using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>使用者與角色權限管理（所有變更都會寫入稽核軌跡）。</summary>
public interface IUserService
{
    /// <summary>所有帳號（回傳複本）。</summary>
    Task<IReadOnlyList<ManagedUser>> GetUsersAsync();

    Task<OperationResult> AddUserAsync(UserDraft draft, string actor);

    Task<OperationResult> UpdateUserAsync(string username, UserDraft draft, string actor);

    Task<OperationResult> SetActiveAsync(string username, bool isActive, string actor);

    /// <summary>重設 MFA：使用者下次登入需重新綁定驗證器。</summary>
    Task<OperationResult> ResetMfaAsync(string username, string actor);
}
