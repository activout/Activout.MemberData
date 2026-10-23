using System.Data;
using Activout.DatabaseClient;
using Activout.DatabaseClient.Attributes;

namespace Activout.MemberData.Cli.Storage;

// Rows map snake_case columns via Dapper's MatchNamesWithUnderscores. Timestamps are ISO 8601 UTC text, except the
// changelog's captured_at/processed_at which keep LinkedIn's epoch milliseconds.

public sealed class SnapshotRecordRow
{
    public string Domain { get; init; } = "";
    public string RecordKey { get; init; } = "";
    public string Json { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string FirstSeenAt { get; init; } = "";
    public string LastSeenAt { get; init; } = "";
    public long FirstRunId { get; init; }
    public long LastRunId { get; init; }
    public string? DeletedAt { get; init; }
}

public sealed class ChangelogEventRow
{
    public string EventId { get; init; } = "";
    public string? ActivityId { get; init; }
    public string? ResourceName { get; init; }
    public string? ResourceId { get; init; }
    public string? Method { get; init; }
    public string? ActivityStatus { get; init; }
    public string? Owner { get; init; }
    public string? Actor { get; init; }
    public long? CapturedAt { get; init; }
    public long? ProcessedAt { get; init; }
    public string Json { get; init; } = "";
    public string FirstSeenAt { get; init; } = "";
    public long RunId { get; init; }
}

public sealed class SyncRunRow
{
    public long Id { get; init; }
    public string Kind { get; init; } = "";
    public string Mode { get; init; } = "";
    public string? Domain { get; init; }
    public string StartedAt { get; init; } = "";
    public string? FinishedAt { get; init; }
    public string Status { get; init; } = "";
    public string? DetailsJson { get; init; }
}

public sealed class SnapshotDomainRow
{
    public string Domain { get; init; } = "";
    public bool Documented { get; init; }
    public string Status { get; init; } = "";
    public long? Pages { get; init; }
    public long? Records { get; init; }
    public string? LastAttemptAt { get; init; }
    public string? LastSuccessAt { get; init; }
    public string? LastError { get; init; }
}

public sealed class DomainCountRow
{
    public string Domain { get; init; } = "";
    public long Live { get; init; }
    public long Deleted { get; init; }
}

public interface IMemberDao : IWithTransactions
{
    [SqlQuery("PRAGMA user_version")]
    Task<long> GetSchemaVersion();

    [SqlUpdate(Schema.V1)]
    Task ApplySchemaV1(IDbTransaction? transaction = null);

    // database_info and sync_checkpoints are both key/value tables.

    [SqlQuery("SELECT value FROM database_info WHERE key = @key")]
    Task<string?> GetInfo(string key, IDbTransaction? transaction = null);

    [SqlUpdate("INSERT INTO database_info (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value")]
    Task<int> SetInfo(string key, string value, IDbTransaction? transaction = null);

    [SqlQuery("SELECT value FROM sync_checkpoints WHERE name = @name")]
    Task<string?> GetCheckpoint(string name, IDbTransaction? transaction = null);

    [SqlUpdate("""
               INSERT INTO sync_checkpoints (name, value, updated_at) VALUES (@name, @value, @now)
               ON CONFLICT (name) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
               """)]
    Task<int> SetCheckpoint(string name, string value, string now, IDbTransaction? transaction = null);

    // Snapshot records

    /// <summary>Returns 1 when the row was inserted, 0 when an existing row was confirmed.</summary>
    [SqlQuery("""
              INSERT INTO snapshot_records (domain, record_key, json, content_hash, first_seen_at, last_seen_at,
                                            first_run_id, last_run_id)
              VALUES (@Domain, @RecordKey, @Json, @ContentHash, @FirstSeenAt, @LastSeenAt, @FirstRunId, @LastRunId)
              ON CONFLICT (domain, record_key) DO UPDATE SET json = excluded.json, last_seen_at = excluded.last_seen_at,
                  last_run_id = excluded.last_run_id, deleted_at = NULL
              RETURNING first_run_id = @LastRunId
              """)]
    Task<long> UpsertRecord([BindProperties] SnapshotRecordRow row, IDbTransaction? transaction = null);

    [SqlUpdate("""
               UPDATE snapshot_records SET deleted_at = @now
               WHERE domain = @domain AND last_run_id <> @runId AND deleted_at IS NULL
               """)]
    Task<int> TombstoneUnseen(string domain, long runId, string now, IDbTransaction? transaction = null);

    [SqlQuery("""
              SELECT * FROM snapshot_records
              WHERE (@domain IS NULL OR domain = @domain) AND (@includeDeleted OR deleted_at IS NULL)
              ORDER BY domain, first_seen_at, record_key LIMIT @limit OFFSET @offset
              """)]
    Task<IEnumerable<SnapshotRecordRow>> ListRecords(string? domain, bool includeDeleted, long limit, long offset);

