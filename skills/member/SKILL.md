---
name: member
description: Answer questions about the user's own LinkedIn data (connections, messages, invitations, endorsements, positions, company follows, learning history and more) with the read-only `member` CLI (Activout.MemberData, LinkedIn Member Data Portability API). Use when the user asks who they are connected to, searches their LinkedIn messages, wants counts or lists from their LinkedIn data, or asks whether that data is up to date.
---

# Reading LinkedIn data with `member`

`member` keeps a local SQLite copy of the user's LinkedIn Member Data Portability (DMA) data. Almost every question
is answered **offline** from that copy. Only `member sync` and `member auth registration/register/import-token`
contact LinkedIn.

## Ground rules

- **Read-only.** `member` cannot change anything on LinkedIn: no messages, no connection removal, no profile edits.
  Don't suggest otherwise.
- **Never handle the token.** Never run `member auth import-token` yourself, and never ask for the token or read it
  from files. When a command exits with code 3, tell the user to run `member auth status` and, if needed, generate a
  new token and import it themselves.
- **Ask before `member sync`.** Check `member sync status --format json` first. Suggest a sync only when the data is
  stale for the question asked (`last_full_snapshot_at`, `changelog_last_success_at`), or when there is a
  `changelog_gap` warning.
- **Other people's data.** The database holds names, positions, profile URLs and message text of people other than
  the user. Answer with what was asked: counts, the matching rows, short excerpts. Don't dump whole tables or full
  message threads into the conversation, and never write results anywhere public.
- **Use `--format json`** for anything you will process. stdout then holds exactly one document:
  `{"ok", "exit_code", "result"}` or `{"ok": false, "exit_code", "error": {"type", "message"}}`.

## What is there

```sh
member domains list --format json      # domain, status (synced/no_data/not_returned/unsupported), records, last_success_at
member database info --format json     # path, size, member, record and event counts
```

Snapshot data lives in `snapshot_records` (one row per record, raw JSON in `json`, `deleted_at` set when LinkedIn
no longer returns it). Domain names are case sensitive (`CONNECTIONS`, `INBOX`, `Events`). Record JSON uses
LinkedIn's human-readable keys; look at one record before writing a query:

```sh
member data list --domain CONNECTIONS --limit 1 --format json
```

Common keys:

| Domain | Keys |
| --- | --- |
| `CONNECTIONS` | `First Name`, `Last Name`, `Company`, `Position`, `Connected On` (e.g. `23 Oct 2026`), `URL`, `Email Address` |
| `INBOX` | `CONVERSATION ID`, `CONVERSATION TITLE`, `FROM`, `TO`, `DATE` (`YYYY-MM-DD HH:MM:SS UTC`), `SUBJECT`, `CONTENT`, `FOLDER`, `SENDER PROFILE URL`, `RECIPIENT PROFILE URLS` |
| `INVITATIONS` | `From`, `To`, `Sent At`, `Message`, `Direction`, `inviterProfileUrl`, `inviteeProfileUrl` |
| `POSITIONS` | `Company Name`, `Title`, `Description`, `Location`, `Started On`, `Finished On` |
| `COMPANY_FOLLOWS` | `Organization`, `Followed On` |
| `ENDORSEMENTS` | `Skill Name`, `Endorsement Date`, `Endorsement Status`, endorser/endorsee names and URLs |
| `RECOMMENDATIONS` | `First Name`, `Last Name`, `Company`, `Job Title`, `Text`, `Creation Date` (`MM/DD/YY, HH:MM AM`), `Status` |
| `MEMBER_SHARE_INFO` | `Date` (`YYYY-MM-DD HH:MM:SS`), `ShareCommentary`, `ShareLink`, `SharedUrl`, `MediaUrl`, `Visibility` |

`MEMBER_SHARE_INFO` holds the user's own posts (the name doesn't make that obvious). Group by `ShareLink`, because
the same post can appear more than once. `INSTANT_REPOSTS` holds reposts without the user's own text. `ARTICLES` holds
only long-form articles, stored as one record per HTML line, so its record count doesn't count articles.

`RECOMMENDATIONS` mixes given and received recommendations. `Status` tells them apart: `VISIBLE` means received
(the name is the author), an empty `Status` means given (the name is the person recommended). Don't guess the
direction from whether the text mentions the user's name.

Changelog events (likes, comments, messages and so on after the user consented, kept for 28 days by LinkedIn) live
in `changelog_events`: `resource_name`, `method`, `activity_status`, `captured_at` and `processed_at` (epoch ms),
and the raw `json`.

## Query (offline, read-only SQL)

```sh
member data query "SELECT ..." --format json      # result.columns, result.rows (objects), result.truncated
```

Only a single SELECT is allowed (exit code 8 otherwise). Rows stop at `--max-rows` (default 1000; check
`truncated`). Keys with spaces need quoted JSON paths: `json_extract(json, '$."First Name"')`. Always add
`deleted_at IS NULL` unless the user asks about removed data.

```sh
# Connections per company
member data query "SELECT json_extract(json,'$.Company') AS company, count(*) AS n FROM snapshot_records
  WHERE domain='CONNECTIONS' AND deleted_at IS NULL GROUP BY 1 ORDER BY n DESC LIMIT 20" --format json

# Find a connection by name
member data query "SELECT json_extract(json,'$.\"First Name\"') AS first, json_extract(json,'$.\"Last Name\"') AS last,
  json_extract(json,'$.Company') AS company, json_extract(json,'$.Position') AS position
  FROM snapshot_records WHERE domain='CONNECTIONS' AND deleted_at IS NULL
  AND json_extract(json,'$.\"Last Name\"') LIKE '%andersson%'" --format json

# Messages from a person, newest first
member data query "SELECT json_extract(json,'$.DATE') AS date, json_extract(json,'$.CONTENT') AS content
  FROM snapshot_records WHERE domain='INBOX' AND deleted_at IS NULL
  AND json_extract(json,'$.FROM') = 'Jane Doe' ORDER BY date DESC LIMIT 10" --format json

# Connections made per year ('Connected On' is 'DD Mon YYYY')
member data query "SELECT substr(json_extract(json,'$.\"Connected On\"'), -4) AS year, count(*) AS n
  FROM snapshot_records WHERE domain='CONNECTIONS' AND deleted_at IS NULL GROUP BY 1 ORDER BY 1" --format json
```

Reasoning about the people (ranking, classifying, drafting messages) is your job, not `member`'s. Do it on the
smallest result set that answers the question.

## Freshness

`member sync status --format json` shows `last_full_snapshot_at`, `changelog_last_success_at`, `changelog_events`
and `changelog_gap`. A domain with status `no_data` returned nothing from LinkedIn; that doesn't prove it is empty
on LinkedIn, because some domains are processed hours or days after consent. An empty query result for a domain
that has never synced means "not downloaded", not "nothing exists".

If the user agrees to a sync: `member sync --format json` (a minute or so for a full snapshot). Report
`result.warnings` and per-domain errors.

## Exit codes

| Code | Meaning | What to do |
| --- | --- | --- |
| 0 | Success | |
| 2 | Usage, missing database, query timeout | Fix the arguments; `member <command> --help` |
| 3 | Token missing, expired or revoked; keychain unavailable | The user runs `member auth status` and imports a new token |
| 4 | LinkedIn or network failure | Retry later |
| 5 | Partial sync | Report per-domain errors; nothing stored was lost |
| 6 | Another sync is running | Wait |
| 7 | Database corrupt, too new, or for another member | Ask the user; use `--database` for other accounts |
| 8 | Unsafe SQL | Rewrite as one SELECT |
