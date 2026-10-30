using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

/// <summary>
/// 系統監控：統計數字由模擬資料庫中「實際操作產生的資料」計算，沒有預設數字。
/// 服務狀態：除了 FHIR Server（設定 Base URL 後可真正測試連線）以外，其餘服務尚未串接，顯示「尚未串接」。
/// </summary>
public sealed class MockSystemMonitorService(MockDataStore store, IFhirConnectionTester fhirTester) : ISystemMonitorService
{
    /// <summary>要監控的服務（系統架構設定，不是模擬資料）。</summary>
    private static readonly (string Key, string Name, string Description)[] Services =
    [
        ("ocr", "地端 OCR", "PaddleOCR／Qwen2.5-VL・院內主機"),
        ("presidio", "去識別化", "Presidio・院內主機"),
        ("llm", "雲端 AI Agent", "LLM 解析（僅去識別化文字）"),
        ("fhir", "HAPI FHIR Server", "TW Core IG・Docker"),
        ("mfa", "MFA 驗證服務", "單一入口登入"),
        ("db", "資料庫", "病例・稽核軌跡・佇列"),
    ];

    public Task<MonitorStats> GetStatsAsync()
    {
        lock (store.Sync)
        {
            var today = DateTime.Today;
            var todayCases = store.Cases.Where(c => c.UploadedAt >= today).ToList();
            var finishedJobs = store.UploadJobs
                .Where(j => j.State == UploadJobState.PendingReview && j.FinishedAt is not null)
                .ToList();

            TimeSpan? average = finishedJobs.Count == 0
                ? null
                : TimeSpan.FromSeconds(finishedJobs.Average(j => (j.FinishedAt!.Value - j.CreatedAt).TotalSeconds));

            return Task.FromResult(new MonitorStats(
                TodayCases: todayCases.Count,
                TodayCompleted: todayCases.Count(c => c.Status == CaseStatus.Completed),
                TodayProcessing: todayCases.Count(c => c.Status == CaseStatus.Processing),
                TodayExceptions: todayCases.Count(c => c.Status == CaseStatus.Exception),
                FailedTasks: store.FailedTasks.Count(t => t.State == FailedTaskState.Failed),
                AverageProcessingTime: average,
                DuplicateHits: store.UploadJobs.Count(j => j.State == UploadJobState.Duplicate && j.CreatedAt >= today)));
        }
    }

    public Task<IReadOnlyList<ServiceHealth>> GetServicesAsync()
    {
        lock (store.Sync)
        {
            IReadOnlyList<ServiceHealth> list = Services
                .Select(s => store.ServiceHealth.TryGetValue(s.Key, out var health)
                    ? health
                    : new ServiceHealth(s.Key, s.Name, s.Description, ServiceStatus.Unknown, "尚未檢查", null))
                .ToList();
            return Task.FromResult(list);
        }
    }

    public async Task<IReadOnlyList<ServiceHealth>> CheckServicesAsync()
    {
        string fhirUrl;
        lock (store.Sync)
        {
            fhirUrl = store.Settings.FhirBaseUrl;
        }

        var now = store.Clock();
        var results = new List<ServiceHealth>();

        foreach (var (key, name, description) in Services)
        {
            if (key == "fhir" && !string.IsNullOrWhiteSpace(fhirUrl))
            {
                var test = await fhirTester.TestAsync(fhirUrl);
                results.Add(new ServiceHealth(key, name, description,
                    test.Success ? ServiceStatus.Healthy : ServiceStatus.Down, test.Message, now));
            }
            else
            {
                var hint = key == "fhir" ? "請先到「系統設定與備份」填寫 FHIR Base URL" : "尚未串接後端，無法檢查";
                results.Add(new ServiceHealth(key, name, description, ServiceStatus.Unknown, hint, now));
            }
        }

        lock (store.Sync)
        {
            foreach (var health in results)
            {
                store.ServiceHealth[health.Key] = health;
            }
        }

        return results;
    }

    public Task<IReadOnlyList<FailedTask>> GetFailedTasksAsync()
    {
        lock (store.Sync)
        {
            IReadOnlyList<FailedTask> list = store.FailedTasks.OrderByDescending(t => t.StartedAt).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<OperationResult> RetryTasksAsync(IReadOnlyCollection<string> logIds, string actor)
    {
        if (logIds.Count == 0)
        {
            return Task.FromResult(OperationResult.Fail("請先選取要重新執行的任務"));
        }

        lock (store.Sync)
        {
            var tasks = store.FailedTasks.Where(t => logIds.Contains(t.LogId) && t.State == FailedTaskState.Failed).ToList();
            foreach (var task in tasks)
            {
                task.State = FailedTaskState.Requeued;
                store.AddAudit(actor, AuditKind.Maintenance, "TASK_RETRY", task.CaseId, "—",
                    $"重新執行失敗任務 {task.LogId}（{task.Stage}）。",
                    new { status = "FAILED", error = task.ErrorMessage }, new { status = "QUEUED" });
            }

            return Task.FromResult(OperationResult.Ok($"已將 {tasks.Count} 筆任務重新排入佇列"));
        }
    }
}
