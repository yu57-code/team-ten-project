using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class ClinicalReport
{
    public long ReportId { get; set; }

    public long? PatientId { get; set; }

    public long UploadedBy { get; set; }

    public string ReportType { get; set; } = null!;

    public string FileName { get; set; } = null!;

    public string Sha256Hash { get; set; } = null!;

    public string StorageUri { get; set; } = null!;

    public DateTime UploadedAt { get; set; }

    public DateOnly? ReportDate { get; set; }

    public DateOnly? EncounterDate { get; set; }

    public DateOnly? StudyDate { get; set; }

    public string? StudyNumber { get; set; }

    public string? PhysicianSpecialty { get; set; }

    public string? Comment { get; set; }

    public string ProcessingStatus { get; set; } = null!;

    public long? ReportTypeId { get; set; }

    public bool IsDeleted { get; set; }

    public string DeidStatus { get; set; } = null!;

    public string? CaseSummary { get; set; }

    public virtual ICollection<AuditTrail> AuditTrails { get; set; } = new List<AuditTrail>();

    public virtual ICollection<CaseResult> CaseResults { get; set; } = new List<CaseResult>();

    public virtual ICollection<ExceptionQueue> ExceptionQueues { get; set; } = new List<ExceptionQueue>();

    public virtual ICollection<ObservationResult> ObservationResults { get; set; } = new List<ObservationResult>();

    public virtual Patient? Patient { get; set; }

    public virtual ICollection<ProcessingLog> ProcessingLogs { get; set; } = new List<ProcessingLog>();

    public virtual ICollection<ReportPractitioner> ReportPractitioners { get; set; } = new List<ReportPractitioner>();

    public virtual ICollection<ReportSection> ReportSections { get; set; } = new List<ReportSection>();

    public virtual ReportType? ReportTypeNavigation { get; set; }

    public virtual ICollection<ReviewRecord> ReviewRecords { get; set; } = new List<ReviewRecord>();

    public virtual ICollection<StructuredOutput> StructuredOutputs { get; set; } = new List<StructuredOutput>();

    public virtual UserAccount UploadedByNavigation { get; set; } = null!;

}
