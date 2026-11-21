using System;
using System.Collections.Generic;
using ImagingAI.Api.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace ImagingAI.Api.Data;

public partial class ImagingAiDbContext : DbContext
{
    public ImagingAiDbContext(DbContextOptions<ImagingAiDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AuditTrail> AuditTrails { get; set; }

    public virtual DbSet<CaseResult> CaseResults { get; set; }

    public virtual DbSet<ClinicalReport> ClinicalReports { get; set; }

    public virtual DbSet<ExceptionQueue> ExceptionQueues { get; set; }

    public virtual DbSet<ObservationResult> ObservationResults { get; set; }

    public virtual DbSet<Patient> Patients { get; set; }

    public virtual DbSet<Practitioner> Practitioners { get; set; }

    public virtual DbSet<ProcessingLog> ProcessingLogs { get; set; }

    public virtual DbSet<ReportPractitioner> ReportPractitioners { get; set; }

    public virtual DbSet<ReportSection> ReportSections { get; set; }

    public virtual DbSet<ReportType> ReportTypes { get; set; }

    public virtual DbSet<ResultEvidence> ResultEvidences { get; set; }

    public virtual DbSet<ReviewRecord> ReviewRecords { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<StructuredOutput> StructuredOutputs { get; set; }

    public virtual DbSet<UserAccount> UserAccounts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditTrail>(entity =>
        {
            entity.HasKey(e => e.AuditId).HasName("audit_trail_pkey");

            entity.ToTable("audit_trail", "system");

            entity.Property(e => e.AuditId).HasColumnName("audit_id");
            entity.Property(e => e.Action)
                .HasMaxLength(50)
                .HasColumnName("action");
            entity.Property(e => e.ActionAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("action_at");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.AfterJson).HasColumnName("after_json");
            entity.Property(e => e.BeforeJson).HasColumnName("before_json");
            entity.Property(e => e.DataVersion)
                .HasDefaultValue(1)
                .HasColumnName("data_version");
            entity.Property(e => e.ReportId).HasColumnName("report_id");

            entity.HasOne(d => d.Actor).WithMany(p => p.AuditTrails)
                .HasForeignKey(d => d.ActorId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_audit_trail_actor_id");

            entity.HasOne(d => d.Report).WithMany(p => p.AuditTrails)
                .HasForeignKey(d => d.ReportId)
                .HasConstraintName("fk_audit_trail_report_id");
        });

        modelBuilder.Entity<CaseResult>(entity =>
        {
            entity.HasKey(e => e.ResultId).HasName("case_result_pkey");

            entity.ToTable("case_result", "clinical");

            entity.Property(e => e.ResultId).HasColumnName("result_id");
            entity.Property(e => e.ClinicalSignificance)
                .HasMaxLength(30)
                .HasColumnName("clinical_significance");
            entity.Property(e => e.ConfidenceScore)
                .HasPrecision(5, 4)
                .HasColumnName("confidence_score");
            entity.Property(e => e.ReportId).HasColumnName("report_id");
            entity.Property(e => e.ResultCategory)
                .HasMaxLength(40)
                .HasColumnName("result_category");
            entity.Property(e => e.ResultName)
                .HasMaxLength(200)
                .HasColumnName("result_name");
            entity.Property(e => e.ResultOrder).HasColumnName("result_order");
            entity.Property(e => e.ResultStatus)
                .HasMaxLength(30)
                .HasColumnName("result_status");
            entity.Property(e => e.ResultText).HasColumnName("result_text");
            entity.Property(e => e.ReviewedAt)
                .HasColumnType("timestamp without time zone")
                .HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(e => e.SourceLocation)
                .HasMaxLength(300)
                .HasColumnName("source_location");

            entity.HasOne(d => d.Report).WithMany(p => p.CaseResults)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_case_result_report_id");

            entity.HasOne(d => d.ReviewedByNavigation).WithMany(p => p.CaseResults)
                .HasForeignKey(d => d.ReviewedBy)
                .HasConstraintName("fk_case_result_reviewed_by");
        });

        modelBuilder.Entity<ClinicalReport>(entity =>
        {
            entity.HasKey(e => e.ReportId).HasName("clinical_report_pkey");

            entity.ToTable("clinical_report", "clinical");

            entity.Property(e => e.ReportId).HasColumnName("report_id");
            entity.Property(e => e.CaseSummary).HasColumnName("case_summary");
            entity.Property(e => e.Comment).HasColumnName("comment");
            entity.Property(e => e.DeidStatus)
                .HasMaxLength(30)
                .HasDefaultValueSql("'PENDING'::character varying")
                .HasColumnName("deid_status");
            entity.Property(e => e.EncounterDate).HasColumnName("encounter_date");
            entity.Property(e => e.FileName)
                .HasMaxLength(255)
                .HasColumnName("file_name");
            entity.Property(e => e.IsDeleted)
                .HasDefaultValue(false)
                .HasColumnName("is_deleted");
            entity.Property(e => e.PatientId).HasColumnName("patient_id");
            entity.Property(e => e.PhysicianSpecialty)
                .HasMaxLength(100)
                .HasColumnName("physician_specialty");
            entity.Property(e => e.ProcessingStatus)
                .HasMaxLength(30)
                .HasDefaultValueSql("'UPLOADED'::character varying")
                .HasColumnName("processing_status");
            entity.Property(e => e.ReportDate).HasColumnName("report_date");
            entity.Property(e => e.ReportType)
                .HasMaxLength(50)
                .HasColumnName("report_type");
            entity.Property(e => e.ReportTypeId).HasColumnName("report_type_id");
            entity.Property(e => e.Sha256Hash)
                .HasMaxLength(64)
                .IsFixedLength()
                .HasColumnName("sha256_hash");
            entity.Property(e => e.StorageUri)
                .HasMaxLength(1000)
                .HasColumnName("storage_uri");
            entity.Property(e => e.StudyDate).HasColumnName("study_date");
            entity.Property(e => e.StudyNumber)
                .HasMaxLength(80)
                .HasColumnName("study_number");
            entity.Property(e => e.UploadedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("uploaded_at");
            entity.Property(e => e.UploadedBy).HasColumnName("uploaded_by");

            entity.HasOne(d => d.Patient).WithMany(p => p.ClinicalReports)
                .HasForeignKey(d => d.PatientId)
                .HasConstraintName("fk_clinical_report_patient_id");

            entity.HasOne(d => d.ReportTypeNavigation).WithMany(p => p.ClinicalReports)
                .HasForeignKey(d => d.ReportTypeId)
                .HasConstraintName("fk_clinical_report_report_type_id");

            entity.HasOne(d => d.UploadedByNavigation).WithMany(p => p.ClinicalReports)
                .HasForeignKey(d => d.UploadedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_clinical_report_uploaded_by");
        });

        modelBuilder.Entity<ExceptionQueue>(entity =>
        {
            entity.HasKey(e => e.ExceptionId).HasName("exception_queue_pkey");

            entity.ToTable("exception_queue", "system");

            entity.Property(e => e.ExceptionId).HasColumnName("exception_id");
            entity.Property(e => e.AssignedUserId).HasColumnName("assigned_user_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.ExceptionCategory)
                .HasMaxLength(80)
                .HasColumnName("exception_category");
            entity.Property(e => e.ReportId).HasColumnName("report_id");
            entity.Property(e => e.Status)
                .HasMaxLength(30)
                .HasColumnName("status");

            entity.HasOne(d => d.AssignedUser).WithMany(p => p.ExceptionQueues)
                .HasForeignKey(d => d.AssignedUserId)
                .HasConstraintName("fk_exception_queue_assigned_user_id");

            entity.HasOne(d => d.Report).WithMany(p => p.ExceptionQueues)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_exception_queue_report_id");
        });

        modelBuilder.Entity<ObservationResult>(entity =>
        {
            entity.HasKey(e => e.ObservationId).HasName("observation_result_pkey");

            entity.ToTable("observation_result", "clinical");

            entity.Property(e => e.ObservationId).HasColumnName("observation_id");
            entity.Property(e => e.AbnormalFlag)
                .HasMaxLength(30)
                .HasColumnName("abnormal_flag");
            entity.Property(e => e.CodingSystem)
                .HasMaxLength(100)
                .HasColumnName("coding_system");
            entity.Property(e => e.ObservedAt)
                .HasColumnType("timestamp without time zone")
                .HasColumnName("observed_at");
            entity.Property(e => e.ReferenceHigh)
                .HasPrecision(18, 6)
                .HasColumnName("reference_high");
            entity.Property(e => e.ReferenceLow)
                .HasPrecision(18, 6)
                .HasColumnName("reference_low");
            entity.Property(e => e.ReportId).HasColumnName("report_id");
            entity.Property(e => e.ResultId).HasColumnName("result_id");
            entity.Property(e => e.SourceLocation)
                .HasMaxLength(300)
                .HasColumnName("source_location");
            entity.Property(e => e.TestCode)
                .HasMaxLength(100)
                .HasColumnName("test_code");
            entity.Property(e => e.TestName)
                .HasMaxLength(200)
                .HasColumnName("test_name");
            entity.Property(e => e.UnitCode)
                .HasMaxLength(40)
                .HasColumnName("unit_code");
            entity.Property(e => e.ValueNumber)
                .HasPrecision(18, 6)
                .HasColumnName("value_number");
            entity.Property(e => e.ValueText)
                .HasMaxLength(2000)
                .HasColumnName("value_text");

            entity.HasOne(d => d.Report).WithMany(p => p.ObservationResults)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_observation_result_report_id");

            entity.HasOne(d => d.Result).WithMany(p => p.ObservationResults)
                .HasForeignKey(d => d.ResultId)
                .HasConstraintName("fk_observation_result_result_id");
        });

        modelBuilder.Entity<Patient>(entity =>
        {
            entity.HasKey(e => e.PatientId).HasName("patient_pkey");

            entity.ToTable("patient", "clinical");

            entity.HasIndex(e => e.Mrn, "patient_mrn_key").IsUnique();

            entity.Property(e => e.PatientId).HasColumnName("patient_id");
            entity.Property(e => e.BirthDate).HasColumnName("birth_date");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.Gender)
                .HasMaxLength(20)
                .HasColumnName("gender");
            entity.Property(e => e.Mrn)
                .HasMaxLength(50)
                .HasColumnName("mrn");
            entity.Property(e => e.NationalId)
                .HasMaxLength(20)
                .HasColumnName("national_id");
            entity.Property(e => e.PatientName)
                .HasMaxLength(100)
                .HasColumnName("patient_name");
        });

        modelBuilder.Entity<Practitioner>(entity =>
        {
            entity.HasKey(e => e.PractitionerId).HasName("practitioner_pkey");

            entity.ToTable("practitioner", "clinical");

            entity.Property(e => e.PractitionerId).HasColumnName("practitioner_id");
            entity.Property(e => e.PractitionerName)
                .HasMaxLength(100)
                .HasColumnName("practitioner_name");
            entity.Property(e => e.RoleName)
                .HasMaxLength(100)
                .HasColumnName("role_name");
        });

        modelBuilder.Entity<ProcessingLog>(entity =>
        {
            entity.HasKey(e => e.ProcessingLogId).HasName("processing_log_pkey");

            entity.ToTable("processing_log", "system");

            entity.Property(e => e.ProcessingLogId).HasColumnName("processing_log_id");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.FinishedAt)
                .HasColumnType("timestamp without time zone")
                .HasColumnName("finished_at");
            entity.Property(e => e.ModelVersion)
                .HasMaxLength(100)
                .HasColumnName("model_version");
            entity.Property(e => e.ReportId).HasColumnName("report_id");
            entity.Property(e => e.StartedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasMaxLength(30)
                .HasColumnName("status");

            entity.HasOne(d => d.Report).WithMany(p => p.ProcessingLogs)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_processing_log_report_id");
        });

        modelBuilder.Entity<ReportPractitioner>(entity =>
        {
            entity.HasKey(e => e.ReportPractitionerId).HasName("report_practitioner_pkey");

            entity.ToTable("report_practitioner", "clinical");

            entity.Property(e => e.ReportPractitionerId).HasColumnName("report_practitioner_id");
            entity.Property(e => e.ParticipationRole)
                .HasMaxLength(50)
                .HasColumnName("participation_role");
            entity.Property(e => e.PractitionerId).HasColumnName("practitioner_id");
            entity.Property(e => e.ReportId).HasColumnName("report_id");

            entity.HasOne(d => d.Practitioner).WithMany(p => p.ReportPractitioners)
                .HasForeignKey(d => d.PractitionerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_report_practitioner_practitioner_id");

            entity.HasOne(d => d.Report).WithMany(p => p.ReportPractitioners)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_report_practitioner_report_id");
        });

        modelBuilder.Entity<ReportSection>(entity =>
        {
            entity.HasKey(e => e.SectionId).HasName("report_section_pkey");

            entity.ToTable("report_section", "clinical");

            entity.Property(e => e.SectionId).HasColumnName("section_id");
            entity.Property(e => e.RawText).HasColumnName("raw_text");
            entity.Property(e => e.ReportId).HasColumnName("report_id");
            entity.Property(e => e.SectionName)
                .HasMaxLength(150)
                .HasColumnName("section_name");
            entity.Property(e => e.SectionOrder).HasColumnName("section_order");
            entity.Property(e => e.SourcePage).HasColumnName("source_page");

            entity.HasOne(d => d.Report).WithMany(p => p.ReportSections)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_report_section_report_id");
        });

        modelBuilder.Entity<ReportType>(entity =>
        {
            entity.HasKey(e => e.ReportTypeId).HasName("report_type_pkey");

            entity.ToTable("report_type", "clinical");

            entity.HasIndex(e => e.TypeCode, "report_type_type_code_key").IsUnique();

            entity.Property(e => e.ReportTypeId).HasColumnName("report_type_id");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.TypeCode)
                .HasMaxLength(60)
                .HasColumnName("type_code");
            entity.Property(e => e.TypeNameEn)
                .HasMaxLength(100)
                .HasColumnName("type_name_en");
            entity.Property(e => e.TypeNameZh)
                .HasMaxLength(100)
                .HasColumnName("type_name_zh");
        });

        modelBuilder.Entity<ResultEvidence>(entity =>
        {
            entity.HasKey(e => e.EvidenceId).HasName("result_evidence_pkey");

            entity.ToTable("result_evidence", "clinical");

            entity.Property(e => e.EvidenceId).HasColumnName("evidence_id");
            entity.Property(e => e.EvidenceText).HasColumnName("evidence_text");
            entity.Property(e => e.ResultId).HasColumnName("result_id");
            entity.Property(e => e.SectionId).HasColumnName("section_id");
            entity.Property(e => e.SourceBbox)
                .HasMaxLength(200)
                .HasColumnName("source_bbox");
            entity.Property(e => e.SourcePage).HasColumnName("source_page");

            entity.HasOne(d => d.Result).WithMany(p => p.ResultEvidences)
                .HasForeignKey(d => d.ResultId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_result_evidence_result_id");

            entity.HasOne(d => d.Section).WithMany(p => p.ResultEvidences)
                .HasForeignKey(d => d.SectionId)
                .HasConstraintName("fk_result_evidence_section_id");
        });

        modelBuilder.Entity<ReviewRecord>(entity =>
        {
            entity.HasKey(e => e.ReviewId).HasName("review_record_pkey");

            entity.ToTable("review_record", "system");

            entity.Property(e => e.ReviewId).HasColumnName("review_id");
            entity.Property(e => e.DataVersion)
                .HasDefaultValue(1)
                .HasColumnName("data_version");
            entity.Property(e => e.ReportId).HasColumnName("report_id");
            entity.Property(e => e.ReviewComment).HasColumnName("review_comment");
            entity.Property(e => e.ReviewStatus)
                .HasMaxLength(30)
                .HasColumnName("review_status");
            entity.Property(e => e.ReviewedAt)
                .HasColumnType("timestamp without time zone")
                .HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewerId).HasColumnName("reviewer_id");

            entity.HasOne(d => d.Report).WithMany(p => p.ReviewRecords)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_review_record_report_id");

            entity.HasOne(d => d.Reviewer).WithMany(p => p.ReviewRecords)
                .HasForeignKey(d => d.ReviewerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_review_record_reviewer_id");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.RoleId).HasName("role_pkey");

            entity.ToTable("role", "system");

            entity.HasIndex(e => e.RoleCode, "role_role_code_key").IsUnique();

            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.RoleCode)
                .HasMaxLength(50)
                .HasColumnName("role_code");
            entity.Property(e => e.RoleName)
                .HasMaxLength(100)
                .HasColumnName("role_name");
        });

        modelBuilder.Entity<StructuredOutput>(entity =>
        {
            entity.HasKey(e => e.OutputId).HasName("structured_output_pkey");

            entity.ToTable("structured_output", "system");

            entity.Property(e => e.OutputId).HasColumnName("output_id");
            entity.Property(e => e.ApprovalStatus)
                .HasMaxLength(30)
                .HasColumnName("approval_status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("created_at");
            entity.Property(e => e.FhirJson).HasColumnName("fhir_json");
            entity.Property(e => e.ReportId).HasColumnName("report_id");
            entity.Property(e => e.StructuredJson).HasColumnName("structured_json");
            entity.Property(e => e.VersionNo)
                .HasDefaultValue(1)
                .HasColumnName("version_no");

            entity.HasOne(d => d.Report).WithMany(p => p.StructuredOutputs)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_structured_output_report_id");
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("user_account_pkey");

            entity.ToTable("user_account", "system");

            entity.HasIndex(e => e.Username, "user_account_username_key").IsUnique();

            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnName("created_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.PasswordHash).HasColumnName("password_hash");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.Username)
                .HasMaxLength(100)
                .HasColumnName("username");

            entity.HasOne(d => d.Role).WithMany(p => p.UserAccounts)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_user_role");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
