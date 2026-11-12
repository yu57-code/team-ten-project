using ImagingAI.Web.Models;

namespace ImagingAI.Web.Components.Shared;

/// <summary>徽章顏色（對應 imaging.css 的 .ia-badge--*）。</summary>
public enum BadgeTone
{
    Ok,
    Warn,
    Err,
    Info,
    Man,
    Mute,
    Adm
}

/// <summary>把資料狀態轉成畫面上的文字與徽章顏色（只處理顯示，不含商業邏輯）。</summary>
public static class StatusStyles
{
    public static string CssClass(BadgeTone tone) => tone switch
    {
        BadgeTone.Ok => "ia-badge ia-badge--ok",
        BadgeTone.Warn => "ia-badge ia-badge--warn",
        BadgeTone.Err => "ia-badge ia-badge--err",
        BadgeTone.Info => "ia-badge ia-badge--info",
        BadgeTone.Man => "ia-badge ia-badge--man",
        BadgeTone.Adm => "ia-badge ia-badge--adm",
        _ => "ia-badge ia-badge--mute",
    };

    public static BadgeTone Tone(CaseStatus status) => status switch
    {
        CaseStatus.PendingReview => BadgeTone.Man,
        CaseStatus.Exception => BadgeTone.Err,
        CaseStatus.Processing => BadgeTone.Info,
        CaseStatus.Completed => BadgeTone.Ok,
        _ => BadgeTone.Mute,
    };

    public static BadgeTone Tone(ExceptionStatus status) => status switch
    {
        ExceptionStatus.InProgress => BadgeTone.Man,
        ExceptionStatus.FixedPendingRevalidation => BadgeTone.Info,
        ExceptionStatus.Resolved => BadgeTone.Ok,
        ExceptionStatus.Unresolvable => BadgeTone.Mute,
        _ => BadgeTone.Warn,
    };

    public static BadgeTone Tone(VerdictLevel level) => level switch
    {
        VerdictLevel.Pass => BadgeTone.Ok,
        VerdictLevel.Warning => BadgeTone.Warn,
        _ => BadgeTone.Err,
    };

    public static (string Label, BadgeTone Tone) Display(Significance significance) => significance switch
    {
        Significance.Significant => ("顯著", BadgeTone.Err),
        Significance.Moderate => ("中度", BadgeTone.Warn),
        Significance.Normal => ("正常", BadgeTone.Ok),
        Significance.Procedure => ("處置", BadgeTone.Info),
        _ => ("結果", BadgeTone.Info),
    };

    public static (string Label, BadgeTone Tone) Display(ObservationFlag flag) => flag switch
    {
        ObservationFlag.Normal => ("正常", BadgeTone.Ok),
        ObservationFlag.High => ("偏高", BadgeTone.Warn),
        _ => ("—", BadgeTone.Mute),
    };

    public static (string Label, BadgeTone Tone) Display(AuditKind kind) => kind switch
    {
        AuditKind.Upload => ("上傳", BadgeTone.Mute),
        AuditKind.AiParse => ("AI 解析", BadgeTone.Info),
        AuditKind.Edit => ("修正", BadgeTone.Warn),
        AuditKind.Review => ("審核", BadgeTone.Man),
        AuditKind.FhirWrite => ("FHIR 寫入", BadgeTone.Ok),
        AuditKind.RoleChange => ("權限變更", BadgeTone.Adm),
        AuditKind.SettingsChange => ("系統設定", BadgeTone.Adm),
        AuditKind.Maintenance => ("系統維護", BadgeTone.Info),
        _ => ("登入", BadgeTone.Mute),
    };

    public static (string Label, BadgeTone Tone) Display(HistoryAction action) => action switch
    {
        HistoryAction.Upload => ("上傳病例", BadgeTone.Mute),
        HistoryAction.Review => ("覆核送出", BadgeTone.Man),
        HistoryAction.Edit => ("修正欄位", BadgeTone.Info),
        HistoryAction.Exception => ("處理異常", BadgeTone.Warn),
        _ => ("標記無法處理", BadgeTone.Err),
    };

    public static (string Label, BadgeTone Tone) Display(HistoryCaseState state) => state switch
    {
        HistoryCaseState.WrittenToFhir => ("已寫入 FHIR", BadgeTone.Ok),
        HistoryCaseState.PendingReview => ("待覆核", BadgeTone.Man),
        HistoryCaseState.Processing => ("處理中", BadgeTone.Info),
        HistoryCaseState.ExceptionInProgress => ("異常處理中", BadgeTone.Err),
        HistoryCaseState.Duplicate => ("重複病例", BadgeTone.Mute),
        _ => ("已結案（未寫入）", BadgeTone.Mute),
    };

    public static (string Label, BadgeTone Tone) Display(ServiceStatus status) => status switch
    {
        ServiceStatus.Healthy => ("正常", BadgeTone.Ok),
        ServiceStatus.Degraded => ("回應變慢", BadgeTone.Warn),
        ServiceStatus.Down => ("無法連線", BadgeTone.Err),
        _ => ("尚未串接", BadgeTone.Mute),
    };

    /// <summary>總覽表格「目前階段」欄的文字。</summary>
    public static string StageText(CaseRecord c)
    {
        var total = Pipeline.Stages.Count;
        return c.Status switch
        {
            CaseStatus.Duplicate => "SHA-256 重複，回傳既有結果",
            CaseStatus.Closed => "已結案，不寫入 FHIR",
            _ when c.StageIndex >= total => $"{total}／{total} 已寫入 FHIR",
            _ => $"{c.StageIndex + 1}／{total} {Pipeline.Stages[c.StageIndex].Title}",
        };
    }
}
