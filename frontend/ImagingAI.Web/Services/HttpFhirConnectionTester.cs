using System.Text.Json;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services;

/// <summary>
/// 真正的連線測試（不是模擬）：向 FHIR Server 讀取 CapabilityStatement（GET {BaseUrl}/metadata）。
/// 例如 HAPI FHIR JPA Server（Docker）預設為 http://localhost:8080/fhir。
/// </summary>
public sealed class HttpFhirConnectionTester(HttpClient http) : IFhirConnectionTester
{
    public async Task<FhirConnectionResult> TestAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(baseUrl?.Trim().TrimEnd('/') + "/metadata", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new FhirConnectionResult(false, "請輸入正確的 Base URL（http:// 或 https:// 開頭）");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/fhir+json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new FhirConnectionResult(false, $"連線失敗：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = json.RootElement;

            var resourceType = root.TryGetProperty("resourceType", out var rt) ? rt.GetString() : null;
            if (resourceType != "CapabilityStatement")
            {
                return new FhirConnectionResult(false, "有回應，但不是 FHIR CapabilityStatement，請確認 Base URL");
            }

            var version = root.TryGetProperty("fhirVersion", out var v) ? v.GetString() : "未知";
            return new FhirConnectionResult(true, $"連線成功・CapabilityStatement 已讀取（FHIR {version}）");
        }
        catch (OperationCanceledException)
        {
            return new FhirConnectionResult(false, "連線逾時（8 秒），請確認 FHIR Server 是否啟動");
        }
        catch (HttpRequestException ex)
        {
            return new FhirConnectionResult(false, $"無法連線：{ex.Message}");
        }
        catch (JsonException)
        {
            return new FhirConnectionResult(false, "有回應，但內容不是 JSON，請確認 Base URL");
        }
    }
}
