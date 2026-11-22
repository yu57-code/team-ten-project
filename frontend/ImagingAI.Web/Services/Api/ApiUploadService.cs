using System.Net.Http.Headers;
using System.Net.Http.Json;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Api;

public sealed class ApiUploadService : IUploadService
{
    private const long MaxFileSize = 20L * 1024 * 1024;

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

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var browserStream = file.BrowserFile.OpenReadStream(MaxFileSize, timeout.Token);
            using var stream = new MemoryStream();
            await browserStream.CopyToAsync(stream, timeout.Token);
            stream.Position = 0;
            using var fileContent = new StreamContent(stream);

            fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                Path.GetExtension(file.FileName).ToLowerInvariant() switch
                {
                    ".pdf" => "application/pdf",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => "application/octet-stream"
                });

            form.Add(
                fileContent,
                "file",
                file.FileName);

            form.Add(
                new StringContent(request.MedicalRecordNumber),
                "medicalRecordNumber");
            form.Add(
                new StringContent(request.ReportType),
                "reportType");

            using var response = await _httpClient.PostAsync(
                "api/upload",
                form, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var message = (int)response.StatusCode switch
                {
                    400 => "請確認病歷號、PDF 格式和報告類型。",
                    404 => "找不到上傳服務，請確認後端已用開發設定啟動。",
                    409 => "相同檔案的病歷關聯需確認。",
                    413 => "檔案超過上傳大小限制。",
                    503 => "後端暫時無法儲存病例，請稍後再試。",
                    _ => "後端無法完成上傳。"
                };
                throw new HttpRequestException(
                    $"上傳失敗（HTTP {(int)response.StatusCode}）。{message}",
                    null, response.StatusCode);
            }

            var result = await response.Content
                .ReadFromJsonAsync<UploadApiResponse>();

            if (result is null)
                throw new InvalidOperationException(
                    "後端沒有回傳上傳結果。");

            var isDuplicate = result.Duplicate
                || result.IsDuplicate
                || !string.IsNullOrWhiteSpace(result.DuplicateOf);

            var job = new UploadJob
            {
                CaseId = result.CaseId,
                FileName = result.FileName,
                SizeBytes = result.FileSize,
                Description = $"{result.FileSize / 1024d / 1024d:0.0} MB",
                State = isDuplicate ? UploadJobState.Duplicate : UploadJobState.Running,
                StageIndex = isDuplicate ? 1 : 0,
                Progress = 0,
                DuplicateOf = isDuplicate ? result.DuplicateOf ?? result.CaseId : null,
                DuplicateReportType = result.DuplicateReportType ?? result.ReportType,
                CreatedAt = DateTime.Now,
                FinishedAt = isDuplicate ? DateTime.Now : null
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
        public bool Duplicate { get; set; }
        public bool IsDuplicate { get; set; }
        public string? DuplicateOf { get; set; }
        public string? ReportType { get; set; }
        public string? DuplicateReportType { get; set; }
    }
}
