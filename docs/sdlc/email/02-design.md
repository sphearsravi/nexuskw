# Transactional email — Phase 5 design

SDLC phase 2 (design) for **Phase 5 — Email** in PRD §12 and PLAN §5.
Written 2026-10-20 against the code as merged at the end of Phase 1.

Scope reminder, already decided and not reopened here: **Kuwait only, KWD only at
three decimals in Western digits** (PRD §4, ST-7, CLAUDE.md rule 1); **Arabic is a
first-class edition, not a translation layer** (PLAN §15 decision 6, CLAUDE.md
rule 4); **the webhook is the only authoritative payment signal** (CLAUDE.md
rule 2); **the repository is public** (CLAUDE.md rule 5).

> **This phase is designed ahead of its dependencies.** Phases 2, 3 and 4 are in
> flight in parallel. Everything this design needs from them is listed in §4.1
> as a short, explicit contract. That list is the merge checklist — if an item
> there is not true when Phase 5 lands, Phase 5 is blocked on it, not around it.

---

## 0. What this phase is actually for

The customer has paid. The money has moved, the order is `Paid`, the grants
exist, and `success.html` is already showing them a working download button.
The email exists for the other ninety percent of the relationship: the tab they
closed, the phone that died at the payment screen, the laptop they will open
tomorrow, and the "I never got my link" message that PRD §11 names as the
support metric that matters.

Two consequences shape the whole design.

**The email is never load-bearing for delivery.** A buyer whose mail server is
down, whose address bounces, or who mistyped their address must still get their
book — from the success page now, and from their library later (Phase 6). No
failure in this phase may ever leave a paid order undelivered. That is why
sending is decoupled from the payment path rather than merely made reliable.

**Silence is the failure mode to design against.** An email that fails loudly is
a support ticket. An email that never gets queued, or sits in a queue nobody
watches, is a customer who paid and heard nothing. Every path below ends in a
row an operator can see, including the paths where nothing is sent.

---

## 1. Domain

### 1.1 `OutboxEmail`

The transactional outbox. One row per message we intend to send, written in the
same database transaction as the business fact that caused it, and sent later by
a background dispatcher.

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `int` identity | Internal only; never in a URL or an email header |
| `PublicRef` | `NVARCHAR(22)`, unique | Base64url of a GUID. The only identifier that leaves the server — used in the bounce return-path and the admin URLs, so neither discloses how many emails the store has sent |
| `IdempotencyKey` | `NVARCHAR(128)`, **unique** | The exactly-once backstop. See §2.4 |
| `Kind` | `int` (`EmailKind`) | |
| `Language` | `NVARCHAR(2)` | `en` or `ar`. The two-letter code, matching `state.lang` in `site.js` and the `I18N` keys, rather than an enum — the same token addresses the template, the layout direction and the i18n dictionary |
| `ToEmail` | `NVARCHAR(256)` | Stored as entered; matched against suppression in normalised (trimmed, lowercased) form |
| `ToName` | `NVARCHAR(128)` | |
| `Subject` | `NVARCHAR(256)` | Rendered at enqueue |
| `BodyHtml` | `NVARCHAR(MAX)` | Rendered at enqueue. Purged 30 days after `Sent` — see §6 risk 4 |
| `BodyText` | `NVARCHAR(MAX)` | The `text/plain` alternative, also rendered at enqueue |
| `OrderId` | `int?` | FK to `Order`, `DeleteBehavior.Restrict`. Nullable because Phase 6's account emails have no order |
| `CustomerId` | `int?` | FK to `Customer`, `DeleteBehavior.SetNull` |
| `Status` | `int` (`EmailStatus`) | |
| `Attempts` | `int` | |
| `NextAttemptAt` | `datetimeoffset` | Set to "now" at enqueue; moved forward by the retry schedule |
| `LockedBy` | `NVARCHAR(64)?` | Machine + process id of the dispatcher holding the lease |
| `LockedUntil` | `datetimeoffset?` | |
| `SentAt` | `datetimeoffset?` | |
| `LastError` | `NVARCHAR(1024)?` | Truncated. An SMTP server will happily return a kilobyte of diagnostics |
| `LastErrorAt` | `datetimeoffset?` | |
| `ProviderMessageId` | `NVARCHAR(256)?` | Whatever the SMTP server echoed on `250 OK`, for correlating with a provider's logs |
| `PossibleDuplicate` | `bit` | Set when a lease was recovered from a row that was mid-send; see §2.4 |
| `CreatedAt` | `datetimeoffset` | |
| `CreatedBy` | `NVARCHAR(128)?` | `webhook`, `reconciliation`, or the admin's user id for a resend |

```
enum EmailKind
{
    OrderConfirmation = 0,   // receipt and download links, sent once per paid order
    DownloadLinks     = 1,   // links only: admin resend, and Phase 6 customer re-issue
    RefundNotice      = 2,   // reserved, Phase 3 §6.5 — not implemented here
    AccountConfirm    = 10,  // reserved, Phase 6
    PasswordReset     = 11   // reserved, Phase 6
}

enum EmailStatus
{
    Queued     = 0,
    Sending    = 1,
    Sent       = 2,
    Failed     = 3,   // permanent rejection, or the schedule gave up — retryable by hand
    Abandoned  = 4,   // retries exhausted
    Suppressed = 5    // never attempted: bounced address, or transport is off
}
```

Reserving the numbers now matters because the column is an `int`: Phase 6 adding
`AccountConfirm = 3` later would silently reinterpret every abandoned row.

**Indexes**

- `UX_OutboxEmail_IdempotencyKey` — unique on `IdempotencyKey`. This is the
  mechanism, not a nicety.
- `UX_OutboxEmail_PublicRef` — unique.
- `IX_OutboxEmail_Dispatch` on `(Status, NextAttemptAt)` filtered
  `WHERE Status IN (0, 1)`. The dispatcher's claim query runs every fifteen
  seconds forever; a filtered index keeps it reading a handful of rows rather
  than the whole send history.
- `IX_OutboxEmail_OrderId` on `(OrderId, CreatedAt DESC)` — the admin order
  detail, and the resend rate-limit count.
- `IX_OutboxEmail_ToEmail` on `(ToEmail, CreatedAt DESC)` — "what did we send
  this customer", and the repeated-soft-bounce rule.

### 1.2 `EmailSuppression`

| Column | Type | Notes |
| --- | --- | --- |
| `Email` | `NVARCHAR(256)` PK | Normalised: trimmed, `ToLowerInvariant` |
| `Reason` | `int` (`SuppressionReason`) | `HardBounce = 0`, `Complaint = 1`, `RepeatedSoftBounce = 2`, `ManualBlock = 3` |
| `FirstSeenAt`, `LastEventAt` | `datetimeoffset` | |
| `SoftBounceCount` | `int` | |
| `Notes` | `NVARCHAR(512)?` | What the SMTP server actually said, or why the operator blocked it |
| `ClearedAt` | `datetimeoffset?` | |
| `ClearedBy` | `NVARCHAR(128)?` | |

A row with `ClearedAt` set is history, not a block. Suppression is reversible by
an operator because the commonest cause is a customer's mailbox being full for a
week, and a permanent block over a temporary condition is how a store loses a
repeat buyer.

### 1.3 `Setting` — shared, first phase to land creates it

PLAN §4 already lists `Setting`. It does not exist in code yet, and Phases 3, 4
and 5 all need it. **Phase 5 does not own it.** Whichever phase lands first
creates it to exactly this shape; the others add keys only.

