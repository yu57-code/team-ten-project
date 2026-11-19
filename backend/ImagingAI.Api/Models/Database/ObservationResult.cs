using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ObservationResult
{
    public long ObservationId { get; set; }

    public long ReportId { get; set; }

    public long? ResultId { get; set; }

    public string? TestCode { get; set; }

    public string TestName { get; set; } = null!;

    public decimal? ValueNumber { get; set; }

    public string? ValueText { get; set; }

    public string? UnitCode { get; set; }

    public decimal? ReferenceLow { get; set; }

    public decimal? ReferenceHigh { get; set; }

    public string? AbnormalFlag { get; set; }

    public DateTime? ObservedAt { get; set; }

    public string? SourceLocation { get; set; }

    public string? CodingSystem { get; set; }

    public virtual ClinicalReport Report { get; set; } = null!;

    public virtual CaseResult? Result { get; set; }

}
