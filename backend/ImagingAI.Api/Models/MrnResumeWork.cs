namespace ImagingAI.Api.Models;

public sealed record MrnSourceFile(string RelativePath, string Sha256);
public sealed record MrnResumeWork(Guid JobId, long ReportId, string StorageUri, long ExceptionId, int CorrectionVersion,
    Guid CorrectionRequestId, Guid OcrAttemptId, string OriginalSha256, string CandidateSha256,
    string CorrectionSha256, IReadOnlyList<MrnSourceFile> Files);
public sealed record MrnResumeRun(string Schema, Guid JobId, Guid RunId, long ReportId, long ExceptionId,
    int Version, DateTimeOffset At, string Status, string? ArtifactSha256 = null, int Sequence = 1,
    int? Attempt = null, string? Reason = null, DateTimeOffset? RetryAt = null);
