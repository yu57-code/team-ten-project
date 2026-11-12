using System.Text.RegularExpressions;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

public sealed partial class MockUserService(MockDataStore store) : IUserService
{
    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$")]
    private static partial Regex UsernamePattern();

    public Task<IReadOnlyList<ManagedUser>> GetUsersAsync()
    {
        lock (store.Sync)
        {
            IReadOnlyList<ManagedUser> list = store.Users
                .OrderBy(u => u.Role)
                .ThenBy(u => u.Username)
                .Select(u => MockDataStore.Clone(u))
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<OperationResult> AddUserAsync(UserDraft draft, string actor)
    {
        var username = draft.Username.Trim();
        if (!UsernamePattern().IsMatch(username))
        {
            return Task.FromResult(OperationResult.Fail("帳號需為 3–32 個英數字，可包含 . _ -"));
        }

        if (string.IsNullOrWhiteSpace(draft.DisplayName))
        {
            return Task.FromResult(OperationResult.Fail("請輸入姓名"));
        }

        lock (store.Sync)
        {
            if (store.Users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(OperationResult.Fail($"帳號 {username} 已存在"));
            }

            store.Users.Add(new ManagedUser
            {
                Username = username,
                DisplayName = draft.DisplayName.Trim(),
                Role = draft.Role,
                MfaBound = false,
                IsActive = true,
            });

            store.AddAudit(actor, AuditKind.RoleChange, "USER_CREATE", null, "—",
                $"新增帳號 {username}（{RoleText(draft.Role)}）。",
                new { }, new { username, display_name = draft.DisplayName.Trim(), role = draft.Role.ToString(), is_active = 1 });

            return Task.FromResult(OperationResult.Ok($"已新增帳號 {username}，首次登入時需綁定 MFA"));
        }
    }

    public Task<OperationResult> UpdateUserAsync(string username, UserDraft draft, string actor)
    {
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
        {
            return Task.FromResult(OperationResult.Fail("請輸入姓名"));
        }

        lock (store.Sync)
        {
            var user = Find(username);
            if (user is null)
            {
                return Task.FromResult(OperationResult.Fail("找不到這個帳號"));
            }

            var before = new { display_name = user.DisplayName, role = user.Role.ToString() };
            user.DisplayName = draft.DisplayName.Trim();
            user.Role = draft.Role;

            store.AddAudit(actor, AuditKind.RoleChange, "ROLE_CHANGE", null, "—",
                $"修改帳號 {user.Username}：姓名「{user.DisplayName}」、角色「{RoleText(user.Role)}」。",
                before, new { display_name = user.DisplayName, role = user.Role.ToString() });

            return Task.FromResult(OperationResult.Ok($"已更新 {user.Username}"));
        }
    }

    public Task<OperationResult> SetActiveAsync(string username, bool isActive, string actor)
    {
        lock (store.Sync)
        {
            var user = Find(username);
            if (user is null)
            {
                return Task.FromResult(OperationResult.Fail("找不到這個帳號"));
            }

            user.IsActive = isActive;
            store.AddAudit(actor, AuditKind.RoleChange, "ROLE_CHANGE", null, "—",
                $"{(isActive ? "啟用" : "停用")}帳號 {user.Username}。",
                new { username = user.Username, is_active = isActive ? 0 : 1 },
                new { username = user.Username, is_active = isActive ? 1 : 0 });

            return Task.FromResult(OperationResult.Ok($"已{(isActive ? "啟用" : "停用")} {user.Username}"));
        }
    }

    public Task<OperationResult> ResetMfaAsync(string username, string actor)
    {
        lock (store.Sync)
        {
            var user = Find(username);
            if (user is null)
            {
                return Task.FromResult(OperationResult.Fail("找不到這個帳號"));
            }

            user.MfaBound = false;
            store.AddAudit(actor, AuditKind.RoleChange, "MFA_RESET", null, "—",
                $"重設帳號 {user.Username} 的 MFA，下次登入需重新綁定。",
                new { mfa_bound = true }, new { mfa_bound = false });

            return Task.FromResult(OperationResult.Ok($"已重設 {user.Username} 的 MFA"));
        }
    }

    private ManagedUser? Find(string username) =>
        store.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

    private static string RoleText(UserRole role) => role == UserRole.Admin ? "系統管理／維護人員" : "醫療／資料審核人員";
}
