# Phases 2–5 — consolidated implementation plan

Orders · Payments · Delivery · Email, reconciled into one buildable plan.

Written 2026-10-20. Supersedes the four independent designs
(`docs/sdlc/orders/02-design.md`, `payments/02-design.md`, `delivery/02-design.md`,
`email/02-design.md`) **wherever they disagree with this document or with each other**.
Everything they say that this document does not contradict still stands and is still the
detail to build from.

Why this exists: the four designs were written in parallel against Phase 1 as merged.
They agree on the product and disagree on about thirty concrete things — the order-number
format, which endpoint the success page calls, where the buyer's name lives, what `Setting`
looks like, whether a late capture is legal, whether the plaintext download token exists.
Built as written, they produce two order-creation endpoints, three success-page mechanisms,
two incompatible `Setting` tables and a payment path that refuses the one transition that
keeps a charged customer from getting nothing. This document picks one of each, says why in
a sentence, and folds in the fixes for the seventeen blockers the adversarial reviews raised.

Settled upstream and not reopened: Kuwait only, KWD only at three decimals, no FX, no VAT;
one bilingual PDF per book; no free books; guest checkout plus accounts plus admin on one
Identity store; Arabic is a first-class edition; Tap **test** keys through Phase 8; the
repository is public.

---

## 0. The six decisions that drive everything else

Read these first; most of the document follows from them.

| # | Decision | Replaces |
| --- | --- | --- |
| **R1** | **One public order entry point: `POST /api/checkout`.** It creates the order *and* the Tap charge in one request, calling `IOrderService.CreateAsync` internally. `POST /api/orders` is never routed. | Orders §3.1 (public `POST /api/orders`) + Payments §3.1. Two public ways to create an order is two places for a total to come from; PM-3 gets one home. |
| **R2** | **One order-state read: `GET /api/checkout/result`, authorised by the `nexus_order` HttpOnly DataProtection cookie.** No order number, charge id or token in any URL. `GET /checkout/return?tap_id=` sets the cookie server-side and `302`s to a clean `/success.html`. | Orders' `/api/orders/current`, Payments' `?r=<token>`, Delivery's `/api/orders/{n}/grants?tapId=`, Email's `/api/orders/{ref}/status`. Four endpoints, one page, one cookie. |
| **R3** | **The plaintext download token is kept, encrypted at rest** (`DownloadGrant.TokenProtected`, `IDataProtector`, key ring in `App_Data/dpkeys`), alongside `TokenHash` which stays the only lookup. | Delivery ADR-2 ("never stored"). Preserves ADR-2's actual property — a database copy is not download access, because the keys are on the filesystem and not in the DB backup set — while letting the email dispatcher and the success page produce a working link. |
| **R4** | **Emails are rendered at dispatch, never at enqueue.** `OutboxEmail` stores no body; the dispatcher re-materialises the model from `OrderId + Kind + Language` and re-validates the grants before sending. | Email §1.1 frozen `BodyHtml`/`BodyText`. Removes live tokens from the database and from every backup, and makes a retry eight days later either correct or honestly refused. |
| **R5** | **Grants and the outbox row are written inside the same transaction as `Status = Paid`. There are no post-commit domain events.** `OrderPaymentStateMachine` calls `IDeliveryService.IssueGrantsForOrderAsync` and `IOrderEmailService.QueueOrderConfirmationAsync` directly. | Payments §2.4's `OrderPaidEvent`/`OrderRefundedEvent` dispatcher. The events existed to keep Phase 3 shippable alone; these four phases now merge together, so the indirection buys nothing and loses durability. |
| **R6** | **`Order.TransitionBlocker` adjudicates; `OrderPaymentStateMachine` orchestrates.** The state machine runs the authorisation guards and the side effects; it never assigns `Status` itself. | Payments §2.4's parallel transition table. Two tables that already disagreed on three rows. |

---

## 1. One consolidated data model

Money is `decimal(18,3)`. Every column that can hold Arabic is `NVARCHAR`. Enum values are
explicit and are never renumbered. Timestamps are `datetimeoffset` in UTC.

### 1.1 Existing entities, changed

#### `Book` — seven new columns and two new publish rules

| Column | Type | Source |
| --- | --- | --- |
| `PdfStorageKey` | `nvarchar(256)?` | Delivery §1.3. Relative, `{bookId}/{sha256}.pdf`, server-derived |
| `PdfSha256` | `char(64)?` | lowercase hex; also the filename and the stamp-cache key |
| `PdfSizeBytes` | `bigint?` | CAT-33 |
| `PdfPageCount` | `int?` | from the structural parse at upload |
| `PdfOriginalFileName` | `nvarchar(256)?` | admin display only |
| `PdfUploadedAt` | `datetimeoffset?` | |
| `PdfUploadedByAdminUserId` | `nvarchar(450)?` | |
| **`PdfIsPlaceholder`** | `bit` default `0` | **New here.** Set by the seeder's generated stub. See blocker B12 |

`PublishBlocker()` gains two rules at the end, in this order:

```
if (PdfStorageKey is null)   return "PDF file is missing.";
if (PdfIsPlaceholder)        return "PDF is a seed placeholder, not the real file.";
```

The second rule is the point. "Unpublished in production" is not the same as "fileless", and
a gate that reports green on the exact failure it was added to catch is worse than no gate.

#### `Order` — the reconciled column list

Three designs wrote this table and they do not match. This is the merged shape; where a name
differs from a source design, the source name is given.

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `int` identity | internal only; never in a URL or an email |
| `OrderNumber` | `nvarchar(20)` unique | `KW-{yyMMdd}-{6 Crockford base32}`, 16 chars. Orders §1.5 wins over Payments' `KW-#####` |
| `CustomerId` | `int` FK → `Customer`, `Restrict` | |
| **`BuyerNameSnapshot`** | `nvarchar(128)` | **Blocker B1.** Delivery §0 asked for it, Orders did not add it. Watermark, invoice and email read *this*, never `Customer` |
| **`BuyerEmailSnapshot`** | `nvarchar(256)` | same |
| `Status` | `int` → `OrderStatus` | §1.3 |
| `StatusDetail` | `nvarchar(200)?` | English, operator-facing, not customer-facing |
| **`ReviewReason`** | `nvarchar(200)?` | **New here.** Set when a paid order needs a human — a withdrawn title, a surplus capture. Blocker B-orders-7 |
| `Language` | `nvarchar(2)` | snapshot of the checkout interface language, normalised (§4, C-lang) |
| `Currency` | `nvarchar(3)` | always `"KWD"` in v1 |
| `SubtotalKwd` | `decimal(18,3)` | |
| `VatRate` | `decimal(5,4)` | always `0` |
| `VatAmount` | `decimal(18,3)` | always `0` |
| `TotalKwd` | `decimal(18,3)` | `Subtotal + VatAmount` |
| `FxRate` | `decimal(18,6)` | always `1`. **Direction, written down once:** units of `Currency` per 1 KWD |
| `TotalCharged` | `decimal(18,3)` | **the amount handed to Tap and re-checked against the webhook.** `TotalCharged = Currency.FromCode(Currency).Round(TotalKwd * FxRate)` |
| **`RefundedTotalKwd`** | `decimal(18,3)` default `0` | **Blocker B6.** Running total of confirmed refunds; drives `Refunded` vs `PartiallyRefunded` |
| `TapChargeId` | `nvarchar(64)?` unique filtered | snapshot of the winning attempt |
| `TapPaymentReference` | `nvarchar(64)?` | |
| `TapGatewayReference` | `nvarchar(64)?` | KNET's own reference — what a bank statement shows |
| `PaymentMethod` | `nvarchar(32)?` | |
| `PaymentAttemptCount` | `int` default `0` | drives `AttemptNumber` and the retry cap |
| `PaymentExpiresAt` | `datetimeoffset` | `CreatedAt + Tap:AbandonAfterHours` (24) |
| `LastPaymentFailureCode` | `nvarchar(32)?` | what the result page explains |
| `IdempotencyKey` | `nvarchar(64)?` unique filtered | §4, C-idem |
| `RowVersion` | `rowversion` | the state machine's concurrency token |
| `CreatedAt`, `UpdatedAt`, `PaidAt?` | `datetimeoffset` | |

