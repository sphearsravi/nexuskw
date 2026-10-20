# Nexus — from static demo to production e-commerce

Plan of record. Target: a real PDF bookstore for **Kuwait** with a secure admin control
panel, Tap payments, and automated build + deploy from GitHub.

> **Scope narrowed 2026-10-20: Kuwait only for now.** One currency (KWD), no VAT, no FX,
> no currency switcher. The UAE and AED are deferred, not abandoned — `Nexus.Domain.Currency`
> keeps AED and its tests, and `Order` keeps `Currency`, `FxRate`, `VatRate`, `VatAmount`
> and `TotalCharged` so a later UAE launch is configuration and UI, not a migration.
> See PRD §4 "Deferring the UAE".

Drafted 2026-10-20 · Status: awaiting answers to the open decisions in §15

---

## 1. Where we start

`Downloads/Nexus/nexus-store` is a static front-end demo: 8 storefront pages, 7 admin
pages, vanilla JS, no build step, no server. It is deployed as-is at
`http://sphearsravi-001-site6.ctempurl.com/` (SmarterASP, confirmed 2026-10-20).

What is already real and worth keeping:

| Asset | Keep? | Note |
|---|---|---|
| Visual design, `site.css` (22 KB), CSS-drawn covers | **Keep as-is** | No image assets to migrate |
| `i18n.js` EN/AR + RTL, all UI strings | **Keep**, move book text to DB | Interface strings stay in JS |
| Dual currency KWD/AED, VAT 5% AED / 0% KWD | **KWD only in v1**; drop the switcher | KWD is 3-decimal — see §6.4 |
| Page flow: home → shop → book → cart → checkout → success → library | **Keep** | Matches the real funnel |
| Admin IA: dashboard, books, categories, orders, customers, downloads, settings | **Keep** | Already the right screens |
| `data.js` const arrays (`BOOKS`, `ORDERS`, `CUSTOMERS`, `DOWNLOADS`, `RATE`) | **Replace** with API client | The single biggest change |
| Cart/library in `localStorage` (`state` in `site.js`) | Cart stays client-side; **library moves server-side** | Entitlements must be authoritative |
| `checkout.html` fake card form | **Delete** | We never touch card data — see §6.2 |
| `admin/login.html` (any credentials pass) | **Replace** with ASP.NET Core Identity | |

Missing entirely: a server, a database, authentication, real payments, actual PDF files,
email, and any deployment automation.

---

## 2. Stack decision

**ASP.NET Core 8 (API + Razor Pages admin) · EF Core · SQL Server · static storefront in `wwwroot`.**

Reasoning, in order of weight:

1. Every other business app in this account is .NET 8 + EF Core + MSSQL on SmarterASP
   IIS (RentCollection, MeetingMins, KIA, KuwaitHR, IMS). One stack to operate, one
   deployment pattern already proven, one set of credentials.
2. The storefront is plain HTML/CSS/JS. It can be served from `wwwroot` untouched and
   converted page-by-page from `data.js` constants to `fetch()` calls. No rewrite, no
   framework migration, no lost design. This is the difference between a 6-week project
   and a 4-month one.
3. Tap integration is a server-side HTTP call plus a webhook endpoint — straightforward
   in ASP.NET Core, and the KNET work in RentCollection already proved out the Kuwait
   gateway-callback shape (raw body reads, HTTPS callbacks, short references).
4. PDF delivery needs authenticated byte streaming from outside the web root. A
   controller action does this natively; a static host cannot do it at all.

**Rejected:** Next.js/Node + Postgres (new runtime to operate, no existing deploy path);
Shopify/WooCommerce (digital-goods licensing, watermarking and KWD 3-decimal handling all
fight the platform); staying static with a serverless payment function (no way to secure
the PDFs, which is the whole product).

### 2.1 Runtime topology

