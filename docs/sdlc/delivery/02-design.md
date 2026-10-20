# Delivery — Phase 4 design

SDLC phase 2 (design) for **Phase 4 — Digital delivery** in PRD §12 and PLAN §5/§7.
Written 2026-10-20 against Phase 1 as merged (`Nexus.Domain`, `CatalogueService`,
`CatalogueController`, the static storefront in `wwwroot`).

This phase delivers **the product**. Everything before it moves metadata and money;
this is the only phase that hands over the thing the customer paid for. It is also
the only phase where a mistake is irreversible in the customer's favour (a file that
leaves cannot be recalled) *and* irreversible against them (a deploy that mirrors over
`App_Data` destroys the catalogue).

Scope reminders, already decided and not reopened: Kuwait only, KWD at three decimals;
**a book is one bilingual PDF** (D5); no free books (D7); guest checkout and customer
accounts share one Identity store; the repository is **public**, so nothing may rely on
a URL or a token format being secret.

Requirements covered: PRD **DL-1 … DL-8**, PM-9, AC-3, AC-4, §10 (performance, security,
data protection), PLAN §7 in full.

---

## 0. What this phase assumes from Phases 2 and 3

Phase 4 branches off `main` **after Phase 3 merges**. It depends on three things that do
not exist yet:

| Needed | From | If it is missing |
| --- | --- | --- |
| `Order` with `Id`, `OrderNumber`, `Status`, `PaidAt`, `TapChargeId` | Phase 2 / 3 | Blocks the migration — the grant FK has nowhere to point |
| `OrderItem` with `BookId` | Phase 2 | Blocks grant issue — nothing says which books were bought |
| `Order.BuyerNameSnapshot`, `Order.BuyerEmailSnapshot` | Phase 2 | **Delivery adds them if Phase 2 did not.** The watermark must not read a live `Customer` row: the customer can delete their data (PRD §10) and the stamp has to stay reproducible and stable for the life of the order |
| A transactional seam in the Tap webhook where the order flips to `Paid` | Phase 3 | Grants must be issued in the *same* transaction — see §4.1 |

**Migration ordering trap.** EF generates migrations against the model as it stands on
the branch. Generating the delivery migration on a branch that does not contain Phase 3's
`Orders` table produces a migration with an FK to a non-existent table, and the idempotent
script applies them in order and fails on staging, not locally. Rule: **rebase on `main`
after Phase 3 merges, then generate the migration.** Never the other way round.

---

## 1. Domain

### 1.1 `DownloadGrant` (new)

One grant is one entitlement: *this order may fetch this book, this many times, until
this date.* It is the only thing that authorises a download; nothing else does.

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `int` identity | |
| `OrderId` | `int` FK → `Orders.Id`, `Restrict` | |
| `BookId` | `int` FK → `Books.Id`, `Restrict` | A book with grants can never be hard-deleted |
| `Sequence` | `int` | `0` for the grant issued at payment, `1..n` for re-issues. Part of the uniqueness key so re-issue is a new row, not an edit |
| `TokenHash` | `binary(32)` | SHA-256 of the token. **The plaintext token is never stored** — see §6 ADR-2 |
| `TokenLookup` | — | not a column; `TokenHash` is the lookup |
| `MaxDownloads` | `int` | **Snapshot** of `delivery.maxDownloads` at issue |
| `UsedCount` | `int` default `0` | |
| `IssuedAt` | `datetimeoffset(3)` | |
| `ExpiresAt` | `datetimeoffset(3)` | **Snapshot**: `IssuedAt + delivery.linkExpiryDays` |
| `State` | `int` | `GrantState` below |
| `RevokedAt` | `datetimeoffset(3)?` | |
| `RevokedReason` | `int?` | `RevocationReason` below |
| `RevokedByAdminUserId` | `nvarchar(450)?` | Null when revoked by the refund path rather than a person |
| `DistinctIpCount` | `int` default `0` | Denormalised from `DownloadLog`, maintained on write — keeps DL-8 off the hot read path |
| `FlaggedAt` | `datetimeoffset(3)?` | Set when `DistinctIpCount` first crosses the threshold. Flagging **never blocks** — §4.6 |
| `StampedKey` | `nvarchar(256)?` | Relative path of the cached stamped copy, or null |
| `StampedSourceSha` | `char(64)?` | The `Book.PdfSha256` the cache was built from. Mismatch ⇒ cache miss |
| `StampedAt` | `datetimeoffset(3)?` | |
| `AbortForgivenCount` | `int` default `0` | Bounds the compensating decrement in §4.4 |

Indexes:

```
UNIQUE  (TokenHash)                       -- the single seek on every download
UNIQUE  (OrderId, BookId, Sequence)       -- makes a double-issue a constraint violation,
                                          -- not a duplicate entitlement
        (OrderId)
        (State, ExpiresAt)                -- the nightly expiry sweep
        (FlaggedAt)  WHERE FlaggedAt IS NOT NULL
```

**No `rowversion`.** Optimistic concurrency is deliberately *not* used here; the counter
is protected by a single conditional `UPDATE` (§4.4, ADR-1). A `rowversion` column would
invite a retry loop, and a retry loop written slightly wrong over-grants.

```csharp
public enum GrantState { Active = 0, Exhausted = 1, Expired = 2, Revoked = 3 }

public enum RevocationReason
{
    None = 0, Refunded = 1, Chargeback = 2, AbuseSuspected = 3,
    OperatorRequest = 4, RightsWithdrawn = 5, CustomerDataDeleted = 6, ReIssued = 7
}
```

`Expired` is a *materialised* state written by the nightly sweep, purely so the admin
screen can filter on it. **It is never relied on at read time**: the download path checks
`ExpiresAt > now` itself, because a grant that expired four minutes ago has not been swept
yet and must still be refused.

### 1.2 `DownloadLog` (new)

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `bigint` identity | This is the table that grows |
| `GrantId` | `int` FK → `DownloadGrants.Id`, `Cascade` | |
| `AtUtc` | `datetimeoffset(3)` | |
| `Outcome` | `int` | `DownloadOutcome` below |
| `IpAddress` | `nvarchar(45)?` | Raw, for the admin screen (DL-4). **Nulled after 180 days** |
| `IpPrefix` | `nvarchar(45)` | `/32` for IPv4, `/64` for IPv6 — what distinctness is counted on (§4.6). Never nulled |
| `UserAgent` | `nvarchar(512)?` | Truncated, not parsed |
| `BytesSent` | `bigint?` | Set when the response completes |
| `Referrer` | — | not captured; the landing page sends `no-referrer` |

```csharp
public enum DownloadOutcome
{
    Served = 0, Aborted = 1, Expired = 2, Exhausted = 3,
    Revoked = 4, RateLimited = 5, StampFailed = 6, Busy = 7
}
```

Indexes: `(GrantId, AtUtc DESC)`, `(AtUtc)` for the admin list, `(GrantId, IpPrefix)` for
the distinct-IP `EXISTS`.

**Unknown tokens are not logged to this table.** DL-4 says every *download* is logged, and
a row needs a `GrantId`. Writing a row per unrecognised token would hand an enumerating
attacker an unbounded-growth primitive against a shared-hosting disk. Unknown tokens
increment a counter (`nexus.download.unknown_token`) and are handled by the rate limiter.

### 1.3 `Book` — new columns, and a tightened publish gate

Added to the Phase 1 entity (PLAN §4 already reserved them):

