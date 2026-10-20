# Nexus — Product Requirements

Working copy for the build. The living version, where comments and edits go, is
at <https://claude.ai/code/artifact/5f0cdc9c-f713-4adf-8900-a2326a2ce4b7>.
Keep the two in step; `tech-writer` owns that.

As of 2026-10-20.

---

## 1. Summary

Nexus sells finance and business books as PDFs to readers in **Kuwait**, in
Arabic and English, paid through Tap and delivered as an instant download.
There is no physical product, no shipping and no inventory.

> **Scope, decided 2026-10-20: Kuwait only for now.** One country, one currency
> (KWD), no VAT, KNET first. The UAE and AED are deferred, not abandoned — see
> §4. This takes FX conversion, dual-currency pricing, a VAT engine and a
> currency switcher out of v1: roughly a week of build, and a permanent
> reduction in the number of ways money can be wrong.

Shipped means four things work together: a storefront that reads equally well
in both languages, Tap taking real money in KWD, every paid order
producing a watermarked PDF behind an expiring link, and an operator who can
add a book without calling a developer.

Today there is a clickable front-end demo and nothing behind it. The catalogue,
orders, customers and download log are arrays in a JavaScript file; the payment
form is decoration.

---

## 2. Problem and opportunity

A Kuwaiti reader who wants a book on Boursa Kuwait dividends or Murabaha
structuring is poorly served. International stores carry little Gulf-specific
finance writing, rarely sell in KWD, and almost never offer the Arabic edition
alongside the English one.

The generic platforms do not fit either, for structural reasons:

| Constraint | Why the usual platform fights it |
| --- | --- |
| KWD is a three-decimal currency | Most storefronts assume two. Prices round wrong and gateway signatures fail |
| KNET is the default way Kuwaitis pay online | Treated as an exotic add-on, where supported at all |
| Arabic is an equal edition, not a veneer | Themes bolt RTL on; book metadata stays single-language |
| Digital goods need watermarking and expiring links | Plugin territory, with the licence terms that implies |
| Kuwait levies no VAT on these sales | Platforms assume a tax engine; ours is simply absent |

Each is solvable with enough plugins. Together they argue for owning the stack,
which also keeps the margin on a product with no unit cost.

---

## 3. Users

**The first-time buyer** arrives from a link, wants one book, and does not want
an account to get it. Pays with KNET on a phone, in Arabic as often as English.
*Get this book onto my phone in under two minutes, in my currency, without
signing up.* Guest checkout exists for them; nothing may block on registration.

**The returning reader** has bought before. Their link expired, or they changed
phone. *Find everything I have ever bought and open it again.* An account gives
them a permanent library — and must absorb their earlier guest purchases, or
the account starts empty and looks pointless.

**The store operator** is the business owner, not an engineer. *Add a book, see
what sold, fix a customer's problem today.* All of it without a deployment.

**The author or rights holder** is not a user but matters: watermarking,
download limits and revocation exist mostly to make Nexus somewhere they are
willing to publish.

---

## 4. Scope

**In:** catalogue with search, filter and sort; cart; guest checkout and
customer accounts; Tap payment by KNET, card and Apple Pay, priced in KWD;
watermarked PDF delivery through expiring, count-limited links; bilingual order
and delivery emails; refunds; a full control panel with bulk import; an audit
trail on every administrative change.

**Out, with reasons:**

| Not building | Why not |
| --- | --- |
| Physical books, shipping, addresses | The product is a download |
| Subscriptions | Needs a catalogue deep enough to justify a recurring fee |
| Multi-vendor / self-publishing | Doubles the admin surface; adds payouts, tax, rights |
| In-browser reader | Buyers want the file; a reader invites a DRM argument |
| Mobile app | The storefront is responsive |
| Reviews and ratings | Needs enough traffic to be credible |
| Coupons, gift cards | Easy to add later against a working order model |
| **UAE / AED / VAT** | **Deferred, not abandoned** — see below |

The boundary: anything that changes **how money is taken** or **how a file is
delivered** belongs in v1, because retrofitting either is expensive.

