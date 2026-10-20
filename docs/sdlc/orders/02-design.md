# Orders and pricing — Phase 2 design

SDLC phase 2 (design) for **Phase 2 — Cart, pricing and order creation** in PRD §12
and PLAN §5. Written 2026-10-20 against Phase 1 as merged.

Settled and not reopened here: **Kuwait only, KWD only, no FX step, no VAT step**
(PRD §4); **a book is one bilingual PDF** (D5); **no free books** (D7); **guest
checkout and customer accounts and admin, one Identity store** (PLAN §4.1);
**Arabic is a first-class edition** (CLAUDE.md rule 4); **the repository is
public** (CLAUDE.md rule 5).

What this phase delivers: a `Pending` order that exists in the database, priced
entirely by the server, with per-item price snapshots and an order number short
enough for Tap's reference field. No payment, no money movement, no download
grants, no email, no admin screen.

---

## 0. The shape of the problem

Phase 1 made the catalogue authoritative. Phase 2 makes the **total**
authoritative. Those are different problems: the catalogue is read-only to the
public, so there was nothing to tamper with. An order is the first thing a
visitor can ask us to write, and the first place a number they control could
turn into a number we charge.

The design answer is blunt and worth stating before the detail: **the client
sends no money at all.** Not a unit price, not a subtotal, not a total, not a
quantity, not a currency. It sends book ids and contact details. Everything with
a decimal point in it is read out of the `Books` table on the server inside the
same request. There is no "validate the client total against ours" step, because
there is no client total to validate — which is the only version of PM-3 that
cannot be got wrong by a later edit.

Three other things have to be right in this phase or they are expensive later:

1. **Price snapshots.** An operator will edit a price. Every order placed before
   that edit must keep showing, on its invoice and in the admin, what was
   actually charged. That is a column, not a join.
2. **The order number.** It becomes Tap's `reference.transaction` in Phase 3,
   gets read out over the phone in support, and appears on the PDF watermark in
   Phase 4. It is hard to change once any of those exist.
3. **The state machine.** Phase 3's webhook, Phase 3's reconciliation job and
   Phase 7's refund button all move the same five states. If each writes its own
   `order.Status = …` the transitions diverge. One domain method, one place.

---

## 1. Domain

Three new entities, one new enum, one new value object. `Book` and `Category`
are unchanged except that `Book.SalesCount` acquires a writer (D2 promised one
in this phase).

### 1.1 `Money` — `src/Nexus.Domain/Money.cs`

```csharp
public readonly record struct Money(decimal Amount, Currency Currency)
```

A pairing of an amount with the `Currency` that owns its scale. It exists so an
amount cannot drift away from the thing that knows it has three decimals.

| Member | Behaviour |
| --- | --- |
| `Money.Kwd(decimal)` | The only constructor Phase 2 calls |
| `Money.Zero(Currency)` | Identity for `Sum` over an empty sequence |
| `operator +`, `Sum(IEnumerable<Money>)` | **Throws** `InvalidOperationException` on mixed currencies — a bug, never a conversion |
| `Rounded` | `Currency.Round(Amount)` — away-from-zero, already tested in `CurrencyTests` |
| `ToString()` | `Currency.Format(Amount)` → `"6.500"`, invariant, Western digits |

Deliberately absent: `operator *` and any implicit conversion to `decimal`.
There is no quantity in this model (§1.4), so multiplication has no caller, and
forcing `.Amount` at the persistence boundary keeps the unwrap visible in a
diff. No `operator -` either until refunds need one in Phase 3.

**`Money` is not persisted.** Order columns are plain `decimal` with explicit
`HasColumnType("decimal(18,3)")`, exactly as `Book.PriceKwd` already is. An EF
owned type or value converter across five money columns buys nothing here and
makes the generated migration harder to read in review — and this is a migration
people will read.

**Rounding happens twice and only twice:** once per line and once on the total,
both inside `PricingService.Quote`. Not inside `operator +`, because rounding on
every addition is how a sum drifts.

### 1.2 `Customer` — `src/Nexus.Domain/Entities/Customer.cs`

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `int` identity | **Database-generated.** `Book.Id` is `ValueGeneratedNever()` because the seed owns book ids (CAT-05); nothing seeds customers, so do not copy that line |
| `Email` | `nvarchar(256)` | As typed, for display |
| `NormalisedEmail` | `nvarchar(256)`, **unique index** | Trimmed, `ToUpperInvariant()`. Matches the Identity convention so Phase 6's merge is a join, not a migration |
| `Name` | `nvarchar(128)` | NVARCHAR: `فاطمة الصايغ` must survive |
| `Country` | `char(2)`, default `'KW'` | **Set server-side, never from the request.** See D2.3 |
| `PreferredLanguage` | `nvarchar(2)` | `en` or `ar`. Captured at checkout so Phase 5 knows which language to send the delivery email in without guessing |
| `IdentityUserId` | `nvarchar(450)` null, indexed | Always null in Phase 2. Non-null from Phase 6 |
| `CreatedAt`, `UpdatedAt` | `datetimeoffset` | UTC |

`PreferredCurrency` from PLAN §4 is **not** added. The UAE-deferral deal keeps
unused *fact-of-record* columns on `Order` (what actually happened); it is not a
licence to add an unused *preference* column for a choice the UI does not offer.
When a currency switcher exists, so can the preference.

### 1.3 `Order` — `src/Nexus.Domain/Entities/Order.cs`

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `int` identity | Internal only; never in a URL or an email |
| `OrderNumber` | `nvarchar(20)`, **unique index** | §1.5. 16 characters today; 20 is the ceiling Tap's reference imposes |
| `CustomerId` | `int` FK → `Customer`, `Restrict` | A customer with orders cannot be deleted; GDPR-style erasure anonymises the customer row and keeps the order (PRD §10, "order records retained for accounting") |
| `Status` | `int` → `OrderStatus` | §1.6 |
| `StatusDetail` | `nvarchar(200)` null | Why it failed or was cancelled, in English, for the admin and the log. **Not customer-facing** — no Arabic obligation, and §3 explains why |
| `Language` | `nvarchar(2)` | The language the order was placed in. A *snapshot*, unlike `Customer.PreferredLanguage`, which the customer can later change — the invoice must stay in the language it was issued in |
| `Currency` | `nvarchar(3)` | Always `"KWD"` in v1 |
| `SubtotalKwd` | `decimal(18,3)` | Sum of the line snapshots |
| `VatRate` | `decimal(5,4)` | Always `0` in v1 |
| `VatAmount` | `decimal(18,3)` | Always `0` in v1 |
| `TotalKwd` | `decimal(18,3)` | `Subtotal + VatAmount`. The authoritative figure |
| `FxRate` | `decimal(18,6)` | Always `1` in v1 |
| `TotalCharged` | `decimal(18,3)` | Always `= TotalKwd` in v1. The amount handed to Tap |
| `TapChargeId` | `nvarchar(64)` null, **unique filtered index** | Written in Phase 3 |
| `TapPaymentReference` | `nvarchar(64)` null | Phase 3 |
| `TapGatewayReference` | `nvarchar(64)` null | Phase 3. KNET's own reference — what a bank statement shows |
| `PaymentMethod` | `nvarchar(32)` null | Phase 3 |
| `IdempotencyKey` | `nvarchar(64)` null, **unique filtered index** | §2.4 |
| `CreatedAt`, `UpdatedAt`, `PaidAt?` | `datetimeoffset` | UTC |

