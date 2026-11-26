using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class UserAccount
{
    public long UserId { get; set; }

    public string Username { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public long RoleId { get; set; }

    public long? SecondaryRoleId { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<AuditTrail> AuditTrails { get; set; } = new List<AuditTrail>();

    public virtual ICollection<CaseResult> CaseResults { get; set; } = new List<CaseResult>();

    public virtual ICollection<ClinicalReport> ClinicalReports { get; set; } = new List<ClinicalReport>();

    public virtual ICollection<ExceptionQueue> ExceptionQueues { get; set; } = new List<ExceptionQueue>();

    public virtual ICollection<ReviewRecord> ReviewRecords { get; set; } = new List<ReviewRecord>();

    public virtual Role Role { get; set; } = null!;

    public virtual Role? SecondaryRole { get; set; }

}
