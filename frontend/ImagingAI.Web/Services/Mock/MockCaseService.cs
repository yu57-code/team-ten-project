using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

public sealed class MockCaseService(MockDataStore store) : ICaseService
{
    public Task<IReadOnlyList<CaseRecord>> GetCasesAsync()
    {
        lock (store.Sync)
        {
            IReadOnlyList<CaseRecord> result = store.Cases.OrderByDescending(c => c.UploadedAt).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<DashboardStats> GetDashboardStatsAsync(string currentUserName)
    {
        lock (store.Sync)
        {
            var processing = store.Cases.Where(c => c.Status == CaseStatus.Processing).ToList();
            var openExceptions = store.Exceptions
                .Where(e => e.Status is not (ExceptionStatus.Resolved or ExceptionStatus.Unresolvable))
                .ToList();

            var stats = new DashboardStats(
                PendingReview: store.Cases.Count(c => c.Status == CaseStatus.PendingReview),
                OpenExceptions: openExceptions.Count,
                ExceptionCategoryCount: openExceptions.Select(e => e.Category).Distinct().Count(),
                ExceptionsAssignedToMe: openExceptions.Count(e => e.Assignee == currentUserName),
                Processing: processing.Count,
                ProcessingLocal: processing.Count(c => c.StageIndex <= 2),
                ProcessingCloud: processing.Count(c => c.StageIndex >= 3),
                WrittenToFhirToday: store.WrittenToFhirToday);

            return Task.FromResult(stats);
        }
    }

    public Task<IReadOnlyList<string>> GetPendingReviewIdsAsync()
    {
        lock (store.Sync)
        {
            IReadOnlyList<string> ids = store.Cases
                .Where(c => c.Status == CaseStatus.PendingReview)
                .OrderByDescending(c => c.UploadedAt)
                .Select(c => c.CaseId)
                .ToList();
            return Task.FromResult(ids);
        }
    }

    public Task<CaseReviewDetail?> GetReviewAsync(string? caseId)
    {
        lock (store.Sync)
        {
            var record = caseId is null
                ? store.Cases.Where(c => c.Status == CaseStatus.PendingReview).OrderByDescending(c => c.UploadedAt).FirstOrDefault()
                : store.Cases.FirstOrDefault(c => string.Equals(c.CaseId, caseId, StringComparison.OrdinalIgnoreCase));

            if (record is null)
            {
                return Task.FromResult<CaseReviewDetail?>(null);
            }

            if (!store.Reviews.TryGetValue(record.CaseId, out var detail))
            {
                // 尚未有 AI 解析結果：只有空白欄位（串接 Web API 後由 structured_json 填入）
                detail = MockSeedData.EmptyReview(record.CaseId, record.MaskedMrn, record.UploadedAt,
                    $"B-{record.UploadedAt:MMdd}-01");
                store.Reviews[record.CaseId] = detail;
            }

            detail.Status = record.Status;

            // 套用「系統設定與備份」中的門檻與啟用中的報告類型
            detail.ConfidenceThreshold = store.Settings.AiConfidenceThreshold;
            var reportTypeField = detail.RequiredFields.FirstOrDefault(f => f.Key == "report_type");
            if (reportTypeField is not null)
            {
                reportTypeField.Options = store.ReportTypes.Where(r => r.IsActive).Select(r => r.NameZh).ToList();
            }

            return Task.FromResult<CaseReviewDetail?>(MockDataStore.Clone(detail));
        }
    }

    public Task<OperationResult> SaveReviewDraftAsync(CaseReviewDetail detail, string reviewer)
    {
        lock (store.Sync)
        {
            var saved = MockDataStore.Clone(detail);
            saved.Version = detail.Version + 1;
            store.Reviews[detail.CaseId] = saved;

            store.AddAudit(reviewer, AuditKind.Edit, "FIELD_EDIT", detail.CaseId, $"v{detail.Version} → v{saved.Version}",
                "儲存覆核草稿（欄位修正與勾選狀態）。",
                new { version = detail.Version }, new { version = saved.Version, comment = detail.Comment });
            store.AddHistory(reviewer, HistoryAction.Edit, detail.CaseId, "儲存覆核草稿", HistoryCaseState.PendingReview);

            return Task.FromResult(OperationResult.Ok($"已儲存草稿（資料版本 v{saved.Version}）"));
        }
    }

    public Task<OperationResult> SubmitReviewAsync(CaseReviewDetail detail, string reviewer)
    {
        lock (store.Sync)
        {
            var unconfirmed = detail.ImportantResults.Count(r => !r.Confirmed);
            if (unconfirmed > 0)
            {
                return Task.FromResult(OperationResult.Fail($"尚有 {unconfirmed} 項重要結果未確認"));
            }

            var record = store.Cases.FirstOrDefault(c => c.CaseId == detail.CaseId);
            if (record is null)
            {
                return Task.FromResult(OperationResult.Fail("找不到這筆病例"));
            }

            store.Reviews[detail.CaseId] = MockDataStore.Clone(detail);
            store.AddAudit(reviewer, AuditKind.Review, "REVIEW_SUBMIT", detail.CaseId, $"v{detail.Version}",
                $"{detail.ImportantResults.Count} 項重要結果全部勾選確認，送出覆核。",
                new { review_status = "PENDING" },
                new { review_status = "APPROVED", confirmed_results = detail.ImportantResults.Count, comment = detail.Comment });

            // 模擬 TW Core 驗證：檢查數值若有未對應標準編碼，FHIR 驗證失敗 → 轉入異常佇列
            var unmapped = detail.Observations.Where(o => !o.CodeMapped).ToList();
            if (unmapped.Count > 0)
            {
                record.Status = CaseStatus.Exception;
                record.StatusLabel = "FHIR 驗證異常";
                record.StageIndex = 6;
                record.StageState = StageState.Failed;
                record.StageNotes[6] = $"{unmapped[0].Item} 缺少標準編碼，不符 TW Core Profile";

                store.Exceptions.Insert(0, new ExceptionItem
                {
                    Id = store.NextExceptionId(),
                    CaseId = detail.CaseId,
                    Category = ExceptionCategory.Fhir,
                    Description = $"Observation（{unmapped[0].Item}）.code 缺少標準編碼，不符 TW Core Observation Profile。",
                    CreatedAt = store.Clock(),
                    Assignee = reviewer,
                    Status = ExceptionStatus.Pending,
                    FailedStep = 8,
                    RestartFrom = "FHIR 轉換與驗證",
                    FixLabel = "Observation 標準編碼",
                    Comparisons =
                    [
                        new($"Observation（{unmapped[0].Item}）.code", "（未對應）", "TW Core Observation Profile", "Code 錯誤", VerdictLevel.Fail),
                    ],
                });

                store.AddHistory(reviewer, HistoryAction.Review, detail.CaseId, "送出覆核，FHIR 驗證未通過", HistoryCaseState.ExceptionInProgress);
                return Task.FromResult(OperationResult.Fail("已送出審核，但 TW Core 驗證未通過，病例已轉入異常佇列"));
            }

            record.Status = CaseStatus.Completed;
            record.StatusLabel = "已完成";
            record.StageIndex = Pipeline.Stages.Count;
            record.StageState = StageState.Done;
            store.WrittenToFhirToday++;

            store.AddAudit("系統", AuditKind.FhirWrite, "FHIR_WRITE", detail.CaseId, $"v{detail.Version}",
                "TW Core 驗證通過，寫入 HAPI FHIR Server，ProcessingLog 更新為 Completed。",
                new { approval_status = "APPROVED", fhir_json = (string?)null },
                new { approval_status = "WRITTEN", fhir_json = "Bundle（4 resources）" });
            store.AddHistory(reviewer, HistoryAction.Review, detail.CaseId,
                $"確認 {detail.ImportantResults.Count} 項重要結果，送出覆核", HistoryCaseState.WrittenToFhir);

            return Task.FromResult(OperationResult.Ok("已送出審核，TW Core 驗證通過並寫入 FHIR Server"));
        }
    }

    public Task<OperationResult> ReturnToExceptionQueueAsync(string caseId, string reason, string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(OperationResult.Fail("請填寫退回原因"));
        }

        lock (store.Sync)
        {
            var record = store.Cases.FirstOrDefault(c => c.CaseId == caseId);
            if (record is null)
            {
                return Task.FromResult(OperationResult.Fail("找不到這筆病例"));
            }

            record.Status = CaseStatus.Exception;
            record.StatusLabel = "AI 解析結果異常";
            record.StageIndex = Pipeline.ReviewStageIndex;
            record.StageState = StageState.Failed;
            record.StageNotes[Pipeline.ReviewStageIndex] = $"覆核退回：{reason}";

            store.Exceptions.Insert(0, new ExceptionItem
            {
                Id = store.NextExceptionId(),
                CaseId = caseId,
                Category = ExceptionCategory.AiResult,
                Description = $"人工覆核退回：{reason}",
                CreatedAt = store.Clock(),
                Assignee = reviewer,
                Status = ExceptionStatus.Pending,
                FailedStep = 7,
                RestartFrom = "第二層：AI 解析結果自動驗證",
                FixLabel = "修正後的內容",
                Comparisons = [new("人工覆核", "退回", "符合醫療人員認定的結果", "需修正", VerdictLevel.Fail)],
            });

            store.AddAudit(reviewer, AuditKind.Review, "REVIEW_RETURN", caseId, "—", $"覆核退回異常佇列：{reason}",
                new { review_status = "PENDING" }, new { review_status = "RETURNED", reason });
            store.AddHistory(reviewer, HistoryAction.Exception, caseId, $"退回異常佇列：{reason}", HistoryCaseState.ExceptionInProgress);

            return Task.FromResult(OperationResult.Ok("已退回異常佇列"));
        }
    }

    public Task<string> ExportFhirBundleAsync(string caseId)
    {
        lock (store.Sync)
        {
            var record = store.Cases.FirstOrDefault(c => c.CaseId == caseId);
            store.Reviews.TryGetValue(caseId, out var detail);
            detail ??= MockSeedData.EmptyReview(caseId, record?.MaskedMrn ?? string.Empty, record?.UploadedAt ?? store.Clock(), string.Empty);

            string F(string key) => detail.RequiredFields.FirstOrDefault(f => f.Key == key)?.Value ?? string.Empty;

            var bundle = new
            {
                resourceType = "Bundle",
                type = "collection",
                meta = new { profile = new[] { "https://twcore.mohw.gov.tw/ig/twcore/StructureDefinition/Bundle-twcore" } },
                identifier = new { system = "urn:imaging-ai:case", value = caseId },
                timestamp = store.Clock().ToString("yyyy-MM-ddTHH:mm:ss+08:00"),
                entry = new object[]
                {
                    new { resource = new { resourceType = "Patient", id = $"pat-{caseId}", identifier = new[] { new { value = F("mrn") } } } },
                    new
                    {
                        resource = new
                        {
                            resourceType = "DiagnosticReport",
                            id = $"dr-{caseId}",
                            status = "final",
                            code = new { text = F("report_type") },
                            subject = new { reference = $"Patient/pat-{caseId}" },
                            effectiveDateTime = F("study_date"),
                            conclusion = F("case_summary"),
                        }
                    },
                }.Concat(detail.Observations.Select((o, i) => (object)new
                {
                    resource = new
                    {
                        resourceType = "Observation",
                        id = $"obs-{caseId}-{i + 1}",
                        status = "final",
                        code = new { text = o.Item },
                        subject = new { reference = $"Patient/pat-{caseId}" },
                        valueString = $"{o.Value} {o.Unit}".Trim(),
                    }
                })).ToArray(),
            };

            return Task.FromResult(MockDataStore.ToJson(bundle));
        }
    }
}
