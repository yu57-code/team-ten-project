using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ImagingAI.Api.Services;

public interface IExtractedFieldsStore
{
    Task WriteAsync(string attemptDirectory, JsonObject result, CancellationToken cancellationToken);
}

public sealed class PrivateJsonFieldsStore : IExtractedFieldsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void RestrictAttemptDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException();
            var info = new DirectoryInfo(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public async Task WriteAsync(string attemptDirectory, JsonObject result, CancellationToken cancellationToken)
    {
        var target = Path.Combine(attemptDirectory, "extracted_fields.json");
        var temporary = Path.Combine(attemptDirectory, $".fields-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, result.ToJsonString(Options), cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
