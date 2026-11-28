using System.Text.Json.Serialization;

namespace ImagingAI.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewCaseStatus
{
    Processing,
    PendingReview,
    Exception,
    Completed,
    Duplicate,
    Closed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewFieldKind
{
    Text,
    Mono,
    Select,
    TextArea
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewSignificance
{
    Significant,
    Moderate,
    Normal,
    Procedure,
    Outcome
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewObservationFlag
{
    None,
    Normal,
    High
}

public sealed class ReviewExtractedFieldDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Value { get; set; } = string.Empty;
    public double? Confidence { get; init; }
    public string Hint { get; init; } = string.Empty;
    public bool HintPositive { get; init; }
    public ReviewFieldKind Kind { get; init; } = ReviewFieldKind.Text;
    public IReadOnlyList<string> Options { get; set; } = [];
    public int Rows { get; init; } = 2;
    public bool Wide => Kind == ReviewFieldKind.TextArea;
}

public sealed class ReviewImportantResultDto
{
    public required string Key { get; init; }
    public required string Title { get; set; }
    public required string Source { get; init; }
    public ReviewSignificance Significance { get; init; }
    public double Confidence { get; init; }
    public bool Confirmed { get; set; }
    public string? SourceLineKey { get; init; }
    public int SourcePage { get; init; } = 1;
}

public sealed class ReviewObservationRowDto
{
    public required string Item { get; init; }
    public required string Value { get; set; }
    public required string Unit { get; init; }
    public string ReferenceRange { get; init; } = "—";
    public ReviewObservationFlag Flag { get; init; }
    public bool CodeMapped { get; set; }
}

public sealed record ReviewFhirCheckDto(string Resource, bool Passed, string Label);

public sealed record ReviewDocLineDto(
    string Text,
    string? Key = null,
    bool IsHeading = false,
    bool IsCenter = false,
    bool IsPlaceholder = false);

public sealed record ReviewDocPageDto(int Number, IReadOnlyList<ReviewDocLineDto> Lines);

public sealed class CaseReviewDetailDto
{
    public required string CaseId { get; init; }
    public required string MaskedMrn { get; init; }
    public string ReportType { get; init; } = "心導管檢查報告";
    public DateTime UploadedAt { get; init; }
    public string BatchId { get; init; } = string.Empty;
    public int Version { get; set; } = 1;
    public ReviewCaseStatus Status { get; set; } = ReviewCaseStatus.PendingReview;
    public double? OcrQuality { get; init; }
    public int? MaskedCount { get; init; }
    public double? ReportTypeConfidence { get; init; }
    public double ConfidenceThreshold { get; set; } = 0.85;
    public List<ReviewDocPageDto> Pages { get; init; } = [];
    public List<ReviewExtractedFieldDto> RequiredFields { get; init; } = [];
    public List<ReviewImportantResultDto> ImportantResults { get; init; } = [];
    public List<ReviewObservationRowDto> Observations { get; init; } = [];
    public List<ReviewFhirCheckDto> FhirChecks { get; init; } = [];
    public string? FhirErrorTarget { get; set; }
    public string? FhirErrorMessage { get; set; }
    public DateTime? LastFhirSubmitAt { get; set; }
    public string Comment { get; set; } = string.Empty;
}

public sealed record SaveReviewRequest(CaseReviewDetailDto Detail, string ReviewerName);

public sealed record ReturnReviewRequest(string Reason, string ReviewerName);

public sealed record ReviewOperationResponse(bool Succeeded, string Message);