Indexes: unique `OrderNumber`; unique filtered `TapChargeId`; unique filtered
`(IdempotencyKey)`; `(CustomerId, CreatedAt DESC)`; `(Status, CreatedAt)`;
`(Status, PaymentExpiresAt)` for the expiry sweep.

**CHECK constraints, in the migration** (blocker B-orders-4 — the v1 invariants had no
database guard at all):

```
CK_Order_Currency_V1      Currency = 'KWD'
CK_Order_Fx_V1            FxRate = 1
CK_Order_Vat_V1           VatRate = 0 AND VatAmount = 0
CK_Order_TotalIdentity    TotalKwd = SubtotalKwd + VatAmount
CK_Order_Charged          TotalCharged = TotalKwd
CK_Order_TotalPositive    TotalKwd > 0
CK_Order_RefundBound      RefundedTotalKwd >= 0 AND RefundedTotalKwd <= TotalCharged
```

They cost nothing, they fail at the moment of the accident rather than two phases later, and
a UAE launch drops them in a migration — which forces the FX work to be a deliberate act
instead of a column quietly changing value.

#### `OrderItem`

`Id`, `OrderId` (FK Cascade), `BookId` (FK Restrict), `UnitPriceKwd decimal(18,3)`,
`TitleEnSnapshot`, `TitleArSnapshot` `nvarchar(256)`, `AuthorEnSnapshot`,
`AuthorArSnapshot` `nvarchar(128)`. Unique `(OrderId, BookId)`. No `Quantity` column (ST-5).

> **Naming conflict resolved.** Email §4.1 item 1 and §2.6 ask for a single
> `OrderItem.TitleSnapshot`. The four-column form wins — an Arabic invoice reprinted in 2028
> needs both — and the email renderer selects on `Order.Language`.

`CK_OrderItem_PricePositive  UnitPriceKwd > 0`.

### 1.2 New entities

| Entity | Owner phase | Shape |
| --- | --- | --- |
| `Customer` | Orders | `Id`, `Email`, `NormalisedEmail` (unique), `Name`, `Country char(2)` default `'KW'`, `PreferredLanguage nvarchar(2)`, `IdentityUserId nvarchar(450)?` indexed, `CreatedAt`, `UpdatedAt`. **No `PreferredCurrency`.** |
| `PaymentAttempt` | Payments | As Payments §1.1, with two changes: `IdempotentReference varchar(24)` (§4, C-ref) and **two poll timestamps**, `LastPolledAt` (reconciliation only) and `LastReturnPolledAt` (the return page), so a worried buyer refreshing the return page cannot starve their own order of reconciliation |
| `WebhookEvent` | Payments | As Payments §1.1. `RawBody nvarchar(max)?` is **full only when `HashValid = 1`**; a rejected delivery stores `RawBodySha256` plus the first 2 KB (blocker B-payments-9) |
| `Refund` | Payments | As Payments §1.1 |
| `OrderEvent` | Payments | As Payments §1.1 |
| `JobLease` | Payments | `Name nvarchar(64)` PK, `HolderId nvarchar(64)`, `AcquiredAt`, `HeldUntil`. Two rows: `payment-reconciliation`, `delivery-maintenance` |
| `DownloadGrant` | Delivery | As Delivery §1.1, **plus `TokenProtected varbinary(512)`** (R3) |
| `DownloadLog` | Delivery | As Delivery §1.2 |
| `Setting` | **shared — this plan owns the shape** | §1.4 |
| `OutboxEmail` | Email | As Email §1.1, **minus `BodyHtml`/`BodyText`** (R4), plus `MessageId nvarchar(256)` (stable, `{PublicRef}@{bounceDomain}`) and `Subject nvarchar(256)?` written at send for the admin list |
| `EmailSuppression` | Email | As Email §1.2, plus `LastClassification nvarchar(32)?` holding the enhanced status code that caused it |
| `AuditLog` | **shared — created here, used by all four** | `Id bigint`, `ActorId nvarchar(450)?`, `ActorEmail nvarchar(256)?`, `Entity nvarchar(64)`, `EntityId nvarchar(64)`, `Action nvarchar(64)`, `BeforeJson nvarchar(max)?`, `AfterJson nvarchar(max)?`, `At datetimeoffset`, `IpAddress nvarchar(45)?`. Index `(Entity, EntityId, At)` |

### 1.3 Enums — the merged set

```
OrderStatus          : Pending=0, Paid=1, Failed=2, Refunded=3, Cancelled=4,
                       PartiallyRefunded=5
PaymentAttemptStatus : Initiated=0, InProgress=1, Captured=2, Authorized=3, Declined=4,
                       Failed=5, Cancelled=6, Abandoned=7, Unknown=99
TapObjectType        : Charge=0, Refund=1, Other=99
RefundStatus         : Pending=0, Refunded=1, Failed=2
RefundOrigin         : Admin=0, TapDashboard=1
PaymentSource        : Webhook=0, Reconciliation=1, Admin=2, Checkout=3
WebhookOutcome       : Accepted=0, Duplicate=1, RejectedSignature=2, RejectedAmount=3,
                       RejectedCurrency=4, UnknownOrder=5, IllegalTransition=6,
                       Unparseable=7
GrantState           : Active=0, Exhausted=1, Expired=2, Revoked=3
RevocationReason     : None=0, Refunded=1, Chargeback=2, AbuseSuspected=3,
                       OperatorRequest=4, RightsWithdrawn=5, CustomerDataDeleted=6,
                       ReIssued=7
DownloadOutcome      : Served=0, Aborted=1, Expired=2, Exhausted=3, Revoked=4,
                       RateLimited=5, StampFailed=6, Busy=7
EmailKind            : OrderConfirmation=0, DownloadLinks=1, RefundNotice=2,
                       AccountConfirm=10, PasswordReset=11
EmailStatus          : Queued=0, Sending=1, Sent=2, Failed=3, Abandoned=4, Suppressed=5
SuppressionReason    : HardBounce=0, Complaint=1, RepeatedSoftBounce=2, ManualBlock=3
```

### 1.4 `Setting` — the shape conflict, arbitrated

Delivery §1.4 and Email §1.3 specify two incompatible tables, and Email's own rule
("first to merge creates it; the others add keys, never columns") would have frozen the
wrong one. One migration means one shape, decided here:

| Column | Type |
| --- | --- |
| `Key` | `nvarchar(128)` PK, dotted lowercase-first |
| `Value` | `nvarchar(max)` — Email's, not Delivery's `nvarchar(1024)`: the seeded EN order-confirmation body is already ~700 characters and the operator will add a paragraph |
| `IsSecret` | `bit` — Email's. Phase 7 puts the Tap keys here and needs somewhere to say so |
| `UpdatedAt` | `datetimeoffset` |
| `UpdatedBy` | `nvarchar(128)?` — Email's name, not Delivery's `UpdatedByAdminUserId` |

- **Seeded by a runtime idempotent seeder, not `HasData`.** `HasData` rewrites seed rows on
  the next migration that touches them, which would destroy an operator's edited email copy.
  A key that already exists is left alone.