Indexes: unique `OrderNumber`; unique filtered `TapChargeId`; unique filtered
`IdempotencyKey`; `(CustomerId, CreatedAt DESC)` for Phase 6's order history;
`(Status, CreatedAt)` for the Phase 3 reconciliation sweep, which asks exactly
"`Pending` older than 30 minutes".

**The four unused columns, stated plainly.** `Currency`, `FxRate`, `VatRate` and
`VatAmount` are written by `PricingService.Quote` and by nothing else, to the
constants `"KWD"`, `1`, `0`, `0`. **No code path reads them to compute
anything** in v1 — not the total, not the Tap amount, not the invoice. They
exist because they are facts about a transaction that already happened, and
backfilling a fact you did not record is guesswork: if the UAE launches in 2027,
every 2026 order must still be able to say "this was KWD, no VAT, rate 1" rather
than have it inferred from the absence of a column (PRD §4, PLAN §6.4). The
single place they are set is one method, so a UAE launch changes `Quote` and the
invoice template, not the order model. `TotalCharged` is slightly different: it
is already load-bearing in Phase 3, because it is the number formatted into the
Tap charge and re-checked against the webhook (PM-7). In v1 it equals
`TotalKwd`; the payment path must read `TotalCharged`, not `TotalKwd`, from the
start, or the UAE launch quietly charges the wrong currency's figure.

**No VAT row renders anywhere.** PM-10. The storefront's remaining 0% VAT row is
removed in this phase — see §6.

### 1.4 `OrderItem` — `src/Nexus.Domain/Entities/OrderItem.cs`

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `int` identity | |
| `OrderId` | FK → `Order`, **Cascade** | Items are owned by the order |
| `BookId` | FK → `Book`, **Restrict** | A sold book can never be deleted. This is what makes the admin's `a_del_confirm` copy ("Buyers who already own it keep their copy") true rather than aspirational — deleting becomes unpublishing |
| `UnitPriceKwd` | `decimal(18,3)` | **The snapshot.** Written once, never updated |
| `TitleEnSnapshot`, `TitleArSnapshot` | `nvarchar(256)` | |
| `AuthorEnSnapshot`, `AuthorArSnapshot` | `nvarchar(128)` | |

Unique index on `(OrderId, BookId)`. **There is no `Quantity` column.** ST-5:
the same book cannot be bought twice in one order because it is a file, not a
stock item. Putting that in the schema means no later code can create the
situation, and it removes multiplication from the pricing engine entirely —
`LineTotal` *is* `UnitPriceKwd`.

Both languages are snapshotted because the invoice (AC-5) and the Phase 5 email
exist in both, and an Arabic invoice reprinted in 2028 must not depend on the
`Books` row still being there, still being published, or still carrying the same
Arabic title. Author is snapshotted for the same reason and costs two columns.

### 1.5 Order number — is `KW-#####` adequate? No.

The demo's `'KW-'+(10429+Math.floor(Math.random()*70))` is a placeholder and
fails on four counts: a five-digit space is exhausted at 100,000 orders; random
draws inside it collide constantly (the demo's 70-wide range collides within a
handful of orders); a sequential version leaks total order volume to anyone who
buys twice, which on a public-source store with a public order number is a
competitor's free metric; and neither version carries a date, so support and
reconciliation cannot narrow a lookup without a database query.

**Decided format — `KW-{yyMMdd}-{XXXXXX}`, 16 characters.**

```
KW-251021-7F3K2M
```

- `KW` — the market. `AE` is reserved for a UAE launch at no cost. Kept because
  the demo, the admin mock-ups and the i18n copy all already read this way.
- `yyMMdd` — the date **in Kuwait time (UTC+3, no DST)**, not UTC. Every stored
  timestamp stays UTC, but the human-readable part of a human-readable number
  must match the day the operator and the buyer remember. An order placed at
  01:30 Kuwait time is the 21st to them and would be the 20th in UTC.
- Six characters of **Crockford base32** (`0123456789ABCDEFGHJKMNPQRSTVWXYZ` —
  no `I`, `L`, `O` or `U`), drawn from `RandomNumberGenerator` with rejection
  sampling so there is no modulo bias. The excluded letters are the ones that
  are misread as `1` and `0` over the phone, which is the number's main job.
  `U` is excluded by Crockford to avoid accidental profanity.

32⁶ ≈ 1.07 × 10⁹ values per day-prefix. At a thousand orders a day the birthday
probability of any collision is about 5 × 10⁻⁴. It is still handled: the unique
index is the authority, and `OrderService` retries generation up to three times
on a unique-constraint violation before failing the request. Three is enough —
a fourth collision in one day is not a thing that happens, and an unbounded
retry loop under a real fault is worse than a 500.

Properties that matter downstream:

- **16 characters**, so it fits Tap's short reference with room to spare, and
  the column is `nvarchar(20)` to make the ceiling explicit rather than
  generous.
- **ASCII uppercase and digits only.** Western digits in both languages
  (PRD §5), nothing a RTL page will reorder — but the storefront must still
  render it inside `<bdi>` or `dir="ltr"`, or Arabic bidi will shuffle the
  hyphenated groups. This is a real rendering bug, not a theoretical one (§3.4).
- **Not a secret and not an authorisation token.** Knowing an order number must
  get an attacker nothing (CLAUDE.md rule 5). Nothing in §3 accepts it as proof
  of anything; the order-status read is authorised by a cookie, not by the
  number.
- Non-sequential, so it reveals no volume.

Generation lives in `src/Nexus.Application/Orders/OrderNumber.cs` as a pure
static — same shape as `ArabicSearch`, same reason: a rule that must be tested
in isolation does not belong inside a service with a `DbContext`.

### 1.6 `OrderStatus` and the state machine

