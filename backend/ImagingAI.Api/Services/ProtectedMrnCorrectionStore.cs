using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImagingAI.Api.Models;
using Microsoft.AspNetCore.DataProtection;

namespace ImagingAI.Api.Services;

public interface IProtectedMrnCorrectionStore
{
    Task<(string Reference, string Sha256)> WriteAsync(ProtectedMrnCorrection correction, CancellationToken token);
    Task<ProtectedMrnCorrection> ReadAsync(MrnCorrectionMetadata metadata, CancellationToken token);
}

public sealed class ProtectedMrnCorrectionStore(IWebHostEnvironment environment,
    IDataProtectionProvider protection) : IProtectedMrnCorrectionStore
{
    private IDataProtector Protector(long report, long exception) => protection.CreateProtector(
        "ImagingAI.MrnCorrection.v1", report.ToString(CultureInfo.InvariantCulture),
        exception.ToString(CultureInfo.InvariantCulture));

    public async Task<(string Reference, string Sha256)> WriteAsync(ProtectedMrnCorrection correction,
        CancellationToken token)
    {
        var directory = Path.Combine(environment.ContentRootPath, "PrivateUploads", "mrn-corrections",
            correction.ReportId.ToString(CultureInfo.InvariantCulture),
            correction.ExceptionId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        PrivateJsonFieldsStore.RestrictAttemptDirectory(directory);
        var name = $"v{correction.Version}-{correction.Request.RequestId:N}-{Guid.NewGuid():N}.protected";
        var ciphertext = Protector(correction.ReportId, correction.ExceptionId).Protect(
            JsonSerializer.Serialize(correction, MrnCorrectionJson.Options));
        var bytes = Encoding.UTF8.GetBytes(ciphertext);
        // CreateNew, random suffix and no replacement: orphaned files never become authoritative.
        await using (var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew,
                         FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
            stream.Flush(flushToDisk: true);
        }
        return (name, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public async Task<ProtectedMrnCorrection> ReadAsync(MrnCorrectionMetadata metadata, CancellationToken token)
    {
        if (!Regex.IsMatch(metadata.ProtectedReference,
                @"\Av[1-9][0-9]*-[a-f0-9]{32}-[a-f0-9]{32}\.protected\z"))
            throw new InvalidDataException();
        var file = Path.Combine(environment.ContentRootPath, "PrivateUploads", "mrn-corrections",
            metadata.ReportId.ToString(CultureInfo.InvariantCulture),
            metadata.ExceptionId.ToString(CultureInfo.InvariantCulture), metadata.ProtectedReference);
        var bytes = await File.ReadAllBytesAsync(file, token);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), metadata.ProtectedSha256,
                StringComparison.Ordinal)) throw new InvalidDataException();
        var saved = JsonSerializer.Deserialize<ProtectedMrnCorrection>(
            Protector(metadata.ReportId, metadata.ExceptionId).Unprotect(Encoding.UTF8.GetString(bytes)),
            MrnCorrectionJson.Options) ?? throw new InvalidDataException();
        if (saved.ExceptionId != metadata.ExceptionId || saved.ReportId != metadata.ReportId ||
            saved.Version != metadata.Version || saved.ReviewerId != metadata.ReviewerId ||
            saved.SavedAt != metadata.SavedAt || saved.Request.RequestId != metadata.RequestId ||
            saved.Request.OcrAttemptId != metadata.OcrAttemptId ||
            saved.Request.OriginalSha256 != metadata.OriginalSha256 ||
            saved.Request.CandidateSha256 != metadata.CandidateSha256 || saved.Request.Reason != metadata.Reason)
            throw new InvalidDataException();
        return saved;
    }
}
