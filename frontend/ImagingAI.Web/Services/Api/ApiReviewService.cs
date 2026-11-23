
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Api;

public sealed class ApiReviewService(
    HttpClient httpClient) : IReviewApiService
{
    public async Task<IReadOnlyList<ApiReviewDto>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var reviews = await httpClient
            .GetFromJsonAsync<List<ApiReviewDto>>(
                "api/reviews/pending",
                cancellationToken);

        if (reviews is null || reviews.Any(review => review is null))
            throw new JsonException("Expected a pending review array containing review objects.");

        return reviews;
    }

    public async Task<ApiReviewDto?> GetByIdAsync(
        long reportId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/reviews/{reportId.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<ApiReviewDto>(
                cancellationToken: cancellationToken)
            ?? throw new JsonException("Expected a review object.");
    }
}
