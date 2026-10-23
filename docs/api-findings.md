# API findings

What LinkedIn documents for the Member Data Portability (Member) API, and what an owner-authorised live account
showed on **2026-10-23**. Documentation version: `li-dma-data-portability-2026-08`.

Sources:

- [Member Data Portability (Member)](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/member-data-portability-member/?view=li-dma-data-portability-2026-08): onboarding
- [Member Snapshot API](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/shared/member-snapshot-api?view=li-dma-data-portability-2026-08)
- [Member Changelog API](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/shared/member-changelog-api?view=li-dma-data-portability-2026-08)
- [Member Authorization Management API](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/shared/member-authorization-management-api?view=li-dma-data-portability-2026-08)
- [Member Snapshot Domains](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/shared/snapshot-domain?view=li-dma-data-portability-2026-08)
- [Portability API terms](https://www.linkedin.com/legal/l/portability-api-terms), [API terms of use](https://www.linkedin.com/legal/l/api-terms-of-use)

## Common

| | |
| --- | --- |
| Base URL | `https://api.linkedin.com/rest/` |
| Headers | `Authorization: Bearer <token>`, `Linkedin-Version: 202312` (the snapshot endpoint accepts only this; others fail with `426 NONEXISTENT_VERSION`), `X-Restli-Protocol-Version: 2.0.0` |
| Error body | `{"code":"","message":"…","status":N}` |

## Authentication

- **Product:** "Member Data Portability API (Member)". The app must be created with the
  [Member Data Portability (Member) Default Company](https://www.linkedin.com/company/member-data-portability-member-default-company)
  page. Don't create a company page of your own.
- **Scope:** `r_dma_portability_self_serve` (onboarding page). The shared API pages list `r_dma_portability_member`
  and `r_dma_portability_3rd_party` instead. **Verified:** a token generated with `r_dma_portability_self_serve`
  works for every call below.
- **Flow:** the documented path is LinkedIn's [OAuth Token Generator](https://www.linkedin.com/developers/tools/oauth).
  `member` therefore only imports tokens; there is no `member auth login`, because no browser callback flow is
  documented for this product.
- **Eligibility:** EEA and Swiss members only.
- **Token lifetime and refresh: unresolved.** The Member DMA pages do not state a lifetime or mention refresh
  tokens, and API responses carry no expiry information. Token introspection would need the app's client secret,
  which `member` does not handle. `member auth import-token --expires-in-days N` records the lifetime the token
  generator shows. When LinkedIn rejects the token (401), `member` asks for a new one and never pretends to
  refresh. It is unverified whether the lifetime is 60 days (typical for LinkedIn 3-legged tokens) or 12 months.

## Member Authorization Management

`GET memberAuthorizations?q=memberAndApplication` returns
`elements[0].memberComplianceAuthorizationKey.{member, developerApplication}`, `regulatedAt` (epoch ms since which
data is archived) and `memberComplianceScopes: ["DMA"]`. `POST memberAuthorizations` with body `{}` registers the
member manually. LinkedIn says registration is automatic and recommends the POST only for debugging.

**Live:** GET returned the member URN, the application URN and `regulatedAt` within minutes of token generation.
`member` uses the member URN to bind each database to one member. POST was not called on the live account,
because registration already existed.

## Member Snapshot

`GET memberSnapshotData?q=criteria[&domain=D][&start=N]`

- Each page has exactly one element: `{"snapshotDomain": D, "snapshotData": [ {...}, ... ]}`.
- `start` is a **page index**, not a record offset. The end of the data is a **404 `No data found for this
  memberId.`** (or `… for this domain and memberId.`). LinkedIn warns that `paging.total` may be inaccurate.
- **Live, per domain:** page 0 returning 404 means the domain has no data, and an unknown or unsupported domain
  returns **400 `The following domain is not supported: D`**.
- **Live, without `domain`:** the API returns every domain with data, one domain at a time (contiguous pages, 51
  pages for the test account). This is the only way to discover domains, and it returned two that differ from the
  documentation:
  - `WHATSAPP_NUMBERS`: not in the documented list.
  - `Events` (mixed case): the documented `EVENTS` returns 400 "not supported", while `domain=Events` works.
- **Live, documented domains rejected with 400:** `LOGIN`, `EVENTS`, `EASYAPPLY_BLOCKING`.
- **Records have no identifiers.** Values are flat strings keyed by human-readable column names (`"First Name"`,
  `"Connected On"`, `"CONVERSATION ID"`), similar to LinkedIn's CSV export. Some records are byte-for-byte identical
  (repeated messages, duplicate phone numbers). `CONNECTIONS.URL` is empty for some connections and duplicated for
  others, so it is not a usable key. `ARTICLES` records are HTML fragments under a `"<html>"` key.
- Page sizes vary from 1 to about 1,500 records. `INBOX` took 19 pages for about 26,000 messages.

`member` therefore reads the combined query for discovery and data, then probes every documented domain that
didn't appear, which tells "no data" apart from "unsupported". See [storage-and-sync.md](storage-and-sync.md).

## Member Changelog

`GET memberChangeLogs?q=memberAndApplication&count=N[&startTime=MS]`

- `count` 1–50, with 10 recommended. `startTime` is **inclusive** epoch ms; LinkedIn recommends using the newest
  `processedAt` from the previous response, which repeats that event on the next call (deduplicate by `id`).
- Events go back at most **28 days**, and only from the moment of consent.
- The fields are `id` (number), `activityId` (shared by reprocessings), `resourceName`, `resourceId`, `resourceUri`,
  `method` (`CREATE`/`UPDATE`/`PARTIAL_UPDATE`/`DELETE`/`ACTION`), `methodName`, `activityStatus`
  (`SUCCESS`/`FAILURE`/`SUCCESSFUL_REPLAY`), `owner`, `actor`, `capturedAt`, `processedAt`, `configVersion`, `activity`,
  `processedActivity`, `siblingActivities` and `parentSiblingActivities`. `DELETE` events have empty `activity` and
  `processedActivity`. Unprocessable fields appear as `"Unable_to_process_this_field."` or `{"message": "Unable to
  process this event."}`.
- LinkedIn recommends querying once an hour at most, spread out. No numeric rate limit is published.
- **Live:** three `socialActions/likes` `CREATE` events appeared within an hour of consent. With an inclusive
  `startTime`, the newest event was returned again and deduplicated, as documented. Whether `start` pages within
  one `processedAt` is unverified (it was never needed). `member` only uses it when a full page shares one
  `processedAt`, and stops with a warning if that makes no progress.

## Unresolved

- Token lifetime and refresh (above).
- Rate limits: none published. `member` retries 429 and 5xx with backoff and honours `Retry-After`.
- Whether snapshot data for the slower domains (`ALL_COMMENTS`, `MEMBER_SHARE_INFO`, `SEARCHES` and others that
  returned 404 on the day of consent) appears later. `member` reports them as `no_data` and probes them on every
  full sync.
- How changelog `resourceName`s map to snapshot domains. No mapping is documented, so `member` never applies
  changelog events to snapshot records.