    [SqlQuery("SELECT * FROM snapshot_records WHERE domain = @domain AND record_key = @recordKey")]
    Task<SnapshotRecordRow?> GetRecord(string domain, string recordKey);

    [SqlQuery("""
              SELECT domain, SUM(deleted_at IS NULL) AS live, SUM(deleted_at IS NOT NULL) AS deleted
              FROM snapshot_records GROUP BY domain ORDER BY domain
              """)]
    Task<IEnumerable<DomainCountRow>> CountRecords();

    // Changelog

    [SqlUpdate("""
               INSERT INTO changelog_events (event_id, activity_id, resource_name, resource_id, method, activity_status,
                   owner, actor, captured_at, processed_at, json, first_seen_at, run_id)
               VALUES (@EventId, @ActivityId, @ResourceName, @ResourceId, @Method, @ActivityStatus, @Owner, @Actor,
                   @CapturedAt, @ProcessedAt, @Json, @FirstSeenAt, @RunId)
               ON CONFLICT (event_id) DO NOTHING
               """)]
    Task<int> InsertEvent([BindProperties] ChangelogEventRow row, IDbTransaction? transaction = null);

    [SqlQuery("SELECT COUNT(*) FROM changelog_events")]
    Task<long> CountEvents();

    [SqlQuery("SELECT MIN(processed_at) FROM changelog_events")]
    Task<long?> EarliestEvent();

    // Sync bookkeeping

    [SqlQuery("""
              INSERT INTO sync_runs (kind, mode, domain, started_at, status) VALUES (@kind, @mode, @domain, @now, 'running')
              RETURNING id
              """)]
    Task<long> InsertRun(string kind, string mode, string? domain, string now);

    [SqlUpdate("UPDATE sync_runs SET finished_at = @now, status = @status, details_json = @detailsJson WHERE id = @id")]
    Task<int> FinishRun(long id, string now, string status, string detailsJson);

    [SqlQuery("SELECT * FROM sync_runs ORDER BY id DESC LIMIT @limit")]
    Task<IEnumerable<SyncRunRow>> RecentRuns(long limit);

    [SqlQuery("SELECT * FROM snapshot_domains ORDER BY domain")]
    Task<IEnumerable<SnapshotDomainRow>> ListDomains();

    [SqlUpdate("""
               INSERT INTO snapshot_domains (domain, documented, status, pages, records, last_attempt_at, last_success_at, last_error)
               VALUES (@Domain, @Documented, @Status, @Pages, @Records, @LastAttemptAt, @LastSuccessAt, @LastError)
               ON CONFLICT (domain) DO UPDATE SET documented = excluded.documented, status = excluded.status,
                   pages = excluded.pages, records = excluded.records, last_attempt_at = excluded.last_attempt_at,
                   last_success_at = COALESCE(excluded.last_success_at, snapshot_domains.last_success_at),
                   last_error = excluded.last_error
               """)]
    Task<int> SaveDomain([BindProperties] SnapshotDomainRow row, IDbTransaction? transaction = null);
}

internal static class Schema
{
    public const int Version = 1;

    public const string V1 = """
        CREATE TABLE database_info (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE TABLE snapshot_records (
            domain TEXT NOT NULL,
            record_key TEXT NOT NULL,
            json TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            first_seen_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            first_run_id INTEGER NOT NULL,
            last_run_id INTEGER NOT NULL,
            deleted_at TEXT,
            PRIMARY KEY (domain, record_key)
        );
        CREATE INDEX ix_snapshot_records_run ON snapshot_records (domain, last_run_id);
        CREATE TABLE changelog_events (
            event_id TEXT PRIMARY KEY,
            activity_id TEXT,
            resource_name TEXT,
            resource_id TEXT,
            method TEXT,
            activity_status TEXT,
            owner TEXT,
            actor TEXT,
            captured_at INTEGER,
            processed_at INTEGER,
            json TEXT NOT NULL,
            first_seen_at TEXT NOT NULL,
            run_id INTEGER NOT NULL
        );
        CREATE INDEX ix_changelog_events_processed ON changelog_events (processed_at);
        CREATE INDEX ix_changelog_events_resource ON changelog_events (resource_name, resource_id);
        CREATE TABLE sync_runs (
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL CHECK (kind IN ('snapshot', 'changelog')),
            mode TEXT NOT NULL,
            domain TEXT,
            started_at TEXT NOT NULL,
            finished_at TEXT,
            status TEXT NOT NULL CHECK (status IN ('running', 'success', 'partial', 'failed')),
            details_json TEXT
        );
        CREATE TABLE sync_checkpoints (
            name TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE snapshot_domains (
            domain TEXT PRIMARY KEY,
            documented INTEGER NOT NULL,
            status TEXT NOT NULL,
            pages INTEGER,
            records INTEGER,
            last_attempt_at TEXT,
            last_success_at TEXT,
            last_error TEXT
        );
        PRAGMA user_version = 1;
        """;
}
