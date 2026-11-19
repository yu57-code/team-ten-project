using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ImagingAI.Api.Services;

internal sealed record OcrCandidateBatch(Guid AttemptId, string SchemaVersion, string ReviewStatus,
    string MrnValidation, string FieldsJson, string PayloadHash)
{
    internal static OcrCandidateBatch Read(JsonElement envelope, long caseId, string attemptId)
    {
        var schema = envelope.GetProperty("schema_version").GetString();
        var review = envelope.GetProperty("review_status").GetString();
        var validation = envelope.GetProperty("mrn_validation").GetString();
        var fields = envelope.GetProperty("fields");
        if (envelope.GetProperty("case_id").GetString() != caseId.ToString(CultureInfo.InvariantCulture) ||
            envelope.GetProperty("ocr_attempt_id").GetString() != attemptId ||
            !Guid.TryParseExact(attemptId, "N", out var attempt) ||
            schema != "ocr_extracted_fields/v1" || review != "UNVERIFIED" ||
            validation is not ("MATCH" or "MRN_MISMATCH" or "MRN_MISSING" or
                "MRN_AMBIGUOUS" or "MRN_INVALID_FORMAT") || fields.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid OCR candidate batch");

        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical))
            WriteCanonical(writer, fields);
        var fieldsJson = Encoding.UTF8.GetString(canonical.ToArray());
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{schema}\n{review}\n{validation}\n{fieldsJson}"));
        return new(attempt, schema, review, validation, fieldsJson, Convert.ToHexString(digest));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    internal async Task StoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long caseId, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO system.ocr_candidate_batch
                (report_id, ocr_attempt_id, schema_version, review_status, mrn_validation,
                 candidates_json, payload_sha256)
            VALUES (@id, @attempt, @schema, @review, @validation, @fields, @hash)
            ON CONFLICT (report_id, ocr_attempt_id) DO NOTHING
            """, connection, transaction);
        AddKey(insert, caseId);
        insert.Parameters.AddWithValue("schema", SchemaVersion);
        insert.Parameters.AddWithValue("review", ReviewStatus);
        insert.Parameters.AddWithValue("validation", MrnValidation);
        insert.Parameters.AddWithValue("fields", NpgsqlDbType.Jsonb, FieldsJson);
        insert.Parameters.AddWithValue("hash", PayloadHash);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            return;

        await using var check = new NpgsqlCommand("""
            SELECT payload_sha256 FROM system.ocr_candidate_batch
            WHERE report_id = @id AND ocr_attempt_id = @attempt
            """, connection, transaction);
        AddKey(check, caseId);
        var existing = await check.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.Equals(existing, PayloadHash, StringComparison.Ordinal))
            throw new CandidateBatchConflictException();
    }

    private void AddKey(NpgsqlCommand command, long caseId)
    {
        command.Parameters.AddWithValue("id", caseId);
        command.Parameters.AddWithValue("attempt", AttemptId);
    }
}

internal sealed class CandidateBatchConflictException : Exception;
