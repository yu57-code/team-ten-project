using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>異常佇列（Exception Queue）。</summary>
public interface IExceptionService
{
    Task<MrnEvidenceResult> GetMrnEvidenceAsync(string exceptionId) =>
        Task.FromResult(new MrnEvidenceResult(null, "目前服務未提供 MRN 證據查詢。"));
    Task<MrnCorrectionSaveResult> SaveMrnCorrectionAsync(string exceptionId, MrnCorrectionRequest request) =>
        Task.FromResult(new MrnCorrectionSaveResult(null, 501, "人工修正保存尚未開放。"));
    /// <summary>尚未結案的異常（依建立時間新到舊）。</summary>
    Task<IReadOnlyList<ExceptionItem>> GetOpenExceptionsAsync();

    /// <summary>修正後重新驗證。</summary>
    Task<OperationResult> ResolveAsync(string exceptionId, ExceptionResolution resolution, string actor);

    /// <summary>標記為無法處理並結案（不寫入 FHIR）。</summary>
    Task<OperationResult> MarkUnresolvableAsync(string exceptionId, string note, string actor);

    /// <summary>人工修正後，執行重新驗證。</summary>
    Task<OperationResult> RevalidateAsync(
    string exceptionId,
    string actor);
}
