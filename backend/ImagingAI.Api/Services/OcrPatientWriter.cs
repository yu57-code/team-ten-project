using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace ImagingAI.Api.Services;

internal sealed record OcrPatientValues(string Mrn, string? Name, string? Gender, string? NationalId,
    DateOnly? BirthDate);

internal static class OcrPatientWriter
{
    private const double MinimumConfidence = 0.8;

    internal static OcrPatientValues? ReadSyntheticPatient(JsonElement extraction, string uploadedMrn)
    {
        if (extraction.GetProperty("mrn_validation").GetString() != "MATCH")
            return null;

        var fields = extraction.GetProperty("fields");
        var mrn = ReadSingle(fields, "mrn", minimumConfidence: 0);
        if (mrn is null || !mrn.StartsWith("SYNTH-", StringComparison.Ordinal) ||
            mrn.Length <= "SYNTH-".Length ||
            !MrnFormat.IsValid(mrn) ||
            !string.Equals(mrn, uploadedMrn.Trim(), StringComparison.Ordinal))
            return null;

        var name = ReadSingle(fields, "patient_name");
        if (name is { Length: > 100 } || name?.Any(char.IsControl) == true)
            name = null;

        var gender = ReadSingle(fields, "gender")?.ToUpperInvariant() switch
        {
            "MALE" or "M" or "男" or "男性" => "male",
            "FEMALE" or "F" or "女" or "女性" => "female",
            _ => null
        };
        var nationalId = ReadSingle(fields, "national_id")?.ToUpperInvariant();
        if (nationalId is not null && !IsTaiwanNationalId(nationalId))
            nationalId = null;

        DateOnly? birth = DateOnly.TryParseExact(ReadSingle(fields, "birth_date"),
            ["yyyy-MM-dd", "yyyy/M/d", "yyyy.M.d", "yyyy年M月d日"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
            date <= DateOnly.FromDateTime(DateTime.Today) ? date : null;

        return new(mrn, name, gender, nationalId, birth);
    }

    private static string? ReadSingle(JsonElement fields, string key, double minimumConfidence = MinimumConfidence)
    {
        if (!fields.TryGetProperty(key, out var field) ||
            !field.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
            return null;

        string? selected = null;
        var confident = false;
        foreach (var candidate in candidates.EnumerateArray())
        {
            var text = candidate.GetProperty("value").GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;
            if (candidate.GetProperty("source").GetString() != "PADDLEOCR" ||
                candidate.GetProperty("review_status").GetString() != "UNVERIFIED" ||
                (selected is not null && !string.Equals(selected, text, StringComparison.Ordinal)))
                return null;
            selected = text;
            var confidence = candidate.GetProperty("confidence").GetDouble();
            confident |= confidence >= minimumConfidence && confidence <= 1;
        }
        return confident ? selected : null;
    }

    private static bool IsTaiwanNationalId(string value)
    {
        if (!Regex.IsMatch(value, @"\A[A-Z][12][0-9]{8}\z"))
            return false;
        const string lettersByCode = "ABCDEFGHJKLMNPQRSTUVXYWZIO";
        var code = lettersByCode.IndexOf(value[0]) + 10;
        var sum = code / 10 + (code % 10 * 9);
        for (var index = 1; index <= 8; index++)
            sum += (value[index] - '0') * (9 - index);
        return (sum + value[9] - '0') % 10 == 0;
    }

    internal static async Task WriteAsync(NpgsqlConnection connection, long reportId, OcrPatientValues patient,
        CancellationToken cancellationToken)
    {
        await using var target = new NpgsqlCommand("""
            SELECT current_database() = 'imaging_ai_db' AND current_user = 'imaging_ai_user'
            """, connection);
        if (await target.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("Unexpected Patient test database or role");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var report = new NpgsqlCommand("""
            SELECT patient_id FROM clinical.clinical_report
            WHERE report_id = @id AND processing_status = 'DEID_PROCESSING' AND NOT is_deleted
            FOR UPDATE
            """, connection, transaction);
        report.Parameters.AddWithValue("id", reportId);
        var linked = await report.ExecuteScalarAsync(cancellationToken);
        if (linked is null)
            throw new InvalidOperationException("Report is no longer eligible");

        await using var lookup = new NpgsqlCommand("""
            SELECT patient_id FROM clinical.patient WHERE mrn = @mrn
            """, connection, transaction);
        lookup.Parameters.AddWithValue("mrn", patient.Mrn);
        var patientId = await lookup.ExecuteScalarAsync(cancellationToken) as long?;
        if (linked is long prior)
        {
            if (patientId != prior)
                throw new OcrPatientLinkConflictException();
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (patientId is null)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO clinical.patient (mrn, patient_name, gender, national_id, birth_date)
                VALUES (@mrn, @name, @gender, @national, @birth)
                ON CONFLICT (mrn) DO NOTHING
                RETURNING patient_id
                """, connection, transaction);
            insert.Parameters.AddWithValue("mrn", patient.Mrn);
            insert.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, (object?)patient.Name ?? DBNull.Value);
            insert.Parameters.AddWithValue("gender", NpgsqlDbType.Varchar, (object?)patient.Gender ?? DBNull.Value);
            insert.Parameters.AddWithValue("national", NpgsqlDbType.Varchar, (object?)patient.NationalId ?? DBNull.Value);
            insert.Parameters.AddWithValue("birth", NpgsqlDbType.Date, (object?)patient.BirthDate ?? DBNull.Value);
            patientId = await insert.ExecuteScalarAsync(cancellationToken) as long?
                ?? await lookup.ExecuteScalarAsync(cancellationToken) as long?
                ?? throw new InvalidOperationException("Patient ID not found");
        }

        await using var link = new NpgsqlCommand("""
            UPDATE clinical.clinical_report SET patient_id = @patient
            WHERE report_id = @id AND patient_id IS NULL
              AND processing_status = 'DEID_PROCESSING' AND NOT is_deleted
            """, connection, transaction);
        link.Parameters.AddWithValue("patient", patientId.Value);
        link.Parameters.AddWithValue("id", reportId);
        if (await link.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new OcrPatientLinkConflictException();
        await transaction.CommitAsync(cancellationToken);
    }
}

internal sealed class OcrPatientLinkConflictException : Exception;
