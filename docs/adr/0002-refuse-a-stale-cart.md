# 0002 — Refuse a stale cart rather than silently reprice it

**Status:** Accepted, 2026-10-21

## Context

The checkout request may carry `clientTotalFils`: what the buyer was shown. The server
always prices the basket from the catalogue, so the client's figure never decides what is
charged. The question was what to do when the two disagree.

Two documents disagreed, and genuinely — not as a matter of emphasis:

- The reconciled implementation plan (§5.1, C-fils) said return **409** and let the
  storefront re-render.
- The `IOrderService` contract's own doc comment said **proceed at the server's price and
  log the discrepancy**.

Stage A implemented the contract, flagged the conflict rather than picking silently, and
stopped. The checkout endpoint could not be written until it was settled.

A mismatch means the catalogue price moved while the cart sat open, or the request was
tampered with. Both look identical from the server.

## Decision

**Refuse the order with `price_changed`, returned as HTTP 409.**

The storefront re-renders the cart at the new price and the buyer decides. The field stays
optional: a request that omits it makes no claim about the price and is priced from the
catalogue as before.

409 rather than 400, because nothing the buyer sent was wrong — the world moved underneath
them.

## Consequences

- A buyer is never charged more than the amount they saw and agreed to. That is the point.
- One extra round trip in a rare case, and a state the storefront must render. Both cheap.
- A tampered total is refused rather than silently corrected, which also removes a quiet
  probing channel.
- A refused checkout must leave no half-made order behind, so the check runs before
  anything is written. There is a test for exactly that.

## Alternatives considered

**Proceed at the server price and log it.** Simplest, and wrong. Charging someone more
than the figure they agreed to is not a logging matter; it is the kind of thing that is
noticed in a chargeback rather than in a log file. The log is read by us, and the person
harmed is the buyer.

**Trust the client's total.** Never considered seriously. It is the oldest hole in
e-commerce.

**Refuse only when the client's figure is lower.** Tempting, since that is the direction
that costs money. Rejected: a buyer who would be charged *less* than they expected still
saw a price that was wrong, and asymmetric rules are the ones people forget when they
change the code.
