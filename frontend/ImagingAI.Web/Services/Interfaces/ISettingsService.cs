using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>系統設定、報告類型與資料備份。</summary>
public interface ISettingsService
{
    /// <summary>目前的系統設定（回傳複本，修改後需呼叫 SaveSettingsAsync）。</summary>
    Task<SystemSettings> GetSettingsAsync();

    Task<OperationResult> SaveSettingsAsync(SystemSettings settings, string actor);

    Task<IReadOnlyList<ReportTypeConfig>> GetReportTypesAsync();

    Task<OperationResult> AddReportTypeAsync(ReportTypeConfig reportType, string actor);

    Task<OperationResult> SetReportTypeActiveAsync(string typeCode, bool isActive, string actor);

    Task<IReadOnlyList<BackupRecord>> GetBackupsAsync();

    Task<OperationResult> CreateBackupAsync(string actor);

    Task<OperationResult> RestoreBackupAsync(string backupId, string actor);
}

/// <summary>測試 FHIR Server 連線（讀取 {BaseUrl}/metadata）。</summary>
public interface IFhirConnectionTester
{
    Task<FhirConnectionResult> TestAsync(string baseUrl, CancellationToken cancellationToken = default);
}
