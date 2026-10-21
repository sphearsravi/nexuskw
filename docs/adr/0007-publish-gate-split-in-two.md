# 0007 — Record completeness and sellability are separate gates

**Status:** Accepted, 2026-10-20

## Context

`Book.PublishBlocker()` enforced what makes a catalogue record complete: Arabic and
English title, author and description, a price above zero, an active category.

Phase 4 added two rules — a book must have a PDF, and that PDF must not be the seeder's
generated stub. The second exists because a placeholder satisfies "a file exists", so
without it the gate reports green on the exact failure it was added to catch.

Adding both to `PublishBlocker` broke phase 1. The twenty seeded demo titles have no PDF,
so nothing could be published, and the development storefront went empty.

## Decision

Two methods, with different jobs:

- **`ContentBlocker()`** — is this record complete? Text in both languages, a sellable
  price, an active category. This is what the seeder checks.
- **`PublishBlocker()`** — may this be offered for sale? `ContentBlocker()` plus a real,
  non-placeholder file. This is what the admin's publish action calls.

## Consequences

- Development and staging seed a browsable catalogue without inventing PDFs.
- No operator can put a fileless or placeholder title on sale, in any environment.
  Production seeds unpublished regardless.
- Two methods where a reader might expect one, so each says in its own words which
  question it answers.
- Content rules are reported before file rules, because "the Arabic description is
  missing" is more useful to an operator than "the PDF is missing" when both are true.

## Alternatives considered

**Make the rules environment-dependent.** Domain logic that behaves differently in
production is the kind of thing that is correct in testing and wrong in the place it
matters.

**Have the seeder write a real stub PDF and publish it.** Defeats the purpose: the gate
would then be green on a placeholder, which is precisely the failure the rule exists to
prevent.
