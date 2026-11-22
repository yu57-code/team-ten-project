using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Api;

public enum UploadConflictType
{
    ExactDuplicate,          // 情境 A：原病例已存在且病歷號一致 — 重複上傳
    PatientIdMismatch,       // 情境 B：檔案內容相同，但輸入的病歷號與原紀錄不一致
    MissingPatientLinkRecord // 情境 C：原紀錄的病歷號關聯檔遺失導致無法比對
}

/// <summary>
/// 專門承載 HTTP 409 衝突細節的例外，供 ReviewerUpload.razor 渲染對應引導卡片
/// </summary>
public sealed class UploadConflictException : Exception
{
    public UploadConflictType ConflictType { get; }
    public string? ExistingCaseId { get; }
    public string? ExistingMedicalRecordNumber { get; }
    public DateTime? UploadedAt { get; }
    public string FileName { get; }

    public UploadConflictException(
        UploadConflictType conflictType,
        string fileName,
        string? existingCaseId,
        string? existingMedicalRecordNumber,
        DateTime? uploadedAt,
        string message)
        : base(message)
    {
        ConflictType = conflictType;
        FileName = fileName;
        ExistingCaseId = existingCaseId;
        ExistingMedicalRecordNumber = existingMedicalRecordNumber;
        UploadedAt = uploadedAt;
    }
}

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
        var normalizedInputMrn = request.MedicalRecordNumber?.Trim() ?? string.Empty;

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
                new StringContent(normalizedInputMrn),
                "medicalRecordNumber");
            form.Add(
                new StringContent(request.ReportType),
                "reportType");

            using var response = await _httpClient.PostAsync(
                "api/upload",
                form, timeout.Token);

            // 專門攔截 HTTP 409 Conflict 並區分三種情境
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var raw409 = await response.Content.ReadAsStringAsync(timeout.Token);
                throw BuildConflictException(raw409, file.FileName, normalizedInputMrn);
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = (int)response.StatusCode switch
                {
                    400 => "請確認病歷號、PDF 格式和報告類型。",
                    404 => "找不到上傳服務，請確認後端已用開發設定啟動。",
                    413 => "檔案超過上傳大小限制。",
                    503 => "後端暫時無法儲存病例，請稍後再試。",
                    _ => "後端無法完成上傳。"
                };
                throw new HttpRequestException(
                    $"上傳失敗（HTTP {(int)response.StatusCode}）。{message}",
                    null, response.StatusCode);
            }

            var result = await response.Content
                .ReadFromJsonAsync<UploadApiResponse>(cancellationToken: timeout.Token);

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
                CreatedAt = result.UploadedAt ?? result.CreatedAt ?? DateTime.Now,
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

    /// <summary>
    /// 解析後端 409 回應內容，分類為情境 A、情境 B 或情境 C
    /// </summary>
    private static UploadConflictException BuildConflictException(
        string rawContent,
        string fileName,
        string inputMrn)
    {
        var parsed = ParseConflictPayload(rawContent);
        var msg = parsed.Message ?? rawContent;

        // 情境 C：原紀錄的病歷號關聯檔遺失導致無法比對
        if (string.IsNullOrWhiteSpace(parsed.ExistingMrn) &&
            (msg.Contains("遺失") ||
             msg.Contains("不存在") ||
             msg.Contains("無法比對") ||
             msg.Contains("關聯") ||
             msg.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return new UploadConflictException(
                UploadConflictType.MissingPatientLinkRecord,
                fileName,
                parsed.CaseId,
                null,
                parsed.UploadedAt,
                "此檔案曾有未完成的殘留上傳紀錄，導致病歷號比對異常。");
        }

        // 情境 A：原病例存在且病歷號與這次輸入一致
        if (!string.IsNullOrWhiteSpace(parsed.ExistingMrn) &&
            string.Equals(parsed.ExistingMrn.Trim(), inputMrn, StringComparison.OrdinalIgnoreCase))
        {
            var dateText = parsed.UploadedAt?.ToString("yyyy/MM/dd") ?? "先前";
            var caseText = !string.IsNullOrWhiteSpace(parsed.CaseId) ? $"（病例 ID：{parsed.CaseId}）" : "";
            return new UploadConflictException(
                UploadConflictType.ExactDuplicate,
                fileName,
                parsed.CaseId,
                parsed.ExistingMrn,
                parsed.UploadedAt,
                $"此檔案已於 {dateText} 上傳過{caseText}。");
        }

        // 情境 B：檔案內容相同，但輸入的病歷號與原紀錄不一致
        var existingCaseLabel = !string.IsNullOrWhiteSpace(parsed.CaseId) ? $"（病例 ID：{parsed.CaseId}）" : "";
        return new UploadConflictException(
            UploadConflictType.PatientIdMismatch,
            fileName,
            parsed.CaseId,
            parsed.ExistingMrn,
            parsed.UploadedAt,
            $"系統偵測到此報告檔案與既有紀錄{existingCaseLabel}內容完全相同，但輸入的病歷號不符。");
    }

    private static (string? CaseId, string? ExistingMrn, DateTime? UploadedAt, string? Message) ParseConflictPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var caseId = TryGetStringOrNumber(root, "duplicateOf")
                      ?? TryGetStringOrNumber(root, "caseId")
                      ?? TryGetStringOrNumber(root, "existingCaseId")
                      ?? TryGetStringOrNumber(root, "reportId")
                      ?? TryGetStringOrNumber(root, "existingReportId");

            var existingMrn = TryGetStringOrNumber(root, "existingMedicalRecordNumber")
                           ?? TryGetStringOrNumber(root, "medicalRecordNumber")
                           ?? TryGetStringOrNumber(root, "existingPatientId")
                           ?? TryGetStringOrNumber(root, "patientId");

            DateTime? uploadedAt = null;
            var dateStr = TryGetStringOrNumber(root, "uploadedAt")
                       ?? TryGetStringOrNumber(root, "createdAt");
            if (DateTime.TryParse(dateStr, out var dt))
                uploadedAt = dt;

            var message = TryGetStringOrNumber(root, "message")
                       ?? TryGetStringOrNumber(root, "detail")
                       ?? TryGetStringOrNumber(root, "title");

            return (caseId, existingMrn, uploadedAt, message);
        }
        catch
        {
            // 若後端直接回傳純文字而非 JSON，將原文當作 message 解析
            return (null, null, null, json);
        }
    }

    private static string? TryGetStringOrNumber(JsonElement root, string propertyName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                _ => null
            };
        }

        return null;
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
        public DateTime? UploadedAt { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}