using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>
/// 上傳病例並追蹤前 5 步處理進度。
/// Mock 版以背景工作模擬進度；正式版可改由 Web API 建立任務、SignalR 推送進度，
/// 只要同樣觸發 JobChanged 事件，頁面不需修改。
/// </summary>
public interface IUploadService
{
    /// <summary>處理任務狀態改變時觸發（可能在背景執行緒，頁面需用 InvokeAsync 更新畫面）。</summary>
    event Action<UploadJob>? JobChanged;

    /// <summary>指定使用者最近的上傳（新到舊）。</summary>
    Task<IReadOnlyList<UploadJob>> GetRecentJobsAsync(string uploadedBy);

    /// <summary>上傳檔案並為每個檔案建立一筆處理任務（CaseID）。</summary>
    Task<IReadOnlyList<UploadJob>> StartAsync(UploadRequest request);

    /// <summary>頁面重新開啟時，讓尚未完成的任務繼續推進。</summary>
    Task ResumeAsync(string caseId);
}
