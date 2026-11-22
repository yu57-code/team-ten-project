
using System.ComponentModel.DataAnnotations;

namespace ImagingAI.Api.Models;

// 異常分類：對應前端的五種類型
public enum ExceptionCategory
{
    MedicalRecordNumber,
    ReportType,
    PreParse,
    AiResult,
    Fhir
}

// 異常處理狀態
public enum ExceptionStatus
{
    Pending,
    InProgress,
    FixedPendingRevalidation,
    Resolved,
    Unresolvable
}

// 驗證結果等級
public enum VerdictLevel
{
    Pass,
    Warning,
    Fail
}

// 異常詳細頁面的欄位比對結果
public sealed record ComparisonRow(
    string Field,
    string Actual,
    string Expected,
    string Verdict,
    VerdictLevel Level
);

// 一筆異常佇列資料
public sealed class ExceptionItem
{
    public required string Id { get; init; }

    public required string CaseId { get; init; }
    public string Code { get; init; } = string.Empty;

    public ExceptionCategory Category { get; init; }

    public required string Description { get; init; }

    public DateTime CreatedAt { get; init; }

    public string Assignee { get; set; } = string.Empty;

    public ExceptionStatus Status { get; set; }

    public int FailedStep { get; init; }

    public required string RestartFrom { get; init; }

    public required string FixLabel { get; init; }

    public string FixValue { get; set; } = string.Empty;

    public bool AllowMerge { get; init; }

    public List<ComparisonRow> Comparisons { get; init; } = [];

    public string? ResolutionNote { get; set; }
}

// 人工修正異常時送出的資料
public sealed record ExceptionResolution(
    string FixValue,
    ExceptionStatus Status,
    bool MergeIntoExistingPatient,
    string Note
);

// 提交人工修正的 API Request
public sealed class ResolveExceptionRequest
{
    [Required]
    public required ExceptionResolution Resolution { get; init; }

    [Required]
    public string Actor { get; init; } = string.Empty;
}

// 標記無法處理的 API Request
public sealed class MarkUnresolvableRequest
{
    [Required]
    public string Note { get; init; } = string.Empty;

    [Required]
    public string Actor { get; init; } = string.Empty;
}

// 重新驗證的 API Request
public sealed class RevalidateExceptionRequest
{
    [Required]
    public string Actor { get; init; } = string.Empty;
}

// API 操作結果
public sealed record ExceptionOperationResponse(
    bool Success,
    string Message
);