```csharp
public enum OrderStatus { Pending = 0, Paid = 1, Failed = 2, Refunded = 3, Cancelled = 4 }
```

Explicit values; these are persisted as `int` and must never be renumbered.

| From | To | Who causes it | Phase |
| --- | --- | --- | --- |
| *(created)* | `Pending` | `POST /api/orders` | 2 |
| `Pending` | `Paid` | A **verified** webhook, or reconciliation reading a captured charge | 3 |
| `Pending` | `Failed` | A verified webhook reporting decline, or reconciliation finding the charge failed | 3 |
| `Pending` | `Cancelled` | Buyer abandons before a charge exists; reconciliation finds no `TapChargeId` after 30 minutes; operator cancels | 3 / 7 |
| `Failed` | `Paid` | **Allowed.** See below | 3 |
| `Cancelled` | `Paid` | **Allowed.** Same reason | 3 |
| `Paid` | `Refunded` | Refund webhook | 3 |
| `Paid` | `Paid` | **No-op, not an error.** Webhook replay (PM-6) | 3 |
| anything | `Pending` | **Refused** | — |
| `Refunded` | anything | **Refused.** A re-purchase is a new order | — |

**`Failed → Paid` and `Cancelled → Paid` are deliberately legal, and this is the
case that would otherwise ship broken.** Reconciliation marks an order `Failed`
after thirty minutes on the strength of a timeout or a `GET /charges/{id}` that
may itself be stale; a verified webhook saying the charge captured is the money
actually having moved. CLAUDE.md rule 2 says the webhook is the only
authoritative signal, so it must be able to override a guess. The guard is not
the prior state, it is the same guard the happy path uses: a valid signature,
and `amount` and `currency` matching `TotalCharged` and `Currency` (PM-7). The
transition records `StatusDetail = "Late capture after reconciliation"` so the
admin can see it happened. A system that refuses this transition takes a
customer's money and never delivers the book, and nothing looks broken.

The guard lives in one domain method:

```csharp
// returns null when the move is allowed, or why it is not
public string? TransitionBlocker(OrderStatus to)
```

plus `bool TryTransitionTo(OrderStatus to, string? detail, DateTimeOffset at)`
which applies it, stamps `UpdatedAt`, and sets `PaidAt` on first entry to
`Paid`. Same pattern as `Book.PublishBlocker()` / `CanPublish()`, for the same
reason that one gives: the admin, the webhook, the reconciliation job and any
future importer cannot drift apart if the rule has exactly one home. Nothing
outside the entity assigns `Status`; `NexusDbContext` does not need to enforce
that, code review does.

### 1.7 `SalesCount`

D2 said the "most bought" sort reads a count on the book, seeded from the demo
figures and **maintained from paid orders in Phase 2**. This phase provides the
writer even though nothing reaches `Paid` until Phase 3:

- `Pending → Paid` increments `Book.SalesCount` by one per order item, in the
  same transaction as the status change.
- `Paid → Refunded` decrements, floored at zero. It is a popularity signal on a
  shelf, not an accounting figure, and a title bought and refunded forty times
  should not be promoted to the home page.
- A replayed webhook does not double-count, because it is a `Paid → Paid` no-op
  that returns before touching anything.

### 1.8 Schema delivery

One EF migration, `OrdersAndCustomers`, after `20261020112732_InitialCatalogue`.

- **Three new tables only.** No column is added to `Books` or `Categories` and
  no existing row is touched, so there are no defaults to backfill and nothing
  that must run inside the `app_offline` window beyond the normal apply.
- CI already generates `dotnet ef migrations script --idempotent` as an
  artefact; a purely additive migration stays correct under that script with no
  special handling.
- `NexusApp` (the integration fixture) does `EnsureDeleted` → `Migrate` → seed,
  so the new tables arrive free.
- **No order or customer data is seeded, in any environment.** The demo's
  `ORDERS`, `CUSTOMERS` and `DOWNLOADS` arrays stay where they are in
  `admin-demo.js` until Phase 7 replaces the admin screens. Seeding fake orders
  into a real database makes the Phase 7 dashboard lie and makes the Phase 3
  reconciliation sweep pick up rows that have no charge behind them.
- Review the generated migration for `decimal(18,2)` before committing it. EF's
  default scale for `decimal` is two. Every money property has an explicit
  `HasColumnType("decimal(18,3)")` in `OnModelCreating`; if one is missed, the
  column is created at two decimals, `6.500` silently stores as `6.50`, and in
  Phase 3 every Tap signature fails. This is the highest-value thirty seconds in
  the phase.

---

## 2. Application

New namespace `Nexus.Application.Orders`, mirroring `Nexus.Application.Catalogue`:
DTOs and interfaces plus the pure logic in `Nexus.Application`, the
`DbContext`-touching implementation in `Nexus.Infrastructure.Orders`.

### 2.1 `PricingService` — pure, no database

`src/Nexus.Application/Orders/PricingService.cs`. Takes lines that have **already
been read from the database** and returns a quote. It never sees an `HttpContext`,
a `DbContext` or a request DTO, which is what makes the three-decimal tests cheap
to write and impossible to accidentally bypass.

```csharp
public sealed record PricedLine(int BookId, decimal UnitPriceKwd,
                                string TitleEn, string TitleAr,
                                string AuthorEn, string AuthorAr);

public sealed record PriceQuote(
    string CurrencyCode,            // "KWD"
    IReadOnlyList<PricedLine> Lines,
    decimal SubtotalKwd,
    decimal VatRate,                // 0
    decimal VatAmount,              // 0
    decimal TotalKwd,
    decimal FxRate,                 // 1
    decimal TotalCharged);          // == TotalKwd

PriceQuote Quote(IReadOnlyList<PricedLine> lines);
```

Rules:

- Throws `ArgumentException` on an empty line set. An empty cart is a validation
  failure at the service boundary (§2.3), not a KWD 0.000 order; the engine
  refuses to produce one so no caller can.
- Throws on any `UnitPriceKwd <= 0`. D7 forbids free books at publish, so this
  is unreachable through the catalogue — which is exactly why it is asserted
  here. A bad row, a bad import or a bad admin edit must not become a
  zero-amount charge in Phase 3.
- `Money` is the arithmetic type inside: each line becomes `Money.Kwd(price)`,
  rounded once at `Currency.Kwd.Scale`; `Sum` produces the subtotal; the total
  is rounded once more. Prices are already stored at three decimals so the
  per-line round is a no-op today, and it stays in so a future discount or
  proration cannot introduce a fourth decimal unnoticed.
