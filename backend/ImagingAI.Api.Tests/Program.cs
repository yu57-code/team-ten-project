using System.Text.Json;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ImagingAI.Api.Controllers;

// Dependency-free, isolated tests. No connection string, live PostgreSQL or production files.
var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Func<Task> run) => tests.Add((name, run));
AuthServiceTests.Register(Test);
AuthRoleSelectionTests.Register(Test);
DevAuthTests.Register(Test);
ImagingAI.Api.Tests.EmailOtpTests.Register(Test);
Test("OtpHttp_loopback_https_routes_cookies_csrf_and_business_gate", OtpHttpTests.Run);
GmailSmtpTests.Register(Test);
void Check(bool value) { if (!value) throw new Exception("Assertion failed (sensitive details suppressed)"); }
async Task Reject(int status, Func<Task> run)
{
    try { await run(); } catch (MrnCorrectionException error) when (error.StatusCode == status) { return; }
    throw new Exception("Expected rejection was not observed");
}

foreach (var reason in Enum.GetValues<MrnCorrectionReason>())
    Test($"Save_{reason}", async () =>
    {
        await using var f = new Fixture();
        var request = f.Request with { Reason = reason,
            ConfirmedMrn = reason == MrnCorrectionReason.OCR_RECOGNITION_ERROR ? "SYNTH-0001" : "SYNTH-0002" };
        var result = await f.Save(request);
        Check(result.Success && result.CorrectionVersion == 1 && !result.ResumeQueued && !result.CloudUploadAllowed);
        Check(result.ValidationStatus == "NOT_RUN" && result.ProcessingStatus == "MRN_REVIEW_REQUIRED");
        Check(result.SourceConfirmationRequired == (reason == MrnCorrectionReason.DOCUMENT_CASE_MISMATCH));
        Check(f.Repository.Status == "FIXED_PENDING_REVALIDATION" && f.Repository.Rows.Count == 1);
        var metadata = f.Repository.Rows.Single();
        var ciphertext = await File.ReadAllTextAsync(f.File(metadata));
        Check(!ciphertext.Contains(request.ConfirmedMrn) && !ciphertext.Contains(request.Note));
        var saved = await f.Store.ReadAsync(metadata, default);
        Check(saved.Request.ConfirmedMrn == request.ConfirmedMrn && saved.Request.Note == request.Note);
        var json = JsonSerializer.Serialize(metadata, MrnCorrectionJson.Options);
        Check(!json.Contains(request.ConfirmedMrn) && !json.Contains(request.Note));
    });
Test("Retry_is_idempotent", async () =>
{
    await using var f = new Fixture(); var request = f.Request;
    var first = await f.Save(request); var second = await f.Save(request);
    Check(first == second && f.Repository.Rows.Count == 1 && f.Repository.CommitCount == 1);
    Check(Directory.GetFiles(f.Root, "*.protected", SearchOption.AllDirectories).Length == 1);
});
Test("Retry_different_content_conflicts", async () =>
{
    await using var f = new Fixture(); var request = f.Request; await f.Save(request);
    await Reject(409, () => f.Save(request with { Note = "different fictional reason" }));
    Check(f.Repository.Rows.Count == 1);
});
Test("Stale_version_conflicts", async () =>
{
    await using var f = new Fixture(); await f.Save(f.Request);
    await Reject(409, () => f.Save(f.Request)); Check(f.Repository.Rows.Count == 1);
});
Test("Next_version_appends_without_overwrite", async () =>
{
    await using var f = new Fixture(); await f.Save(f.Request);
    var original = await File.ReadAllBytesAsync(f.File(f.Repository.Rows[0]));
    var result = await f.Save(f.Request with { ExpectedCorrectionVersion = 1 });
    Check(result.CorrectionVersion == 2 && f.Repository.Rows.Count == 2);
    Check(original.SequenceEqual(await File.ReadAllBytesAsync(f.File(f.Repository.Rows[0]))));
});
foreach (var field in new[] { "pdf", "candidate", "attempt", "reference" })
    Test($"Source_conflict_{field}", async () =>
    {
        await using var f = new Fixture(); var request = f.Request;
        request = field switch {
            "pdf" => request with { OriginalSha256 = new string('C',64) },
            "candidate" => request with { CandidateSha256 = new string('C',64) },
            "attempt" => request with { OcrAttemptId = Guid.NewGuid() },
            _ => request with { EvidenceReferences = [new(2,"missing")] }
        };
        await Reject(409, () => f.Save(request)); Check(f.Repository.CommitCount == 0);
    });
