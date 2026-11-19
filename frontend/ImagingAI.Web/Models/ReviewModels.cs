namespace ImagingAI.Web.Models;

public enum FieldKind
{
    Text,
    Mono,
    Select,
    TextArea
}

/// <summary>AI 擷取的單一欄位（對應 structured_json 的一個欄位）。</summary>
public sealed class ExtractedField
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public string Value { get; set; } = string.Empty;

    /// <summary>AI 擷取的信心值（0–1）；尚未解析時為 null。</summary>
    public double? Confidence { get; init; }

    /// <summary>欄位下方的說明，例如「0.97・第 1 頁」。</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary>說明是否為正面訊息（綠字），例如「與上傳者輸入一致」。</summary>
    public bool HintPositive { get; init; }

    public FieldKind Kind { get; init; } = FieldKind.Text;

    public IReadOnlyList<string> Options { get; set; } = [];

    public int Rows { get; init; } = 2;

    /// <summary>是否佔滿整列（文字區塊）。</summary>
    public bool Wide => Kind == FieldKind.TextArea;
}

public enum Significance
{
    Significant,
    Moderate,
    Normal,
    Procedure,
    Outcome
}

/// <summary>需要人工勾選確認的重要結果。</summary>
public sealed class ImportantResult
{
    public required string Key { get; init; }

    public required string Title { get; set; }

    public required string Source { get; init; }

    public Significance Significance { get; init; }

    public double Confidence { get; init; }

    public bool Confirmed { get; set; }

    /// <summary>對應原始文件中的行（用來標示來源位置）。</summary>
    public string? SourceLineKey { get; init; }

    public int SourcePage { get; init; } = 1;
}

public enum ObservationFlag
{
    None,
    Normal,
    High
}

/// <summary>檢查數值（FHIR Observation）。</summary>
public sealed class ObservationRow
{
    public required string Item { get; init; }

    public required string Value { get; set; }

    public required string Unit { get; init; }

    public string ReferenceRange { get; init; } = "—";

    public ObservationFlag Flag { get; init; }

    public bool CodeMapped { get; set; }
}

public sealed record FhirCheck(string Resource, bool Passed, string Label);

/// <summary>原始文件（已去識別化）的一行。文字中的 [PERSON]、[ID] 等代表 Presidio 遮蔽處。</summary>
public sealed record DocLine(string Text, string? Key = null, bool IsHeading = false, bool IsCenter = false, bool IsPlaceholder = false);

public sealed record DocPage(int Number, IReadOnlyList<DocLine> Lines);

/// <summary>待覆核頁面所需的完整資料。</summary>
public sealed class CaseReviewDetail
{
    public required string CaseId { get; init; }

    public required string MaskedMrn { get; init; }

    public string ReportType { get; init; } = "心導管檢查報告";

    public DateTime UploadedAt { get; init; }

    public string BatchId { get; init; } = string.Empty;

    public int Version { get; set; } = 2;

    public CaseStatus Status { get; set; } = CaseStatus.PendingReview;

    /// <summary>OCR 文字品質（0–1）；尚未有資料時為 null。</summary>
    public double? OcrQuality { get; init; }

    /// <summary>Presidio 遮蔽的個資數量；尚未有資料時為 null。</summary>
    public int? MaskedCount { get; init; }

    /// <summary>報告類型辨識信心值；尚未有資料時為 null。</summary>
    public double? ReportTypeConfidence { get; init; }

    /// <summary>AI 欄位信心值門檻（取自系統設定）。</summary>
    public double ConfidenceThreshold { get; set; } = 0.85;

    public List<DocPage> Pages { get; init; } = [];

    /// <summary>必要欄位（前 4 個為兩欄格線，TextArea 佔滿整列）。</summary>
    public List<ExtractedField> RequiredFields { get; init; } = [];

    public List<ImportantResult> ImportantResults { get; init; } = [];

    public List<ObservationRow> Observations { get; init; } = [];

    public List<FhirCheck> FhirChecks { get; init; } = [];

    public string? FhirErrorTarget { get; set; }

    public string? FhirErrorMessage { get; set; }

    public DateTime? LastFhirSubmitAt { get; set; }

    public string Comment { get; set; } = string.Empty;
}
