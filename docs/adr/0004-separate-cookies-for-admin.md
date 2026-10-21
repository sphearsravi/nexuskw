# 0004 — Customers and administrators never share a session

**Status:** Accepted, 2026-10-21

## Context

One ASP.NET Core Identity store serves three roles: `Customer`, `Staff` and `Admin`. The
default configuration gives all of them one cookie and one scheme.

The blast radius is not the same. A stolen customer session loses one reader's books. A
stolen admin session loses the catalogue, every order, and every customer's name and
email address.

## Decision

Two cookie schemes, configured separately:

| | Customer | Admin |
| --- | --- | --- |
| Cookie | `nexus_customer` | `nexus_admin` |
| `SameSite` | Lax | **Strict** |
| `Path` | `/` | **`/admin`** |
| Lifetime | 30 days, sliding | **8 hours, not sliding** |

Authorization policies name their scheme explicitly, so `AdminOnly` cannot be satisfied by
a customer cookie even if one is present.

`Path=/admin` means the browser never transmits the admin cookie on a storefront request —
so a cross-site issue in a public page cannot carry it.

An unauthenticated `/api` request gets 401 or 403, never a 200 carrying a login page for
the caller to parse as JSON.

## Consequences

- Escalating from a customer session to an administrator requires a second, separate
  credential. The cheap one is not a route to the expensive one.
- An administrator who also shops has two sessions. That is correct, not a defect.
- A short, non-sliding admin session means an unattended panel dies. Eight hours covers a
  working day without covering a weekend.
- Two schemes to keep configured, and a test for each policy that proves it **refuses**,
  not merely that it permits.

## Alternatives considered

**One cookie, role checks only.** The framework default, and it makes every storefront
XSS a potential admin compromise.

**A separate application for the admin.** Stronger still, and it doubles the deployment,
the configuration and the shared code problem for a store this size. Revisit if the admin
grows.

**Same cookie, shorter lifetime for admin.** Not expressible: one cookie has one lifetime.
