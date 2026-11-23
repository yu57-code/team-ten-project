using System.Net;
using System.Net.Sockets;

namespace ImagingAI.Web.Services;

// This is transport admission, not user authentication. Mock roles have no authority here.
public sealed class LocalDevelopmentAccess(IWebHostEnvironment environment, IConfiguration configuration)
{
    private bool installed;
    private Uri? ApiTarget => Uri.TryCreate(configuration["ApiSettings:BaseUrl"] ?? "http://localhost:5142/",
        UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && LocalHost(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        ? uri : null;
    public bool CanForward => installed && environment.IsDevelopment() && ApiTarget is not null;
    public static bool Loopback(IPAddress? ip) => ip is not null && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    public static bool LocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var ip) && Loopback(ip);
    public static bool Allowed(HttpContext context)
    {
        if (!Loopback(context.Connection.RemoteIpAddress) || !Loopback(context.Connection.LocalIpAddress) ||
            !LocalHost(context.Request.Host.Host) || context.Request.Host.Port != context.Connection.LocalPort ||
            context.Request.Headers.Keys.Any(k => k.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || k.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase))) return false;
        var origin = context.Request.Headers.Origin.ToString();
        var hubHandshake = context.WebSockets.IsWebSocketRequest || context.Request.Path.Equals("/_blazor/negotiate");
        if (hubHandshake && string.IsNullOrEmpty(origin)) return false;
        return (string.IsNullOrEmpty(origin) || Uri.TryCreate(origin, UriKind.Absolute, out var uri) && LocalHost(uri.Host) &&
            uri.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Scheme == context.Request.Scheme && uri.Port == context.Connection.LocalPort) &&
            context.Request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
    public void Install(WebApplication app)
    {
        installed = true;
        // Before any proxy, components or SignalR. Reconnection must pass the same admission.
        app.Use(async (context, next) =>
        {
            if (environment.IsDevelopment() && !Allowed(context)) { context.Response.StatusCode = 403; return; }
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            await next();
        });
    }
    public SocketsHttpHandler CreateHandler()
    {
        var target = ApiTarget;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var requestUri = context.InitialRequestMessage.RequestUri;
                if (target is null || requestUri is null ||
                    !requestUri.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase) ||
                    !requestUri.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) ||
                    requestUri.Port != target.Port || !LocalHost(context.DnsEndPoint.Host))
                    throw new HttpRequestException("Unapproved Development API destination refused");
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                foreach (var ip in addresses.Where(Loopback))
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); }
                }
                throw new HttpRequestException("Loopback connection unavailable");
            }
        };
    }
}
