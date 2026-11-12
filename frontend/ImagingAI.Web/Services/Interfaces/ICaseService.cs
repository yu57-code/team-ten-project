using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>病例查詢、人工覆核與 FHIR 匯出。之後由 Web API 實作取代 MockCaseService。</summary>
public interface ICaseService
{
    /// <summary>所有病例（依上傳時間新到舊）。</summary>
    Task<IReadOnlyList<CaseRecord>> GetCasesAsync();

    /// <summary>總覽頁上方的統計數字。</summary>
    Task<DashboardStats> GetDashboardStatsAsync(string currentUserName);

    /// <summary>
    /// 取得待覆核頁的資料（回傳複本，修改後需呼叫 Save / Submit 才會寫回）。
    /// caseId 為 null 時回傳第一筆待覆核病例；找不到時回傳 null。
    /// </summary>
    Task<CaseReviewDetail?> GetReviewAsync(string? caseId);

    /// <summary>待覆核病例 ID 清單（依上傳時間新到舊）。</summary>
    Task<IReadOnlyList<string>> GetPendingReviewIdsAsync();

    Task<OperationResult> SaveReviewDraftAsync(CaseReviewDetail detail, string reviewer);

    /// <summary>送出審核並轉換 FHIR。所有重要結果都必須已勾選。</summary>
    Task<OperationResult> SubmitReviewAsync(CaseReviewDetail detail, string reviewer);

    /// <summary>覆核時發現無法直接修正，退回異常佇列。</summary>
    Task<OperationResult> ReturnToExceptionQueueAsync(string caseId, string reason, string reviewer);

    /// <summary>匯出已寫入 FHIR Server 的病例 Bundle（JSON 字串）。</summary>
    Task<string> ExportFhirBundleAsync(string caseId);
}
