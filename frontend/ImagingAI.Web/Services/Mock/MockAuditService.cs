using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

public sealed class MockAuditService(MockDataStore store) : IAuditService
{
    public DateTime Now => store.Clock();

    public Task<IReadOnlyList<AuditEntry>> GetEntriesAsync(AuditQuery query)
    {
        lock (store.Sync)
        {
            IEnumerable<AuditEntry> result = store.AuditEntries;

            if (query.CaseRelatedOnly)
            {
                result = result.Where(e => AuditKinds.CaseRelated.Contains(e.Kind));
            }

            if (query.Since is { } since)
            {
                result = result.Where(e => e.Time >= since);
            }

            result = query.Actor switch
            {
                AuditActorFilter.Me => result.Where(e => e.Actor == query.CurrentUserName),
                AuditActorFilter.Users => result.Where(e => e.Actor != "系統"),
                AuditActorFilter.System => result.Where(e => e.Actor == "系統"),
                _ => result,
            };

            if (query.Kind is { } kind)
            {
                result = result.Where(e => e.Kind == kind);
            }

            if (!string.IsNullOrWhiteSpace(query.CaseIdKeyword))
            {
                var keyword = query.CaseIdKeyword.Trim();
                result = result.Where(e => e.CaseId?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true);
            }

            IReadOnlyList<AuditEntry> list = result.OrderByDescending(e => e.Time).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync(string userName)
    {
        lock (store.Sync)
        {
            IReadOnlyList<HistoryRecord> list = store.History.TryGetValue(userName, out var records)
                ? records.OrderByDescending(r => r.Time).ToList()
                : [];
            return Task.FromResult(list);
        }
    }

    public Task<HistoryStats> GetHistoryStatsAsync(string userName, HistoryRange range)
    {
        var today = DateTime.Today;
        var since = range switch
        {
            HistoryRange.ThisMonth => new DateTime(today.Year, today.Month, 1),
            HistoryRange.Last90Days => today.AddDays(-90),
            _ => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), // 本週（週一起算）
        };

        lock (store.Sync)
        {
            var records = store.History.TryGetValue(userName, out var list)
                ? list.Where(r => r.Time >= since).ToList()
                : new List<HistoryRecord>();

            return Task.FromResult(new HistoryStats(
                Uploads: records.Count(r => r.Action == HistoryAction.Upload),
                Reviews: records.Count(r => r.Action == HistoryAction.Review),
                Exceptions: records.Count(r => r.Action is HistoryAction.Exception or HistoryAction.Unresolvable),
                Edits: records.Count(r => r.Action == HistoryAction.Edit)));
        }
    }
}
