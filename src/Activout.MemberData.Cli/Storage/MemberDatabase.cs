using System.Data;
using System.Globalization;
using Activout.DatabaseClient.Dapper;
using Activout.DatabaseClient.Implementation;
using Microsoft.Data.Sqlite;

namespace Activout.MemberData.Cli.Storage;

/// <summary>The local SQLite database for one member: one connection, migrations and transaction scoping.</summary>
internal sealed class MemberDatabase : IDisposable
{
    public const string MemberKey = "member";
    public const string ApplicationKey = "developer_application";

    private readonly SqliteConnection _connection;

    public IMemberDao Dao { get; }
    public string Path { get; }

    static MemberDatabase()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private MemberDatabase(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
        Dao = new DatabaseClientBuilder().With(new DapperGateway(connection)).Build<IMemberDao>();
    }

    /// <summary>Opens (creating if needed) and migrates the database.</summary>
    public static async Task<MemberDatabase> Open(string path)
    {
        MemberPaths.EnsurePrivateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 30,
        }.ConnectionString);
        connection.Open();
        MemberPaths.RestrictFile(path);

        var database = new MemberDatabase(connection, path);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 30000;";
                await command.ExecuteNonQueryAsync();
            }

            await database.Migrate();
            return database;
        }
        catch (SqliteException e) when (e.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT, SQLITE_NOTADB
        {
            database.Dispose();
            throw new MemberDatabaseException($"{path} is not a valid member database: {e.Message}");
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private async Task Migrate()
    {
        var version = await Dao.GetSchemaVersion();
        if (version > Schema.Version)
            throw new MemberDatabaseException(
                $"Database schema version {version} is newer than this member version supports ({Schema.Version})");
        if (version < 1)
        {
            await InTransaction(async t =>
            {
                await Dao.ApplySchemaV1(t);
                await Dao.SetInfo("created_at", Timestamp(DateTimeOffset.UtcNow), t);
            });
        }
    }

    /// <summary>
    /// Binds an empty database to the member, or verifies that it already belongs to them. One member per database:
    /// records from different accounts are never mixed.
    /// </summary>
    public async Task BindMember(string member, string application)
    {
        var existing = await Dao.GetInfo(MemberKey);
        if (existing == null)
        {
            await InTransaction(async t =>
            {
                await Dao.SetInfo(MemberKey, member, t);
                await Dao.SetInfo(ApplicationKey, application, t);
            });
        }
        else if (existing != member)
        {
            throw new MemberDatabaseException(
                $"{Path} belongs to {existing}, but the token is for {member}. Use --database for a separate database.");
        }
    }

    /// <summary>Runs DAO calls in one SQLite transaction. Never perform network I/O inside.</summary>
    public async Task InTransaction(Func<IDbTransaction, Task> action)
    {
        using var transaction = Dao.BeginTransaction();
        await action(transaction);
        transaction.Commit();
    }

    public void Dispose() => _connection.Dispose();

    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
}
