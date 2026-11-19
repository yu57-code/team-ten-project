namespace ImagingAI.Web.Models;

// ======================================================================
// 系統監控
// ======================================================================

public enum ServiceStatus
{
    /// <summary>尚未串接或尚未檢查。</summary>
    Unknown,
    Healthy,
    Degraded,
    Down
}

/// <summary>一個外部服務的健康狀態。</summary>
public sealed record ServiceHealth(
    string Key,
    string Name,
    string Description,
    ServiceStatus Status,
    string Metric,
    DateTime? CheckedAt);

public enum FailedTaskState
{
    Failed,
    Requeued
}

/// <summary>系統錯誤造成的失敗任務（對應 ProcessingLog 中 Status = Failed 的紀錄）。</summary>
public sealed class FailedTask
{
    public required string LogId { get; init; }

    public required string CaseId { get; init; }

    public required string Stage { get; init; }

    public required string ErrorMessage { get; init; }

    public string ModelVersion { get; init; } = "—";

    public DateTime StartedAt { get; init; }

    public FailedTaskState State { get; set; }
}

/// <summary>系統監控頁上方的統計。AverageProcessingTime 為 null 代表還沒有可計算的資料。</summary>
public sealed record MonitorStats(
    int TodayCases,
    int TodayCompleted,
    int TodayProcessing,
    int TodayExceptions,
    int FailedTasks,
    TimeSpan? AverageProcessingTime,
    int DuplicateHits);

// ======================================================================
// 使用者與角色權限
// ======================================================================

/// <summary>系統帳號（對應 UserAccount）。</summary>
public sealed class ManagedUser
{
    public required string Username { get; init; }

    public string DisplayName { get; set; } = string.Empty;

    public UserRole Role { get; set; }

    /// <summary>是否已綁定 MFA 驗證器。</summary>
    public bool MfaBound { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>新增或編輯使用者時的表單內容。</summary>
public sealed class UserDraft
{
    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Reviewer;
}

/// <summary>角色權限對照的一列（依使用案例圖）。</summary>
public sealed record RolePermission(string Feature, bool Reviewer, bool Admin);

public static class RolePermissions
{
    /// <summary>系統規格中的權限定義（不是模擬資料）。</summary>
    public static readonly IReadOnlyList<RolePermission> All =
    [
        new("單一入口登入＋MFA", true, true),
        new("上傳病例", true, false),
        new("查看病例與處理進度", true, false),
        new("審核確認重要結果", true, false),
        new("修正解析結果", true, false),
        new("人工處理異常病例", true, false),
        new("匯出 FHIR JSON", true, false),
        new("查看稽核軌跡（病例相關）", true, true),
        new("查看稽核軌跡（權限變更、登入、系統設定）", false, true),
        new("管理使用者與角色權限", false, true),
        new("重新執行系統失敗任務", false, true),
        new("系統設定與備份", false, true),
    ];
}

// ======================================================================
// 系統設定與備份
// ======================================================================

/// <summary>系統設定。門檻預設值為系統規格，可在設定頁調整。</summary>
public sealed class SystemSettings
{
    /// <summary>第二層：AI 欄位信心值門檻（0.50–0.99）。</summary>
    public double AiConfidenceThreshold { get; set; } = 0.85;

    /// <summary>第一層：OCR 文字品質門檻（0.30–0.95）。</summary>
    public double OcrQualityThreshold { get; set; } = 0.70;

    public bool RequireOcrQuality { get; set; } = true;

    public bool RequireKnownReportType { get; set; } = true;

    public bool RequireCompletePages { get; set; } = true;

    public string FhirBaseUrl { get; set; } = string.Empty;

    public string TwCoreIgVersion { get; set; } = string.Empty;

    public string FhirVersion { get; set; } = "R4";

    public string BackupSchedule { get; set; } = "每日 02:00";

    public string BackupRetention { get; set; } = "30 天";

    public string BackupLocation { get; set; } = string.Empty;
}

/// <summary>報告類型（對應 ReportType；決定 AI Agent 載入的 Prompt）。</summary>
public sealed class ReportTypeConfig
{
    public required string TypeCode { get; init; }

    public required string NameZh { get; set; }

    public string NameEn { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

public enum BackupStatus
{
    Completed,
    Failed
}

public sealed record BackupRecord(string Id, DateTime CreatedAt, string Kind, string Size, BackupStatus Status, string Note);

public sealed record FhirConnectionResult(bool Success, string Message);