| Column | Type | Notes |
| --- | --- | --- |
| `Key` | `NVARCHAR(128)` PK | Dotted, lowercase-first: `email.from.address` |
| `Value` | `NVARCHAR(MAX)` | Plain text, or a DataProtection payload when `IsSecret` |
| `IsSecret` | `bit` | Protected with the `App_Data/dpkeys` ring already wired in `Program.cs` |
| `UpdatedAt` | `datetimeoffset` | |
| `UpdatedBy` | `NVARCHAR(128)?` | |

The `email.*` keys Phase 5 owns:

| Key | Default | Secret |
| --- | --- | --- |
| `email.enabled` | `true` | no |
| `email.from.address` | `orders@nexus.example` | no |
| `email.from.name.en` | `Nexus` | no |
| `email.from.name.ar` | `Nexus` | no |
| `email.replyTo` | *(empty, falls back to from)* | no |
| `email.support.address` | `support@nexus.example` | no |
| `email.bounce.mailbox` | *(empty)* | no |
| `email.resend.perOrderPerHour` | `3` | no |
| `email.resend.perOrderPerDay` | `10` | no |
| `email.resend.perActorPerHour` | `30` | no |
| `email.template.orderConfirmation.subject.en` | §3.4 | no |
| `email.template.orderConfirmation.subject.ar` | §3.4 | no |
| `email.template.orderConfirmation.body.en` | §3.4 | no |
| `email.template.orderConfirmation.body.ar` | §3.4 | no |
| `email.template.downloadLinks.subject.{en,ar}` | §3.4 | no |
| `email.template.downloadLinks.body.{en,ar}` | §3.4 | no |

**No SMTP credential is a `Setting`.** The line is: *content and policy* live in
`Setting`, where the operator edits them; *transport* lives in configuration,
where the server owner sets it. The Tap keys are a `Setting` because the operator
rotates them from the admin screen (PLAN §8); the SMTP host and password are
infrastructure the operator never touches, and `IOptions<SmtpOptions>` with
`ValidateOnStart` fails fast on a typo at deploy time instead of at two in the
morning on the first order.

### 1.4 Schema delivery

One EF migration, `EmailOutbox`, creating `OutboxEmail`, `EmailSuppression` and
— if no other phase has yet — `Setting`, plus the seed of the six default
template keys. Nothing in it is destructive and nothing backfills, so:

- **Nothing needs the `app_offline` window.** The tables are new and no existing
  row changes. The migration can be applied before the deploy or inside the
  window, and a rollback simply leaves three unused tables.
- **Defaults for existing rows:** not applicable; there are none.
- **The CI idempotent script** stays correct because it is generated from the
  final merged model, not per branch. The only ordering hazard is two phases
  generating migrations against different snapshots of `NexusDbContext`; §5.3
  says how that is avoided.
- **Template defaults are seeded, not hardcoded at read time.** A `Setting` row
  the operator can see and edit from day one is better than a magic fallback
  they discover does not exist. Seeding is idempotent in the manner
  `CatalogueSeeder` already establishes: a key that exists is left alone, so a
  redeploy never overwrites an operator's wording.

### 1.5 One entity configuration per file

`NexusDbContext.OnModelCreating` currently holds every entity inline. With four
phases in flight it is a guaranteed merge conflict. Phase 5 proposes — and the
first phase to merge should perform — the mechanical change to
`IEntityTypeConfiguration<T>` classes under
`src/Nexus.Infrastructure/Persistence/Configurations/`, with the context body
reduced to `b.ApplyConfigurationsFromAssembly(typeof(NexusDbContext).Assembly)`.
The existing `Category` and `Book` mappings move across unchanged. After that
each phase adds files instead of editing one.

---

## 2. Application

Namespace `Nexus.Application.Email` for the contracts and DTOs,
`Nexus.Infrastructure.Email` for the implementations, matching the
`Catalogue` split Phase 1 established.

### 2.1 Why sending inside the webhook request is wrong

The webhook handler's job is to decide that money arrived and record it. Putting
an SMTP conversation inside it breaks four things at once:

1. **It makes delivery depend on a third party's uptime.** A TLS handshake to a
   mail host is 200 ms on a good day and a 30-second timeout on a bad one. If it
   throws, the handler either swallows the error — and we have an order that is
   paid with no email and no record that one was wanted — or it fails the
   request, and we have told Tap the payment was not accepted.
2. **Tap retries on anything that is not a prompt 2xx.** A slow mail host turns
   one webhook into five, and PLAN §6.3's "respond 200 quickly" into a lie. Tap
   disables endpoints that keep timing out. The failure mode is: the mail server
   went down, so the store stopped taking orders.
3. **The transaction is held open across a network call to someone else.** The
   row locks on `Order`, `DownloadGrant` and `WebhookEvent` stay taken for the
   duration of the SMTP conversation.
4. **The app pool cannot afford it.** PLAN §10: 32-bit, 512 MB, shared with five
   other applications, and streaming PDFs. A request thread parked on a socket
   during a traffic spike is the recycle that happens mid-download.

So: the handler writes an `OutboxEmail` row **inside the same transaction** that
moves the order to `Paid` and creates the grants, then commits and returns 200.
One commit, so an order cannot be paid with no email queued, and a payment that
rolls back cannot leave an email queued for an order that never completed. A
background dispatcher does the network I/O, fifteen seconds later, where it can
fail safely and be retried.

This is the transactional outbox pattern, and it is the single structural
decision of this phase.

### 2.2 `IEmailOutbox` — enqueue

```
Task<EnqueueResult> EnqueueAsync(EmailRequest request, CancellationToken ct);
```

`EmailRequest` carries `Kind`, `IdempotencyKey`, `Language`, `ToEmail`,
`ToName`, `Subject`, `BodyHtml`, `BodyText`, `OrderId?`, `CustomerId?`,
`CreatedBy`. `EnqueueResult` is
`{ PublicRef, Status, WasAlreadyQueued }`.

Rules, in order:

1. **It does not call `SaveChanges`.** It adds to the ambient `NexusDbContext`
   and returns. The caller's transaction decides whether the row exists. This is
   the one rule that makes §2.1 true, and it is the easiest to get wrong — a
   helpful `SaveChangesAsync` inside this method quietly reintroduces a
   half-committed state.
2. If `email.enabled` is false, or the configured transport is `None`, the row
   is still written, with `Status = Suppressed` and
   `LastError = "sending disabled"`. It is written, not skipped, because the
   admin must be able to see that the customer was not emailed.
3. If the normalised recipient is on the suppression list with no `ClearedAt`,
   the row is written with `Status = Suppressed` and the reason. Same argument.
4. Otherwise `Status = Queued`, `NextAttemptAt = now`.
5. A duplicate `IdempotencyKey` is not an error. See §2.4.

### 2.3 `IOrderEmailService` — composition

```
Task<EnqueueResult> QueueOrderConfirmationAsync(int orderId, CancellationToken ct);
Task<EnqueueResult> QueueDownloadLinksAsync(ResendRequest request, CancellationToken ct);
```

`QueueOrderConfirmationAsync` is what Phase 3's webhook handler calls, inside its
transaction. It:

1. Loads the order with its items and its active grants.
2. Refuses — returning a result, not throwing — if the order is not `Paid`. The
   caller is in a transaction; an exception here would roll back a payment.
