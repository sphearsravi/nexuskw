# Payments (Tap) — Phase 3 design

SDLC phase 2 (design) for **Phase 3 — Tap payments** in PLAN §5 and §6, PRD §7.
Written 2026-10-20 against Phase 1 as merged (`Nexus.Domain.Currency`,
`NexusDbContext`, `CatalogueService`, `CatalogueController`, the static storefront).

This builds on **PLAN §6**, which holds the verified hashstring formula and the
charge field list. Nothing here contradicts it. Where this design is more
specific than §6 — a five-minute sweep rather than only a nightly one, a
filtered uniqueness index, an attempt row per charge — the reason is stated.

**Phase boundary.** Phase 2 (cart, pricing, order creation) owns `Order`,
`OrderItem`, `Customer`, `OrderStatus`, `PricingService` and order numbering.
This design *extends* those and says exactly which lines it adds. Phase 4 owns
`DownloadGrant`; Phase 5 owns email; Phase 6/7 own Identity. Where this phase
needs something those phases own, it raises a **domain event with a no-op
handler** rather than reaching forward. That keeps the money path shippable on
its own and keeps the file split disjoint.

Settled and not reopened: Kuwait only, KWD only, three decimals, no VAT, no FX,
no AED in the UI; one bilingual PDF per book; no free books; guest checkout;
Tap **test** keys through Phase 8; the repository is public.

---

## 0. The one-paragraph version

The browser posts a cart to `POST /api/checkout`. The server recomputes the
total from the database, writes a `Pending` order and one `PaymentAttempt`,
calls `POST https://api.tap.company/v2/charges`, and returns Tap's hosted-page
URL for the browser to navigate to. The customer pays on Tap's page. Tap then
does two independent things: it posts a signed notification to
`POST /api/payments/tap/webhook`, which is **the only thing that changes order
state**, and it sends the browser back to `GET /checkout/return?tap_id=…`,
which re-reads the charge, writes nothing, and hands the storefront a
short-lived display token. A five-minute reconciliation sweep settles anything
whose webhook never arrived. Refunds come back through the same webhook
endpoint with the same signature scheme and revoke the order's download grants.

---

## 1. Domain

### 1.1 New entities

All money is `decimal(18,3)`. Every column that can hold Arabic is `NVARCHAR`
(the existing `NexusDbContext` comment — *"a VARCHAR column silently mangles
Arabic"* — applies to customer names captured at checkout and to Tap failure
messages, which Tap returns localised).

#### `PaymentAttempt`

One row per Tap charge. PLAN §4 gives `Order` a single `TapChargeId`; that is
kept as the *winning* charge, but it cannot be the whole record, because a
customer whose card is declined will try again and a second charge id would
overwrite the first — and then the late webhook for the first charge has
nothing to match against. The attempt row is where per-charge truth lives.

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `int` identity | |
| `OrderId` | `int` FK → Order, `Restrict` | |
| `AttemptNumber` | `int` | 1-based, per order |
| `TapChargeId` | `nvarchar(64)` null | null only between row insert and a successful `POST /charges` |
| `IdempotentReference` | `varchar(20)` | what we send as `reference.transaction` **and** `reference.idempotent`; `KW-10429-1` |
| `AmountKwd` | `decimal(18,3)` | what we asked Tap to charge |
| `CurrencyCode` | `char(3)` | always `KWD` in v1 |
| `Status` | `int` | `PaymentAttemptStatus` |
| `HostedPageUrl` | `nvarchar(1024)` null | `response.transaction.url` |
| `PaymentMethod` | `nvarchar(32)` null | `KNET`, `VISA`, `MASTERCARD`, `APPLE_PAY` |
| `TapPaymentReference` | `nvarchar(64)` null | `reference.payment` |
| `TapGatewayReference` | `nvarchar(64)` null | `reference.gateway` — **may be absent; stored as `null`, hashed as `""`** |
| `CardLast4` | `char(4)` null | display only; never a PAN |
| `CardBrand` | `nvarchar(32)` null | |
| `FailureCode` | `nvarchar(32)` null | Tap `response.code` |
| `FailureMessage` | `nvarchar(512)` null | **NVARCHAR** — Tap returns this localised |
| `CreatedAt` | `datetimeoffset` | |
| `LastPolledAt` | `datetimeoffset` null | set by reconciliation |
| `SettledAt` | `datetimeoffset` null | |

Indexes: unique `TapChargeId` (filtered `WHERE [TapChargeId] IS NOT NULL`);
unique `(OrderId, AttemptNumber)`; unique `IdempotentReference`;
`(Status, CreatedAt)` for the reconciliation sweep; `OrderId`.

#### `WebhookEvent`

PLAN §4 specifies `TapChargeId, Attempt, RawBody, HashValid, ProcessedAt` and a
unique index on (charge id, status) for idempotency. Kept, with two changes
that matter.

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `bigint` identity | |
| `TapObjectId` | `nvarchar(64)` | the body's `id` — `chg_…` for a charge, `re_…` for a refund. This is the value hashed as `x_id`, which is exactly the right idempotency key. PLAN calls the column `TapChargeId`; it is renamed because it legitimately holds a refund id too |
| `ObjectType` | `int` | `TapObjectType` |
| `ParentChargeId` | `nvarchar(64)` null | on a refund, the body's `charge.id` |
| `Status` | `nvarchar(32)` | verbatim from the body (`CAPTURED`, `DECLINED`, `REFUNDED`…) — not parsed into an enum here, because an unknown status must still be recorded |
| `OrderId` | `int` null FK | resolved after verification; null when we cannot match it |
| `HashValid` | `bit` | |
| `RawBody` | `nvarchar(max)` | exactly what was read off the wire |
| `RawBodySha256` | `char(64)` | hash of the raw bytes, so a test or a replay can prove byte-identity |
| `ReceivedAt` | `datetimeoffset` | |
| `ProcessedAt` | `datetimeoffset` null | |
| `Outcome` | `int` | `WebhookOutcome` |
| `RejectionReason` | `nvarchar(256)` null | |
| `RemoteIp` | `nvarchar(64)` null | |

**Change 1 — the uniqueness index is filtered.**

```sql
CREATE UNIQUE INDEX UX_WebhookEvent_Object_Status
  ON WebhookEvent (TapObjectId, Status) WHERE HashValid = 1;
```

The repository is public, so the webhook URL and the body shape are known. An
unfiltered unique index means anyone can POST a forged body carrying
`(chg_realid, CAPTURED)` with a wrong signature, occupy the idempotency slot,
and the genuine notification that follows is then swallowed as a "duplicate" —
a paid customer with no book, caused by an anonymous request. Filtering on
`HashValid = 1` means rejected deliveries are recorded in full but can never
pre-empt a real one. **This is the single most important line in the schema.**

**Change 2 — retention.** `RawBody` is cleared (the row and its hash stay)
after 90 days by the same hosted service that runs reconciliation. The body
holds a customer email and a masked card, so it is not kept forever.

#### `Refund`

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `int` identity | |
| `OrderId` | `int` FK | |
| `PaymentAttemptId` | `int` FK | the captured attempt being reversed |
| `TapRefundId` | `nvarchar(64)` null | `re_…`; null until Tap answers |
| `AmountKwd` | `decimal(18,3)` | |
| `CurrencyCode` | `char(3)` | |
| `Reason` | `nvarchar(256)` | |
| `Status` | `int` | `RefundStatus` |
| `Origin` | `int` | `RefundOrigin` — `Admin` or `TapDashboard` |
| `RequestedAt` / `ConfirmedAt` | `datetimeoffset` / null | |

Unique `TapRefundId` (filtered not-null). Index `OrderId`. A guard, not an
index: a new refund is refused while one is `Pending` or `Refunded` on the same
order.

#### `OrderEvent`

