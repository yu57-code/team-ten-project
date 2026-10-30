namespace ImagingAI.Web.Models;

/// <summary>審核人員側邊選單上的數字徽章。</summary>
/// <param name="PendingReview">待覆核病例數</param>
/// <param name="OpenExceptions">異常佇列待處理數</param>
public sealed record ReviewerNavCounts(int PendingReview, int OpenExceptions);

/// <summary>管理員側邊選單上的數字徽章。</summary>
/// <param name="FailedTasks">失敗任務數</param>
public sealed record AdminNavCounts(int FailedTasks);
