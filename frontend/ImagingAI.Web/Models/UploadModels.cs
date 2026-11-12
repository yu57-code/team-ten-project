namespace ImagingAI.Web.Models;

public enum UploadJobState
{
    Running,
    PendingReview,
    Duplicate,
    Failed
}

/// <summary>上傳後建立的處理任務（前 5 步：上傳到結構化資料驗證）。</summary>
public sealed class UploadJob
{
    public required string CaseId { get; init; }

    public required string FileName { get; init; }

    public long SizeBytes { get; init; }

    public string Description { get; set; } = string.Empty;

    public UploadJobState State { get; set; }

    /// <summary>目前步驟（0–4），完成時為 5。</summary>
    public int StageIndex { get; set; }

    public int Progress { get; set; }

    public string? DuplicateOf { get; set; }

    public DateTime CreatedAt { get; init; }

    /// <summary>前 5 步處理完成（或判定重複）的時間，用來計算平均處理時間。</summary>
    public DateTime? FinishedAt { get; set; }

    public bool IsFinished => State != UploadJobState.Running;
}

/// <summary>要上傳的一個檔案。Content 由頁面讀取後提供（Mock 只用來計算雜湊）。</summary>
public sealed record UploadFile(string FileName, long SizeBytes, string ContentType);

/// <summary>上傳表單送出的內容。</summary>
public sealed record UploadRequest(IReadOnlyList<UploadFile> Files, string MedicalRecordNumber, string ReportType, string UploadedBy);
