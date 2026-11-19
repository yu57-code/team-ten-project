using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ExceptionQueue
{
    public long ExceptionId { get; set; }

    public long ReportId { get; set; }

    public string ExceptionCategory { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public long? AssignedUserId { get; set; }

    public virtual UserAccount? AssignedUser { get; set; }

    public virtual ClinicalReport Report { get; set; } = null!;

}
