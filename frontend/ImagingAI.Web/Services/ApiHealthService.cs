using System.Net.Http.Json;

namespace ImagingAI.Web.Services;

public class ApiHealthService
{
    private readonly HttpClient _httpClient;

    public ApiHealthService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ApiHealthResponse?> CheckHealthAsync()
    {
        using var response = await _httpClient.GetAsync("api/Health");

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<ApiHealthResponse>();
    }
}

public class ApiHealthResponse
{
    public string? Status { get; set; }

    public string? Message { get; set; }

    public string? Service { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
}