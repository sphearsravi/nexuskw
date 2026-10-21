# 0001 — Kuwait only, KWD only, for v1

**Status:** Accepted, 2026-10-20

## Context

The demo modelled two markets: KWD and AED, a currency switcher, a conversion rate, and
VAT at 5% on UAE orders and 0% on Kuwaiti ones. The product owner narrowed the launch to
Kuwait.

That removes work, but the question was how much of the two-market model to delete. The
cheap answer is "all of it", and the cheap answer makes a later UAE launch a migration
instead of a configuration change.

## Decision

v1 sells to Kuwait in KWD only. No currency switcher, no FX rate, no VAT calculation, no
country selection at checkout.

What stays, deliberately:

- `Currency` keeps AED at two decimals, with its tests. The code is written and passing;
  deleting it only means writing it again.
- `Order` keeps `Currency`, `FxRate`, `VatRate`, `VatAmount` and `TotalCharged`. In v1
  currency is always KWD, FX is 1 and VAT is 0.
- Seven CHECK constraints enforce exactly that at the database, so the dormant columns
  cannot drift to a wrong value while nobody is looking.

## Consequences

- Roughly a week of build removed, and a permanent reduction in the number of ways money
  can be wrong: there is no conversion step and no tax step to get wrong.
- Three unused columns, which is a trivial price against rewriting the order model.
- A UAE launch must **drop the CHECK constraints deliberately**, in a migration. That is
  the feature: it forces the FX work to be an act of intent rather than a column quietly
  changing value one day.
- Anyone reading the schema sees fields that look unused. This ADR is why.

## Alternatives considered

**Delete the multi-currency model entirely.** Cleanest-looking, and it makes the UAE a
schema migration on a table full of live orders. Rejected.

**Keep the switcher and hide it.** Hidden features are untested features, and a stored
`cur` preference from an earlier visit would have resurrected AED on the storefront. The
storefront audit now fails the build if a second currency appears anywhere in `wwwroot`.