- **Read through `ISettingsReader` with a 60-second `IMemoryCache`** (Delivery's choice),
  **invalidated on save** so a corrected template takes effect immediately.
- **`ISettingsReader` clamps and never throws** (blocker B-delivery-4): an unparseable or
  out-of-range value is logged at Error and replaced by the fallback.
  `maxDownloads` 1–20, `linkExpiryDays` 1–365, `distinctIpThreshold` 2–50,
  `maxUploadBytes` 1 MB–256 MB, `reissueLimitPerItem` 1–20,
  `stampCacheBudgetBytes` 100 MB–20 GB, `resend.*` 1–500.
- **`GET /api/settings/public` serves a hardcoded allow-list in code, never a projection**
  (blocker B11). For these phases, exactly: `delivery.linkExpiryDays`,
  `delivery.maxDownloads`. A unit test asserts the response key set equals that literal list,
  so adding a row to `Settings` can never widen the endpoint.

Keys owned by this plan: `delivery.*` (Delivery §1.4, all seven) and `email.*`
(Email §1.3, all sixteen). No SMTP credential is a `Setting`.

### 1.5 Disagreements found between the designs — all of them, named

| # | Thing | Orders says | Payments says | Delivery says | Email says | **Ruling** |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Order-creation endpoint | `POST /api/orders`, public | `POST /api/checkout` supersedes it | — | — | **`POST /api/checkout` only** (R1) |
| 2 | Success-page read | `/api/orders/current` + cookie | `/api/checkout/result?r=token` | `/api/orders/{n}/grants?tapId=` | `/api/orders/{ref}/status` | **`/api/checkout/result` + cookie** (R2) |
| 3 | Order number | `KW-{yyMMdd}-{6}`, 16 ch, CSPRNG | `KW-#####`, "short and sequential" | — | `KW-00042` | **Orders' format**; Payments' §3.3 premise is void |
| 4 | Tap reference charset | hyphens, uncited 20-ch ceiling | `KW-10429-1` | — | — | **No hyphens**: `KW2510217F3K2M1` (§4, C-ref) |
| 5 | Webhook amount re-check | `TotalCharged`, `Currency.FromCode` | `Currency.Kwd.Format(TotalKwd)` | — | — | **Orders'**. Payments' form breaks on the day the UAE column it exists for is used |
| 6 | `Failed → Paid` | legal, "the one whose absence loses money" | absent → `IllegalTransition` | — | — | **Legal** (B4) |
| 7 | Decline webhook | `Pending → Failed` | order stays `Pending` | — | — | **Stays `Pending`**; the buyer is one tap from retrying |
| 8 | Abandoned order | `Cancelled` at 30 min | `Failed` at 24 h | — | — | **`Failed` at 24 h**; `Cancelled` is the admin action only |
| 9 | Client total | no money field at all | optional `clientTotalKwd` | — | — | **Optional `clientTotalFils` (int)**, advisory only (§4, C-fils) |
| 10 | State-machine home | `Order.TransitionBlocker` | `OrderPaymentStateMachine` | — | — | **Both, split** (R6) |
| 11 | Buyer name/email | live `Customer` join | — | asks Phase 2 for snapshots | live `Customer` row | **Snapshot on `Order`** (B1) |
| 12 | Item title snapshot | `TitleEnSnapshot`/`TitleArSnapshot` | — | — | `TitleSnapshot` | **Orders' two columns** |
| 13 | Email language | `Order.Language` | — | — | `Customer.PreferredLanguage` first | **`Order.Language` first** (§4, C-lang) |
| 14 | `Setting` shape | — | — | `nvarchar(1024)`, `HasData`, no `IsSecret` | `nvarchar(max)`, `IsSecret`, runtime seed | **Email's columns, Delivery's cache** (§1.4) |
| 15 | Plaintext token | — | — | never stored | needs it for the email | **Encrypted at rest** (R3) |
| 16 | Grants/email trigger | — | post-commit domain events | in the paid transaction | in the paid transaction | **In the transaction, no events** (R5) |
| 17 | `success.html` owner | storefront, Phase 2 | storefront, Phase 3 | storefront, Phase 4 | storefront, Phase 5 "exclusively" | **One storefront pass, Stage E** (§4) |
| 18 | `i18n.js` owner | storefront alone | storefront alone | storefront alone | storefront, §3.7 keys only | **One commit in Stage 0**, all keys |
| 19 | VAT row deletion | "deleted in Phase 2" | "deleted in Phase 3" | — | — | **Stage E, once** |
| 20 | `NexusDbContext` | append a block per entity | one `ApplyConfiguration` line | three blocks | convert to `ApplyConfigurationsFromAssembly` | **Convert first, in Stage 0** |

---

## 2. One migration

**`20261021xxxxxx_OrdersPaymentsDeliveryEmail`**, generated once, in Stage 0, on a branch
that contains every entity and every configuration class. Parallel streams add no
migrations; a second migration in these phases is a merge defect, not a feature.

This removes Delivery §0's "migration ordering trap" and Email §5.3's "two phases generating
against different snapshots" entirely — there is one snapshot and one script.

Ordered list of what it creates:

1. **Tables, in FK order**
   1. `Customers`
   2. `Orders` (FK → `Customers`)
   3. `OrderItems` (FK → `Orders` cascade, → `Books` restrict)
   4. `PaymentAttempts` (FK → `Orders` restrict)
   5. `WebhookEvents` (FK → `Orders` null)
   6. `Refunds` (FK → `Orders`, → `PaymentAttempts`)
   7. `OrderEvents` (FK → `Orders`, → `WebhookEvents` null, → `PaymentAttempts` null)
   8. `JobLeases`
   9. `Settings`
   10. `DownloadGrants` (FK → `Orders` restrict, → `Books` restrict)
   11. `DownloadLogs` (FK → `DownloadGrants` cascade)
   12. `OutboxEmails` (FK → `Orders` restrict, → `Customers` set-null)
   13. `EmailSuppressions`
   14. `AuditLogs`
2. **Columns added to `Books`** — the eight `Pdf*` columns, all nullable except
   `PdfIsPlaceholder bit NOT NULL DEFAULT 0`. No backfill.
3. **Unique indexes** — `Customers.NormalisedEmail`; `Orders.OrderNumber`;
   `Orders.TapChargeId` filtered not-null; `Orders.IdempotencyKey` filtered not-null;
   `OrderItems (OrderId, BookId)`; `PaymentAttempts.TapChargeId` filtered not-null;
   `PaymentAttempts (OrderId, AttemptNumber)`; `PaymentAttempts.IdempotentReference`;
   **`WebhookEvents (TapObjectId, Status) WHERE HashValid = 1`** — expressed as
   `.HasFilter("[HashValid] = 1")` in the model so EF emits it into the idempotent script
   and the next scaffold does not drop it; `Refunds.TapRefundId` filtered not-null;
   `DownloadGrants.TokenHash`; `DownloadGrants (OrderId, BookId, Sequence)`;
   `OutboxEmails.IdempotencyKey`; `OutboxEmails.PublicRef`.
4. **Non-unique indexes** — `Orders (CustomerId, CreatedAt DESC)`, `(Status, CreatedAt)`,
   `(Status, PaymentExpiresAt)`; `PaymentAttempts (Status, CreatedAt)`, `(OrderId)`;
   `OrderEvents (OrderId, At)`; `DownloadGrants (OrderId)`, `(State, ExpiresAt)`,
   `(FlaggedAt) WHERE FlaggedAt IS NOT NULL`; `DownloadLogs (GrantId, AtUtc DESC)`,
   `(AtUtc)`, `(GrantId, IpPrefix)`; `OutboxEmails (Status, NextAttemptAt) WHERE Status IN (0,1)`,
   `(OrderId, CreatedAt DESC)`, `(ToEmail, CreatedAt DESC)`; `AuditLogs (Entity, EntityId, At)`.
5. **CHECK constraints** — the seven on `Orders` and the one on `OrderItems` in §1.1.
6. **No seed data in the migration.** `Settings` defaults are written by the runtime
   idempotent seeder (§1.4); no orders, customers, grants or emails are seeded in any
   environment, and the demo `ORDERS`/`CUSTOMERS`/`DOWNLOADS` arrays stay in `admin-demo.js`
   until Phase 7.

Properties to hold:

- **Purely additive.** No drop, no rename, no retype, no data motion. The CI
  `dotnet ef migrations script --idempotent` artefact stays correct, and nothing needs the
  `app_offline` window beyond the ordinary DLL swap.
- **Read the generated file for `decimal(18,2)` before committing.** EF's default scale is
  two. Every money property carries an explicit `HasColumnType("decimal(18,3)")`; one miss
  means `6.500` stores as `6.50` and every Tap signature fails, silently, with nothing
  visibly wrong. This is the highest-value thirty seconds in the whole build.
- **Apply before the new binaries serve traffic** (`deploy-staging.yml` already orders it
  that way), or a webhook arriving mid-deploy hits a handler whose tables do not exist. It
  will be retried, so this is a delay rather than a loss — say so in the runbook.

---

## 3. Build order and dependencies

### Stage 0 — the shared spine (sequential, one developer, ~1 day)

Nothing else starts until this merges. It is deliberately all of the contested surface:

1. `NexusDbContext.OnModelCreating` → `b.ApplyConfigurationsFromAssembly(...)`; `Category`
   and `Book` mappings move to `Persistence/Configurations/` unchanged. After this, no phase
   edits the context body again — each adds a file.
2. Every entity and enum in §1, shapes only. `Order.TransitionBlocker` / `TryTransitionTo`
   with the full §5-B4 table, because four streams call it.
3. Every `IEntityTypeConfiguration<T>`.
4. **The one migration** (§2).
5. Every Application-layer interface, with DTO shapes and XML docs but no implementation:
   `IOrderService`, `IPricingService`, `ICurrentCustomer`, `ITapClient`, `ITapSignature`,
   `IPaymentService`, `IRefundService`, `IOrderPaymentStateMachine`, `IDeliveryService`,
   `IPdfLibrary`, `IPdfStamper`, `ISettingsReader`, `IEmailOutbox`, `IEmailSender`,
   `IEmailTemplateRenderer`, `IOrderEmailService`, `IEmailSuppressionList`, `IAuditLog`.
6. `DependencyInjection.cs` gains exactly four lines — `AddNexusOrders`, `AddNexusPayments`,
   `AddNexusDelivery`, `AddNexusEmail` — each implemented in its own file in its own stream.
7. `Program.cs`, written **once**, complete: rate-limiter policies (configured, not just
   named — blocker B-orders-9), `ForwardedHeaders` with explicit `KnownProxies`, the
   `Referrer-Policy: no-referrer` and `X-Content-Type-Options` response headers, the
   webhook body-buffering middleware ahead of `UseRouting`, the `AdminOnly`/`AdminOrStaff`
   policies, form and body size limits, the `/health` response writer, and
   `.AddCheck<>` for `tap`, `library` and `outbox`.
8. `tests/Nexus.IntegrationTests/NexusApp.cs` — `Tap:BaseUrl` → `TapStub`, a fixed fake
   `sk_test_` key, `Email:Transport = None` plus `RecordingEmailSender`, the `AdminOnly`
   test auth handler. **Nothing may weaken the `NEXUS_TEST_DB` handling.**
9. `src/Nexus.Web/wwwroot/assets/js/i18n.js` — **all** new keys from all four designs, EN
   and AR, in one commit, plus the deletions (`vat`, `co_card`, `co_exp`, `co_cvv`,
   `co_holder`, `co_pay`, `co_country`, `co_secure`, `ok_body`) and the two Arabic-Indic
   fixes (`ok_expiry`, and the templated `trust_t`/`as2_d`/`how4_d`). One file, one commit,
   no rebase pain for anyone.
10. **The Tap hashstring fixture** — the literal `toBeHashed` string and expected hex, from
    Tap's documented sample or a captured signed sandbox delivery, pasted into
    `tests/Nexus.UnitTests/Payments/TapSignatureSampleTests.cs`. See open question Q1: this
    is the one Stage 0 item that depends on something outside the repository, and it blocks
    Stage B, not Stage 0.

### Stages A–D — parallel, after Stage 0

| Stage | Scope | Depends on | Can start |
| --- | --- | --- | --- |
| **A — Orders** | `PricingService`, `OrderNumber`, `OrderService.CreateAsync`, the customer upsert, `Money` | Stage 0 | immediately |
| **B — Payments** | `TapSignature`, `TapClient`, `PaymentService`, `OrderPaymentStateMachine`, `RefundService`, `PaymentReconciliationService`, `CheckoutController`, `TapWebhookController` | Stage 0 + the Q1 fixture. Calls A's `CreateAsync` and C's/D's interfaces — **the interfaces, not the implementations** | immediately, with A's `IOrderService` stubbed in unit tests |
| **C — Delivery** | `PdfLibrary`, `PdfSharpStamper`, `DeliveryService`, `DownloadController`, `AdminDeliveryController`, `DeliveryMaintenanceService`, `SettingsReader` | Stage 0 | immediately |
| **D — Email** | `EmailOutbox`, `OrderEmailService`, `EmailDispatcher`, the three senders, `EmailTemplateRenderer`, `EmailHtmlLayout`, `KuwaitDates`, `MoneyText`, admin email controllers | Stage 0; reads C's `DownloadGrant` rows and `TokenProtected` | immediately |

**Genuinely sequential, and nothing else is:**

- A before B's *integration* tests (B's unit tests stub `IOrderService`).
- C before D's *integration* tests (the renderer needs real grants).
- B's `OrderPaymentStateMachine` is the join point: it calls
  `IDeliveryService.IssueGrantsForOrderAsync` and
  `IOrderEmailService.QueueOrderConfirmationAsync` inside its transaction. Against the Stage 0
  interfaces this compiles on day one; the end-to-end test lands when A, B, C and D are all in.