```
Browser
  ├── GET /                     static storefront (wwwroot, unchanged markup)
  ├── GET /api/*                public catalogue JSON  ─┐
  ├── POST /api/checkout        creates Tap charge      ├── ASP.NET Core 8
  ├── GET /download/{token}     streams watermarked PDF ─┤   (OutOfProcess, IIS)
  └── GET /admin/*              Razor Pages, cookie auth ┘
                                        │
Tap  ──POST /api/payments/tap/webhook──►│
                                        ▼
                SQL Server (SmarterASP)   +   App_Data/library/*.pdf (never web-served)
```

---

## 3. Repository layout

```
nexus/
├── .github/workflows/        ci.yml · deploy-staging.yml · deploy-prod.yml
├── src/
│   ├── Nexus.Domain/         entities, enums, value objects (Money, Currency)
│   ├── Nexus.Application/    services, DTOs, interfaces, validators
│   ├── Nexus.Infrastructure/ EF Core, Tap client, email, PDF stamping, storage
│   └── Nexus.Web/            API controllers + Razor admin + wwwroot (storefront)
│       └── wwwroot/          index.html, shop.html, assets/… (from the demo)
├── tests/
│   ├── Nexus.UnitTests/        pricing, currency scale, hashstring, tokens
│   ├── Nexus.IntegrationTests/ WebApplicationFactory + LocalDB
│   └── Nexus.E2E/              Playwright: browse → pay (Tap test) → download
├── db/                       generated idempotent migration SQL per release
├── docs/                     this plan, ADRs, runbook, DEPLOY_CREDENTIALS.md (git-ignored)
└── Directory.Build.props     central package versions
```

---

## 4. Data model

EF Core entities (`int` keys unless noted):

- **Category** — Slug, NameEn, NameAr, Hue, SortOrder, IsActive
- **Book** — CategoryId, PriceKwd `decimal(18,3)`, Pages, Year, Isbn?, IsPublished,
  TitleEn/Ar, AuthorEn/Ar, DescriptionEn/Ar, CoverMotif, **PdfStorageKey**, PdfSizeBytes,
  PdfSha256, SalesCount (denormalised), CreatedAt/UpdatedAt
- **Customer** — Email (unique), Name, Country, PreferredLanguage, PreferredCurrency,
  `IdentityUserId?` (null = guest), CreatedAt — see §4.1
- **Order** — OrderNumber (`KW-#####` / `AE-#####`, unique), CustomerId, Currency,
  SubtotalKwd, VatRate, VatAmount, TotalKwd, TotalCharged, FxRate, Status
  (`Pending|Paid|Failed|Refunded|Cancelled`), TapChargeId, TapPaymentReference,
  TapGatewayReference, PaymentMethod, CreatedAt, PaidAt
- **OrderItem** — OrderId, BookId, **UnitPriceKwd captured at purchase**, TitleSnapshot
- **DownloadGrant** — OrderId, BookId, Token (GUID, unique, indexed), MaxDownloads,
  UsedCount, ExpiresAt, State (`Active|Exhausted|Expired|Revoked`), RevokedAt
- **DownloadLog** — GrantId, DownloadedAt, IpAddress, UserAgent
- **WebhookEvent** — TapChargeId, Attempt, RawBody, HashValid, ProcessedAt;
  unique index on (TapChargeId, Status) for **idempotency**
- **Setting** — key/value for Tap keys (encrypted), link expiry days, max downloads,
  watermark on/off, guest checkout, from-address, email template
- **AdminUser** — ASP.NET Core Identity, roles `Admin` / `Staff`
- **AuditLog** — AdminUserId, Entity, EntityId, Action, Before/After JSON, At

Design rules: prices stored in **KWD at 3 decimals**, converted at display and charge
time; `OrderItem.UnitPriceKwd` is a snapshot so later price edits never rewrite history;
order totals are **always recomputed server-side** from Book rows, never accepted from
the client.

### 4.1 Three identities, one Identity store

A single ASP.NET Core Identity store with roles `Admin`, `Staff`, `Customer`. Separate
cookies and separate login pages keep the two audiences apart:

| Identity | Auth | Can |
|---|---|---|
| **Guest** | none | Browse, cart, checkout, pay. Gets a `Customer` row with `IdentityUserId = null` and a time-limited emailed download link — nothing else. |
| **Customer** | Identity, cookie `nexus_customer`, role `Customer` | Everything a guest can, plus a permanent **library** of every book ever bought, re-issue of an expired link, order history, invoices, profile, language/currency preference. |
| **Admin / Staff** | Identity, cookie `nexus_admin`, roles `Admin`/`Staff`, separate `/admin/login` | The control panel (§8). Never shares a cookie with the storefront. |

**Guest → account claiming.** Guest orders are keyed on email. When someone later
registers with that email and confirms it, every prior guest `Customer` row and its
orders are merged into the account and the download grants reappear in their library.
This is the single most valuable retention feature on the site and costs one service
method, so it lands in Phase 6 rather than being deferred.

Rules: email confirmation required before a guest order is merged (otherwise registering
as `someone@else.com` steals their books); customer self-registration cannot grant
`Admin`/`Staff`; admin accounts are created only by an existing `Admin`.

---

## 5. Phases

Working days for one developer. Each phase ends green in CI and deployed to staging.

### Phase 0 — Foundations (2 d)
Solution scaffold, `Directory.Build.props`, EditorConfig, git repo + GitHub (§9),
`ci.yml` running build + test on every push. Copy the demo into `Nexus.Web/wwwroot`
verbatim; confirm it still renders from Kestrel.
*Done when:* CI is green and `dotnet run` serves the unchanged demo at `/`.

### Phase 1 — Domain, database, catalogue API (4 d)
Entities, `NexusDbContext`, first migration, seeder importing the 6 categories and
20 books out of `data.js`. Public endpoints: `GET /api/categories`,
`GET /api/books?cat=&q=&sort=`, `GET /api/books/{id}`, `GET /api/settings/public`.
No paging: the catalogue grows ~50 titles a year, so the whole published set is one
small response (requirements D8, CAT-15, CAT-37).
Response shape mirrors the existing `BOOKS` object so the front-end change stays minimal.
*Done when:* `shop.html` and `book.html` render from the API with `data.js` deleted.

### Phase 2 — Cart, pricing and order creation (3 d)
Server-side pricing engine (`Money`, `PricingService`), KWD only. `POST /api/orders`
takes book IDs + customer details, recomputes the total, writes a `Pending` order and
returns an order number. Cart stays in `localStorage` but holds only IDs.
*Done when:* unit tests cover KWD at three decimals, rounding at that scale, and empty
or invalid carts. AED stays covered by the `Currency` tests against a future UAE launch.

### Phase 3 — Tap payments (5 d) — full design in §6
Tap client, charge creation, redirect handling, webhook with hashstring verification,
idempotent state machine, reconciliation job. Replace `checkout.html`'s fake card form
with contact fields + a "Pay with Tap" button.
*Done when:* a test-mode KNET and a card payment each move an order `Pending → Paid`
**via the webhook**, and replaying the webhook changes nothing.

### Phase 4 — Digital delivery (4 d) — full design in §7
PDF upload to `App_Data/library` (outside wwwroot), per-order watermark stamping, signed
expiring download grants, authenticated streaming endpoint, download counter and IP
logging, `library.html` reading real entitlements.
*Done when:* a paid order yields a working link; the link dies at the limit and at
expiry; the raw PDF is unreachable by URL guessing.

### Phase 5 — Email (2 d)
SMTP sender, order-confirmation + download-link template (EN/AR, driven by the Settings
template field), resend-link action on the admin Orders screen, outbox table with retry.
*Done when:* a paid order sends exactly one email, and a resend is logged and rate-limited.

### Phase 6 — Customer accounts and library (4 d) — detail in §4.1
Identity with role `Customer`, register / login / confirm email / forgot password (all
bilingual), profile with language + currency preference, permanent library listing every
grant ever issued, **re-issue of an expired link from the account**, order history with
printable invoice. Guest checkout stays available and unchanged.
**Guest-order claiming:** on confirmed registration, merge prior guest orders on that email.
*Done when:* a guest buys, then registers with the same email, and the book appears in
their library — and the same merge does not fire for an unconfirmed email.