3. Picks the language (§2.5).
4. Builds `EmailModel` (§2.6).
5. Renders subject, HTML and text through `IEmailTemplateRenderer`.
6. Enqueues with `IdempotencyKey = $"order-confirmation:{orderId}"`.

`QueueDownloadLinksAsync` is the resend path, called from the admin controller
outside any payment transaction. It additionally:

- Refuses with `NoLiveGrants` when every grant on the order is expired,
  exhausted or revoked, naming the affected books. **Sending a dead link is a
  worse support outcome than refusing to send**: the customer tries it, it
  fails, and now they distrust the next one too. Re-issue is Phase 4's action
  and must happen first.
- Uses `IdempotencyKey = $"download-links:{orderId}:{n}"` where `n` is the count
  of prior `DownloadLinks` rows for the order. A resend is *meant* to be a new
  email; what bounds it is the rate limit (§2.7), not the unique index. The key
  is still unique so that a double-clicked button within the same second
  collides rather than sending twice.

**Money is never recomputed here.** The email renders `Order.TotalKwd` and
`OrderItem.UnitPriceKwd` as stored — the snapshot Phase 2 captured. An email that
disagreed with the invoice would be worse than no email, and a receipt is a
record of what was charged, not a fresh calculation.

### 2.4 Idempotency — one paid order, one email

Four layers, each independently sufficient for the case below it, deliberately
stacked because the cost is one unique index.

| Layer | Owned by | Stops |
| --- | --- | --- |
| `WebhookEvent` unique on (charge id, status) | Phase 3 | The handler body running twice for one Tap delivery |
| `UPDATE … WHERE Status = Pending` affected-rows check before composing | Phase 3 | The handler running for an order the reconciliation job already settled |
| `OutboxEmail.IdempotencyKey` unique index | **Phase 5** | Everything above it being wrong |
| Dispatcher claim by conditional `UPDATE` | **Phase 5** | Two dispatcher instances sending the same row |

Layer 3 is the one this phase guarantees. The insert races at the database, the
loser gets SQL error 2601/2627, and `EnqueueAsync` translates that into
`WasAlreadyQueued = true` and a success result rather than an exception — because
the caller is Tap's webhook, and throwing would turn a successful duplicate
suppression into a 500 and another Tap retry.

Note what this does **not** claim. SMTP has no transaction. If the process dies
between the mail server accepting the message and the `Status = Sent` write, the
lease expires and the row is picked up again. The design handles that by
recovering the lease, retrying once, and setting `PossibleDuplicate` so the
operator can see it happened — not by pretending exactly-once delivery to a
mailbox is achievable. The requirement as stated — *one paid order sends exactly
one email even when Tap retries the webhook* — is about Tap retries, and is
fully met. A duplicate from a crash mid-send is rarer, harmless (the links are
the same links), and recorded.

### 2.5 Language selection

In precedence order:

1. `Customer.PreferredLanguage`, when set. Phase 6 populates it; the column is in
   PLAN §4 and may already exist.
2. `Order.Language` — the interface language the buyer was using at checkout.
3. `en`.

**This needs Phase 2 to capture the checkout language on the order** (§4.1). It
is two characters in a POST body and a column, and without it every guest buyer
gets an English email regardless of the Arabic storefront they just bought
from — which would make PRD §11's "share of orders in Arabic" metric measure a
store that answers in English.

The admin resend takes an explicit language, defaulting to the order's, so "they
wrote in asking in Arabic" is one dropdown rather than a data fix.

### 2.6 `EmailModel`

Everything the templates can reference, resolved before rendering. No lazy
loading inside a renderer that may run in a background thread.

```
EmailModel
  StoreName            string            from email.from.name.{lang}
  Language             "en" | "ar"
  OrderNumber          string            e.g. KW-00042
  PaidAt               DateTimeOffset
  CustomerName         string
  CustomerEmail        string
  TotalKwd             decimal
  Items                [{ Title, UnitPriceKwd }]      title in the chosen language,
                                                      from OrderItem.TitleSnapshot
  Links                [{ Title, Url, ExpiresAt, MaxDownloads, Remaining }]
  LinkExpiryDays       int               from Settings (Phase 4)
  MaxDownloads         int               from Settings (Phase 4)
  LibraryUrl           string
  SupportEmail         string
  PublicBaseUrl        string
```

### 2.7 Resend rate limiting

Two independent limits, both settings-driven, both counted from `OutboxEmail`
rows rather than from an in-memory counter — because an in-memory counter resets
on every app pool recycle, and on this host that is often.

- **Per order:** 3 per rolling hour, 10 per rolling day. Protects the customer
  from a stuck operator clicking a button that seems not to work.
- **Per actor:** 30 per rolling hour across all orders. Protects the *sending
  domain* from a compromised staff account being used to fire mail from our
  address at arbitrary recipients. The domain's reputation is the asset here;
  once it is burned, every customer's link lands in spam.

Both return `429` with `Retry-After` and a bilingual message. Both are audited
whether they succeed or are refused — a wall of refused resends is itself a
signal.

**There is no anonymous resend endpoint in this phase.** The existing demo
`library.html` has a "Email me a new link" button (`lib_resend`), and wiring it
to a public endpoint that takes an email address would make the store an open
relay sending from our domain to anywhere. That button belongs to Phase 6,
behind a customer session, scoped to that customer's own orders.

### 2.8 The dispatcher

`EmailDispatcher : BackgroundService`, registered once, concurrency **one**.

- **Claim:** a single statement, not a select-then-update:
  `UPDATE TOP (10) OutboxEmail SET Status = 1, LockedBy = @instance, LockedUntil = @lease, Attempts = Attempts + 1 OUTPUT inserted.Id WHERE Status = 0 AND NextAttemptAt <= @now` — plus the lease-recovery arm in §2.9. EF Core's `ExecuteUpdate` returns a count rather than the rows, so this is raw SQL through `FromSqlRaw`, and that is deliberate rather than a gap.
- **Cadence:** every 15 seconds when idle; immediately again while a batch came
  back full. A customer waiting on a link should not wait a minute.
- **Serial sends.** Not parallel. Order volume is tens a day, SMTP providers
  rate-limit connections, and the pool has 512 MB. There is nothing to gain.
- **Lease:** 2 minutes, comfortably longer than any SMTP timeout we set (30 s
  connect, 60 s overall).
- On success: `Status = Sent`, `SentAt`, `ProviderMessageId`, clear the lock.
- On failure: classify (§2.9), set `LastError`, `NextAttemptAt` and status.

### 2.9 Failure classification and retry

| Condition | Treatment |
| --- | --- |
| SMTP 5xx on `RCPT TO` (550, 551, 553, 554 for an unknown user) | **Permanent.** `Failed`, and an `EmailSuppression` row with `HardBounce` |
| SMTP 5xx other, or a malformed-message rejection | **Permanent.** `Failed`, no suppression — the address is fine, our message was not, and suppressing would punish the customer for our bug |
| SMTP 4xx (421, 450, 451, 452), connection refused, DNS failure, timeout, TLS handshake failure | **Transient.** Retry per the schedule |
| Authentication failure (535) | **Transient, but loud.** Retry, and log at error level with a distinct event id — this is a misconfiguration and every email in the store is about to fail |
| Recipient suppressed between enqueue and send | `Suppressed`, no attempt |
| Lease expired while `Sending` | Recovered once: `Queued`, `PossibleDuplicate = true` |

Schedule, from the first failure: **+1 min, +5 min, +15 min, +1 hour, +4 hours,
+12 hours**, then `Abandoned`. Six retries over about eighteen hours, with ±20%
jitter so that a provider coming back from an outage does not receive the entire
backlog in one burst.

