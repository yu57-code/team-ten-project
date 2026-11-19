using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ResultEvidence
{
    public long EvidenceId { get; set; }

    public long ResultId { get; set; }

    public long? SectionId { get; set; }

    public string? EvidenceText { get; set; }

    public int? SourcePage { get; set; }

    public string? SourceBbox { get; set; }

    public virtual CaseResult Result { get; set; } = null!;

    public virtual ReportSection? Section { get; set; }

}
