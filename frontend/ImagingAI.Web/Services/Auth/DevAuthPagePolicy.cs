using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace ImagingAI.Web.Services.Auth;

public static class DevAuthPagePolicy
{
    public enum PageKind { Unknown, Login, Identity, Reviewer, Admin, Closed }
    public enum Decision { Allow, NotFound, LoginRequired, Forbidden, Unavailable }

    public readonly record struct Target(string Path, PageKind Kind, string? Role, bool IsOpen);

    private const string Reviewer = nameof(Models.UserRole.MEDICAL_REVIEWER);
    private const string Admin = nameof(Models.UserRole.SYSTEM_ADMIN);
    private const string ReviewDetailPrefix = "/reviewer/review/";

    private static readonly Dictionary<string, Target> Routes = CreateRoutes();

    private static Dictionary<string, Target> CreateRoutes()
    {
        var routes = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        Add(PageKind.Login, null, true, "/", "/login");
        Add(PageKind.Reviewer, Reviewer, true, "/reviewer");
        Add(PageKind.Reviewer, Reviewer, false, "/reviewer/upload", "/reviewer/review",
            "/reviewer/exceptions", "/reviewer/structured-data", "/reviewer/history", "/reviewer/audit");
        Add(PageKind.Admin, Admin, true, "/admin");
        Add(PageKind.Admin, Admin, false, "/admin/users", "/admin/audit", "/admin/settings");
        Add(PageKind.Closed, null, false, "/home", "/counter", "/weather", "/api-test", "/upload", "/Error");
        return routes;

        void Add(PageKind kind, string? role, bool open, params string[] paths)
        {
            foreach (var path in paths)
                routes.Add(path, new(path, kind, role, open));
        }
    }

    public static Target FromHttpRequest(HttpRequest request)
    {
        if (request.PathBase.HasValue)
            return default;

        var raw = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var path = request.Path.Value;
        if (!string.IsNullOrEmpty(raw))
        {
            var query = raw.IndexOf('?');
            var originalPath = query < 0 ? raw : raw[..query];
            if (!string.Equals(originalPath, path, StringComparison.Ordinal))
                return default;
        }

        return Resolve(path, request.QueryString.Value);
    }

    public static Target FromNavigation(string absoluteUri, string baseUri)
    {
        if (!absoluteUri.StartsWith(baseUri, StringComparison.OrdinalIgnoreCase))
            return default;

        var relative = absoluteUri[baseUri.Length..];
        var fragment = relative.IndexOf('#');
        if (fragment >= 0)
            relative = relative[..fragment];
        var query = relative.IndexOf('?');
        return query < 0 ? Resolve("/" + relative, null) :
            Resolve("/" + relative[..query], relative[query..]);
    }

    public static Target Resolve(string? path, string? query)
    {
        if (path is null || path.Length == 0 || path[0] != '/' ||
            path.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '-' or '_')))
            return default;

        if (Routes.TryGetValue(path, out var target))
        {
            if (target.Path != "/login")
                return target;

            if (!QueryHelpers.ParseQuery(query).TryGetValue("view", out var view))
                return target;

            return view.Count == 1 && view[0] == "identity"
                ? new("/login", PageKind.Identity, null, true)
                : default;
        }

        if (path.StartsWith(ReviewDetailPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var caseId = path[ReviewDetailPrefix.Length..];
            if (caseId.Length is > 0 and <= 128 && !caseId.Contains('/'))
                return new(ReviewDetailPrefix + caseId, PageKind.Reviewer, Reviewer, false);
        }

        return default;
    }

    public static Decision Evaluate(Target target, ClaimsPrincipal? user, bool requiresReload = false,
        bool identityUnavailable = false)
    {
        if (!target.IsOpen)
            return Decision.NotFound;

        if (target.Kind is PageKind.Login or PageKind.Identity)
            return Decision.Allow;

        if (target.Kind is not (PageKind.Reviewer or PageKind.Admin) || target.Role is null)
            return Decision.NotFound;

        if (identityUnavailable)
            return Decision.Unavailable;

        if (requiresReload || user?.Identities.Any(identity => identity.IsAuthenticated) != true)
            return Decision.LoginRequired;

        var role = TrustedRole(user);
        return role == target.Role ? Decision.Allow : Decision.Forbidden;
    }

    public static string? TrustedRole(ClaimsPrincipal user)
    {
        if (user.Identities.Count() != 1 ||
            user.Identity is not ClaimsIdentity { IsAuthenticated: true, AuthenticationType: DevAuthIdentityHandler.SchemeName } identity ||
            identity.RoleClaimType != ClaimTypes.Role)
            return null;

        var identifiers = identity.FindAll(ClaimTypes.NameIdentifier).ToArray();
        if (identifiers.Length != 1 ||
            !long.TryParse(identifiers[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return null;

        var roles = identity.FindAll(ClaimTypes.Role).ToArray();
        return roles.Length == 1 && roles[0].Value is Reviewer or Admin ? roles[0].Value : null;
    }
}