### Phase 7 — Admin control panel (6 d) — detail in §8
Identity login replacing the fake one, dashboard on real figures, full CRUD for books
(incl. cover + PDF upload) and categories, **bulk CSV import** for growing the
catalogue, order detail with refund, customer view, downloads log with revoke, settings
persisted and encrypted, audit log on every write.
*Done when:* every admin screen reads and writes the database, `/admin` is unreachable
without a session, and a 50-row CSV + PDF batch imports cleanly.

### Phase 8 — Hardening (3 d)
Rate limiting on checkout/download/login, antiforgery, security headers + CSP, HTTPS
redirect + HSTS, DataProtection keys persisted to `App_Data/dpkeys` (**this bit us on
RentCollection — without it every deploy logs everyone out**), structured logging,
`/health`, nightly DB backup, customer data deletion.

### Phase 9 — Go-live (2 d)
Swap Tap **test** keys for live keys (configuration only — no code change), production
deploy, smoke test with a real low-value charge, refund it, custom domain, monitoring.
Gated on the Tap merchant account being approved.

**Total ≈ 35 working days (~7–8 weeks).** Phases 1–4 are the critical path; 5, 6 and 7
can overlap with a second developer. Everything through Phase 8 runs on the Tap **test**
account, so none of it is blocked by merchant approval.

---

## 6. Tap payment gateway — detailed design

Verified against Tap's current docs (2026-10-20). Base URL `https://api.tap.company/v2`.

### 6.1 Flow: server-created Charge + hosted page + webhook

```
1. Browser  POST /api/checkout            { items[], name, email, country, currency }
2. Server   recompute total from DB → create Pending order
3. Server   POST https://api.tap.company/v2/charges
              Authorization: Bearer sk_test_…          (secret key, server only)
              { amount, currency, customer{first_name,email},
                source:{id:"src_all"},                 ← hosted page: KNET, cards, Apple Pay
                threeDSecure:true,
                reference:{ transaction: orderNumber, order: orderNumber,
                            idempotent: orderGuid },
                redirect:{ url: "https://…/checkout/return" },
                post:{     url: "https://…/api/payments/tap/webhook" },
                metadata:{ orderId } }
4. Server   store charge.id → 302 the browser to response.transaction.url
5. Customer pays on Tap's page (KNET / card / Apple Pay)
6. Tap      POST webhook  ──► authoritative state change (may arrive before step 7)
7. Browser  returns to /checkout/return?tap_id=chg_… ──► server does GET /charges/{id}
            and shows the result; it never writes state on the redirect alone
```

### 6.2 Why this flow

- **Zero PCI scope.** Card data is entered on Tap's hosted page; our servers never see a
  PAN. The demo's card form in `checkout.html` is deleted, not wired up.
- `src_all` gives KNET, cards and Apple Pay from one hosted page. KNET is how most
  Kuwaitis pay online; the others cost nothing extra to accept and cover visitors.
- **The webhook is the only reliable signal.** Browser redirects are lost to closed tabs,
  dead batteries and app switches. The redirect is for the human; the webhook is for the
  database.

### 6.3 Webhook security — non-negotiable

Tap signs each webhook with HMAC-SHA256 over a fixed concatenation, keyed by the
**secret API key**, delivered in the `hashstring` header:

```
toBeHashed = "x_id"                 + id
           + "x_amount"             + amount          ← rounded to the currency's scale
           + "x_currency"           + currency
           + "x_gateway_reference"  + reference.gateway    (empty string if absent)
           + "x_payment_reference"  + reference.payment
           + "x_status"             + status
           + "x_created"            + transaction.created

expected = HMAC_SHA256(toBeHashed, secretApiKey)   → fixed-time compare to the header
```

Implementation rules:

- Read the **raw request body** before model binding (the KNET work hit exactly this).
- Format `amount` at the currency's decimal places — **KWD and BHD use 3**, AED/SAR/USD
  use 2. Getting this wrong fails every hash.
