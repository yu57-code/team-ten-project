namespace ImagingAI.Api.Models;

public sealed record MrnEvidenceCandidate(string Value, int Page, string BlockId,
    int[] Bbox, double Confidence, string ReviewStatus);

public sealed record MrnEvidence(string ExceptionId, string ReportId, long ReviewerId,
    Guid OcrAttemptId, string OriginalSha256, string CandidateSha256,
    string UploadedMrn, IReadOnlyList<MrnEvidenceCandidate> Candidates,
    string ReviewStatus = "UNVERIFIED", bool CloudUploadAllowed = false,
    int? CurrentCorrectionVersion = null, Guid? LastCorrectionRequestId = null);
