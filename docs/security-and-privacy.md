# Security and privacy

## The token

The access token grants read access to everything in your LinkedIn data, including your messages. `member` treats
it as follows:

- **Default: the operating system's secure store.** The token and its non-secret metadata (import time, expiry) are
  stored as one item:
  - macOS: login Keychain, service `Activout.MemberData`, account `access-token`. The first time a new `member`
    binary reads it, macOS may ask for permission; answer **Always Allow**.
  - Windows: Credential Manager, generic credential `Activout.MemberData:access-token`, stored per user.
  - Linux: Secret Service through `secret-tool` (package `libsecret-tools`). The secret passes only through
    `secret-tool`'s stdin and stdout, never through its arguments.
- **No fallback.** If the store is missing or locked, which is common on headless Linux, `member` stops with exit
  code 3. It never silently falls back to environment variables or plaintext files.
- **Environment variables: explicit opt-in only.** Use `--credential-source environment` or
  `MEMBERDATA_CREDENTIAL_SOURCE=environment` to read `MEMBERDATA_ACCESS_TOKEN` (and optionally
  `MEMBERDATA_TOKEN_EXPIRES_AT`). Without the opt-in, these variables are ignored. Environment variables can leak
  into child processes, crash dumps, `/proc/<pid>/environ`, CI logs and shell history, so use them at your own
  risk and fill them from a secrets manager.
- The token is never accepted as a command argument, where it would appear in process listings and shell history.
  `import-token` reads it from a hidden prompt or from stdin.
- The token is never written to stdout, stderr, the database, sync run details or exception messages. Error
  messages contain LinkedIn's error text and the HTTP status only. The tests assert that a synthetic token never
  appears in any output.
- `member auth logout` deletes the stored token and leaves the database alone.

### Why not a credential library

The spec suggested evaluating `ktsu.CredentialCache`. It is MIT-licensed and supports all three platforms, but it
pulls in `ktsu.Semantics.Strings` (a large physics, units and music-theory library) from a single maintainer, and it
keys secrets by its own persona GUIDs. `Devlooped.CredentialManager` requires accepting the OSMF EULA. For one
secret, the platform calls are about 200 lines with no dependencies (`Credentials.cs`): `SecKeychain*` on macOS,
`CredRead/CredWrite/CredDelete` on Windows and `secret-tool` on Linux.

## The database

- It contains your own data **and information about other people**: connections' names, positions and profile URLs,
  message contents, invitations, endorsements and recommendations. Treat it like your mailbox.
- It is created with Unix mode 600 in a 700 directory. On Windows it inherits the per-user profile ACLs.
- It is never uploaded anywhere by `member`. Back it up encrypted, and `member database purge --yes` deletes it.
- **AI agents:** an agent that runs `member data …` sends the results to its model provider. Ask the agent for
  aggregates and specific answers rather than whole tables, and don't give it the database file itself.
- The DMA terms let you store your portability data, subject to data protection law. They do **not** permit
  publishing or sharing other people's data. Don't commit the database, captures or query results to Git, issues or
  CI artefacts. `.gitignore` excludes `*.db`, `captures/`, `live/` and `.env*`.

## Read-only SQL

`member data query` is a local power-user interface; never expose it to untrusted clients. It is defended in layers,
because a regular expression that matches `SELECT` proves nothing:

1. A read-only SQLite connection (`Mode=ReadOnly`), plus `PRAGMA query_only = ON`.
2. An SQLite authoriser (`sqlite3_set_authorizer`) that allows only `SELECT`, column reads, recursive CTEs and
   functions. It denies `ATTACH`, `DETACH`, every `PRAGMA`, DDL, DML, transactions and virtual tables, and the
   functions `load_extension`, `fts3_tokenizer`, `readfile`, `writefile`, `edit` and `zipfile`.
3. The text is prepared once. Anything after the first statement, other than whitespace and comments, is rejected
   unexecuted, and `sqlite3_stmt_readonly` must confirm the statement (this catches `VACUUM INTO`).
4. A row limit (`--max-rows`, default 1000, with the result marked `truncated`) and a timeout (`--timeout`, default
   30 s, enforced with `sqlite3_interrupt`).

The tests run DELETE, UPDATE, INSERT, DROP, stacked statements, ATTACH, DETACH, PRAGMA writes and reads,
`load_extension`, virtual tables, `VACUUM INTO`, temp tables and CTE-wrapped DELETE, and check that every one is
rejected with exit code 8 and leaves the data unchanged.

## Network

Only `api.linkedin.com` is contacted, only by `sync`, `auth import-token`, `auth registration` and
`auth register`. Everything else works offline. GET requests are retried on network errors, 408, 429 and 5xx, with
exponential backoff and jitter, and `Retry-After` is honoured. 400, 401, 403 and 404 are never retried, and the
registration POST is never retried.
