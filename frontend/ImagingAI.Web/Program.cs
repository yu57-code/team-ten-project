using ImagingAI.Web.Components;
using ImagingAI.Web.Services;
using ImagingAI.Web.Services.Interfaces;
using ImagingAI.Web.Services.Mock;
using MudBlazor;
using MudBlazor.Services;
using ImagingAI.Web.Services.Api;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 3000;
});

// ---------------------------------------------------------------
// 前端服務（目前使用 Mock Data）
// 之後串接 ASP.NET Core Web API 時，只要新增 Services/Api 底下的實作，
// 並把下面的 Mock 類別換成 Api 類別即可，頁面程式碼不需修改。
// ---------------------------------------------------------------

// 登入狀態：每個瀏覽器分頁（circuit）各自一份
builder.Services.AddScoped<IAuthService, MockAuthService>();

// 模擬資料庫與資料服務：全站共用一份，在某頁的操作其他頁也看得到
builder.Services.AddSingleton<MockDataStore>();
builder.Services.AddSingleton<INavBadgeService, MockNavBadgeService>();
builder.Services.AddSingleton<ICaseService, MockCaseService>();

builder.Services.AddHttpClient<IUploadService, ApiUploadService>(
    client =>
    {
        client.BaseAddress = new Uri(
            builder.Configuration["ApiSettings:BaseUrl"]
            ?? "http://localhost:5089/");
    });

builder.Services.AddSingleton<IExceptionService, MockExceptionService>();
builder.Services.AddSingleton<IAuditService, MockAuditService>();
builder.Services.AddSingleton<ISystemMonitorService, MockSystemMonitorService>();
builder.Services.AddSingleton<IUserService, MockUserService>();
builder.Services.AddSingleton<ISettingsService, MockSettingsService>();

// FHIR Server 連線測試：真正發出 HTTP 請求（GET {BaseUrl}/metadata），不是模擬
builder.Services.AddHttpClient<IFhirConnectionTester, HttpFhirConnectionTester>();

// 瀏覽器下載（JS Interop）
builder.Services.AddScoped<BrowserDownloadService>();
// YARP Reverse Proxy
builder.Services.AddReverseProxy()
    .LoadFromConfig(
        builder.Configuration.GetSection("ReverseProxy"));


 // ==========================================
 // 檢查 YARP 設定是否正確載入
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
     builder.Configuration["ReverseProxy:Routes:api-route:Match:Path"]);

 Console.WriteLine(
     "Swagger Route：" +
     builder.Configuration["ReverseProxy:Routes:swagger-route:Match:Path"]);

 Console.WriteLine(
     "Backend Address：" +
     builder.Configuration["ReverseProxy:Clusters:backend:Destinations:destination1:Address"]);

 Console.WriteLine("=========================");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

// 將 API 與 Swagger 請求轉送至後端
app.MapReverseProxy();
app.MapRazorComponents<App>()
     .AddInteractiveServerRenderMode();

app.Run();
