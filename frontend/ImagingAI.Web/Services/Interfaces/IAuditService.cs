using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>稽核軌跡與個人處理紀錄（唯讀）。</summary>
public interface IAuditService
{
    /// <summary>依條件查詢稽核軌跡（新到舊）。</summary>
    Task<IReadOnlyList<AuditEntry>> GetEntriesAsync(AuditQuery query);

    /// <summary>指定使用者的個人處理紀錄（新到舊）。</summary>
    Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync(string userName);

    Task<HistoryStats> GetHistoryStatsAsync(string userName, HistoryRange range);

    /// <summary>目前時間，用來計算「近 24 小時」等範圍。</summary>
    DateTime Now { get; }
}