### Deferring the UAE

v1 sells to Kuwait in KWD only. No currency switcher, no FX rate, no VAT
calculation, no country selection at checkout.

What stays in place so adding the UAE later is a configuration and UI change
rather than a migration:

- `Nexus.Domain.Currency` already defines AED at two decimals and is tested.
  It stays.
- `Order` keeps `Currency`, `FxRate`, `VatRate`, `VatAmount` and
  `TotalCharged`. In v1 currency is always KWD, FX is 1 and VAT is 0. Three
  unused columns is a cheap price for not having to rewrite the order model.
- Tap's hosted page already offers cards and Apple Pay alongside KNET, so no
  gateway work is needed to accept a UAE card later.

What a UAE launch would then need: a currency switcher, an FX rate in
Settings, a VAT rule by country, AED invoice formatting, and a decision about
whether prices are converted or set per market.

---

## 5. Storefront requirements

| # | Requirement | Acceptance |
| --- | --- | --- |
| ST-1 | Browse by category | Every published book sits under exactly one category |
| ST-2 | Search title and author in either language | Arabic query matches Arabic metadata |
| ST-3 | Filter by category and price; sort by newest, price, popularity | Filters combine and survive a reload |
| ST-4 | Book detail page | Reachable by a stable, shareable URL |
| ST-5 | Cart with multiple books | Same book cannot be added twice — it is a file, not a quantity |
| ST-6 | Switch language anywhere | Whole interface flips to RTL without losing place or cart |
| ST-7 | All prices shown in KWD at three decimals | `6.500`, never `6.50`; no currency switcher in v1 |
| ST-8 | Works on a phone | Primary target; KNET buyers are overwhelmingly mobile |

**Language is a hard requirement.** A book may not be published until its
Arabic title, author and description exist. The admin enforces this at publish.

**Digits stay Western** — `6.500`, never Arabic-Indic numerals, in either
language. This matches Gulf banking interfaces and keeps what the gateway
receives identical in both.

---

## 6. Accounts and library

| # | Requirement | Acceptance |
| --- | --- | --- |
| AC-1 | Buy as a guest with name, email, country | No account created, purchase completes |
| AC-2 | Register, confirm, sign in, reset — bilingual | Every email in the customer's language |
| AC-3 | Permanent library for signed-in customers | Survives link expiry |
| AC-4 | Customer re-issues an expired link themselves | No support request for the commonest complaint |
| AC-5 | Order history with printable invoice | Matches what Tap charged, to the fils |
| AC-6 | Profile holds preferred language | Applied on next sign-in, any device |
| AC-7 | Guest purchases merge on confirmed registration | Buy as guest, register later, book is there |
| AC-8 | Admin and staff sign in separately from customers | Separate login, separate session, no shared cookie |

**AC-7 has a security edge:** the merge fires only after the email is
*confirmed*. Otherwise registering as someone else's address hands over their
purchases.

**AC-8 exists because the blast radius differs.** A compromised customer
session loses one reader's books; a compromised admin session loses the
catalogue.

---

## 7. Payments

Tap covers KNET, cards and Apple Pay from one integration.

The flow: checkout saves a `Pending` order → the server creates a charge at Tap
with the total recomputed server-side → the customer pays on Tap's hosted page
→ **two things then return independently.** The browser redirect comes back
only if the tab survived, and it re-reads the charge to display a result,
writing nothing. The webhook arrives server to server, is retried, and is the
only thing that marks the order paid, issues the download links and sends the
email.

| # | Requirement | Acceptance |
| --- | --- | --- |
| PM-1 | Pay by KNET, card or Apple Pay | One hosted page offers all three |
| PM-2 | Card details never reach our servers | No card field in our own forms |
| PM-3 | Total recomputed server-side | A tampered client total is ignored |
| PM-4 | Only a verified webhook marks an order paid | Closing the tab still completes the order |
| PM-5 | Signatures verified before processing | Unsigned or mismatched rejected and logged |
| PM-6 | Replays change nothing | Same notification twice → one order, one email |
| PM-7 | Amount and currency re-checked against our order | Valid signature, wrong amount, still refused |
| PM-8 | Stuck orders reconciled automatically | Pending over 30 minutes settled or failed nightly |
| PM-9 | Refunds revoke download grants | Refunded customer loses access |
| PM-10 | No VAT line on a Kuwait order | Invoice shows the KWD total only; the field stays in the model for later |

