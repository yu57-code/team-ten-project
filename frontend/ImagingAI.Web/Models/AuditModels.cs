namespace ImagingAI.Web.Models;

/// <summary>稽核軌跡的動作類型。RoleChange 之後的項目只有系統管理員看得到。</summary>
public enum AuditKind
{
    Upload,
    AiParse,
    Edit,
    Review,
    FhirWrite,
    RoleChange,
    Login,
    SettingsChange,
    Maintenance
}

public static class AuditKinds
{
    /// <summary>與病例資料相關的動作（審核人員可以看到的範圍）。</summary>
    public static readonly IReadOnlySet<AuditKind> CaseRelated = new HashSet<AuditKind>
    {
        AuditKind.Upload, AuditKind.AiParse, AuditKind.Edit, AuditKind.Review, AuditKind.FhirWrite,
    };
}

/// <summary>稽核軌跡（對應 AuditTrail）。</summary>
public sealed record AuditEntry(
    string Id,
    DateTime Time,
    string Actor,
    AuditKind Kind,
    string ActionCode,
    string? CaseId,
    string Version,
    string Note,
    string BeforeJson,
    string AfterJson);

public enum AuditActorFilter
{
    All,
    Me,

    /// <summary>所有人工操作（排除系統自動）。</summary>
    Users,
    System
}

/// <summary>稽核軌跡查詢條件。</summary>
/// <param name="Since">只取這個時間之後的紀錄；null = 不限</param>
/// <param name="Actor">操作者篩選</param>
/// <param name="CurrentUserName">「只看我」時比對的顯示名稱</param>
/// <param name="Kind">動作類型；null = 全部</param>
/// <param name="CaseIdKeyword">病例 ID 關鍵字</param>
/// <param name="CaseRelatedOnly">true = 只回傳病例相關紀錄（審核人員用，不含權限變更、登入與系統設定）</param>
public sealed record AuditQuery(
    DateTime? Since = null,
    AuditActorFilter Actor = AuditActorFilter.All,
    string? CurrentUserName = null,
    AuditKind? Kind = null,
    string? CaseIdKeyword = null,
    bool CaseRelatedOnly = true);

public enum HistoryAction
{
    Upload,
    Review,
    Edit,
    Exception,
    Unresolvable
}

public enum HistoryCaseState
{
    WrittenToFhir,
    PendingReview,
    Processing,
    ExceptionInProgress,
    Duplicate,
    Closed
}

/// <summary>個人處理紀錄的一列。</summary>
public sealed record HistoryRecord(DateTime Time, HistoryAction Action, string CaseId, string Description, HistoryCaseState CaseState);

/// <summary>個人處理紀錄上方的統計。</summary>
public sealed record HistoryStats(int Uploads, int Reviews, int Exceptions, int Edits);

public enum HistoryRange
{
    ThisWeek,
    ThisMonth,
    Last90Days
}
