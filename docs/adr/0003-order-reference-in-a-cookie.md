# 0003 — The order reference travels in a cookie, not a URL

**Status:** Accepted, 2026-10-21

## Context

The demo's confirmation page took the order reference and the buyer's email in its query
string: `success.html?o=KW-10429&e=someone@example.com`.

The security review of the delivery design found the consequence. That page loads a font
cross-origin, and a query string is sent to third parties in the `Referer` header. So the
order reference, and the buyer's email address, were handed to an unrelated host on every
visit. They also sat in browser history, in any link the buyer shared, and in any proxy
log on the way.

The design's own fix was a `<meta name="referrer">` tag on that page. That works, and the
next page somebody adds will not have it.

## Decision

The order reference is never in a URL. At checkout the server sets `nexus_order`: an
HttpOnly, `SameSite=Lax` cookie whose value is the order number sealed with
DataProtection. The confirmation page calls `GET /api/checkout/result`, which reads the
cookie and nothing else.

`success.html` now has no query string at all.

`Referrer-Policy: no-referrer` is also set as a response header for the whole site — as
defence in depth, not as the fix.

## Consequences

- There is nothing in the address bar to leak, to share by accident, or to guess.
- A tampered or stale cookie returns 404, not 500 and not somebody else's order.
- `SameSite=Lax` rather than `Strict`, because the buyer will return from the payment
  gateway on another origin and Strict would drop the cookie on that navigation. Lax is
  the correct trade here, and it is a deliberate one.
- The confirmation page is no longer shareable or bookmarkable. That is the intent.
- A buyer who clears cookies loses the confirmation view. Their order, email and library
  are unaffected.

## Alternatives considered

**A per-page meta referrer tag.** The design's proposal. Correct for that page and
forgotten on the next one.

**A signed token in the query string.** Still a credential in a URL: still in history,
still shared, still in logs. It narrows the window rather than closing the hole.

**Nothing — accept the leak.** The leaked values are an order reference and an email
address, which is not catastrophic. But it is free to fix properly, and "not catastrophic"
is a poor standard for something a customer did not consent to.
