using System.Text.Json;
using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Mock;

/// <summary>
/// 記憶體中的模擬資料庫（Singleton）。初始內容是空的，資料由操作產生；
/// 所有 Mock 服務共用同一份資料，所以在某一頁的操作其他頁也看得到。重新啟動程式會清空。
/// 之後串接 Web API／PostgreSQL 時，整個 Services/Mock 資料夾都可以不再註冊。
/// </summary>
public sealed class MockDataStore
{
    /// <summary>所有讀寫都要先 lock 這個物件。</summary>
    public object Sync { get; } = new();

    public List<CaseRecord> Cases { get; } = MockSeedData.Cases();

    public Dictionary<string, CaseReviewDetail> Reviews { get; } = MockSeedData.Reviews();

    public List<ExceptionItem> Exceptions { get; } = MockSeedData.Exceptions();

    public List<AuditEntry> AuditEntries { get; } = MockSeedData.AuditEntries();

    public Dictionary<string, List<HistoryRecord>> History { get; } = MockSeedData.History();

    public List<UploadJob> UploadJobs { get; } = MockSeedData.UploadJobs();

    /// <summary>模擬 SHA-256 重複檢查：以檔名（不分大小寫）代表檔案雜湊 → 病例 ID。</summary>
    public Dictionary<string, string> FileHashIndex { get; } = MockSeedData.FileHashIndex();

    /// <summary>今天已寫入 FHIR 的病例數。</summary>
    public int WrittenToFhirToday { get; set; }

    // ---------- 系統管理 ----------

    /// <summary>帳號：一開始是空的，使用者第一次登入時建立。</summary>
    public List<ManagedUser> Users { get; } = [];

    /// <summary>系統錯誤造成的失敗任務。</summary>
    public List<FailedTask> FailedTasks { get; } = [];

    public SystemSettings Settings { get; set; } = new();

    /// <summary>報告類型：預設只有系統目前支援的心導管檢查報告（系統設定，不是模擬資料）。</summary>
    public List<ReportTypeConfig> ReportTypes { get; } =
    [
        new() { TypeCode = "CATH", NameZh = "心導管檢查報告", NameEn = "Cardiac Catheterization Report", IsActive = true },
    ];

    public List<BackupRecord> Backups { get; } = [];

    /// <summary>上次檢查的服務狀態；key = 服務代碼。</summary>
    public Dictionary<string, ServiceHealth> ServiceHealth { get; } = new();

    private int _nextCaseNumber = 1;
    private int _nextAuditNumber = 1;
    private int _nextExceptionNumber = 1;
    private int _nextBackupNumber = 1;

    public string NextBackupId() => $"BK-{_nextBackupNumber++:0000}";

    public string NextCaseId() => $"C-{DateTime.Now:MMdd}-{_nextCaseNumber++:0000}";

    public string NextAuditId() => $"AT-{_nextAuditNumber++:00000}";

    public string NextExceptionId() => $"E-{_nextExceptionNumber++:0000}";

    public DateTime Clock() => DateTime.Now;

    public void AddAudit(string actor, AuditKind kind, string actionCode, string? caseId, string version, string note,
        object? before = null, object? after = null)
    {
        AuditEntries.Insert(0, new AuditEntry(NextAuditId(), Clock(), actor, kind, actionCode, caseId, version, note,
            ToJson(before ?? new { }), ToJson(after ?? new { })));
    }

    public void AddHistory(string userName, HistoryAction action, string caseId, string description, HistoryCaseState state)
    {
        if (!History.TryGetValue(userName, out var list))
        {
            list = [];
            History[userName] = list;
        }

        list.Insert(0, new HistoryRecord(Clock(), action, caseId, description, state));
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(object value) => JsonSerializer.Serialize(value, PrettyJson);

    /// <summary>深層複製（讓頁面修改的是複本，送出後才寫回）。</summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
