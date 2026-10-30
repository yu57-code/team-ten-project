using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Interfaces;

/// <summary>提供側邊選單上的數字徽章（待覆核、異常佇列、失敗任務）。</summary>
public interface INavBadgeService
{
    Task<ReviewerNavCounts> GetReviewerCountsAsync();

    Task<AdminNavCounts> GetAdminCountsAsync();
}
