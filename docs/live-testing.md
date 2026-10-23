# Live testing

The offline tests (`dotnet test`) use synthetic fixtures only. The live test checks the real API with an
owner-authorised account, and it is run by hand because it needs the owner's own token.

## Procedure

1. Generate a token for your own account ([dma-setup.md](dma-setup.md)) and import it without the token appearing
   on screen. For example, from a `KEY=VALUE` file:
   ```sh
   sed -n 's/^MEMBER_DATA_ACCESS_TOKEN=//p' ~/.linkedin | member auth import-token --stdin
   ```
2. `member auth status`, then `member auth registration`.
3. `member sync` (initial full snapshot plus changelog).
4. `member sync` twice more: both must be `incremental`, with 0 new events beyond real new activity.
5. `member sync --full`: it must report 0 `added` and 0 `removed` per domain if nothing changed on LinkedIn, and the
   changelog count must be unchanged.
6. The read-only SQL checks from [security-and-privacy.md](security-and-privacy.md#read-only-sql), against a copy of
   the live database.
7. A self-contained single-file build (`dotnet publish … -p:PublishSingleFile=true`) must run `auth status`, `sync`
   and `data query`.

Keep raw captures and the live database **outside the repository**. Report only aggregates: never names, message
text, URLs, emails, ids or tokens.

## Report: 2026-10-23

| | |
| --- | --- |
| Run (UTC) | 2026-10-23 13:21–13:58 |
| Platform | macOS 26 (Darwin 25.6), Apple silicon, .NET SDK 10.0.201 |
| API | `api.linkedin.com/rest`, `Linkedin-Version: 202312` |
| Scope | `r_dma_portability_self_serve` (from the OAuth Token Generator); `memberComplianceScopes: ["DMA"]` |
| Credential source | macOS Keychain, imported through stdin |
| Token expiry | **Unknown.** Not shown in API responses; not recorded at import |

### Results

| Check | Result |
| --- | --- |
| `auth import-token --stdin` (verified with LinkedIn) | Passed |
| `auth status` (no token value in output) | Passed |
| `auth registration` | Passed: registered, `regulatedAt` 2026-10-23 13:21:59 UTC |
| `auth register` (POST) | **Not run**: already registered; covered by offline tests only |
| Initial `sync` | Passed after one fix (see below): strategy `initial`, 38 s |
| Two incremental syncs | Passed: `incremental`, 0 new events, the inclusive-overlap event deduplicated |
| `sync --full` | Passed: 37 s, 51 snapshot pages, 32,023 records, 0 added, 0 removed |
| Database bound to the member URN | Passed |
| Changelog | 3 events (`socialActions/likes`, `CREATE`, `SUCCESS`), stored once each |
| Unsafe SQL (DELETE, stacked statements, ATTACH, PRAGMA, `load_extension`, CREATE, `VACUUM INTO`) | Passed: all rejected, no files created |
| Recursive CTE timeout | Passed: interrupted after `--timeout` |
| Self-contained osx-arm64 single file (37 MB, compressed) | Passed: `auth status`, `sync --full`, `sync`, `data query` |
| Windows and Linux binaries, Linux Secret Service, Windows Credential Manager | **Not run live**: CI builds and tests on all three operating systems, but only macOS was exercised against LinkedIn |

The first live sync found a bug that the synthetic tests had not covered (SQLite returned an integer where the
code expected a boolean). Nothing was lost: the pass was marked partial and no records were tombstoned. After the
fix, the run above passed, and the offline tests now go through the same code path.

### Domains

68 domains were considered: the 66 documented plus 2 discovered (`WHATSAPP_NUMBERS`, `Events`).

| Status | Count |
| --- | --- |
| `synced` (returned data) | 29 |
| `no_data` (404 on page 0) | 36 |
| `unsupported` (400): `LOGIN`, `EVENTS`, `EASYAPPLY_BLOCKING` | 3 |

| Domain | Pages | Records |
| --- | --- | --- |
| AD_TARGETING | 1 | 1 |
| ARTICLES | 2 | 292 |
| CAUSES_YOU_CARE_ABOUT | 1 | 2 |
| COMPANY_FOLLOWS | 1 | 586 |
| CONNECTIONS | 1 | 3706 |
| COURSES | 1 | 2 |
| EDUCATION | 1 | 1 |
| EMAIL_ADDRESSES | 1 | 3 |
| ENDORSEMENTS | 2 | 186 |
| Events | 1 | 39 |
| GROUPS | 1 | 28 |
| INBOX | 19 | 25746 |
| INVITATIONS | 1 | 175 |
| LANGUAGES | 1 | 2 |
| LEARNING | 2 | 1044 |
| MARKETPLACE_PROVIDERS | 1 | 1 |
| PHONE_NUMBERS | 1 | 5 |
| POSITIONS | 1 | 11 |
| PROFILE | 1 | 1 |
| PROFILE_SUMMARY | 1 | 1 |
| PROJECTS | 1 | 1 |
| RECEIPTS_LBP | 1 | 7 |
| RECOMMENDATIONS | 2 | 33 |
| REGISTRATION | 1 | 1 |
| REVIEWS | 1 | 7 |
| RICH_MEDIA | 1 | 92 |
| SKILLS | 1 | 47 |
| TRUSTED_GRAPH | 1 | 2 |
| WHATSAPP_NUMBERS | 1 | 1 |

188 records were exact duplicates of another record in the same domain (mostly `ARTICLES` HTML fragments). They
are kept as separate rows with `-2`, `-3`, … keys.

### Coverage caveats and outstanding tests

- The token was generated on the test day, so many domains (comments, shares, likes, searches) may still have been
  in LinkedIn's offline processing. Their `no_data` status is honest, not evidence that they are empty.
- No changelog `DELETE`, `UPDATE`, `FAILURE` or `SUCCESSFUL_REPLAY` events occurred. They are covered by synthetic
  fixtures only; no destructive edits were made to the account to provoke them.
- The 28-day gap, the 21-day warning and recovery were tested offline with a simulated clock; they cannot be
  observed live in one session.
- Snapshot tombstoning after a real deletion on LinkedIn was not observed live (offline test only).
- Token expiry and the 401 path were tested offline only.

### Fixtures

`examples/synthetic-fixtures/` follows the observed structure (one element per page, string-valued records with
human-readable keys, mixed-case domain names, the 404 and 400 bodies, the changelog event layout) with invented
values. No live JSON was copied. Before committing, the repository was scanned for the live member URN, the
application URN, the owner's names and email domain, and a sample of connection names and conversation ids from the
live database (`scripts/scan-pii.sh`).