Test("No_authorization_does_not_open_transaction", async () =>
{
    await using var f = new Fixture(); f.Access.Allowed = false;
    await Reject(404, () => f.Save(f.Request)); Check(f.Repository.BeginCount == 0);
});
Test("Unallowed_or_missing_case_is_hidden", async () =>
{
    await using var f = new Fixture(); f.Repository.NotFound = true;
    await Reject(404, () => f.Save(f.Request)); Check(f.Repository.CommitCount == 0);
});
Test("Resolved_exception_cannot_be_changed", async () =>
{
    await using var f = new Fixture(); f.Repository.Status = "RESOLVED";
    await Reject(409, () => f.Save(f.Request));
});
Test("Wrong_report_stage_cannot_be_changed", async () =>
{
    await using var f = new Fixture(); f.Repository.ProcessingStatus = "PENDING_REVIEW";
    await Reject(409, () => f.Save(f.Request));
});
Test("Invalid_MRN_is_rejected", async () =>
{
    await using var f = new Fixture();
    await Reject(400, () => f.Save(f.Request with { ConfirmedMrn = "invalid value" }));
});
Test("Reason_must_match_source", async () =>
{
    await using var f = new Fixture();
    await Reject(400, () => f.Save(f.Request with { ConfirmedMrn = "SYNTH-9999" }));
});
Test("Unknown_reason_is_rejected", async () =>
{
    await using var f = new Fixture();
    await Reject(400, () => f.Save(f.Request with { Reason = (MrnCorrectionReason)99 }));
});
Test("Empty_request_id_is_rejected", async () =>
{
    await using var f = new Fixture(); await Reject(400, () => f.Save(f.Request with { RequestId = Guid.Empty }));
});
Test("Transaction_failure_does_not_publish_or_mark_fixed", async () =>
{
    await using var f = new Fixture(); f.Repository.FailCommit = true; var request = f.Request;
    try { await f.Save(request); throw new Exception("Failure not injected"); } catch (IOException) { }
    Check(f.Repository.Rows.Count == 0 && f.Repository.Status == "OPEN" && f.Repository.DisposedCount == 1);
    Check(Directory.GetFiles(f.Root, "*.protected", SearchOption.AllDirectories).Length == 1);
    f.Repository.FailCommit = false; await f.Save(request);
    Check(f.Repository.Rows.Count == 1 && f.Repository.CommitCount == 1);
    Check(Directory.GetFiles(f.Root, "*.protected", SearchOption.AllDirectories).Length == 2);
});
Test("Source_change_after_file_write_does_not_commit", async () =>
{
    await using var f = new Fixture(); f.Repository.FailRecheck = true;
    await Reject(409, () => f.Save(f.Request)); Check(f.Repository.Rows.Count == 0);
});
Test("Concurrent_different_requests_get_one_conflict", async () =>
{
    await using var f = new Fixture();
    async Task<int> Save(MrnCorrectionRequest request) {try{await f.Save(request);return 200;}catch(MrnCorrectionException e){return e.StatusCode;}}
    var results = await Task.WhenAll(Save(f.Request), Save(f.Request));
    Check(results.Order().SequenceEqual(new[]{200,409}) && f.Repository.Rows.Count == 1);
});
Test("Concurrent_same_request_commits_once", async () =>
{
    await using var f = new Fixture(); var request=f.Request;
    var results=await Task.WhenAll(f.Save(request),f.Save(request));
    Check(results[0]==results[1] && f.Repository.CommitCount==1);
});
Test("Ciphertext_tampering_is_rejected", async () =>
{
    await using var f = new Fixture(); var request = f.Request; await f.Save(request);
    await File.AppendAllTextAsync(f.File(f.Repository.Rows[0]), "tamper");
    try {await f.Save(request); throw new Exception("Tampering accepted");} catch(InvalidDataException) { }
});
Test("Protected_reference_path_traversal_is_rejected", async () =>
{
    await using var f = new Fixture(); await f.Save(f.Request);
    try {await f.Store.ReadAsync(f.Repository.Rows[0] with {ProtectedReference="../outside"},default);throw new Exception("Traversal accepted");}
    catch(InvalidDataException) { }
});
Test("Corrupted_previous_version_cannot_be_bypassed_by_new_request", async () =>
{
    await using var f=new Fixture();await f.Save(f.Request);
    await File.AppendAllTextAsync(f.File(f.Repository.Rows[0]),"tamper");
    try{await f.Save(f.Request with {ExpectedCorrectionVersion=1});throw new Exception("Corrupt history accepted");}
    catch(InvalidDataException) { }
    Check(f.Repository.Rows.Count==1);
});
Test("MRN_leading_zeros_and_separators_are_preserved", async () =>
{
    await using var f = new Fixture(); var mrn="00Ab-01/2.3_4";
    f.Repository.Evidence=f.Repository.Evidence with {UploadedMrn=mrn};
    await f.Save(f.Request with {Reason=MrnCorrectionReason.OCR_RECOGNITION_ERROR,ConfirmedMrn=mrn});
    Check((await f.Store.ReadAsync(f.Repository.Rows[0],default)).Request.ConfirmedMrn==mrn);
});
Test("Production_disabled_or_wrong_credential_is_denied", () =>
{
    using var db=new ImagingAiDbContext(new DbContextOptionsBuilder<ImagingAiDbContext>().Options);
    var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["MrnReview:Enabled"]="true",["MrnReview:TestCredential"]=new string('X',64)}).Build();
    var env=new TestEnvironment(Path.GetTempPath()){EnvironmentName="Production"};
    var gate=new MrnEvidenceService(db,config,env,new EphemeralDataProtectionProvider());
    Check(!gate.IsAuthorized(new string('X',64))); env.EnvironmentName="Development";
    Check(gate.IsAuthorized(new string('X',64)) && !gate.IsAuthorized(new string('Y',64)));
    config["MrnReview:Enabled"]="false"; Check(!gate.IsAuthorized(new string('X',64)));
    config["MrnReview:Enabled"]="true";config["MrnReview:TestCredential"]="short"; Check(!gate.IsAuthorized("short"));
    return Task.CompletedTask;
});
Test("All_exception_statuses_map_explicitly", () =>
{
    foreach(var pair in new[]{("OPEN",ExceptionStatus.Pending),("FIXED_PENDING_REVALIDATION",ExceptionStatus.FixedPendingRevalidation),
        ("IN_PROGRESS",ExceptionStatus.InProgress),("RESOLVED",ExceptionStatus.Resolved),("UNRESOLVABLE",ExceptionStatus.Unresolvable)})
        Check(ExceptionService.Map(new ImagingAI.Api.Models.Database.ExceptionQueue {
            ExceptionId=1,ReportId=1,ExceptionCategory="MRN_MISMATCH",Status=pair.Item1}).Status==pair.Item2);
    return Task.CompletedTask;
});
Test("Controller_does_not_open_revalidate_or_unresolvable", () =>
{
    using var db=new ImagingAiDbContext(new DbContextOptionsBuilder<ImagingAiDbContext>().Options);
    var controller=new ExceptionsController(new ExceptionService(db),NullLogger<ExceptionsController>.Instance);
    Check(controller.ReadOnlyOperation("91") is ObjectResult {StatusCode:501});
    return Task.CompletedTask;
});
Test("Controller_unauthorized_resolve_is_safe_404", async () =>
{
    await using var f=new Fixture();f.Access.Allowed=false;
    using var db=new ImagingAiDbContext(new DbContextOptionsBuilder<ImagingAiDbContext>().Options);
    var controller=new ExceptionsController(new ExceptionService(db),NullLogger<ExceptionsController>.Instance)
        {ControllerContext=new ControllerContext {HttpContext=new DefaultHttpContext()}};
    var result=await controller.Resolve("91",f.Request,
        new MrnCorrectionService(f.Access,f.Repository,f.Store),default);
    Check(result is ObjectResult {StatusCode:404} && f.Repository.BeginCount==0);
});
Test("Store_failure_does_not_publish_database_changes", async () =>
{
    await using var f=new Fixture();
    var service=new MrnCorrectionService(f.Access,f.Repository,new FailingStore());
    try{await service.SaveAsync(91,f.Request,"test-only",default);throw new Exception("Failure not injected");}
    catch(IOException) { }
    Check(f.Repository.Rows.Count==0 && f.Repository.CommitCount==0 && f.Repository.Status=="OPEN");
});
Test("Missing_reason_is_not_silently_defaulted", () =>
{
    try {JsonSerializer.Deserialize<MrnCorrectionRequest>("{}",MrnCorrectionJson.Options);throw new Exception("Missing reason accepted");}
    catch(JsonException) { }
    return Task.CompletedTask;
});

