
namespace ImagingAI.Web.Models;

public sealed class ApiReviewDto
{
    public long ReportId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ProcessingStatus { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }

    public long? ReportTypeId { get; set; }

    public string? ReportType { get; set; }

    public string? DeidStatus { get; set; }
}