The payment audit trail PRD §10 ("payment state changes recorded and
queryable") asks for, and what the admin payment panel and the runbook read.

`Id bigint`, `OrderId int`, `At datetimeoffset`, `FromStatus int`,
`ToStatus int`, `Source int` (`PaymentSource`), `WebhookEventId bigint?`,
`PaymentAttemptId int?`, `Note nvarchar(512)?`. Index `(OrderId, At)`.

#### `JobLease`

`Name nvarchar(64)` PK, `HeldUntil datetimeoffset`, `HolderId nvarchar(64)`.
One row, `Name = 'payment-reconciliation'`. IIS overlapped recycle runs the old
and new worker process at the same time for a few seconds, so "there is only
one instance" is false exactly when a recycle happens — which is exactly when a
sweep is most likely to be mid-flight. The lease costs one table.

### 1.2 Enums — explicit numeric values

```
PaymentAttemptStatus : Initiated=0, InProgress=1, Captured=2, Authorized=3,
                       Declined=4, Failed=5, Cancelled=6, Abandoned=7,
                       Unknown=99
TapObjectType        : Charge=0, Refund=1, Other=99
RefundStatus         : Pending=0, Refunded=1, Failed=2
RefundOrigin         : Admin=0, TapDashboard=1
PaymentSource        : Webhook=0, Reconciliation=1, Admin=2, Checkout=3
WebhookOutcome       : Accepted=0, Duplicate=1, RejectedSignature=2,
                       RejectedAmount=3, RejectedCurrency=4, UnknownOrder=5,
                       IllegalTransition=6, Unparseable=7
```

`Unknown=99` exists because Tap may add a status. An unrecognised status is
recorded, answered `200`, and alerted — never mapped onto a guess.

### 1.3 Changes to Phase 2's entities (shared)

`OrderStatus` gains one member:

```
Pending=0, Paid=1, Failed=2, Refunded=3, Cancelled=4, PartiallyRefunded=5
```

`PartiallyRefunded` is reachable only from a refund raised in the Tap
dashboard, because admin refunds in v1 are full-order only (§6.2). Without the
state, a partial refund has nowhere to land and would either be silently
dropped or would wrongly revoke every grant.

`Order` gains:

| Column | Type | Why |
| --- | --- | --- |
| `PaymentAttemptCount` | `int` default 0 | drives `AttemptNumber` and the retry cap |
| `RowVersion` | `rowversion` | concurrency token; the state machine's guard |
| `PaymentExpiresAt` | `datetimeoffset` | `CreatedAt + Tap:AbandonAfterHours`; after this a `Pending` order is failed by reconciliation |
| `LastPaymentFailureCode` | `nvarchar(32)` null | what the return page explains to the customer |

`Order.TapChargeId`, `TapPaymentReference`, `TapGatewayReference`,
`PaymentMethod`, `PaidAt` stay as PLAN §4 defines them and are a **snapshot of
the winning attempt**, written in the same transaction as the transition to
`Paid`. They are a convenience for the admin list and for invoices; the
authoritative per-charge record is `PaymentAttempt`.

`Order.Currency` is `KWD`, `FxRate` is `1`, `VatRate` and `VatAmount` are `0`,
`TotalCharged == TotalKwd`. v1 writes those constants; the columns exist so a
UAE launch is configuration, not a migration (PRD §4).

### 1.4 Schema delivery

One EF Core migration, `Payments`, added to
`src/Nexus.Infrastructure/Persistence/Migrations`. It is **purely additive**:
five new tables, one new enum value (an `int`, no constraint), four new
columns on `Order`.

- **Defaults for existing rows.** Only staging has orders at this point.
  `PaymentAttemptCount` defaults to `0`; `PaymentExpiresAt` is backfilled in
  the migration to `CreatedAt + 24h` so the reconciler does not see a null;
  `RowVersion` is populated by SQL Server automatically when the column is
  added.
- **The CI idempotent script stays correct** because nothing is renamed,
  retyped or moved. The one thing to check in review: the filtered unique
  index is expressed as `.HasFilter("[HashValid] = 1")` in the model so EF
  emits it into the script. A filter written only as raw `migrationBuilder.Sql`
  would be absent from the model snapshot and silently dropped by the next
  scaffolded migration.
- **Nothing needs the `app_offline` window** beyond the ordinary DLL swap. No
  data motion, no table rewrite, no column drop.
- **Deploy order matters once, at Phase 9.** The webhook URL in Tap's merchant
  dashboard points at a host. Migrations must be applied *before* the new
  binaries serve traffic, as `deploy-staging.yml` already does (step 1 before
  step 3), or a webhook arriving mid-deploy hits a handler whose tables do not
  exist yet. It will be retried, so this is a delay, not a loss — but say so in
  the runbook.

---

## 2. Application

Namespace `Nexus.Application.Payments`, mirroring how `Nexus.Application.Catalogue`
is laid out: interfaces and DTOs in `Application`, implementations in
`Infrastructure`, registered in `DependencyInjection.AddNexusInfrastructure`.

### 2.1 `ITapSignature` — the thing that must be right

```csharp
bool Verify(string rawBody, string hashStringHeader, out TapNotification parsed, out string? reason);
string Compute(TapNotification n, string secretKey);   // exposed for tests
```

Implemented by `TapSignature` in Infrastructure. The formula is PLAN §6.3
verbatim:

```
toBeHashed = "x_id"                + id
           + "x_amount"            + amountFormattedAtCurrencyScale
           + "x_currency"          + currency
           + "x_gateway_reference" + (reference.gateway ?? "")
           + "x_payment_reference" + reference.payment
           + "x_status"            + status
           + "x_created"           + transaction.created

expected = lowercase-hex( HMAC_SHA256( UTF8(toBeHashed), UTF8(secretKey) ) )
```

Rules the implementation must follow, each of which has a test in §7:

1. **Six of the seven fields are used verbatim as strings, exactly as they
   appear in the body.** `id`, `currency`, `reference.gateway`,
   `reference.payment`, `status` and `transaction.created` are read with
   `JsonElement.GetString()` (or `GetRawText()` trimmed of quotes) and
   concatenated unchanged. In particular `transaction.created` is an epoch
   milliseconds **string**; parsing it to a `long` or a `DateTime` and
   re-rendering it changes the bytes and fails every hash.
2. **`amount` is the one field that is reformatted**, and it is reformatted at
   the scale of the currency named *in the body*:
   `Currency.FromCode(body.currency).Format(amountDecimal)`. KWD gives
   `6.500`. `Nexus.Domain.Currency.Format` already rounds away-from-zero and
   renders with `InvariantCulture` — this is the whole reason that type exists,
   and payments must not grow a second formatter.
3. **The amount is read as `decimal`, never `double`.**
   `JsonElement.GetDecimal()` on a number, or `decimal.Parse(s,
   NumberStyles.Number, CultureInfo.InvariantCulture)` on a string, because Tap
   may send either. Binding `6.500` through a `double` yields
   `6.4999999999999996` and the formatted result is a coin toss.
4. **An unknown currency code is a rejection, not an exception.**
   `Currency.FromCode` throws `ArgumentOutOfRangeException`; the verifier calls
   `Currency.TryFromCode` and returns `false` with
   `reason = "unsupported currency"`. An unhandled throw here becomes a 500,
   and a 500 makes Tap retry a message we will never accept.
5. **Absent `reference.gateway` is the empty string.** Not `"null"`, not the
   literal `null`, not the segment omitted. Tap omits it on some KNET
   responses, which is the common case in Kuwait.
6. **Fixed-time comparison.** Decode both hex strings to bytes and use
   `CryptographicOperations.FixedTimeEquals`. Comparing hex with
   `string.Equals` leaks timing and gets the case question wrong; comparing
   bytes makes case irrelevant.
7. **Two keys are tried**, current then previous (`Tap:SecretKey`,
   `Tap:PreviousSecretKey`), both in fixed time, so a key rotation at Phase 9
   does not drop the notifications already in Tap's retry queue. A match on the
   previous key is accepted and logged at Warning.

`TapNotification` is a flat record of exactly what the hash and the state
machine need: `Id`, `ObjectType`, `ParentChargeId?`, `Status`, `AmountRaw`
(decimal), `CurrencyCode`, `GatewayReference`, `PaymentReference`, `CreatedRaw`
(string), `MetadataOrderNumber?`, `PaymentMethod?`, `CardLast4?`, `CardBrand?`,
`ResponseCode?`, `ResponseMessage?`.

### 2.2 `ITapClient` — the outbound calls

```csharp
Task<TapChargeResult> CreateChargeAsync(TapChargeRequest r, CancellationToken ct);
Task<TapChargeResult> GetChargeAsync(string chargeId, CancellationToken ct);
Task<TapRefundResult> CreateRefundAsync(TapRefundRequest r, CancellationToken ct);
```

A typed `HttpClient` (`AddHttpClient<ITapClient, TapClient>`), base address from
`Tap:BaseUrl`, 20-second timeout, `Authorization: Bearer {secretKey}` set
per-request from `IOptionsMonitor<TapOptions>` so a key change needs a pool
restart and not a redeploy.

**Retry policy, stated precisely because it is a money decision.**

- `GET /charges/{id}` — retried up to 3 times on timeout or 5xx, exponential
  backoff. Safe: it is a read.
- `POST /refunds` — not retried automatically. A duplicate refund is a
  duplicate payout.
- `POST /charges` — **one** bounded retry, on timeout or 5xx only, and it must
  re-send the **identical** `reference.idempotent`. The attempt row with its
  `IdempotentReference` is written to the database *before* the HTTP call
  precisely so this is possible. Re-posting with a *new* key after a timeout is
  the double-charge bug; re-posting with the same key is what `idempotent` is
  for and returns the charge Tap already created. See the open question in §8
  — this must be confirmed in the Tap sandbox before it is relied on, and until
  it is, the retry is disabled by `Tap:RetryChargeOnTimeout` (default `false`)
  and a timed-out attempt is left for reconciliation to resolve.
- The secret key never reaches a log. `Microsoft.Extensions.Http`'s handler
  logs request headers at `Trace`; the Tap client's logging category is pinned
  to `Warning` in `appsettings.json` and a redacting `ILogger` scope masks any
  value matching `sk_(test|live)_\w+` to `sk_…_****`.

### 2.3 `IPaymentService` — what the storefront calls

```csharp
Task<CheckoutResult> StartCheckoutAsync(CheckoutRequest r, string languageCode, CancellationToken ct);
Task<CheckoutResult> RetryAsync(string orderNumber, CancellationToken ct);
Task<PaymentDisplayResult> DescribeReturnAsync(string tapChargeId, CancellationToken ct);
Task<PaymentDisplayResult?> ReadResultTokenAsync(string protectedToken, CancellationToken ct);
```

**`StartCheckoutAsync`** — validation and rules, in order:

1. `Items` is non-empty, ≤ 20 ids, **distinct** (PRD ST-5: a book is a file,
   not a quantity; duplicates are collapsed, not rejected, because a double
   click on "add" should not be an error).
2. Every id resolves to a **published** book. Any that does not →
   `409 item_unavailable` naming the ids. Nothing is written, no charge is
   created.
3. `Name` 2–128 chars; `Email` parses and is ≤ 254 chars; `Language` is `en`
   or `ar`. Country is not a field — Kuwait only.
4. **The total is recomputed from the `Book` rows** (PRD PM-3). The request has
   no price, no subtotal and no total field at all; the DTO does not define
   one, so there is nothing to tamper with and nothing for a future developer
   to accidentally trust.
5. `ClientTotalKwd` *is* accepted as an **optional** field for one purpose: if
   it is present and differs from the recomputed total, the response is
   `409 price_changed` carrying the new total and the per-item prices, and the
   storefront re-renders the summary and asks for a second confirmation. A
   silent re-price upward is a worse failure than an extra click.
6. `PricingService` (Phase 2) produces `SubtotalKwd` and `TotalKwd`, already
   rounded by `Currency.Kwd.Round`. No book can be free (CAT-34), so the total
   is `> 0` and a zero-amount charge is structurally impossible.
7. A `Customer` row is found or created by email, `IdentityUserId = null`.
8. A `Pending` `Order` is written with `PaymentExpiresAt = now + 24h`, and
   `OrderItem` rows snapshot `UnitPriceKwd` and the title in both languages.
9. **In-flight guard.** If the order already has an attempt in `Initiated` or
   `InProgress` created less than `Tap:HostedPageTtlMinutes` (default 30) ago
   and holding a `HostedPageUrl`, that URL is returned unchanged. A double
   click on "Pay" produces one charge.
10. Otherwise `AttemptNumber = ++PaymentAttemptCount` (capped at
    `Tap:MaxAttempts`, default 5 → `429 too_many_attempts`), the attempt row is
    written, and `ITapClient.CreateChargeAsync` is called.
11. The charge id, status and `transaction.url` are stored on the attempt.
    `CheckoutResult { orderNumber, paymentUrl, totalKwd, totalFormatted }`.

`StartCheckoutAsync` **never** marks anything paid, whatever Tap's synchronous
response says — even a charge that comes back `CAPTURED` immediately. One code
path writes `Paid`, and it is the state machine.

**`DescribeReturnAsync`** — called by `GET /checkout/return`. Calls
`GET /charges/{id}`, resolves the attempt by charge id, and returns a display
result. **It writes nothing to the order.** Its only write is the
`PaymentAttempt.LastPolledAt` timestamp and, if Tap reports a terminal failure,
`FailureCode`/`FailureMessage` on the attempt — never on the order, and never a
status transition. That is PLAN rule 2 and CLAUDE.md rule 2 made structural:
the method has no access to `IOrderPaymentStateMachine`.

### 2.4 `IOrderPaymentStateMachine` — the only writer

```csharp
Task<TransitionOutcome> ApplyAsync(PaymentSnapshot snapshot, PaymentSource source,
                                   long? webhookEventId, CancellationToken ct);
```

`PaymentSnapshot` is the normalised view of a charge or refund — the same shape
whether it came from a webhook body or from `GET /charges/{id}`. One state
machine, two feeds, no divergence.

**Authorisation checks before any transition** (a valid signature is not
authority to do anything in particular):

| Check | Failure |
| --- | --- |
| The charge id resolves to a `PaymentAttempt` of a known order | `UnknownOrder`, 200, alert |
| `snapshot.CurrencyCode == "KWD"` | `RejectedCurrency`, 200, alert, no transition |
| `Currency.Kwd.Format(snapshot.Amount) == Currency.Kwd.Format(order.TotalKwd)` | `RejectedAmount`, 200, alert, no transition |
| The transition is legal from the order's current status | `IllegalTransition`, 200, alert |

The amount comparison is made on the **formatted strings at the currency's
scale**, not on raw decimals, so `6.5000m` and `6.500m` compare equal and
`6.50m` does not.

**Transition table.**

| From | To | Trigger | Side effects, all in one transaction |
| --- | --- | --- | --- |
| `Pending` | `Paid` | charge `CAPTURED` | snapshot the winning attempt onto `Order` (`TapChargeId`, both references, `PaymentMethod`, `PaidAt`); attempt → `Captured`; `Book.SalesCount++` per item; `OrderEvent`; raise `OrderPaidEvent` |
| `Pending` | *(none)* | charge `DECLINED` / `FAILED` | attempt → `Declined`/`Failed` with code and message; **the order stays `Pending`**; `OrderEvent` |
| `Pending` | `Failed` | reconciliation, `now > PaymentExpiresAt`, no captured attempt | `OrderEvent`; nothing issued |
| `Pending` | `Cancelled` | admin (Phase 7) | |
| `Paid` | `Refunded` | refund webhook, `amount == TotalKwd` | `Refund` row → `Refunded`; raise `OrderRefundedEvent` (revokes grants); `OrderEvent` |
| `Paid` | `PartiallyRefunded` | refund webhook, `0 < amount < TotalKwd` | `Refund` row; **grants untouched**; `OrderEvent`; alert for the operator to decide |
| `Paid` | *(none)* | a **different** charge id captures | attempt → `Captured`, order unchanged, `OrderEvent` note `surplus-payment`, **alert** |
| any | *(none)* | the same charge id and status again | `Duplicate`, 200, nothing |

**A declined attempt does not fail the order.** A card that bounces is not a
dead order; the customer is one tap from trying again, and the return page
offers exactly that. Failing immediately would strand them with an order they
cannot pay and a cart they have already emptied. The order fails only when
`PaymentExpiresAt` passes with nothing captured.

**Surplus payment is called out explicitly** because it is the expensive
mistake. Two tabs, two attempts, both capture: the second must not issue a
second set of grants and must not be refunded automatically. It is flagged,
and a human decides. Silently ignoring it loses the money; auto-refunding it
is an unreviewed payout.

**Concurrency.** The transition is a single `SaveChangesAsync` inside a
`ReadCommitted` transaction, against an `Order` loaded with its `RowVersion`
concurrency token. A `DbUpdateConcurrencyException` means another feed won the
race; the outcome is `Duplicate` and the response is still `200`. Two
simultaneous deliveries therefore produce exactly one transition, one set of
grants and one email, with no distributed lock anywhere.

**Forward-phase effects are domain events, not calls.** `OrderPaidEvent` and
`OrderRefundedEvent` are dispatched after the transaction commits, to handlers
resolved from DI. Phase 3 ships a logging no-op handler for each. Phase 4
registers the grant issuer and revoker; Phase 5 registers the email enqueuer.
Phase 3 therefore merges and runs on its own, and neither later phase edits the
state machine.

### 2.5 `IRefundService`

```csharp
Task<RefundResult> RefundOrderAsync(int orderId, string reason, string adminUserId, CancellationToken ct);
```

v1 refunds the **full order total** — `AmountKwd = order.TotalKwd`, formatted
at the KWD scale. Partial refunds are not offered from our admin because an
amount cannot be mapped back to items reliably, and PRD PM-9 ("a refund revokes
download grants") would then be ambiguous about *which* grants. Guards: the
order is `Paid`; no refund is `Pending` or `Refunded`; the winning attempt has
a charge id. Writes a `Pending` `Refund`, calls `POST /v2/refunds`, stores the
`re_…` id. **The order is not flipped here** — the refund webhook does that,
for the same reason the charge webhook does, and the reconciler polls a
`Pending` refund the same way it polls a charge.

**The HTTP endpoint for this ships in Phase 7, not Phase 3.** Admin
authentication does not exist until Identity lands, and an unauthenticated
endpoint that moves money, in a public repository, is not something to ship
behind a flag and remember to remove. Until then the operator refunds from the
Tap dashboard, the refund webhook arrives with `Origin = TapDashboard`, and
everything downstream — state, grant revocation, audit — works identically. The
service, its tests and the webhook path are all built now so Phase 7 only adds
a controller.

### 2.6 `PaymentReconciliationService` — the net

A `BackgroundService` in Infrastructure, holding the `payment-reconciliation`
`JobLease` for the duration of each tick.

- **Every `Tap:SweepIntervalMinutes` (default 5).** PLAN §6.6 says nightly and
  PRD PM-8 says "pending over 30 minutes … nightly". Nightly satisfies the
  letter and fails the customer: a lost webhook means somebody who paid at
  09:00 waits until 02:00 for their book, and the support request arrives long
  before the job does. Five minutes is the same logic run more often; the
  nightly deep pass is kept as well.
- **Each tick:** take attempts in `Initiated`/`InProgress` whose `CreatedAt` is
  older than `Tap:ReconcileAfterMinutes` (default 30) and whose
  `LastPolledAt` is older than the interval, oldest first, at most
  `Tap:SweepBatchSize` (default 50). For each, `GET /charges/{id}` → build a
  `PaymentSnapshot` → `ApplyAsync(…, PaymentSource.Reconciliation, null)`.
  Exactly the same state machine, exactly the same guards.
- Attempts with **no charge id** (the `POST /charges` that timed out) are not
  pollable. They are left to expire with the order and are alerted on, because
  each one is a possible charge we do not know about. The count of these is the
  metric that decides the §8 open question about retrying on timeout.
- A `Pending` order past `PaymentExpiresAt` with no captured attempt →
  `Failed`.
- `Pending` refunds older than 30 minutes are polled the same way.
- **Nightly pass, 02:00 Kuwait:** re-poll everything touched in the last 7 days
  that is not terminal, and clear `WebhookEvent.RawBody` older than 90 days.
- **Every settlement that comes from reconciliation raises an alert**, because
  it means a webhook was lost. PRD §11 puts the webhook rejection target at
  zero; a webhook that never arrived is the same defect wearing a different
  hat, and without this alert it is invisible — the order completes and nobody
  learns that the notification path is broken.

### 2.7 Observability

`System.Diagnostics.Metrics` counters on the meter `Nexus.Payments` — no
package needed:

`webhook.received`, `webhook.duplicate`, `webhook.rejected` (tagged `reason`),
`webhook.processed`, `charge.created`, `charge.failed`, `order.settled`
(tagged `source=webhook|reconciliation`), `payment.surplus`,
`refund.requested`, `refund.confirmed`.

Alert conditions, routed to the daily error digest until Phase 5 gives us SMTP
(§8 open question on the channel): any `webhook.rejected{reason=signature}`;
any `payment.surplus`; `order.settled{source=reconciliation}` above 2% of
settlements in a day; any attempt stuck with no charge id.

`/health` gains a `tap` check reporting `{ mode: "test" | "live", reachable }`,
derived from the key prefix. It never prints the key. This is what the Phase 9
go-live smoke test looks at to confirm the live key is actually loaded.

---

## 3. Contracts

Developers build against this section alone. **Errors carry an i18n key, never
a sentence** — `{ "error": "price_changed", "messageKey": "co_err_price_changed" }`.
The backend therefore never writes a user-facing string in either language, and
`i18n.js` stays owned by one person (§5). That is how PRD §10's bilingual parity
is enforced structurally rather than by remembering.

### 3.1 `POST /api/checkout` — anonymous

Supersedes Phase 2's `POST /api/orders` as the storefront's entry point; see
§6.2 decision 12. Phase 2's order-creation service is called from inside it.

```jsonc
// request
{ "items": [3, 11],
  "name": "فاطمة الصايغ",
  "email": "fatima.a@email.com",
  "language": "ar",
  "clientTotalKwd": 13.000 }      // optional; only used to detect a re-price
```

```jsonc
// 201
{ "orderNumber": "KW-10429",
  "paymentUrl": "https://checkout.tap.company/…",
  "totalKwd": 13.000,
  "totalFormatted": "13.000" }
```

| Status | `error` | When |
| --- | --- | --- |
| 400 | `invalid_request` | empty/oversized cart, bad email, bad language |
| 409 | `item_unavailable` | an id is unknown or unpublished; `ids` lists them |
| 409 | `price_changed` | `clientTotalKwd` ≠ recomputed; body carries `totalKwd` and `items[{id, priceKwd}]` |
| 429 | `too_many_attempts` | more than `Tap:MaxAttempts` on this order |
| 502 | `gateway_error` | Tap rejected or did not answer; the order stays `Pending` and is retryable |
| 503 | `payment_unavailable` | `Tap:Enabled = false` or no key configured |

`503` is deliberate and is never a fake success. A build with no Tap key must
be visibly unable to take money.

### 3.2 `POST /api/checkout/{orderNumber}/retry` — anonymous

For the customer who came back from a declined payment. Body empty. Requires
the order to be `Pending`, not expired, and under the attempt cap. Returns the
same `201` shape with a new `paymentUrl`. Errors as above, plus `404
unknown_order` and `409 order_not_payable`. Rate-limited per order number.

### 3.3 `GET /checkout/return?tap_id=chg_…` — anonymous, redirect target

Not an API: it is the URL given to Tap as `redirect.url` and the browser lands
on it. It calls `GET /v2/charges/{tap_id}`, **writes no order state**, mints a
DataProtection-protected result token (15-minute lifetime, purpose string
`"Nexus.Payments.Result.v1"`, using the key ring already persisted to
`App_Data/dpkeys` in `Program.cs`) and issues a `302`:

- captured → `/success.html?r=<token>`
- declined, failed or cancelled → `/checkout.html?r=<token>`
- `tap_id` missing or unknown → `/checkout.html?e=unknown`

The token, not the order number, is what the success page presents. Order
numbers are short and sequential (`KW-#####`); the repository is public, so
`success.html?o=KW-10431` would be an enumeration of other people's orders
waiting to happen. A protected token signed by a server-held key is the same
pattern the download grants will use, and reuses machinery already wired up.

### 3.4 `GET /api/checkout/result?r=<token>` — anonymous, token-bound

Unseals the token and reads the **order**, not Tap.

```jsonc
{ "orderNumber": "KW-10429",
  "state": "paid",                 // paid | pending | failed | cancelled
  "email": "fatima.a@email.com",
  "totalKwd": 13.000,
  "paymentMethod": "KNET",
  "items": [ { "bookId": 3, "titleEn": "…", "titleAr": "…" } ],
  "messageKey": "ok_body",
  "failureCode": null }
```

`410 token_expired` after 15 minutes; `400 invalid_token` on a bad token.

**`state: "pending"` is a normal outcome, not an error.** The browser can beat
the webhook back. The storefront polls this endpoint every 2 seconds for up to
30 seconds showing `pay_pending_t` / `pay_pending_d`, then stops and tells the
customer their email will confirm it. Reconciliation finishes the job within
five minutes regardless. Letting the return page settle the order instead would
be two writers on the money path, which is the thing CLAUDE.md rule 2 forbids.

### 3.5 `POST /api/payments/tap/webhook` — gateway-signed

Headers: `hashstring` (hex HMAC-SHA256, case-insensitive),
`content-type: application/json`.

**Where the raw request body is read, and why it matters.**

The action is declared with **no body-bound parameter at all**, so MVC's model
binding never touches the stream:

```csharp
[HttpPost("api/payments/tap/webhook")]
public async Task<IActionResult> Receive(CancellationToken ct)
{
    Request.EnableBuffering();
    using var reader = new StreamReader(Request.Body, Encoding.UTF8,
                                        detectEncodingFromByteOrderMarks: false,
                                        leaveOpen: true);
    var raw = await reader.ReadToEndAsync(ct);
    …
}
```

Four distinct reasons, because "read the raw body" is usually given without
one and then quietly undone by the next developer who adds a DTO:

1. **The request body is a forward-only, non-rewindable stream.** If model
   binding reads it first, the action's own read returns an empty string, the
   hash is computed over nothing, every webhook 401s, and no order ever
   completes. `EnableBuffering()` on the first line makes the read repeatable
   so a logging middleware or an exception filter added in Phase 8 cannot
   starve it either.
2. **`transaction.created` must be hashed exactly as it arrived.** It is an
   epoch-milliseconds *string*. A DTO property typed `long` or `DateTime`
   round-trips to a different text — `1760947200000` becomes `1760947200`, or
   an ISO-8601 date — and the concatenation no longer matches what Tap hashed.
   The only safe source is the original text.
3. **`amount` must be parsed as `decimal` from the original token.** A DTO with
   a `double` (or a JSON converter that widens through one) turns `6.500` into
   `6.4999999999999996`, and the formatted value is then whatever rounding
   feels like. Reading the raw text with `JsonElement.GetDecimal()` is exact.
4. **The stored body must be the bytes we verified.** `WebhookEvent.RawBody`
   plus `RawBodySha256` is what a test replays and what the runbook pastes into
   a support ticket. A re-serialised body proves nothing: key order, whitespace
   and number rendering all change, so a "replay" of it would exercise a
   message Tap never sent.

Body size is capped at 64 KB (`Request.ContentLength` checked first, then a
bounded read); over that is `413` without reading. This is an anonymous
endpoint on a 512 MB shared pool.

**Processing order, which is also the security order:**

1. Read raw.
2. Verify the `hashstring` against the current key, then the previous key, in
   fixed time.
3. On failure: insert a `WebhookEvent` with `HashValid = 0` (which the filtered
   index lets through without reserving the slot), increment
   `webhook.rejected`, return **401**. Nothing downstream sees it.
4. On success: parse into a `TapNotification`.
5. **Insert the `WebhookEvent` with `HashValid = 1` first.** A unique violation
   (SQL 2601/2627) on `(TapObjectId, Status)` *is* the duplicate check — return
   `200` immediately, no side effects, `webhook.duplicate++`.
6. Call `ApplyAsync`.
7. Stamp `ProcessedAt` and `Outcome`.
8. Return `200`.

| Status | When |
| --- | --- |
| 200 | accepted, duplicate, unknown order, rejected on amount/currency, illegal transition, unknown status — anything we have durably recorded and will not accept on a retry |
| 400 | body is not JSON, or lacks the fields the hash needs |
| 401 | `hashstring` missing or not matching either key |
| 413 | body over 64 KB |
| 503 | the event could **not** be recorded (database down). This is the only case where Tap should retry, and it is the only case where we ask it to |

**Never 500.** An unhandled exception becomes a retry loop on a message we will
never process, and on this host it also costs memory. Everything inside the
action is wrapped; a failure after the event row is committed returns `200` and
leaves the work to reconciliation, which is exactly what reconciliation is for.

Forward constraints for Phase 8, written here so they are not discovered by
breaking production: this route is exempt from antiforgery, exempt from the
authentication scheme, and rate-limited by `TapObjectId` rather than by IP —
Tap retries from a pool of addresses and an IP limiter would throttle genuine
retries during an incident.

### 3.6 `POST /api/admin/orders/{id}/refund` — admin role — **Phase 7**

Specified here so the service contract is fixed, and deliberately not routed in
Phase 3 (§2.5). Body `{ "reason": "…" }`. Responses `202` with the `Refund`
row; `404 unknown_order`; `409 order_not_refundable`; `409 refund_in_progress`;
`502 gateway_error`.

### 3.7 `GET /api/admin/orders/{id}/payment` — admin role — **Phase 7**

Attempts, webhook events (without `RawBody` unless `?raw=1`), refunds and
`OrderEvent` rows. This is what the runbook's "webhook stopped arriving" and
"customer did not get their link" entries are read against.

### 3.8 Storefront

| Page | Endpoint | Shape it expects |
| --- | --- | --- |
| `checkout.html` | `POST /api/checkout` | §3.1; on `201` → `location.href = paymentUrl` |
| `checkout.html` | `GET /api/checkout/result?r=` | §3.4, when it was loaded with `?r=` after a failure |
| `checkout.html` | `POST /api/checkout/{o}/retry` | §3.2 |
| `success.html` | `GET /api/checkout/result?r=` | §3.4, polled while `state === "pending"` |

`paymentUrl` is followed by a **full top-level navigation**. Never an iframe:
3-D Secure step-ups and the KNET page both refuse to render framed, and the
failure mode is a blank box with no error.

What changes in the markup, kept as close to the current pages as possible:

- `checkout.html` — the `co_pay` fieldset (card number, expiry, CVV, holder)
  is **deleted**, not disabled (PRD PM-2). The contact fieldset stays, minus
  the pre-filled demo values. The country `<select>` becomes a static line
  reading `co_country_kw`, so the order still records a country without a
  control implying a choice. The summary loses its VAT row. The button becomes
  `co_pay_tap` + the total, disables itself on submit, and navigates to
  `paymentUrl`. The whole inline `<script>` moves to `assets/js/checkout.js`;
  the page keeps its existing structure, classes and `data-i18n` idiom.
- `success.html` — reads `?r=`, calls `/api/checkout/result`, renders the
  existing `.ok` panel from the response, and polls while `state === "pending"`.
  The covers grid is built from `items` in the response instead of from
  `state.lib`.

New i18n keys, every one in both `en` and `ar` in the same change (CLAUDE.md
rule 4):

| Key | EN | AR |
| --- | --- | --- |
| `co_country_kw` | Kuwait | الكويت |
| `co_pay_tap` | Pay with Tap | ادفع عبر Tap |
| `co_pay_note` | KNET, card or Apple Pay on Tap's secure page. We never see your card. | كي‑نت أو بطاقة أو Apple Pay على صفحة Tap الآمنة. لا نطّلع على بيانات بطاقتك. |
| `co_redirecting` | Taking you to Tap… | جارٍ تحويلك إلى Tap… |
| `co_err_cart` | A book in your cart is no longer available. | أحد الكتب في سلتك لم يعد متاحاً. |
| `co_err_price_changed` | The price changed while you were shopping. Check the new total. | تغيّر السعر أثناء تصفحك. راجع الإجمالي الجديد. |
| `co_err_email` | Check the email address. | تأكد من البريد الإلكتروني. |
| `co_err_unavailable` | Payment is unavailable right now. Nothing was charged. | الدفع غير متاح حالياً. لم يتم خصم أي مبلغ. |
| `co_err_generic` | We could not start the payment. Nothing was charged. | تعذّر بدء عملية الدفع. لم يتم خصم أي مبلغ. |
| `co_confirm_new_total` | Confirm and pay | أكّد وادفع |
| `pay_pending_t` | Confirming your payment… | جارٍ تأكيد الدفع… |
| `pay_pending_d` | This takes a few seconds. You can close this page — the link reaches your email either way. | تستغرق العملية ثوانٍ. يمكنك إغلاق الصفحة، وسيصلك الرابط على بريدك في كل الأحوال. |
| `pay_failed_t` | The payment did not go through. | لم تتم عملية الدفع. |
| `pay_failed_d` | Nothing was charged. You can try again. | لم يتم خصم أي مبلغ. يمكنك المحاولة مرة أخرى. |
| `pay_cancelled_t` | Payment cancelled. | تم إلغاء الدفع. |
| `pay_cancelled_d` | Your cart is still here. | سلتك ما زالت محفوظة. |
| `pay_retry` | Try again | حاول مرة أخرى |
| `pay_method` | Paid by | وسيلة الدفع |

Three existing strings must change rather than be added to, because they become
false the day this ships:

- `co_secure` — *"This demo does not charge anything."* Replaced by
  `co_pay_note`. Shipping a live payment page that says it is a demo is its own
  kind of defect.
- `co_pay`, `co_card`, `co_exp`, `co_cvv`, `co_holder` — deleted with the card
  fieldset.
- `ok_expiry` (AR) currently reads `الرابط صالح ٧ أيام · ٣ مرات تحميل` —
  **Arabic-Indic digits**, which PRD §5 forbids outright. The string belongs to
  Phase 4, but it is already wrong and sits on a page this phase edits, so it
  is fixed here: `الرابط صالح 7 أيام · 3 مرات تحميل`.

---

## 4. Integration points

**Tap.** `TapClient` is the only type that holds the secret key or knows the
base URL, and the only place an outbound Tap call is made. Three call sites
reach it: `PaymentService.StartCheckoutAsync` / `RetryAsync`
(`POST /charges`), `PaymentService.DescribeReturnAsync` and
`PaymentReconciliationService` (`GET /charges/{id}`), and `RefundService`
(`POST /refunds`). Inbound, `TapWebhookController` is the only entry point and
`TapSignature` the only verifier.

**The charge request, field by field** (PLAN §6.1, completed):

```jsonc
{
  "amount":   13.000,                    // decimal, Currency.Kwd.Round'ed; never a double
  "currency": "KWD",
  "threeDSecure": true,
  "save_card":  false,                   // we store nothing re-chargeable
  "description": "Nexus order KW-10429",
  "statement_descriptor": "NEXUS BOOKS", // what shows on the bank statement
  "lang_code": "AR",                     // hosted page language — see §8
  "receipt": { "email": false, "sms": false },
  "customer": { "first_name": "فاطمة", "last_name": "الصايغ",
                "email": "fatima.a@email.com" },
  "source":   { "id": "src_all" },       // KNET + cards + Apple Pay, one page
  "reference": { "transaction": "KW-10429-1",
                 "order":       "KW-10429",
                 "idempotent":  "KW-10429-1" },
  "metadata": { "orderId": "1042", "orderNumber": "KW-10429", "attempt": "1" },
  "post":     { "url": "https://…/api/payments/tap/webhook" },
  "redirect": { "url": "https://…/checkout/return" }
}
```

Notes that are load-bearing:

- `reference.transaction` is the KNET **trackid** and must be **≤ 20
  characters**. `KW-10429-1` is ten. This limit cost real time on the
  RentCollection KNET integration and is cheap to assert in a test.
- `receipt.email: false` because Tap's own receipt is English-only and would
  reach an Arabic customer before ours does. Our bilingual email (Phase 5) is
  the only receipt.
- `customer.first_name` is required by Tap. A single `name` field is split on
  the first space; with no space, the whole string goes in `first_name` and
  `last_name` is `"-"`. Arabic names are sent as UTF-8, unchanged.
- `lang_code` is how the hosted page comes up in Arabic, which PRD §5 makes a
  requirement rather than a nicety. The exact field name is the one item in
  this payload not yet confirmed against the live sandbox — §8.
- `amount` is serialised from a `decimal`. `System.Text.Json` preserves the
  scale, so `13.000m` writes as `13.000`. Asserted by a test, because a
  `double` anywhere in that chain breaks it invisibly.

**Response fields consumed** from `POST /charges` and `GET /charges/{id}`:
`id`, `status`, `transaction.url`, `transaction.created`, `amount`, `currency`,
`reference.payment`, `reference.gateway`, `source.payment_method`,
`card.last_four`, `card.brand`, `response.code`, `response.message`,
`metadata.orderNumber`.

**Webhook body fields consumed:** `id`, `object`, `status`, `amount`,
`currency`, `reference.gateway`, `reference.payment`, `transaction.created`,
`charge.id` (refunds only), `metadata.orderNumber`.

**PDF storage and stamping (Phase 4).** Not called from here.
`OrderPaidEvent` is raised; Phase 4 registers the handler that issues one
`DownloadGrant` per `OrderItem`. `OrderRefundedEvent` revokes them. Phase 3
ships no-op handlers so the event path is exercised and the contract is fixed.

**Email (Phase 5).** Same mechanism. `OrderPaidEvent` carries everything the
email needs — order number, customer email, preferred language, item titles in
both languages — so Phase 5 adds a handler and touches nothing in this phase.
Enqueue, never send inline: the webhook must return within a second on a shared
pool, and an SMTP timeout must not become a Tap retry.

**Background jobs.** `PaymentReconciliationService` is the only one added here,
registered as a hosted service in `AddNexusInfrastructure`, guarded by
`JobLease`, and disabled entirely when `Tap:Enabled = false` so a developer
machine with no key does not poll an endpoint it cannot authenticate to.

**Configuration — and the Phase 9 switch.** `TapOptions`, bound from the `Tap`
section, `DataAnnotations`-validated, `ValidateOnStart()`.

| Key | Dev | Staging | Production (Phase 9) |
| --- | --- | --- | --- |
| `Tap:BaseUrl` | `https://api.tap.company/v2` | same | same |
| `Tap:SecretKey` | env `Tap__SecretKey`, `sk_test_…` | Actions secret → App Pool env var | **`sk_live_…`**, App Pool env var only |
| `Tap:PreviousSecretKey` | unset | unset | set for the rotation window, then cleared |
| `Tap:WebhookUrl` | tunnel URL | staging host | production host |
| `Tap:ReturnUrl` | `http://localhost:5091/checkout/return` | staging host | production host |
| `Tap:Enabled` | `false` unless a key is present | `true` | `true` |
| `Tap:TimeoutSeconds` | 20 | 20 | 20 |
| `Tap:ReconcileAfterMinutes` | 30 | 30 | 30 |
| `Tap:SweepIntervalMinutes` | 5 | 5 | 5 |
| `Tap:SweepBatchSize` | 50 | 50 | 50 |
| `Tap:AbandonAfterHours` | 24 | 24 | 24 |
| `Tap:MaxAttempts` | 5 | 5 | 5 |
| `Tap:HostedPageTtlMinutes` | 30 | 30 | 30 |
| `Tap:RetryChargeOnTimeout` | `false` | `false` | `false` until §8 is answered |
| `Tap:StatementDescriptor` | `NEXUS BOOKS` | same | same |

**Going live at Phase 9 is four environment variables and a pool restart** —
`Tap__SecretKey`, `Tap__WebhookUrl`, `Tap__ReturnUrl`, and the webhook URL in
Tap's merchant dashboard. No code change, no redeploy, which is what PLAN §6.7
and decision 3 promise. Two startup guards make that safe rather than merely
possible:

- A key starting `sk_live_` outside `Production` → **startup failure**. Staging
  must not be able to take real money by accident.
- No key, or a blank key, in `Production` → **startup failure**, unless
  `Tap:Enabled` is explicitly `false`. A production store that silently cannot
  charge is worse than one that will not start.
- A key starting `sk_test_` in `Production` logs a Warning on every charge and
  shows `mode: "test"` on `/health`. Through Phases 3–8 production does not
  exist yet, but the day it does, this is the thing somebody forgets.

No key, no masked key, no fragment of a key is ever written to
`appsettings.json`, `appsettings.Development.json` or any file in the
repository. `appsettings.json` carries only the non-secret `Tap` keys above.

---

## 5. File ownership

The split is disjoint. The contract the two sides meet at is §3: JSON shapes,
status codes and `messageKey` values. Neither side waits for the other.

### `backend-dev` owns — new files

```
src/Nexus.Domain/Entities/PaymentAttempt.cs
src/Nexus.Domain/Entities/WebhookEvent.cs
src/Nexus.Domain/Entities/Refund.cs
src/Nexus.Domain/Entities/OrderEvent.cs
src/Nexus.Domain/Entities/JobLease.cs
src/Nexus.Domain/Payments/PaymentEnums.cs
src/Nexus.Domain/Payments/OrderPaidEvent.cs
src/Nexus.Domain/Payments/OrderRefundedEvent.cs

src/Nexus.Application/Payments/ITapClient.cs
src/Nexus.Application/Payments/ITapSignature.cs
src/Nexus.Application/Payments/IPaymentService.cs
src/Nexus.Application/Payments/IRefundService.cs
src/Nexus.Application/Payments/IOrderPaymentStateMachine.cs
src/Nexus.Application/Payments/IDomainEventDispatcher.cs
src/Nexus.Application/Payments/PaymentDtos.cs
src/Nexus.Application/Payments/TapContracts.cs

src/Nexus.Infrastructure/Payments/TapOptions.cs
src/Nexus.Infrastructure/Payments/TapClient.cs
src/Nexus.Infrastructure/Payments/TapSignature.cs
src/Nexus.Infrastructure/Payments/PaymentService.cs
src/Nexus.Infrastructure/Payments/RefundService.cs
src/Nexus.Infrastructure/Payments/OrderPaymentStateMachine.cs
src/Nexus.Infrastructure/Payments/PaymentReconciliationService.cs
src/Nexus.Infrastructure/Payments/PaymentMetrics.cs
src/Nexus.Infrastructure/Payments/NoOpPaymentEventHandlers.cs
src/Nexus.Infrastructure/Payments/TapHealthCheck.cs
src/Nexus.Infrastructure/Persistence/Configurations/PaymentModel.cs
src/Nexus.Infrastructure/Persistence/Migrations/<timestamp>_Payments.cs

src/Nexus.Web/Controllers/CheckoutController.cs
src/Nexus.Web/Controllers/TapWebhookController.cs

tests/Nexus.UnitTests/Payments/*
tests/Nexus.IntegrationTests/Payments/*
tests/Nexus.IntegrationTests/TapStub.cs
```

### `storefront-dev` owns

```
src/Nexus.Web/wwwroot/checkout.html
src/Nexus.Web/wwwroot/success.html
src/Nexus.Web/wwwroot/assets/js/checkout.js      (new)
src/Nexus.Web/wwwroot/assets/js/i18n.js          (sole owner — see below)
```

**`i18n.js` has exactly one owner.** It is the file two developers always edit
at once and it is where bilingual parity is won or lost. Because the API
returns `messageKey` and never prose (§3), `backend-dev` has no reason to open
it: a new error means a new key in the design table above, added by
`storefront-dev` in both languages in the same commit. The existing i18n
completeness unit test enforces it.

### Shared — the collision list

| File | What this phase adds | How to avoid the conflict |
| --- | --- | --- |
| `src/Nexus.Infrastructure/Persistence/NexusDbContext.cs` | five `DbSet`s and one `b.ApplyConfiguration(new PaymentModel())` line | all the model detail lives in `PaymentModel.cs`, which this phase owns outright; the context gains six lines, not two hundred |
| `src/Nexus.Infrastructure/DependencyInjection.cs` | `AddHttpClient<ITapClient,TapClient>`, `AddOptions<TapOptions>().ValidateOnStart()`, the hosted service, the scoped payment services | one contiguous block, appended after the catalogue registrations |
| `src/Nexus.Domain/Entities/Order.cs` | four columns, one enum member (§1.3) | **Phase 2 lands first.** This phase takes the file as it is and appends |
| `src/Nexus.Web/Program.cs` | `.AddCheck<TapHealthCheck>("tap")` | one line |
| `src/Nexus.Web/appsettings.json` | the non-secret `Tap` section, and the `TapClient` log-level pin | no secrets, ever |
| `src/Nexus.Web/appsettings.Development.json` | `Tap:Enabled: false`, local return URL | |
| `src/Nexus.Web/wwwroot/assets/js/site.js` | clear `state.cart` only once `state === "paid"`; delete the fake `state.lib` writes | storefront-dev; the cart must survive a failed payment |
| `tests/Nexus.IntegrationTests/NexusApp.cs` | point `Tap:BaseUrl` at `TapStub`, inject a fixed fake `sk_test_` key | backend-dev |
| `.github/workflows/deploy-staging.yml` | `Tap__SecretKey`, `Tap__WebhookUrl`, `Tap__ReturnUrl` | devops |
| `README.md`, `PLAN.md` §6 | status, and the two §6 refinements (five-minute sweep, filtered index) | tech-writer, after merge |

### Deleted by this phase

`checkout.html`'s card fieldset and its `onclick` handler, including the lines
that fabricate library rows and — still live in the current file — the AED VAT
calculation `const rate = state.cur === 'AED' ? 0.05 : 0`. CAT-25 removed the
currency switcher but left this behind; it is dead under Kuwait-only and must
not survive into a page that now charges real money.

---

## 6. Risks and decisions

### 6.1 Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| An amount hashed at two decimals (`6.50` for `6.500`) | Every webhook 401s, silently; no order ever completes and the storefront looks perfect | The hashstring test against Tap's documented sample is written **first**, before any other payment code (PLAN §14). `Currency.Format` is the only formatter. Review greps for `F2`, `ToString("0.00")` and `double` anywhere near a payment |
| **Idempotency poisoning** — a forged, unsigned webhook reserves `(chargeId, CAPTURED)` and the genuine one is swallowed as a duplicate | A paid customer with no book, triggerable by anyone, and indistinguishable in the logs from a Tap retry | The unique index is filtered on `HashValid = 1` (§1.1). Test 15 in §7 exists only for this |
| Double capture from two tabs | Charged twice, refunded late or never | In-flight attempt guard; surplus-payment detection on a `Paid` order; alert; no automatic refund |
| `POST /charges` times out after Tap created the charge | A charge we have no id for — real money, invisible | The attempt row and its idempotent reference are written **before** the call; a same-key re-post returns the existing charge. Disabled behind `Tap:RetryChargeOnTimeout` until the sandbox confirms the semantics (§8); meanwhile each occurrence is alerted and counted |
| IIS overlapped recycle runs two reconcilers | Double processing during a deploy — the worst possible moment | `JobLease`, plus the state machine's `RowVersion` guard, which makes a double run harmless anyway |
| The webhook handler does slow work inline (stamping a 300-page PDF, SMTP) | Tap times out and retries; the 512 MB shared pool recycles mid-request | Everything downstream is a post-commit domain event, enqueued. The handler's own work is one insert and one update |
| `reference.transaction` longer than 20 characters | KNET rejects the charge; cards still work, so it reads as a KNET outage | Asserted in a test. `KW-#####-n` is ten |
| A live key in a log, an Actions output or a commit | Rotate the key, tell Tap, explain the gap | Redacting log scope; `TapClient` logging pinned to Warning; masked `/health`; gitleaks already in CI; the key is in no file in the repository |
| 3-D Secure in an iframe | A blank box and no error | Full top-level navigation to `paymentUrl`; stated in §3.8 and checked in E2E |
| The redirect becomes a second writer "just for the fast path" | Two writers on the money path, races, double grants | `DescribeReturnAsync` has no access to the state machine. The constraint is in the type system, not in a comment |
| A dashboard refund arrives before Phase 4 exists | A handler reaches for a grant table that is not there | Grant revocation is an event handler; Phase 3's is a no-op. The order still flips to `Refunded` correctly |
| Tap adds or renames a status | An unknown status mapped onto a guess | `Unknown=99`, recorded, `200`, alerted. Never inferred |

### 6.2 Decisions

1. **One attempt row per charge, `Order.TapChargeId` as the winning snapshot.**
   PLAN §4's single column cannot survive a retry. Rejected: overwriting it,
   which orphans the first charge's late webhook.
2. **The uniqueness index is filtered on `HashValid = 1`.** The alternative —
   a plain unique index as PLAN §4 reads — is remotely exploitable in a public
   repository. This is the one place this design is deliberately stricter than
   §6, and it needs recording back in PLAN.
3. **A declined attempt leaves the order `Pending`.** The order fails only on
   expiry. Rejected: failing immediately, which strands a customer who needs a
   second try and empties their cart for nothing.
4. **Reconciliation runs every five minutes, with the nightly pass kept.**
   PLAN §6.6 says nightly; nightly meets PM-8's letter and turns a lost webhook
   into a next-day delivery. Rejected: nightly only.
5. **Every reconciliation settlement raises an alert.** A webhook that never
   arrived is the same defect as one that was rejected, and PRD §11 sets that
   target at zero. Without the alert the order completes and the broken
   notification path is never noticed.
6. **Admin refunds are full-order only; partial refunds are accepted inbound
   only.** An amount cannot be mapped to items, so "which grants" would be
   ambiguous. `PartiallyRefunded` exists so a dashboard refund lands somewhere
   honest and gets a human's attention.
7. **The refund HTTP endpoint ships in Phase 7, not here.** No admin identity
   exists yet, and an unauthenticated money endpoint in a public repository is
   not acceptable behind a flag. The service and the webhook path ship now; the
   operator uses the Tap dashboard in the interim and everything downstream
   behaves identically.
8. **`Tap:Enabled = false` returns `503`, never a simulated success.** The fake
   Tap server lives in the test project and is referenced by no shipped
   assembly. Rejected: a `FakeTapClient` in Infrastructure — one bad
   registration away from a production store that pretends to take money.
9. **The result token, not the order number, is what the success page holds.**
   `KW-#####` is sequential and the repository is public. Reuses the
   DataProtection key ring already persisted in `Program.cs`.
10. **The API returns `messageKey`, never prose.** It makes `i18n.js`
    single-owner and makes a missing Arabic string impossible to ship.
11. **Domain events, not direct calls, for grants and email.** Keeps Phase 3
    mergeable alone and keeps Phases 4 and 5 out of the state machine.
12. **`POST /api/checkout` is the single storefront entry point** and calls
    Phase 2's order service internally, per PLAN §6.1 step 1. Two public ways
    to create an order is two places for the total to be computed. If Phase 2
    shipped `POST /api/orders` publicly, this phase retires it — see §8.
13. **No new runtime NuGet package beyond three first-party ones.**
    `Microsoft.Extensions.Http` 8.0.1 (`IHttpClientFactory` in Infrastructure),
    `Microsoft.Extensions.Hosting.Abstractions` 8.0.1 (`BackgroundService` in
    Infrastructure), `Microsoft.Extensions.Options.DataAnnotations` 8.0.0
    (`ValidateOnStart`). HMAC, fixed-time comparison, metrics and JSON are all
    BCL. Polly was considered and rejected: the only retry worth having is on a
    GET, and the one on a POST must be hand-written precisely because it is
    dangerous. Versions sit alongside the 8.0.11 EF packages already pinned, and
    `TreatWarningsAsErrors` plus the CI dependency audit both stay clean.

An ADR is warranted for decisions 2, 4 and 7 — each would be expensive or
embarrassing to reverse:
`docs/adr/0003-webhook-idempotency-and-reconciliation.md` and
`docs/adr/0004-refund-surface-deferred-to-phase-7.md`.

---

## 7. What must be tested

The hashstring test is written **before any other payment code exists**. That
is PLAN §14's top risk and it is cheap insurance: the formula is seven string
concatenations and one HMAC, and getting it wrong costs every order.

### Unit — `tests/Nexus.UnitTests/Payments`

1. `toBeHashed` and the HMAC match **Tap's own documented sample**, byte for byte.
2. KWD scale: `6.5m → "6.500"`, `6m → "6.000"`, `6.5005m → "6.501"`
   (away from zero), `13m → "13.000"`. An `F2` anywhere fails.
3. AED still formats at two decimals — the `Currency` contract is unchanged by
   this phase.
4. A body whose `amount` arrives as the number `6.5` hashes identically to one
   sending `6.500` or the string `"6.500"`.
5. Absent `reference.gateway` hashes as `""` — not `"null"`, not omitted.
6. `transaction.created` is hashed verbatim; `"created": "1760947200000"` must
   not be re-rendered.
7. An unknown currency is rejected with a reason and **no exception thrown**.
8. A body with reordered keys, extra whitespace and unknown fields still
   verifies — proof that nothing is re-serialised.
9. Verification succeeds on `Tap:PreviousSecretKey` during rotation, and logs it.
10. `reference.transaction` is ≤ 20 characters for an order number at the top
    of the range.
11. `TapOptions`: `sk_live_` outside Production fails startup; a blank key in
    Production fails startup; `Tap:Enabled = false` passes.
12. The secret key appears in no emitted log — asserted against a captured
    sink, including the `HttpClient` handler's own logging.

### Integration — `tests/Nexus.IntegrationTests/Payments`, real SQL Server, `TapStub`

`TapStub` is a tiny in-test Kestrel host owned by the test project: it answers
`POST /charges`, `GET /charges/{id}` and `POST /refunds`, counts requests, and
signs webhook bodies with the fixture's fake `sk_test_` key. No new package.
`NexusApp` points `Tap:BaseUrl` at it.

13. A signed `CAPTURED` webhook moves a `Pending` order to `Paid`, writes the
    attempt snapshot onto the order, increments `SalesCount`, and raises
    `OrderPaidEvent` exactly once.
14. **Replay:** the identical delivery a second time → `200`, one
    `WebhookEvent` row, no second event, no second `OrderEvent`.
15. **Idempotency poisoning:** a forged `(chargeId, CAPTURED)` body with a bad
    hash arrives *first*; it is `401`ed and recorded, and the genuine delivery
    that follows is still processed. *This is the case that would otherwise
    ship broken and be indistinguishable from "Tap sent a duplicate".*
16. Valid signature, amount ≠ order total → `200`, order stays `Pending`,
    `webhook.rejected{reason=amount}` incremented.
17. Valid signature, currency ≠ KWD → the same.
18. Missing `hashstring` → `401`; nothing written but the rejection row.
19. A webhook for an unknown charge id → `200` (not a retry loop), recorded,
    alerted.
20. A **different** charge id captures on an already-`Paid` order → no second
    grant event, `OrderEvent` notes `surplus-payment`, alert raised.
21. `CAPTURED` on a `Refunded` order → no transition, `IllegalTransition`, alert.
22. Refund webhook, amount == total → `Refunded`, `OrderRefundedEvent` once.
    Amount < total → `PartiallyRefunded`, **no** revocation event.
23. **Two concurrent POSTs of the same webhook** (`Task.WhenAll`) → exactly one
    transition, one event, one `WebhookEvent` row. Needs the real database; an
    in-memory provider passes this while production fails it.
24. The database being unavailable gives `503`, not `500` — so Tap retries.
25. A 70 KB body is `413`ed without being read.
26. **The total is never taken from the client:** post a `clientTotalKwd` of
    `0.001` and assert `409 price_changed`; post a request carrying an injected
    `totalKwd` field and assert the charge amount equals the database total.
27. A book unpublished between cart and checkout → `409 item_unavailable`, no
    order written, no charge created.
28. **Double click on Pay** → one `PaymentAttempt`, one `POST /charges` seen by
    the stub, the same `paymentUrl` returned twice.
29. Charge creation timeout with `RetryChargeOnTimeout = true` → exactly one
    re-post, carrying the **identical** `reference.idempotent`.
30. **The return endpoint writes nothing:** with webhooks suppressed, hit
    `/checkout/return?tap_id=…` for a captured charge and assert the order is
    still `Pending` and no grant event fired.
31. The result token expires at fifteen minutes (`410`); a tampered token is
    `400`.
32. Reconciliation: a 31-minute-old attempt that Tap reports `CAPTURED` →
    `Paid`, `Source = Reconciliation`, lost-webhook alert raised.
33. Reconciliation: `DECLINED` and past `PaymentExpiresAt` → order `Failed`, no
    grants.
34. Reconciliation racing a webhook for the same charge → one transition.
35. The reconciler does not run when `Tap:Enabled = false`.
36. `Tap:Enabled = false` → `POST /api/checkout` returns `503
    payment_unavailable`, and no order row is created.

### i18n and E2E

37. The existing i18n completeness test covers every key added in §3.8 in both
    `en` and `ar`, and asserts no Arabic-Indic digit appears in any value.
38. Playwright, both languages and RTL: browse → cart → checkout → Tap test
    KNET → success, and the Arabic run lands on an Arabic hosted page.
39. Playwright: a declined test card → the failure panel, the cart still
    populated, and "try again" producing a second working `paymentUrl`.

---

## 8. Open questions

Each is answerable in the Tap sandbox during the build. None blocks starting,
and each has a stated default so the work is not held up.

1. **Does the current Tap API still honour `reference.idempotent`, and does it
   dedupe charge creation on it?** PLAN §6.1 names the field, and the same-key
   re-post after a timeout (§2.2) depends on it. *Default until answered:*
   `Tap:RetryChargeOnTimeout = false`; timeouts are resolved by reconciliation
   and alerted.
2. **The exact field controlling the hosted page's language, and whether it
   accepts `"AR"`.** `lang_code` is assumed. PRD §5 makes an Arabic checkout a
   requirement rather than a nicety, so this is verified in the sandbox on the
   first day of the phase.
3. **Does a refund notification hash over the refund's own `id` (`re_…`) or the
   parent charge's?** PLAN §6.5 says "the same hashstring scheme", which this
   design reads as the refund's own `id`. Verify before the refund path is
   called done.
4. **What is the public HTTPS URL for webhooks during local development?** The
   webhook cannot reach `localhost:5091`; a tunnel is needed. It affects
   nothing but the developer loop.
5. **Phase 2's public surface.** If `POST /api/orders` shipped as a public
   endpoint, this phase retires it in favour of `POST /api/checkout`
   (decision 12). Confirm with whoever builds Phase 2 before both exist.
6. **Where do payment alerts go before Phase 5's SMTP exists?** Today the
   answer is the Serilog error digest, which nobody reads at 02:00. A webhook
   rejection with a target of zero needs a channel with a human on it.
7. **Is the `KW-#####` order-number range adequate, and is it sequential or
   randomised?** Sequential is fine for KNET but makes order numbers guessable,
   which is why the result token exists; confirm Phase 2's choice.
8. **A Tap sandbox merchant account with a per-environment webhook URL** —
   otherwise staging and a developer's tunnel compete for one setting.