| Column | Type | Notes |
| --- | --- | --- |
| `PdfStorageKey` | `nvarchar(256)?` | Relative to the library root, e.g. `12/9f3c….pdf`. **Server-derived, never client-supplied** |
| `PdfSha256` | `char(64)?` | Lowercase hex. Also the filename and the cache key |
| `PdfSizeBytes` | `bigint?` | Satisfies CAT-33 ("the detail page shows the real file size") |
| `PdfPageCount` | `int?` | From the structural parse at upload; not the same as the marketing `Pages` |
| `PdfOriginalFileName` | `nvarchar(256)?` | Operator's filename, shown in the admin only |
| `PdfUploadedAt` | `datetimeoffset(3)?` | |
| `PdfUploadedByAdminUserId` | `nvarchar(450)?` | |

`Book.PublishBlocker()` gains one rule, at the end:

```
if (PdfStorageKey is null) return "PDF file is missing.";
```

This closes the hole where a buyer pays for a book with no file, and it closes it in the
one place the admin, the seeder and the bulk import all pass through — which is exactly
why Phase 1 put the gate on the entity. `tests/Nexus.UnitTests/PublishGateTests.cs` gains
the case.

Consequence: the dev/staging seeder currently publishes twenty books with no files.
`CatalogueSeeder` therefore generates a **one-page placeholder PDF** per seeded book on
first run (PDFsharp, in-process, ~2 KB each) into the library and fills the `Pdf*` fields.
Placeholders can never reach production because production seeds unpublished (D4/CAT-35),
and a real upload replaces them. One rule, enforced everywhere, is worth twenty tiny files.

### 1.4 `Setting` (new — brought forward from Phase 7)

DL-6 requires expiry and limits to be changeable without a deployment, so the table lands
here rather than with its admin screen. Three columns, read through a 60-second
`IMemoryCache` — the same window Phase 1 chose for the catalogue (D9).

| Key | Default | Used by |
| --- | --- | --- |
| `delivery.linkExpiryDays` | `7` | Grant issue (snapshot) |
| `delivery.maxDownloads` | `3` | Grant issue (snapshot) |
| `delivery.watermarkEnabled` | `true` | Stamping (DL-5) |
| `delivery.maxUploadBytes` | `67108864` (64 MB) | Upload validation |
| `delivery.distinctIpThreshold` | `4` | Abuse flag (DL-8) |
| `delivery.reissueLimitPerItem` | `5` | Re-issue |
| `delivery.stampCacheBudgetBytes` | `2147483648` (2 GB) | Cache eviction |

`Setting` is `Key nvarchar(128)` PK, `Value nvarchar(1024)`, `UpdatedAt`,
`UpdatedByAdminUserId?`. Values are plain text in this phase; Phase 7 adds encryption for
the Tap-key rows, which do not exist yet.

**Snapshotting matters.** Expiry and max-downloads are copied onto the grant at issue. An
operator who shortens the window from 7 days to 3 must not retroactively shrink an
allowance someone already paid for — and an operator who lengthens it must not silently
extend links already in the wild. Both behaviours surprise, and only one of them is safe.

### 1.5 Schema delivery

One migration, `Delivery`, all additive:

- creates `DownloadGrants`, `DownloadLogs`, `Settings`
- adds seven nullable `Pdf*` columns to `Books` — nullable, so **no backfill and no
  default is needed for existing rows**
- seeds the `Settings` defaults via `HasData` so a fresh and an upgraded database agree

Nothing in it requires the `app_offline` window beyond the ordinary script run: no drops,
no type changes, no data movement. The idempotent script stays correct because every
operation is additive and EF's `IF NOT EXISTS` wrappers cover it; CI's existing
`dotnet ef migrations script --idempotent` step needs no change.

The one ordering constraint is §0: generate it only on a branch that already has Phase 3's
`Orders`.

---

## 2. Application

### 2.1 Storage layout

```
App_Data/
├── dpkeys/                        DataProtection key ring (Phase 0, untouched here)
├── library/                       the product. SOURCE PDFs. Backed up.
│   └── {bookId}/{sha256}.pdf
├── incoming/                      upload quarantine. Disposable.
│   └── {guid}.part
├── stamped/                       per-grant watermarked copies. Disposable cache.
│   └── {gg}/{grantId}-{sha16}.pdf        gg = last two hex digits of grantId
└── seed-pdfs/                     non-Production only: operator-dropped PDFs the
                                   seeder imports so staging has real files
```

Root resolves to `Path.Combine(ContentRootPath, "App_Data")` unless
`Nexus:Library:Root` overrides it (a server with a separate data volume should).

**Naming.** `{bookId}/{sha256}.pdf` is content-addressed, which buys four things for free:
re-uploading identical bytes is a no-op; the filename *is* the integrity check; the path
contains no operator- or customer-supplied text, so path traversal is impossible by
construction rather than by escaping; and replacing a book's PDF writes a new file instead
of overwriting one that an in-flight response may be reading.

Two defences are still applied, because "impossible by construction" survives exactly one
careless refactor: the key is validated against `^\d+/[0-9a-f]{64}\.pdf$` before use, and
the resolved absolute path is asserted to sit under the library root after
`Path.GetFullPath`.

**The stamped cache is keyed on `grantId`, never on the token.** The token is a bearer
credential; writing it into a filename puts it in directory listings, backups, disk
forensics and any stack trace that prints a path. `grantId` leaks nothing.

**The stamped cache key includes the source SHA.** Without it, an operator who uploads a
corrected PDF would keep serving the old one to every buyer who had already downloaded
once — a stale-cache bug that nobody would notice for months. With it, a replaced source
is simply a cache miss.

**Replacing a PDF never deletes the old file.** A response may be mid-stream and grants
point at `BookId`, not at a SHA. Orphans (files no `Book` row references) are removed by
an explicit admin sweep, not automatically — see §4.8.

### 2.2 `IPdfLibrary` (Infrastructure)

| Method | Behaviour |
| --- | --- |
| `Task<StoredPdf> IngestAsync(Stream source, string originalName, long declaredLength, CancellationToken)` | Streams to `incoming/{guid}.part` while computing SHA-256 in the same pass, validates (§2.3), then `File.Move`s to `library/{bookId}/{sha}.pdf`. Deletes the quarantine file on any failure. Returns sha, size, page count |
| `Stream OpenRead(string storageKey)` | `FileStream` with `FileShare.Read`, 64 KB buffer, `Asynchronous \| SequentialScan`. Throws `PdfMissingException` if the key resolves to nothing — a published book whose file vanished is an operator incident, not a 404 |
| `bool Exists(string storageKey)` | For `/health` |
| `Task<long> SweepOrphansAsync(bool dryRun, CancellationToken)` | §4.8 |

`StoredPdf(string Sha256, string StorageKey, long SizeBytes, int PageCount)`.

### 2.3 Upload validation — by magic bytes, in this order

Every step below runs against **bytes already written to the quarantine file**, never
against a claim made by the client. Filename extension and `Content-Type` are checked
first only because they are free; neither is evidence.

1. **Size, before a byte is read.** `delivery.maxUploadBytes`. Enforced in three places
   or it is enforced nowhere (§6 risk R13): `IISServerOptions.MaxRequestBodySize`,
   `FormOptions.MultipartBodyLengthLimit`, and `maxAllowedContentLength` in `web.config`.
   Over-size ⇒ `413 pdf_too_large`.
2. **Extension is `.pdf`, content type is `application/pdf`.** Advisory only. ⇒ `400 pdf_not_pdf`.
3. **Streamed write + hash.** `MultipartReader` → quarantine file, `IncrementalHash`
   fed from the same buffer. The file is read once and never held in memory. A server with
   512 MB shared across six apps cannot afford `CopyToAsync(new MemoryStream())`, and that
   is the line most upload code actually contains.
