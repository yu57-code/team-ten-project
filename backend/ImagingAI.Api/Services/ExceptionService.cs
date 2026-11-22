using System.Globalization;
using System.Text.RegularExpressions;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ImagingAI.Api.Services;

public sealed class ExceptionService(ImagingAiDbContext database)
{
    public async Task<IReadOnlyList<ExceptionItem>> GetOpenExceptionsAsync(CancellationToken token)
    {
        var rows = await database.ExceptionQueues.AsNoTracking()
            .Where(e => e.Status != "RESOLVED" && e.Status != "UNRESOLVABLE")
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.ExceptionId)
            .ToListAsync(token);
        return rows.Select(Map).ToList();
    }

    public async Task<ExceptionItem?> GetByIdAsync(long id, CancellationToken token)
    {
        var row = await database.ExceptionQueues.AsNoTracking()
            .SingleOrDefaultAsync(e => e.ExceptionId == id, token);
        return row is null ? null : Map(row);
    }

    public async Task<object> GetSummaryAsync(CancellationToken token)
    {
        var rows = await GetOpenExceptionsAsync(token);
        return new
        {
            Total = rows.Count,
            MedicalRecordNumber = rows.Count(e => e.Category == ExceptionCategory.MedicalRecordNumber),
            ReportType = rows.Count(e => e.Category == ExceptionCategory.ReportType),
            PreParse = rows.Count(e => e.Category == ExceptionCategory.PreParse),
            AiResult = rows.Count(e => e.Category == ExceptionCategory.AiResult),
            Fhir = rows.Count(e => e.Category == ExceptionCategory.Fhir)
        };
    }

    public static ExceptionCategory MapCategory(string code) => code switch
    {
        "MRN_MISSING" or "MRN_MISMATCH" or "MRN_AMBIGUOUS" or "MRN_INVALID_FORMAT"
            => ExceptionCategory.MedicalRecordNumber,
        "REPORT_TYPE_MISSING" or "REPORT_TYPE_UNKNOWN" or "REPORT_TYPE_AMBIGUOUS"
            => ExceptionCategory.ReportType,
        _ => ExceptionCategory.PreParse
    };

    public static ExceptionItem Map(Models.Database.ExceptionQueue row)
    {
        var code = Regex.IsMatch(row.ExceptionCategory, @"\A[A-Z][A-Z0-9_]{0,79}\z")
            ? row.ExceptionCategory : "UNKNOWN";
        var category = MapCategory(code);
        return new ExceptionItem
        {
            Id = row.ExceptionId.ToString(CultureInfo.InvariantCulture),
            CaseId = row.ReportId.ToString(CultureInfo.InvariantCulture),
            Code = code,
            Category = category,
            Description = SafeDescription(code),
            // PostgreSQL stores local wall time without a time-zone offset.
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Unspecified),
            Assignee = row.AssignedUserId?.ToString(CultureInfo.InvariantCulture) ?? "",
            Status = row.Status switch
            {
                "OPEN" => ExceptionStatus.Pending,
                "FIXED_PENDING_REVALIDATION" => ExceptionStatus.FixedPendingRevalidation,
                "IN_PROGRESS" => ExceptionStatus.InProgress,
                "RESOLVED" => ExceptionStatus.Resolved,
                "UNRESOLVABLE" => ExceptionStatus.Unresolvable,
                _ => ExceptionStatus.InProgress
            },
            FailedStep = category == ExceptionCategory.ReportType ? 3 : 4,
            RestartFrom = category == ExceptionCategory.ReportType ? "類型辨識" : "地端資料驗證",
            FixLabel = "唯讀異常資料",
            AllowMerge = false
        };
    }

    private static string SafeDescription(string code) => code switch
    {
        "MRN_MISSING" => "OCR 未辨識到病歷號碼，待確認。",
        "MRN_MISMATCH" => "上傳與 OCR 的病歷號碼不一致，待確認。",
        "MRN_AMBIGUOUS" => "OCR 辨識到多個病歷號碼，待確認。",
        "MRN_INVALID_FORMAT" => "OCR 病歷號碼格式異常，待確認。",
        "DEID_VERIFICATION_FAILED" => "去識別化流程或驗證未通過。",
        "OCR_OUTPUT_INVALID" => "OCR 結果缺失或格式異常。",
        "OCR_CANDIDATE_CONFLICT" => "同次 OCR 的候選版本不一致。",
        "FIELD_STORAGE_FAILED" => "候選資料保存未完成。",
        "UPLOAD_MRN_UNAVAILABLE" => "無法讀取上傳病歷號碼。",
        "OCR_FAILED" or "OCR_TIMEOUT" or "OCR_EXECUTION_FAILED" => "OCR 執行失敗或逾時。",
        "REPORT_TYPE_MISSING" or "REPORT_TYPE_UNKNOWN" or "REPORT_TYPE_AMBIGUOUS"
            => "報告類型缺失或無法確認。",
        _ => "地端處理異常，請依異常代碼查核。"
    };
}
