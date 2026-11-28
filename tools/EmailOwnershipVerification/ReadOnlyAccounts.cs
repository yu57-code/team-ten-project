using Npgsql;

namespace EmailOwnershipVerification;

internal sealed class ReadOnlyAccounts(string password)
{
    internal string? MaintenanceVersion { get; set; }
    internal const string Target = "127.0.0.1:5432/imaging_ai_db";
    private readonly string connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = 5432, Database = "imaging_ai_db", Username = "imaging_ai_user",
        Password = password, Pooling = false, Options = "-c default_transaction_read_only=on",
        Timeout = 5, CommandTimeout = 5, IncludeErrorDetail = false
    }.ConnectionString;

    internal async Task<Account?> ReadAsync(string username, CancellationToken token)
    {
        if (!Account.Allowed(username)) return null;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await mode.ExecuteNonQueryAsync(token);
        await using (var guard = new NpgsqlCommand("""
            SELECT current_database() = 'imaging_ai_db' AND current_user = 'imaging_ai_user'
               AND current_setting('transaction_read_only') = 'on'
               AND inet_server_addr() = '127.0.0.1'::inet AND inet_server_port() = 5432
            """, connection, transaction))
        {
            if (await guard.ExecuteScalarAsync(token) is not true) throw new InvalidOperationException();
        }
        await using var versionCheck = new NpgsqlCommand(
            "SELECT has_column_privilege(current_user, 'system.user_account', 'xmin', 'SELECT')", connection, transaction);
        var canReadVersion = await versionCheck.ExecuteScalarAsync(token) is true;
        var versionColumn = canReadVersion ? "a.xmin::text" : "NULL::text";
        await using var query = new NpgsqlCommand($"""
            SELECT a.user_id, a.username, a.role_id, a.secondary_role_id,
                   p.role_code, s.role_code, a.is_active, a.email, a.email_verified_at, {versionColumn}
            FROM system.user_account a
            JOIN system.role p ON p.role_id = a.role_id
            LEFT JOIN system.role s ON s.role_id = a.secondary_role_id
            WHERE a.username = @username
            """, connection, transaction);
        query.Parameters.AddWithValue("username", username);
        await using var row = await query.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token)) return null;
        var account = new Account(row.GetInt64(0), row.GetString(1), row.GetInt64(2),
            row.IsDBNull(3) ? null : row.GetInt64(3), row.GetString(4), row.IsDBNull(5) ? null : row.GetString(5),
            row.GetBoolean(6), row.IsDBNull(7) ? null : row.GetString(7),
            row.IsDBNull(8) ? null : row.GetDateTime(8),
            canReadVersion ? row.GetString(9) : MaintenanceVersion ?? "") { VersionReadByTool = canReadVersion };
        if (await row.ReadAsync(token)) throw new InvalidOperationException();
        return account;
    }
}
