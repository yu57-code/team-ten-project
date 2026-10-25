var builder = WebApplication.CreateBuilder(args);

// ========================================
// 1. 註冊 Controller
// ========================================
builder.Services.AddControllers();

// ========================================
// 2. 註冊 Swagger
// ========================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ImagingAI API",
        Version = "v1",
        Description = "AI 影像病例結構化平台後端 API"
    });
});

// ========================================
// 3. 建立應用程式
// ========================================
var app = builder.Build();

// ========================================
// 4. Swagger 設定
// ========================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint(
            "/swagger/v1/swagger.json",
            "ImagingAI API v1"
        );
    });
}

// ========================================
// 5. Middleware
// ========================================
app.UseHttpsRedirection();

app.UseAuthorization();

// ========================================
// 6. 對應 Controller
// ========================================
app.MapControllers();

// ========================================
// 7. 啟動
// ========================================
app.Run();