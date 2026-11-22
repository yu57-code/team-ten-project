namespace ImagingAI.Web.Models;

/// <summary>異常佇列的 5 種類別（對應驗證流程中會失敗的階段）。</summary>
public enum ExceptionCategory
{
    MedicalRecordNumber,
    ReportType,
    PreParse,
    AiResult,
    Fhir
}

public enum ExceptionStatus
{
    Pending,
    InProgress,
    FixedPendingRevalidation,
    Resolved,
    Unresolvable
}

public enum VerdictLevel
{
    Pass,
    Warning,
    Fail
}

/// <summary>異常詳細中的比對列。</summary>
public sealed record ComparisonRow(string Field, string Actual, string Expected, string Verdict, VerdictLevel Level);

/// <summary>異常佇列中的一筆（對應 ExceptionQueue）。</summary>
public sealed class ExceptionItem
{
    public string Code { get; init; } = string.Empty;
    public required string Id { get; init; }

    public required string CaseId { get; init; }

    public ExceptionCategory Category { get; init; }

    public required string Description { get; init; }

    public DateTime CreatedAt { get; init; }

    /// <summary>指派人員顯示名稱；空字串代表未指派。</summary>
    public string Assignee { get; set; } = string.Empty;

    public ExceptionStatus Status { get; set; }

    /// <summary>失敗位置：ValidationSteps 的索引。</summary>
    public int FailedStep { get; init; }

    /// <summary>重新驗證從哪個步驟開始。</summary>
    public required string RestartFrom { get; init; }

    public required string FixLabel { get; init; }

    public string FixValue { get; set; } = string.Empty;

    /// <summary>病歷號異常時，可選擇合併至既有病人。</summary>
    public bool AllowMerge { get; init; }

    public List<ComparisonRow> Comparisons { get; init; } = [];

    public string? ResolutionNote { get; set; }
}

public sealed record MrnEvidenceCandidate(string Value, int Page, string BlockId,
    int[] Bbox, double Confidence, string ReviewStatus);

public sealed record MrnEvidence(string ExceptionId, string ReportId, long ReviewerId,
    Guid OcrAttemptId, string OriginalSha256, string CandidateSha256,
    string UploadedMrn, IReadOnlyList<MrnEvidenceCandidate> Candidates,
    string ReviewStatus, bool CloudUploadAllowed,
    int? CurrentCorrectionVersion = null, Guid? LastCorrectionRequestId = null);

public sealed record MrnEvidenceResult(MrnEvidence? Evidence, string? Error);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<MrnCorrectionReason>))]
public enum MrnCorrectionReason
{
    UPLOAD_MRN_INPUT_ERROR,
    OCR_RECOGNITION_ERROR,
    DOCUMENT_CASE_MISMATCH
}

public sealed record MrnEvidenceReference(int Page, string BlockId);

public sealed record MrnCorrectionRequest(Guid RequestId, int ExpectedCorrectionVersion,
    Guid OcrAttemptId, string OriginalSha256, string CandidateSha256,
    MrnCorrectionReason Reason, string ConfirmedMrn,
    IReadOnlyList<MrnEvidenceReference> EvidenceReferences, string Note);

public sealed record MrnCorrectionResponse(bool Success, string Message, Guid RequestId,
    int CorrectionVersion, string ExceptionStatus, string ValidationStatus, string ProcessingStatus,
    [property: System.Text.Json.Serialization.JsonRequired] bool ResumeQueued,
    [property: System.Text.Json.Serialization.JsonRequired] bool CloudUploadAllowed,
    [property: System.Text.Json.Serialization.JsonRequired] bool SourceConfirmationRequired);

public sealed record MrnCorrectionSaveResult(MrnCorrectionResponse? Response, int? StatusCode,
    string Message, bool OutcomeUnknown = false);

/// <summary>人工處理異常時送出的內容。</summary>
public sealed record ExceptionResolution(string FixValue, ExceptionStatus Status, bool MergeIntoExistingPatient, string Note);

public static class ExceptionText
{
    /// <summary>異常詳細中「失敗位置」的 9 個步驟。</summary>
    public static readonly IReadOnlyList<string> ValidationSteps =
        ["輸入", "重複檢查", "預處理", "類型辨識", "解析前驗證", "AI 解析", "結果驗證", "人工覆核", "FHIR"];

    public static string CategoryLabel(ExceptionCategory category) => category switch
    {
        ExceptionCategory.MedicalRecordNumber => "病歷號異常",
        ExceptionCategory.ReportType => "報告類型異常",
        ExceptionCategory.PreParse => "解析前驗證異常",
        ExceptionCategory.AiResult => "AI 解析結果異常",
        ExceptionCategory.Fhir => "FHIR 驗證異常",
        _ => category.ToString()
    };

    public static string StatusLabel(ExceptionStatus status) => status switch
    {
        ExceptionStatus.Pending => "待處理",
        ExceptionStatus.InProgress => "處理中",
        ExceptionStatus.FixedPendingRevalidation => "已修正，待重新驗證",
        ExceptionStatus.Resolved => "已解決",
        ExceptionStatus.Unresolvable => "無法處理",
        _ => status.ToString()
    };
}
