
using Microsoft.OpenApi.Models;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ImagingAI.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json.Serialization;
using ImagingAI.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();

if (builder.Environment.IsDevelopment() &&
    builder.Configuration.GetValue<bool>("Ocr:Enabled"))
{
    builder.Services.AddSingleton<IExtractedFieldsStore, PrivateJsonFieldsStore>();
    builder.Services.AddHostedService<PdfOcrWorker>();
}

builder.Services.AddDbContext<ImagingAiDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")
    )
);

// ==========================================
// 1. 註冊服務
// ==========================================

// Controller 註冊
// Enum 使用文字格式傳送
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();

// Swagger 設定（保留原本設定）
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ImagingAI.Api",
        Version = "v1",
        Description = "影像病例結構化平台 Web API"
    });
});

// ==========================================
// 異常佇列服務（保留 Mock）
// ==========================================

// Singleton：整個應用程式共用同一份模擬資料
// 不連接 PostgreSQL
builder.Services.AddSingleton<ExceptionService>();

// ==========================================
// 新增：待覆核 API 服務
// ==========================================

// Scoped：使用 EF Core DbContext 查詢 PostgreSQL
builder.Services.AddScoped<ReviewService>();

// ==========================================
// 2. 建立應用程式
// ==========================================

var app = builder.Build();

// ==========================================
// 3. Swagger 設定
// ==========================================

if (app.Environment.IsDevelopment())
{
    // 保留原本 Swagger JSON 版本相容性處理
    app.Use(async (context, next) =>
    {
        if (!context.Request.Path.Equals(
                "/swagger/v1/swagger.json",
                StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var originalBody = context.Response.Body;

        using var memoryStream = new MemoryStream();

        context.Response.Body = memoryStream;

        try
        {
            await next();

            memoryStream.Position = 0;

            if (context.Response.StatusCode == 200)
            {
                using var reader = new StreamReader(
                    memoryStream,
                    Encoding.UTF8,
                    leaveOpen: true);

                var json = await reader.ReadToEndAsync();

                // 僅修改文件最外層的 OpenAPI 版本欄位
                json = Regex.Replace(
                    json,
                    "^([\\s\\S]*?\"openapi\"\\s*:\\s*)\"3\\.0\\.4\"",
                    "$1\"3.0.1\"",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1));

                var bytes = Encoding.UTF8.GetBytes(json);

                context.Response.Body = originalBody;
                context.Response.ContentLength = bytes.Length;

                await context.Response.Body.WriteAsync(bytes);
            }
            else
            {
                context.Response.Body = originalBody;
                memoryStream.Position = 0;
                await memoryStream.CopyToAsync(originalBody);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    });

    app.UseSwagger();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint(
            "/swagger/v1/swagger.json",
            "ImagingAI API v1"
        );

        options.DocumentTitle =
            "ImagingAI API Documentation";
    });
}

// ==========================================
// 4. HTTP Middleware
// ==========================================

app.UseHttpsRedirection();

app.UseAuthorization();

// ==========================================
// 5. Controller 路由
// ==========================================

app.MapControllers();

// ==========================================
// 6. 啟動 API
// ==========================================

app.Run();
