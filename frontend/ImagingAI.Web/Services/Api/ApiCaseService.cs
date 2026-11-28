using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;
using ImagingAI.Web.Services.Mock;

namespace ImagingAI.Web.Services.Api;

public sealed class ApiCaseService(HttpClient http, MockCaseService mockFallback) : ICaseService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // 總覽頁統計與列表暫維持 Mock，不影響其他頁面運作
    public Task<IReadOnlyList<CaseRecord>> GetCasesAsync() => mockFallback.GetCasesAsync();

    public Task<DashboardStats> GetDashboardStatsAsync(string currentUserName) =>
        mockFallback.GetDashboardStatsAsync(currentUserName);

    public async Task<IReadOnlyList<string>> GetPendingReviewIdsAsync()
    {
        var ids = await http.GetFromJsonAsync<List<string>>("api/reviews/pending-ids", JsonOptions);
        return ids ?? [];
    }

    public async Task<CaseReviewDetail?> GetReviewAsync(string? caseId)
    {
        var url = string.IsNullOrWhiteSpace(caseId)
            ? "api/reviews/next"
            : $"api/reviews/{Uri.EscapeDataString(caseId)}";

        using var response = await http.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CaseReviewDetail>(JsonOptions);
    }

    public async Task<OperationResult> SaveReviewDraftAsync(CaseReviewDetail detail, string reviewer)
    {
        var payload = new { Detail = detail, ReviewerName = reviewer };
        using var response = await http.PutAsJsonAsync(
            $"api/reviews/{Uri.EscapeDataString(detail.CaseId)}/draft",
            payload,
            JsonOptions);

        return await ReadOperationResultAsync(response);
    }

    public async Task<OperationResult> SubmitReviewAsync(CaseReviewDetail detail, string reviewer)
    {
        var payload = new { Detail = detail, ReviewerName = reviewer };
        using var response = await http.PostAsJsonAsync(
            $"api/reviews/{Uri.EscapeDataString(detail.CaseId)}/submit",
            payload,
            JsonOptions);

        return await ReadOperationResultAsync(response);
    }

    public async Task<OperationResult> ReturnToExceptionQueueAsync(string caseId, string reason, string reviewer)
    {
        var payload = new { Reason = reason, ReviewerName = reviewer };
        using var response = await http.PostAsJsonAsync(
            $"api/reviews/{Uri.EscapeDataString(caseId)}/return",
            payload,
            JsonOptions);

        return await ReadOperationResultAsync(response);
    }

    public async Task<string> ExportFhirBundleAsync(string caseId)
    {
        using var response = await http.GetAsync($"api/reviews/{Uri.EscapeDataString(caseId)}/fhir");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "{}";
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<OperationResult> ReadOperationResultAsync(HttpResponseMessage response)
    {
        var result = await response.Content.ReadFromJsonAsync<OperationResult>(JsonOptions);
        return result ?? new OperationResult(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "操作成功" : "操作失敗");
    }
}