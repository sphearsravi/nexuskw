# Architecture decisions

One file per decision that would be expensive to reverse, or that a reasonable person
would otherwise undo because the reasoning is not visible in the code.

Not every choice needs a record. These do, because each one has a cheaper-looking
alternative that someone will eventually propose.

| # | Decision | Status |
| --- | --- | --- |
| [0001](0001-kuwait-only-for-v1.md) | Kuwait only, KWD only, for v1 | Accepted |
| [0002](0002-refuse-a-stale-cart.md) | Refuse a stale cart rather than silently reprice it | Accepted |
| [0003](0003-order-reference-in-a-cookie.md) | The order reference travels in a cookie, not a URL | Accepted |
| [0004](0004-separate-cookies-for-admin.md) | Customers and administrators never share a session | Accepted |
| [0005](0005-filter-the-webhook-index.md) | The webhook idempotency index is filtered on signature validity | Accepted |
| [0006](0006-no-paging-while-small.md) | No catalogue paging while the catalogue is small | Accepted |
| [0007](0007-publish-gate-split-in-two.md) | Record completeness and sellability are separate gates | Accepted |

## Writing one

Keep it to four headings: **Context**, **Decision**, **Consequences**, and **Alternatives
considered**. State the decision in the present tense, as a rule the code follows. Record
what it costs as well as what it buys — an ADR that lists only benefits is marketing, and
the next person will not trust the rest of it.
