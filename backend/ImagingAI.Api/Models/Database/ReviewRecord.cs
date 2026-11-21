using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ReviewRecord
{
    public long ReviewId { get; set; }

    public long ReportId { get; set; }

    public long ReviewerId { get; set; }

    public string ReviewStatus { get; set; } = null!;

    public DateTime? ReviewedAt { get; set; }

    public string? ReviewComment { get; set; }

    public int DataVersion { get; set; }

    public virtual ClinicalReport Report { get; set; } = null!;

    public virtual UserAccount Reviewer { get; set; } = null!;

}
