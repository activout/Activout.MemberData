# Storage and sync

## One database per member

The default database is `member.db` in the platform data directory:

| Platform | Path |
| --- | --- |
| macOS | `~/Library/Application Support/Activout.MemberData/member.db` |
| Windows | `%LOCALAPPDATA%\Activout.MemberData\member.db` |
| Linux | `$XDG_DATA_HOME/activout-memberdata/member.db` (default `~/.local/share/…`) |

`--database PATH` selects another file. On the first sync the database is bound to the member URN that LinkedIn
reports for the token. A token for another member is refused with exit code 7, so data from two accounts is never
mixed. The directory is created with mode 700 and the file with mode 600 on Unix.

SQLite runs in WAL mode with a 30-second busy timeout. Every page is written in its own transaction, and network I/O
never happens inside a transaction. A lock file (`member.db.lock`) prevents two syncs from running at once.

## Tables

| Table | Contents |
| --- | --- |
| `snapshot_records` | `domain`, `record_key`, `json` (exactly as LinkedIn sent it), `content_hash`, `first_seen_at`, `last_seen_at`, `first_run_id`, `last_run_id`, `deleted_at` |
| `changelog_events` | `event_id` (LinkedIn's `id`), `activity_id`, `resource_name`, `resource_id`, `method`, `activity_status`, `owner`, `actor`, `captured_at`, `processed_at` (epoch ms), `json`, `first_seen_at`, `run_id` |
| `snapshot_domains` | Per domain: `documented`, `status`, `pages`, `records`, `last_attempt_at`, `last_success_at`, `last_error` |
| `sync_runs` | `kind` (snapshot/changelog), `mode`, `domain`, timestamps, `status` (running/success/partial/failed), `details_json` (sanitised) |
| `sync_checkpoints` | `changelog.processed_at`, `changelog.last_success_at`, `snapshot.last_full_success_at` |
| `database_info` | `member`, `developer_application`, `created_at` |

Timestamps are ISO 8601 UTC text. The schema version is `PRAGMA user_version`. Payloads are never mapped to
per-domain columns; use SQLite's JSON functions instead:

```sql
SELECT json_extract(json, '$."First Name"') || ' ' || json_extract(json, '$."Last Name"') AS name,
       json_extract(json, '$.Company') AS company
FROM snapshot_records WHERE domain = 'CONNECTIONS' AND deleted_at IS NULL;
```

Keys with spaces need the quoted path form `'$."First Name"'`.

## Snapshot record identity

Snapshot records have no identifiers (see [api-findings.md](api-findings.md)). `record_key` is the first 32 hex
characters of the SHA-256 of the record's canonical JSON (object keys sorted). Identical records within one domain
are numbered: the second copy gets the suffix `-2`, the third `-3`, and so on.

This means:

- The same record gets the same key in every sync, so syncs are idempotent.
- Identical records are never merged. Two identical messages stay two rows.
- A record whose content changes on LinkedIn's side becomes a new key, and the old key is tombstoned. History of
  edits is therefore visible as `deleted_at` rows (`--include-deleted`).
- A domain-specific natural key was rejected: the live data showed empty and duplicated `URL`s in `CONNECTIONS`,
  and no other domain has a reliable id.

## Snapshot sync

1. Read the snapshot **without** a domain, page by page until LinkedIn's 404 terminator. This discovers every domain
   with data, including undocumented ones (`WHATSAPP_NUMBERS`, mixed-case `Events`). Each record is upserted; a
   record seen again gets `last_seen_at`/`last_run_id` updated and `deleted_at` cleared.
2. If and only if the whole pass completed, reconcile each domain that returned data: rows of that domain not seen
   in this run get `deleted_at`.
3. Probe every documented (or previously seen) domain that did not appear, one request each:
   - data: read it fully and reconcile it as in steps 1 and 2 (`synced`)
   - 404 on page 0: `no_data`, or `not_returned` if records are stored. **Stored records are kept**, because an
     empty answer is not proof of deletion.
   - 400 "not supported": `unsupported`
   - other errors: `failed`, recorded per domain without stopping the other domains
4. Record `snapshot.last_full_success_at`.

If the pass fails part-way (network, 5xx after retries, corrupt JSON), the pages read so far are kept, **nothing is
tombstoned**, and the run is `partial` (exit code 5). The next sync starts a new pass.

`member sync --domain D` does the same for one domain, case-sensitively.

## Changelog sync

- Start at the `changelog.processed_at` checkpoint, or at the registration time (`regulatedAt`) on the first run.
- Request 10 events at a time with `startTime = checkpoint` (inclusive). Insert each event with `ON CONFLICT
  (event_id) DO NOTHING`, and move the checkpoint to the newest `processedAt` **in the same transaction**. A crash
  can therefore only cause re-reading, never a gap.
- Stop at a short page. If a full page of already stored events shares one `processedAt`, page past it with
  `start`; if that makes no progress either, stop with a warning rather than loop.
- `FAILURE` and `SUCCESSFUL_REPLAY` events have different `id`s and the same `activity_id`; both are kept.
  `DELETE` events are kept with their empty payloads.
- **Changelog events are never applied to `snapshot_records`.** LinkedIn documents no mapping between changelog
  `resourceName`s (`socialActions/likes`, `messages`, …) and snapshot domains, and the payload shapes differ.
  The changelog is an event archive; the snapshot is current state, reconciled by full snapshot syncs.

## The 28-day limit

LinkedIn returns changelog events for 28 days only. `member` measures the time since the last successful changelog
sync (or since registration):

- After 21 days, `sync` and `sync status` warn: sync within N days.
- After 28 days, they report that events **may be permanently missing**. The default strategy then also runs a
  recovery snapshot. A snapshot reconciles current state but **does not restore lost events**, and `member` says
  so.

## Default strategy (`member sync`)

1. Check registration and the member binding.
2. Changelog catch-up, every time.
3. Snapshot, when there has never been a complete one (`initial`), the last one is older than a day (`refresh`), the
   changelog gap exceeds 28 days (`recovery`), or `--full`/`--domain` is given. Otherwise `incremental` (changelog
   only).

Changelog events can arrive between snapshot pages; they are kept, and the next sync's inclusive `startTime`
overlap catches anything newer. Running `member sync` hourly or daily is safe, and repeated runs are idempotent.

## Scheduling

Run it from cron, launchd or Task Scheduler as the same user who imported the token, so the keychain is
available. For example, hourly at minute 17:

```cron
17 * * * * /usr/local/bin/member sync --format json >> ~/.local/state/member-sync.log 2>&1
```

On headless Linux use the environment source explicitly ([security-and-privacy.md](security-and-privacy.md)).

## Backups

The database is a single SQLite file. Back it up with `sqlite3 member.db ".backup member-backup.db"` while no sync
runs, and store backups as carefully as the original: they contain your messages.