Three consecutive `Abandoned` sends to the same address create a
`RepeatedSoftBounce` suppression. It is visible and clearable, and it exists
because an address that has swallowed three days of retries is not going to
start working on the fourth.

### 2.10 Bounce handling — what ships and what does not

Plain SMTP gives no delivery callback, so this is deliberately partial and the
partition is drawn here rather than discovered later.

**Ships in Phase 5:**

- Synchronous rejection handling (§2.9) — which catches most dead addresses,
  because a mistyped domain fails at `RCPT TO`.
- A **VERP return path**: the SMTP envelope sender (`MAIL FROM`) is
  `bounces+{PublicRef}@{bounceDomain}` while the visible `From:` stays the store
  address. Every asynchronous bounce that arrives at that mailbox therefore
  names the exact message that caused it, from day one, whether or not anything
  is yet reading the mailbox. `PublicRef` rather than `Id` so the address does
  not disclose the store's email volume to anyone who receives one.
- The suppression list, with manual add and clear from the admin.
- Repeated-soft-bounce detection.

**Does not ship in Phase 5:** an inbound bounce reader. It needs a mailbox, an
IMAP poll and a DSN parser, and the SMTP provider is still an open question
(PRD §13). The `email.bounce.mailbox` setting is the recorded hook. Until then
the operator marks an address manually, which is the honest state of it.

A suppressed recipient never blocks anything. The order completes, the grants
exist, the success page shows the links, and the admin order row carries a
"not emailed — address suppressed" state so somebody can pick up the phone.

---

## 3. Contracts

### 3.1 `IEmailSender` and transport selection

```
Task<SendOutcome> SendAsync(OutboundMessage message, CancellationToken ct);

SendOutcome = { Delivered, Classification, ProviderMessageId?, Diagnostic? }
Classification = Delivered | TransientFailure | PermanentFailure | RecipientRejected
```

Three implementations, selected by configuration:

| `Email:Transport` | Implementation | Behaviour |
| --- | --- | --- |
| `FileDrop` | `FileDropEmailSender` | Writes a real RFC 5322 `.eml` to `Email:FileDrop:Path`, default `App_Data/mail-drop`, named `{yyyyMMdd-HHmmss}-{publicRef}-{lang}.eml`. A real `.eml` and not a text log, because it opens in Outlook and Apple Mail, which is the only way to actually check the Arabic renders right |
| `Smtp` | `SmtpEmailSender` | MailKit |
| `None` | `NullEmailSender` | Records `Suppressed`; never opens a socket |

**Selected by configuration, not by `IHostEnvironment`.** The environment only
supplies the default: `FileDrop` in Development, `Smtp` elsewhere.
Configuration overrides it, because staging needs to run `FileDrop` before an
SMTP account exists, and production needs to be able to run `None` on day one —
the SMTP provider is an open PRD question, and the KuwaitHR deployment already
established that shipping with email off beats shipping with email broken.

Configuration keys, none of them ever committed with a value:

```
Email:Transport            FileDrop | Smtp | None
Email:FileDrop:Path        App_Data/mail-drop
Email:Smtp:Host
Email:Smtp:Port            587
Email:Smtp:UseStartTls     true
Email:Smtp:User
Email:Smtp:Password        ← App Pool environment variable Email__Smtp__Password
Email:Smtp:TimeoutSeconds  60
Email:BounceDomain
Site:PublicBaseUrl         https://…   (see §4.4)
```

`appsettings.json` carries the non-secret defaults. `appsettings.Development.json`
sets `Transport: FileDrop` and **no credentials at all** — there is nothing to
redact because there is nothing there. On the server the credentials are App Pool
environment variables, as the HR app already does.

**Startup validation warns, it does not crash.** If `Transport` is `Smtp` and
`Host` is empty, the app logs an error, reports `/health` as degraded, and
behaves as `None`. Refusing to start would take the store down over email, and
the store can still sell and deliver without it. What must not happen is
silence, and `Suppressed` rows piling up visibly is not silence.

### 3.2 Template rendering

`IEmailTemplateRenderer.Render(EmailKind kind, string language, EmailModel model)`
→ `{ Subject, BodyHtml, BodyText }`.

**Templates are restricted-token strings, not Razor, not Liquid, not any engine.**
An operator-edited string that reaches a compiler is a code-execution path, and
in a public repository the shape of that path is published. The renderer does
one thing: replace known tokens.

| Token | Produced by | Notes |
| --- | --- | --- |
| `{{customer_name}}` | model | HTML-encoded |
| `{{order_number}}` | model | Wrapped `<span dir="ltr">` |
| `{{order_date}}` | renderer | §3.3 |
| `{{order_total}}` | renderer | §3.3. Operators cannot format money |
| `{{book_list}}` | renderer | A block: title and price per line |
| `{{download_links}}` | renderer | A block: one button and one bare URL per book |
| `{{link_expiry_days}}`, `{{max_downloads}}` | model | Western digits |
| `{{library_url}}`, `{{support_email}}`, `{{store_name}}` | model | |

Rules:

- Every scalar token is HTML-encoded on substitution. The block tokens are
  renderer-produced HTML and are the only ones inserted raw.
- An **unknown** token is left verbatim and logged as a warning. It never throws:
  a typo in a template must not stop a paid customer's email.
- A **missing required** token is refused at *save* time, not send time. The
  Settings validator rejects a `downloadLinks` body without `{{download_links}}`,
  naming it, in the operator's language. The failure belongs on the screen where
  the mistake was made.
- The operator edits the prose around the blocks. They cannot delete the links,
  and they cannot write `6.50`.

The HTML layout — the table scaffold, the inline CSS, the direction attributes —
is `EmailHtmlLayout`, not a setting. An operator who can edit the layout can
break Outlook, and nobody will find out until a customer says the email looked
wrong.

### 3.3 Money, numbers and dates in email

- **Money** is `Currency.Kwd.Format(amount)` — the Phase 1 type, invariant
  culture, exactly three decimals. English renders `KD 6.500`; Arabic renders
  `6.500 د.ك`, matching `price()` in `site.js`. No `CultureInfo` is consulted
  anywhere in the renderer.
- **Bidi isolation is mandatory.** In an RTL body, `KD 6.500` placed next to
  Arabic text is reordered by the bidirectional algorithm, and `KW-00042` can
  have its hyphen rendered on the wrong side. Every amount, every order number
  and every URL is wrapped in `<span dir="ltr" style="unicode-bidi:isolate">`,
  and in the plain-text alternative each URL sits alone on its own line with no
  surrounding sentence, so no mail client's bidi pass can mangle it. This is the
  specific defect that makes an Arabic receipt look amateur, and it is invisible
  until someone reads one.
- **Digits stay Western**, in both languages, including in dates — PRD §5, and
  the Phase 1 lesson of `PDF، ٦٫٢ ميغابايت`.
- **Dates** are rendered in Kuwait time (UTC+3, no DST) via
  `TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait")`, which resolves on both
  the Ubuntu CI runner and Windows IIS. The Arabic month name comes from a
  **hardcoded twelve-name array** — يناير، فبراير، مارس، أبريل، مايو، يونيو،
  يوليو، أغسطس، سبتمبر، أكتوبر، نوفمبر، ديسمبر — not from `CultureInfo("ar-KW")`.
  Two reasons: the Levantine month names a generic Arabic culture may produce
  (كانون الثاني and so on) are not what a Kuwaiti reader expects, and ICU's digit
  substitution for some Arabic cultures emits Arabic-Indic numerals, which PRD §5
  forbids outright. A fixed array is deterministic, testable, and cannot regress
  when the runner's ICU version changes.

