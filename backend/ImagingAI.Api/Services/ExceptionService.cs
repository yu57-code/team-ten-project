
using ImagingAI.Api.Models;

namespace ImagingAI.Api.Services;

public sealed class ExceptionService
{
    private readonly object _sync = new();

    private readonly List<ExceptionItem> _exceptions = new()
    {
        // EX-001：病歷號碼不一致（原有資料）
        new ExceptionItem
        {
            Id = "EX-001",
            CaseId = "CASE-013",
            Category = ExceptionCategory.MedicalRecordNumber,
            Description = "上傳病歷號碼與 OCR 辨識結果不一致",
            CreatedAt = DateTime.UtcNow.AddMinutes(-50),
            Status = ExceptionStatus.Pending,
            FailedStep = 4,
            RestartFrom = "解析前驗證",
            FixLabel = "正確病歷號碼",
            AllowMerge = true,
            Comparisons =
            [
                new ComparisonRow(
                    "MRN",
                    "B789012",
                    "A123456",
                    "不一致",
                    VerdictLevel.Fail)
            ]
        },

        // EX-002：未知報告類型（原有資料）
        new ExceptionItem
        {
            Id = "EX-002",
            CaseId = "CASE-014",
            Category = ExceptionCategory.ReportType,
            Description = "無法辨識醫療報告類型",
            CreatedAt = DateTime.UtcNow.AddMinutes(-40),
            Status = ExceptionStatus.Pending,
            FailedStep = 3,
            RestartFrom = "類型辨識",
            FixLabel = "正確報告類型",
            Comparisons =
            [
                new ComparisonRow(
                    "Report Type",
                    "UNKNOWN",
                    "心導管報告",
                    "需要人工確認",
                    VerdictLevel.Warning)
            ]
        },

        // EX-003：解析前驗證異常（原有資料）
        new ExceptionItem
        {
            Id = "EX-003",
            CaseId = "CASE-015",
            Category = ExceptionCategory.PreParse,
            Description = "解析前驗證發現必要欄位缺失",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            Status = ExceptionStatus.Pending,
            FailedStep = 4,
            RestartFrom = "解析前驗證",
            FixLabel = "缺失欄位修正值",
            Comparisons =
            [
                new ComparisonRow(
                    "Study Date",
                    "未擷取到",
                    "有效檢查日期",
                    "缺少必要欄位",
                    VerdictLevel.Fail)
            ]
        },

        // EX-004：AI 解析結果異常（原有資料）
        new ExceptionItem
        {
            Id = "EX-004",
            CaseId = "CASE-016",
            Category = ExceptionCategory.AiResult,
            Description = "AI 解析結果需要人工確認",
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            Status = ExceptionStatus.Pending,
            FailedStep = 6,
            RestartFrom = "結果驗證",
            FixLabel = "正確解析結果",
            Comparisons =
            [
                new ComparisonRow(
                    "LAD Stenosis",
                    "80%",
                    "待人工確認",
                    "需要覆核",
                    VerdictLevel.Warning)
            ]
        },

        // EX-005：FHIR 驗證異常（原有資料）
        new ExceptionItem
        {
            Id = "EX-005",
            CaseId = "CASE-017",
            Category = ExceptionCategory.Fhir,
            Description = "FHIR 必要欄位驗證失敗",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            Status = ExceptionStatus.Pending,
            FailedStep = 8,
            RestartFrom = "FHIR",
            FixLabel = "FHIR 欄位修正值",
            Comparisons =
            [
                new ComparisonRow(
                    "DiagnosticReport.status",
                    "Missing",
                    "final",
                    "必要欄位缺失",
                    VerdictLevel.Fail)
            ]
        },

        // ==========================================
        // 新增測試資料
        // ==========================================

        // EX-006：MRN 不一致
        new ExceptionItem
        {
            Id = "EX-006",
            CaseId = "CASE-018",
            Category = ExceptionCategory.MedicalRecordNumber,
            Description = "上傳 MRN 與 OCR 擷取 MRN 不一致",
            CreatedAt = DateTime.UtcNow.AddMinutes(-8),
            Status = ExceptionStatus.Pending,
            FailedStep = 4,
            RestartFrom = "解析前驗證",
            FixLabel = "正確病歷號碼",
            AllowMerge = false,
            Comparisons =
            [
                new ComparisonRow(
                    "MRN",
                    "B789012",
                    "A123456",
                    "病歷號碼不一致",
                    VerdictLevel.Fail)
            ]
        },

        // EX-007：MRN 缺失
        new ExceptionItem
        {
            Id = "EX-007",
            CaseId = "CASE-019",
            Category = ExceptionCategory.MedicalRecordNumber,
            Description = "OCR 未擷取到病歷號碼，無法完成 MRN 驗證",
            CreatedAt = DateTime.UtcNow.AddMinutes(-7),
            Status = ExceptionStatus.Pending,
            FailedStep = 4,
            RestartFrom = "解析前驗證",
            FixLabel = "正確病歷號碼",
            AllowMerge = false,
            Comparisons =
            [
                new ComparisonRow(
                    "MRN",
                    "未擷取到",
                    "SYNTH00019",
                    "必要欄位缺失",
                    VerdictLevel.Fail)
            ]
        },

        // EX-008：未知報告類型
        new ExceptionItem
        {
            Id = "EX-008",
            CaseId = "CASE-020",
            Category = ExceptionCategory.ReportType,
            Description = "系統無法辨識上傳的醫療報告類型",
            CreatedAt = DateTime.UtcNow.AddMinutes(-6),
            Status = ExceptionStatus.Pending,
            FailedStep = 3,
            RestartFrom = "類型辨識",
            FixLabel = "正確報告類型",
            Comparisons =
            [
                new ComparisonRow(
                    "Report Type",
                    "UNKNOWN",
                    "心導管報告",
                    "需要人工確認",
                    VerdictLevel.Warning)
            ]
        },

        // EX-009：報告類型判斷錯誤
        new ExceptionItem
        {
            Id = "EX-009",
            CaseId = "CASE-021",
            Category = ExceptionCategory.ReportType,
            Description = "AI 將心導管報告誤判為心臟超音波報告",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            Status = ExceptionStatus.Pending,
            FailedStep = 3,
            RestartFrom = "類型辨識",
            FixLabel = "正確報告類型",
            Comparisons =
            [
                new ComparisonRow(
                    "Report Type",
                    "心臟超音波報告",
                    "心導管報告",
                    "報告類型判斷錯誤",
                    VerdictLevel.Fail)
            ]
        }
    };

