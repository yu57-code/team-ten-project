
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Api;

public sealed class ApiExceptionService(HttpClient http)
    : IExceptionService
{
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
