using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using ImagingAI.Api.Auth;
using ImagingAI.Api.Controllers;
using ImagingAI.Api.Services;

var root = Directory.GetCurrentDirectory();
var mode = args.SingleOrDefault() ?? "smoke";
if (mode is not ("quick" or "smoke" or "race" or "timed" or "outage" or "completion" or "api-child")) return 2;
if (!File.Exists(Path.Combine(root, "frontend", "ImagingAI.Web", "ImagingAI.Web.csproj")) ||
    !File.Exists(Environment.GetEnvironmentVariable("PHASE77_CHROMIUM"))) return 2;
if (mode != "api-child" && System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port is 7018 or 7090 or 17446 or 17447 or 17448 or 17449)) return 2;
var sender = new Sender();
string? controlPath = null;
bool IsActive(long id) => id != 413570438 || controlPath is null || !File.Exists(controlPath + ".disabled");
string CurrentRole(long id) => id == 413570438 && controlPath is not null &&
    File.Exists(controlPath + ".changed-role") ? "MEDICAL_REVIEWER" : Primary(id);
if (mode == "api-child")
{
    if (!Console.IsOutputRedirected) return 2;
    sender.Deliver = Console.WriteLine;
    await using var owned = BuildApi();
    await owned.RunAsync();
    return 0;
}

WebApplication BuildApi()
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
    builder.Logging.ClearProviders();
    var testConfig = new Microsoft.Extensions.Configuration.ConfigurationManager();
    testConfig["Auth:EnableDevelopmentLogin"] = "true";
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback,7090,l=>l.UseHttps()));
    var mvc = builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
    mvc.ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new AuthOnly()));
    builder.Services.AddSingleton(new AuthProtection(new EphemeralDataProtectionProvider()));
    AuthRegistration.AddDevelopmentAuth(mvc,builder.Environment,testConfig);
    builder.Services.RemoveAll<AuthService>();
    builder.Services.RemoveAll<EmailOtpService>();
    builder.Services.AddSingleton<IEmailOtpSender>(sender);
    builder.Services.AddScoped(sp => new AuthService((name,_) => Task.FromResult<AuthService.AccountCredentials?>(name is "413570452" or "413570438" or "413570359" ? Credentials(name) : null),
    builder.Environment, sp.GetRequiredService<ILogger<AuthService>>(),
    (id,_) => Task.FromResult<AuthService.AccountRoles?>(new(id,id.ToString(),IsActive(id),CurrentRole(id),id==413570452,id==413570452?"SYSTEM_ADMIN":null)),
    sp.GetRequiredService<AuthSessionStore>()));
    builder.Services.AddScoped(sp => new EmailOtpService((id,_) => Task.FromResult<EmailOtpAccount?>(new(id,id.ToString(),IsActive(id),
    "synthetic@example.invalid",DateTime.UnixEpoch,CurrentRole(id),id==413570452?2:null,id==413570452?"SYSTEM_ADMIN":null)),
    sp.GetRequiredService<EmailOtpChallengeStore>(),sender,TimeProvider.System,sp.GetRequiredService<EmailOtpOptions>()));
    var api=builder.Build();
    api.UseDevelopmentAuthBoundary(true);
    api.MapControllers();
    return api;
}
await using var app = mode == "outage" ? null : BuildApi();
Process? apiChild = null;
if (app is not null) await app.StartAsync();
var webInfo=new ProcessStartInfo("dotnet") { WorkingDirectory=Path.Combine(root,"frontend","ImagingAI.Web"), UseShellExecute=false,
 RedirectStandardOutput=true, RedirectStandardError=true };
foreach(var arg in new[]{"run","--no-build","--no-launch-profile","--","--urls","https://localhost:7018"}) webInfo.ArgumentList.Add(arg);
webInfo.Environment["ASPNETCORE_ENVIRONMENT"]="Development";
webInfo.Environment["Auth__EnableDevelopmentLogin"]="true";
webInfo.Environment["ReverseProxy__Clusters__backend__Destinations__destination1__Address"]="https://localhost:7090/";
var testAppData = Directory.CreateTempSubdirectory("phase77-web-");
if (mode == "completion") controlPath = Path.Combine(testAppData.FullName, "control");
webInfo.Environment["APPDATA"] = testAppData.FullName;
webInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
foreach (var key in webInfo.Environment.Keys.Where(k => k.StartsWith("EmailOtp__", StringComparison.OrdinalIgnoreCase)).ToArray())
    webInfo.Environment.Remove(key);
