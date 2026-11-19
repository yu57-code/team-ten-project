using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class StructuredOutput
{
    public long OutputId { get; set; }

    public long ReportId { get; set; }

    public int VersionNo { get; set; }

    public string StructuredJson { get; set; } = null!;

    public string? FhirJson { get; set; }

    public string ApprovalStatus { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual ClinicalReport Report { get; set; } = null!;

}