    // 複製資料，避免外部直接修改記憶體內容
    private static ExceptionItem Clone(ExceptionItem item)
    {
        return new ExceptionItem
        {
            Id = item.Id,
            CaseId = item.CaseId,
            Category = item.Category,
            Description = item.Description,
            CreatedAt = item.CreatedAt,
            Assignee = item.Assignee,
            Status = item.Status,
            FailedStep = item.FailedStep,
            RestartFrom = item.RestartFrom,
            FixLabel = item.FixLabel,
            FixValue = item.FixValue,
            AllowMerge = item.AllowMerge,
            Comparisons = item.Comparisons.ToList(),
            ResolutionNote = item.ResolutionNote
        };
    }

    // 取得尚未結案的異常
    public IReadOnlyList<ExceptionItem> GetOpenExceptions()
    {
        lock (_sync)
        {
            return _exceptions
                .Where(e => e.Status is not
                    (ExceptionStatus.Resolved or
                     ExceptionStatus.Unresolvable))
                .OrderByDescending(e => e.CreatedAt)
                .Select(Clone)
                .ToList();
        }
    }

    // 根據 ID 查詢單筆異常（包含已結案資料）
    public ExceptionItem? GetById(string id)
    {
        lock (_sync)
        {
            var item = _exceptions.FirstOrDefault(
                e => e.Id.Equals(
                    id, StringComparison.OrdinalIgnoreCase));

            return item is null ? null : Clone(item);
        }
    }