- Reject mismatches with `401` and log them; never process an unverified webhook.
- **Idempotency:** `WebhookEvent` has a unique index on (charge id, status). Replays are
  recorded and ignored. Tap retries, and retries will happen.
- Re-verify `amount` and `currency` against our order before marking it paid. A valid
  signature on the wrong amount is still a failed order.
- Respond `200` quickly.

### 6.4 Money rules
`decimal` end to end, never `double`. Prices are KWD at 3 dp and that is what Tap is
charged — in v1 there is no conversion and no VAT step to get wrong. The order still
persists `Currency`, `FxRate` (1) and `TotalCharged`, so a UAE launch adds a conversion
rather than rewriting the order model.

### 6.5 Refunds
Admin "Refund" calls `POST /v2/refunds` with the charge id, amount and reason; the refund
webhook (same hashstring scheme) flips the order to `Refunded` and **revokes the download
grants**.

### 6.6 Reconciliation
Nightly hosted service: any order `Pending` for more than 30 minutes gets a
`GET /v2/charges/{id}` and is settled or marked `Failed`. The safety net for webhooks
that never arrived.

### 6.7 Keys
Test keys (`sk_test_…`) through Phase 8 — the whole build runs on the Tap test account.
Live keys only at Phase 9, set as App Pool
environment variables / server-side `appsettings.Production.json` — never in git, never
in Actions logs. The admin Settings screen shows them masked and write-only, as the demo
already pretends to.

---

## 7. Digital delivery and PDF security

This is the actual product, so it gets the most care.

1. **Storage.** PDFs live in `App_Data/library/{bookId}/{sha256}.pdf` — outside
   `wwwroot`, unreachable by URL. `App_Data` is excluded from deployment mirroring
   (mirroring over it is how you delete your own catalogue).
2. **Grants.** On payment, one `DownloadGrant` per book: random GUID token,
   `ExpiresAt = now + Settings.LinkExpiryDays` (default 7), `MaxDownloads` (default 3).
3. **Serving.** `GET /download/{token}` validates token → state → expiry → count, then
   increments the counter inside a transaction and streams the file with
   `Content-Disposition: attachment`. Every hit writes a `DownloadLog` row with IP and
   user agent — which is what the admin Downloads screen already displays.
4. **Watermarking.** When enabled, stamp each page footer with buyer name, email and
   order number before streaming, using **PDFsharp 6.x** (MIT). Cache the stamped copy
   per grant so a 300-page book is stamped once, not three times.
   *(PdfPig — already used in KuwaitHR — reads but does not write; iText7 is AGPL.)*
5. **Abuse limits.** Rate-limit by token and by IP; a grant hit from more than N distinct
   IPs is flagged in the admin for manual revoke.

---

## 8. Admin control panel

Keep the existing admin HTML/CSS (it is good and bilingual) but serve it from Razor Pages
under `/admin`, protected by ASP.NET Core Identity cookie auth with
`[Authorize(Roles="Admin,Staff")]`. The API enforces authorisation independently, so a
leaked page is not a leaked database.

| Screen | Becomes |
|---|---|
| Login | Identity: hashed passwords, lockout after 5 attempts, optional TOTP 2FA |
| Dashboard | Real revenue/orders/downloads, 30-day sales from the DB |
| Books | Full CRUD + cover motif picker + **PDF upload** (size/type/magic-byte checked) + **bulk CSV import**; publish blocked until EN *and* AR text is present |
| Categories | CRUD with reorder; delete blocked while books reference it |
| Orders | Detail, Tap reference deep-link, **refund**, **resend download email** |
| Customers | Profile, order history, lifetime value, delete-my-data |
| Downloads | Live log with IP + count, **revoke grant**, re-issue |
| Settings | Persisted + encrypted; Tap keys write-only/masked |

Every mutating action writes an `AuditLog` row.

---

## 9. GitHub repository

**<https://github.com/sphearsravi/nexuskw> — public**, decided 2026-10-20.

A private repo was the original recommendation, but on a free account GitHub refuses
Actions runners, branch protection *and* secret scanning for private repositories, which
left the whole pipeline inert. Public gets all three at no cost. The trade is that the
source is readable: business logic, admin routes and the download-token scheme are all
visible, so **none of it may rely on being secret**. That is the right discipline anyway —
but it makes the rules below load-bearing rather than advisory.

