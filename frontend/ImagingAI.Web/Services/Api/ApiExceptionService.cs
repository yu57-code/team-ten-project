
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Api;

public sealed class ApiExceptionService(HttpClient http, IConfiguration configuration, IWebHostEnvironment environment)
    : IExceptionService
{
    public async Task<MrnEvidenceResult> GetMrnEvidenceAsync(string exceptionId)
    {
        var credential = configuration["MrnReview:TestCredential"];
        if (!environment.IsDevelopment() || !configuration.GetValue("MrnReview:Enabled", false) ||
            string.IsNullOrWhiteSpace(credential) || credential.Length < 32)
            return new(null, "MRN 證據查詢尚未啟用：需在 Development 設定受控測試憑證。此設定須與後端一致。");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"api/exceptions/{Uri.EscapeDataString(exceptionId)}/evidence");
            request.Headers.Add("X-Mrn-Review-Credential", credential);
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new(null, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.NotFound => "無法讀取證據：請確認後端測試憑證、審核身分及虛構病例允許清單。",
                    System.Net.HttpStatusCode.Conflict => "來源版本或候選雜湊不一致，請重新載入並確認病例來源。",
                    _ => "MRN 證據查詢暫時失敗，請稍後重試。"
                });
            var evidence = await response.Content.ReadFromJsonAsync<MrnEvidence>(JsonOptions);
            if (evidence is null || evidence.ExceptionId != exceptionId)
                return new(null, "後端未回傳有效證據。");
            if (evidence.CurrentCorrectionVersion is null or < 0)
                return new(evidence, "後端未提供有效修正版本，請更新後端並重新載入；目前無法儲存。");
            return new(evidence, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return new(null, "無法取得 MRN 比對內容，請確認後端連線後重試。");
        }
    }

    public async Task<MrnCorrectionSaveResult> SaveMrnCorrectionAsync(string exceptionId, MrnCorrectionRequest request)
    {
        var credential = configuration["MrnReview:TestCredential"];
        if (!environment.IsDevelopment() || !configuration.GetValue("MrnReview:Enabled", false) ||
            string.IsNullOrWhiteSpace(credential) || credential.Length < 32)
            return new(null, 404, "MRN 修正保存未啟用，請確認 Development 受控設定。");
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post,
                $"api/exceptions/{Uri.EscapeDataString(exceptionId)}/resolve");
            message.Headers.Add("X-Mrn-Review-Credential", credential);
            message.Content = JsonContent.Create(request, options: JsonOptions);
            using var response = await http.SendAsync(message);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return new(null, status, status switch
                {
                    400 => "修正內容或來源不符合規則，請檢查 MRN、原因、來源與說明。",
                    404 => "此病例無法處置，請確認憑證、允許清單與病例狀態。",
                    409 => "來源或修正版本已變更，請重新載入證據後再確認。",
                    501 => "後端尚未開放新版人工修正保存，請確認服務版本。",
                    _ => "保存結果尚未確認，請使用相同請求重試，勿另建修正。"
                }, status is not (400 or 404 or 409 or 501));
            var saved = await response.Content.ReadFromJsonAsync<MrnCorrectionResponse>(JsonOptions);
            if (saved is null || !saved.Success || saved.RequestId != request.RequestId ||
                saved.CorrectionVersion != (long)request.ExpectedCorrectionVersion + 1 ||
                saved.ExceptionStatus != "FIXED_PENDING_REVALIDATION" || saved.ValidationStatus != "NOT_RUN" ||
                saved.ProcessingStatus != "MRN_REVIEW_REQUIRED" || saved.CloudUploadAllowed || saved.ResumeQueued ||
                saved.SourceConfirmationRequired != (request.Reason == MrnCorrectionReason.DOCUMENT_CASE_MISMATCH))
                return new(null, status, "後端回應與保存契約不符；結果尚未確認，請保留原請求重試。", true);
            return new(saved, status, "人工修正已保存，等待重新驗證");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new(null, null, "保存結果尚未確認，請使用相同請求重試，勿另建修正。", true);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

    private sealed record ApiOperationResponse(
        bool Success,
        string Message);

    // 取得後端未結案異常
    public async Task<IReadOnlyList<ExceptionItem>>
        GetOpenExceptionsAsync()
    {
        var items = await http.GetFromJsonAsync<List<ExceptionItem>>(
            "api/exceptions",
            JsonOptions);

        return items ?? [];
    }

    // 儲存人工修正
    public async Task<OperationResult> ResolveAsync(
        string exceptionId,
        ExceptionResolution resolution,
        string actor)
    {
        if (resolution.Status == ExceptionStatus.Unresolvable)
        {
            return await MarkUnresolvableAsync(
                exceptionId,
                resolution.Note,
                actor);
        }

        var request = new
        {
            Resolution = resolution,
            Actor = actor
        };

        return await PostAsync(
            $"api/exceptions/{Uri.EscapeDataString(exceptionId)}/resolve",
            request);
    }

    // 標記無法處理
    public async Task<OperationResult> MarkUnresolvableAsync(
        string exceptionId,
        string note,
        string actor)
    {
        var request = new
        {
            Note = note,
            Actor = actor
        };

        return await PostAsync(
            $"api/exceptions/{Uri.EscapeDataString(exceptionId)}/unresolvable",
            request);
    }

    // 額外提供重新驗證功能
    public async Task<OperationResult> RevalidateAsync(
        string exceptionId,
        string actor)
    {
        return await PostAsync(
            $"api/exceptions/{Uri.EscapeDataString(exceptionId)}/revalidate",
            new { Actor = actor });
    }

    // 統一處理 POST API 回傳結果
    private async Task<OperationResult> PostAsync<T>(
        string url,
        T request)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                url,
                request,
                JsonOptions);

            var result = await response.Content
                .ReadFromJsonAsync<ApiOperationResponse>(
                    JsonOptions);

            if (result is null)
            {
                return OperationResult.Fail(
                    "後端未回傳有效的操作結果");
            }

            return response.IsSuccessStatusCode && result.Success
                ? OperationResult.Ok(result.Message)
                : OperationResult.Fail(result.Message);
        }
        catch (HttpRequestException ex)
        {
            return OperationResult.Fail(
                $"無法連線至異常佇列 API：{ex.Message}");
        }
        catch (JsonException)
        {
            return OperationResult.Fail(
                "後端回傳的 JSON 格式不正確");
        }
    }
}