4. **Header.** `%PDF-` (`25 50 44 46 2D`) must appear **within the first 1024 bytes**, and
   the two bytes after it must be `1.` or `2.`. Not "at offset 0": the PDF spec tolerates a
   preamble, and several real generators emit one. Absent ⇒ `400 pdf_bad_magic`.
5. **Trailer.** `%%EOF` must appear within the last 2048 bytes. This is what catches a
   **truncated upload**, which passes the magic-byte check perfectly and then fails to open
   on the buyer's phone three weeks later. ⇒ `400 pdf_truncated`.
6. **Structural parse.** `PdfReader.Open(path, PdfDocumentOpenMode.Import)`. Any throw ⇒
   `400 pdf_unparseable`. A password/owner-password document throws distinctly ⇒
   `400 pdf_encrypted`, refused outright: we cannot stamp it, and if we could, the buyer
   would still be asked for a password we do not have. Page count is taken here.
7. **Promote.** `File.Move(quarantine, library/{bookId}/{sha}.pdf)` — same volume, so it is
   atomic. If the destination already exists the bytes are identical by definition; delete
   the quarantine file and return the existing key.

**Explicitly not done:** scanning for `/JavaScript`, `/Launch`, `/OpenAction` or embedded
files. We never render these PDFs ourselves, they are served with
`Content-Disposition: attachment` and `nosniff`, and the operator uploads books they hold
rights to — not user-generated content. A heuristic scan here would reject legitimate
interactive PDFs and would not make anyone safer. If the catalogue ever accepts
third-party submissions, revisit.

### 2.4 `IDeliveryService` (Application)

```csharp
Task<int> IssueGrantsForOrderAsync(int orderId, CancellationToken ct);
Task<GrantStatusDto?> GetStatusAsync(string token, CancellationToken ct);
Task<DownloadDecision> BeginDownloadAsync(string token, ClientContext client, CancellationToken ct);
Task CompleteDownloadAsync(long logId, long bytesSent, bool completed, CancellationToken ct);
Task<int> RevokeGrantAsync(int grantId, RevocationReason reason, string? adminUserId, CancellationToken ct);
Task<int> RevokeOrderGrantsAsync(int orderId, RevocationReason reason, CancellationToken ct);
Task<ReIssueResult> ReIssueAsync(int grantId, string? adminUserId, CancellationToken ct);
Task<int> SweepExpiredAsync(CancellationToken ct);
```

`DownloadDecision` is a discriminated result: `Allowed(grantId, logId, storageKey,
fileName, lengthBytes, stampPlan)` | `Refused(GrantRefusal reason)` | `Busy` |
`NotFound`. The controller turns it into HTTP and does no business logic of its own —
the Phase 1 rule that `CatalogueController` has no filtering in it.

Validation and rules are in §4. `IssueGrantsForOrderAsync` is the only writer of new
grants; nothing else may create one.

### 2.5 `IPdfStamper` (Infrastructure)

```csharp
Task StampAsync(string sourcePath, string destinationPath, StampText text, CancellationToken ct);
```

`StampText(string OrderNumber, string BuyerEmail, string? BuyerNameAscii, DateOnly PaidOn,
string BookTitleEn, string BookTitleAr)`.

Behaviour, with PDFsharp 6.x (MIT — PdfPig reads but cannot write, and iText7 is AGPL,
which a public repository selling a commercial product cannot take):

- Opens the source in `Modify` mode, draws a footer on **every page**, saves to a
  temp file next to the destination, then `File.Move`s it into place. Writing straight to
  the destination means a crash or a pool recycle mid-stamp leaves a truncated PDF in the
  cache that is then served, forever, to that grant. This is the sharpest failure mode in
  the whole phase and it costs one `File.Move` to avoid.
- Footer: `Licensed to {email} · Order {orderNumber} · {yyyy-MM-dd}`, 7 pt, 35 % grey,
  centred in the bottom margin, drawn with `XGraphicsPdfPageOptions.Append`.
- The footer rectangle is computed from the page's **MediaBox with its `/Rotate` applied**,
  or a landscape or rotated page gets its stamp sideways in the gutter. A rotated-page
  fixture is a required test (§7).
- Document info is also set: `Title` = the book title, `Keywords` =
  `nexus-order:{orderNumber}` and `nexus-grant:{grantId}`. Metadata survives a crop, so a
  file whose footers were trimmed is still attributable.

**The stamp is Latin-script and Western-digit only, and that is deliberate.** PDFsharp
draws glyph runs; it does not do Arabic shaping or bidi. Arabic text drawn naively comes
out as disconnected, left-to-right letterforms — worse than absent. So:

- email, order number and date are ASCII by nature and always stamped;
- the buyer's **name** is stamped only when it is Latin-script (a Unicode-block test), and
  omitted otherwise. The email already identifies the buyer, which is what DL-5 is for.
- The document `Title` may be Arabic — PDF info strings are UTF-16 and the *reader* renders
  them, so no shaping is needed on our side.

**CLAUDE.md rule 4 (Arabic is a first-class edition) is not violated by this.** The stamp
is forensic metadata burned into a file, not interface copy. Every string the customer
*reads* — the landing page, the library, the error states, the email — exists in both
languages, and §3.4 lists them. Written down here so a reviewer does not have to guess.

**Memory and the 512 MB pool.** PDFsharp loads the whole document into managed memory;
a 50 MB source can mean 150–200 MB of heap. The app pool is 32-bit, 512 MB, and shared with
five other apps (PLAN §10). Therefore:

- stamping is serialised behind a process-wide `SemaphoreSlim(1, 1)`;
- a request that cannot acquire it within 30 s returns **503 + `Retry-After: 10`**
  (`DownloadDecision.Busy`) and **does not consume a download** — the increment happens
  before streaming but the stamp happens after, so a `Busy` result compensates the counter
  exactly as an abort does (§4.4);
- the result is cached per grant, so a 600-page book is stamped once per grant, not once
  per download;
- `delivery.maxUploadBytes` defaults to 64 MB but the launch catalogue should be held at
  **25 MB** until the dedicated pool question (PLAN §10) is settled.

A pool that OOMs does not just drop this request — it takes down the five other sites
sharing it. That is the real argument for the dedicated pool, and it belongs in this design
because this phase is what creates the pressure.

---

## 3. Contracts

Developers build only against this section.

### 3.1 Public — the download path

#### `GET /download/{token}`

