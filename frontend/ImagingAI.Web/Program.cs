
using ImagingAI.Web.Components;
using ImagingAI.Web.Services;
using ImagingAI.Web.Services.Interfaces;
using ImagingAI.Web.Services.Mock;
using ImagingAI.Web.Services.Api;
using MudBlazor;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<LocalDevelopmentAccess>();

// ==========================================
// 1. Blazor 與 MudBlazor
// ==========================================

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass =
        Defaults.Classes.Position.BottomRight;

    config.SnackbarConfiguration.ShowCloseIcon = true;

    config.SnackbarConfiguration.VisibleStateDuration = 3000;
});

// ==========================================
// 2. 前端服務註冊
// ==========================================

// 登入狀態：每個 Blazor Circuit 各自一份
builder.Services.AddScoped<IAuthService, MockAuthService>();

// Mock 資料服務：保留其他頁面既有功能
builder.Services.AddSingleton<MockDataStore>();

builder.Services.AddSingleton<
    INavBadgeService, MockNavBadgeService>();

builder.Services.AddSingleton<
    ICaseService, MockCaseService>();

// ==========================================
// 3. 真正呼叫後端 API 的服務
// ==========================================

// 病例上傳 API（保留原本設定）
builder.Services.AddHttpClient<IUploadService, ApiUploadService>(
    client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["ApiSettings:BaseUrl"]
            ?? "http://localhost:5089/");
    });

// 新增：異常佇列 API
// 取代原本 MockExceptionService 註冊
builder.Services.AddHttpClient<
    IExceptionService, ApiExceptionService>(
    client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["ApiSettings:BaseUrl"]
            ?? "http://localhost:5142/");
    }).ConfigurePrimaryHttpMessageHandler(services =>
    {
        var access = services.GetRequiredService<LocalDevelopmentAccess>();
        return builder.Environment.IsDevelopment() ? access.CreateHandler() : new SocketsHttpHandler();
    }).RedactLoggedHeaders(_ => true);

// ==========================================
// 4. 其他 Mock 服務
// ==========================================

builder.Services.AddSingleton<
    IAuditService, MockAuditService>();

builder.Services.AddSingleton<
    ISystemMonitorService, MockSystemMonitorService>();

builder.Services.AddSingleton<
    IUserService, MockUserService>();

builder.Services.AddSingleton<
    ISettingsService, MockSettingsService>();

// FHIR Server 連線測試
builder.Services.AddHttpClient<
    IFhirConnectionTester, HttpFhirConnectionTester>();

// 瀏覽器下載
builder.Services.AddScoped<BrowserDownloadService>();

// ==========================================
// 5. YARP Reverse Proxy
// ==========================================

builder.Services.AddReverseProxy()
    .LoadFromConfig(
        builder.Configuration.GetSection("ReverseProxy"));

// ==========================================
// 6. YARP 設定檢查
// ==========================================

Console.WriteLine("===== YARP 設定檢查 =====");

Console.WriteLine(
    "目前環境：" + builder.Environment.EnvironmentName);

var proxyRoutes = builder.Configuration
    .GetSection("ReverseProxy:Routes")
    .GetChildren()
    .Select(x => x.Key)
    .ToArray();

Console.WriteLine(
    "YARP Routes：" + string.Join(", ", proxyRoutes));

Console.WriteLine(
    "API Route：" +
    builder.Configuration[
        "ReverseProxy:Routes:api-route:Match:Path"]);

Console.WriteLine(
    "Swagger Route：" +
    builder.Configuration[
        "ReverseProxy:Routes:swagger-route:Match:Path"]);

Console.WriteLine(
    "Backend Address：" +
    builder.Configuration[
        "ReverseProxy:Clusters:backend:Destinations:destination1:Address"]);

Console.WriteLine("=========================");

// ==========================================
// 7. 建立應用程式
// ==========================================

var app = builder.Build();
app.Services.GetRequiredService<LocalDevelopmentAccess>().Install(app);

// ==========================================
// 8. HTTP Middleware
// ==========================================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(
        "/Error",
        createScopeForErrors: true);

    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseAntiforgery();

// ==========================================
// 9. 路由設定
// ==========================================

// API 與 Swagger 透過 YARP 轉送
app.MapReverseProxy();

// Blazor 頁面
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ==========================================
// 10. 啟動前端
// ==========================================

app.Run();
