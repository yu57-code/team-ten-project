using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class AuditTrail
{
    public long AuditId { get; set; }

    public long? ReportId { get; set; }

    public long ActorId { get; set; }

    public string Action { get; set; } = null!;

    public DateTime ActionAt { get; set; }

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    public int? DataVersion { get; set; }

    public virtual UserAccount Actor { get; set; } = null!;

    public virtual ClinicalReport? Report { get; set; }

}
