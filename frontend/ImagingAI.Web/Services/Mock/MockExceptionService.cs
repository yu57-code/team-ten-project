using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

public sealed class MockExceptionService(MockDataStore store) : IExceptionService
{
    public Task<IReadOnlyList<ExceptionItem>> GetOpenExceptionsAsync()
    {
        lock (store.Sync)
        {
            IReadOnlyList<ExceptionItem> items = store.Exceptions
                .Where(e => e.Status is not (ExceptionStatus.Resolved or ExceptionStatus.Unresolvable))
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => MockDataStore.Clone(e)) // 回傳複本，送出前不會改到資料
                .ToList();
            return Task.FromResult(items);
        }
    }

    public Task<OperationResult> ResolveAsync(string exceptionId, ExceptionResolution resolution, string actor)
    {
        if (resolution.Status == ExceptionStatus.Unresolvable)
        {
            return MarkUnresolvableAsync(exceptionId, resolution.Note, actor);
        }

        if (string.IsNullOrWhiteSpace(resolution.Note))
        {
            return Task.FromResult(OperationResult.Fail("請填寫處理說明（會寫入稽核軌跡）"));
        }

        lock (store.Sync)
        {
            var item = store.Exceptions.FirstOrDefault(e => e.Id == exceptionId);
            if (item is null)
            {
                return Task.FromResult(OperationResult.Fail("找不到這筆異常"));
            }

            if (string.IsNullOrWhiteSpace(resolution.FixValue))
            {
                return Task.FromResult(OperationResult.Fail($"請填入「{item.FixLabel}」"));
            }

            item.FixValue = resolution.FixValue.Trim();
            item.ResolutionNote = resolution.Note.Trim();
            item.Status = ExceptionStatus.Resolved;

            // 模擬重新驗證通過
            var record = store.Cases.FirstOrDefault(c => c.CaseId == item.CaseId);
            string message;
            if (item.Category == ExceptionCategory.Fhir)
            {
                if (record is not null)
                {
                    record.Status = CaseStatus.Completed;
                    record.StatusLabel = "已完成";
                    record.StageIndex = Pipeline.Stages.Count;
                    record.StageState = StageState.Done;
                }
                store.WrittenToFhirToday++;
                message = "已修正並重新驗證，TW Core 驗證通過，已寫入 FHIR Server";
            }
            else
            {
                if (record is not null)
                {
                    if (item.Category == ExceptionCategory.MedicalRecordNumber)
                    {
                        var mrn = item.FixValue;
                        record.MaskedMrn = mrn.Length >= 6 && !mrn.Contains('•') ? $"{mrn[..4]}•••{mrn[^1]}" : mrn;
                    }
                    record.Status = CaseStatus.PendingReview;
                    record.StatusLabel = "待覆核";
                    record.StageIndex = Pipeline.ReviewStageIndex;
                    record.StageState = StageState.Waiting;
                    record.StageNotes.Clear();
                    record.StageNotes[Pipeline.ReviewStageIndex] = "異常已修正，等待人工覆核";
                }
                message = "已修正並重新驗證通過，病例已進入「待覆核」";
            }

            store.AddAudit(actor, AuditKind.Edit, "EXCEPTION_RESOLVE", item.CaseId, "—",
                $"異常佇列 {item.Id}（{ExceptionText.CategoryLabel(item.Category)}）：{item.ResolutionNote}",
                new { exception_status = "PENDING" },
                new { exception_status = "RESOLVED", fix_label = item.FixLabel, fix_value = item.FixValue, merge = resolution.MergeIntoExistingPatient });
            store.AddHistory(actor, HistoryAction.Exception, item.CaseId,
                $"{ExceptionText.CategoryLabel(item.Category)}：{item.ResolutionNote}",
                item.Category == ExceptionCategory.Fhir ? HistoryCaseState.WrittenToFhir : HistoryCaseState.PendingReview);

            return Task.FromResult(OperationResult.Ok(message));
        }
    }

    public Task<OperationResult> MarkUnresolvableAsync(string exceptionId, string note, string actor)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return Task.FromResult(OperationResult.Fail("請填寫無法處理的原因（會寫入稽核軌跡）"));
        }

        lock (store.Sync)
        {
            var item = store.Exceptions.FirstOrDefault(e => e.Id == exceptionId);
            if (item is null)
            {
                return Task.FromResult(OperationResult.Fail("找不到這筆異常"));
            }

            item.Status = ExceptionStatus.Unresolvable;
            item.ResolutionNote = note.Trim();

            var record = store.Cases.FirstOrDefault(c => c.CaseId == item.CaseId);
            if (record is not null)
            {
                record.Status = CaseStatus.Closed;
                record.StatusLabel = "已結案（未寫入）";
            }

            store.AddAudit(actor, AuditKind.Edit, "EXCEPTION_CLOSE", item.CaseId, "—",
                $"異常佇列 {item.Id} 標記為無法處理：{item.ResolutionNote}",
                new { exception_status = "PENDING" }, new { exception_status = "UNRESOLVABLE", reason = item.ResolutionNote });
            store.AddHistory(actor, HistoryAction.Unresolvable, item.CaseId, $"標記無法處理：{item.ResolutionNote}", HistoryCaseState.Closed);

            return Task.FromResult(OperationResult.Ok("已標記為無法處理並結案（不寫入 FHIR）"));
        }
    }
}