### 3.4 The templates

Both kinds, both languages. These are the seeded defaults; the operator may
rewrite the prose.

**Order confirmation — EN**

> Subject: `Your order {{order_number}} — your download link`
>
> Hello {{customer_name}},
>
> Thank you. Your payment went through and your book is ready.
>
> {{book_list}}
> Total {{order_total}} · paid {{order_date}}
>
> **Download your file**
> {{download_links}}
>
> Each link works for {{link_expiry_days}} days and up to {{max_downloads}}
> downloads — enough for a phone, a laptop and a mistake.
>
> Every file is watermarked with your name and your order number.
>
> Keep this email, or sign in at {{library_url}} and your books will be waiting.
>
> Any problem at all, just reply to this message.

**Order confirmation — AR**

> الموضوع: `طلبك {{order_number}} — رابط التحميل`
>
> مرحباً {{customer_name}}،
>
> شكراً لك. تم استلام الدفعة، وكتابك جاهز.
>
> {{book_list}}
> الإجمالي {{order_total}} · تاريخ الدفع {{order_date}}
>
> **حمّل ملفك**
> {{download_links}}
>
> كل رابط يعمل لمدة {{link_expiry_days}} أيام، وحتى {{max_downloads}} عمليات
> تحميل — ما يكفي للهاتف والحاسوب ومحاولة إضافية.
>
> يحمل كل ملف اسمك ورقم طلبك.
>
> احتفظ بهذه الرسالة، أو سجّل الدخول على {{library_url}} وستجد كتبك بانتظارك.
>
> لأي ملاحظة، يكفي الرد على هذه الرسالة.

**Download links (resend) — EN**

> Subject: `Your download link for order {{order_number}}`
>
> Hello {{customer_name}},
>
> Here is your download link again, as requested.
>
> {{download_links}}
>
> **Nothing has been charged.** Order {{order_number}} was paid on
> {{order_date}} and this message only repeats the link.
>
> If a link has stopped working, reply and we will issue a new one.

**Download links (resend) — AR**

> الموضوع: `رابط التحميل لطلبك {{order_number}}`
>
> مرحباً {{customer_name}}،
>
> هذا رابط التحميل مرة أخرى، كما طلبت.
>
> {{download_links}}
>
> **لم يتم خصم أي مبلغ.** طلبك {{order_number}} مدفوع بتاريخ {{order_date}}،
> وهذه الرسالة تعيد إرسال الرابط فقط.
>
> إذا توقف أحد الروابط عن العمل، ردّ على الرسالة وسنصدر رابطاً جديداً.

The "nothing has been charged" line is not padding. A second email about an
order the customer already paid for reads, at a glance, like a second charge,
and that is a support call and possibly a chargeback.

The Arabic is written as Arabic, not as English with Arabic words. No
"عميلنا العزيز", no passive constructions imported from the English, and the
imperative مرحباً/شكراً register a Gulf reader actually uses. A translation
reviewer, not a translation tool, signs this off before Phase 9.

### 3.5 HTML layout constraints

- Table-based, single column, `max-width: 600px`, all CSS inline. No `<style>`
  block survives every client; no external stylesheet survives any.
- `<html dir="rtl" lang="ar">` for Arabic — **and** `dir="rtl"` plus
  `text-align: right` on the `<table>` and on each `<td>`. Outlook's word
  rendering engine ignores `dir` on the document in several versions; setting it
  per cell is the thing that actually works.
- No web fonts. `font-family: 'Segoe UI', Tahoma, Arial, Helvetica, sans-serif`
  covers Arabic on Windows and Outlook; Apple Mail falls through to the system
  Arabic face.
- No images at all, including no logo and no tracking pixel. The storefront's
  covers are drawn in CSS and there is nothing to attach; a tracking pixel would
  be a privacy claim we have not made.
- `multipart/alternative` with a plain-text part always. Some Gulf corporate
  mail gateways strip HTML entirely, and a download link that only exists in an
  HTML part is a link the customer never sees.
- Charset UTF-8, enforced. MimeKit chooses the transfer encoding; what must
  never happen is a fallback to ISO-8859-1, which is how Arabic becomes `????`.

### 3.6 REST — admin

All of these sit under `/api/admin`, require the `nexus_admin` cookie scheme, and
require antiforgery on every mutating verb because the authentication is a
cookie. Phase 5 registers the scheme and the policies; **it does not build a
login**. With no user store yet nothing can authenticate, which is the correct
and safe state — the endpoints are built, tested with a test authentication
handler, and become reachable when Phase 6/7 put Identity behind the same scheme
name. Policy names, so the later phases match: `AdminOnly`, `AdminOrStaff`.

#### `POST /api/admin/orders/{orderId}/resend-email`

Auth: `AdminOrStaff`. Antiforgery: required.

```json
{ "language": "ar", "to": "buyer@example.com" }
```

`language` optional, defaults to the order's. `to` optional; when present it
overrides the recipient for this send only, does not change the order, and is
recorded in the audit row — the operator correcting a typo'd address is the
commonest real use.

`202 Accepted`:

```json
{
  "publicRef": "k3Qv8Zr1TUy9Xp2aBcDeFg",
  "status": "Queued",
  "to": "buyer@example.com",
  "language": "ar",
  "queuedAt": "2026-10-20T11:04:22+03:00"
}
```

| Status | When | Body |
| --- | --- | --- |
| `400` | `language` is neither `en` nor `ar`; `to` is not a valid address | `{ "error": "invalid_language" \| "invalid_recipient", "messageEn", "messageAr" }` |
| `401` | No admin session | — |
| `403` | Authenticated but not `Admin`/`Staff`; or antiforgery failed | — |
| `404` | Order does not exist | `{ "error": "order_not_found" }` |
| `409` | Order is not `Paid` | `{ "error": "order_not_paid", "status": "Pending" }` |
| `409` | Every grant is dead | `{ "error": "no_live_grants", "books": [{ "bookId": 7, "titleEn": "…", "titleAr": "…", "grantState": "Expired" }] }` — the operator re-issues first |
| `409` | Recipient suppressed | `{ "error": "recipient_suppressed", "reason": "HardBounce", "since": "…" }` |
| `429` | Rate limit | `{ "error": "rate_limited", "scope": "order" \| "actor", "limit": 3, "windowMinutes": 60, "retryAfterSeconds": 1840 }`, plus a `Retry-After` header |
| `503` | Transport is `None` or degraded | `{ "error": "email_disabled" }` |

Every outcome, including `429` and `409`, writes an `AuditLog` row.

#### `GET /api/admin/orders/{orderId}/emails`

Auth: `AdminOrStaff`. Metadata only — **never the body**, which contains live
download URLs.

```json
[{
  "publicRef": "k3Qv8Zr1TUy9Xp2aBcDeFg",
  "kind": "OrderConfirmation",
  "language": "ar",
  "to": "buyer@example.com",
  "status": "Sent",
  "attempts": 1,
  "createdAt": "2026-10-20T11:02:10+03:00",
  "sentAt": "2026-10-20T11:02:24+03:00",
  "lastError": null,
  "possibleDuplicate": false
}]
```

#### `GET /api/admin/outbox?status=&from=&to=&page=&pageSize=`

