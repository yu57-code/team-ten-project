using System.Net;
using System.Text.Json;
using ImagingAI.Web.Models;

namespace ImagingAI.Web.Services.Auth;

public sealed class DevAuthIdentityClient(HttpClient http)
{
    public const string CookieName = "__Host-ImagingAI.DevAuth";
    private static readonly Uri MeEndpoint = new("https://localhost:7090/api/auth/me");

    internal async Task<IdentityCheck> VerifyAsync(string cookie, CancellationToken cancellationToken)
    {
        // ASP.NET Core cookie tickets are base64url. Reject delimiters and oversized values locally.
        if (cookie.Length is 0 or > 4096 ||
            cookie.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            return new(CheckStatus.Rejected);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, MeEndpoint);
        request.Headers.Add("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Cookie", CookieName + "=" + cookie);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return new(CheckStatus.Rejected);
                case HttpStatusCode.NotFound:
                    return new(CheckStatus.Disabled);
                case HttpStatusCode.TooManyRequests:
                    var retry = response.Headers.RetryAfter;
                    return new(CheckStatus.RateLimited, RetryAfter: retry?.Delta ??
                        (retry?.Date is { } date ? MaxZero(date - DateTimeOffset.UtcNow) : null));
                case HttpStatusCode.BadGateway:
                case HttpStatusCode.ServiceUnavailable:
                    return new(CheckStatus.Unavailable);
                case HttpStatusCode.OK:
                    break;
                default:
                    return new(CheckStatus.InvalidResponse);
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return new(CheckStatus.InvalidResponse);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var data = json.RootElement;
            if (data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("userId", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var userId) || userId <= 0 ||
                ReadString(data, "username") is not { Length: <= 100 } username ||
                string.IsNullOrWhiteSpace(username) ||
                ReadString(data, "activeRoleCode") is not
                    (nameof(UserRole.MEDICAL_REVIEWER) or nameof(UserRole.SYSTEM_ADMIN)))
            {
                return new(CheckStatus.InvalidResponse);
            }

            return new(CheckStatus.Verified,
                new VerifiedWebIdentity(userId, username, ReadString(data, "activeRoleCode")!));
        }
        catch (JsonException)
        {
            return new(CheckStatus.InvalidResponse);
        }
        catch (HttpRequestException)
        {
            return new(CheckStatus.Unavailable);
        }
        catch (IOException)
        {
            return new(CheckStatus.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(CheckStatus.Unavailable);
        }
    }

    private static string? ReadString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static TimeSpan MaxZero(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

    internal enum CheckStatus { Verified, Rejected, Disabled, RateLimited, Unavailable, InvalidResponse }
    internal sealed record IdentityCheck(CheckStatus Status, VerifiedWebIdentity? Identity = null, TimeSpan? RetryAfter = null);
    internal sealed record VerifiedWebIdentity(long UserId, string Username, string RoleCode);
}