Anonymous. The token **is** the credential: the emailed link has to work from a phone's
mail app with no session, which is the product (PRD §3, "under two minutes, without
signing up").

Token format: 43 characters of Base64Url (256 bits from `RandomNumberGenerator`).
Anything else is rejected before any database call.

| Status | When | Body |
| --- | --- | --- |
| `200` | Allowed | `application/pdf`, streamed |
| `404` | Malformed or unknown token | Bilingual HTML (browser) or `{"error":"not_found"}` (JSON `Accept`) |
| `410` | `expired` / `exhausted` / `revoked` | `{"error":"gone","reason":"expired\|exhausted\|revoked"}` |
| `429` | Rate-limited | `Retry-After` in seconds |
| `503` | Stamping queue timeout | `Retry-After: 10`. The download is **not** consumed |
| `500` | Source file missing from the library | Logged as an operator incident, alerted |

Response headers on `200`:

```
Content-Type: application/pdf
Content-Length: <exact>
Content-Disposition: attachment; filename="the-patient-portfolio-KW-10429.pdf";
                     filename*=UTF-8''%D8%A7%D9%84%D9%85%D8%AD%D9%81%D8%B8%D8%A9…
Cache-Control: no-store, private
Accept-Ranges: none
X-Content-Type-Options: nosniff
X-Robots-Tag: noindex, nofollow
```

ASCII `filename` is slug(TitleEn)-OrderNumber; RFC 5987 `filename*` is the title in the
buyer's language, so an Arabic buyer gets an Arabic filename and an old client still gets
something usable.

`HEAD /download/{token}` ⇒ **405**. ASP.NET Core maps HEAD onto GET handlers by default;
left alone, a HEAD from a link checker would consume a download.

**Range requests are not honoured** (`Accept-Ranges: none`). A download manager that splits
a file into eight ranges would otherwise be eight downloads against a three-use grant.
One request is one download, and that is the only definition that matches what DL-3 says.
The cost is no resumable downloads; at 2–50 MB that is acceptable, and the stamped cache
makes a retry cheap. Revisit if the catalogue ever carries files above ~100 MB.

#### `GET /api/downloads/{token}/status`

Anonymous, `no-store`, **does not increment anything**. Backs the landing page and the
library.

```json
{
  "token": "u7Qx…",
  "bookId": 3,
  "titleEn": "The Patient Portfolio",
  "titleAr": "المحفظة الصبورة",
  "orderNumber": "KW-10429",
  "used": 1,
  "max": 3,
  "left": 2,
  "expiresAt": "2026-10-27T09:14:00+00:00",
  "state": "active",
  "sizeBytes": 6488064,
  "downloadUrl": "/download/u7Qx…"
}
```

`404` for unknown or malformed. **The buyer's email and name are never in this response** —
the token may have been forwarded, and the person holding it is not necessarily the person
who paid.

#### `GET /api/orders/{orderNumber}/grants?tapId={chargeId}`

Anonymous, rate-limited (10/hour/IP). How `success.html` gets its links without an account.
Authorised by possession of the Tap charge id that the redirect carried, matched against
`Order.TapChargeId`; served only while the order is `Paid` **and within one hour of
`PaidAt`**. After that the emailed link is the route, which is what the email is for.

```json
{ "status": "paid", "orderNumber": "KW-10429",
  "grants": [ { "bookId": 3, "token": "u7Qx…" } ] }
```

`202 {"status":"pending"}` while the order is still `Pending` — **the browser redirect can
and does beat the webhook** (PLAN §6.1), so the success page must be able to wait. It polls
every 2 s for 60 s, then shows "your link is on its way by email" rather than an error.
`404` if the order number and charge id do not match. `409 {"status":"failed"}` for a
failed order.

#### `GET /download.html?t={token}` — the landing page (static, `wwwroot`)

**The emailed link points here, not at `/download/{token}`.** Corporate mail-security
scanners, Outlook SafeLinks and chat-app link previews fetch URLs found in email. A link
that streams the file on `GET` gets one of a buyer's three downloads consumed by a robot
before the human has read the message. The landing page is anonymous, counts nothing, shows
the book, the downloads remaining and the expiry, and puts the actual stream behind a click.

It carries `<meta name="referrer" content="no-referrer">`, because every storefront page
loads fonts from `fonts.googleapis.com` and the token is in the query string.

### 3.2 Admin

Phase 7 builds the screens; Phase 4 builds the endpoints DL-7 and DL-8 need, plus the
upload without which there is nothing to deliver.

**Identity does not exist until Phase 6/7.** These endpoints ship now behind a named policy
`AdminOnly`, registered as **deny-everything outside Development**:

```
Development  → allow (so the dev and the integration tests can drive it)
anything else→ RequireAssertion(_ => false)
```

Phase 7 replaces the policy body with `RequireRole("Admin","Staff")` and nothing else
changes. The endpoints are therefore testable today and are not an open door on staging.
Every mutating call writes an `AuditLog` row once Phase 7 adds the table; until then it
writes a structured log line with the same fields.

| Route | Verb | Auth | Request | Response | Errors |
| --- | --- | --- | --- | --- | --- |
| `/api/admin/books/{id}/pdf` | `POST` | AdminOnly | `multipart/form-data`, field `file` | `201 {sha256,sizeBytes,pageCount,uploadedAt}` | `400 pdf_not_pdf \| pdf_bad_magic \| pdf_truncated \| pdf_encrypted \| pdf_unparseable`, `413 pdf_too_large`, `404 book_not_found`, `507 disk_full` |
| `/api/admin/books/{id}/pdf` | `DELETE` | AdminOnly | — | `204` | `409 pdf_in_use` when any `Active` grant references the book; `409 book_published` when the book is published (unpublish first — the gate in §1.3 would otherwise be bypassed) |
| `/api/admin/grants` | `GET` | AdminOnly | `?orderNumber=&bookId=&state=&flagged=&from=&to=&take=100&skip=0` | `{total, items:[GrantRowDto]}` | |
| `/api/admin/grants/{id}` | `GET` | AdminOnly | — | `GrantDetailDto` incl. the last 50 log rows | `404` |
| `/api/admin/grants/{id}/revoke` | `POST` | AdminOnly | `{"reason":"AbuseSuspected","note":"…"}` | `200 {state:"revoked"}` | `404`, `409 already_revoked` |
| `/api/admin/grants/{id}/reissue` | `POST` | AdminOnly | — | `200 {grantId, landingUrl, expiresAt}` | `404`, `409 order_not_paid`, `409 reissue_limit` |
| `/api/admin/library/sweep` | `POST` | AdminOnly | `{"dryRun":true}` | `200 {orphanCount, bytes, files[]}` | |

`GrantRowDto`: `id, orderNumber, bookId, titleEn, titleAr, buyerEmail, used, max, state,
issuedAt, expiresAt, lastDownloadAt, lastIp, distinctIpCount, flagged`.
This is exactly what `admin/downloads.html` renders today from `DOWNLOADS` in
`admin-demo.js`, so Phase 7 swaps the source and keeps the markup — the Phase 1 move.

**Revoke is not reversible.** There is no un-revoke; operator error is corrected by
**re-issue**, which writes a new grant row (`Sequence + 1`, fresh token, fresh window,
`UsedCount = 0`) and leaves the revoked row as history. An audit trail you can edit is not
one.

### 3.3 Storefront — which page consumes what

| Page | Endpoint | Change |
| --- | --- | --- |
| `download.html` **(new)** | `GET /api/downloads/{token}/status`, then navigates to `downloadUrl` | New standalone page, reuses `chrome()`, `cover()`, `price()` and the i18n machinery from `site.js` unchanged |
| `library.html` | `GET /api/downloads/{token}/status` per held token | Replaces the fake `state.lib` counter arithmetic. `state.lib` becomes a list of `{orderNumber, bookId, token}` — opaque references only, the same discipline the cart follows (ids only) |
| `success.html` | `GET /api/orders/{orderNumber}/grants?tapId=`, polling | Stops inventing `KW-10429` and `you@email.com`; writes the returned tokens into `state.lib`; renders a download button per book |
| `index.html` | `GET /api/settings/public` (extend the Phase 1 endpoint) | `trust_t`, `as2_d`, `how4_d` stop hardcoding "seven days, three downloads" and template from the settings |

**The download must be a navigation, not a `fetch` into a Blob.** `location.href =
downloadUrl` (or an `<a download>`), so the browser's own download manager streams it to
disk. Fetching a 40 MB PDF into a Blob URL puts the whole file in the tab's memory —
acceptable for the KuwaitHR CV viewer, wrong here, and on a mid-range Android it is a crash.

### 3.4 i18n keys — EN and AR in the same change (CLAUDE.md rule 4)

Western digits in both languages (PRD §5). `tf()` templating, so nothing hardcodes 7 or 3.

| Key | EN | AR |
| --- | --- | --- |
| `dl_page_t` | Your download | التحميل الخاص بك |
| `dl_page_d` | This link is personal to your order. | هذا الرابط خاص بطلبك. |
| `dl_btn` | Download the PDF | تحميل الملف |
| `dl_left_n` | {n} downloads left | {n} مرات تحميل متبقية |
| `dl_valid_until` | Link valid until {d} | الرابط صالح حتى {d} |
| `dl_s_expired` | This link has expired. | انتهت صلاحية هذا الرابط. |
| `dl_s_exhausted` | This link has been used {n} times. | استُخدم هذا الرابط {n} مرات. |
| `dl_s_revoked` | This link is no longer active. | لم يعد هذا الرابط فعّالاً. |
| `dl_s_unknown` | We could not find that link. | تعذّر العثور على هذا الرابط. |
| `dl_busy` | The file is being prepared. Try again in a moment — this attempt was not counted. | يجري تجهيز الملف. حاول بعد لحظات — لم تُحتسب هذه المحاولة. |
| `dl_aborted` | The download did not finish, and it was not counted. | لم يكتمل التحميل، ولم يُحتسب. |
| `dl_reissue` | Email me a new link | أرسل لي رابطاً جديداً |
| `dl_watermark_note` | Every file carries your order number. | كل ملف يحمل رقم طلبك. |
| `ok_waiting` | Confirming your payment… | جارٍ تأكيد الدفع… |
| `ok_by_email` | Your link is on its way to {e}. | رابطك في طريقه إلى {e}. |

**Two existing strings are corrected in this phase because they are delivery strings:**

- `ok_expiry` currently reads `'الرابط صالح ٧ أيام · ٣ مرات تحميل'` — **Arabic-Indic
  digits, which PRD §5 forbids outright**, and hardcoded 7/3. Becomes
  `'الرابط صالح {d} أيام · {n} مرات تحميل'` / `'Link valid for {d} days · {n} downloads'`.
- `as2_d`, `trust_t`, `how4_d` likewise template their counts.

---

## 4. Integration points and behaviour

### 4.1 Grant issue — inside the webhook's transaction

Phase 3's webhook handler, at the point it flips the order to `Paid`, calls
`IssueGrantsForOrderAsync(orderId)` **in the same `SaveChangesAsync`**. Paid and entitled
commit together or not at all; there is no window in which a customer has been charged and
has no grants.

Idempotency is belt and braces: the webhook is already idempotent on
`(TapChargeId, Status)` (PLAN §6.3), and the unique index on `(OrderId, BookId, Sequence)`
turns any second attempt into a caught constraint violation rather than a duplicate
entitlement. Both, because this is the one place where "it ran twice" means someone gets
six downloads instead of three.

One grant per `OrderItem` (one book = one file = one grant). `MaxDownloads` and `ExpiresAt`
are read from `Setting` once and snapshotted onto each row.

The token: 32 bytes from `RandomNumberGenerator.GetBytes`, Base64Url-encoded to 43 chars.
It is returned to the caller **once**, in memory, for the email (Phase 5) and the success
page; only `SHA-256(token)` is persisted.

### 4.2 Refund revokes (PM-9, DL-7)

The refund webhook, in the transaction that sets `Status = Refunded`, calls
`RevokeOrderGrantsAsync(orderId, RevocationReason.Refunded)`. That sets every grant on the
order to `Revoked` and **deletes its stamped cache file** — housekeeping, not security
(the token is refused regardless), but a refunded customer's personalised PDF should not
sit on the disk.

### 4.3 Customer data deletion (PRD §10)

When a customer exercises deletion, their grants are revoked with
`RevocationReason.CustomerDataDeleted` and **their stamped cache files are deleted**.
Without that step the "deleted" customer's email address is still sitting inside cached
PDFs on the server. Order rows are retained for accounting, as the PRD requires, with the
buyer snapshot fields redacted.

### 4.4 The checks on every download, and the atomic increment

Order of operations, with the cheap rejections first so an enumeration attempt never
reaches the database:

1. **Rate limit** (.NET 8 built-in limiter): by token, 10 requests / 5 min; by IP,
   30 / 5 min on `/download/*`. Breach ⇒ `429`, logged with `Outcome = RateLimited` **only
   if the grant is known**.
2. **Shape check.** 43 Base64Url characters, or `404` with no database call.
3. **Lookup.** `SHA-256(token)` → single seek on the unique `TokenHash` index, projecting
   the grant with its order status, book key, title and buyer snapshot. Not found ⇒ `404`.
4. **Order must be `Paid`.** A refunded order's grants are already `Revoked`; this is the
   second lock on the same door.
5. **The authorising `UPDATE`** — state, expiry and count are checked *and* the counter is
   incremented in one statement:

```sql
UPDATE DownloadGrants
   SET UsedCount = UsedCount + 1,
       State     = CASE WHEN UsedCount + 1 >= MaxDownloads THEN 1 ELSE State END
 WHERE Id         = @id
   AND State      = 0            -- Active
   AND ExpiresAt  > @now
   AND UsedCount  < MaxDownloads;
```

   Rows affected `= 1` ⇒ proceed. `= 0` ⇒ refuse, then re-read the row to say *why*
   (expired / exhausted / revoked) so the customer gets an honest message.

**Why two concurrent requests on a 3-use grant cannot yield four downloads.** The statement
is a single `UPDATE`, so SQL Server takes an exclusive lock on that row for its duration and
holds it to the end of the (implicit) transaction. Under `READ COMMITTED`, the second
request's `UPDATE` **blocks on the row lock**, and when it is released it re-evaluates its
`WHERE` clause against the newly committed value — it reads `UsedCount = 3`, the predicate
`UsedCount < MaxDownloads` is false, and it affects zero rows. There is no read-then-write
window to lose, because there is no read. The guarantee comes from the engine's write
locking, not from application code getting an ordering right.

This is why the test in §7 fires eight parallel requests at a grant with `Used = 2` and
asserts **exactly one 200 and seven 410s** — and why it must run against real SQL Server.
An in-memory provider has no row locks; the test would pass for the wrong reason and the
bug would ship.

6. **Log** an `InProgress` row, keep its `Id`.
7. **Resolve the bytes.** Watermark off ⇒ stream the source directly, no cache entry.
   Watermark on ⇒ cache hit when `StampedKey` is set, the file exists **and**
   `StampedSourceSha == Book.PdfSha256`; otherwise acquire the stamping semaphore, stamp to
   a temp file, `File.Move` into the cache, update the three `Stamped*` columns.
8. **Stream** (§4.5), then `CompleteDownloadAsync`.

**Compensation when nothing was delivered.** The increment happens before streaming, which
is correct for safety but means a client that never received the file has still spent a
download. On a 512 MB pool that recycles, this will happen. So:

- a `Busy` (semaphore timeout) or a stamping failure, where **zero bytes** reached the
  client, always compensates;
- an aborted stream compensates only when `BytesSent < min(fileLength, 1 MB)` **and**
  `BytesSent < 10 %` of the file — enough to distinguish "it never started" from "it
  stopped at 99 % on purpose";
- compensation is itself guarded and bounded: `AbortForgivenCount` caps it at 3 per grant.

```sql
UPDATE DownloadGrants
   SET UsedCount = UsedCount - 1,
       AbortForgivenCount = AbortForgivenCount + 1,
       State = CASE WHEN State = 1 THEN 0 ELSE State END   -- Exhausted → Active
 WHERE Id = @id AND UsedCount > 0 AND AbortForgivenCount < 3 AND State IN (0, 1);
```

It can only ever hand back what this request took, so it cannot over-grant.

### 4.5 Streaming, not buffering

- `FileStream(path, Open, Read, FileShare.Read, 65536, FileOptions.Asynchronous | SequentialScan)`
  returned as a `FileStreamResult`; ASP.NET Core copies it in 64 KB chunks. Peak managed
  memory per concurrent download is tens of kilobytes, not the file size.
- `Content-Length` from `FileInfo.Length`, so the browser shows real progress and no proxy
  re-chunks the body.
- `IHttpResponseBodyFeature.DisableBuffering()` on the response, and `/download/*` is
  excluded from IIS dynamic compression — a PDF is already compressed, so gzipping it burns
  CPU on a shared pool to make the file slightly larger.
- Bytes are counted through a thin wrapping stream so `CompleteDownloadAsync` knows whether
  the response finished; `HttpContext.RequestAborted` distinguishes a disconnect from a
  server fault.
- **Nothing in this path calls `ReadAllBytes`, `MemoryStream` or `ToArray`.** The only place
  a whole PDF is ever in memory is the stamper, and that is serialised and capped (§2.5).

### 4.6 Abuse detection across distinct IPs (DL-8)

On each served download: `EXISTS(SELECT 1 FROM DownloadLogs WHERE GrantId=@g AND
IpPrefix=@p)`. New prefix ⇒ `DistinctIpCount + 1`; crossing
`delivery.distinctIpThreshold` (default 4) sets `FlaggedAt`. One indexed `EXISTS` on a path
that runs a handful of times per order is free.

**Distinctness is counted on a prefix, not an address.** IPv4 ⇒ `/32`, IPv6 ⇒ `/64`. An
IPv6 client with privacy extensions rotates its address within its own `/64` between
requests and would otherwise flag itself on the second download.

**Flagging never blocks.** It sets a field, surfaces on the admin Downloads screen and adds
a line to the daily digest; revocation is a human decision. Kuwait's mobile networks NAT
heavily — one household can present several addresses, and many households can present one
— so an automatic block would refuse honest buyers on a phone far more often than it would
stop a sharer. Watermarking is the deterrent (PRD §8: "a social deterrent, not a technical
one"); the IP count is a tip-off for the operator.

**Client IP behind IIS.** `HttpContext.Connection.RemoteIpAddress` is the reverse proxy
unless `ForwardedHeaders` is configured. It must be configured **with explicit
`KnownProxies`/`KnownNetworks`** — trusting `X-Forwarded-For` from anyone lets a client
forge a different address on every request, which both evades the per-IP rate limit and
poisons the flag. Whether SmarterASP's ANCM path delivers the real client address is an
open question for devops (§8), to be confirmed on staging before go-live.

**Retention.** `IpAddress` is personal data. A nightly job nulls it after 180 days;
`IpPrefix` is kept so historical distinct counts remain meaningful.

### 4.7 Expiry sweep and the stamped-cache budget

One `IHostedService` (joining Phase 3's reconciliation job rather than adding a scheduler):

- `Active` grants past `ExpiresAt` ⇒ `State = Expired`. Cosmetic, for the admin filter;
  the download path never trusts it.
- Delete stamped files for grants that are `Expired`, `Exhausted` or `Revoked` and whose
  `ExpiresAt` is more than 7 days past — a re-issue would re-stamp anyway.
- If `App_Data/stamped` exceeds `delivery.stampCacheBudgetBytes`, evict least-recently-used
  files until it does not. The cache is always reconstructible, so eviction is safe.
- Clear `App_Data/incoming/*.part` older than 1 hour (abandoned uploads).
- Null `DownloadLogs.IpAddress` older than 180 days.

**A full disk must fail the download with `503`, not write a truncated file.** Before
stamping, check free space ≥ 2 × source size; if not, evict, and if still not, serve the
**unstamped** source rather than refusing — the buyer gets their book, the operator gets an
alert. Delivering the product beats delivering the watermark.

### 4.8 Orphan sweep

A file in `library/` that no `Book` row references is an orphan (a replaced PDF, or an
upload that half-failed). Removal is an explicit admin action with a `dryRun` default, not
an automatic job. Automatically deleting files from the directory that holds the entire
product is not a thing a nightly timer should be allowed to do unattended.

### 4.9 `/health`

Extended with: library root exists and is writable; free space above a floor; the stamping
semaphore is not permanently saturated. A store that cannot deliver is down, even if it can
still take money — arguably *especially* then.

### 4.10 Email (Phase 5) and Tap (Phase 3)

Email is Phase 5; this phase defines what it sends: a link to
`https://{host}/download.html?t={token}` per book, the expiry date and the download count,
in the buyer's language, with both strings templated from the grant's snapshot values
rather than from the live settings. The `tap-payments` agent owns the webhook detail; the
only delivery-side contract is §4.1 and §4.2 — grants issue inside the paid transaction,
and revoke inside the refunded one.

---

## 5. File ownership

Disjoint. The two developers meet at §3.

### `backend-dev` owns

```
src/Nexus.Domain/Entities/DownloadGrant.cs                      new
src/Nexus.Domain/Entities/DownloadLog.cs                        new
src/Nexus.Domain/Entities/Setting.cs                            new
src/Nexus.Domain/Delivery/GrantState.cs                         new  (+ RevocationReason, DownloadOutcome)
src/Nexus.Application/Delivery/IDeliveryService.cs              new
src/Nexus.Application/Delivery/DeliveryDtos.cs                  new
src/Nexus.Application/Delivery/IPdfLibrary.cs                   new
src/Nexus.Application/Delivery/IPdfStamper.cs                   new
src/Nexus.Application/Settings/ISettingsReader.cs               new
src/Nexus.Infrastructure/Delivery/DeliveryService.cs            new
src/Nexus.Infrastructure/Delivery/PdfLibrary.cs                 new
src/Nexus.Infrastructure/Delivery/PdfMagicBytes.cs              new
src/Nexus.Infrastructure/Delivery/PdfSharpStamper.cs            new
src/Nexus.Infrastructure/Delivery/EmbeddedFontResolver.cs       new
src/Nexus.Infrastructure/Delivery/DeliveryMaintenanceService.cs new
src/Nexus.Infrastructure/Settings/SettingsReader.cs             new
src/Nexus.Infrastructure/Persistence/Migrations/*_Delivery.*    new
src/Nexus.Web/Controllers/DownloadController.cs                 new
src/Nexus.Web/Controllers/AdminDeliveryController.cs            new
src/Nexus.Web/web.config                                        new (maxAllowedContentLength)
tests/Nexus.UnitTests/*                                         new
tests/Nexus.IntegrationTests/Delivery*.cs                       new
```

### `storefront-dev` owns

```
src/Nexus.Web/wwwroot/download.html                 new
src/Nexus.Web/wwwroot/assets/js/delivery.js         new
src/Nexus.Web/wwwroot/library.html                  rewrite the render + handlers
src/Nexus.Web/wwwroot/success.html                  rewrite the render + polling
src/Nexus.Web/wwwroot/index.html                    template the three hardcoded-count strings
src/Nexus.Web/wwwroot/assets/js/i18n.js             the §3.4 keys, EN and AR together
src/Nexus.Web/wwwroot/assets/css/site.css           download-page styles only
```

`admin/downloads.html` and `pg-dls.js` are **not** touched in this phase — the admin screens
are Phase 7 and stay on demo data. The endpoints they will need exist (§3.2).

### Shared files — the collision list

Both of these are already touched by Phase 3, so Phase 4 rebases rather than merges blind:

| File | Change | Owner |
| --- | --- | --- |
| `src/Nexus.Infrastructure/Persistence/NexusDbContext.cs` | three `DbSet`s and their `OnModelCreating` blocks | backend-dev |
| `src/Nexus.Infrastructure/DependencyInjection.cs` | register library, stamper, delivery service, settings reader, maintenance hosted service | backend-dev |
| `src/Nexus.Web/Program.cs` | rate-limiter policies, `ForwardedHeaders`, `AdminOnly` policy, form/body size limits | backend-dev |
| `src/Nexus.Web/Nexus.Web.csproj` | `App_Data` exclusions (§6.1) | backend-dev |
| `src/Nexus.Infrastructure/Nexus.Infrastructure.csproj` | PDFsharp, embedded font | backend-dev |
| `src/Nexus.Domain/Entities/Book.cs` | `Pdf*` columns and the publish-gate rule | backend-dev |
| `src/Nexus.Infrastructure/Persistence/CatalogueSeeder.cs` | placeholder PDFs, `seed-pdfs` import | backend-dev |
| `tests/Nexus.UnitTests/PublishGateTests.cs` | the "no PDF" case | backend-dev |
| `.github/workflows/ci.yml` | the App_Data assertion (§6.1) | backend-dev |
| `src/Nexus.Web/wwwroot/assets/js/i18n.js` | §3.4 keys | **storefront-dev only** — backend-dev supplies the strings in §3.4 so there is no round trip |

---

## 6. `App_Data` — out of the deploy, out of CI, and backed up

CLAUDE.md rule 3 in operational form. Five links in the chain; all five must hold.

### 6.1 The chain

1. **Git.** `.gitignore` already carries `src/Nexus.Web/App_Data/`,
   `**/App_Data/library/`, `**/App_Data/dpkeys/`. Add `**/App_Data/stamped/`,
   `**/App_Data/incoming/`, `**/App_Data/seed-pdfs/`. Verify with
   `git check-ignore -v src/Nexus.Web/App_Data/library/1/aa.pdf` — an assertion, not a hope.
2. **The project file.** Add to `Nexus.Web.csproj` so the answer does not depend on which
   globs the Web SDK happens to apply:

   ```xml
   <DefaultItemExcludes>$(DefaultItemExcludes);App_Data\**</DefaultItemExcludes>
   <ItemGroup>
     <Content Remove="App_Data\**" />
     <None    Remove="App_Data\**" />
   </ItemGroup>
   ```
3. **CI.** The current step is `rm -rf publish/App_Data`, which silently hides the day
   someone adds it back. Replace it with an **assertion that fails the build**:

   ```
   test ! -e publish/App_Data || { echo "App_Data in publish output"; exit 1; }
   ! find publish -name '*.pdf' -print -quit | grep -q . || { echo "PDF in publish output"; exit 1; }
   ```

   Same two lines in `deploy-staging.yml`. A removal tells you nothing; a failure tells you
   the configuration drifted.
4. **Deploy.** `FTP-Deploy-Action` already excludes `**/App_Data/**`. Two rules go in the
   runbook: **`dangerous-clean-slate` is never enabled**, and the exclusion is verified on
   staging — create a sentinel file in `App_Data/library` before a release and confirm it
   survives.
5. **Web reachability.** ASP.NET Core only serves `wwwroot`, and IIS request filtering
   lists `App_Data` among its default hidden segments, so nothing routes there today. Both
   are defaults someone can change, so an integration test asserts
   `GET /App_Data/library/1/<sha>.pdf` ⇒ 404, and `UseStaticFiles` is never given a second
   `FileProvider`.

### 6.2 The part that is usually missed

**The nightly backup must include `App_Data/library`.** The PDFs are not in SQL Server. A
backup plan that covers the database restores a catalogue of rows pointing at files that no
longer exist — every book's metadata intact, every book unreadable. The library is
append-mostly and small (150 titles × ~10 MB ≈ 1.5 GB), so a weekly full plus a daily
incremental off-site copy is enough. `App_Data/dpkeys` goes with it. `App_Data/stamped`,
`App_Data/incoming` and `App_Data/seed-pdfs` are **disposable** — document that they can be
deleted at any time and the app rebuilds them, so nobody treats the cache as precious or the
library as expendable.

---

## 7. Tests

The first one is the one that would otherwise ship broken.

### Integration (real SQL Server — the CI service container, never a fake provider)

| Test | Assertion |
| --- | --- |
| **Concurrency on the counter** | Grant `Max=3`, `Used=2`. Fire 8 parallel `GET /download/{token}`. **Exactly one `200`, seven `410`, `UsedCount == 3`.** Repeat 20× to shake out ordering flukes. An in-memory provider has no row locks and would pass this for the wrong reason — it must run on SQL Server |
| Walk to exhaustion | Three `200`s, then `410 exhausted`, and `State == Exhausted` |
| Expiry | Back-date `ExpiresAt` by a minute ⇒ `410 expired`, **without waiting for the sweep** |
| Revoke | `410 revoked`; stamped cache file is gone |
| Refund | Refund webhook ⇒ every grant on the order `Revoked` |
| Unknown / malformed token | `404`, and no `DownloadLogs` row written |
| `HEAD` | `405`, `UsedCount` unchanged |
| Status endpoint | `UsedCount` unchanged after ten calls |
| App_Data unreachable | `GET /App_Data/library/...` ⇒ `404` |
| Arabic filename | `Content-Disposition` carries a valid RFC 5987 `filename*` and an ASCII fallback; a client that understands neither still gets a `.pdf` |
| Stamp once per grant | Two downloads ⇒ one stamp (cache file mtime unchanged, stamper call count 1) |
| Replaced source invalidates cache | Upload a different PDF for the book ⇒ the next download re-stamps and delivers the new bytes |
| Aborted stream is forgiven | Cancel after 0 bytes ⇒ `UsedCount` back where it started, `Outcome = Aborted` |
| Abort forgiveness is bounded | Four aborts ⇒ the fourth is **not** forgiven |
| Busy | Hold the semaphore, request ⇒ `503`, `Retry-After`, `UsedCount` unchanged |
| Upload: renamed PNG | `400 pdf_bad_magic` |
| Upload: truncated PDF (valid header, no `%%EOF`) | `400 pdf_truncated` |
| Upload: password-protected PDF | `400 pdf_encrypted` |
| Upload: over `maxUploadBytes` | `413`, quarantine file deleted |
| Upload: identical bytes twice | One file on disk, same SHA, `201` both times |
| Publish gate | A book with no PDF cannot be published; the message names the PDF |
| `DELETE` PDF while a grant is active | `409 pdf_in_use` |
| Grants issue once | Replay the paid webhook ⇒ still one grant per item |

### Unit

Token format and entropy (43 chars, Base64Url alphabet, 10 000 generated with no
collision); `SHA-256` lookup round-trip; magic-byte scanner against header-at-offset-0,
header-at-offset-600, header-absent, empty file, `%%EOF` at the very last byte, `%%EOF`
followed by 2 KB of junk; storage-key regex and path-traversal rejection
(`../`, absolute, UNC, `%2e%2e`); IP prefix normalisation (IPv4 `/32`, IPv6 `/64`,
IPv4-mapped IPv6); refusal-reason mapping; stamp-cache key changes with the source SHA;
`StampText` omits a non-Latin name and keeps the email.

### Stamping

A rotated-page fixture, a landscape fixture, a 600-page fixture (timing, and that it is
stamped once), a fixture with existing bottom-margin content (overlap is accepted, not a
crash), and a CI-only check that **the embedded font resolver is registered** — see R2.

### E2E (Playwright, both languages, RTL)

Browse → cart → pay with Tap test KNET → success page polls and shows the link → landing
page → download → the library shows 2 of 3 remaining. Then exhaust it and see the bilingual
`410` state.

---

## 8. Risks, decisions and open questions

### Decisions taken here

| # | Decision | Why, and what was rejected |
| --- | --- | --- |
| **ADR-1** | The download counter is enforced by a **single conditional `UPDATE`**, not by a read-then-write under a transaction, and not by optimistic concurrency | The statement's own row lock is the concurrency control, so there is no window to lose and nothing to hold open across a slow stream. `SELECT … UPDLOCK` + save works but keeps a transaction open next to I/O on a 512 MB pool; a `rowversion` retry loop can over-grant if written slightly wrong |
| **ADR-2** | Grants store **`SHA-256(token)`, never the plaintext** | A database copy then does not hand over the catalogue. A plain SHA-256 is correct here and a memory-hard KDF would be wrong: the secret has 256 bits of entropy, so there is nothing to brute-force, and the lookup must be one index seek. Consequence: an admin "re-send" cannot resend the same URL — it **rotates** the token, which also kills a forwarded old link. The email says so, in both languages |
| **ADR-3** | Token is **256-bit Base64Url**, not a GUID (PLAN §7 said GUID) | Same cost, no version/variant structure, 256 bits rather than 122, uniform alphabet, and it reads as a secret rather than as an identifier. The repository is public, so the format is known — entropy is the only thing protecting it, and this maximises it |
| **ADR-4** | The emailed link points at a **landing page**; only a click streams the file | Mail scanners, SafeLinks and chat previews fetch URLs in email. Without this, a robot spends one of three downloads before the buyer opens the message, and the support ticket is unexplainable |
| **ADR-5** | **Range requests are refused** (`Accept-Ranges: none`) | One request = one download is the only definition that matches DL-3. A download manager splitting into eight ranges would otherwise consume a 3-use grant on the first click. Cost: no resume. Revisit above ~100 MB files |
| **ADR-6** | The watermark is **Latin-script, Western-digit, ASCII-safe**; the buyer's name is omitted when it is not Latin-script | PDFsharp does no Arabic shaping or bidi; drawn Arabic would be disconnected letterforms, which is worse than nothing. The email identifies the buyer. The stamp is forensic metadata, not interface copy — every *readable* string ships bilingual |
| **ADR-7** | Increment **before** streaming, with bounded compensation for a stream that delivered nothing | Correctness first. Incrementing after success reopens the race; never compensating punishes honest buyers on a recycling pool. The compensation can only return what this request took |
| **ADR-8** | Abuse detection **flags, never blocks**, and counts `/64` prefixes | Kuwait's mobile networks NAT heavily in both directions. Auto-blocking would refuse honest phone buyers more often than it stopped a sharer |
| **ADR-9** | `Setting` arrives in Phase 4, not Phase 7 | DL-6 requires expiry and limits to change without a deployment, and this is the phase that uses them. Values are **snapshotted onto each grant** at issue, so a settings change never rewrites an allowance already sold |
| **ADR-10** | `PublishBlocker()` gains "PDF file is missing" | Otherwise a buyer can pay for a book with no file. The gate already lives on the entity precisely so admin, seeder and bulk import cannot drift |
| **ADR-11** | Admin endpoints ship behind a **deny-by-default `AdminOnly` policy** until Identity lands | They are needed now (there must be a way to get files in) and Identity is Phase 6/7. Deny-outside-Development means they are testable and are not an open door on staging |

Rejected and worth recording: putting the token in the URL **fragment** (`#t=`) so it never
reaches server logs — cleverer, and it hides the token from link-preview bots too, but the
stream URL carries the token in its path and *is* logged regardless, so the gain is partial
while the surprise is total. Query string `?t=`, consistent with `book.html?id=`, plus
Serilog scrubbing of `/download/*` paths and the `t` parameter.

### Risks

| # | Risk | Impact | Mitigation |
| --- | --- | --- | --- |
| **R1** | **PDFsharp stamping on a 32-bit, 512 MB pool shared with five apps** | An OOM takes down six sites, not one | Semaphore of 1; per-grant cache; 25 MB practical upload cap at launch; `503 + Retry-After` instead of an OOM. **The real fix is the dedicated pool in PLAN §10 — this phase is what makes that decision urgent** |
| **R2** | **CI has no system fonts.** PDFsharp 6 needs a `GlobalFontSettings.FontResolver`; without one, every stamping test passes on Windows and fails on `ubuntu-latest` | A red pipeline nobody can reproduce locally | Embed one redistributable TTF (OFL/public-domain — the repository is public, so the licence must permit redistribution) as an `EmbeddedResource` and register a resolver at startup. A CI-only test asserts the resolver is registered |
| **R3** | IIS `maxAllowedContentLength` defaults to ~30 MB and rejects a larger upload with a bare `404.13` **before ASP.NET sees it** | "The upload just fails" with nothing in the application log | Set it in `web.config` alongside `IISServerOptions.MaxRequestBodySize` and `FormOptions.MultipartBodyLengthLimit`; test at the boundary |
| **R4** | A crash mid-stamp leaves a truncated PDF in the cache, served forever to that grant | Silent, permanent corruption for one buyer | Stamp to a temp file, `File.Move` into place |
| **R5** | Replacing a book's PDF keeps serving the old one from cache | Errata never reach buyers | Source SHA in the cache key |
| **R6** | Backups cover SQL but not `App_Data/library` | **The catalogue is unrecoverable** | §6.2, and a restore drill before go-live |
| **R7** | A deploy mirrors over `App_Data` | Same | Five-link chain in §6.1, sentinel file verified on staging |
| **R8** | `X-Forwarded-For` trusted without `KnownProxies` | Rate limits evaded, IP flags poisoned | Configure `ForwardedHeaders` explicitly; confirm on staging what SmarterASP actually sends |
| **R9** | The stamped cache fills the disk | Downloads fail | LRU eviction to a budget; free-space check before stamping; **serve unstamped rather than refuse** when space runs out |
| **R10** | Tokens in server access logs | A log reader could replay a live link | Scrub `/download/*` paths and the `t` parameter in Serilog; tokens expire and are counted; rotate on re-issue |
| **R11** | The browser redirect beats the webhook, so `success.html` has no links yet | Buyer thinks the purchase failed seconds after paying | `202 pending` + 60 s polling + "it is on its way by email" |
| **R12** | A customer deletion leaves their email inside cached stamped PDFs | Data-protection gap | §4.3 purges the cache with the revoke |
| **R13** | `library.html` or `success.html` fetching the PDF into a Blob | Phone tab crash on a 40 MB file | Navigation, not `fetch` — stated in §3.3 |

### Open questions

1. **Does `Order` (Phase 2) carry `BuyerNameSnapshot` and `BuyerEmailSnapshot`?** If not,
   this phase adds them. The watermark must not read a mutable, deletable `Customer` row.
2. **Dedicated app pool, or the shared 512 MB `sphearsravi-001`?** Already open in PLAN §10;
   R1 makes it a delivery question, not just a hosting one. Recommendation unchanged:
   dedicated for production.
3. **What is the real maximum PDF size in the launch catalogue?** The 25 MB working cap is a
   guess until the operator supplies the first ten real files.
4. **Does SmarterASP's ANCM path deliver the real client IP**, and via which header? Blocks
   both rate limiting and DL-8 being meaningful. Confirm on staging.
5. **Where do `App_Data/library` backups go, who verifies them, and who has run a restore?**
   An unverified backup of the only copy of the product is not a backup.
6. **Which embedded font**, and is its licence redistributable in a public repository?
7. **Does the operator want the watermark on by default at launch?** `DL-5` says "when
   enabled"; the default here is `true`, and an author-facing promise probably requires it.