Auth: `AdminOnly`. The operations view: default filter `Failed,Abandoned,Queued`,
ordered oldest first, `pageSize` capped at 100. Paged, unlike the catalogue —
this table grows without bound where the catalogue does not.

#### `POST /api/admin/outbox/{publicRef}/retry`

Auth: `AdminOnly`. Moves a `Failed` or `Abandoned` row back to `Queued` with
`NextAttemptAt = now`, preserving `Attempts` so the history is not rewritten.
`409` if the row is `Sent` or `Sending`. Audited.

#### `GET /api/admin/suppressions`, `POST /api/admin/suppressions`, `DELETE /api/admin/suppressions/{email}`

Auth: `AdminOnly`. Manual block and clear, with `notes`. `DELETE` sets
`ClearedAt`/`ClearedBy` rather than deleting the row — why an address was
blocked and who unblocked it is exactly what you want six months later. Audited.

#### `GET /health`

Phase 5 adds an `outbox` check, reported alongside the existing ones:

- `Healthy` — oldest `Queued` row is under 5 minutes old, or there is none.
- `Degraded` — oldest `Queued` row is 5–15 minutes old, or transport is `None`,
  or the SMTP configuration is incomplete.
- `Unhealthy` — oldest `Queued` row is over 15 minutes old.

That last one is the alarm for §6 risk 1, and the reason the check exists at all.

### 3.7 Storefront

Phase 5 is almost entirely server-side. One change, in one place.

`success.html` currently renders `ok_body` — *"Your file is ready. We also sent
the link to {email}"* — unconditionally. When the transport is off or the
address is suppressed, that sentence is false, and the customer closes the tab
believing an email is coming.

The order-status response the success page already reads (Phase 3's
`GET /api/orders/{ref}/status`) gains one boolean:

```json
{ "...": "...", "emailQueued": true }
```

and the page picks between two strings:

| Key | EN | AR |
| --- | --- | --- |
| `ok_mail_sent` | `We also sent the link to {email}.` | `وأرسلنا الرابط أيضاً إلى {email}.` |
| `ok_mail_none` | `Download your file now — we could not email the link to {email}.` | `حمّل ملفك الآن — تعذّر إرسال الرابط إلى {email}.` |

`ok_body` is retired. Markup is otherwise untouched: same element, same classes,
different key.

Admin i18n keys are added in the same change even though the screens are
Phase 7, so the EN/AR pair lands together per CLAUDE.md rule 4:
`a_resend_lang`, `a_resend_ok`, `a_resend_limit`, `a_resend_nolinks`,
`a_mail_queued`, `a_mail_sent`, `a_mail_failed`, `a_mail_suppressed`,
`a_mail_retry`, `a_s_mail_off`, `a_s_replyto`, `a_s_subj_en`, `a_s_subj_ar`,
`a_s_tmpl_en`, `a_s_tmpl_ar`, `a_s_tmpl_missing`.

### 3.8 Admin Razor pages

**None in this phase.** `wwwroot/admin/*.html` is still the demo, and any
credentials pass. Wiring a real resend into an unauthenticated page would be the
worst outcome available. The resend button in `pg-orders.js` keeps calling
`toast()` until Phase 7 replaces the screen; the endpoint it will call already
exists and is tested.

---

## 4. Integration points

### 4.1 What Phase 5 needs from Phases 2–4 — the merge checklist

| # | From | Needed | If absent |
| --- | --- | --- | --- |
| 1 | Phase 2 | `Order` with `OrderNumber`, `Status`, `TotalKwd`, `PaidAt`, `CustomerId`; `OrderItem` with `TitleSnapshot` and `UnitPriceKwd` | Blocked |
| 2 | Phase 2 | `Order.Language` `NVARCHAR(2)` default `en`, captured from the checkout page's interface language | Every guest gets English. Phase 5 adds the column in its own migration if Phase 2 has not |
| 3 | Phase 2 | `Customer` with `Email`, `Name`, and `PreferredLanguage` nullable | Falls back to `Order.Language` |
| 4 | Phase 3 | The webhook handler calls `QueueOrderConfirmationAsync` **inside** the transaction that sets `Paid` and creates grants, before commit | Orders complete with no email |
| 5 | Phase 3 | The reconciliation job (PLAN §6.6) takes the same path when it settles a stuck order to `Paid` | Orders rescued by reconciliation are never emailed |
| 6 | Phase 3 | `GET /api/orders/{ref}/status` returns `emailQueued` | `success.html` keeps claiming an email was sent |
| 7 | Phase 4 | `DownloadGrant` with `Token`, `BookId`, `ExpiresAt`, `MaxDownloads`, `UsedCount`, `State` | Blocked |
| 8 | Phase 4 | `Setting` keys `delivery.linkExpiryDays` and `delivery.maxDownloads` | Renderer falls back to 7 and 3 |
| 9 | Phase 3/4/7 | `Setting` and `AuditLog` entities, to the shapes in §1.3 and §5.2 | Phase 5 creates them |

### 4.2 Tap

Phase 5 calls nothing at Tap and verifies no signature. Its only relationship to
the gateway is the call site in §4.1 item 4 and the constraint it imposes:
**the webhook handler performs no network I/O.** The `tap-payments` agent owns
the handler's detail; this design owns the rule that the enqueue is in-transaction
and that the handler returns 200 without waiting for a mail server.

### 4.3 Download grants and PDF storage

The renderer reads grants; it never touches `App_Data` and never opens a PDF.
Watermarking happens at download time (PLAN §7.4), not at email time.

### 4.4 Absolute URLs

Every link in an email is absolute, built from `Site:PublicBaseUrl` —
**never from the request**. The dispatcher runs on a background thread with no
`HttpContext`; an `IUrlHelper` or `IHttpContextAccessor` there yields null and
ships `/download/abc…` as a relative path, which is a dead link in a mail
client and looks fine in every test that renders the body as a string. The
setting is validated at startup: absent or not absolute, and `/health` is
degraded with a message naming it.

### 4.5 Background jobs

- `EmailDispatcher` — the hosted service in §2.8.
- **A nightly sweep**, added to the Phase 3 reconciliation service rather than
  as a second timer: any `Queued` row older than an hour is logged at error
  level and counted into the daily digest. The dispatcher should already have
  sent it; if it has not, the dispatcher is not running (§6 risk 1).
- **Body retention**, in the same nightly pass: `BodyHtml` and `BodyText` are
  set to `NULL` on rows `Sent` more than 30 days ago. The metadata row stays
  forever.

### 4.6 Packages

| Package | Version | Why |
| --- | --- | --- |
| `MailKit` | 4.8.0 | SMTP with working STARTTLS and modern authentication. `System.Net.Mail.SmtpClient` is documented as not recommended for new development |
| `MimeKit` | 4.8.0 | Comes with MailKit. Builds the `multipart/alternative` and gets the UTF-8 encoding of Arabic right, which is the part that silently produces `????` when hand-rolled |

Both MIT, both pinned in `Directory.Build.props` alongside the existing EF Core
pins. **No templating engine package** — §3.2 explains why the renderer is fifty
lines of our own code instead.

---

## 5. File ownership

Phase 5 is roughly 95% backend. The split is small but the shared list is not,
because three other phases are in flight.

### 5.1 `backend-dev` owns, exclusively

