using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingAI.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter<MrnCorrectionReason>))]
public enum MrnCorrectionReason
{
    UPLOAD_MRN_INPUT_ERROR,
    OCR_RECOGNITION_ERROR,
    DOCUMENT_CASE_MISMATCH
}

public sealed record MrnEvidenceReference(int Page, string BlockId);

public sealed record MrnCorrectionRequest(Guid RequestId, int ExpectedCorrectionVersion,
    Guid OcrAttemptId, string OriginalSha256, string CandidateSha256,
    [property: JsonRequired] MrnCorrectionReason Reason, string ConfirmedMrn,
    IReadOnlyList<MrnEvidenceReference> EvidenceReferences, string Note);

public sealed record MrnCorrectionResponse(bool Success, string Message, Guid RequestId,
    int CorrectionVersion, string ExceptionStatus = "FIXED_PENDING_REVALIDATION",
    string ValidationStatus = "NOT_RUN", string ProcessingStatus = "MRN_REVIEW_REQUIRED",
    bool ResumeQueued = false, bool CloudUploadAllowed = false,
    bool SourceConfirmationRequired = false);

// Only non-sensitive references belong in review_comment / audit JSON.
public sealed record MrnCorrectionMetadata(string Schema, long ExceptionId, long ReportId,
    Guid RequestId, int Version, long ReviewerId, DateTimeOffset SavedAt,
    Guid OcrAttemptId, string OriginalSha256, string CandidateSha256,
    MrnCorrectionReason Reason, string ProtectedReference, string ProtectedSha256);

public sealed record ProtectedMrnCorrection(long ExceptionId, long ReportId, int Version,
    long ReviewerId, DateTimeOffset SavedAt, MrnCorrectionRequest Request);

public sealed class MrnCorrectionException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class MrnCorrectionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
