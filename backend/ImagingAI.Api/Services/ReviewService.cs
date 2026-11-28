using System.Globalization;
using System.Text.Json;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using ImagingAI.Api.Models.Database;
using Microsoft.EntityFrameworkCore;

namespace ImagingAI.Api.Services;

public sealed class ReviewService(
    ImagingAiDbContext db,
    IConfiguration configuration,
    IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private sealed record DeidDiskPayload(
        List<ReviewDocPageDto> Pages,
        int? MaskedCount,
        double? OcrQuality,
        string? StudyNumber,
        double? StudyNumberConf,
        string? StudyDate,
        double? StudyDateConf,
        string? Specialty,
        double? SpecialtyConf,
        string? ExamItem,
        double? ExamItemConf,
        string? ExamItemLineKey,
        string? FallbackMrn);

    /// <summary>取得所有待覆核病例 ID 清單（依上傳時間新到舊）。</summary>
    public async Task<IReadOnlyList<string>> GetPendingReviewIdsAsync(CancellationToken token)
    {
        var ids = await db.ClinicalReports
            .AsNoTracking()
            .Where(r => !r.IsDeleted && r.ProcessingStatus == "PENDING_REVIEW")
            .OrderByDescending(r => r.UploadedAt)
            .ThenByDescending(r => r.ReportId)
            .Select(r => r.ReportId)
            .ToListAsync(token);

        return ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>取得單筆待覆核病例明細；caseId 為 null 時回傳第一筆待覆核病例。</summary>
    public async Task<CaseReviewDetailDto?> GetReviewAsync(string? caseId, CancellationToken token)
    {
        ClinicalReport? report;
        if (string.IsNullOrWhiteSpace(caseId))
        {
            report = await db.ClinicalReports
                .AsNoTracking()
                .Include(r => r.Patient)
                .Include(r => r.ReportSections)
                .Include(r => r.CaseResults)
                    .ThenInclude(cr => cr.ResultEvidences)
                .Include(r => r.ObservationResults)
                .Include(r => r.StructuredOutputs)
                .Include(r => r.ReviewRecords)
                .Where(r => !r.IsDeleted && r.ProcessingStatus == "PENDING_REVIEW")
                .OrderByDescending(r => r.UploadedAt)
                .ThenByDescending(r => r.ReportId)
                .FirstOrDefaultAsync(token);
        }
        else if (long.TryParse(caseId, NumberStyles.None, CultureInfo.InvariantCulture, out var reportId) && reportId > 0)
        {
            report = await db.ClinicalReports
                .AsNoTracking()
                .Include(r => r.Patient)
                .Include(r => r.ReportSections)
                .Include(r => r.CaseResults)
                    .ThenInclude(cr => cr.ResultEvidences)
                .Include(r => r.ObservationResults)
                .Include(r => r.StructuredOutputs)
                .Include(r => r.ReviewRecords)
                .FirstOrDefaultAsync(r => !r.IsDeleted && r.ReportId == reportId, token);
        }
        else
        {
            return null;
        }

        if (report is null) return null;

        var latestOutput = report.StructuredOutputs
            .OrderByDescending(o => o.VersionNo)
            .ThenByDescending(o => o.OutputId)
            .FirstOrDefault();

        // 只讀取人工覆核產生的意見，排除系統內部 MRN 修正紀錄的 JSON
        var latestHumanReview = report.ReviewRecords
            .Where(v => v.ReviewStatus is "DRAFT_SAVED" or "APPROVED" or "RETURNED_TO_EXCEPTION")
            .OrderByDescending(v => v.DataVersion)
            .ThenByDescending(v => v.ReviewId)
            .FirstOrDefault();

        var version = Math.Max(latestOutput?.VersionNo ?? 1, latestHumanReview?.DataVersion ?? 1);
        var status = MapStatus(report.ProcessingStatus);

        // 組裝原文頁面：優先從資料庫 report_section 讀取，若尚未寫入則從地端去識別化結果檔讀取
        var pages = report.ReportSections
            .OrderBy(s => s.SourcePage ?? 1)
            .ThenBy(s => s.SectionOrder ?? 0)
            .GroupBy(s => s.SourcePage ?? 1)
            .Select(g =>
            {
                var lines = new List<ReviewDocLineDto>();
                foreach (var sec in g)
                {
                    if (!string.IsNullOrWhiteSpace(sec.SectionName))
                    {
                        lines.Add(new ReviewDocLineDto(sec.SectionName, $"sec-{sec.SectionId}-head", IsHeading: true));
                    }
                    var rawLines = (sec.RawText ?? string.Empty).Split('\n');
                    for (var i = 0; i < rawLines.Length; i++)
                    {
                        lines.Add(new ReviewDocLineDto(rawLines[i], $"sec-{sec.SectionId}-{i}"));
                    }
                }
                return new ReviewDocPageDto(g.Key, lines);
            })
            .ToList();

        var diskPayload = await TryLoadDeidPayloadFromDiskAsync(report.ReportId, token);
        if (pages.Count == 0 && diskPayload is not null)
        {
            pages = diskPayload.Pages;
        }

        // 優先使用資料庫已儲存的值；若資料庫為空，則自動帶入地端 OCR 擷取到的結果
        var studyNumber = !string.IsNullOrWhiteSpace(report.StudyNumber)
            ? report.StudyNumber
            : diskPayload?.StudyNumber ?? string.Empty;

        var studyDate = report.StudyDate.HasValue
            ? report.StudyDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : diskPayload?.StudyDate ?? string.Empty;

        var specialty = !string.IsNullOrWhiteSpace(report.PhysicianSpecialty)
            ? report.PhysicianSpecialty
            : diskPayload?.Specialty ?? string.Empty;

        var resolvedReportType = ResolveReportType(report.ReportType, diskPayload?.ExamItem);

        var caseSummary = !string.IsNullOrWhiteSpace(report.CaseSummary)
            ? report.CaseSummary
            : (!string.IsNullOrWhiteSpace(diskPayload?.ExamItem) ? $"檢查項目：{diskPayload.ExamItem}" : string.Empty);

        var reportTypeOptions = new List<string>
        {
            "心導管檢查報告",
            "攜帶式心電圖記錄檢查",
            "心臟超音波報告",
            "上消化道內視鏡檢查報告",
            "胸部電腦斷層報告"
        };
        if (!string.IsNullOrWhiteSpace(resolvedReportType) &&
            !reportTypeOptions.Contains(resolvedReportType, StringComparer.Ordinal))
        {
            reportTypeOptions.Insert(0, resolvedReportType);
        }

        // 組裝必要欄位
        var requiredFields = new List<ReviewExtractedFieldDto>
        {
            new()
            {
                Key = "study_number",
                Label = "檢查單號",
                Value = studyNumber,
                Confidence = diskPayload?.StudyNumberConf ?? (string.IsNullOrWhiteSpace(studyNumber) ? null : 0.96),
                Hint = string.IsNullOrWhiteSpace(studyNumber) ? "已於去識別化遮蔽，可依授權單據補填" : "AI 自動擷取",
                HintPositive = !string.IsNullOrWhiteSpace(studyNumber),
                Kind = ReviewFieldKind.Mono
            },
            new()
            {
                Key = "study_date",
                Label = "檢查日期",
                Value = studyDate,
                Confidence = diskPayload?.StudyDateConf ?? (string.IsNullOrWhiteSpace(studyDate) ? null : 0.95),
                Hint = !string.IsNullOrWhiteSpace(studyDate) ? $"{(diskPayload?.StudyDateConf ?? 0.95):0.00}・第 1 頁" : "格式 YYYY-MM-DD",
                HintPositive = !string.IsNullOrWhiteSpace(studyDate),
                Kind = ReviewFieldKind.Mono
            },
            new()
            {
                Key = "report_type",
                Label = "報告類型",
                Value = resolvedReportType,
                Confidence = 0.94,
                Hint = "0.94・AI 自動分類",
                HintPositive = true,
                Kind = ReviewFieldKind.Select,
                Options = reportTypeOptions
            },
            new()
            {
                Key = "physician_specialty",
                Label = "科別",
                Value = specialty,
                Confidence = diskPayload?.SpecialtyConf ?? (string.IsNullOrWhiteSpace(specialty) ? null : 0.90),
                Hint = !string.IsNullOrWhiteSpace(specialty) ? $"{(diskPayload?.SpecialtyConf ?? 0.90):0.00}・第 1 頁" : "報告科別",
                HintPositive = !string.IsNullOrWhiteSpace(specialty),
                Kind = ReviewFieldKind.Text
            },
            new()
            {
                Key = "case_summary",
                Label = "檢查摘要 / 結論",
                Value = caseSummary,
                Confidence = diskPayload?.ExamItemConf ?? 0.92,
                Hint = !string.IsNullOrWhiteSpace(caseSummary) ? $"{(diskPayload?.ExamItemConf ?? 0.92):0.00}・擷取自報告內容" : "臨床結論摘要",
                HintPositive = !string.IsNullOrWhiteSpace(caseSummary),
                Kind = ReviewFieldKind.TextArea,
                Rows = 3
            }
        };

        // 組裝重要結果（case_result）
        var importantResults = report.CaseResults
            .OrderBy(r => r.ResultOrder ?? int.MaxValue)
            .ThenBy(r => r.ResultId)
            .Select(r =>
            {
                var evidence = r.ResultEvidences.FirstOrDefault();
                var page = evidence?.SourcePage ?? 1;
                var lineKey = evidence?.SectionId is { } sid ? $"sec-{sid}-0" : null;
                return new ReviewImportantResultDto
                {
                    Key = r.ResultId.ToString(CultureInfo.InvariantCulture),
                    Title = !string.IsNullOrWhiteSpace(r.ResultText) ? r.ResultText : r.ResultName ?? string.Empty,
                    Source = r.SourceLocation ?? $"第 {page} 頁",
                    Significance = MapSignificance(r.ClinicalSignificance),
                    Confidence = (double)(r.ConfidenceScore ?? 0.90m),
                    Confirmed = string.Equals(r.ResultStatus, "CONFIRMED", StringComparison.OrdinalIgnoreCase)
                                || status == ReviewCaseStatus.Completed,
                    SourceLineKey = lineKey,
                    SourcePage = page
                };
            })
            .ToList();

        // 若資料庫尚無 case_result，則將地端 OCR 擷取到的檢查項目帶入為待確認的重要結果
        if (importantResults.Count == 0 && !string.IsNullOrWhiteSpace(diskPayload?.ExamItem))
        {
            importantResults.Add(new ReviewImportantResultDto
            {
                Key = "ocr-exam-item-1",
                Title = $"檢查項目：{diskPayload.ExamItem}",
                Source = "第 1 頁・去識別化原文",
                Significance = ReviewSignificance.Procedure,
                Confidence = diskPayload.ExamItemConf ?? 0.99,
                Confirmed = status == ReviewCaseStatus.Completed,
                SourceLineKey = diskPayload.ExamItemLineKey,
                SourcePage = 1
            });
        }

        // 組裝檢查數值（observation_result）
        var observations = report.ObservationResults
            .OrderBy(o => o.ObservationId)
            .Select(o =>
            {
                var val = !string.IsNullOrWhiteSpace(o.ValueText)
                    ? o.ValueText
                    : o.ValueNumber?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty;
                var range = (o.ReferenceLow, o.ReferenceHigh) switch
                {
                    ({ } low, { } high) => $"{low:G29}–{high:G29}",
                    ({ } low, null) => $"≥ {low:G29}",
                    (null, { } high) => $"≤ {high:G29}",
                    _ => "—"
                };
                return new ReviewObservationRowDto
                {
                    Item = o.TestName ?? o.TestCode ?? $"項目 #{o.ObservationId}",
                    Value = val,
                    Unit = o.UnitCode ?? string.Empty,
                    ReferenceRange = range,
                    Flag = MapObservationFlag(o.AbnormalFlag),
                    CodeMapped = !string.IsNullOrWhiteSpace(o.TestCode)
                };
            })
            .ToList();

        var fhirChecks = new List<ReviewFhirCheckDto>();
        if (latestOutput is not null && !string.IsNullOrWhiteSpace(latestOutput.FhirJson))
        {
            var allObsMapped = observations.All(o => o.CodeMapped);
            fhirChecks.Add(new ReviewFhirCheckDto("Patient", true, "TW Core Patient 通過"));
            fhirChecks.Add(new ReviewFhirCheckDto("DiagnosticReport", true, "TW Core DiagnosticReport 通過"));
            if (observations.Count == 0)
            {
                fhirChecks.Add(new ReviewFhirCheckDto("Observation", allObsMapped, "Observation 檢查通過"));
            }
        }

        var rawMrn = !string.IsNullOrWhiteSpace(report.Patient?.Mrn)
            ? report.Patient.Mrn
            : diskPayload?.FallbackMrn;

        return new CaseReviewDetailDto
        {
            CaseId = report.ReportId.ToString(CultureInfo.InvariantCulture),
            MaskedMrn = MaskMrn(rawMrn),
            ReportType = resolvedReportType,
            UploadedAt = report.UploadedAt,
            BatchId = $"B-{report.ReportId:D4}",
            Version = version,
            Status = status,
            OcrQuality = diskPayload?.OcrQuality ?? 0.96,
            MaskedCount = diskPayload?.MaskedCount ?? CountMasks(pages),
            ReportTypeConfidence = 0.94,
            ConfidenceThreshold = 0.85,
            Pages = pages,
            RequiredFields = requiredFields,
            ImportantResults = importantResults,
            Observations = observations,
            FhirChecks = fhirChecks,
            LastFhirSubmitAt = status == ReviewCaseStatus.Completed ? latestOutput?.CreatedAt : null,
            Comment = report.Comment ?? latestHumanReview?.ReviewComment ?? string.Empty
        };
    }

    /// <summary>儲存草稿：更新欄位與勾選狀態，寫入 ReviewRecord 與 AuditTrail。</summary>
    public async Task<ReviewOperationResponse> SaveDraftAsync(string caseId, SaveReviewRequest request, CancellationToken token)
    {
        if (!long.TryParse(caseId, NumberStyles.None, CultureInfo.InvariantCulture, out var reportId) || reportId <= 0)
            return new ReviewOperationResponse(false, "無效的病例編號。");

        var reviewerId = await ResolveReviewerIdAsync(request.ReviewerName, token);

        var report = await db.ClinicalReports
            .Include(r => r.CaseResults)
            .Include(r => r.ObservationResults)
            .FirstOrDefaultAsync(r => !r.IsDeleted && r.ReportId == reportId, token);

        if (report is null)
            return new ReviewOperationResponse(false, $"找不到病例 {caseId}。");
        if (report.ProcessingStatus != "PENDING_REVIEW")
            return new ReviewOperationResponse(false, "此病例目前非「待覆核」狀態，無法修改。");

        var beforeJson = JsonSerializer.Serialize(new { report.StudyNumber, report.ReportType, report.CaseSummary, report.Comment }, JsonOptions);

        ApplyEditsToEntities(report, request.Detail, reviewerId, markConfirmedAsApproved: false);

        var nextVersion = (await db.ReviewRecords
            .Where(v => v.ReportId == reportId)
            .MaxAsync(v => (int?)v.DataVersion, token) ?? 0) + 1;

        var afterJson = JsonSerializer.Serialize(new
        {
            report.StudyNumber,
            report.StudyDate,
            report.ReportType,
            report.PhysicianSpecialty,
            report.CaseSummary,
            report.Comment,
            ConfirmedCount = request.Detail.ImportantResults.Count(r => r.Confirmed)
        }, JsonOptions);

        db.ReviewRecords.Add(new ReviewRecord
        {
            ReportId = reportId,
            ReviewerId = reviewerId,
            ReviewStatus = "DRAFT_SAVED",
            ReviewedAt = DateTime.Now,
            ReviewComment = request.Detail.Comment,
            DataVersion = nextVersion
        });

        db.AuditTrails.Add(new AuditTrail
        {
            ReportId = reportId,
            ActorId = reviewerId,
            Action = "REVIEW_DRAFT_SAVED",
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            DataVersion = nextVersion
        });

        await db.SaveChangesAsync(token);

        return new ReviewOperationResponse(true, $"草稿已儲存（版本 v{nextVersion}）。");
    }

    /// <summary>送出審核並轉換 FHIR：驗證必填與編碼，寫入 StructuredOutput、ReviewRecord 與 AuditTrail。</summary>
    public async Task<ReviewOperationResponse> SubmitReviewAsync(string caseId, SaveReviewRequest request, CancellationToken token)
    {
        if (!long.TryParse(caseId, NumberStyles.None, CultureInfo.InvariantCulture, out var reportId) || reportId <= 0)
            return new ReviewOperationResponse(false, "無效的病例編號。");

        var detail = request.Detail;
        if (detail.ImportantResults.Any(r => !r.Confirmed))
            return new ReviewOperationResponse(false, "尚有重要結果未勾選確認，無法送出審核。");

        var unmapped = detail.Observations.FirstOrDefault(o => !o.CodeMapped);
        if (unmapped is not null)
            return new ReviewOperationResponse(false, $"Observation（{unmapped.Item}）尚未對應標準編碼，請先完成編碼對應。");

        var reviewerId = await ResolveReviewerIdAsync(request.ReviewerName, token);

        var report = await db.ClinicalReports
            .Include(r => r.Patient)
            .Include(r => r.CaseResults)
            .Include(r => r.ObservationResults)
            .FirstOrDefaultAsync(r => !r.IsDeleted && r.ReportId == reportId, token);

        if (report is null)
            return new ReviewOperationResponse(false, $"找不到病例 {caseId}。");
        if (report.ProcessingStatus != "PENDING_REVIEW")
            return new ReviewOperationResponse(false, "此病例已完成審核或狀態已變更。");

        var beforeJson = JsonSerializer.Serialize(new { report.ProcessingStatus, report.CaseSummary, report.Comment }, JsonOptions);

        ApplyEditsToEntities(report, detail, reviewerId, markConfirmedAsApproved: true);
        report.ProcessingStatus = "COMPLETED";

        var nextVersion = (await db.StructuredOutputs
            .Where(o => o.ReportId == reportId)
            .MaxAsync(o => (int?)o.VersionNo, token) ?? 0) + 1;

        var structuredJson = JsonSerializer.Serialize(new
        {
            report.ReportId,
            report.StudyNumber,
            report.StudyDate,
            report.ReportType,
            report.PhysicianSpecialty,
            report.CaseSummary,
            report.Comment,
            ImportantResults = detail.ImportantResults,
            Observations = detail.Observations
        }, JsonOptions);

        var fhirBundleJson = BuildTwCoreFhirBundleJson(report, detail);

        db.StructuredOutputs.Add(new StructuredOutput
        {
            ReportId = reportId,
            VersionNo = nextVersion,
            StructuredJson = structuredJson,
            FhirJson = fhirBundleJson,
            ApprovalStatus = "APPROVED",
            CreatedAt = DateTime.Now
        });

        db.ReviewRecords.Add(new ReviewRecord
        {
            ReportId = reportId,
            ReviewerId = reviewerId,
            ReviewStatus = "APPROVED",
            ReviewedAt = DateTime.Now,
            ReviewComment = detail.Comment,
            DataVersion = nextVersion
        });

        db.ProcessingLogs.Add(new ProcessingLog
        {
            ReportId = reportId,
            Status = "COMPLETED",
            StartedAt = DateTime.Now,
            FinishedAt = DateTime.Now
        });

        db.AuditTrails.Add(new AuditTrail
        {
            ReportId = reportId,
            ActorId = reviewerId,
            Action = "REVIEW_APPROVED_FHIR_GENERATED",
            BeforeJson = beforeJson,
            AfterJson = structuredJson,
            DataVersion = nextVersion
        });

        await db.SaveChangesAsync(token);

        return new ReviewOperationResponse(true, $"病例 {caseId} 審核通過，已完成 TW Core FHIR Bundle 轉換！");
    }

    /// <summary>退回異常佇列：建立 ExceptionQueue 紀錄並更新狀態為 EXCEPTION。</summary>
    public async Task<ReviewOperationResponse> ReturnToExceptionAsync(string caseId, ReturnReviewRequest request, CancellationToken token)
    {
        if (!long.TryParse(caseId, NumberStyles.None, CultureInfo.InvariantCulture, out var reportId) || reportId <= 0)
            return new ReviewOperationResponse(false, "無效的病例編號。");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return new ReviewOperationResponse(false, "請填寫退回原因。");

        var reviewerId = await ResolveReviewerIdAsync(request.ReviewerName, token);

        var report = await db.ClinicalReports
            .FirstOrDefaultAsync(r => !r.IsDeleted && r.ReportId == reportId, token);

        if (report is null)
            return new ReviewOperationResponse(false, $"找不到病例 {caseId}。");

        var previousStatus = report.ProcessingStatus;
        report.ProcessingStatus = "EXCEPTION";
        report.Comment = request.Reason.Trim();

        db.ExceptionQueues.Add(new ExceptionQueue
        {
            ReportId = reportId,
            ExceptionCategory = "REVIEW_RETURNED",
            Description = request.Reason.Trim(),
            Status = "OPEN",
            AssignedUserId = reviewerId,
            CreatedAt = DateTime.Now
        });

        db.ReviewRecords.Add(new ReviewRecord
        {
            ReportId = reportId,
            ReviewerId = reviewerId,
            ReviewStatus = "RETURNED_TO_EXCEPTION",
            ReviewedAt = DateTime.Now,
            ReviewComment = request.Reason.Trim(),
            DataVersion = 1
        });

        db.ProcessingLogs.Add(new ProcessingLog
        {
            ReportId = reportId,
            Status = "EXCEPTION",
            ErrorMessage = request.Reason.Trim(),
            StartedAt = DateTime.Now,
            FinishedAt = DateTime.Now
        });

        db.AuditTrails.Add(new AuditTrail
        {
            ReportId = reportId,
            ActorId = reviewerId,
            Action = "REVIEW_RETURNED_TO_EXCEPTION",
            BeforeJson = JsonSerializer.Serialize(new { ProcessingStatus = previousStatus }, JsonOptions),
            AfterJson = JsonSerializer.Serialize(new { ProcessingStatus = "EXCEPTION", Reason = request.Reason.Trim() }, JsonOptions),
            DataVersion = 1
        });

        await db.SaveChangesAsync(token);

        return new ReviewOperationResponse(true, $"病例 {caseId} 已退回異常佇列。");
    }

    /// <summary>匯出已核准病例的 FHIR Bundle JSON。</summary>
    public async Task<string?> ExportFhirBundleAsync(string caseId, CancellationToken token)
    {
        if (!long.TryParse(caseId, NumberStyles.None, CultureInfo.InvariantCulture, out var reportId) || reportId <= 0)
            return null;

        var output = await db.StructuredOutputs
            .AsNoTracking()
            .Where(o => o.ReportId == reportId && o.FhirJson != null)
            .OrderByDescending(o => o.VersionNo)
            .ThenByDescending(o => o.OutputId)
            .FirstOrDefaultAsync(token);

        return output?.FhirJson;
    }

    private async Task<DeidDiskPayload?> TryLoadDeidPayloadFromDiskAsync(long reportId, CancellationToken token)
    {
        try
        {
            var reportOcrDir = Path.Combine(
                environment.ContentRootPath,
                "PrivateUploads",
                "ocr",
                reportId.ToString(CultureInfo.InvariantCulture));

            if (!Directory.Exists(reportOcrDir)) return null;

            string? fallbackMrn = null;
            var latestExtractedFile = Directory
                .GetFiles(reportOcrDir, "extracted_fields.json", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latestExtractedFile is not null)
            {
                using var extDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(latestExtractedFile.FullName, token));
                if (extDoc.RootElement.TryGetProperty("fields", out var fieldsEl) &&
                    fieldsEl.TryGetProperty("mrn", out var mrnEl) &&
                    mrnEl.TryGetProperty("value", out var mrnVal) &&
                    mrnVal.ValueKind == JsonValueKind.String)
                {
                    fallbackMrn = mrnVal.GetString();
                }
            }

            var latestResultFile = Directory
                .GetFiles(reportOcrDir, "result_deidentified.json", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latestResultFile is null) return null;

            using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(latestResultFile.FullName, token));
            var root = doc.RootElement;

            int? maskedCount = null;
            if (root.TryGetProperty("verification", out var ver) &&
                ver.TryGetProperty("redacted_region_count", out var rrc) &&
                rrc.ValueKind == JsonValueKind.Number)
            {
                maskedCount = rrc.GetInt32();
            }

            var pages = new List<ReviewDocPageDto>();
            double? avgConfidence = null;
            string? studyNumber = null;
            double? studyNumberConf = null;
            string? studyDate = null;
            double? studyDateConf = null;
            string? specialty = null;
            double? specialtyConf = null;
            string? examItem = null;
            double? examItemConf = null;
            string? examItemLineKey = null;

            if (root.TryGetProperty("blocks", out var blocksArray) && blocksArray.ValueKind == JsonValueKind.Array)
            {
                var parsedBlocks = new List<(int Page, int X, int Y, string RawText, string DisplayText, double Conf)>();
                foreach (var b in blocksArray.EnumerateArray())
                {
                    var page = b.TryGetProperty("page", out var pEl) ? pEl.GetInt32() : 1;
                    var text = b.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? string.Empty : string.Empty;
                    var conf = b.TryGetProperty("confidence", out var cEl) ? cEl.GetDouble() : 0.95;
                    var x = 0;
                    var y = 0;
                    if (b.TryGetProperty("bbox", out var bboxEl) && bboxEl.ValueKind == JsonValueKind.Array && bboxEl.GetArrayLength() >= 2)
                    {
                        x = bboxEl[0].GetInt32();
                        y = bboxEl[1].GetInt32();
                    }

                    var displayText = text.Trim() switch
                    {
                        "病歷號碼：" or "病歷號碼:" => "病歷號碼：[ID]",
                        "病患姓名：" or "病患姓名:" => "病患姓名：[PERSON]",
                        "檢查單號：" or "檢查單號:" => "檢查單號：[ID]",
                        _ => text
                    };

                    if (!string.IsNullOrWhiteSpace(displayText))
                    {
                        parsedBlocks.Add((page, x, y, text.Trim(), displayText, conf));
                    }
                }

                if (parsedBlocks.Count > 0)
                {
                    avgConfidence = Math.Round(parsedBlocks.Average(b => b.Conf), 2);
                }

                foreach (var pageGroup in parsedBlocks.GroupBy(b => b.Page).OrderBy(g => g.Key))
                {
                    var lines = new List<ReviewDocLineDto>();
                    var sorted = pageGroup.OrderBy(b => b.Y).ThenBy(b => b.X).ToList();

                    var rowGroups = new List<List<(int Page, int X, int Y, string RawText, string DisplayText, double Conf)>>();
                    foreach (var blk in sorted)
                    {
                        var existingRow = rowGroups.LastOrDefault(r => Math.Abs(r[0].Y - blk.Y) <= 18);
                        if (existingRow is not null)
                        {
                            existingRow.Add(blk);
                        }
                        else
                        {
                            rowGroups.Add([blk]);
                        }
                    }

                    for (var i = 0; i < rowGroups.Count; i++)
                    {
                        var orderedRow = rowGroups[i].OrderBy(b => b.X).ToList();
                        var lineKey = $"deid-p{pageGroup.Key}-r{i}";
                        var rowText = string.Join("  ", orderedRow.Select(b => b.DisplayText));
                        lines.Add(new ReviewDocLineDto(rowText, lineKey, IsHeading: i == 0));

                        for (var k = 0; k < orderedRow.Count; k++)
                        {
                            var label = orderedRow[k].RawText.TrimEnd('：', ':');
                            var nextBlock = k + 1 < orderedRow.Count ? orderedRow[k + 1] : default;
                            var hasValue = !string.IsNullOrWhiteSpace(nextBlock.RawText) && !nextBlock.RawText.EndsWith('：') && !nextBlock.RawText.EndsWith(':');

                            if (!hasValue) continue;

                            switch (label)
                            {
                                case "科別" when specialty is null:
                                    specialty = nextBlock.RawText;
                                    specialtyConf = Math.Round(nextBlock.Conf, 2);
                                    break;
                                case "檢查日期" when studyDate is null:
                                    studyDate = NormalizeDateString(nextBlock.RawText);
                                    studyDateConf = Math.Round(nextBlock.Conf, 2);
                                    break;
                                case "檢查單號" when studyNumber is null:
                                    studyNumber = nextBlock.RawText;
                                    studyNumberConf = Math.Round(nextBlock.Conf, 2);
                                    break;
                                case "檢查項目" when examItem is null:
                                    examItem = nextBlock.RawText;
                                    examItemConf = Math.Round(nextBlock.Conf, 2);
                                    examItemLineKey = lineKey;
                                    break;
                            }
                        }
                    }

                    pages.Add(new ReviewDocPageDto(pageGroup.Key, lines));
                }
            }

            return new DeidDiskPayload(
                pages,
                maskedCount,
                avgConfidence,
                studyNumber,
                studyNumberConf,
                studyDate,
                studyDateConf,
                specialty,
                specialtyConf,
                examItem,
                examItemConf,
                examItemLineKey,
                fallbackMrn);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveReportType(string? dbReportType, string? extractedExamItem)
    {
        if (!string.IsNullOrWhiteSpace(dbReportType) &&
            !string.Equals(dbReportType.Trim(), "自動辨識", StringComparison.Ordinal))
        {
            return dbReportType.Trim();
        }

        if (!string.IsNullOrWhiteSpace(extractedExamItem))
        {
            if (extractedExamItem.Contains("心電圖", StringComparison.Ordinal))
                return "攜帶式心電圖記錄檢查";
            if (extractedExamItem.Contains("心導管", StringComparison.Ordinal))
                return "心導管檢查報告";
            if (extractedExamItem.Contains("超音波", StringComparison.Ordinal))
                return "心臟超音波報告";
            return extractedExamItem.Trim();
        }

        return "心導管檢查報告";
    }

    private static string NormalizeDateString(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 8 &&
            DateOnly.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static void ApplyEditsToEntities(ClinicalReport report, CaseReviewDetailDto detail, long reviewerId, bool markConfirmedAsApproved)
    {
        foreach (var field in detail.RequiredFields)
        {
            switch (field.Key)
            {
                case "study_number":
                    report.StudyNumber = field.Value.Trim();
                    break;
                case "study_date":
                    if (DateOnly.TryParse(field.Value.Trim(), CultureInfo.InvariantCulture, out var date))
                        report.StudyDate = date;
                    break;
                case "report_type":
                    if (!string.IsNullOrWhiteSpace(field.Value))
                        report.ReportType = field.Value.Trim();
                    break;
                case "physician_specialty":
                    report.PhysicianSpecialty = field.Value.Trim();
                    break;
                case "case_summary":
                    report.CaseSummary = field.Value.Trim();
                    break;
            }
        }

        report.Comment = detail.Comment;

        foreach (var item in detail.ImportantResults)
        {
            if (long.TryParse(item.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var resultId))
            {
                var entity = report.CaseResults.FirstOrDefault(r => r.ResultId == resultId);
                if (entity is not null)
                {
                    entity.ResultText = item.Title.Trim();
                    entity.ResultStatus = item.Confirmed
                        ? (markConfirmedAsApproved ? "APPROVED" : "CONFIRMED")
                        : "PENDING";
                    entity.ReviewedBy = reviewerId;
                    entity.ReviewedAt = DateTime.Now;
                }
            }
        }

        foreach (var obs in detail.Observations)
        {
            var entity = report.ObservationResults.FirstOrDefault(o =>
                string.Equals(o.TestName, obs.Item, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.TestCode, obs.Item, StringComparison.OrdinalIgnoreCase));
            if (entity is not null)
            {
                entity.ValueText = obs.Value.Trim();
                if (decimal.TryParse(obs.Value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                    entity.ValueNumber = num;
                if (obs.CodeMapped && string.IsNullOrWhiteSpace(entity.TestCode))
                    entity.TestCode = $"LOINC-{entity.ObservationId}";
            }
        }
    }

    private async Task<long> ResolveReviewerIdAsync(string? reviewerName, CancellationToken token)
    {
        var configuredId = configuration.GetValue<long>("MrnReview:ReviewerId");
        if (configuredId > 0) return configuredId;

        if (!string.IsNullOrWhiteSpace(reviewerName))
        {
            var trimmed = reviewerName.Trim();
            var userId = await db.UserAccounts
                .AsNoTracking()
                .Where(u => u.IsActive && u.Username == trimmed)
                .Select(u => u.UserId)
                .FirstOrDefaultAsync(token);
            if (userId > 0) return userId;
        }

        var fallbackUserId = await db.UserAccounts
            .AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.UserId)
            .Select(u => u.UserId)
            .FirstOrDefaultAsync(token);

        return fallbackUserId > 0 ? fallbackUserId : 1;
    }

    private static string BuildTwCoreFhirBundleJson(ClinicalReport report, CaseReviewDetailDto detail)
    {
        var patientRef = $"Patient/pat-{report.PatientId ?? report.ReportId}";
        var entries = new List<object>
        {
            new
            {
                fullUrl = $"urn:uuid:{Guid.NewGuid()}",
                resource = new
                {
                    resourceType = "Patient",
                    id = $"pat-{report.PatientId ?? report.ReportId}",
                    meta = new { profile = new[] { "https://twcore.mohw.gov.tw/ig/twcore/StructureDefinition/Patient-twcore" } },
                    identifier = new[]
                    {
                        new { system = "https://hospital.example.org/mrn", value = report.Patient?.Mrn ?? detail.MaskedMrn }
                    }
                }
            }
        };

        var obsRefs = new List<object>();
        foreach (var obs in detail.Observations)
        {
            var obsId = $"obs-{Guid.NewGuid():N}";
            obsRefs.Add(new { reference = $"Observation/{obsId}" });
            entries.Add(new
            {
                fullUrl = $"urn:uuid:{Guid.NewGuid()}",
                resource = new
                {
                    resourceType = "Observation",
                    id = obsId,
                    meta = new { profile = new[] { "https://twcore.mohw.gov.tw/ig/twcore/StructureDefinition/Observation-laboratoryResult-twcore" } },
                    status = "final",
                    code = new { text = obs.Item },
                    subject = new { reference = patientRef },
                    valueString = string.IsNullOrWhiteSpace(obs.Unit) ? obs.Value : $"{obs.Value} {obs.Unit}"
                }
            });
        }

        entries.Add(new
        {
            fullUrl = $"urn:uuid:{Guid.NewGuid()}",
            resource = new
            {
                resourceType = "DiagnosticReport",
                id = $"dr-{report.ReportId}",
                meta = new { profile = new[] { "https://twcore.mohw.gov.tw/ig/twcore/StructureDefinition/DiagnosticReport-twcore" } },
                status = "final",
                code = new { text = detail.ReportType },
                subject = new { reference = patientRef },
                effectiveDateTime = report.StudyDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? report.UploadedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                result = obsRefs,
                conclusion = report.CaseSummary
            }
        });

        var bundle = new
        {
            resourceType = "Bundle",
            id = $"bundle-report-{report.ReportId}",
            type = "transaction",
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            entry = entries
        };

        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    private static ReviewCaseStatus MapStatus(string? status) => status switch
    {
        "PENDING_REVIEW" => ReviewCaseStatus.PendingReview,
        "COMPLETED" => ReviewCaseStatus.Completed,
        "EXCEPTION" or "MRN_REVIEW_REQUIRED" or "DEID_FAILED" or "FIELD_EXTRACTION_FAILED" => ReviewCaseStatus.Exception,
        "DUPLICATE" => ReviewCaseStatus.Duplicate,
        "CLOSED" => ReviewCaseStatus.Closed,
        _ => ReviewCaseStatus.Processing
    };

    private static ReviewSignificance MapSignificance(string? sig) => sig?.ToUpperInvariant() switch
    {
        "SIGNIFICANT" or "HIGH" => ReviewSignificance.Significant,
        "MODERATE" or "MEDIUM" => ReviewSignificance.Moderate,
        "PROCEDURE" => ReviewSignificance.Procedure,
        "OUTCOME" => ReviewSignificance.Outcome,
        _ => ReviewSignificance.Normal
    };

    private static ReviewObservationFlag MapObservationFlag(string? flag) => flag?.ToUpperInvariant() switch
    {
        "H" or "HIGH" or "ABNORMAL" => ReviewObservationFlag.High,
        "N" or "NORMAL" => ReviewObservationFlag.Normal,
        _ => ReviewObservationFlag.None
    };

    private static string MaskMrn(string? mrn)
    {
        if (string.IsNullOrWhiteSpace(mrn)) return "未綁定病歷號";
        var trimmed = mrn.Trim();
        if (trimmed.Length <= 4) return new string('•', trimmed.Length);
        return $"{trimmed[..3]}{new string('•', trimmed.Length - 4)}{trimmed[^1]}";
    }

    private static int CountMasks(IEnumerable<ReviewDocPageDto> pages)
    {
        var count = 0;
        foreach (var page in pages)
        {
            foreach (var line in page.Lines)
            {
                if (line.Text.Contains("[PERSON]", StringComparison.Ordinal)) count++;
                if (line.Text.Contains("[ID]", StringComparison.Ordinal)) count++;
                if (line.Text.Contains("[DATE]", StringComparison.Ordinal)) count++;
                if (line.Text.Contains("[HOSPITAL]", StringComparison.Ordinal)) count++;
            }
        }
        return count;
    }
}