What public means in practice:

- **No secret ever enters the repository.** Tap keys, the DB connection string and the
  FTP password live in App Pool environment variables on the server and in GitHub Actions
  secrets. A key in a commit means rotating the key, not deleting the commit.
- **Authorisation is enforced server-side, every time.** Knowing an admin URL or a
  download-token format must get an attacker nothing.
- **Download tokens are unguessable** (GUID) and checked against state, expiry and count
  on every request.
- Before go-live, re-read the diff of anything touching payments as though an attacker
  were reading it too, because they can.

Settings now applied:

- **Branching:** trunk-based. `main` always deployable; short-lived `feat/*` and `fix/*`
  branches merged by PR.
- **Protection on `main`:** the three CI checks (`Build and test`, `Secret scan`,
  `Dependency audit`) must pass, branch must be up to date, conversations resolved, no
  force-push, no deletion. `enforce_admins` is **off**, so the repo owner can still push
  directly in an emergency; GitHub records it as a bypass rather than refusing it. Day to
  day, **work goes through a `feat/*` branch and a PR from Phase 1 onward** — that is a
  convention backed by an audit trail, not a hard gate. Turn `enforce_admins` on if you
  want it to be one.
- **Secret scanning + push protection: enabled.** Plus a pinned `gitleaks` full-history
  scan in CI as a second line of defence.
- **`.gitignore`:** `bin/ obj/ publish/ App_Data/ *.user appsettings.*.local.json docs/DEPLOY_CREDENTIALS.md`
- Issue templates and a project board with the Phase 0–9 milestones above.

---

## 10. CI/CD

### `ci.yml` — every push and PR
`setup-dotnet 8.0.x` → restore → build `-warnaserror` → unit tests → integration tests
(LocalDB) → `dotnet format --verify-no-changes` → publish artefact → EF Core
**idempotent** migration script generated and uploaded as an artefact.
Independent job: `dotnet list package --vulnerable` + CodeQL.

### `deploy-staging.yml` — on merge to `main`
1. Apply the migration script to the staging DB over `sqlcmd` (SmarterASP accepts remote
   MSSQL connections — proved on the KuwaitHR migration).
2. Upload `app_offline.htm` so IIS releases the locked DLLs.
3. FTPS sync the publish output via `SamKirkland/FTP-Deploy-Action`, **excluding**
   `appsettings*.json` and `App_Data/**` — server-side config always wins.
4. Delete `app_offline.htm`.
5. Smoke test: `/health` returns 200 and the storefront title matches.
6. On smoke-test failure, redeploy the previous artefact.

### `deploy-prod.yml` — manual dispatch / tag `v*`
Identical, plus a GitHub **environment protection rule** requiring your approval, and a
DB backup taken before migrations run.

### Hosting caveat to settle first
App pool `sphearsravi-001` on SmarterASP is **32-bit, 512 MB, ASP.NET 4.x**, and already
shared by site3, KIA, MeetingAI, Shoptok, Ikarus and nexus. IIS allows **one in-process
.NET Core app per pool**, so Nexus must run `hostingModel="OutOfProcess"` — and it will
compete for 512 MB with five other apps while streaming PDFs. Options, best first:

1. **A dedicated SmarterASP plan/pool for Nexus** (~$5–10/mo). Recommended — a store that
   takes money should not share a 512 MB pool with five side projects.
2. Keep the shared pool for staging only; dedicated pool for production.
3. Move production to a small Azure App Service / VPS if traffic justifies it.

Secrets: `FTP_SERVER`, `FTP_USERNAME`, `FTP_PASSWORD`, `DB_CONNECTION`, `TAP_SECRET_KEY`
as GitHub Actions secrets per environment; application secrets set as App Pool
environment variables on the server (the pattern already used for the HR app).

---

## 11. Environments

