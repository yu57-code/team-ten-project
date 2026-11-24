using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

// Isolated state-machine tests. The repository is an in-memory transaction fake; these tests
// never create a PostgreSQL connection or mutate the developer's database.
internal static class MrnWorkflowIsolationTests
{
    public static async Task<(int Total, int Failed)> RunAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("revalidation_pass_queues_only_a_bound_resume_job", PassQueuesJob),
            ("document_case_mismatch_never_queues", DocumentMismatch),
            ("blocking_exception_and_patient_conflict_remain_blocked", BlockersAndPatientConflict),
            ("stale_version_hash_and_attempt_are_rejected", StaleSourceRequests),
            ("missing_or_tampered_correction_never_queues", CorrectionIntegrity),
            ("request_id_is_idempotent_under_parallel_retry", IdempotentParallelRetry),
            ("validation_transaction_failure_rolls_back", ValidationRollback),
            ("source_mutation_blocks_resume_before_pipeline", SourceMutation),
            ("lease_loss_before_directory_creation", () => LeaseLossAtPhase(1)),
            ("lease_loss_after_artifact_validation", () => LeaseLossAtPhase(6)),
            ("lease_loss_before_publish_commit", () => LeaseLossAtPhase(8)),
            ("lease_loss_before_pointer_replacement", () => LeaseLossAtPhase(9)),
            ("lease_loss_cancels_running_pipeline", LeaseLossDuringPipeline),
            ("windows_job_cancellation_stops_descendants", CancelWindowsJob),
            ("resume_backoff_and_cap_survive_worker_restart", RetryLimit),
            ("resume_restart_uses_new_run_and_keeps_partial_output", RestartRecovery),
            ("stale_run_cannot_publish_over_latest_run", StaleRunFencing),
            ("resume_publishes_only_verified_local_artifacts", SuccessfulResume),
            ("deid_and_reocr_validation_failure_stays_blocked", PipelineFailure),
            ("local_review_access_denies_production_and_bad_credential", AccessGate)
        };
        var failed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"FAIL {test.Name}");
                Console.WriteLine(error);
            }
        }
        return (tests.Length, failed);
    }

    private static async Task PassQueuesJob()
    {
        await using var f = new WorkflowFixture();
        var response = await f.Revalidate();
        Check(response.Passed && response.ResumeQueued && response.ResumeJobId is { } job && job != Guid.Empty);
        Check(response.Code == "MRN_REVALIDATION_PASSED" && response.ProcessingStatus == "MRN_REVALIDATED");
        Check(f.Repository.Status == "RESOLVED" && f.Repository.ProcessingStatus == "MRN_REVALIDATED");
        Check(f.Repository.Validations.Count == 1 && f.Repository.CommitCount == 1);
        var work = f.Repository.Validations.Single().Work!;
        Check(work.JobId == response.ResumeJobId && work.CorrectionVersion == 1 && work.Files.Count >= 7);
        Check(!response.CloudUploadAllowed);
    }

    private static async Task DocumentMismatch()
    {
        await using var f = new WorkflowFixture(MrnCorrectionReason.DOCUMENT_CASE_MISMATCH);
        var response = await f.Revalidate();
        Check(!response.Passed && !response.ResumeQueued && response.ResumeJobId is null);
        Check(response.Code == "DOCUMENT_SOURCE_REQUIRED");
        Check(f.Repository.Status == "FIXED_PENDING_REVALIDATION" && f.Repository.ProcessingStatus == "MRN_REVIEW_REQUIRED");
    }

    private static async Task BlockersAndPatientConflict()
    {
        await using (var blocked = new WorkflowFixture())
        {
            blocked.Repository.Blockers = 1;
            var result = await blocked.Revalidate();
            Check(!result.Passed && !result.ResumeQueued && result.Code == "OTHER_BLOCKING_EXCEPTIONS");
            Check(blocked.Repository.ProcessingStatus == "MRN_REVIEW_REQUIRED");
        }
        await using (var conflict = new WorkflowFixture())
        {
            conflict.Repository.PatientMatches = false;
            var result = await conflict.Revalidate();
            Check(!result.Passed && !result.ResumeQueued && result.Code == "PATIENT_LINK_CONFLICT");
            Check(conflict.Repository.ProcessingStatus == "MRN_REVIEW_REQUIRED");
        }
    }

    private static async Task StaleSourceRequests()
    {
        await RejectWithoutChanges(400, "INVALID_REQUEST", request => request with { ExpectedCorrectionVersion = 0 });
        await RejectWithoutChanges(409, "VERSION_CONFLICT",
            request => request with { ExpectedCorrectionVersion = request.ExpectedCorrectionVersion + 1 });
        await RejectWithoutChanges(409, "VERSION_CONFLICT", request => request with { CandidateSha256 = new string('C', 64) });
        await RejectWithoutChanges(409, "VERSION_CONFLICT", request => request with { OcrAttemptId = Guid.NewGuid() });
        await RejectWithoutChanges(409, "VERSION_CONFLICT", request => request with { OriginalSha256 = new string('D', 64) });

        static async Task RejectWithoutChanges(int status, string code,
            Func<MrnRevalidationRequest, MrnRevalidationRequest> change)
        {
            await using var fixture = new WorkflowFixture();
            await ExpectWorkflow(status, () => fixture.Revalidate(change(fixture.Request)), code);
            Check(fixture.Repository.ProcessingStatus == "MRN_REVIEW_REQUIRED");
            Check(fixture.Repository.Status == "FIXED_PENDING_REVALIDATION");
            Check(fixture.Repository.CommitCount == 0 && fixture.Repository.Validations.Count == 0 &&
                fixture.Repository.Runs.Count == 0);
        }
    }

    private static async Task CorrectionIntegrity()
    {
        await using (var missing = new WorkflowFixture())
        {
            missing.Store.Missing = true;
            var result = await missing.Revalidate();
            Check(!result.Passed && !result.ResumeQueued && result.Code == "EVIDENCE_INTEGRITY_FAILED");
        }
        await using (var tampered = new WorkflowFixture())
        {
            tampered.Store.Tampered = true;
            var result = await tampered.Revalidate();
            Check(!result.Passed && !result.ResumeQueued && result.Code == "EVIDENCE_INTEGRITY_FAILED");
        }
    }

    private static async Task IdempotentParallelRetry()
    {
        await using var f = new WorkflowFixture();
        var sameRequest = f.Request;
        var first = f.Revalidate(sameRequest);
        var second = f.Revalidate(sameRequest);
        var results = await Task.WhenAll(first, second);
        Check(results.All(x => x.Passed && x.ResumeJobId == results[0].ResumeJobId));
        Check(f.Repository.CommitCount == 1 && f.Repository.Validations.Count == 1);
    }

    private static async Task ValidationRollback()
    {
        await using var f = new WorkflowFixture();
        f.Repository.FailCommit = true;
        try { await f.Revalidate(); throw new Exception("expected isolated transaction failure"); }
        catch (IOException) { }
        Check(f.Repository.Validations.Count == 0 && f.Repository.CommitCount == 0);
        Check(f.Repository.Status == "FIXED_PENDING_REVALIDATION" && f.Repository.ProcessingStatus == "MRN_REVIEW_REQUIRED");
    }

    private static async Task SourceMutation()
    {
        await using var f = new WorkflowFixture();
        var queued = await f.Revalidate();
        Check(queued.Passed);
        await File.WriteAllTextAsync(f.UploadPath, "different synthetic source bytes");
        var callbackCount = 0;
        var service = f.NewResumeService();
        await service.ProcessAsync(WorkflowFixture.ExceptionId, (_, _, _, _) =>
        {
            callbackCount++;
            return Task.CompletedTask;
        }, default);
        Check(callbackCount == 0 && f.Repository.ProcessingStatus == "MRN_REVIEW_REQUIRED");
        Check(f.Repository.ExceptionCodes.Contains("MRN_RESUME_BLOCKED"));
    }

    private static async Task LeaseLossAtPhase(int loseAt)
    {
        await using var f = new WorkflowFixture();
        Check((await f.Revalidate()).Passed);
        var calls = 0;
        var checks = 0;
        try
        {
            await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, async (work, correction, directory, token) =>
            {
                calls++;
                await WorkflowFixture.WriteValidArtifacts(directory, token);
            }, default, (run, _) => ++checks == loseAt
                ? Task.FromException(new MrnResumeLeaseLostException("LockMissing", run)) : Task.CompletedTask);
            throw new Exception("Expected lease cancellation");
        }
        catch (MrnResumeLeaseLostException) { }
        Check(calls == (loseAt == 1 ? 0 : 1));
        Check(f.Repository.Runs.Any(r => r.Status == "MRN_RESUME_PUBLISHED") == (loseAt == 9));
        var deid = Path.GetDirectoryName(f.ResumeDirectory(Guid.Empty))!;
        Check(!File.Exists(Path.Combine(deid, "current.txt")));
        if (loseAt == 1) Check(!Directory.Exists(deid));
    }

    private static async Task LeaseLossDuringPipeline()
    {
        await using var f = new WorkflowFixture();
        Check((await f.Revalidate()).Passed);
        var running = false;
        var canceled = false;
        try
        {
            await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, async (_, _, _, token) =>
            {
                running = true;
                try { await Task.Delay(TimeSpan.FromSeconds(20), token); }
                finally { canceled = token.IsCancellationRequested; }
            }, default, (run, _) => running
                ? Task.FromException(new MrnResumeLeaseLostException("LockMissing", run)) : Task.CompletedTask);
            throw new Exception("Expected lease cancellation");
        }
        catch (MrnResumeLeaseLostException) { }
        Check(canceled && f.Repository.ProcessingStatus == "MRN_DEID_PROCESSING");
    }

    private static async Task CancelWindowsJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = new WorkflowFixture();
        var script = Path.Combine(f.ContentRoot, "test-process-tree.ps1");
        var marker = Path.Combine(f.ContentRoot, "child.pid");
        await File.WriteAllTextAsync(script, """
            param($Marker)
            $child = Start-Process powershell.exe -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 60' -PassThru
            [IO.File]::WriteAllText($Marker, [string]$child.Id)
            $child.WaitForExit()
            """);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var execution = LocalDeidProcessRunner.RunAsync("powershell.exe", f.ContentRoot,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, marker],
            TimeSpan.FromSeconds(25), cancel.Token);
        while (!File.Exists(marker))
        {
            Check(!execution.IsCompleted);
            await Task.Delay(50, cancel.Token);
        }
        var childId = int.Parse(await File.ReadAllTextAsync(marker, cancel.Token));
        using var child = Process.GetProcessById(childId);
        cancel.Cancel();
        try { await execution; throw new Exception("Expected job cancellation"); }
        catch (OperationCanceledException) { }
        Check(child.HasExited);
    }

    private static async Task RetryLimit()
    {
        await using var f = new WorkflowFixture();
        Check((await f.Revalidate()).Passed);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId,
                    (_, _, _, _) => throw new MrnResumeLeaseLostException("ConnectionError", null), default);
                throw new Exception("Expected lease cancellation");
            }
            catch (MrnResumeLeaseLostException) { }
            if (attempt < 3) await ScheduleRetry(f, attempt == 1 ? 5 : 15);
        }
        Check(!await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, UnexpectedPipeline, default));
        f.Clock.Now += TimeSpan.FromHours(1);
        Check(!await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, UnexpectedPipeline, default));
        Check(f.Repository.Runs.Count(r => r.Status == "MRN_RESUME_STARTED") == 3);
        Check(f.Repository.Runs.Count(r => r.Status == "MRN_RESUME_STOPPED" && r.Reason == "ATTEMPT_LIMIT") == 1);
    }

    private static async Task ScheduleRetry(WorkflowFixture f, int delay)
    {
        Check(!await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, UnexpectedPipeline, default));
        Check(f.Repository.Runs.Last().RetryAt == f.Clock.Now.AddSeconds(delay));
        f.Clock.Now += TimeSpan.FromSeconds(delay - 1);
        Check(!await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, UnexpectedPipeline, default));
        f.Clock.Now += TimeSpan.FromSeconds(1);
    }

    private static Task UnexpectedPipeline(MrnResumeWork work, ProtectedMrnCorrection correction, string directory,
        CancellationToken token) => throw new Exception("Pipeline ran before it was eligible");

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task RestartRecovery()
    {
        await using var f = new WorkflowFixture();
        Check((await f.Revalidate()).Passed);
        var service = f.NewResumeService();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partialRun = service.ProcessAsync(WorkflowFixture.ExceptionId, async (_, _, directory, token) =>
        {
            Directory.CreateDirectory(Path.Combine(directory, "result_deidentified_pages"));
            await File.WriteAllTextAsync(Path.Combine(directory, "partial.txt"), "synthetic partial artifact", token);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        try { await partialRun; throw new Exception("expected worker cancellation"); }
        catch (OperationCanceledException) { }

        var firstRun = f.Repository.Runs.Single(r => r.Status == "MRN_RESUME_STARTED");
        var firstDirectory = f.ResumeDirectory(firstRun.RunId);
        Check(f.Repository.ProcessingStatus == "MRN_DEID_PROCESSING" && File.Exists(Path.Combine(firstDirectory, "partial.txt")));

        var calls = 0;
        await ScheduleRetry(f, 5);
        await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, async (_, _, directory, token) =>
        {
            calls++;
            await WorkflowFixture.WriteValidArtifacts(directory, token);
        }, default);
        var starts = f.Repository.Runs.Where(r => r.Status == "MRN_RESUME_STARTED").ToArray();
        Check(calls == 1 && starts.Length == 2 && starts[0].RunId != starts[1].RunId);
        Check(Directory.Exists(firstDirectory) && File.Exists(Path.Combine(firstDirectory, "partial.txt")));
        Check(f.Repository.ProcessingStatus == "PENDING_REVIEW");
    }

    private static async Task StaleRunFencing()
    {
        await using var f = new WorkflowFixture();
        Check((await f.Revalidate()).Passed);
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOldFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTask = f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, async (_, _, directory, token) =>
        {
            oldStarted.SetResult();
            await allowOldFinish.Task.WaitAsync(token);
            await WorkflowFixture.WriteValidArtifacts(directory, token);
        }, default);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ScheduleRetry(f, 5);
        await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, (_, _, directory, token) => WorkflowFixture.WriteValidArtifacts(directory, token), default);
        allowOldFinish.SetResult();
        await oldTask;
        var published = f.Repository.Runs.Single(r => r.Status == "MRN_RESUME_PUBLISHED");
        var pointer = await File.ReadAllTextAsync(Path.Combine(f.ContentRoot, "PrivateUploads", "ocr",
            WorkflowFixture.ReportId.ToString(), f.AttemptId.ToString("N"), "deid", "current.txt"));
        Check(f.Repository.ProcessingStatus == "PENDING_REVIEW" && pointer.Trim() == published.RunId.ToString("N"));
        Check(f.Repository.Runs.Count(r => r.Status == "MRN_RESUME_PUBLISHED") == 1);
    }

    private static async Task SuccessfulResume()
    {
        await using var f = new WorkflowFixture();
        Check((await f.Revalidate()).Passed);
        var before = await f.SourceDigests();
        var invoked = 0;
        await f.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId, async (_, correction, directory, token) =>
        {
            invoked++;
            Check(correction.Request.ConfirmedMrn == WorkflowFixture.ConfirmedMrn);
            await WorkflowFixture.WriteValidArtifacts(directory, token);
        }, default);
        var after = await f.SourceDigests();
        Check(invoked == 1 && before.SequenceEqual(after));
        Check(f.Repository.ProcessingStatus == "PENDING_REVIEW" && f.Repository.Status == "RESOLVED");
        Check(f.Repository.ExceptionCodes.Count == 0 && f.Repository.Runs.Any(r => r.Status == "MRN_RESUME_PUBLISHED"));
        var published = f.Repository.Runs.Single(r => r.Status == "MRN_RESUME_PUBLISHED");
        var resume = f.NewResumeService();
        await resume.ProcessAsync(WorkflowFixture.ExceptionId, (_, _, _, _) =>
        { invoked++; return Task.CompletedTask; }, default);
        Check(invoked == 1 && f.Repository.Runs.Count(r => r.Status == "MRN_RESUME_PUBLISHED") == 1);
        var outputDirectory = f.ResumeDirectory(published.RunId);
        Check(File.Exists(Path.Combine(outputDirectory, "result_deidentified.json")) &&
              File.Exists(Path.Combine(outputDirectory, "result_deidentified_pages", "page_001.png")));
    }

    private static async Task PipelineFailure()
    {
        await using (var failure = new WorkflowFixture())
        {
            Check((await failure.Revalidate()).Passed);
            await failure.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId,
                (_, _, _, _) => throw new InvalidDataException("simulated deid/re-OCR rejection"), default);
            Check(failure.Repository.ProcessingStatus == "DEID_FAILED");
            Check(failure.Repository.ExceptionCodes.Contains("DEID_VERIFICATION_FAILED"));
        }
        await using (var invalidArtifact = new WorkflowFixture())
        {
            Check((await invalidArtifact.Revalidate()).Passed);
            await invalidArtifact.NewResumeService().ProcessAsync(WorkflowFixture.ExceptionId,
                async (_, _, directory, token) => await WorkflowFixture.WriteInvalidCloudArtifact(directory, token), default);
            Check(invalidArtifact.Repository.ProcessingStatus == "DEID_FAILED");
            Check(invalidArtifact.Repository.ExceptionCodes.Contains("DEID_VERIFICATION_FAILED"));
        }
    }

    private static Task AccessGate()
    {
        var secret = new string('x', 48);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MrnReview:Enabled"] = "true",
            ["MrnReview:TestCredential"] = secret
        }).Build();
        var dev = new TestWorkflowEnvironment("Development", Path.GetTempPath());
        var db = new ImagingAI.Api.Data.ImagingAiDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ImagingAI.Api.Data.ImagingAiDbContext>().Options);
        var service = new MrnEvidenceService(db, configuration, dev, new EphemeralDataProtectionProvider());
        Check(service.IsAuthorized(secret));
        Check(!service.IsAuthorized(secret + "wrong"));
        var production = new TestWorkflowEnvironment("Production", Path.GetTempPath());
        Check(!new MrnEvidenceService(db, configuration, production, new EphemeralDataProtectionProvider()).IsAuthorized(secret));
        return Task.CompletedTask;
    }

    private static async Task ExpectWorkflow(int status, Func<Task<MrnRevalidationResponse>> operation, string? code = null)
    {
        try { await operation(); throw new Exception("expected workflow rejection"); }
        catch (MrnWorkflowException error) when (error.Status == status && (code is null || error.Code == code)) { }
    }

    private static void Check(bool value)
    {
        if (!value) throw new Exception("Assertion failed (test details suppressed)");
    }

    private sealed class WorkflowFixture : IAsyncDisposable
    {
        public const long ReportId = 901;
        public const long ExceptionId = 91;
        public const string UploadedMrn = "SYNTH-TEST-0101";
        public const string ConfirmedMrn = "SYNTH-TEST-0202";
        private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();
        private readonly TestWorkflowEnvironment environment;
        private readonly FakeWorkflowStore store;
        private readonly FakeWorkflowAccess access = new();
        public string ContentRoot { get; } = Path.Combine(Path.GetTempPath(), "ImagingAI-MrnWorkflowTests", Guid.NewGuid().ToString("N"));
        public string UploadPath { get; private set; } = "";
        public Guid AttemptId { get; private set; }
        public MrnCorrectionRequest CorrectionRequest { get; private set; } = null!;
        public MrnCorrectionMetadata Metadata { get; private set; } = null!;
        public ProtectedMrnCorrection Correction { get; private set; } = null!;
        public MrnRevalidationRequest Request { get; private set; } = null!;
        public FakeWorkflowRepository Repository { get; } = new();

        public WorkflowFixture(MrnCorrectionReason reason = MrnCorrectionReason.UPLOAD_MRN_INPUT_ERROR)
        {
            Directory.CreateDirectory(ContentRoot);
            environment = new TestWorkflowEnvironment("Development", ContentRoot);
            var privateRoot = Path.Combine(ContentRoot, "PrivateUploads");
            var reportDirectory = Path.Combine(privateRoot, "ocr", ReportId.ToString());
            AttemptId = Guid.NewGuid();
            var attemptDirectory = Path.Combine(reportDirectory, AttemptId.ToString("N"));
            Directory.CreateDirectory(Path.Combine(attemptDirectory, "pages"));
            Directory.CreateDirectory(Path.Combine(privateRoot, "mrn-corrections", ReportId.ToString(), ExceptionId.ToString()));
            UploadPath = Path.Combine(privateRoot, Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllText(UploadPath, "synthetic original PDF bytes");
            var storageUri = "PrivateUploads/" + Path.GetFileName(UploadPath);
            File.WriteAllText(Path.Combine(privateRoot, ReportId + ".mrn.protected"),
                Protection.CreateProtector("ImagingAI.Upload.Mrn.v1").Protect(UploadedMrn));
            File.WriteAllText(Path.Combine(reportDirectory, "current.txt"), AttemptId.ToString("N"));
            File.WriteAllText(Path.Combine(attemptDirectory, "ocr_result.json"),
                "{\"total_pages\":1,\"pages\":[{\"page\":1}]}");
            File.WriteAllBytes(Path.Combine(attemptDirectory, "pages", "page_001.png"), [1, 2, 3, 4]);
            var fields = JsonDocument.Parse("""
                {"mrn":{"candidates":[{"value":"SYNTH-TEST-0202","page":1,"block_id":"block_1","bbox":[1,2,30,40],"confidence":0.99}]},
                 "patient_name":{"candidates":[{"value":"Synthetic Patient","page":1,"block_id":"block_2","bbox":[1,2,30,40],"confidence":0.98}]}}
                """);
            var fieldsJson = fields.RootElement;
            var hash = CandidateHash(fieldsJson);
            var extracted = JsonSerializer.Serialize(new
            {
                schema_version = "ocr_extracted_fields/v1", review_status = "UNVERIFIED",
                case_id = ReportId.ToString(), ocr_attempt_id = AttemptId.ToString("N"),
                mrn_validation = "MRN_MISMATCH", fields = fieldsJson
            });
            File.WriteAllText(Path.Combine(attemptDirectory, "extracted_fields.json"), extracted);

            var requestId = Guid.NewGuid();
            CorrectionRequest = new(requestId, 0, AttemptId, FileHash(UploadPath), hash, reason,
                ConfirmedMrn, [new(1, "block_1")], "synthetic review note");
            var now = DateTimeOffset.UtcNow;
            Correction = new(ExceptionId, ReportId, 1, 1, now, CorrectionRequest);
            var protectedName = $"v1-{requestId:N}-{Guid.NewGuid():N}.protected";
            var protectedFile = Path.Combine(privateRoot, "mrn-corrections", ReportId.ToString(), ExceptionId.ToString(), protectedName);
            File.WriteAllText(protectedFile, "synthetic ciphertext reference bytes");
            Metadata = new("mrn_correction/v1", ExceptionId, ReportId, requestId, 1, 1, now, AttemptId,
                CorrectionRequest.OriginalSha256, hash, reason, protectedName, FileHash(protectedFile));
            store = new FakeWorkflowStore(Correction);
            var candidate = new MrnEvidenceCandidate(ConfirmedMrn, 1, "block_1", [1, 2, 30, 40], 0.99, "UNVERIFIED");
            Repository.Evidence = new(ExceptionId.ToString(), ReportId.ToString(), 1, AttemptId,
                CorrectionRequest.OriginalSha256, hash, UploadedMrn, [candidate], "UNVERIFIED", false, 1, requestId);
            Repository.Corrections.Add(Metadata);
            Repository.Storage = storageUri;
            Request = new(Guid.NewGuid(), 1, requestId, AttemptId, CorrectionRequest.OriginalSha256, hash);
            fields.Dispose();
        }

        public async Task<MrnRevalidationResponse> Revalidate(MrnRevalidationRequest? request = null) =>
            (await NewRevalidationService().RevalidateAsync(ExceptionId, request ?? Request, "test-only", default)).Result;

        public MrnRevalidationService NewRevalidationService() =>
            new(access, Repository, store, new MrnSourceCheckpoint(environment));

        public FakeWorkflowStore Store => store;

        public TestClock Clock { get; } = new();

        public MrnResumeService NewResumeService() =>
            new(Repository, store, new MrnSourceCheckpoint(environment), environment,
                NullLogger<MrnResumeService>.Instance, Clock);

        public async Task<IReadOnlyList<string>> SourceDigests()
        {
            var work = Repository.Validations.Single(v => v.Result.Passed).Work!;
            var result = new List<string>();
            foreach (var item in work.Files)
            {
                await using var stream = File.OpenRead(new MrnSourceCheckpoint(environment).Resolve(item.RelativePath));
                result.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream)));
            }
            return result;
        }

        public string ResumeDirectory(Guid runId) => Path.Combine(ContentRoot, "PrivateUploads", "ocr",
            ReportId.ToString(), AttemptId.ToString("N"), "deid", runId.ToString("N"));

        public static async Task WriteValidArtifacts(string directory, CancellationToken token)
        {
            Directory.CreateDirectory(Path.Combine(directory, "result_deidentified_pages"));
            await File.WriteAllTextAsync(Path.Combine(directory, "result_deidentified.json"), """
                {"schema_version":"deid_ocr/v1","page_count":1,"pages":[{}],
                 "deid_status":"AUTO_REDACTED_PENDING_REVIEW","review_status":"PENDING_REVIEW",
                 "cloud_upload_allowed":false,"verification":{"passed_automated_checks":true}}
                """, token);
            await File.WriteAllBytesAsync(Path.Combine(directory, "result_deidentified_pages", "page_001.png"), [9, 8, 7, 6], token);
        }

        public static async Task WriteInvalidCloudArtifact(string directory, CancellationToken token)
        {
            await WriteValidArtifacts(directory, token);
            await File.WriteAllTextAsync(Path.Combine(directory, "result_deidentified.json"), """
                {"schema_version":"deid_ocr/v1","page_count":1,"pages":[{}],
                 "deid_status":"AUTO_REDACTED_PENDING_REVIEW","review_status":"PENDING_REVIEW",
                 "cloud_upload_allowed":true,"verification":{"passed_automated_checks":true}}
                """, token);
        }

        private static string CandidateHash(JsonElement fields)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, fields);
            var payload = Encoding.UTF8.GetString(buffer.ToArray());
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"ocr_extracted_fields/v1\nUNVERIFIED\nMRN_MISMATCH\n{payload}")));
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); foreach (var child in element.EnumerateArray()) WriteCanonical(writer, child); writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }

        private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        public ValueTask DisposeAsync()
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ImagingAI-MrnWorkflowTests"));
            var target = Path.GetFullPath(ContentRoot);
            if (Path.GetDirectoryName(target) != parent || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
                throw new InvalidOperationException("Unexpected isolated test cleanup path");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Repository.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWorkflowAccess : IMrnCorrectionAccess
    { public bool IsAuthorized(string credential) => credential == "test-only"; }

    private sealed class FakeWorkflowStore(ProtectedMrnCorrection correction) : IProtectedMrnCorrectionStore
    {
        public bool Missing { get; set; }
        public bool Tampered { get; set; }
        public Task<(string Reference, string Sha256)> WriteAsync(ProtectedMrnCorrection value, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<ProtectedMrnCorrection> ReadAsync(MrnCorrectionMetadata metadata, CancellationToken token)
        {
            if (Missing) throw new FileNotFoundException();
            if (Tampered) throw new CryptographicException();
            return Task.FromResult(correction);
        }
    }

    private sealed class FakeWorkflowRepository : IMrnWorkflowRepository, IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public MrnEvidence Evidence { get; set; } = null!;
        public string Status { get; set; } = "FIXED_PENDING_REVALIDATION";
        public string ProcessingStatus { get; set; } = "MRN_REVIEW_REQUIRED";
        public string Storage { get; set; } = "";
        public int Blockers { get; set; }
        public bool PatientMatches { get; set; } = true;
        public bool FailCommit { get; set; }
        public bool FailRunCommit { get; set; }
        public int CommitCount { get; private set; }
        public List<MrnCorrectionMetadata> Corrections { get; } = [];
        public List<MrnRevalidationRecord> Validations { get; } = [];
        public List<MrnResumeRun> Runs { get; } = [];
        public List<string> ExceptionCodes { get; } = [];

        public async Task<IMrnWorkflowSession?> OpenAsync(long exceptionId, CancellationToken token)
        {
            if (exceptionId != WorkflowFixture.ExceptionId) return null;
            await gate.WaitAsync(token);
            return new Session(this);
        }
        public void Dispose() => gate.Dispose();

        private sealed class Session(FakeWorkflowRepository owner) : IMrnWorkflowSession
        {
            private bool disposed;
            public long ReportId => WorkflowFixture.ReportId;
            public long ReviewerId => 1;
            public string ExceptionStatus => owner.Status;
            public string ProcessingStatus => owner.ProcessingStatus;
            public int Blockers => owner.Blockers;
            public string Storage => owner.Storage;
            public IReadOnlyList<MrnCorrectionMetadata> Corrections => owner.Corrections;
            public IReadOnlyList<MrnRevalidationRecord> Validations => owner.Validations;
            public IReadOnlyList<MrnResumeRun> Runs => owner.Runs;
            public Task<MrnEvidence?> EvidenceAsync(CancellationToken token) => Task.FromResult<MrnEvidence?>(owner.Evidence);
            public Task<bool> PatientMatchesAsync(string mrn, CancellationToken token) => Task.FromResult(owner.PatientMatches);

            public Task CommitValidationAsync(MrnRevalidationRecord record, CancellationToken token)
            {
                if (owner.FailCommit) throw new IOException("simulated transaction rollback");
                owner.Validations.Add(record);
                owner.Status = record.Result.ExceptionStatus;
                if (record.Result.Passed) owner.ProcessingStatus = record.Result.ProcessingStatus;
                owner.CommitCount++;
                return Task.CompletedTask;
            }

            public Task CommitRunAsync(MrnResumeRun run, string state, string? exceptionCode, CancellationToken token)
            {
                if (owner.FailRunCommit) throw new IOException("simulated run transaction rollback");
                owner.Runs.Add(run);
                owner.ProcessingStatus = state;
                if (exceptionCode is not null) owner.ExceptionCodes.Add(exceptionCode);
                owner.CommitCount++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!disposed) { disposed = true; owner.gate.Release(); }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestWorkflowEnvironment(string environmentName, string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "ImagingAI.Api.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