    // 取得五種類型的未結案異常數量
    public object GetSummary()
    {
        lock (_sync)
        {
            var open = _exceptions
                .Where(e => e.Status is not
                    (ExceptionStatus.Resolved or
                     ExceptionStatus.Unresolvable))
                .ToList();

            return new
            {
                Total = open.Count,
                MedicalRecordNumber = open.Count(
                    e => e.Category ==
                        ExceptionCategory.MedicalRecordNumber),
                ReportType = open.Count(
                    e => e.Category == ExceptionCategory.ReportType),
                PreParse = open.Count(
                    e => e.Category == ExceptionCategory.PreParse),
                AiResult = open.Count(
                    e => e.Category == ExceptionCategory.AiResult),
                Fhir = open.Count(
                    e => e.Category == ExceptionCategory.Fhir)
            };
        }
    }

    // 儲存人工修正
    public ExceptionOperationResponse Resolve(
        string id,
        ExceptionResolution resolution,
        string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            return new(false, "請提供處理人員");

        if (string.IsNullOrWhiteSpace(resolution.Note))
            return new(false, "請填寫處理說明");

        if (string.IsNullOrWhiteSpace(resolution.FixValue))
            return new(false, "請填寫修正值");

        lock (_sync)
        {
            var item = _exceptions.FirstOrDefault(e => e.Id == id);

            if (item is null)
                return new(false, "找不到異常紀錄");

            if (item.Status is ExceptionStatus.Resolved
                or ExceptionStatus.Unresolvable)
                return new(false, "此異常已結案");

            if (resolution.MergeIntoExistingPatient &&
                (item.Category != ExceptionCategory.MedicalRecordNumber ||
                 !item.AllowMerge))
                return new(false, "此異常不允許合併病人");

            // 第一階段不執行真實病人合併
            item.FixValue = resolution.FixValue.Trim();
            item.ResolutionNote = resolution.Note.Trim();
            item.Assignee = actor.Trim();
            item.Status = ExceptionStatus.FixedPendingRevalidation;

            return new(
                true,
                "修正資料已儲存，等待重新驗證（模擬模式）");
        }
    }

    // 模擬重新驗證
    public ExceptionOperationResponse Revalidate(
        string id,
        string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            return new(false, "請提供驗證人員");

        lock (_sync)
        {
            var item = _exceptions.FirstOrDefault(e => e.Id == id);

            if (item is null)
                return new(false, "找不到異常紀錄");

            if (item.Status != ExceptionStatus.FixedPendingRevalidation)
                return new(false, "此異常尚未進入待重新驗證狀態");

            // 僅示範 MRN 格式檢查
            if (item.Category != ExceptionCategory.MedicalRecordNumber)
                return new(false,
                    "此類型尚未實作重新驗證規則，不能標記為通過");

            bool valid = item.FixValue.Length >= 3 &&
                item.FixValue.All(char.IsLetterOrDigit);

            if (!valid)
            {
                item.Status = ExceptionStatus.InProgress;
                return new(false, "MRN 模擬格式驗證失敗，請重新修正");
            }

            // 這不代表真實病歷號碼已核對成功
            item.Status = ExceptionStatus.Resolved;

            return new(
                true,
                "MRN 模擬格式驗證通過，異常已標記解決");
        }
    }

    // 標記無法處理
    public ExceptionOperationResponse MarkUnresolvable(
        string id,
        string note,
        string actor)
    {
        if (string.IsNullOrWhiteSpace(note))
            return new(false, "請填寫無法處理原因");

        if (string.IsNullOrWhiteSpace(actor))
            return new(false, "請提供處理人員");

        lock (_sync)
        {
            var item = _exceptions.FirstOrDefault(e => e.Id == id);

            if (item is null)
                return new(false, "找不到異常紀錄");

            if (item.Status is ExceptionStatus.Resolved
                or ExceptionStatus.Unresolvable)
                return new(false, "此異常已結案");

            item.Status = ExceptionStatus.Unresolvable;
            item.ResolutionNote = note.Trim();
            item.Assignee = actor.Trim();

            return new(
                true,
                "已標記為無法處理（模擬模式，不寫入 FHIR）");
        }
    }
}