| | Dev | Staging | Production |
|---|---|---|---|
| Host | Kestrel `localhost:5080` | SmarterASP temp URL | site6 + custom domain |
| DB | LocalDB / `RAVIWST\SQLEXPRESS` | SmarterASP SQL | SmarterASP SQL |
| Tap | test keys | test keys | **live keys** |
| Email | file-drop pickup dir | real SMTP, internal addresses only | real SMTP |
| Data | seeded | seeded + scrubbed | real |

---

## 12. Testing

- **Unit:** pricing and currency-scale rounding, order-number generation, **Tap hashstring against
  Tap's own documented sample**, grant expiry/exhaustion, i18n key completeness.
- **Integration:** `WebApplicationFactory` over a real DB — checkout creates a Pending
  order; a signed webhook pays it; a replayed webhook is a no-op; an unsigned webhook is
  rejected; a tampered amount is rejected; a download past its limit returns 410.
- **E2E (Playwright, both languages + RTL):** browse → cart → pay with Tap test KNET →
  success → download from the library. Reuse the patterns in `z-venue-e2e-tests`.
- **Manual before go-live:** real KWD 1.000 charge, real refund, Apple Pay on a device,
  Arabic checkout end to end.

---

## 13. Operations

`/health` (DB + disk + Tap reachability); Serilog to rolling files plus a daily error
digest; nightly DB backup + weekly off-site copy; a one-page runbook covering "webhook
stopped arriving", "customer did not get their link", "refund a charge", and "roll back a
deploy".

---

## 14. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Hashstring mismatch (KWD 3-dp) silently rejects every webhook | No order ever completes | Unit-test against Tap's documented sample before writing any other payment code; alert on any rejected webhook |
| Shared 512 MB 32-bit pool | Recycles mid-download; memory pressure | Dedicated pool (§10); stream, never buffer, PDFs |
| DataProtection keys not persisted | Admin logged out every deploy; antiforgery breaks | `App_Data/dpkeys` in Phase 8 — the exact RentCollection failure |
| Deploy mirrors over `App_Data` | Catalogue PDFs deleted | Explicit FTP exclusions; verified in staging first |
| PDF piracy | Revenue leak | Watermarking, expiring 3-use links, IP logging, revoke |
| Tap merchant account not approved in time | Blocks Phase 9 only | Everything else is built and tested on the Tap **test** account; apply now so approval lands before Phase 9 |
| Rights to the titles sold | Legal | Real books confirmed; the 20 demo titles are placeholders and must all be replaced via the admin before go-live |

---

## 15. Decisions — locked 2026-10-20

| # | Decision | Consequence |
|---|---|---|
| 1 | **Real books will be sold**, delivered as **download only** | No shipping, no addresses, no physical fulfilment anywhere in the model. The 20 demo titles are placeholders and will be replaced via the admin. |
| 2 | **The catalogue grows ~50 titles a year** | Book CRUD with PDF upload is core, not nice-to-have; add **bulk CSV import** (§8). Search/filter/sort run against the DB, not a hardcoded array — but at this size no paging is needed. |
| 3 | **Tap test account** for all build phases | `sk_test_…` through Phases 3–8. Live keys are a Phase 9 configuration switch, no code change. |
| 4 | **SmarterASP.NET** hosting | Confirms .NET 8 + IIS + `OutOfProcess` + FTPS deploy (§10). |
| 5 | **Customer accounts _and_ guest checkout, plus admin accounts** | Three identities — see §4.1. One Identity store, three roles. Guest orders are claimable later by email. |
| 6 | **Arabic is a first-class language**, not a veneer | Every book needs AR title/author/description before publish; admin validates this. AR emails, AR invoices, RTL throughout. |

### Still open (not blocking — decide before Phase 9)

- **Dedicated app pool vs the shared 512 MB `sphearsravi-001` pool** (§10). Recommended:
  dedicated for production. Staging can live on the shared pool.
- **Custom domain** vs staying on the ctempurl address.
- SMTP provider for transactional email (Phase 5).

### Explicit non-goals

Physical goods, shipping, inventory counts, subscriptions, multi-vendor, print-on-demand.
