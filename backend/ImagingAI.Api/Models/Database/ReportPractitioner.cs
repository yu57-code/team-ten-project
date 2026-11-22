using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ReportPractitioner
{
    public long ReportPractitionerId { get; set; }

    public long ReportId { get; set; }

    public long PractitionerId { get; set; }

    public string? ParticipationRole { get; set; }

    public virtual Practitioner Practitioner { get; set; } = null!;

    public virtual ClinicalReport Report { get; set; } = null!;

}
