using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImagingAI.Api.Models;

namespace ImagingAI.Api.Services;

public sealed class MrnRevalidationService(IMrnCorrectionAccess access, IMrnWorkflowRepository repository,
    IProtectedMrnCorrectionStore store, MrnSourceCheckpoint checkpoint)
{
    public async Task<(MrnRevalidationResponse Result, int Status)> RevalidateAsync(long id,
        MrnRevalidationRequest request, string credential, CancellationToken token)
    {
        if (!access.IsAuthorized(credential)) throw new MrnWorkflowException(404, "NOT_AVAILABLE");
        if (request is null || request.RequestId == Guid.Empty || request.CorrectionRequestId == Guid.Empty || request.OcrAttemptId == Guid.Empty ||
            request.ExpectedCorrectionVersion < 1 || !Hash(request.OriginalSha256) || !Hash(request.CandidateSha256))
            throw new MrnWorkflowException(400, "INVALID_REQUEST");
        request = request with { OriginalSha256 = request.OriginalSha256.ToUpperInvariant(), CandidateSha256 = request.CandidateSha256.ToUpperInvariant() };
        await using var session = await repository.OpenAsync(id, token) ?? throw new MrnWorkflowException(404, "NOT_AVAILABLE");
        var latest = session.Corrections.OrderByDescending(c => c.Version).FirstOrDefault() ?? throw new MrnWorkflowException(409, "CORRECTION_REQUIRED");
        if (latest.Version != request.ExpectedCorrectionVersion || latest.RequestId != request.CorrectionRequestId || latest.OcrAttemptId != request.OcrAttemptId ||
            latest.OriginalSha256 != request.OriginalSha256 || latest.CandidateSha256 != request.CandidateSha256) throw new MrnWorkflowException(409, "VERSION_CONFLICT");
        var replays = session.Validations.Where(v => v.Request.RequestId == request.RequestId).ToArray();
        if (replays.Length > 1) throw new MrnWorkflowException(409, "HISTORY_CONFLICT");
        if (replays.Length == 1)
        {
            if (replays[0].Request != request || replays[0].ReviewerId != session.ReviewerId) throw new MrnWorkflowException(409, "REQUEST_CONFLICT");
            return (replays[0].Result, replays[0].HttpStatus);
        }
        if (session.ExceptionStatus != "FIXED_PENDING_REVALIDATION" || session.ProcessingStatus != "MRN_REVIEW_REQUIRED")
            throw new MrnWorkflowException(409, "STATE_CONFLICT");
        var code = "MRN_RULE_INVALID"; var status = 200; MrnResumeWork? work = null;
        try
        {
            var saved = await store.ReadAsync(latest, token);
            var source = await session.EvidenceAsync(token) ?? throw new InvalidDataException();
            ValidateConfirmed(saved, latest, source);
            if (saved.Request.Reason == MrnCorrectionReason.DOCUMENT_CASE_MISMATCH) code = "DOCUMENT_SOURCE_REQUIRED";
            else if (session.Blockers != 0) code = "OTHER_BLOCKING_EXCEPTIONS";
            else if (!await session.PatientMatchesAsync(saved.Request.ConfirmedMrn, token)) code = "PATIENT_LINK_CONFLICT";
            else
            {
                var files = await checkpoint.CaptureAsync(session.ReportId, latest.OcrAttemptId, session.Storage, latest, token);
                work = new(Guid.NewGuid(), session.ReportId, session.Storage, id, latest.Version, latest.RequestId, latest.OcrAttemptId,
                    latest.OriginalSha256, latest.CandidateSha256, latest.ProtectedSha256, files);
                await using var lease = await checkpoint.LeaseAsync(work, token);
                var final = await session.EvidenceAsync(token) ?? throw new InvalidDataException();
                ValidateConfirmed(saved, latest, final);
                if (!await session.PatientMatchesAsync(saved.Request.ConfirmedMrn, token)) throw new MrnWorkflowException(409, "PATIENT_LINK_CONFLICT");
                code = "VALIDATED";
            }
        }
        catch (MrnCorrectionException e) { code = e.StatusCode == 400 ? "MRN_RULE_INVALID" : "SOURCE_CONFLICT"; status = e.StatusCode == 400 ? 200 : 409; work = null; }
        catch (MrnWorkflowException e) { code = e.Code; status = e.Status; work = null; }
        catch (Exception e) when (e is InvalidDataException or JsonException or CryptographicException or FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException or FormatException)
        { code = "EVIDENCE_INTEGRITY_FAILED"; status = 409; work = null; }
        var passed = work is not null;
        var result = new MrnRevalidationResponse(true, passed, passed ? "已排入背景恢復，尚未完成去識別化。" : "重新驗證未通過，病例仍維持阻擋。",
            request.RequestId, latest.Version, passed ? "MRN_REVALIDATION_PASSED" : code, passed ? "RESOLVED" : "FIXED_PENDING_REVALIDATION",
            passed ? "MRN_REVALIDATED" : "MRN_REVIEW_REQUIRED", work?.JobId, passed);
        await session.CommitValidationAsync(new("mrn_revalidation/v2", id, session.ReportId, session.ReviewerId,
            DateTimeOffset.UtcNow, request, result, status, work), token);
        return (result, status);
    }
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Fa-f0-9]{64}\z");
    internal static void ValidateConfirmed(ProtectedMrnCorrection saved, MrnCorrectionMetadata metadata, MrnEvidence evidence)
    {
        if (evidence.OcrAttemptId != metadata.OcrAttemptId || evidence.OriginalSha256 != metadata.OriginalSha256 ||
            evidence.CandidateSha256 != metadata.CandidateSha256) throw new InvalidDataException();
        var request = MrnCorrectionService.Validate(saved.Request);
        MrnCorrectionService.CheckEvidence(request, evidence);
        if (request.Reason == MrnCorrectionReason.UPLOAD_MRN_INPUT_ERROR && !evidence.Candidates.Any(c =>
                c.Value == request.ConfirmedMrn && request.EvidenceReferences.Contains(new(c.Page, c.BlockId))))
            throw new MrnWorkflowException(200, "MRN_SOURCE_NOT_CONFIRMED");
    }
}
