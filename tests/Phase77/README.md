# Phase 7.7 安全回歸

從版本庫根目錄執行。環境需要 .NET 8、Node.js 22.19 以上、Chromium headless shell，以及已受本機信任的 ASP.NET Core 開發 HTTPS 憑證。保留完整自然到期時間。測試 API 使用合成帳號與程序內寄信替身，不接觸 PostgreSQL 或 Gmail。

```powershell
dotnet build backend/ImagingAI.Api/ImagingAI.Api.csproj
dotnet build frontend/ImagingAI.Web/ImagingAI.Web.csproj
dotnet run --project backend/ImagingAI.Api.Tests/ImagingAI.Api.Tests.csproj
node frontend/ImagingAI.Web/Tests/devAuth.test.mjs
node --test frontend/ImagingAI.Web/Tests/devAuthOtp.test.mjs
dotnet run --project tests/Phase77/Interop/Interop.csproj
dotnet run --project tests/Phase77/Model/Model.csproj
```

啟動瀏覽器前，確認 7018、7090、17446–17449、17451、17452 可用。將 `PHASE77_CHROMIUM` 指向本機 Chromium headless shell。無須設定 SMTP 或資料庫憑證。

```powershell
$env:PHASE77_CHROMIUM = Read-Host 'Chromium headless shell 執行檔完整路徑'
dotnet run --project tests/Phase77/Browser/Browser.csproj -- smoke
dotnet run --project tests/Phase77/Browser/Browser.csproj -- race
dotnet run --project tests/Phase77/Browser/Browser.csproj -- outage
dotnet run --project tests/Phase77/Browser/Browser.csproj -- completion
node --use-system-ca tests/Phase77/Browser/mock.mjs 'frontend/ImagingAI.Web'
```

`completion` 補驗角色待選重整、OTP 成功後返回舊頁、健康重連、斷線時停用帳號或變更角色，以及雙分頁同時選角。帳號異動只作用於隔離 Host 的合成資料。

自然到期模式使用分離的 Chromium 設定檔並行等待 120 秒選角期限、300 秒 OTP 期限及完整 30 分鐘固定 Session。過期後再等待 Circuit 原定重驗。請保留終端機與電腦運作約 32 分鐘。

```powershell
dotnet run --project tests/Phase77/Browser/Browser.csproj -- timed
```

正常完成或測試失敗時，測試工具停止本輪 Web、API 及 Chromium，清理暫存設定檔。若手動強制關閉，確認僅清理本輪建立的程序。Mock 測試的業務上游指向無服務的 loopback 位址。