- **Stage E — the storefront pass**, last, one developer, one sitting. Four designs each
  rewrite `checkout.html` and `success.html`; doing that four times is four merge conflicts
  and three wasted rewrites.

### Stage E — storefront (after A–D, ~1.5 days)

One pass over `cart.html`, `checkout.html`, `success.html`, `library.html`, `index.html`,
the new `download.html`, and the new `assets/js/checkout.js` + `delivery.js`. `i18n.js` is
already done (Stage 0). The contract it builds against is §3 of the four designs as amended
by R1, R2 and §4 below.

### Stage F — integration and hardening (~1 day)

The cross-stream tests that no single stage owns: webhook → Paid → grants → outbox row →
dispatcher → `.eml` with a working link; replay three times → one of everything; two
concurrent webhooks → one transition; refund → revoked grants → dead link; a Paid order with
no grants raises the reconciliation alarm.

---

## 4. File ownership

### 4.1 Disjoint — one owner, no coordination needed

| File or directory | Stage / owner |
| --- | --- |
| `src/Nexus.Domain/Money.cs`, `MoneyText.cs` | 0 |
| `src/Nexus.Domain/Entities/*.cs` (all new entities) | 0 |
| `src/Nexus.Domain/Payments/PaymentEnums.cs`, `Delivery/*.cs`, `Email/*.cs` (enums) | 0 |
| `src/Nexus.Infrastructure/Persistence/Configurations/**` | 0 |
| `src/Nexus.Infrastructure/Persistence/Migrations/*_OrdersPaymentsDeliveryEmail.*` | 0 |
| `src/Nexus.Application/Orders/**` | A (backend) |
| `src/Nexus.Infrastructure/Orders/**` | A |
| `src/Nexus.Application/Payments/**`, `src/Nexus.Infrastructure/Payments/**` | B |
| `src/Nexus.Web/Controllers/CheckoutController.cs`, `TapWebhookController.cs` | B |
| `src/Nexus.Application/Delivery/**`, `Settings/**` | C |
| `src/Nexus.Infrastructure/Delivery/**`, `Settings/**` | C |
| `src/Nexus.Web/Controllers/DownloadController.cs`, `AdminDeliveryController.cs` | C |
| `src/Nexus.Web/web.config` | C |
| `src/Nexus.Application/Email/**`, `src/Nexus.Infrastructure/Email/**` | D |
| `src/Nexus.Web/Controllers/Admin/AdminEmailController.cs`, `AdminSuppressionController.cs` | D |
| `src/Nexus.Web/Health/*.cs` | 0 creates the files; B, C, D fill their own check |
| `src/Nexus.Web/wwwroot/*.html`, `assets/js/checkout.js`, `delivery.js`, `orders.js`, `assets/css/site.css` | E (storefront) |
| `tests/Nexus.UnitTests/{Orders,Payments,Delivery,Email}/**` | A / B / C / D respectively |
| `tests/Nexus.IntegrationTests/{Orders,Payments,Delivery,Email}*.cs` | A / B / C / D |
| `tests/Nexus.IntegrationTests/TapStub.cs`, `RecordingEmailSender.cs`, `AdminTestAuthHandler.cs` | 0 |

`src/Nexus.Web/wwwroot/admin/**` and `pg-*.js` are **untouched** by all four phases. The
admin screens are Phase 7 and stay on `admin-demo.js`; the endpoints they will need exist.

