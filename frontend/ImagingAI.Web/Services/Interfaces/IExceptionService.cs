using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>異常佇列（Exception Queue）。</summary>
public interface IExceptionService
{
    /// <summary>尚未結案的異常（依建立時間新到舊）。</summary>
    Task<IReadOnlyList<ExceptionItem>> GetOpenExceptionsAsync();

    /// <summary>修正後重新驗證。</summary>
    Task<OperationResult> ResolveAsync(string exceptionId, ExceptionResolution resolution, string actor);

    /// <summary>標記為無法處理並結案（不寫入 FHIR）。</summary>
    Task<OperationResult> MarkUnresolvableAsync(string exceptionId, string note, string actor);
}
