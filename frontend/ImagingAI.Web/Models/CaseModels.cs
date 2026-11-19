namespace ImagingAI.Web.Models;

/// <summary>病例目前的整體狀態。</summary>
public enum CaseStatus
{
    Processing,
    PendingReview,
    Exception,
    Completed,
    Duplicate,

    /// <summary>異常無法處理，已結案（不寫入 FHIR）。</summary>
    Closed
}

/// <summary>處理流程中單一步驟的狀態。</summary>
public enum StageState
{
    Done,
    Running,
    Waiting,
    Failed,
    Todo
}

/// <summary>處理流程的一個步驟（顯示用文字）。</summary>
public sealed record PipelineStage(string Title, string DoneText, string RunningText, string TodoText);

/// <summary>病例處理流程定義（對應「系統資料驗證流程」）。</summary>
public static class Pipeline
{
    /// <summary>完整 7 步：總覽頁使用。</summary>
    public static readonly IReadOnlyList<PipelineStage> Stages =
    [
        new("上傳與 SHA-256 檢查", "非重複檔案・病歷號正常", "計算 SHA-256 雜湊中…", "計算 SHA-256、檢查病歷號"),
        new("地端 OCR 與去識別化", "OCR 辨識完成・Presidio 個資遮蔽完成", "Qwen2.5-VL-3B 辨識中…", "OCR 辨識文字並遮蔽個資"),
        new("個資安全檢查", "通過，可送雲端", "檢查遮蔽完整性與雲端傳送條件中…", "確認遮蔽完整、符合雲端傳送條件"),
        new("雲端 AI 解析", "GPT-4o 擷取結構化欄位完成", "GPT-4o 擷取結構化欄位中…", "GPT-4o 擷取結構化欄位（含信心值）"),
        new("結構化資料驗證", "欄位完整性、數值合理性、信心值門檻通過", "驗證欄位完整性與信心值中…", "欄位完整性、數值合理性、信心值門檻"),
        new("人工覆核", "重要結果已確認", "等待人工覆核", "確認重要結果是否符合醫療認定"),
        new("FHIR 轉換與寫入", "TW Core 驗證通過，已寫入 FHIR Server", "依 TW Core Mapping 轉換中…", "依 TW Core Mapping、驗證後寫入"),
    ];

    /// <summary>上傳頁只顯示到「結構化資料驗證」的前 5 步。</summary>
    public const int UploadStageCount = 5;

    /// <summary>人工覆核步驟的索引（0 起算）。</summary>
    public const int ReviewStageIndex = 5;
}

/// <summary>病例（對應 Case + ProcessingLog 的摘要）。</summary>
public sealed class CaseRecord
{
    public required string CaseId { get; init; }

    /// <summary>遮蔽後的病歷號，例如 0012•••8。</summary>
    public required string MaskedMrn { get; set; }

    public required DateTime UploadedAt { get; init; }

    public string ReportType { get; set; } = "心導管檢查報告";

    public string FileName { get; set; } = string.Empty;

    public string UploadedBy { get; set; } = string.Empty;

    public CaseStatus Status { get; set; }

    /// <summary>狀態文字，例如「待覆核」「病歷號異常」。</summary>
    public string StatusLabel { get; set; } = string.Empty;

    /// <summary>目前所在步驟（0–6），全部完成時為 7。</summary>
    public int StageIndex { get; set; }

    /// <summary>目前步驟的狀態。</summary>
    public StageState StageState { get; set; }

    /// <summary>目前步驟的進度（0–100），只有 Running 時使用。</summary>
    public int Progress { get; set; }

    /// <summary>覆寫特定步驟的說明文字（例如失敗原因）。key = 步驟索引。</summary>
    public Dictionary<int, string> StageNotes { get; } = new();

    /// <summary>SHA-256 重複時，原本的病例 ID。</summary>
    public string? DuplicateOf { get; set; }
}

/// <summary>總覽頁上方的統計數字。</summary>
public sealed record DashboardStats(
    int PendingReview,
    int OpenExceptions,
    int ExceptionCategoryCount,
    int ExceptionsAssignedToMe,
    int Processing,
    int ProcessingLocal,
    int ProcessingCloud,
    int WrittenToFhirToday);
