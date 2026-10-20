# Catalogue — Phase 1 requirements

SDLC phase 1 (requirements) for **Phase 1 — Catalogue** in PRD §12 and PLAN §5.
Written 2026-10-20 against the demo as it stands in `src/Nexus.Web/wwwroot`.

Scope reminder, already decided and not reopened here: **Kuwait only, KWD only**
(PRD §4 "Deferring the UAE"), **Arabic is a first-class edition** (PRD §5, PLAN
§15 decision 6), storefront markup unchanged, catalogue only — cart, checkout,
payment, accounts, delivery and the admin UI are later phases.

---

## 1. The problem

The **first-time buyer** (PRD §3) arrives from a link on a phone, in Arabic as
often as English, and wants to find one book and look at it. Today that works,
but only because the twenty books and six categories are a JavaScript array,
`wwwroot/assets/js/data.js`, downloaded by every visitor. Nothing the **store
operator** does can change what a buyer sees — adding a title means a developer
and a deployment, which is the opposite of the test PRD §9 sets ("the owner
runs the store for a month without a developer"). The array is also the reason
every later phase is blocked: an order cannot reference a book that only exists
in the browser, and a price the client supplies cannot be trusted (PRD PM-3).

This phase moves books and categories behind the server so that the buyer's
experience is identical, the operator's edits are possible at all, and orders
have something real to point at. The buyer should notice nothing. That is the
point.

---

## 2. Requirements

### Catalogue content and publication

| # | Requirement | Acceptance criterion | Priority |
| --- | --- | --- | --- |
| CAT-01 | Books and categories are served by the application, not shipped to the browser as source | With `data.js` deleted, `shop.html` and `book.html` render the same twenty books; no book title, author, description, price, page count or year appears in any file served from `wwwroot` | **must** |
| CAT-02 | Only published books are visible to the public | A book switched to unpublished disappears, on the next page load, from the shop grid, the header subject menu counts, the home shelf and "more in this subject"; its detail URL returns the not-found state of CAT-20 | **must** |
| CAT-03 | A book cannot be published without Arabic title, author and description (PRD §5, "Language is a hard requirement") | Attempting to publish a book missing any of the three is refused with a message naming the missing field; no published book is ever returned with an empty Arabic title, author or description | **must** |
| CAT-04 | Every published book sits under exactly one category, and that category is active (PRD ST-1) | A book whose category is missing or deactivated cannot be published, and deactivating a category that still has published books is refused | **must** |
| CAT-05 | A book has a stable, shareable address (PRD ST-4) | `book.html?id=3` resolves to the same book after a redeploy and after the seed is re-run; book identifiers are never reassigned | **must** |
| CAT-06 | A category carries an English name, an Arabic name, a display colour, an operator-defined position and an active flag | The header subject menu, the shop filter dropdown and the home subject grid all list the active categories in the same order with the same colours as today; changing the position changes all three | **must** |
| CAT-07 | Each category reports how many published books it holds | The number beside each subject in the header menu and on the home grid equals the count of published books in that category, and changes when a book is published or unpublished | **must** |
| CAT-34 | A book cannot be published at a zero or negative price (decision D7) | Publishing a book priced `0.000` or below is refused with a message. No free book can reach checkout, so Phase 3 never has to handle a zero-amount charge | **must** |
| CAT-35 | The twenty demo titles are seeded into every environment, but **unpublished in production** (decision D4) | A fresh production database holds the six categories and all twenty books with `IsPublished = false`; the storefront shows an empty catalogue until the operator publishes a title. Dev and staging seed them published so the storefront is usable | **must** |

### Browse, search, filter and sort

| # | Requirement | Acceptance criterion | Priority |
| --- | --- | --- | --- |
| CAT-08 | The shop page lists every published book in the existing grid | With the demo's twenty books seeded, `shop.html` renders twenty cards visually identical to today's, including the drawn covers | **must** |
| CAT-09 | Search matches title and author in **both** languages, whatever language the interface is in (PRD ST-2) | `المحفظة` returns *The Patient Portfolio* with the interface in English **and** in Arabic; `Patient` does the same in both. Today the demo searches only the active language, so an Arabic reader cannot find a book by its English title | **must** |
| CAT-10 | Arabic search tolerates the normal orthographic variation | `المحفظه الصبوره` returns the same single result as `المحفظة الصبورة`; alef forms (ا أ إ آ), yaa / alef maqsura (ي ى), taa marbuta (ة ه) and diacritics do not change the result set | **should** |
| CAT-11 | Category filter and search combine | Category = Islamic finance plus the query `صكوك` returns only *Sukuk for Beginners*, and the result count shown matches the number of cards rendered | **must** |
| CAT-12 | Four sort orders — most bought, newest, price low to high, price high to low — in the same order and with the same labels as today | Each ordering is total: two identical requests return the same sequence, ties included. **"Newest" sorts on the date the book was added to the store (D1)**, not its publication year, so a 2024 title added today ranks above a 2025 title added last year; the year stays a displayed fact and the tiebreak. **"Most bought" reads a sales count held on the book (D2)**, seeded from the demo figures and maintained from paid orders in Phase 2 | **must** |
| CAT-13 | Search, filter and sort survive a reload and can be shared (PRD ST-3, "Filters combine and survive a reload") | Set a category, a sort and a search term, copy the address, open it in a new tab: same result set, same three controls populated. Today the demo reads `q` and `cat` from the address on first load but never writes them back, and sort always resets to "most bought", so this fails | **must** |
| CAT-14 | Books can be filtered by price range (PRD ST-3 names price among the filters) | A minimum and a maximum in KWD can be applied and combine with category and search. **Decided (D3): ships in this phase** — it is one control added to the existing toolbar, and deferring it means reopening the catalogue for a requirement the PRD already states | **should** |
| CAT-15 | The first screen of results does not require downloading the whole catalogue | With 500 published books seeded, the shop page shows its first full screen of results within 2 seconds on a throttled "slow 3G" profile, and the data fetched for that first screen does not grow with the size of the catalogue. **Assumption (D8, unconfirmed):** the catalogue reaches hundreds, not tens of thousands | **should** |
| CAT-16 | The empty-result state is preserved | A query matching nothing shows today's bilingual empty panel with a working "clear filters" action that restores the full list | **must** |
| CAT-17 | If the catalogue cannot be read, the page says so in the visitor's language | With the catalogue source unavailable, `shop.html` and `book.html` show a localised message; the header, footer and language switch still work; the browser console shows no uncaught error and the page is not blank | **must** |

### Book detail

| # | Requirement | Acceptance criterion | Priority |
| --- | --- | --- | --- |
| CAT-18 | The detail page shows category, title, author, description, page count, publication year and price from the catalogue, in the active language | Every one of those seven values matches what the operator stored, in both languages, with the existing layout unchanged | **must** |
| CAT-19 | "More in this subject" offers other published books from the same category | Up to four cards, never the book being viewed, never an unpublished book, never the same book twice; fewer than four is rendered without a gap | **must** |
| CAT-20 | An unknown, unpublished or malformed book address produces an honest not-found state | `book.html?id=9999`, `?id=abc` and the id of an unpublished book each show a bilingual "not found" panel with a link back to the shop. Today the demo falls back to `BOOKS[0]`, so `?id=9999` silently displays *The Patient Portfolio* — a shareable URL that lies about what it points to | **must** |
| CAT-21 | Covers still render with no image asset | Every card and detail cover draws as today: category colour, category motif, title and author text, per-book angle variation; no network request for an image | **must** |
| CAT-22 | A category the operator adds after launch gets a usable cover without a code change | A seventh category is created and a book published under it; its cards render with a legible cover in its chosen colour rather than a blank or broken one. Today the motif set is a hardcoded map keyed by the six demo category slugs | **should** |
| CAT-23 | The detail page stops asserting per-book facts it does not hold | The fixed `fmt_val` string is removed in this phase: today it reads "PDF, 6.2 MB" for all twenty books, and its Arabic form is `PDF، ٦٫٢ ميغابايت` — Arabic-Indic digits, which PRD §5 forbids outright. Real size and format arrive with the file in Phase 4 (CAT-33). "Language" becomes a stored per-book field, not an interface string, so an English-only title can say so | **must** |
| CAT-36 | The dead "Read first pages" control is removed (decision D6) | Neither the button nor the sample panel appears on the detail page. PRD §4 rules out an in-browser reader, so a control that does nothing is a defect, not a placeholder. A real sample is a separate feature with its own rights question | **must** |

### Money and language

| # | Requirement | Acceptance criterion | Priority |
| --- | --- | --- | --- |
| CAT-24 | Every price is KWD at three decimals, in Western digits (PRD ST-7, §5 "Digits stay Western") | A 6.5 KWD book renders `KD 6.500` in English and `6.500 د.ك` in Arabic — never `6.50`, never `٦٫٥٠٠` | **must** |
| CAT-25 | No currency switcher, no AED, no conversion rate and no VAT anywhere on the storefront (PRD §4, ST-7, PM-10) | The KWD/AED control is gone from the header; a search of `wwwroot` finds no conversion rate, no AED symbol and no VAT row; a stored currency preference from an earlier visit does not resurrect any of them | **must** |
| CAT-26 | The pages that share the catalogue helpers keep working | After `data.js` is removed, `index.html`, `cart.html`, `checkout.html`, `library.html` and `success.html` still render with no uncaught error. They all resolve books and prices through the same shared helpers, so they break with the catalogue even though their own features belong to later phases | **must** |
| CAT-27 | Switching language flips all catalogue text without losing the visitor's place (PRD ST-6) | On the shop page with a category, sort and query applied, switching to Arabic keeps the same filtered set and the same control values, flips direction to RTL, and switches every title, author, description and category name to Arabic | **must** |
| CAT-28 | Interface copy stops hardcoding how many books exist | With twenty-one books seeded, no page claims twenty: "Twenty titles across six subjects", "See all 20 titles" and "clear the filter to see all twenty titles" are either driven by the real counts or reworded, in both languages | **must** |
| CAT-29 | Reading order, keyboard operation and contrast are no worse than today in either direction (PRD §10) | The shop and detail pages are fully keyboard operable and pass an automated accessibility check with no new violations, run in both LTR and RTL | **should** |

### Operability

| # | Requirement | Acceptance criterion | Priority |
| --- | --- | --- | --- |
| CAT-30 | An operator edit is visible to buyers without a deployment | A price change, a publish and an unpublish are each visible on the storefront within 60 seconds of being saved, with no deployment and no restart; no cached copy serves a superseded price after that window | **must** |
| CAT-31 | A book can override its category's cover motif | An operator picks a different motif for one book and only that book's cover changes | **could** |
| CAT-32 | The detail page can show the ISBN when one exists | A book with an ISBN shows it among the facts; one without shows no empty row | **could** |
| CAT-33 | The detail page shows the real file size and format once the PDF exists | The facts list reflects the actual stored file rather than a fixed string. Depends on Phase 4 | **could** |

**Count: 28 must, 5 should, 3 could — 36 total.**

---

## 3. Scope boundary

**Not in this phase, and why:**

| Not included | Why |
| --- | --- |
| Cart behaviour, the one-copy-per-book rule (ST-5) | Phase 2. The catalogue only has to provide a stable book identity and a server-held price for the cart to reference |
| Order creation, server-side totals, pricing engine | Phase 2 (PLAN §5) |
| Payment of any kind | Phase 3. `checkout.html`'s card form stays untouched and fake in this phase; it is deleted in Phase 3, not here |
| PDF files, download grants, watermarking, the real library | Phase 4. `library.html` keeps reading `localStorage`; entitlements move server-side later |
| Customer accounts, sign-in, preferences stored per profile | Phase 6. Language choice stays a browser-local preference in this phase |
| The admin screens that create and edit books and categories, including bulk import | Phase 7. This phase defines what *publication* means and what the storefront may show; the screens that set it come later. Until then, catalogue content arrives by seed |
| Reviews, ratings, recommendations, an in-browser reader, a sample-chapter file | Out of product scope entirely (PRD §4) |
| Any currency other than KWD, FX, VAT, country selection | Out of v1 (PRD §4) |

**Existing behaviour that must not change:**

- The markup of `shop.html` and `book.html`, and the visual result of both.
- The drawn covers: colour by category, motif by subject, per-book variation.
- The four sort options, their labels, their order, and the default ("most bought").
- `shop.html?cat=<slug>` and `shop.html?q=<text>` as entry points — the header
  subject menu and the header search box both produce them.
- `book.html?id=<n>` as the shareable address of a book.
- Language switching from any page, with RTL applied to the whole document.
- The home page shelf (top four by popularity) and subject grid.

---

## 4. Bilingual and currency impact

**Bilingual — substantial.**

- Every book carries a full Arabic edition of title, author and description, and
  Arabic completeness is the gate on publication (CAT-03). The twenty demo books
  already have complete Arabic text; the production seed must not lower that bar.
- Every category carries both names (CAT-06).
- New interface strings in this phase are the not-found panel (CAT-20) and the
  catalogue-unavailable message (CAT-17). Both land in EN and AR in the same
  change, per CLAUDE.md rule 4.
- Existing strings that must be corrected in both languages: the count-bearing
  copy in CAT-28, and the Arabic-Indic digits in `fmt_val` flagged in CAT-23.
- Search must work across both languages regardless of interface language
  (CAT-09) and tolerate Arabic orthographic variation (CAT-10). This is the one
  place where bilingual support is a behaviour rather than a pair of strings.
- Sorting Arabic titles alphabetically is **not** required: none of the four
  sort orders is alphabetical, and none should become one in this phase.

**Currency — KWD only, no VAT.**

- Prices are KWD at three decimals, stored and displayed at that scale
  (PRD ST-7, PLAN §6.4, CLAUDE.md rule 1). Three decimals is not cosmetic: the
  same scale is what the Tap signature hashes in Phase 3, so the catalogue is
  where the habit is set.
- Digits stay Western in both languages. Symbol placement differs: `KD 6.500`
  before the number in English, `6.500 د.ك` after it in Arabic.
- No VAT line appears anywhere in this phase (PRD PM-10). The demo's cart and
  checkout VAT rows are driven by the AED switch and disappear with it (CAT-25).
- No price is ever accepted from the browser. The catalogue is read-only to the
  public, so this phase has nothing to recompute — but it is the phase that
  makes recomputation possible in Phase 2.

---

## 5. Edge cases

1. **An Arabic-only visitor searching in Arabic for an English-titled book.**
   Covered by CAT-09; the demo fails this today.
2. **A query mixing scripts or digits** — `صكوك 2024`, or Arabic-Indic digits
   typed on an Arabic keyboard. Must not error; a no-match is an acceptable
   answer, a stack trace is not.
3. **A book unpublished while someone is looking at it.** The open tab keeps
   working until it reloads; the next load shows not-found (CAT-20). Nothing in
   this phase may make an already-purchased book unreachable — but order history
   belongs to Phase 2 and its title snapshot, not here.
4. **A book unpublished after someone bought it** (the PRD's own example). Out
   of this phase's reach because orders do not exist yet, but it is the reason
   CAT-02 hides the book from browsing rather than deleting it.
5. **A category deactivated or reordered while it still holds published books.**
   Refused by CAT-04; reordering is always allowed and changes all three places
   that list categories.
6. **A fresh database with nothing published.** The shop shows the empty state,
   the subject menu shows zero counts, the home shelf renders without error. The
   store looks empty, not broken.
7. **A price edited between the shop grid and the detail page.** Both read live,
   so the buyer may see two different prices in one session. Acceptable in this
   phase; it stops mattering in Phase 2, where the order captures the price.
8. **A stale cart in `localStorage` referencing a book that is now unpublished
   or deleted.** The cart page must not crash on an unresolvable id. The cart's
   own correct behaviour is Phase 2; not crashing is this phase's problem,
   because this phase is what makes ids unresolvable (CAT-26).
9. **A guessed or tampered book address** — negative, enormous, non-numeric, or
   the id of an unpublished book. All resolve to the same not-found state, and
   none reveals that the id exists (CAT-20, PLAN §9: the repository is public).
10. **A very long Arabic title** in the fixed-size drawn cover. Must clip or
    wrap within the cover rather than overflow it; the demo's longest title
    (`تصفية الأسهم المتوافقة مع الشريعة`) is the current worst case and a
    longer one is plausible.
11. **A category with one book**, so "more in this subject" is empty. The
    section renders empty or is hidden — never four duplicates of the book being
    viewed.
12. **Two books with identical sales counts or the same publication year.**
    Covered by the determinism criterion in CAT-12; without a tiebreak, paging
    silently duplicates and drops rows once CAT-15 lands.
13. **Local storage blocked** (private browsing, locked-down device). The
    existing in-memory fallback must keep working; the catalogue must not start
    depending on storage being writable.
14. **A price of 0.000.** Not currently possible and probably should not be —
    see Q7.

---

## 6. Decisions

All nine questions raised at analysis are now settled. Four were answered by the
product owner on 2026-10-20 (D4, D5, D7 and — partly — D8); the rest were taken
on the analyst's recommendation with no objection.

| # | Question | Decision | Settled by |
| --- | --- | --- | --- |
| D1 | What does "newest" sort on? | **Date added to the store**, not publication year. The year stays a displayed fact and the tiebreak | Recommendation (CAT-12) |
| D2 | What does "most bought" sort on before real orders? | **A sales count held on the book**, seeded from the demo figures, maintained from paid orders in Phase 2 — so nothing on the storefront changes when real sales start | Recommendation (CAT-12) |
| D3 | Does the price filter ship this phase? | **Yes.** One control in an existing toolbar; deferring means reopening the catalogue for a requirement the PRD already states | Recommendation (CAT-14) |
| D4 | Seed the twenty demo titles into production? | **Yes — but unpublished in production.** They exist so the operator can edit rather than retype, and nothing unlicensed is ever offered for sale. Dev and staging seed them published | **Product owner** (CAT-35) |
| D5 | One bilingual PDF per book, or separate editions? | **One PDF per book**, containing both languages. A book is one saleable thing with one file | **Product owner** (CAT-23, and Phase 4) |
| D6 | Does v1 ship a readable sample? | **No.** The dead "Read first pages" control is removed rather than left in place. A real sample is a separate feature with its own rights question | Recommendation (CAT-36) |
| D7 | Can a book be free? | **No.** Zero and negative prices are refused at publish, which keeps zero-amount charges out of Phase 3 entirely | **Product owner** (CAT-34) |
| D8 | How large will the catalogue get? | **Assumed hundreds, not tens of thousands.** Server-side paging and filtering are built anyway — they are inexpensive here and the PRD commits to a growing catalogue | Assumption — see below |
| D9 | May the storefront cache the catalogue? | **Yes, 60 seconds.** Trades the operator seeing an edit immediately against load on a shared 512 MB app pool | Recommendation (CAT-30) |

### The one assumption still carrying risk

**D8 was not answered.** The reply addressed delivery format — which is settled,
and is D5 — rather than catalogue size. The question stands: *how many titles
will the store hold, and how soon?*

It is not blocking. CAT-15 is a **should**, and the work is built either way
because server-side paging costs little at this stage and removing it later is
easy. It matters only if the honest answer is tens of thousands, in which case
search and indexing need designing rather than assuming, and the architect
should know before Phase 2.

Ask again before the architect sizes CAT-15.
