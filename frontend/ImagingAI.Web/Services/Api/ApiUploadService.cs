
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Api;

public sealed class ApiUploadService : IUploadService
{
    private const long MaxFileSize = 20 * 1024 * 1024;

    private readonly HttpClient _httpClient;

    public ApiUploadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public event Action<UploadJob>? JobChanged;

    public Task<IReadOnlyList<UploadJob>> GetRecentJobsAsync(
        string uploadedBy)
    {
        // 待後端提供歷史紀錄 API
        return Task.FromResult<IReadOnlyList<UploadJob>>(
            Array.Empty<UploadJob>());
    }

    public async Task<IReadOnlyList<UploadJob>> StartAsync(
        UploadRequest request)
    {
        var jobs = new List<UploadJob>();

        foreach (var file in request.Files)
        {
            if (file.BrowserFile is null)
                throw new InvalidOperationException(
                    "找不到實際上傳的檔案內容。");

            using var form = new MultipartFormDataContent();

            using var stream = file.BrowserFile.OpenReadStream(
                MaxFileSize);

            using var fileContent = new StreamContent(stream);

            fileContent.Headers.ContentType =
                new MediaTypeHeaderValue("application/pdf");

            form.Add(
                fileContent,
                "file",
                file.FileName);

            form.Add(
                new StringContent(request.MedicalRecordNumber),
                "medicalRecordNumber");

            using var response = await _httpClient.PostAsync(
                "api/upload",
                form);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"上傳失敗 ({(int)response.StatusCode})：{error}");
            }

            var result = await response.Content
                .ReadFromJsonAsync<UploadApiResponse>();

            if (result is null)
                throw new InvalidOperationException(
                    "後端沒有回傳上傳結果。");

            var job = new UploadJob
            {
                CaseId = result.CaseId,
                FileName = result.FileName,
                SizeBytes = result.FileSize,
                Description = "PDF 已上傳",
                State = UploadJobState.Running,
                StageIndex = 0,
                Progress = 0,
                CreatedAt = DateTime.Now
            };

            jobs.Add(job);
            JobChanged?.Invoke(job);
        }

        return jobs;
    }

    public Task ResumeAsync(string caseId)
    {
        // 待後端提供處理進度查詢 API
        return Task.CompletedTask;
    }

    private sealed class UploadApiResponse
    {
        public string CaseId { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
    }
}
