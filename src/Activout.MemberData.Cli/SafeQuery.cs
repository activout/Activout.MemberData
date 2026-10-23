using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Activout.MemberData.Cli;

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows,
    bool Truncated);

/// <summary>
/// Runs one user-supplied SELECT against the database with several independent guards: a read-only connection,
/// <c>PRAGMA query_only</c>, an SQLite authoriser that allows only reads and ordinary functions, a single-statement
/// check on the prepared statement, <c>sqlite3_stmt_readonly</c>, a row limit and a timeout.
/// </summary>
internal static class SafeQuery
{
    private const int SqliteOk = 0;
    private const int SqliteDeny = 1;
    private const int SqliteRead = 20;
    private const int SqliteSelect = 21;
    private const int SqliteFunction = 31;
    private const int SqliteRecursive = 33;

    private static readonly HashSet<string> DeniedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension", "fts3_tokenizer", "readfile", "writefile", "edit", "sqlite_dbpage", "zipfile",
    };

    public static async Task<QueryResult> Run(string databasePath, string sql, int maxRows, TimeSpan timeout,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            throw new MemberUsageException($"No database at {databasePath}; run 'member sync' first");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = ON;";
            pragma.ExecuteNonQuery();
        }

        var db = connection.Handle!;
        raw.sqlite3_set_authorizer(db, Authorise, null);
        try
        {
            Validate(db, sql);

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await using var interrupt = timeoutCts.Token.Register(() => raw.sqlite3_interrupt(db));
            try
            {
                await using var reader = await command.ExecuteReaderAsync(timeoutCts.Token);
                var columns = Enumerable.Range(0, reader.FieldCount).Select(UniqueName(reader)).ToList();
                var rows = new List<Dictionary<string, object?>>();
                var truncated = false;
                while (await reader.ReadAsync(timeoutCts.Token))
                {
                    if (rows.Count == maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new Dictionary<string, object?>(columns.Count);
                    for (var i = 0; i < columns.Count; i++)
                        row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }

                return new QueryResult(columns, rows, truncated);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 9) // SQLITE_INTERRUPT
            {
                ct.ThrowIfCancellationRequested();
                throw new MemberUsageException($"Query exceeded the {timeout.TotalSeconds:F0} second timeout");
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 23) // SQLITE_AUTH
            {
                throw new UnsafeQueryException("Query rejected: only reading tables and ordinary functions is allowed");
            }
        }
        finally
        {
            raw.sqlite3_set_authorizer(db, (delegate_authorizer?)null, null);
        }
    }

    /// <summary>Prepares the text once: exactly one statement, and SQLite itself must call it read-only.</summary>
    private static void Validate(sqlite3 db, string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new MemberUsageException("Empty query");

        var rc = raw.sqlite3_prepare_v2(db, sql, out var statement, out var tail);
        using (statement)
        {
            if (rc != SqliteOk)
            {
                var message = raw.sqlite3_errmsg(db).utf8_to_string();
                if (rc == 23 || message.Contains("not authorized", StringComparison.OrdinalIgnoreCase))
                    throw new UnsafeQueryException("Query rejected: " + message);
                throw new MemberUsageException("Invalid SQL: " + message);
            }

            if (statement == null || statement.IsInvalid) throw new MemberUsageException("Empty query");
            if (raw.sqlite3_stmt_readonly(statement) == 0)
                throw new UnsafeQueryException("Query rejected: only read-only statements are allowed");
        }

        if (!string.IsNullOrWhiteSpace(tail))
        {
            // Trailing comments prepare to nothing; any further statement is rejected unexecuted.
            rc = raw.sqlite3_prepare_v2(db, tail, out var next, out _);
            using (next)
            {
                if (rc != SqliteOk || (next != null && !next.IsInvalid))
                    throw new UnsafeQueryException("Query rejected: exactly one statement is allowed");
            }
        }
    }

    private static int Authorise(object userData, int action, utf8z param0, utf8z param1, utf8z database,
        utf8z trigger) => action switch
    {
        SqliteSelect or SqliteRead or SqliteRecursive => SqliteOk,
        SqliteFunction => DeniedFunctions.Contains(param1.utf8_to_string() ?? "") ? SqliteDeny : SqliteOk,
        _ => SqliteDeny,
    };

    private static Func<int, string> UniqueName(SqliteDataReader reader)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        return i =>
        {
            var name = reader.GetName(i);
            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{name}_{n}";
            return candidate;
        };
    }
}
