using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ReportSection
{
    public long SectionId { get; set; }

    public long ReportId { get; set; }

    public string SectionName { get; set; } = null!;

    public string? RawText { get; set; }

    public int? SectionOrder { get; set; }

    public int? SourcePage { get; set; }

    public virtual ClinicalReport Report { get; set; } = null!;

    public virtual ICollection<ResultEvidence> ResultEvidences { get; set; } = new List<ResultEvidence>();

}
