# =====================================================
# 病歷圖片去識別化流程（全部於本機執行）
#
#   1. (選用) PaddleOCR 第一階段辨識          .venv
#   2. Presidio 個資偵測 + OCR 座標對應       .venv-deid
#   3. 圖片實際遮蔽 + 重新 OCR                .venv
#   4. 驗證並產生去識別化 JSON                .venv-deid
#
# 兩個虛擬環境以 JSON 交換資料（避免 PyYAML 相依性衝突）
#
# PDF：加上 -Pdf，逐頁轉圖片後執行上述流程，最後合併並跨頁檢查
#   .\ocr\run_image_deid.ps1 -Pdf test_pdfs\sample.pdf
# =====================================================

param(
    [string]$Image = "test_images\patient_test.jpg",
    [string]$OcrJson = "output\private\patient_test_ocr_result.json",
    [string]$Name = "patient_test",
    [switch]$RunInitialOcr,
    [string]$ExpectedPii = "",
    [string]$Pdf = "",
    [string]$PdfOcrJsonPath = "",
    [string]$PdfPageImageDir = "",
    [string]$ResultDirectory = "",
    [string]$OcrPython = "",
    [string]$DeidPython = ""
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$OcrDir = Join-Path $Root "ocr"
$Py = Join-Path $Root ".venv\Scripts\python.exe"
$PyDeid = Join-Path $Root ".venv-deid\Scripts\python.exe"

function Resolve-ProjectPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $Root $Path
}

$Image = Resolve-ProjectPath $Image
$OcrJson = Resolve-ProjectPath $OcrJson
if ($OcrPython) { $Py = Resolve-ProjectPath $OcrPython }
if ($DeidPython) { $PyDeid = Resolve-ProjectPath $DeidPython }
if ($ExpectedPii) { $ExpectedPii = Resolve-ProjectPath $ExpectedPii }

if ($Pdf) {
    $Pdf = Resolve-ProjectPath $Pdf
    if (-not $PSBoundParameters.ContainsKey("Name")) {
        $Name = [System.IO.Path]::GetFileNameWithoutExtension($Pdf)
    }
}

$OutputDir = Join-Path $Root "output"
if ($ResultDirectory) { $OutputDir = Resolve-ProjectPath $ResultDirectory }
$PrivateDir = Join-Path $OutputDir "private"
New-Item -ItemType Directory -Force $PrivateDir | Out-Null

$Plan = Join-Path $PrivateDir "${Name}_mask_plan.json"
$Report = Join-Path $PrivateDir "${Name}_redaction_report.json"
$ReOcr = Join-Path $PrivateDir "${Name}_reocr_raw.json"
$Verification = Join-Path $PrivateDir "${Name}_verification_report.json"
$DeidImage = Join-Path $OutputDir "${Name}_deidentified.png"
$FinalJson = Join-Path $OutputDir "${Name}_deidentified.json"
# 驗證通過後，上述暫存檔整併為這一份本機紀錄（含原始個資，不可上傳）
$PrivateRecord = Join-Path $PrivateDir "${Name}_private_record.json"

# 中文輸出與離線執行（不檢查線上模型來源）
$env:PYTHONIOENCODING = "utf-8"
$env:PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK = "True"

function Invoke-Step([string]$Title, [string]$Python, [string[]]$Arguments) {
    Write-Host ""
    Write-Host "==================== $Title ====================" -ForegroundColor Cyan
    Push-Location $OcrDir
    # PaddleOCR 會將一般訊息寫到 stderr，不可視為錯誤；改以結束代碼判斷
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Python @Arguments
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
        Pop-Location
    }
    if ($code -ne 0) {
        Write-Host "流程停止於：$Title（結束代碼 $code），請人工確認。" -ForegroundColor Red
        exit $code
    }
}

