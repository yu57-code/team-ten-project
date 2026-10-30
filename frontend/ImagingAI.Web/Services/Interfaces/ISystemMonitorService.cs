using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>系統監控：統計、服務健康狀態與失敗任務。</summary>
public interface ISystemMonitorService
{
    Task<MonitorStats> GetStatsAsync();

    /// <summary>取得上次檢查的服務狀態（不會重新檢查）。</summary>
    Task<IReadOnlyList<ServiceHealth>> GetServicesAsync();

    /// <summary>重新檢查各服務的連線狀態。</summary>
    Task<IReadOnlyList<ServiceHealth>> CheckServicesAsync();

    /// <summary>失敗任務（新到舊）。</summary>
    Task<IReadOnlyList<FailedTask>> GetFailedTasksAsync();

    /// <summary>重新執行失敗任務，從失敗的階段接續。</summary>
    Task<OperationResult> RetryTasksAsync(IReadOnlyCollection<string> logIds, string actor);
}