```
src/Nexus.Domain/Entities/OutboxEmail.cs            (+ EmailKind, EmailStatus)
src/Nexus.Domain/Entities/EmailSuppression.cs       (+ SuppressionReason)

src/Nexus.Application/Email/IEmailOutbox.cs
src/Nexus.Application/Email/IEmailSender.cs
src/Nexus.Application/Email/IEmailTemplateRenderer.cs
src/Nexus.Application/Email/IEmailSuppressionList.cs
src/Nexus.Application/Email/IOrderEmailService.cs
src/Nexus.Application/Email/EmailDtos.cs
src/Nexus.Application/Email/EmailTemplateKeys.cs

src/Nexus.Infrastructure/Email/EmailOutbox.cs
src/Nexus.Infrastructure/Email/OrderEmailService.cs
src/Nexus.Infrastructure/Email/EmailDispatcher.cs
src/Nexus.Infrastructure/Email/EmailRetryPolicy.cs
src/Nexus.Infrastructure/Email/SmtpEmailSender.cs
src/Nexus.Infrastructure/Email/FileDropEmailSender.cs
src/Nexus.Infrastructure/Email/NullEmailSender.cs
src/Nexus.Infrastructure/Email/EmailTemplateRenderer.cs
src/Nexus.Infrastructure/Email/EmailHtmlLayout.cs
src/Nexus.Infrastructure/Email/KuwaitDates.cs
src/Nexus.Infrastructure/Email/EmailOptions.cs
src/Nexus.Infrastructure/Email/EmailServiceCollectionExtensions.cs
src/Nexus.Infrastructure/Email/EmailSuppressionList.cs
src/Nexus.Infrastructure/Persistence/Configurations/OutboxEmailConfiguration.cs
src/Nexus.Infrastructure/Persistence/Configurations/EmailSuppressionConfiguration.cs
src/Nexus.Infrastructure/Persistence/SeedData/email-templates.json
src/Nexus.Infrastructure/Persistence/EmailTemplateSeeder.cs
src/Nexus.Infrastructure/Persistence/Migrations/<ts>_EmailOutbox.*

src/Nexus.Web/Controllers/Admin/AdminEmailController.cs
src/Nexus.Web/Controllers/Admin/AdminSuppressionController.cs
src/Nexus.Web/Health/OutboxHealthCheck.cs

tests/Nexus.UnitTests/EmailTemplateRendererTests.cs
tests/Nexus.UnitTests/EmailRetryPolicyTests.cs
tests/Nexus.UnitTests/EmailLanguageTests.cs
tests/Nexus.UnitTests/ArabicEmailFormattingTests.cs
tests/Nexus.UnitTests/KuwaitDatesTests.cs
tests/Nexus.IntegrationTests/EmailOutboxTests.cs
tests/Nexus.IntegrationTests/ResendEndpointTests.cs
tests/Nexus.IntegrationTests/RecordingEmailSender.cs
tests/Nexus.IntegrationTests/AdminTestAuthHandler.cs
```

### 5.2 `storefront-dev` owns, exclusively

```
src/Nexus.Web/wwwroot/success.html                  (two strings and one boolean)
src/Nexus.Web/wwwroot/assets/js/i18n.js             (the keys listed in §3.7 ONLY)
```

Nothing else in `wwwroot` changes in this phase. `storefront-dev` should take
this work last and in one sitting; it is perhaps an hour, and `i18n.js` is the
file every other phase also wants.

### 5.3 Shared — the collision list

| File | Also touched by | Rule |
| --- | --- | --- |
| `src/Nexus.Domain/Entities/Setting.cs` | Phases 3, 4, 7 | First to merge creates it to §1.3 exactly. Others add keys, never columns |
| `src/Nexus.Domain/Entities/AuditLog.cs` | Phase 7 nominally; 3, 4, 5 all need it | Same rule. Shape: `Id`, `ActorId`, `ActorEmail`, `Entity`, `EntityId`, `Action`, `BeforeJson`, `AfterJson`, `At`, `IpAddress` |
| `src/Nexus.Domain/Entities/Order.cs` | Phase 2 owns | Phase 5 **reads only**. If `Language` is missing at merge, Phase 5 adds the column and nothing else |
| `src/Nexus.Infrastructure/Persistence/NexusDbContext.cs` | Everyone | Convert to `ApplyConfigurationsFromAssembly` (§1.5) on first merge; after that each phase adds a configuration file and the context is never edited again |
| `src/Nexus.Infrastructure/DependencyInjection.cs` | Everyone | One line: `services.AddNexusEmail(config);`. All wiring lives in Phase 5's own extension file |
| `src/Nexus.Web/Program.cs` | Everyone | Two lines: the `nexus_admin` cookie scheme + `AdminOnly`/`AdminOrStaff` policies, and `.AddCheck<OutboxHealthCheck>("outbox")`. Phase 6/7 reuse the scheme, they do not re-register it |
| `src/Nexus.Web/appsettings.json`, `appsettings.Development.json` | Everyone | Phase 5 adds the `Email` and `Site` sections. No secret values |
| `src/Nexus.Infrastructure/Nexus.Infrastructure.csproj` | Everyone | MailKit and MimeKit package references |
| `Directory.Build.props` | Everyone | Version pins |
| `tests/Nexus.IntegrationTests/NexusApp.cs` | Everyone | Phase 5 adds `Email:Transport = None` and registers `RecordingEmailSender`. **Nothing may weaken the `NEXUS_TEST_DB` handling** — that was the bug that had the integration suite dropping `NexusDev` |
| `Migrations/` | Everyone | Generate last, against the merged model. The CI idempotent script is produced from the final model, so branch ordering does not affect the release |

**The contract the two developers meet at:** `emailQueued` in the order-status
response, and the sixteen i18n keys in §3.7. Nothing else crosses the line.

---

## 6. Risks and decisions

### Risks

**1. The app pool idles out and the dispatcher stops.** The most likely way this
phase fails in production. SmarterASP's default IIS idle timeout stops the
worker process after twenty minutes without a request, taking the
`BackgroundService` with it, and a store with light overnight traffic will have
emails sitting in `Queued` until the next visitor wakes the pool. Three
mitigations, all needed: set the app pool to Always Running and record it in the
runbook; the nightly sweep (§4.5); and the `/health` outbox check (§3.6) going
`Unhealthy` at fifteen minutes so monitoring catches it rather than a customer.
Deliberately **not** mitigated with a public keep-alive endpoint — that is an
unauthenticated route on a public repository, and a cron ping is the ops team's
job, not the application's.

**2. Arabic rendering in real mail clients.** Charset, direction and bidi
isolation are all specified above, and none of it can be proven by a unit test.
Outlook in particular ignores document-level `dir`. The mitigation is a manual
matrix before Phase 9 — Outlook on Windows, Gmail web, the Gmail Android app,
Apple Mail on iOS — each in Arabic, each checking that the money reads `6.500`
left to right inside right-to-left text and that the order number is not
reversed. The `.eml` file drop exists specifically so that check is a
double-click rather than a test purchase.

**3. Sending-domain reputation.** A brand-new domain sending transactional mail
lands in spam, and "I didn't get my link" is the support request PRD §11 is
trying to drive down. SPF, DKIM and DMARC records on the sending domain are a
precondition for Phase 9, and they depend on two still-open questions — the
custom domain and the SMTP provider. This does not block Phase 5; it blocks
go-live, and should be raised now because DNS and provider verification have
lead times measured in days.

