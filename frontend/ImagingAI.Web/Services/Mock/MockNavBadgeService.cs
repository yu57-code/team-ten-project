using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

/// <summary>側邊選單徽章：依模擬資料庫目前的內容計算（數字為 0 時不顯示徽章）。</summary>
public sealed class MockNavBadgeService(MockDataStore store) : INavBadgeService
{
    public Task<ReviewerNavCounts> GetReviewerCountsAsync()
    {
        lock (store.Sync)
        {
            var pending = store.Cases.Count(c => c.Status == CaseStatus.PendingReview);
            var exceptions = store.Exceptions.Count(e => e.Status is not (ExceptionStatus.Resolved or ExceptionStatus.Unresolvable));
            return Task.FromResult(new ReviewerNavCounts(pending, exceptions));
        }
    }

    public Task<AdminNavCounts> GetAdminCountsAsync()
    {
        lock (store.Sync)
        {
            return Task.FromResult(new AdminNavCounts(store.FailedTasks.Count(t => t.State == FailedTaskState.Failed)));
        }
    }
}
