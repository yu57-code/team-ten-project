using System.Text.RegularExpressions;
using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

public sealed partial class MockSettingsService(MockDataStore store) : ISettingsService
{
    [GeneratedRegex("^[A-Z0-9_]{2,20}$")]
    private static partial Regex TypeCodePattern();

    public Task<SystemSettings> GetSettingsAsync()
    {
        lock (store.Sync)
        {
            return Task.FromResult(MockDataStore.Clone(store.Settings));
        }
    }

    public Task<OperationResult> SaveSettingsAsync(SystemSettings settings, string actor)
    {
        if (settings.AiConfidenceThreshold is < 0.5 or > 0.99)
        {
            return Task.FromResult(OperationResult.Fail("AI 欄位信心值門檻需介於 0.50–0.99"));
        }

        if (settings.OcrQualityThreshold is < 0.3 or > 0.95)
        {
            return Task.FromResult(OperationResult.Fail("OCR 文字品質門檻需介於 0.30–0.95"));
        }

        if (!string.IsNullOrWhiteSpace(settings.FhirBaseUrl)
            && !Uri.TryCreate(settings.FhirBaseUrl.Trim(), UriKind.Absolute, out _))
        {
            return Task.FromResult(OperationResult.Fail("FHIR Base URL 格式不正確"));
        }

        lock (store.Sync)
        {
            var before = MockDataStore.Clone(store.Settings);
            store.Settings = MockDataStore.Clone(settings);
            store.Settings.FhirBaseUrl = store.Settings.FhirBaseUrl.Trim();

            store.AddAudit(actor, AuditKind.SettingsChange, "SETTINGS_CHANGE", null, "—", "更新系統設定。", before, store.Settings);
            return Task.FromResult(OperationResult.Ok("已儲存系統設定"));
        }
    }

    public Task<IReadOnlyList<ReportTypeConfig>> GetReportTypesAsync()
    {
        lock (store.Sync)
        {
            IReadOnlyList<ReportTypeConfig> list = store.ReportTypes.Select(r => MockDataStore.Clone(r)).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<OperationResult> AddReportTypeAsync(ReportTypeConfig reportType, string actor)
    {
        var code = reportType.TypeCode.Trim().ToUpperInvariant();
        if (!TypeCodePattern().IsMatch(code))
        {
            return Task.FromResult(OperationResult.Fail("type_code 需為 2–20 個大寫英數字或底線"));
        }

        if (string.IsNullOrWhiteSpace(reportType.NameZh))
        {
            return Task.FromResult(OperationResult.Fail("請輸入中文名稱"));
        }

        lock (store.Sync)
        {
            if (store.ReportTypes.Any(r => r.TypeCode == code))
            {
                return Task.FromResult(OperationResult.Fail($"type_code {code} 已存在"));
            }

            var created = new ReportTypeConfig
            {
                TypeCode = code,
                NameZh = reportType.NameZh.Trim(),
                NameEn = reportType.NameEn.Trim(),
                IsActive = reportType.IsActive,
            };
            store.ReportTypes.Add(created);
            store.AddAudit(actor, AuditKind.SettingsChange, "REPORT_TYPE_CREATE", null, "—",
                $"新增報告類型 {code}（{created.NameZh}）。", new { }, created);

            return Task.FromResult(OperationResult.Ok($"已新增報告類型 {code}"));
        }
    }

    public Task<OperationResult> SetReportTypeActiveAsync(string typeCode, bool isActive, string actor)
    {
        lock (store.Sync)
        {
            var type = store.ReportTypes.FirstOrDefault(r => r.TypeCode == typeCode);
            if (type is null)
            {
                return Task.FromResult(OperationResult.Fail("找不到這個報告類型"));
            }

            type.IsActive = isActive;
            store.AddAudit(actor, AuditKind.SettingsChange, "REPORT_TYPE_CHANGE", null, "—",
                $"{(isActive ? "啟用" : "停用")}報告類型 {typeCode}。",
                new { type_code = typeCode, is_active = !isActive }, new { type_code = typeCode, is_active = isActive });

            return Task.FromResult(OperationResult.Ok($"已{(isActive ? "啟用" : "停用")} {typeCode}"));
        }
    }

    public Task<IReadOnlyList<BackupRecord>> GetBackupsAsync()
    {
        lock (store.Sync)
        {
            IReadOnlyList<BackupRecord> list = store.Backups.OrderByDescending(b => b.CreatedAt).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<OperationResult> CreateBackupAsync(string actor)
    {
        lock (store.Sync)
        {
            // 前端原型：記錄一筆「手動備份」，實際備份需由後端執行（pg_dump 等）
            var record = new BackupRecord(store.NextBackupId(), store.Clock(), "手動", "—", BackupStatus.Completed,
                $"前端模擬：{store.Cases.Count} 筆病例、{store.AuditEntries.Count} 筆稽核軌跡");
            store.Backups.Add(record);
            store.AddAudit(actor, AuditKind.Maintenance, "BACKUP_CREATE", null, "—", $"建立手動備份 {record.Id}。",
                new { }, new { backup_id = record.Id });

            return Task.FromResult(OperationResult.Ok($"已建立備份 {record.Id}（前端模擬，尚未串接後端）"));
        }
    }

    public Task<OperationResult> RestoreBackupAsync(string backupId, string actor)
    {
        lock (store.Sync)
        {
            if (store.Backups.All(b => b.Id != backupId))
            {
                return Task.FromResult(OperationResult.Fail("找不到這個備份"));
            }

            store.AddAudit(actor, AuditKind.Maintenance, "BACKUP_RESTORE_REQUEST", null, "—",
                $"要求從備份 {backupId} 還原（前端模擬，未實際還原）。", new { }, new { backup_id = backupId });

            return Task.FromResult(OperationResult.Fail("還原需由後端執行，目前尚未串接；已記錄這次的還原要求"));
        }
    }
}
