using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Mock;

/// <summary>
/// 模擬資料庫的初始內容：全部為空，只保留欄位結構。
/// 資料會在操作時產生（例如上傳病例後才會出現病例、稽核軌跡與個人紀錄）。
/// </summary>
internal static class MockSeedData
{
    public static List<CaseRecord> Cases() => [];

    public static Dictionary<string, CaseReviewDetail> Reviews() => new();

    public static List<ExceptionItem> Exceptions() => [];

    public static List<AuditEntry> AuditEntries() => [];

    public static Dictionary<string, List<HistoryRecord>> History() => new();

    public static List<UploadJob> UploadJobs() => [];

    public static Dictionary<string, string> FileHashIndex() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 待覆核頁的空白資料：只有欄位，沒有值。
    /// 之後串接 Web API 時，這些欄位會由 AI Agent 的解析結果（structured_json）填入。
    /// </summary>
    public static CaseReviewDetail EmptyReview(string caseId, string maskedMrn, DateTime uploadedAt, string batchId) => new()
    {
        CaseId = caseId,
        MaskedMrn = maskedMrn,
        UploadedAt = uploadedAt,
        BatchId = batchId,
        Version = 1,
        RequiredFields =
        [
            new() { Key = "mrn", Label = "病歷號 mrn", Kind = FieldKind.Mono },
            new() { Key = "patient_name", Label = "病人姓名 patient_name" },
            new() { Key = "report_type", Label = "報告類型 report_type", Kind = FieldKind.Select, Options = ["心導管檢查報告"] },
            new() { Key = "study_date", Label = "檢查日期 study_date" },
            new() { Key = "result_text", Label = "主要病例結果 result_text", Kind = FieldKind.TextArea, Rows = 2 },
            new() { Key = "case_summary", Label = "病例總結 case_summary（AI 生成）", Kind = FieldKind.TextArea, Rows = 3 },
        ],
    };
}
