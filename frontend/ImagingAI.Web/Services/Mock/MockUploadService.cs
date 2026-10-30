using ImagingAI.Web.Models;
using ImagingAI.Web.Services.Interfaces;

namespace ImagingAI.Web.Services.Mock;

/// <summary>
/// 模擬上傳與前 5 步處理：每 0.35 秒推進 25%，約 7 秒完成。
/// 以檔名模擬 SHA-256 重複檢查（同檔名視為同一份檔案）。
/// </summary>
public sealed class MockUploadService(MockDataStore store) : IUploadService
{
    private const int StepDelayMs = 350;
    private const int ProgressPerTick = 25;

    private readonly HashSet<string> _running = [];

    public event Action<UploadJob>? JobChanged;

    public Task<IReadOnlyList<UploadJob>> GetRecentJobsAsync(string uploadedBy)
    {
        lock (store.Sync)
        {
            var mine = store.Cases.Where(c => c.UploadedBy == uploadedBy).Select(c => c.CaseId).ToHashSet();
            IReadOnlyList<UploadJob> jobs = store.UploadJobs
                .Where(j => mine.Contains(j.CaseId))
                .OrderByDescending(j => j.CreatedAt)
                .Take(8)
                .ToList();
            return Task.FromResult(jobs);
        }
    }

    public Task<IReadOnlyList<UploadJob>> StartAsync(UploadRequest request)
    {
        var created = new List<UploadJob>();
        var toRun = new List<UploadJob>();

        lock (store.Sync)
        {
            foreach (var file in request.Files)
            {
                var caseId = store.NextCaseId();
                var now = store.Clock();
                var mrn = Mask(request.MedicalRecordNumber);

                if (store.FileHashIndex.TryGetValue(file.FileName, out var existing))
                {
                    // SHA-256 已存在：直接回傳既有病例結果，不重複解析
                    var duplicate = new UploadJob
                    {
                        CaseId = caseId,
                        FileName = file.FileName,
                        SizeBytes = file.SizeBytes,
                        Description = FormatSize(file.SizeBytes),
                        State = UploadJobState.Duplicate,
                        StageIndex = 1,
                        DuplicateOf = existing,
                        CreatedAt = now,
                        FinishedAt = now,
                    };
                    store.UploadJobs.Insert(0, duplicate);
                    store.Cases.Add(new CaseRecord
                    {
                        CaseId = caseId,
                        MaskedMrn = mrn,
                        UploadedAt = now,
                        FileName = file.FileName,
                        UploadedBy = request.UploadedBy,
                        Status = CaseStatus.Duplicate,
                        StatusLabel = "重複病例",
                        StageIndex = 0,
                        StageState = StageState.Done,
                        DuplicateOf = existing,
                    });
                    store.AddAudit(request.UploadedBy, AuditKind.Upload, "UPLOAD", caseId, "—",
                        $"SHA-256 已存在（與 {existing} 相同），直接回傳既有結果，未重複解析。",
                        new { }, new { duplicate_of = existing });
                    store.AddHistory(request.UploadedBy, HistoryAction.Upload, caseId,
                        $"上傳 {file.FileName}（與 {existing} 重複）", HistoryCaseState.Duplicate);
                    created.Add(duplicate);
                    continue;
                }

                var job = new UploadJob
                {
                    CaseId = caseId,
                    FileName = file.FileName,
                    SizeBytes = file.SizeBytes,
                    Description = FormatSize(file.SizeBytes),
                    State = UploadJobState.Running,
                    StageIndex = 0,
                    Progress = 0,
                    CreatedAt = now,
                };
                store.UploadJobs.Insert(0, job);
                store.FileHashIndex[file.FileName] = caseId;
                store.Cases.Add(new CaseRecord
                {
                    CaseId = caseId,
                    MaskedMrn = mrn,
                    UploadedAt = now,
                    FileName = file.FileName,
                    UploadedBy = request.UploadedBy,
                    ReportType = request.ReportType == "自動辨識" ? "自動辨識中" : request.ReportType,
                    Status = CaseStatus.Processing,
                    StatusLabel = "處理中",
                    StageIndex = 0,
                    StageState = StageState.Running,
                });
                store.AddAudit(request.UploadedBy, AuditKind.Upload, "UPLOAD", caseId, "v0",
                    $"上傳 {file.FileName}，SHA-256 未重複，建立處理任務。",
                    new { }, new { file = file.FileName, processing_status = "QUEUED" });
                store.AddHistory(request.UploadedBy, HistoryAction.Upload, caseId, $"上傳 {file.FileName}", HistoryCaseState.Processing);

                created.Add(job);
                toRun.Add(job);
            }
        }

        foreach (var job in toRun)
        {
            StartSimulation(job);
        }

        return Task.FromResult<IReadOnlyList<UploadJob>>(created);
    }

    public Task ResumeAsync(string caseId)
    {
        UploadJob? job;
        lock (store.Sync)
        {
            job = store.UploadJobs.FirstOrDefault(j => j.CaseId == caseId && j.State == UploadJobState.Running);
        }

        if (job is not null)
        {
            StartSimulation(job);
        }

        return Task.CompletedTask;
    }

    private void StartSimulation(UploadJob job)
    {
        lock (_running)
        {
            if (!_running.Add(job.CaseId))
            {
                return; // 已經在跑
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(StepDelayMs);

                    lock (store.Sync)
                    {
                        job.Progress += ProgressPerTick;
                        if (job.Progress >= 100)
                        {
                            job.StageIndex++;
                            job.Progress = 0;
                        }

                        var record = store.Cases.FirstOrDefault(c => c.CaseId == job.CaseId);
                        if (record is not null)
                        {
                            record.StageIndex = job.StageIndex;
                            record.Progress = job.Progress;
                            record.StageNotes.Remove(record.StageIndex - 1);
                            if (job.StageIndex >= 3 && record.ReportType == "自動辨識中")
                            {
                                record.ReportType = "心導管檢查報告";
                            }
                        }

                        if (job.StageIndex >= Pipeline.UploadStageCount)
                        {
                            job.State = UploadJobState.PendingReview;
                            job.FinishedAt = store.Clock();
                            if (record is not null)
                            {
                                record.Status = CaseStatus.PendingReview;
                                record.StatusLabel = "待覆核";
                                record.StageIndex = Pipeline.ReviewStageIndex;
                                record.StageState = StageState.Waiting;
                                record.StageNotes[Pipeline.ReviewStageIndex] = "等待人工覆核";
                            }

                            store.AddAudit("系統", AuditKind.AiParse, "AI_PARSE", job.CaseId, "v1",
                                "AI Agent 解析完成，產生結構化 JSON（含信心值），第二層自動驗證通過。",
                                new { }, new { structured_json = "v1", model_version = "gpt-4o" });
                        }
                    }

                    JobChanged?.Invoke(job);

                    if (job.IsFinished)
                    {
                        break;
                    }
                }
            }
            finally
            {
                lock (_running)
                {
                    _running.Remove(job.CaseId);
                }
            }
        });
    }

    private static string Mask(string mrn)
    {
        mrn = mrn.Trim();
        return mrn.Length >= 6 ? $"{mrn[..4]}•••{mrn[^1]}" : mrn;
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}
