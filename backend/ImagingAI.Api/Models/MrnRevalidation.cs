namespace ImagingAI.Api.Models;

public sealed record MrnRevalidationRequest(Guid RequestId, int ExpectedCorrectionVersion,
    Guid CorrectionRequestId, Guid OcrAttemptId, string OriginalSha256, string CandidateSha256);
public sealed record MrnRevalidationResponse(bool Success, bool Passed, string Message, Guid RequestId,
    int CorrectionVersion, string Code, string ExceptionStatus, string ProcessingStatus,
    Guid? ResumeJobId, bool ResumeQueued, bool CloudUploadAllowed = false);
public sealed record MrnRevalidationRecord(string Schema, long ExceptionId, long ReportId, long ReviewerId,
    DateTimeOffset At, MrnRevalidationRequest Request, MrnRevalidationResponse Result, int HttpStatus,
    MrnResumeWork? Work);
public sealed class MrnWorkflowException(int status, string code) : Exception("MRN workflow rejected")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