- `VatRate`, `VatAmount` and `FxRate` are literal constants here and only here.
  This method is the single edit a UAE launch makes to pricing. There is **no
  conversion step and no VAT step** — not a step that multiplies by one and
  adds zero, but no step at all.
- Deterministic, allocation-light, no clock, no randomness, no I/O. Everything
  that varies per request lives in `OrderService`.

### 2.2 `IOrderService` / `OrderService`

Interface in `Nexus.Application/Orders`, implementation in
`Nexus.Infrastructure/Orders`, registered scoped in `AddNexusInfrastructure`
alongside `ICatalogueService`.

```csharp
Task<CreateOrderResult> CreateAsync(CreateOrderRequest request, string? idempotencyKey, CancellationToken ct);
Task<OrderDto?>         GetByIdAsync(int orderId, CancellationToken ct);

// Written now because Phase 3 and Phase 7 must not invent their own.
Task<bool> MarkPaidAsync(int orderId, PaymentFacts facts, CancellationToken ct);
Task<bool> MarkFailedAsync(int orderId, string detail, CancellationToken ct);
Task<bool> MarkCancelledAsync(int orderId, string detail, CancellationToken ct);
Task<bool> MarkRefundedAsync(int orderId, string detail, CancellationToken ct);
```

The four `Mark*` methods have no HTTP surface in Phase 2 and are unreachable
from the storefront. They exist now so Phase 3's webhook handler is "verify the
signature, re-check amount and currency, call `MarkPaidAsync`" — the payment
code should contain no order logic of its own. Each returns `false` rather than
throwing when the transition is refused, because a refused transition on a
replay is the normal case (PM-6), not an exception.

#### `CreateAsync`, step by step

This is where PM-3 is satisfied, so the order of operations is part of the
contract:

1. **Validate shape** (§2.3). Reject before touching the database.
2. **Canonicalise the item list**: distinct, sorted ascending. Duplicates are
   collapsed silently, not rejected — ST-5 means the client should never send
   one, and punishing a buyer for a client-side bug when the correct order is
   unambiguous is the wrong trade.
3. **Idempotency check** (§2.4).
4. **Read the books**: one query, `WHERE Id IN (@ids) AND IsPublished`,
   `AsNoTracking`. The `IsPublished` filter is in the query, not applied
   afterwards, so an unpublished title cannot be priced even by mistake.
5. **Reject on any missing id** with the list of unavailable ids. A requested id
   that did not come back is unknown, unpublished or withdrawn, and the response
   does not distinguish between them — CAT-20 established that the catalogue
   does not confirm the existence of unpublished ids, and the order endpoint
   must not become the oracle the catalogue refuses to be.
6. **Price**: map rows to `PricedLine`, call `PricingService.Quote`. **This is
   the only place a total comes into existence.** The request DTO has no money
   field, so there is nothing to compare against and nothing to override.
7. **Resolve the customer** (§2.5).
8. **Generate the order number** (§1.5).
9. **Write** `Order` + `OrderItem`s in one `SaveChangesAsync`, inside an explicit
   transaction with the customer upsert, retrying the order number up to three
   times on a unique violation.
10. **Return** `CreateOrderResult` carrying the full `OrderDto` and the opaque
    receipt the controller turns into a cookie (§3.2).

Note what is *not* in this list: no call to Tap, no email, no download grant, no
`SalesCount` change. A `Pending` order is a record of intent and nothing else.

### 2.3 Validation

| Field | Rule | Error code |
| --- | --- | --- |
| `items` | Present, 1–20 entries, each a positive `int` | `cart_empty`, `cart_too_large`, `invalid_items` |
| `items` | Every id resolves to a published book | `items_unavailable` (409) |
| `name` | Trimmed length 2–128 | `invalid_name` |
| `email` | Trimmed, ≤ 256, parses as an address | `invalid_email` |
| `language` | `en` or `ar`; anything else **silently becomes `en`** | — |

The cap of twenty books per order is a denial-of-service bound, not a product
rule: each item is a PDF that Phase 4 will watermark and stream, and twenty is
comfortably more than any real order while keeping a malicious cart from asking
for a hundred-item stamping job. Flagged in §6 as a number the product owner may
want to change; nothing depends on the exact value.

Email validation is deliberately loose — a single `@`, a dot in the domain, a
length bound. Clever regexes reject valid addresses, and the address is proved
in Phase 5 by a delivery email that either arrives or does not. `language` does
not error because it comes from our own storefront's `state.lang`; an unexpected
value is our bug, and failing a purchase over it is the wrong response.

### 2.4 Double submission and idempotency

A buyer who double-taps "Place order" on a slow 3G connection must not get two
`Pending` orders, two Tap charges in Phase 3 and two support tickets.

`POST /api/orders` accepts an optional `Idempotency-Key` header: a UUID the
storefront mints once per checkout attempt and keeps in `sessionStorage` until
the order succeeds. The server stores it on the `Order` behind a unique filtered
index. A repeat with the same key returns `200` and the **existing** order rather
than `201` and a new one — including when the items or the prices have changed
in between, because the second request is a retry of the first, not a new
intent.

The key is not a secret and does not authorise anything: it is scoped by the
unique index alone, and the worst a guessed key achieves is a collision that
fails that buyer's checkout. This is stated because the repository is public and
someone will read it looking for exactly this.

### 2.5 Guest versus signed-in customer

Phase 2 has no Identity (Phase 6), so every order is a guest order. The seam is
built now so Phase 6 adds a registration flow rather than a rewrite.

`ICurrentCustomer` — `Task<int?> GetCustomerIdAsync()` — resolved from
`HttpContext`, returning `null` throughout Phase 2.

| Case | Behaviour |
| --- | --- |
| **Guest, email unseen** | Create a `Customer` with `IdentityUserId = null`, `Country = "KW"`, `PreferredLanguage` from the request |
| **Guest, email matches an existing guest row** | Reuse that row. Update `Name` and `PreferredLanguage` — the most recent checkout is the best information we have about a guest |
| **Guest, email matches a *registered* customer (Phase 6+)** | Reuse the row so the order appears in their library when they next sign in. **Do not modify `Name`, `PreferredLanguage` or any other profile field** from an unauthenticated request, and **do not tell the caller the account exists** — no "this email is registered, please sign in". That is account enumeration, on a public-source site, from an anonymous endpoint |
| **Signed-in customer (Phase 6+)** | `CustomerId` comes from the session. The request's `name` and `email` are **ignored entirely**, not merely preferred against. An authenticated request that lets the body override the identity is the same class of bug as a client-supplied total |

Matching is on `NormalisedEmail`. Guest-order claiming (AC-7) stays Phase 6 and
needs nothing more than the unique index this phase creates.