using var web=Process.Start(webInfo)!;
web.BeginOutputReadLine();web.BeginErrorReadLine();
try
{
    using var http=new HttpClient(){Timeout=TimeSpan.FromSeconds(3)};
    if (mode == "outage")
    {
        apiChild = StartApiChild();
        await VerifyApi(http, apiChild);
    }
    var ready=false;
    for(var i=0;i<40&&!web.HasExited;i++)
    {
        try { using var result=await http.GetAsync("https://localhost:7018/login"); if(result.IsSuccessStatusCode){ready=true;break;} }
        catch(HttpRequestException){} catch(TaskCanceledException){}
        await Task.Delay(250);
    }
    if(!ready)throw new InvalidOperationException("Web readiness failed");
    var browser=new ProcessStartInfo("node"){UseShellExecute=false,RedirectStandardInput=true};
    browser.ArgumentList.Add("--use-system-ca");
    browser.Environment["PHASE77_CONTROL"] = Path.Combine(testAppData.FullName, "control");
    browser.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"acceptance.mjs"));
    browser.ArgumentList.Add(mode);
    using var process=Process.Start(browser)!;
    sender.Deliver = code=>{process.StandardInput.WriteLine(code);process.StandardInput.Flush();};
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(mode == "timed" ? 36 : mode == "completion" ? 6 : 3));
    try
    {
        if (mode != "outage") await process.WaitForExitAsync(deadline.Token);
        else
        {
            var control = Path.Combine(testAppData.FullName, "control");
            while (!process.HasExited)
            {
                if (File.Exists(control + ".stop") && !File.Exists(control + ".stopped"))
                {
                    await StopApiChild(apiChild!);
                    apiChild = null;
                    if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                        .GetActiveTcpListeners().Any(x => x.Port == 7090))
                        throw new InvalidOperationException("Owned API exited but 7090 is still listening");
                    await File.WriteAllTextAsync(control + ".stopped", "exit-and-port-confirmed", deadline.Token);
                }
                if (File.Exists(control + ".restart") && !File.Exists(control + ".restarted"))
                {
                    apiChild = StartApiChild();
                    await VerifyApi(http, apiChild);
                    await File.WriteAllTextAsync(control + ".restarted", "ready", deadline.Token);
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        return process.ExitCode;
    }
    finally { if(!process.HasExited)process.Kill(entireProcessTree:true); }
}
finally
{
    if(!web.HasExited){web.Kill(entireProcessTree:true);await web.WaitForExitAsync();}
    if (app is not null) await app.StopAsync();
    if (apiChild is not null) await StopApiChild(apiChild);
    testAppData.Delete(recursive: true);
}
Process StartApiChild()
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(typeof(Sender).Assembly.Location);
    start.ArgumentList.Add("api-child");
    var child = Process.Start(start)!;
    child.OutputDataReceived += (_, e) =>
    {
        if (e.Data is { Length: 6 } code && code.All(char.IsAsciiDigit))
            sender.Deliver?.Invoke(code);
    };
    child.BeginOutputReadLine();
    child.BeginErrorReadLine();
    return child;
}

static async Task StopApiChild(Process child)
{
    using (child)
    {
        if (!child.HasExited) child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
    }
}

static async Task VerifyApi(HttpClient http, Process child)
{
    for (var i = 0; i < 40 && !child.HasExited; i++)
    {
        try
        {
            using var reply = await http.GetAsync("https://localhost:7090/api/auth/csrf");
            if (reply.IsSuccessStatusCode) return;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        await Task.Delay(250);
    }
    throw new InvalidOperationException("Isolated API child did not accept HTTPS");
}
static string Primary(long id)=>id==413570438?"SYSTEM_ADMIN":"MEDICAL_REVIEWER";
static AuthService.AccountCredentials Credentials(string name)
{
    var id=long.Parse(name);
    return new(id,name,new PasswordHasher<object>().HashPassword(new object(),"phase75-test-password"),
        true,Primary(id),id==413570452,id==413570452?"SYSTEM_ADMIN":null);
}
sealed class Sender : IEmailOtpSender
{
    public Action<string>? Deliver {get;set;}
    public Task SendAsync(string email,string code,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (Deliver ?? throw new InvalidOperationException())(code);
        return Task.CompletedTask;
    }
}
sealed class AuthOnly : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts,ControllerFeature feature)
    {
        foreach(var c in feature.Controllers.Where(c=>c.AsType()!=typeof(AuthController)).ToArray()) feature.Controllers.Remove(c);
    }
}