**The three-decimal rule is a product risk.** Tap signs each notification with
an HMAC over the amount formatted at the currency's scale. KWD has three
decimals. Send `6.5` where Tap hashed `6.500` and every check fails — silently,
with nothing wrong on the storefront and no order completing. Pinned by tests
before any payment code runs; a non-zero rejection rate raises an alert.

---

## 8. Digital delivery

| # | Requirement | Acceptance |
| --- | --- | --- |
| DL-1 | PDFs stored outside the web root | No URL returns a PDF without a valid grant |
| DL-2 | One grant per book per paid order | Unguessable token, 7-day expiry, 3 downloads by default |
| DL-3 | Token, state, expiry and count checked, then incremented | The fourth download of a 3-use grant is refused |
| DL-4 | Every download logged with time, IP, user agent | Visible in the admin |
| DL-5 | Per-order watermark when enabled | Buyer name, email, order number on every page |
| DL-6 | Expiry and limits are settings | Changed without a deployment |
| DL-7 | A refund revokes that order's grants | Refunded customer cannot keep downloading |
| DL-8 | Grants used from many IPs are flagged | Operator can revoke and re-issue |

**Watermarking is a social deterrent**, not a technical one. It makes casual
sharing personally attributable, which is what changes behaviour — and it is
what makes an author willing to hand over a PDF.

**Limits must be invisible to honest buyers.** Three downloads over seven days
covers a phone, a laptop and a mistake, and AC-4 lets them re-issue.

**`App_Data` is excluded from deployment.** A release that mirrors over it
deletes every book in the store.

---

## 9. Control panel

The test: the owner runs the store for a month without a developer.

| Screen | Must do |
| --- | --- |
| Sign in | Hashed passwords, lockout, optional 2FA |
| Dashboard | Real revenue, orders, downloads, 30-day trend |
| Books | CRUD, cover and PDF upload, **bulk import** |
| Categories | CRUD and reorder; delete blocked while referenced |
| Orders | Detail, Tap reference, **refund**, **re-send link** |
| Customers | Profile, history, lifetime value, delete-my-data |
| Downloads | Live log with IP and count, **revoke**, re-issue |
| Settings | Tap keys, expiry, limit, watermark, email template |

**Bulk import is a requirement.** Adding books one form at a time stops being
viable around the thirtieth, and the catalogue is expected to grow.

**Three actions resolve most support requests** and must exist from day one:
re-send the email, re-issue the link, refund the order.

Every administrative write is audited: who, what, before, after, when.

---

## 10. Non-functional requirements

| Area | Requirement |
| --- | --- |
| Bilingual parity | Every string, email and error in both languages. A missing Arabic string is a bug |
| Money | `decimal` throughout; KWD at 3 decimals, rounded at that scale |
| Payment integrity | Totals recomputed server-side; client totals ignored |
| Security | No card data on our servers; authorisation per request; rate limits on checkout, download, sign-in |
| Secrets | Server environment configuration only; never in the repository |
| Availability | A failed deploy must not strand the store; releases roll back |
| Session durability | A deploy must not sign everyone out |
| Performance | Fast on mobile; PDFs stream rather than buffer |
| Accessibility | Keyboard navigable, sufficient contrast, correct reading order both directions |
| Data protection | Customer data deletable on request; order records retained for accounting |
| Auditability | Payment state changes and admin writes recorded and queryable |

**The repository is public,** so nothing may depend on an attacker not knowing
a URL, a token format or a route.

---

## 11. Success metrics

