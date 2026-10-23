# Troubleshooting

## Exit codes

| Code | `error.type` | Meaning | What to do |
| --- | --- | --- | --- |
| 0 | | Success | |
| 1 | `unexpected` | A bug; the stack trace is on stderr | Open an issue without personal data |
| 2 | `usage` | Invalid arguments, missing database, query timeout | Check `member <command> --help` |
| 3 | `auth_required` | No token, token expired/invalid/revoked, missing scope, no DMA registration, keychain unavailable | `member auth status`; generate a new token and `member auth import-token` |
| 4 | `api_or_network` | LinkedIn error or network failure after retries | Retry later; nothing stored was lost |
| 5 | `partial_sync` | Some steps failed; the result lists them per domain | Read `result.snapshot.domains[].error` and `result.changelog.error`; sync again later |
| 6 | `busy` | Another `member` process is using the database | Wait and retry |
| 7 | `database` | Corrupt database, newer schema, or a token for another member | Use `--database` for a different member; restore a backup |
| 8 | `unsafe_query` | The SQL is not a single read-only SELECT | Rewrite as one SELECT |
| 130 | | Cancelled (Ctrl+C) | |

With JSON output, success is `{"ok": true, "exit_code": 0, "result": …}`, a partial sync is
`{"ok": false, "exit_code": 5, "result": …}` and a failure is
`{"ok": false, "exit_code": N, "error": {"type": "…", "message": "…"}}`. Exactly one document goes to stdout;
progress and warnings go to stderr.

## Common problems

**Token generator: "We can’t verify the authenticity of your request because the state parameter was
modified."** A browser extension, often a password manager, altered the sign-in redirect. Disable it for the page,
or generate the token in a private/incognito window without extensions ([dma-setup.md](dma-setup.md#3-generate-a-token)).

**`No access token; run 'member auth import-token'`.** You haven't imported a token, or you're running as another
user, or with another credential source than when you imported it.

**`LinkedIn rejected the token … invalid, expired or revoked`.** Generate a new token
([dma-setup.md](dma-setup.md)) and import it.

**`LinkedIn refused access … r_dma_portability_self_serve`.** The token was generated with another scope, or the
app lacks the Member Data Portability (Member) product.

**`LinkedIn reports no DMA registration for this token`.** Wait a few minutes after generating the token, then
`member auth registration`; if it still reports nothing, `member auth register`.

**`The Linux Secret Service is unavailable`.** No unlocked keyring (typical on servers and over SSH). Install
`libsecret-tools` and unlock a keyring, or opt into the environment source ([security-and-privacy.md](security-and-privacy.md)).

**`macOS Keychain could not read the credential (OSStatus -25308)`.** The keychain is locked or the access dialog
was denied. Unlock the login keychain, or run once interactively and choose **Always Allow**.

**`… belongs to urn:li:person:A, but the token is for urn:li:person:B`.** Each database holds one member. Use
`--database other.db` for the second account.

**Many domains are `no_data`.** LinkedIn builds some domains offline after consent; they can appear hours or days
later, and every full sync probes them again. Domains you have never used stay empty.

**`not_returned` warning.** A domain that had records returned nothing this time. The records are kept on purpose;
an empty answer is not proof of deletion.

**Changelog gap warning.** More than 21 (or 28) days since the last changelog sync. Events older than 28 days can't
be recovered; schedule `member sync` (see [storage-and-sync.md](storage-and-sync.md#scheduling)).

**Windows: "Windows protected your PC".** The executable is unsigned. Click **More info**, then **Run anyway**.

**macOS: "cannot be opened because the developer cannot be verified".** Run `xattr -d com.apple.quarantine member`.
