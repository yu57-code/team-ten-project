using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class Patient
{
    public long PatientId { get; set; }

    public string? Mrn { get; set; }

    public string? NationalId { get; set; }

    public string? PatientName { get; set; }

    public string? Gender { get; set; }

    public DateOnly? BirthDate { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<ClinicalReport> ClinicalReports { get; set; } = new List<ClinicalReport>();

}
