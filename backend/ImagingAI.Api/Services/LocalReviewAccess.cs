using System.Net;

namespace ImagingAI.Api.Services;

public static class LocalReviewAccess
{
    public static bool Loopback(IPAddress? ip) => ip is not null && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    public static bool Allowed(HttpContext context) => Loopback(context.Connection.RemoteIpAddress) &&
        Loopback(context.Connection.LocalIpAddress) && !context.Request.Headers.Keys.Any(k =>
            k.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) || k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase));
    public static void Install(WebApplication app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        var sensitive = path.StartsWith("/api/exceptions/", StringComparison.OrdinalIgnoreCase) &&
            new[] { "/evidence", "/resolve", "/revalidate" }.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (sensitive && (!app.Environment.IsDevelopment() || !Allowed(context)))
        { context.Response.StatusCode = 404; return; }
        await next();
    });
}