### 2.6 Where the currency scale is applied

| Place | Form | Who |
| --- | --- | --- |
| Storage | `decimal(18,3)` columns | §1.3 |
| Arithmetic | `Money` over `Currency.Kwd.Round`, twice per quote | `PricingService` |
| JSON responses | Plain JSON numbers, unformatted | §3 |
| Storefront display | `toFixed(3)` in the existing `price()` helper | unchanged from Phase 1 |
| **Tap charge amount** | `Currency.Kwd.Format(order.TotalCharged)` → `"16.250"` | Phase 3, the only string-formatted amount |

The API returns `16.25`, not `"16.250"`, because `System.Text.Json` writes a
decimal that way and because `BookDto.Price` already does exactly this and the
storefront already renders it at three decimals. Consistency with the shape
phase 1 established beats a cosmetically tidier payload. The one place where the
string form is load-bearing is the Tap amount and its signature, and that is
produced by `Currency.Format`, never by a JSON serialiser.

---

## 3. Contracts

Two public endpoints. Both live in
`src/Nexus.Web/Controllers/OrdersController.cs`, `[Route("api/orders")]`,
following `CatalogueController`'s shape.

### 3.0 Errors

Problem details (RFC 7807) with one extension member, `code`, which is the
stable machine value:

```json
{ "status": 409, "title": "Items unavailable", "code": "items_unavailable",
  "unavailableBookIds": [7, 19] }
```

**The server never returns customer-facing prose.** `title` is English, for logs
and developers. The storefront maps `code` to an i18n key it already owns
(§3.4). This is not laziness about rule 4 — it is the only arrangement where
every user-visible string lives in `i18n.js` with its Arabic twin beside it. A
server-rendered message needs a second translation home, an `Accept-Language`
negotiation, and a way to keep the two homes in step; an unknown `code` in the
client falls back to a generic bilingual message that already exists.

### 3.1 `POST /api/orders` — create a pending order

**Auth:** anonymous. **Rate limit policy:** `orders` (named now, configured in
Phase 8). **Optional header:** `Idempotency-Key: <uuid>`.

Request:

```json
{
  "items": [3, 11, 7],
  "name": "فاطمة الصايغ",
  "email": "fatima.a@example.com",
  "language": "ar"
}
```

There is no `price`, `total`, `subtotal`, `currency`, `quantity`, `vat` or
`country` field, and unknown members are **ignored**, not rejected — an older
cached copy of the storefront sending a stale field must still be able to buy a
book. An integration test asserts that a body carrying `"total": 0.001` produces
an order at the catalogue total.

`201 Created`, `Location: /api/orders/current`:

```json
{
  "orderNumber": "KW-251021-7F3K2M",
  "status": "Pending",
  "currency": "KWD",
  "language": "ar",
  "createdAt": "2026-10-21T08:14:02+00:00",
  "items": [
    { "bookId": 3,  "unitPrice": 6.5,  "titleEn": "The Patient Portfolio", "titleAr": "المحفظة الصبورة" },
    { "bookId": 7,  "unitPrice": 4.75, "titleEn": "…", "titleAr": "…" },
    { "bookId": 11, "unitPrice": 5.0,  "titleEn": "…", "titleAr": "…" }
  ],
  "subtotal": 16.25,
  "total": 16.25,
  "totalCharged": 16.25
}
```

`vatRate` and `vatAmount` are **not serialised**. They exist on the entity as a
record of fact; putting a zero VAT field in the public payload invites a
storefront to render a zero VAT row, which PM-10 forbids. Phase 7's admin DTO
may include them.

Side effect: sets the order cookie (§3.2).

| Status | `code` | When |
| --- | --- | --- |
| 200 | — | Replay of a known `Idempotency-Key`; body is the original order |
| 400 | `cart_empty` | `items` absent or empty |
| 400 | `cart_too_large` | more than 20 items |
| 400 | `invalid_items` | a non-positive or non-integer id |
| 400 | `invalid_name` | name missing or outside 2–128 |
| 400 | `invalid_email` | email missing or unparseable |
| 409 | `items_unavailable` | one or more ids are unknown, unpublished or withdrawn; `unavailableBookIds` lists them |
| 429 | `too_many_requests` | rate limit (Phase 8) |
| 500 | `order_failed` | including three order-number collisions |

### 3.2 `GET /api/orders/current` — the order just placed

**Auth:** the order cookie. Returns the same `OrderDto` as §3.1, with `status`
re-read (so it will say `Paid` here once Phase 3 lands).

| Status | `code` | When |
| --- | --- | --- |
| 200 | — | |
| 404 | `no_order` | No cookie, an unreadable cookie, or the order no longer exists |