### 4.2 Shared — the merge-conflict list, with the exact edit each stage needs

These are the files more than one stream must touch. Naming them is the point of this
document. **Every one of them is written completely in Stage 0 except where noted**, so that
A–E append rather than rewrite.

| File | Stage 0 writes | A adds | B adds | C adds | D adds | E adds |
| --- | --- | --- | --- | --- | --- | --- |
| `Persistence/NexusDbContext.cs` | conversion to `ApplyConfigurationsFromAssembly`; all 14 `DbSet`s | — | — | — | — | — |
| `Infrastructure/DependencyInjection.cs` | the four `AddNexus*` call lines | body of `AddNexusOrders` (own file) | body of `AddNexusPayments` (own file) | body of `AddNexusDelivery` (own file) | body of `AddNexusEmail` (own file) | — |
| `Nexus.Web/Program.cs` | **everything** (see Stage 0 item 7) | — | — | — | — | — |
| `Nexus.Web/appsettings.json` | the non-secret `Tap`, `Email`, `Site`, `Nexus:Library` sections and the `TapClient` log-level pin | — | — | — | — | — |
| `Nexus.Web/appsettings.Development.json` | `Tap:Enabled=false`, local return URL, `Email:Transport=FileDrop`, `Site:PublicBaseUrl` | — | — | — | — | — |
| `Nexus.Domain/Entities/Order.cs` | all columns + `TransitionBlocker`/`TryTransitionTo` | — | — | — | — | — |
| `Nexus.Domain/Entities/Book.cs` | `Pdf*` columns + the two publish rules | — | — | — | — | — |
| `Persistence/CatalogueSeeder.cs` | — | — | — | **placeholder-PDF generation, `!IsProduction()` gated** (B12) | — | — |
| `Nexus.Infrastructure.csproj` | PDFsharp 6.x, MailKit 4.8.0, MimeKit 4.8.0, `Microsoft.Extensions.Http` 8.0.1, `.Hosting.Abstractions` 8.0.1, `.Options.DataAnnotations` 8.0.0, the embedded font | — | — | — | — | — |
| `Directory.Build.props` | the version pins for the above | — | — | — | — | — |
| `Nexus.Web/Nexus.Web.csproj` | `App_Data\**` exclusions (Delivery §6.1 item 2) | — | — | — | — | — |
| `tests/.../NexusApp.cs` | **everything** (Stage 0 item 8) | — | — | — | — | — |
| `tests/Nexus.UnitTests/PublishGateTests.cs` | the two new cases | — | — | — | — | — |
| `wwwroot/assets/js/i18n.js` | **all keys, all four phases, both languages, plus the deletions** | — | — | — | — | — |
| `wwwroot/assets/js/site.js` | — | — | — | — | — | `cartTotal()` in integer fils, unresolvable-id guard, delete the fake `state.lib` writes and the `state.cur === 'AED'` VAT line, clear the cart only on `state === "paid"` |
| `.gitignore` | `**/App_Data/` (widened from the three named subfolders) | — | — | — | — | — |
| `.github/workflows/ci.yml` | the App_Data and `*.pdf` **assertions** (not `rm -rf`) | — | — | — | — | — |
| `.github/workflows/deploy-staging.yml` | `Tap__*`, `Email__Smtp__*`, `Site__PublicBaseUrl` env vars; the same two assertions | — | — | — | — | — |
| `README.md`, `PLAN.md` §6 | — | — | — | — | — | tech-writer, after merge |

**`i18n.js` and `site.js` have exactly one owner each and it is never `backend-dev`.** The
API returns `code`/`messageKey` and never prose; that is what makes the split hold.

---

## 5. Blockers to resolve before coding

Seventeen were raised. Each is folded into the plan above; the decision is restated here so
nothing is lost between documents.

### B1 — The buyer's identity is not snapshotted, and a guest row is somebody's record
*(orders)*

An unauthenticated `POST` matching an existing guest `Customer` by email was licensed to
overwrite `Name` and `PreferredLanguage`. Every money-bearing fact was snapshotted; the two
facts that name the human were a live join to a row anyone can rewrite — so a stranger could
retroactively change the name on a buyer's invoices and the name stamped into PDFs they
bought last month.

**Decision.** `Order.BuyerNameSnapshot` and `Order.BuyerEmailSnapshot` ship in the one
migration. The watermark (C), the invoice (Phase 6) and every email (D) read the snapshot and
never `Customer`. **No unauthenticated request mutates any `Customer` row** — a matched row
is reused as-is, a new row is created only when the normalised email is unseen, and the
response is identical in both cases (no account enumeration). `Customer.Name` is a mutable
convenience field that no order, invoice, watermark or email reads, and the XML doc on the
property says so.

### B2 — `CreateAsync`'s retry is wrong in both cases that fire it *(orders)*

"Retry the order number up to three times on a unique violation" re-ran `SaveChanges` on a
context whose `Customer` INSERT had already succeeded, turning a 1-in-10⁹ collision into a
500; and the idempotency check was a read-then-write with no stated behaviour on a write-time
conflict, so a genuine double-tap returned 500 `order_failed` while a `Pending` order existed.

**Decision.** On `DbUpdateException`, branch on the constraint name:

| Constraint | Behaviour |
| --- | --- |
| `UX_Order_IdempotencyKey` | re-read by `(IdempotencyKey, NormalisedEmail)` and return `200` with the existing order and its existing `paymentUrl` |
| `UX_Order_OrderNumber` | **roll back the whole transaction** and re-run the complete unit of work with a fresh number, up to 3 times; never `SaveChanges` again on a dirty context |
| `UX_Customer_NormalisedEmail` | roll back, re-read the customer, re-run |

The replay lookup is scoped to `(IdempotencyKey, NormalisedEmail)` so a guessed key can never
return another buyer's order. The storefront mints the key with `crypto.randomUUID()` — stated
in the contract, not left to taste. Test: two **concurrent** requests with the same key →
one `201`, one `200`, one row. The sequential test that was specified cannot catch this.

### B3 — Orders and Payments contradict each other in four load-bearing places *(orders)*

Resolved by §1.5 rows 1, 2, 5, 6 and 9, and by R1/R2/R6. Record the outcome as three ADRs
before coding: `0002-order-number-and-tap-reference.md` (§1.5 rows 3–4),
`0003-one-checkout-endpoint-one-result-cookie.md` (R1, R2),
`0004-late-capture-is-legal.md` (B4).

### B4 — No `Failed → Paid`, so a late capture takes money and delivers nothing *(payments)*

A customer pays at 23:50, the webhook is lost, the sweep fails the order at hour 24, the
retried webhook arrives at hour 25 with a valid signature and the right amount — and the
Payments transition table answers `IllegalTransition`, 200, no grant, no email, no refund,
with no admin surface until Phase 7.

**Decision.** The authoritative transition table, which lives on `Order.TransitionBlocker`
and nowhere else:

| From | To | Allowed | Note |
| --- | --- | --- | --- |
| *(created)* | `Pending` | yes | |
| `Pending` | `Paid` | yes | |
| `Pending` | `Failed` | yes | reconciliation past `PaymentExpiresAt` with nothing captured |
| `Pending` | `Cancelled` | yes | admin only (Phase 7) |
| **`Failed`** | **`Paid`** | **yes** | `StatusDetail = "Late capture after reconciliation"` |
| **`Cancelled`** | **`Paid`** | **yes** | same |
| `Paid` | `Paid` | **no-op, not an error** | webhook replay, PM-6 |
| `Paid` | `Refunded` | yes | cumulative refunds reach `TotalCharged` |
| `Paid` | `PartiallyRefunded` | yes | `0 < cumulative < TotalCharged` |
| `PartiallyRefunded` | `PartiallyRefunded` | yes | a second partial refund |
| `PartiallyRefunded` | `Refunded` | yes | the partials now sum to the total |
| `Refunded` | anything | no | a re-purchase is a new order |
| anything | `Pending` | no | |

