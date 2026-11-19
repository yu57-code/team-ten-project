using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ReportType
{
    public long ReportTypeId { get; set; }

    public string TypeCode { get; set; } = null!;

    public string TypeNameZh { get; set; } = null!;

    public string? TypeNameEn { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<ClinicalReport> ClinicalReports { get; set; } = new List<ClinicalReport>();

}
