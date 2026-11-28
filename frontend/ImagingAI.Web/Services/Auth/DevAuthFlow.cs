using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Api;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace ImagingAI.Web.Services.Auth;

public sealed class DevAuthFlow(ApiAuthService api, DevAuthStateProvider state)
{
    public void EnableBrowserInterop() => api.EnableBrowserInterop();

    public Task<AuthApiResult> LoginAsync(ElementReference form) => api.LoginWithFormAsync(form);
    public Task PrepareOtpAsync(ElementReference form) => api.PrepareOtpAsync(form);
    public Task<AuthApiResult> VerifyOtpAsync(ElementReference form) => api.VerifyOtpAsync(form);
    public Task<AuthApiResult> ResendOtpAsync(ElementReference form) => api.ResendOtpAsync(form);
    public Task<AuthApiResult> MeAsync() => api.MeAsync();
    public Task<AuthApiResult> RecoverLoginAsync() => api.RecoverLoginAsync();

    public async Task<AuthApiResult> SelectRoleAsync(UserRole role)
    {
        var result = await api.SelectRoleAsync(role);
        if (result.Status is AuthApiStatus.NetworkError or AuthApiStatus.ProtocolError or AuthApiStatus.ProxyError or AuthApiStatus.ServiceUnavailable)
        {
            var confirmed = await api.MeAsync();
            if (confirmed.Status == AuthApiStatus.Authenticated && confirmed.Identity?.ActiveRole == role)
                return confirmed;
        }

        return result;
    }

    public async Task<AuthApiResult> LogoutAsync()
    {
        try
        {
            return await api.LogoutAsync();
        }
        finally
        {
            state.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        }
    }

    public static string Error(AuthApiResult result) => result.Status switch
    {
        AuthApiStatus.InvalidCredentials => "帳號或密碼錯誤，或此帳號無法登入。",
        AuthApiStatus.InvalidRequest => "請確認輸入資料後再試。",
        AuthApiStatus.InvalidCsrf => "安全驗證失敗，請重新整理後再試。",
        AuthApiStatus.InvalidOtp => "驗證碼錯誤或驗證已失效。請確認驗證碼，或返回重新登入。",
        AuthApiStatus.VerificationPending => "驗證碼寄送中，請稍後再試。",
        AuthApiStatus.LogoutRequired => "已有登入工作階段，請先登出。",
        AuthApiStatus.RoleNotAllowed or AuthApiStatus.InvalidRoleSelection => "角色選擇已失效或不允許，請重新登入。",
        AuthApiStatus.Unauthorized or AuthApiStatus.InvalidSession => "工作階段已失效，請重新登入。",
        AuthApiStatus.Forbidden => "目前無法授權此操作。",
        AuthApiStatus.RateLimited when result.RetryAfter is { TotalSeconds: > 0 } delay =>
            $"操作過於頻繁，請等待 {Math.Ceiling(delay.TotalSeconds):0} 秒再試。",
        AuthApiStatus.RateLimited => "操作過於頻繁，請稍後再試。",
        AuthApiStatus.NotFound => "目前未開放真實登入。",
        AuthApiStatus.ProxyError => "目前無法連線至登入服務。",
        AuthApiStatus.ServiceUnavailable => "登入服務暫時無法使用。",
        AuthApiStatus.NetworkError => "網路中斷，無法確認操作結果。",
        _ => "無法確認登入服務回應，請稍後再試。"
    };
}
