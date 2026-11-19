using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class CaseResult
{
    public long ResultId { get; set; }

    public long ReportId { get; set; }

    public string ResultCategory { get; set; } = null!;

    public string ResultName { get; set; } = null!;

    public string? ResultText { get; set; }

    public string ResultStatus { get; set; } = null!;

    public string? ClinicalSignificance { get; set; }

    public int? ResultOrder { get; set; }

    public string? SourceLocation { get; set; }

    public decimal? ConfidenceScore { get; set; }

    public long? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public virtual ICollection<ObservationResult> ObservationResults { get; set; } = new List<ObservationResult>();

    public virtual ClinicalReport Report { get; set; } = null!;

    public virtual ICollection<ResultEvidence> ResultEvidences { get; set; } = new List<ResultEvidence>();

    public virtual UserAccount? ReviewedByNavigation { get; set; }

}
