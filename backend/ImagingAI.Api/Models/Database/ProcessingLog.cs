using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ProcessingLog
{
    public long ProcessingLogId { get; set; }

    public long ReportId { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string Status { get; set; } = null!;

    public string? ModelVersion { get; set; }

    public string? ErrorMessage { get; set; }

    public virtual ClinicalReport Report { get; set; } = null!;

}
