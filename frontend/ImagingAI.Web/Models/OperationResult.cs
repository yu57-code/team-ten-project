namespace ImagingAI.Web.Models;

/// <summary>一般操作（送出審核、處理異常…）的結果。</summary>
public sealed record OperationResult(bool Succeeded, string Message)
{
    public static OperationResult Ok(string message) => new(true, message);

    public static OperationResult Fail(string message) => new(false, message);
}