if ($Pdf) {
    # ---------- PDF：逐頁處理 ----------
    # 原始頁面圖片與 OCR 含個資，存放於 private
    $PdfOcrJson = Join-Path $PrivateDir "${Name}_ocr_result.json"
    $PageImageDir = Join-Path $PrivateDir "${Name}_pages"
    if ($PdfOcrJsonPath -or $PdfPageImageDir) {
        if (-not $PdfOcrJsonPath -or -not $PdfPageImageDir -or $RunInitialOcr) {
            throw "重用原始 OCR 時必須提供 JSON 與頁面目錄。"
        }
        $PdfOcrJson = Resolve-ProjectPath $PdfOcrJsonPath
        $PageImageDir = Resolve-ProjectPath $PdfPageImageDir
        if (-not (Test-Path -LiteralPath $PdfOcrJson -PathType Leaf) -or
            -not (Test-Path -LiteralPath $PageImageDir -PathType Container)) {
            throw "找不到既有的 OCR JSON 或頁面目錄。"
        }
    }
    $DeidPageDir = Join-Path $OutputDir "${Name}_deidentified_pages"
    New-Item -ItemType Directory -Force $DeidPageDir | Out-Null

    # 開始前移除舊的合併結果，避免流程中途停止時誤用過期檔案
    if (Test-Path $FinalJson) { Remove-Item $FinalJson }

    if ($RunInitialOcr -or -not (Test-Path $PdfOcrJson)) {
        Invoke-Step "Step 1 PDF 轉圖片與第一階段 OCR" $Py @(
            "test_pdf.py", "--pdf", $Pdf, "--output", $PdfOcrJson, "--image-dir", $PageImageDir
        )
    }

    $PageCount = (Get-Content $PdfOcrJson -Raw -Encoding UTF8 | ConvertFrom-Json).total_pages

    # 一次偵測所有頁面：某頁確認的個資（例如醫師姓名）在其他頁無標籤出現時也會遮蔽
    Invoke-Step "Step 2-3 Presidio 個資偵測（全部 $PageCount 頁）" $PyDeid @(
        "pii_block_detector.py", "--ocr-json", $PdfOcrJson, "--all-pages",
        "--plan", (Join-Path $PrivateDir "${Name}_p{page}_mask_plan.json")
    )
    $PageFinals = @()
    $PagePlans = @()
    $PageReports = @()
    $PageVerifications = @()
    $PageReOcrs = @()

    for ($page = 1; $page -le $PageCount; $page++) {
        $p = "p{0:D3}" -f $page
        $PagePlan = Join-Path $PrivateDir "${Name}_${p}_mask_plan.json"
        $PageReport = Join-Path $PrivateDir "${Name}_${p}_redaction_report.json"
        $PageReOcr = Join-Path $PrivateDir "${Name}_${p}_reocr_raw.json"
        $PageVerification = Join-Path $PrivateDir "${Name}_${p}_verification_report.json"
        $PageFinal = Join-Path $PrivateDir "${Name}_${p}_deidentified.json"
        $PageImage = Join-Path $PageImageDir ("page_{0:D3}.png" -f $page)
        $PageDeidImage = Join-Path $DeidPageDir ("page_{0:D3}.png" -f $page)

        Invoke-Step "第 $page/$PageCount 頁 Step 4-5 圖片遮蔽與重新 OCR" $Py @(
            "image_redactor.py", "--image", $PageImage, "--plan", $PagePlan,
            "--output", $PageDeidImage, "--reocr-json", $PageReOcr, "--report", $PageReport
        )

        $pageVerifyArgs = @(
            "verify_deid.py", "--plan", $PagePlan, "--report", $PageReport,
            "--reocr-json", $PageReOcr, "--output", $PageFinal,
            "--verification-report", $PageVerification,
            "--source-type", "pdf", "--page", $page
        )
        if ($ExpectedPii) { $pageVerifyArgs += @("--expected-pii", $ExpectedPii) }
        Invoke-Step "第 $page/$PageCount 頁 Step 6 驗證" $PyDeid $pageVerifyArgs

        $PageFinals += $PageFinal
        $PagePlans += $PagePlan
        $PageReports += $PageReport
        $PageVerifications += $PageVerification
        $PageReOcrs += $PageReOcr
    }

    Invoke-Step "Step 7 合併各頁並跨頁檢查" $PyDeid (
        @("verify_deid.py", "--output", $FinalJson, "--source-file", (Split-Path -Leaf $Pdf),
          "--private-record", $PrivateRecord,
          "--merge-pages") + $PageFinals + @("--merge-plans") + $PagePlans +
        @("--merge-reports") + $PageReports + @("--merge-verifications") + $PageVerifications +
        @("--merge-reocr") + $PageReOcrs
    )

    Write-Host ""
    Write-Host "去識別化頁面圖片：$DeidPageDir"
    Write-Host "完成。最終 JSON 狀態為 PENDING_REVIEW，人工確認前不可上傳雲端。" -ForegroundColor Yellow
    exit 0
}

if ($RunInitialOcr) {
    Invoke-Step "Step 1 第一階段 OCR" $Py @("ocr_engine.py", "--image", $Image, "--output", $OcrJson)
}

Invoke-Step "Step 2-3 Presidio 個資偵測" $PyDeid @(
    "pii_block_detector.py", "--ocr-json", $OcrJson, "--plan", $Plan
)

Invoke-Step "Step 4-5 圖片遮蔽與重新 OCR" $Py @(
    "image_redactor.py", "--image", $Image, "--plan", $Plan,
    "--output", $DeidImage, "--reocr-json", $ReOcr, "--report", $Report
)

$verifyArgs = @(
    "verify_deid.py", "--plan", $Plan, "--report", $Report,
    "--reocr-json", $ReOcr, "--output", $FinalJson,
    "--verification-report", $Verification, "--private-record", $PrivateRecord
)

if ($ExpectedPii) {
    $verifyArgs += @("--expected-pii", (Resolve-ProjectPath $ExpectedPii))
}

Invoke-Step "Step 6-7 驗證與輸出 JSON" $PyDeid $verifyArgs

Write-Host ""
Write-Host "完成。最終 JSON 狀態為 PENDING_REVIEW，人工確認前不可上傳雲端。" -ForegroundColor Yellow
