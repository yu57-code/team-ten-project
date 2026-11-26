using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingAI.Web.Models;

/// <summary>系統角色：醫療／資料審核人員、系統管理／維護人員。</summary>
[JsonConverter(typeof(UserRoleJsonConverter))]
public enum UserRole
{
    MEDICAL_REVIEWER = 1,
    SYSTEM_ADMIN
}

internal sealed class UserRoleJsonConverter : JsonConverter<UserRole>
{
    public override UserRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a role code string.");

        return reader.GetString() switch
        {
            nameof(UserRole.MEDICAL_REVIEWER) => UserRole.MEDICAL_REVIEWER,
            nameof(UserRole.SYSTEM_ADMIN) => UserRole.SYSTEM_ADMIN,
            _ => throw new JsonException("Unknown role code.")
        };
    }

    public override void Write(Utf8JsonWriter writer, UserRole value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value))
            throw new JsonException("Unknown role code.");

        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>登入使用者（對應資料表 UserAccount）。</summary>
/// <param name="Username">帳號，例如 dr.chen</param>
/// <param name="DisplayName">顯示名稱，例如 陳醫師</param>
/// <param name="Role">角色</param>
/// <param name="AvatarText">頭像文字（姓氏）</param>
/// <param name="MfaEnabled">是否已完成 MFA 驗證</param>
public sealed record UserAccount(
    string Username,
    string DisplayName,
    UserRole Role,
    string AvatarText,
    bool MfaEnabled);

/// <summary>登入流程每一步的結果。</summary>
public sealed record AuthResult(bool Succeeded, string? Error = null)
{
    public static AuthResult Success() => new(true);

    public static AuthResult Fail(string error) => new(false, error);
}