**The cookie, not a token in the URL.** On creation the server sets
`nexus_order`: `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, two-hour expiry,
value = the order id protected with `IDataProtector` (purpose
`"Nexus.Orders.Receipt"`). The DataProtection key ring is already persisted to
`App_Data/dpkeys` in `Program.cs`, so this survives a deploy.

Why this rather than `success.html?o=KW-…&e=fatima.a@example.com`, which is what
the demo does:

- The demo's URL puts a **customer's email address** into browser history, the
  `Referer` header of every outbound link on the page, and any screenshot of a
  support request. That is the kind of leak that is free to avoid now and
  awkward to retract later.
- A URL parameter is shareable; an `HttpOnly` cookie is not readable by script
  and does not travel in a link.
- `SameSite=Lax` still arrives on the top-level GET navigation back from Tap's
  hosted page in Phase 3, which is exactly the case it must survive.
- It keeps the order number out of the authorisation path entirely, which is
  what §1.5 promised.

It holds one order — the most recent. A buyer who places two orders in one
session can see the second on the success page and the first only from Phase 6's
order history or their delivery email. Accepted; flagged in §6.

Degradation: if the key ring is missing or rotated, the cookie fails to unprotect
and the endpoint returns `404 no_order`. The success page then shows the "check
your email" state rather than throwing a 500. This path must be tested, because
on a shared SmarterASP pool it is a recycle away.

### 3.3 Reserved, not built in this phase

Named so `storefront-dev` does not build against them and Phase 3 does not
invent a different spelling:

- `GET /api/orders/{orderNumber}` — Phase 6, authorised by the customer session,
  `404` (never `403`) for an order that is not theirs.
- `POST /api/payments/tap/session` — Phase 3. **This differs from PLAN §6.1**,
  which has a single `POST /api/checkout` that creates the order and the charge
  together. Splitting them is better: the order must exist before a charge can
  reference it; a buyer retrying payment on an existing `Pending` order must not
  create a second order (and therefore a second order number, a second Tap
  reference and a second row for the operator to reconcile); and the whole
  pricing path is testable without touching Tap. For `tap-payments` to confirm
  when it designs Phase 3.
- `POST /api/orders/{orderNumber}/cancel` — Phase 7, admin.

### 3.4 Storefront

`storefront-dev` owns everything under `wwwroot`. The markup stays as close to
current as possible; the changes below are the minimum that makes the pages
honest.

**`cart.html`** — the VAT row goes. It is currently
`rate = state.cur==='AED' ? 0.05 : 0`, which renders "VAT (0%) KD 0.000" on
every cart. CAT-25 required no VAT row anywhere and this one was missed in Phase
1; PM-10 forbids it outright. Subtotal and total remain and are equal.
A cart id that no longer resolves in `BOOKS` (unpublished between visits —
requirements edge case 8) is dropped from the rendering with a single line of
bilingual copy saying so, rather than throwing on `book(id).price`.

**`checkout.html`** — two deletions and one new call.

- **The fake card fieldset is deleted in this phase**, not Phase 3 as PLAN §5
  schedules it. Once this page creates a real order, a form asking for a card
  number that goes nowhere stops being a harmless mock-up. `co_card`, `co_exp`,
  `co_cvv`, `co_holder` and `co_pay` become unused keys.
- **The country select is deleted.** Its options are Kuwait and the UAE; PRD §4
  removes country selection from checkout, and `Customer.Country` is set
  server-side to `KW` (D2.3).
- The button calls `Orders.create({ items, name, email, language })` with the
  `Idempotency-Key` from `sessionStorage`, disables itself while in flight, and
  on `201` clears `state.cart` and navigates to `success.html` with **no query
  string**.
- On `409 items_unavailable`: remove those ids from `state.cart`, re-render, and
  show `err_unavailable_t` / `err_unavailable_d`. Do not auto-retry — the buyer
  is about to pay and the total just changed.
- **Price-change re-confirmation.** The client never sends a total, so it cannot
  be "wrong"; but the page can still compare the total it displayed against
  `total` in the response. If they differ, show `err_price_changed` with the new
  total and require a second click before continuing to payment. Flagged in §6
  as the one UX call here the product owner may want to overturn.

**`success.html`** — reads `GET /api/orders/current` instead of
`localStorage.lib` and the `?o=&e=` parameters. Shows the order number inside
`<bdi>` (§1.5), the line items, the total, and `ok_status_pending` while the
order is `Pending`. On `404 no_order`, the existing "we emailed you the link"
copy stands on its own.

**`library.html`** — **unchanged, and therefore temporarily empty.** Checkout
currently fabricates `state.lib` entries so the library looks populated; that
fabrication is deleted with the fake payment, and real entitlements arrive in
Phase 4. An honestly empty library in staging for one phase is better than a
page that claims a customer owns books they have not paid for. Called out so it
is read as a known consequence, not a regression.

**`assets/js/orders.js`** — new, mirroring `api.js`: `Orders.create(...)`,
`Orders.current()`, and `Orders.errorKey(code)` mapping a `code` to an i18n key
with a generic fallback. Loaded by `cart.html`, `checkout.html` and
`success.html` only.

#### New i18n keys — EN and AR in the same change

| Key | EN | AR |
| --- | --- | --- |
| `co_place` | Place order | تأكيد الطلب |
| `co_placing` | Placing your order… | جارٍ تأكيد الطلب… |
| `co_name_req` | Enter your full name. | أدخل اسمك الكامل. |
| `co_email_req` | Enter a valid email address. | أدخل بريدًا إلكترونيًا صحيحًا. |
| `err_order_t` | We could not create your order. | تعذّر إنشاء طلبك. |
| `err_order_d` | Nothing has been charged. Please try again. | لم يتم خصم أي مبلغ. يرجى المحاولة مرة أخرى. |
| `err_unavailable_t` | Some books are no longer on sale. | بعض الكتب لم تعد معروضة للبيع. |
| `err_unavailable_d` | We removed them from your cart. Check the total before you continue. | أزلناها من سلتك. راجع الإجمالي قبل المتابعة. |
| `err_price_changed` | A price changed while you were shopping. Please check the new total. | تغيّر أحد الأسعار أثناء تسوقك. يرجى مراجعة الإجمالي الجديد. |
| `err_cart_too_large` | A cart holds up to {n} books. | تتسع السلة حتى {n} كتابًا. |
| `err_rate` | Too many attempts. Wait a moment and try again. | محاولات كثيرة. انتظر لحظة ثم حاول مرة أخرى. |
| `cart_gone` | One book in your cart is no longer on sale and has been removed. | أحد الكتب في سلتك لم يعد معروضًا للبيع وقد تمت إزالته. |
| `ok_order_no` | Order number | رقم الطلب |
| `ok_status_pending` | Awaiting payment | في انتظار الدفع |

`err_cart_too_large` uses the existing `tf()` token helper, so the cap is not
hardcoded in copy — the same mistake CAT-28 fixed for "twenty titles".

Keys **removed from both dictionaries** with the markup that used them:
`vat`, `co_card`, `co_exp`, `co_cvv`, `co_holder`, `co_pay`, `co_country`.
`co_secure` ("This demo does not charge anything") is reworded in Phase 3, not
here, because this phase genuinely does not charge anything.

### 3.5 Admin

Nothing. The admin screens are Phase 7 and still read `admin-demo.js`. Recorded
so the gap is deliberate: an operator cannot see orders until Phase 7, which is
acceptable only because nothing can reach `Paid` until Phase 3 and a Phase 3
order can be inspected in SQL. Phase 7 will need `OrderDto` plus `vatRate`,
`vatAmount`, `fxRate`, `statusDetail` and the Tap references, which is why they
are on the entity now.

---

## 4. Integration points

**Tap** — none in this phase. The seam: `Order.TotalCharged` and
`Order.Currency` are what Phase 3 formats through `Currency.Format` into the
charge amount, and `Order.OrderNumber` is what it puts in
`reference.transaction` and `reference.order`. `TapChargeId`,
`TapPaymentReference`, `TapGatewayReference` and `PaymentMethod` are created by
this phase's migration so Phase 3 adds no columns to `Order`. `MarkPaidAsync`,
`MarkFailedAsync` and `MarkRefundedAsync` are the only ways payment code may
change an order. Detail belongs to `tap-payments`; §3.3 flags the one place this
design departs from PLAN §6.1.

**PDF storage and stamping** — none. Phase 4 reads `OrderItem` to decide which
grants to issue, and takes the buyer name, email and order number for the
watermark from `Customer` and `Order.OrderNumber`.

**Email** — none. Phase 5 reads `Order.Language` (not
`Customer.PreferredLanguage`) to choose the language of the delivery email, for
the snapshot reason in §1.3.

**Background jobs** — none. The Phase 3 reconciliation sweep is already indexed
for: `(Status, CreatedAt)` answers "`Pending`, older than thirty minutes".

**Rate limiting** — the `orders` policy is named on the endpoint now and
configured in Phase 8, so Phase 8 is a `Program.cs` change with no controller
edit.

---

## 5. File ownership

Disjoint. The contract they meet at is §3 — the JSON in §3.1 and §3.2 and the
error codes in §3.0. Neither needs the other to be finished.

### `backend-dev` owns

New:

```
src/Nexus.Domain/Money.cs
src/Nexus.Domain/Entities/Customer.cs
src/Nexus.Domain/Entities/Order.cs            (+ OrderStatus, TransitionBlocker)
src/Nexus.Domain/Entities/OrderItem.cs
src/Nexus.Application/Orders/OrderDtos.cs
src/Nexus.Application/Orders/IOrderService.cs
src/Nexus.Application/Orders/IPricingService.cs
src/Nexus.Application/Orders/PricingService.cs
src/Nexus.Application/Orders/OrderNumber.cs
src/Nexus.Application/Orders/ICurrentCustomer.cs
src/Nexus.Infrastructure/Orders/OrderService.cs
src/Nexus.Infrastructure/Persistence/Migrations/*_OrdersAndCustomers.cs
src/Nexus.Web/Controllers/OrdersController.cs
src/Nexus.Web/Orders/OrderCookie.cs           (IDataProtector wrapper)
src/Nexus.Web/Orders/AnonymousCurrentCustomer.cs
tests/Nexus.UnitTests/MoneyTests.cs
tests/Nexus.UnitTests/PricingServiceTests.cs
tests/Nexus.UnitTests/OrderNumberTests.cs
tests/Nexus.UnitTests/OrderStateMachineTests.cs
tests/Nexus.IntegrationTests/OrderApiTests.cs
```

Modified:

```
src/Nexus.Infrastructure/Persistence/NexusDbContext.cs   three entity configurations
src/Nexus.Infrastructure/DependencyInjection.cs          IOrderService, IPricingService
src/Nexus.Web/Program.cs                                 rate-limit policy name
```

### `storefront-dev` owns

```
src/Nexus.Web/wwwroot/cart.html
src/Nexus.Web/wwwroot/checkout.html
src/Nexus.Web/wwwroot/success.html
src/Nexus.Web/wwwroot/assets/js/orders.js        new
src/Nexus.Web/wwwroot/assets/js/i18n.js          both dictionaries
src/Nexus.Web/wwwroot/assets/js/site.js          cartTotal(), unresolvable-id guard
src/Nexus.Web/wwwroot/assets/css/site.css        only if a new state needs it
```

### The collision list

Three files other phases also touch, so they are `backend-dev`'s alone in this
phase and every edit should be small enough to rebase:

- `src/Nexus.Infrastructure/Persistence/NexusDbContext.cs` — Phases 4, 5, 6, 7
  all add entity configurations here. Append a block per entity; do not reorder
  the existing `Category` and `Book` blocks.
- `src/Nexus.Infrastructure/DependencyInjection.cs` — one registration line per
  phase.
- `src/Nexus.Web/Program.cs` — Phases 6 and 8 both rewrite the middleware
  pipeline. Keep this phase's change to the rate-limit policy declaration.

`i18n.js` is **storefront-dev's alone**. `backend-dev` adds no string to it —
that is the whole point of returning error codes (§3.0), and it is why the two
can work in parallel on the same feature without touching the same file.

Migration files are generated, not written: whoever runs `dotnet ef migrations
add` owns the output, and only `backend-dev` runs it in this phase.

---

## 6. Risks and decisions

### Decisions

| # | Decision | Why, and what it rules out |
| --- | --- | --- |
| D2.1 | **The client sends no money at all** — no price, total, currency or quantity field exists in the request | The strongest reading of PM-3. Rules out a "compare the client total to ours" check, which is a comparison someone eventually relaxes. A price change is detected by the *client* comparing what it displayed to the authoritative total in the response, so the correction flows the right way |
| D2.2 | **Order number `KW-{yyMMdd}-{6 Crockford base32}`, 16 chars**, date in Kuwait time, from a CSPRNG, unique index plus three retries | `KW-#####` is exhausted at 100k, collides when random, leaks volume when sequential, and carries no date. Rules out a database sequence. See §1.5 |
| D2.3 | **No country field at checkout.** `Customer.Country` is set to `KW` server-side | PRD §4 removes country selection; the demo's Kuwait/UAE select is the currency switcher wearing a hat. Keeps the column for a UAE launch |
| D2.4 | **`Failed → Paid` and `Cancelled → Paid` are legal transitions** from a verified webhook | The webhook is the only authoritative signal (CLAUDE.md rule 2); reconciliation is a guess. Refusing this transition means taking money and delivering nothing, silently |
| D2.5 | **Order state is readable only via an `HttpOnly` DataProtection cookie**, never by order number in a URL | Keeps a customer's email out of browser history and `Referer`; keeps the order number out of the authorisation path, which a public repository requires |
| D2.6 | **The fake card form and country select are deleted in Phase 2**, not Phase 3 | Once the page creates a real order, a dead card form is a defect. One line of PLAN §5 moves a phase earlier |
| D2.7 | **Errors return codes, not prose**; all copy stays in `i18n.js` | One translation home, enforced by the file split (§5). Rules out `Accept-Language` negotiation on the API |
| D2.8 | **No `Quantity` column**; `(OrderId, BookId)` is unique | ST-5 in the schema. Removes multiplication from pricing entirely |
| D2.9 | **`PricingService` is pure** — no `DbContext`, no clock, no randomness | Follows `ArabicSearch`. Makes the three-decimal tests trivial and unskippable |
| D2.10 | **Both languages snapshotted on `OrderItem`**, plus author | An Arabic invoice reprinted years later must not depend on the `Books` row |
| D2.11 | **No orders or customers seeded, anywhere** | Fake rows would make the Phase 7 dashboard lie and the Phase 3 reconciliation sweep pick up charge-less orders |
| D2.12 | **`Customer.PreferredCurrency` is not added** | The UAE-deferral deal preserves facts of record, not preferences for a control that does not exist |

Worth an ADR in `docs/adr/` — D2.2 (the order number is in Tap references, PDF
watermarks and support scripts within two phases), D2.4 (the one transition
whose absence silently loses money) and D2.5 (changing it later means changing
every link in every delivery email). This task was scoped to one file, so they
are listed rather than written.

### Risks

| Risk | If it happens | Mitigation |
| --- | --- | --- |
| **A money column is generated as `decimal(18,2)`** — EF's default when `HasColumnType` is missed | `6.500` stores as `6.50`; in Phase 3 every Tap signature fails and no order ever completes, with nothing visibly wrong | Explicit `decimal(18,3)` on every money property; **read the generated migration for `18,2` before committing**; an integration test round-trips a price ending in a non-zero third decimal |
| **A late capture arrives after reconciliation marked the order `Failed`** | Customer is charged and gets nothing | D2.4 makes the transition legal; a unit test walks the whole transition table including this edge |
| **Double submission** on a slow connection | Two orders, two charges in Phase 3, a refund and a support ticket | `Idempotency-Key` + unique filtered index (§2.4); the button disables itself in flight |
| **A stale cart holds a book unpublished since the last visit** | A 500, or an order for a withdrawn title | `IsPublished` is in the query, not applied after; `409 items_unavailable` with the ids; the cart page drops unresolvable ids instead of dereferencing them |
| **The order number is treated as an authorisation token** by a later phase | Anyone can enumerate orders on a store whose source is public | No endpoint in §3 accepts it as proof of anything; Phase 6's lookup is session-scoped and returns `404`, not `403` |
| **Bidi scrambles `KW-251021-7F3K2M` on the Arabic pages** | The buyer reads back a mangled number to support | `<bdi>` / `dir="ltr"` on every render; checked in the RTL pass, which has caught exactly this class of bug before |
| **DataProtection key ring lost on a pool recycle** | `GET /api/orders/current` stops resolving | Keys already persist to `App_Data/dpkeys` (Phase 0); the endpoint returns `404 no_order` and the success page falls back to "check your email" rather than 500. Tested |
| **The `Books` row changes between the price read and the write** | An order at a price the buyer never saw | The window is one `SaveChangesAsync`; the snapshot makes whatever was read the permanent record; the storefront re-confirms on any difference from what it displayed (§3.4) |
| **An unauthenticated checkout overwrites a registered customer's profile** | Account takeover by typing someone's email | §2.5: a row with `IdentityUserId` set is reused but never mutated, and its existence is never disclosed |
| **Anonymous write endpoint abused** | Thousands of `Pending` rows, twenty stamping jobs each | 20-item cap; `orders` rate-limit policy named now, configured in Phase 8; Phase 3's sweep cancels abandoned `Pending` orders |

### Tests

**Unit** (`Nexus.UnitTests`, no database):

- `Money` — addition, `Sum` over empty and single sequences, `Rounded` at scale
  3, and that mixing KWD with AED **throws** rather than converting.
- `PricingService` — one line; many lines; **a total that is only correct at
  three decimals** (`2.335 × 3 = 7.005`, which two-decimal rounding turns into
  `7.01` and a wrong signature); an empty line set throws; a zero or negative
  unit price throws; `VatRate`, `VatAmount` are `0` and `FxRate` is `1`;
  `TotalCharged == TotalKwd`.
- `OrderNumber` — matches `^KW-\d{6}-[0-9A-HJKMNP-TV-Z]{6}$`; length ≤ 20; no
  `I`, `L`, `O` or `U`; 100,000 generations produce no duplicate; the date part
  is Kuwait time, pinned with a fixed clock across the 21:00–00:00 UTC window
  where UTC and Kuwait disagree on the day.
- `Order.TransitionBlocker` — the whole table in §1.6 as a `[Theory]`, legal and
  illegal, **including `Failed → Paid` and `Cancelled → Paid` as allowed** and
  `Refunded → anything` and `* → Pending` as refused; `Paid → Paid` is a no-op,
  not an error.
- `SalesCount` — increments on first `Paid`, not on a repeat, decrements on
  `Refunded`, floors at zero.

**Integration** (`Nexus.IntegrationTests`, real SQL Server — the decimal scale
and the NVARCHAR behaviour are exactly what an in-memory provider gets wrong):

- A valid cart creates a `Pending` order whose `TotalKwd` equals the sum of the
  catalogue prices, and whose `OrderItem.UnitPriceKwd` matches `Book.PriceKwd`.
- **A body carrying `"total": 0.001` and `"unitPrice": 0.001` produces the
  catalogue total.** The headline PM-3 test.
- **Editing a book's price after the order leaves the order's total and line
  price unchanged.** The headline snapshot test.
- A price ending in a non-zero third decimal round-trips through the database
  unchanged (`6.505` is `6.505`, not `6.50` or `6.51`).
- An unpublished book id → `409 items_unavailable` listing it; an unknown id →
  the same response, indistinguishable from the unpublished case.
- Empty items → `400 cart_empty`; 21 items → `400 cart_too_large`; a duplicated
  id → one `OrderItem`, `201`.
- The same `Idempotency-Key` twice → one order row, second response `200` with
  the original order number.
- An Arabic name and an Arabic title snapshot survive the round trip
  (`فاطمة الصايغ` is not `??????`).
- `GET /api/orders/current` with the cookie → the order; without it → `404`;
  with a cookie protected by a different key → `404`, not a 500.
- A guest checkout with an email already on a `Customer` row reuses it and does
  not create a second row; the response does not differ from a first-time email.
- `MarkPaidAsync` twice → one increment of `SalesCount`, `PaidAt` unchanged on
  the second call.

**What would otherwise ship broken** — the three-decimal total, the snapshot
surviving a price edit, the late capture after a `Failed` mark, the duplicate
submission, and the stale cart holding an unpublished book. Each has a named
test above.

---

## 7. Open questions

Not blocking the build; each has a stated default that ships if nobody answers.

- **Twenty books per order** — a denial-of-service bound, not a product rule.
  Default: 20.
- **Re-confirm on a price change, or proceed at the server total?** Default:
  re-confirm with a second click. The argument against is one more step before
  payment on a mobile checkout.
- **One order in the cookie.** A buyer placing two orders in a session sees only
  the second on the success page until Phase 6. Default: accept.
- **Abandoned `Pending` orders** — Phase 3 cancels them after 30 minutes. How
  long are they then kept? Default: forever, and revisit in Phase 8 with the
  data-retention work.
- **`Customer.Country` fixed to `KW`.** Confirming that a UAE-resident buyer
  paying in KWD is recorded as `KW` and corrected at the UAE launch, rather than
  given a field to fill in now.
- **`POST /api/payments/tap/session` instead of PLAN §6.1's single
  `POST /api/checkout`** (§3.3). For `tap-payments` to confirm before Phase 3
  starts.
