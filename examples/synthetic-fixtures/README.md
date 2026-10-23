# Synthetic fixtures

Fully synthetic LinkedIn Member Data Portability responses for tests and examples. Their **structure** follows
what the live API returned for an owner-authorised account on 2026-10-23 (field names, nesting, value types,
one element per snapshot page, string-typed values, mixed-case domain names, `No data found` terminators). Every
**value** is invented: names, URLs, URNs, ids, dates, message text and numbers do not come from real data.

| File | Shape |
| --- | --- |
| `member-authorization.json` | `GET /rest/memberAuthorizations?q=memberAndApplication` |
| `snapshot-all-0.json` … `snapshot-all-3.json` | `GET /rest/memberSnapshotData?q=criteria&start=N` without a domain |
| `snapshot-no-data.json` | 404 body ending a paged snapshot or meaning "no data for this domain" |
| `snapshot-unsupported.json` | 400 body for a domain the API rejects (seen live for LOGIN, EVENTS, EASYAPPLY_BLOCKING) |
| `changelog.json` | `GET /rest/memberChangeLogs?q=memberAndApplication`: like, delete, failure and replay, unprocessed event |