**The prior state is never the guard.** The guard is a valid signature plus amount and
currency matching `TotalCharged` and `Currency`. A `Failed → Paid` transition issues grants
and enqueues the email exactly as `Pending → Paid` does, and raises an alert.
Reconciliation's nightly pass re-polls `Failed` orders younger than 7 days for the same reason.

### B5 — The double-charge guard does not cover the scenario it claims *(payments)*

The in-flight attempt guard sat *after* an unconditional order INSERT, so two taps produced
two orders, two attempts, two charges — and surplus detection could not see it because they
were different orders.

**Decision.** `POST /api/checkout` **requires** an `Idempotency-Key` header. Resolution order
inside one transaction: (1) look up `(IdempotencyKey, NormalisedEmail)`; on a hit, return
`200` with that order and its live `HostedPageUrl` — no new order, no new attempt, no new
charge; (2) only then create the order; (3) only then the in-flight attempt guard
(`Initiated`/`InProgress`, younger than `Tap:HostedPageTtlMinutes`, holding a URL → return
that URL). The header is mandatory because the alternative is an optional safety belt on the
one request that moves money; a missing header is `400 invalid_request`.

### B6 — `Paid → PartiallyRefunded` is unreachable and refunds are never summed *(payments)*

The amount-equality guard ran before any transition and rejected exactly the inputs the
partial transition requires; and two 6.500 refunds on a 13.000 order would each land
`PartiallyRefunded`, leaving the order never `Refunded` and the customer fully refunded with
working grants.

**Decision.** Refund snapshots are **exempt** from the equality guard and get their own:
`0 < amount <= TotalCharged - RefundedTotalKwd`, rejected as `RejectedAmount` otherwise.
`Order.RefundedTotalKwd` is incremented in the same transaction as the `Refund` row, and the
target state is chosen from the running total, not from the single amount:
`RefundedTotalKwd >= TotalCharged` → `Refunded` (revoke all grants);
otherwise → `PartiallyRefunded` (grants untouched, `ReviewReason` set, alert).

### B7 — Post-commit event dispatch is not durable *(payments)*

Resolved structurally by **R5**: grants and the outbox row are written inside the paid
transaction, so there is nothing to dispatch and nothing to lose. The residual risk — a
transaction that commits with neither — is covered by a new reconciliation invariant:

> A `Paid` order older than 10 minutes with zero `DownloadGrant` rows, or with no
> `OutboxEmail` row of kind `OrderConfirmation`, is **repaired** (issue / enqueue, both
> idempotent) and **alerted**. The counter `nexus.payments.paid_without_delivery` has a
> target of zero, like the webhook rejection rate.

### B8 — The grants endpoint rate limit is a quarter of its own polling budget *(delivery)*

10 requests/hour/IP against a page that polls 30 times in 60 seconds, on carrier-NATed
Kuwaiti mobile networks. Every purchase breaks at about the twenty-second mark.

**Decision.** The endpoint is gone (R2); `GET /api/checkout/result` replaces it and is keyed
on the **order id inside the `nexus_order` cookie**, not on the IP:
**120 per hour per order**, plus a loose `600 per hour per IP` as a flood stop only. The
arithmetic is stated in the contract and asserted in a test:
`polls_per_purchase (30) × retries (2) < limit (120)`.

### B9 — `success.html` leaks a bearer credential through `Referer` *(delivery)*

The page carried `orderNumber` and `tapId` in its query string, loaded
`fonts.googleapis.com` cross-origin, and had no `Referrer-Policy`; the grants response was
not `no-store`.

**Decision.** Three changes, all in Stage 0's `Program.cs` or Stage E:
(1) **no credential in any storefront URL** — R2 removes `?o=`, `?e=`, `?tapId=` and `?r=`
entirely; `/checkout/return` sets the cookie and `302`s to a bare `/success.html`;
(2) **`Referrer-Policy: no-referrer` as a site-wide response header**, not a per-page `<meta>`
the next new page will forget — this also covers `/download.html?t=`, which still has to
carry a token, and every `/download/*` error page;
(3) `Cache-Control: no-store, private` on `/api/checkout/result` and
`/api/downloads/{token}/status`, in the contract table next to the status codes.

### B10 — Revoking a grant does filesystem I/O inside a database transaction *(delivery)*

`RevokeOrderGrantsAsync` deleted the stamped cache file inside the transaction that sets
`Status = Refunded`. A buyer mid-download holds the file with `FileShare.Read`, `File.Delete`
throws, the transaction rolls back, the order is never `Refunded` and every grant stays
`Active` — the refunded customer keeps downloading and Tap retries into the same wall.

**Decision.** Split them. The state change (`State`, `RevokedAt`, `RevokedReason`) commits in
the transaction **and nothing else**. The cache purge is a separate best-effort step **after**
commit, in `try/catch`, and a failure is queued for the nightly maintenance pass, which
already deletes stamped files for revoked grants. Stamped files are opened
`FileShare.Read | FileShare.Delete` so a delete can win against an in-flight read.
Stated in one line in the design: *revocation is authoritative in the database; the cache
purge is housekeeping and must never be able to fail the revoke.*

### B11 — `/api/settings/public` is specified as a projection over a table that will hold the Tap secret key *(delivery)*

**Decision.** Hardcoded allow-list in code (§1.4), exactly `delivery.linkExpiryDays` and
`delivery.maxDownloads`, with a unit test asserting the response key set equals that literal
list. Adding a row to `Settings` can never widen the endpoint. `IsSecret` rows are
additionally refused by the reader used on the public path, as a second lock on the same door.

### B12 — Seeded placeholder PDFs make the publish gate report green on the failure it exists to catch *(delivery)*

"Placeholders can never reach production because production seeds unpublished" is not the
same as "production has no placeholder files". If the seeder ran, `PdfStorageKey` is set, the
gate passes, the operator publishes, and the buyer pays 6.500 KWD for one blank page with
their email stamped on it.

**Decision.** Three locks: (1) placeholder generation is gated on `!env.IsProduction()` **in
the seeder**, not on publish state; (2) `Book.PdfIsPlaceholder` is set on those rows and
`PublishBlocker()` refuses them by name — *"PDF is a seed placeholder, not the real file."*;
(3) an integration test asserts the production seed path writes zero files to
`App_Data/library`. A real upload clears the flag.

### B13 — The email cannot build a working download link *(email)*

The renderer was specified to read grants, and grants store `TokenHash` only.

**Decision.** **R3** — `DownloadGrant.TokenProtected` holds `IDataProtector.Protect(token)`,
purpose `"Nexus.Delivery.GrantToken.v1"`, key ring in `App_Data/dpkeys`. Lookup on download
is still a single seek on `TokenHash`; the protected copy is read in exactly two places, the
email dispatcher and `/api/checkout/result` within one hour of `PaidAt`, and never logged.
The emailed URL is `{Site:PublicBaseUrl}/download.html?t={token}` — the landing page, never
`/download/{token}`, so SafeLinks and mail scanners cannot spend a download; the renderer
unit test asserts the `/download.html?t=` prefix so it cannot drift back.
A resend therefore re-sends the *same* working link; re-issue stays an explicit action.

### B14 — Composing the email inside the payment transaction can roll back a captured payment *(email)*

**Decision.** Enqueue does the minimum possible inside the transaction: insert an
`OutboxEmail` row carrying `Kind`, `Language`, `ToEmail`, `ToName`, `OrderId`,
`IdempotencyKey`. **No `Setting` read, no template load, no rendering** — all of that moves
to the dispatcher (R4). The call is still wrapped in `try/catch`: on any exception, log at
Error, write a minimal `Status = Failed` row with `LastError`, and **never rethrow into the
payment transaction**. Test: a renderer configured to throw still leaves the order `Paid`
with working grants.

### B15 — Sender-policy rejections are classified as hard bounces, suppressing the whole customer base *(email)*

5.7.1 / 5.7.23 / 5.7.26 are returned at `RCPT TO` just like 5.1.1, and SPF/DKIM/DMARC are
still unconfigured. The first day of real sending with DNS not yet propagated would suppress
every customer who bought that day, permanently and silently, with `/health` green.

**Decision.** Classify on the **enhanced status code**, never on the command:

