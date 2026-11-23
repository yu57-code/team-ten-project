namespace ImagingAI.Api.Models;

public sealed record PendingReviewDto(
    long ReportId,
    string FileName,
    string ProcessingStatus,
    DateTime UploadedAt,
    long? ReportTypeId,
    string? ReportType,
    string DeidStatus
);

internal sealed record ReviewFieldsDto(
    long ReportId,
    Guid OcrAttemptId,
    DateTimeOffset CreatedAt,
    string SchemaVersion,
    string ReviewStatus,
    string MrnValidation,
    ReviewCandidateFieldsDto Fields);

internal sealed record ReviewCandidateFieldsDto(
    ReviewFieldDto PatientName,
    ReviewFieldDto Mrn,
    ReviewFieldDto Gender,
    ReviewFieldDto ReportDate,
    ReviewFieldDto ReportType);

internal sealed record ReviewFieldDto(
    string Status,
    string? Value,
    IReadOnlyList<ReviewFieldCandidateDto> Candidates);

internal sealed record ReviewFieldCandidateDto(
    string Value,
    string Source,
    int Page,
    string BlockId,
    IReadOnlyList<int> Bbox,
    double Confidence,
    string ReviewStatus);

internal enum ReviewFieldsError
{
    ReportNotFound,
    CurrentAttemptMissing,
    CurrentAttemptInvalid,
    CandidateBatchNotFound,
    UnsupportedSchema,
    InvalidCandidatePayload,
    DatabaseFailure
}

internal sealed record ReviewFieldsResult(ReviewFieldsDto? Data, ReviewFieldsError? Error)
{
    public static ReviewFieldsResult Failed(ReviewFieldsError error) => new(null, error);
}

internal enum ReviewPreviewStatus
{
    ReportNotFound,
    ReportNotEligible,
    InvalidPage,
    CurrentOcrAttemptMissing,
    CurrentDeidAttemptMissing,
    InvalidPublishedArtifact,
    PreviewImageMissing,
    PreviewImageInvalid,
    StorageFailure,
    DatabaseFailure,
    Success
}

internal sealed record ReviewPreviewResult(ReviewPreviewStatus Status, byte[]? Png = null)
{
    public string? ContentType => Status == ReviewPreviewStatus.Success ? "image/png" : null;
}

internal sealed record ReviewDraftSourceDto(string FieldName, int Page, string BlockId);

internal sealed record ReviewDraftFieldDto(string? Value, ReviewDraftSourceDto? SourceReference);

internal sealed record SaveReviewDraftDto(Guid OcrAttemptId, Dictionary<string, ReviewDraftFieldDto> Fields);

internal sealed record ReviewDraftDocument(
    string SchemaVersion, Guid OcrAttemptId, int Version, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, Dictionary<string, ReviewDraftFieldDto> Fields);

internal sealed record ReviewDraftDto(
    long ReportId, long ReviewerId, string Status, Guid OcrAttemptId, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Dictionary<string, ReviewDraftFieldDto> Fields);

internal enum ReviewDraftStatus
{
    ReportNotFound,
    DraftNotFound,
    Conflict,
    PreconditionRequired,
    PreconditionFailed,
    InvalidDraft,
    InvalidSourceReference,
    StorageFailure,
    DatabaseFailure,
    Success
}

internal sealed record ReviewDraftResult(
    ReviewDraftStatus Status, ReviewDraftDto? Data = null, string? ETag = null);

internal sealed record ConfirmReviewRequestDto(int DraftVersion, Guid OcrAttemptId);

internal sealed record ReviewConfirmationSnapshot(
    string SchemaVersion, long ReportId, Guid OcrAttemptId, long SourceDraftReviewId,
    int SourceDraftVersion, DateTimeOffset ConfirmedAt, Dictionary<string, ReviewDraftFieldDto> Fields);

internal enum ReviewConfirmationStatus
{
    ReportNotFound,
    ReportNotEligible,
    AlreadyConfirmed,
    DraftNotFound,
    DuplicateDraft,
    PreconditionRequired,
    PreconditionFailed,
    InvalidDraft,
    OcrAttemptConflict,
    InvalidSourceReference,
    BlockingException,
    ConfirmationPolicyNotReady,
    StorageFailure,
    DatabaseFailure,
    Success
}

internal sealed record ReviewConfirmationResult(
    ReviewConfirmationStatus Status, long? ConfirmationId = null);
