# 0006 — No catalogue paging while the catalogue is small

**Status:** Accepted, 2026-10-21

## Context

The phase 1 requirements assumed the catalogue would grow and specified paging, written
against 500 books. Nobody had been asked how large it would actually get.

The answer was about **fifty titles a year** — roughly 150 after three years.

## Decision

No paging, no cursor, no infinite scroll. The whole published catalogue is one response,
cached for 60 seconds by the browser, and the storefront filters and sorts it in memory.

The endpoint still accepts `cat`, `q`, `sort` and a price range, so the server remains the
single place where bilingual search and Arabic folding are implemented and tested.

## Consequences

- The demo's **instant** filtering survives. No round trip per keystroke, which matters
  more at this size than any saving paging would offer.
- The constraint on phase 1 was that the markup must not change, and this honours it.
- One obvious failure mode if the premise is wrong: at ten thousand books this is a slow
  page. Hence the trigger below.
- Nothing about correctness relaxes. Bilingual search, Arabic orthographic tolerance,
  total sort orders and shareable filter state are all still required and still tested.

## Revisit when either is true

- **300 published books**, or
- the catalogue response exceeds **150 KB compressed**.

Written down because a decision whose reasoning is recorded can be reversed cheaply, and
one that is merely absent gets rediscovered the hard way.