var failures=0;
foreach(var test in tests)
{
    try {await test.Run();Console.WriteLine($"PASS {test.Name}");}
    catch(Exception error){failures++;Console.WriteLine($"FAIL {test.Name} ({error.GetType().Name})");}
}
Console.WriteLine($"Tests={tests.Count} Passed={tests.Count-failures} Failed={failures}; LiveDatabaseWrites=0");
return failures==0?0:1;

sealed class Fixture : IAsyncDisposable
{
    public string Root {get;}=Path.Combine(Path.GetTempPath(),"ImagingAI-MrnCorrectionTests",Guid.NewGuid().ToString("N"));
    public Access Access {get;}=new();
    public FakeRepository Repository {get;}=new();
    public ProtectedMrnCorrectionStore Store {get;}
    private readonly MrnCorrectionService service;
    public Fixture(){Directory.CreateDirectory(Root);Store=new(new TestEnvironment(Root),new EphemeralDataProtectionProvider());service=new(Access,Repository,Store);}
    public MrnCorrectionRequest Request=>new(Guid.NewGuid(),0,Repository.Evidence.OcrAttemptId,
        Repository.Evidence.OriginalSha256,Repository.Evidence.CandidateSha256,
        MrnCorrectionReason.UPLOAD_MRN_INPUT_ERROR,"SYNTH-0002",[new(1,"block_1")],"fictional correction explanation");
    public Task<MrnCorrectionResponse> Save(MrnCorrectionRequest request)=>service.SaveAsync(91,request,"test-only",default);
    public string File(MrnCorrectionMetadata metadata)=>Path.Combine(Root,"PrivateUploads","mrn-corrections","901","91",metadata.ProtectedReference);
    public ValueTask DisposeAsync()
    {
        var parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"ImagingAI-MrnCorrectionTests"));
        var target=Path.GetFullPath(Root);
        if(Path.GetDirectoryName(target)!=parent || !Guid.TryParseExact(Path.GetFileName(target),"N",out _))
            throw new InvalidOperationException("Unexpected test cleanup path");
        Directory.Delete(target,true);return ValueTask.CompletedTask;
    }
}
sealed class FailingStore : IProtectedMrnCorrectionStore
{
    public Task<(string Reference,string Sha256)> WriteAsync(ProtectedMrnCorrection correction,CancellationToken token)
        =>throw new IOException("simulated storage failure");
    public Task<ProtectedMrnCorrection> ReadAsync(MrnCorrectionMetadata metadata,CancellationToken token)
        =>throw new IOException("simulated storage failure");
}
sealed class Access : IMrnCorrectionAccess {public bool Allowed=true;public bool IsAuthorized(string credential)=>Allowed;}
sealed class FakeRepository : IMrnCorrectionRepository
{
    public MrnEvidence Evidence=new("91","901",1,Guid.NewGuid(),new string('A',64),new string('B',64),
        "SYNTH-0001",[new("SYNTH-0002",1,"block_1",[1,2,30,40],0.99,"UNVERIFIED")]);
    public readonly List<MrnCorrectionMetadata> Rows=[];
    public string Status="OPEN",ProcessingStatus="MRN_REVIEW_REQUIRED";
    public bool FailCommit,FailRecheck,NotFound;
    public int BeginCount,CommitCount,DisposedCount;
    private readonly SemaphoreSlim mutex=new(1,1);
    public async Task<IMrnCorrectionSession?> BeginAsync(long id,CancellationToken token)
    {
        BeginCount++;await mutex.WaitAsync(token);
        if(NotFound){mutex.Release();return null;}return new Session(this);
    }
    sealed class Session(FakeRepository owner):IMrnCorrectionSession
    {
        public MrnEvidence Evidence=>owner.Evidence;
        public string ExceptionStatus=>owner.Status;
        public string ProcessingStatus=>owner.ProcessingStatus;
        public IReadOnlyList<MrnCorrectionMetadata> Corrections=>owner.Rows;
        public Task RecheckEvidenceAsync(CancellationToken token)=>owner.FailRecheck
            ?Task.FromException(new MrnCorrectionException(409,"source changed")):Task.CompletedTask;
        public Task CommitAsync(MrnCorrectionMetadata metadata,CancellationToken token)
        {
            if(owner.FailCommit)throw new IOException("simulated transaction failure");
            owner.Rows.Add(metadata);owner.Status="FIXED_PENDING_REVALIDATION";owner.CommitCount++;return Task.CompletedTask;
        }
        public ValueTask DisposeAsync(){owner.DisposedCount++;owner.mutex.Release();return ValueTask.CompletedTask;}
    }
}
sealed class TestEnvironment(string root):IWebHostEnvironment
{
    public string EnvironmentName {get;set;}="Development";
    public string ApplicationName {get;set;}="ImagingAI.Api.Tests";
    public string ContentRootPath {get;set;}=root;
    public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    public string WebRootPath {get;set;}="";
    public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
}