| Code | Treatment |
| --- | --- |
| 5.1.1, 5.1.2, 5.1.10 | recipient hard bounce → `Failed` + suppression candidate |
| **5.7.x** | **sender-policy failure → `Failed`, no suppression**, logged at Error with its own event id |
| other 5.x.x | message rejected → `Failed`, no suppression |
| 4.x.x, connect/DNS/TLS/timeout | transient → retry schedule |
| 535 | transient but loud |

Plus: **two failures on different calendar days** before an address is suppressed, and a
**circuit breaker** — 10 consecutive permanent failures across distinct recipients stops
suppression entirely, flips `/health` to `Unhealthy` and alerts, because the common cause is
us, not them.

### B16 — Every undelivered-but-terminal state is invisible *(email)*

Health measured only the age of the oldest `Queued` row; `Failed`, `Abandoned` and rows stuck
in `Sending` after a second lease expiry were counted by nothing and there is no admin screen
until Phase 7.

**Decision.** The `outbox` health check counts four things, not one:
oldest `Queued` age (Degraded at 5 min, Unhealthy at 15);
`Failed` + `Abandoned` created in the last 24 h (Degraded at ≥1, Unhealthy at ≥5);
any row with `Status = Sending AND LockedUntil < now` (Unhealthy);
transport misconfigured (Degraded). The nightly sweep reports the same four sets into the
digest. **The second lease expiry is defined**: `Sending` with `PossibleDuplicate = true` and
an expired lease → `Failed`, `LastError = "lease expired twice"`.

### B17 — The frozen body puts live download tokens in the database and in every backup *(email)*

**Decision.** **R4** — no body is stored at all. `OutboxEmail` has no `BodyHtml` or
`BodyText`; the dispatcher re-materialises the model from `OrderId + Kind + Language` at send
time and re-validates the grants first. Consequences, all good: nightly backups carry no
working links; a retry eight days later either renders fresh links or is honestly refused
with `409 no_live_grants`; the `Remaining` count in the email is never stale; and the
admin API has no body to accidentally return. The `.eml` file drop still contains links —
`App_Data/mail-drop` is swept at 7 days by the same nightly pass, and `.gitignore` is widened
to `**/App_Data/`.

### 5.1 The majors that change the plan's shape

Folded in without ceremony; listed so they are not rediscovered.

| Tag | Change |
| --- | --- |
| **C-ref** | `PaymentAttempt.IdempotentReference` is **alphanumeric uppercase, no hyphens**: `KW2510217F3K2M` + attempt number → `KW2510217F3K2M1`, 15 characters, `varchar(24)`. KNET's trackid is commonly alphanumeric-only; a stripped hyphen would make the bank statement stop matching the database. `OrderNumber` keeps its hyphens on screen and in email. The charset, not just the length, goes to `tap-payments` for sandbox confirmation (Q1) |
| **C-fils** | The request may carry `clientTotalFils` (`int`, e.g. `18389`), **never a decimal total**. It is compared to `(int)(total * 1000)` and used for one thing: `409 price_changed`. It is never stored and never charged. `cartTotal()` in `site.js` returns integer fils for the same reason — comparing a JS float accumulation to a server decimal produced a spurious mismatch on 28.7 % of multi-item carts |
| **C-lang** | `language` is normalised: `Trim()`, `ToLowerInvariant()`, two-letter prefix. `"AR"`, `"ar-KW"` and `"ar "` all resolve to `ar`; anything else falls back to `en` **and logs at Warning**. Email precedence is `Order.Language` → `Customer.PreferredLanguage` (order-less kinds only) → `en` |
| **C-idem** | `Idempotency-Key` is mandatory, `crypto.randomUUID()`, in `sessionStorage` until the order succeeds |
| **C-priced** | `PricedLine(int BookId, decimal UnitPriceKwd, LocalisedText En, LocalisedText Ar)` — reusing Phase 1's `LocalisedText` so a copy-in-entity-order swap of `TitleAr` and `AuthorEn` is a type error, not four permanently wrong orders. The test asserts each snapshot against its source field with four distinguishable fixture values |
| **C-sales** | `Book.SalesCount` moves by a set-based `ExecuteUpdate` (`SalesCount + 1`, and `CASE WHEN SalesCount > 0 THEN SalesCount - 1 ELSE 0 END` on refund) inside the same transaction. A tracked read-modify-write loses sales under concurrent captures and `Book` has no concurrency token |
| **C-ordnum-test** | The order-number test asserts 2,000 distinct draws from a seeded injected RNG **plus** a per-position frequency check over ~200,000 draws. "100,000 generations produce no duplicate" fails 99 runs in 100 by the design's own arithmetic, and the fix a developer reaches for is deleting the assertion |
| **C-withdrawn** | `MarkPaid` re-reads `Book.IsPublished` per item. Money has moved, so the order becomes `Paid` regardless. Grants issue for still-published items; a withdrawn title's grant is **withheld**, `Order.ReviewReason = "withdrawn-title:{bookId}"` is set, an alert is raised, and the confirmation email lists the delivered items plus one bilingual line (`ok_held_t` / `ok_held_d`) saying one title is being checked. The operator's three existing actions — re-issue, resend, refund — close it |
| **C-buffer** | `Request.EnableBuffering()` moves into middleware registered **ahead of `UseRouting()`** for the webhook path; the in-action call stays as belt and braces. Forward constraint for Phase 8, in writing: no middleware may read the body on `/api/payments/tap/webhook`. Test: register a body-reading middleware and assert the webhook still verifies |
| **C-502** | `502 gateway_error` splits. A Tap **rejection** (4xx with a decline body) → `gateway_declined` + `co_err_generic` ("Nothing was charged"). A **timeout or 5xx** → `gateway_pending` + a new key pair saying the payment is being confirmed and not to retry yet. We never assert "nothing was charged" on a path where the outcome is unknown |
| **C-unknown** | A webhook whose charge id matches no attempt falls back to `metadata.orderNumber` and adopts the charge id onto that order's open attempt. If neither resolves: `503` for a bounded number of retries (keeping Tap's retry schedule alive), then `200` + alert |
| **C-verbatim** | All six verbatim hash fields are read via `GetRawText()` with surrounding quotes stripped, so a bare JSON number and a quoted number both yield the original token. Missing or null optional string → `""`. Unit tests for `transaction.created` as a number and `reference.payment` absent |
| **C-lease** | `JobLease` gains `Tap:LeaseMinutes` (30, longer than the worst-case tick), conditional-`UPDATE` acquisition with a rowcount check, mid-tick renewal, and a wall-clock budget bounding the tick in addition to `SweepBatchSize` |
| **C-reject-flood** | A failed-signature delivery stores `RawBodySha256` + the first 2 KB, dedupes identical rejected bodies by hash, and is rate-limited **per IP on the failed-verification path only** — genuine Tap retries never reach it. `WebhookOutcome.Unparseable` is recorded before the `400`, with a `webhook.rejected{reason=unparseable}` alert. The 90-day retention sweep runs even when `Tap:Enabled = false` |
| **C-health** | `/health` returns a JSON body of check names and statuses, plus exactly one data field — `tap.mode` (`"test"` / `"live"`), which Phase 9's smoke test asserts on. Free space, filesystem paths and semaphore saturation move to `/api/admin/health` behind `AdminOnly`. `AdminOnly` in Development is `RequireAssertion(ctx => connection is loopback)`, not an unconditional allow — the integration tests run over loopback and the office-LAN and staging cases close |
| **C-upload** | The upload-time `PdfReader.Open` goes behind the **same** process-wide stamping semaphore (it is the same whole-document managed load), with its own timeout returning `503 + Retry-After`. The seeder generates one placeholder and copies it 19 times rather than invoking PDFsharp 20 times at startup. `delivery.maxUploadBytes` is the **operational** cap, enforced by aborting the streamed write the moment bytes-written exceeds it; the three static IIS/Kestrel limits are the absolute ceiling |
| **C-stamp-race** | A per-grant keyed async lock in front of the stamping semaphore, so three concurrent first downloads of one grant produce one stamp and three served responses. On `File.Move`, "destination already exists" is **success** — the bytes are determined by `grantId` + source SHA |
| **C-slug** | `slug()` is defined: lowercase `[a-z0-9]`, runs of anything else collapsed to `-`, trimmed, max 60 chars, falling back to `book-{id}`. An admin-supplied title containing CR/LF would otherwise 500 a valid grant after the download was counted. `StampedKey` gets the same regex and `Path.GetFullPath` assertion the library key already has |
| **C-logs** | Serilog scrubs on **route templates**, not a path glob: `/download/{token}`, `/api/downloads/{token}/status`, `download.html?t=`. `/api/downloads/*` is excluded from IIS logging too. The status endpoint gains a limit (30/5 min per token, 120/5 min per IP). `nexus.download.unknown_token` gets a threshold (>100 in 5 min pages the operator) and a stated owner, and the design says plainly that the token-keyed limiter is not an enumeration control |
| **C-ip** | If the resolved client address for a sampled request is loopback or a configured proxy, the per-IP limiter is **disabled and logged**, not applied to everyone — an unresolvable client IP must not turn into a global 30-requests-per-5-minutes cap that looks like an outage |
| **C-delete** | Customer data deletion also nulls `IpAddress` and `UserAgent` on every `DownloadLog` reachable from that customer's orders, and scrubs `OutboxEmail.ToEmail`/`ToName`. `IpPrefix` is kept for the 180-day distinct counts. `Order` rows and their buyer snapshots are redacted, not deleted |
| **C-take** | `GET /api/admin/grants` clamps `take` to 200 server-side. `?take=1000000` was an export of every customer email and last-known IP through a listing endpoint |
| **C-money-text** | `MoneyText.Format(decimal, lang)` in `Nexus.Domain` returns `KD 6.500` / `6.500 د.ك`. `Currency.Format` returns the bare `"6.500"` and always will — it is what Tap hashes. `price()` in `site.js` is its storefront twin, and a unit test pins the two together |
| **C-bidi** | Unicode isolates, not markup, wherever markup cannot reach: U+2068 FSI … U+2069 PDI around order numbers, amounts and URLs in the **subject** and the **text part**; `<span dir="ltr" style="unicode-bidi:isolate">` in the HTML part. The test is restated as "no Latin-script run in an Arabic subject or text part is unisolated" |
| **C-plural** | `{{link_expiry_days}} أيام` becomes `{{expiry_phrase}}`, and `{{max_downloads}} عمليات تحميل` becomes `{{downloads_phrase}}`. The application renders the whole quantity phrase with Arabic's four cases (1, 2, 3–10, 11+) and English's two. Unit-tested at 1, 2, 3, 7, 10, 11, 30 in both languages. The defaults (7, 3) read correctly, which is exactly why this would have passed review and broken the day an operator set expiry to 1 day |
| **C-recipient** | A per-recipient email limit (5/hour, 20/day) counted from normalised `OutboxEmail.ToEmail` across all orders and actors; the `to` override becomes `AdminOnly`, not `AdminOrStaff`; refused calls count toward the actor limit from `AuditLog`, so a refusal is not free |
| **C-msgid** | A stable `Message-Id` of `{PublicRef}@{bounceDomain}`, reused on every attempt, so Gmail and Exchange Online collapse the recovered-lease duplicate |
| **C-verp** | `Email:BounceDomain` **must** be the `From:` organisational domain or a subdomain, validated at startup with the same degrade-and-warn treatment as the other SMTP checks — DMARC aligns SPF on `MAIL FROM`, and the obvious misconfiguration quarantines every email |
| **C-claim** | The dispatcher claims from a derived table ordered by `(NextAttemptAt, Id)` with `WITH (READPAST, ROWLOCK, UPDLOCK)` and `OUTPUT inserted.*`. `UPDATE TOP (10)` unordered delivers a post-outage backlog in random order while the health check's "oldest queued" ages past 15 minutes |
| **C-keys** | Four error codes had no `messageKey`: `too_many_attempts`, `unknown_order`, `order_not_payable`, `invalid_request`. All four get an EN/AR pair in Stage 0's `i18n.js`, and the contract carries an explicit code → key map so `backend-dev` invents nothing |
| **C-grants-tx** | Grant issue inside the paid transaction uses a **read** under the webhook's existing lock on the order row (`WHERE OrderId = @o AND Sequence = 0`) and returns early if grants exist. The unique index stays as a schema invariant, but a violation is handled by rolling back and re-running from the top — catch-and-continue on a poisoned transaction is not a safety net |
| **C-return-poll** | `DescribeReturnAsync` writes `LastReturnPolledAt`, never `LastPolledAt`, so a worried buyer refreshing the return page cannot exclude their own order from the reconciliation sweep |

