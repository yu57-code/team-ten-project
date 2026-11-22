using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class Practitioner
{
    public long PractitionerId { get; set; }

    public string? PractitionerName { get; set; }

    public string? RoleName { get; set; }

    public virtual ICollection<ReportPractitioner> ReportPractitioners { get; set; } = new List<ReportPractitioner>();

}