| Measure | Why | Target |
| --- | --- | --- |
| Checkout completion rate | Clearest signal that payment or currency handling is wrong | Floor agreed before launch |
| Payment failure by method | Separates KNET problems from card problems | Below the gateway baseline |
| Webhook rejection rate | **Must be zero.** Anything else means orders are silently lost | 0 — alert on any |
| First-attempt download success | Delivery is the product | Near 100% |
| Support requests per 100 orders | Whether re-send and re-issue work | Falling month on month |
| Share of orders in Arabic | Tests the core bet | Hypothesis now, check at 90 days |
| Guest-to-account conversion | Whether the library brings people back | Set after month one |
| Repeat purchase at 90 days | The real test of catalogue depth | Set after launch |

The Arabic share is the interesting one: the whole premise is that a Gulf
reader wants this content in their own language.

---

## 12. Release plan

About 35 working days for one developer, roughly seven to eight weeks.
Phase 0 is complete. Everything through Phase 8 runs on the Tap **test**
account, so merchant approval gates only the final two days.

| # | Phase | Days | What a stakeholder can see |
| --- | --- | --- | --- |
| 0 | Foundations | 2 | ✅ Storefront runs on real infrastructure; the pipeline works |
| 1 | Catalogue | 4 | Books come from a database |
| 2 | Cart and orders | 3 | An order exists in KWD with the right total |
| 3 | Payments | 5 | Money moves in test mode; orders complete by themselves |
| 4 | **Delivery** | 4 | **A real purchase produces a real, watermarked, expiring download** |
| 5 | Email | 2 | The customer gets their link without being on the site |
| 6 | Customer accounts | 4 | A buyer returns and finds everything they own |
| 7 | Control panel | 6 | The owner adds a book, refunds, revokes — unaided |
| 8 | Hardening | 3 | Rate limits, rollback, backups, no sign-outs on deploy |
| 9 | Go-live | 2 | Live keys, a real charge, a real refund |

The product first exists end to end at **day 18**, the end of Delivery.

Order matters: payments before delivery, because a delivery mechanism with
nothing to trigger it cannot be tested honestly; delivery before email, because
an email is worthless without a working link; the control panel late, because
it is the largest phase and the only one that can be worked around manually.

---

## 13. Risks and open questions

| Risk | If it happens | Mitigation |
| --- | --- | --- |
| Amount formatted at the wrong currency scale | No order ever completes, nothing looks broken | Pinned by tests before payment code; alert on rejections |
| Merchant account not approved in time | Launch slips | Built and tested on the test account; gates Phase 9 only |
| Deploy deletes the stored catalogue | Every book gone | `App_Data` excluded; verified on staging |
| Shared hosting capacity | Recycles mid-download | Decide on dedicated capacity before launch |
| Piracy | Revenue leak | Watermarks, expiring limited links, IP logs, revocation |
| Rights to titles | Legal exposure | Written rights confirmed per title before publishing |
| Thin catalogue at launch | Low repeat purchase | Bulk import lets it grow fast once rights settle |

**External dependencies:** an approved Tap merchant account, a transactional
email provider, the PDFs with rights cleared, and Arabic text for every title.

### Settled since drafting (2026-10-20)

- **A book is one bilingual PDF**, not an Arabic edition and an English edition.
  One catalogue entry, one file, one purchase.
- **No free books.** A zero or negative price is refused at publish, which keeps
  zero-amount charges out of the payment path entirely.
- **The demo titles are seeded everywhere but unpublished in production**, so the
  operator edits rather than retypes and nothing unlicensed is ever offered for
  sale. Rights are still confirmed per title before publishing.
- **The catalogue grows by about fifty titles a year** — roughly 150 after three
  years. Small enough that the storefront needs no paging; see CAT-15 and
  CAT-37 for the threshold at which that is revisited.

### Open questions

- [ ] Are rights cleared **in writing** for each title before it is published?
- [ ] Dedicated hosting capacity, or launch on the shared pool?
- [ ] Which transactional email provider, from which domain?
- [ ] Custom domain at launch, or the temporary address?
- [ ] Who sets the launch targets in §11?
- [ ] Is Arabic text written in-house or commissioned, and at what lead time?

Rights and hosting block launch. The rest shape it.