### 5.2 Two things the reviews flagged that this merge dissolves

- **`pay_pending_d` promising an email Phase 3 cannot send.** Phases 3 and 5 now ship
  together, so the string is true on the day it ships.
- **`co_secure` / the VAT row / the card fieldset being deleted "in Phase 2" and again "in
  Phase 3".** One storefront pass (Stage E) deletes each once.

### 5.3 The missing artefact, acknowledged

There is no `01-requirements.md` for orders, payments, delivery or email. The pipeline in
`CLAUDE.md` puts the analyst before the architect, and the four designs cite PRD and
catalogue ids with nothing to trace against, so `qa-tester` has no coverage source. This
document is **not** a substitute. Before Stage A starts, `analyst` writes the four
requirements files to the `docs/sdlc/catalogue/01-requirements.md` standard; the id ranges
are reserved now so the designs can be back-referenced: `ORD-xx`, `PAY-xx`, `DEL-xx`,
`EML-xx`. It is half a day and it is the only way AC-5, PM-7, PM-9 and DL-7 get verified
rather than assumed.

---

## 6. Open questions for the product owner

Five, and only these. Everything else has a stated default that ships if nobody answers.

1. **Tap's documented hashstring sample, or one captured signed sandbox webhook.**
   *Blocks Stage B on day one.* The top risk in PLAN §14, in CLAUDE.md rule 1 and in the
   Payments design has exactly one mitigation — a test against Tap's own sample — and that
   artefact exists nowhere in the repository. Without it the test written instead asserts our
   implementation against itself, passes, and the error surfaces as 100 % webhook rejection
   against the sandbox. Needed: a sandbox merchant account with a per-environment webhook
   URL, and the literal `toBeHashed` string plus expected hex pasted into the fixture.
   *Secondary, same conversation:* does Tap still honour `reference.idempotent` for charge
   dedupe, does `lang_code` bring up the Arabic hosted page, does a refund notification hash
   over the refund's own `re_…` id, and **does KNET's trackid accept hyphens** (we have
   assumed not — see C-ref).

2. **The SMTP provider and the sending domain.**
   *Does not block the build; blocks go-live, with DNS lead times measured in days.* Phase 5
   ships with `Email:Transport = None` and visible `Suppressed` rows, which is the correct
   state until this is answered. SPF, DKIM and DMARC on the chosen domain are a precondition
   for Phase 9, and `Email:BounceDomain` must align with the `From:` domain (C-verp).

3. **Dedicated app pool, or the shared 512 MB 32-bit `sphearsravi-001`?**
   *Changes what gets built, not just where it runs.* PDFsharp loads a whole document into
   managed memory; stamping and upload parsing are both serialised behind one semaphore
   because of this, the practical upload cap is held at 25 MB rather than the configured
   64 MB, and an OOM takes down five other sites. If the answer is "shared", the 25 MB cap
   and the semaphore are permanent and the operator needs to know before supplying files.

4. **Is the watermark on by default at launch?**
   DL-5 says "when enabled"; the default here is `true`, and an author-facing promise
   probably requires it. If it is `false`, the stamping semaphore, the stamped cache, the
   cache budget and about a day of work are dead weight at launch — worth knowing now.

5. **Rights, per title, in writing, before publish.**
   Already open in PRD §13, but C-withdrawn makes it a build input: the behaviour when a
   title is unpublished between order and capture (Paid, grant withheld, order flagged,
   bilingual "we are checking one title" line) is a *product* decision about what a buyer is
   told, and the alternative — honour the sale and refund later — is equally defensible. The
   plan builds the first unless told otherwise.