**4. The outbox body contains live download URLs.** Anyone with read access to
the database can lift a working link out of `BodyHtml`. This adds no exposure
that `DownloadGrant.Token` does not already create in the same database, and the
real controls are unchanged: grants expire, are count-limited, are revocable and
are logged. What this phase adds is bounded retention — bodies are nulled 30 days
after send — and the rule that the admin API never returns a body.

**5. An operator breaks a template.** Restricted tokens with save-time validation
of required placeholders, application-rendered blocks for the links and the
money, and an unknown token that renders verbatim rather than throwing. Phase 7
adds a preview. The worst available outcome — an operator accidentally deleting
the download link from the delivery email — is prevented at the point of saving.

**6. A crash between SMTP accepting and the `Sent` write produces a duplicate.**
Accepted, recorded with `PossibleDuplicate`, and the duplicate is harmless
because the links are identical. Not solvable over SMTP; stated rather than
hidden.

**7. Resend as a spam vector.** A compromised `Staff` account could fire mail
from our domain at arbitrary addresses using the `to` override. Bounded by the
per-actor hourly limit, every call audited including refusals, and no anonymous
endpoint exists.

**8. Phase 5 is designed against three unbuilt phases.** §4.1 is the mitigation
and the merge checklist. The two items most likely to be missed are
`Order.Language` and the enqueue being inside the webhook's transaction rather
than after its commit.

### Decisions

| # | Decision | Alternative rejected | Why |
| --- | --- | --- | --- |
| E1 | **Transactional outbox**: enqueue in the payment transaction, send from a background dispatcher | Send inline in the webhook handler | §2.1. A mail server outage would stop the store taking orders |
| E2 | **One email per paid order**, receipt and links together; a second template for link-only resends | Separate "receipt" and "your download" emails | The buyer is waiting for one thing. Two emails arriving together is a support question, and it doubles the ways delivery can half-fail |
| E3 | **Transport chosen by configuration**, environment only supplies the default | `if (env.IsDevelopment())` | Staging needs `FileDrop` before SMTP exists; production needs `None` while the provider is undecided |
| E4 | **Templates are restricted-token strings** rendered by fifty lines of our own code | Razor, Liquid, Scriban | An operator-edited string reaching a compiler is a code-execution path, and this repository is public |
| E5 | **Content and policy in `Setting`; transport credentials in configuration** | Everything in `Setting`, like the Tap keys | The operator rotates Tap keys from a screen; nobody rotates an SMTP host. `ValidateOnStart` fails at deploy instead of at 2am |
| E6 | **Misconfigured transport warns and degrades to `None`** | Refuse to start | The store must keep selling. `Suppressed` rows and a red health check are visible; a store that will not boot over email is not |
| E7 | **Money and dates are rendered by the application, never by the template** | Give the operator `{{total}}` as a raw number | Three decimals is the rule the whole product turns on. An operator writing `6.50` must not be possible |
| E8 | **Arabic month names from a fixed array**, not `CultureInfo` | `ToString("d MMMM yyyy", ar-KW)` | ICU may emit Arabic-Indic digits, which PRD §5 forbids, and may emit Levantine month names a Kuwaiti reader does not use. The CI runner's ICU version must not be able to change an email |
| E9 | **Resend refuses when every grant is dead** | Send the links anyway | A customer trying a dead link distrusts the next one. Re-issue first |
| E10 | **No anonymous resend endpoint in this phase** | Wire the existing `lib_resend` button | It would make the store an open relay from our own domain. Phase 6, behind a customer session |
| E11 | **VERP return path shipped, bounce reader deferred** | Both, or neither | The data starts accumulating from the first email at no cost; the reader needs a mailbox and a provider decision that has not been made |

Three of these would be expensive to reverse and warrant an ADR under
`docs/adr/` when this phase is built — **E1** (transactional outbox), **E4**
(restricted-token templates) and **E3** (transport by configuration). This task
writes one file, so they are recorded here and should be lifted out verbatim.

---

## 7. Testing

**Unit** — `Nexus.UnitTests`, no database:

- `Currency.Kwd.Format` through the renderer: a 6.5 KWD total renders `KD 6.500`
  in English and `6.500 د.ك` in Arabic, in both the HTML and the text part, and
  **never** `6.50` or `٦٫٥٠٠`.
- Bidi isolation: every amount, order number and URL in an Arabic body is inside
  a `dir="ltr"` span; every URL in the Arabic text part is alone on its line.
- No Arabic-Indic digit (U+0660–U+0669, U+06F0–U+06F9) appears anywhere in a
  rendered Arabic email. One assertion, run over every template and both kinds —
  this is the Phase 1 `fmt_val` defect, generalised so it cannot come back.
- `KuwaitDates`: a UTC instant near midnight renders the Kuwait calendar day, the
  Arabic month name comes from the fixed array, and the test passes on both a
  Windows and an Ubuntu runner.
- Template renderer: unknown token survives verbatim and logs; required token
  missing is refused by the validator with the field named; a scalar token
  containing `<script>` is HTML-encoded; block tokens are not.
- Retry policy: the full schedule with jitter bounds; each SMTP classification
  maps to the right status; the schedule terminates at `Abandoned`.
- Language selection: all three precedence levels and the fallback.
- i18n completeness: every key added in §3.7 exists in both `en` and `ar` in
  `i18n.js`. Phase 1 already has this shape of test; extend it rather than
  writing a second one.

**Integration** — `Nexus.IntegrationTests`, real SQL Server, `RecordingEmailSender`:

- A paid order enqueues exactly one `OrderConfirmation` row.
- **The replayed webhook case**: delivering the same Tap notification three times
  leaves one order, one outbox row and one send. This is the headline requirement
  and gets its own named test.
- A rolled-back payment transaction leaves **no** outbox row — the negative of
  the same property, and the one that catches a stray `SaveChangesAsync` in
  `EnqueueAsync`.
- An order settled by reconciliation rather than by webhook still enqueues, and
  a webhook arriving afterwards does not enqueue a second.
- The dispatcher: claims, sends, marks `Sent`; a transient failure reschedules
  with the right `NextAttemptAt`; a hard bounce creates a suppression and does
  not retry; a lease expired mid-`Sending` is recovered once with
  `PossibleDuplicate` set and not twice.
- Two dispatcher instances against one database send each row once.
- A suppressed recipient produces a `Suppressed` row, no send, **and a paid
  order with working grants** — the "email failure never blocks delivery"
  property, asserted rather than assumed.
- Resend: 202 on the happy path; 429 on the fourth within an hour with a correct
  `Retry-After`; 409 when every grant is dead, naming the books; 409 on an unpaid
  order; 401 with no admin cookie; 403 with a customer cookie; audited in every
  one of those cases including the refusals.
- `email.enabled = false` yields `Suppressed` rows, not lost ones, and
  `/health` reports degraded.
- An outbox row older than fifteen minutes makes `/health` `Unhealthy`.

**Manual, before Phase 9** — the things no test settles:

- The client matrix in risk 2, in Arabic.
- A native Arabic reader reviews both templates for register, not grammar. The
  question to ask is "would a Kuwaiti bank send this", not "is it correct".
- One real send to a Gmail, an Outlook.com and a locally hosted Kuwaiti address,
  checking the spam folder in each.
- SPF, DKIM and DMARC verified on the production sending domain.

**What would otherwise ship broken.** Three things, each of which would look
entirely fine in development: the dispatcher not running because the app pool
idled out; absolute URLs built from a null `HttpContext` and shipping relative;
and the Arabic email that renders perfectly in a browser and reverses its order
number in Outlook.
