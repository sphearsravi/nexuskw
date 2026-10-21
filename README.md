# Nexus

Bilingual (EN/AR) digital bookstore for Kuwait and the UAE. Customers buy PDF books and
download them; there is no physical fulfilment. ASP.NET Core 8, EF Core, SQL Server,
Tap Payments, deployed to SmarterASP.NET IIS.

The full build plan — architecture, phases, Tap integration design, CI/CD and risks —
is in [`PLAN.md`](PLAN.md). Read that first.

## Layout

| Path | What |
|---|---|
| `src/Nexus.Domain` | Entities, enums, value objects |
| `src/Nexus.Application` | Services, DTOs, interfaces, validators |
| `src/Nexus.Infrastructure` | EF Core, Tap client, email, PDF stamping, storage |
| `src/Nexus.Web` | API controllers, Razor admin, and the storefront in `wwwroot` |
| `tests/` | Unit, integration and (later) Playwright E2E |
| `db/` | Generated idempotent migration SQL per release |
| `deploy/` | `app_offline.htm` used by the deploy workflow |

## Running locally

```bash
dotnet run --project src/Nexus.Web --no-launch-profile
```

Then open <http://localhost:5091>. Port 5091 rather than 5080 — DeskPilot already uses 5080
on the dev machine.

```bash
dotnet build Nexus.sln -c Release   # warnings are errors
dotnet test  Nexus.sln -c Release
```

The .NET SDK is pinned in `global.json` to 8.0.x so local and CI agree.

## Things that will break if you forget them

- **`App_Data/` is server-owned.** It holds the PDF catalogue and the DataProtection key
  ring. It is git-ignored, stripped from CI artefacts and excluded from the deploy. A
  deployment that mirrors over it deletes the catalogue.
- **DataProtection keys are persisted to `App_Data/dpkeys`.** Without this every deploy
  invalidates auth cookies and antiforgery tokens, and everyone is logged out.
- **KWD is a 3-decimal currency.** Prices, order totals and — critically — the Tap
  webhook hashstring must all be formatted at the currency's own scale. AED is 2.
- **Never trust the payment redirect.** The Tap webhook is the only authoritative signal
  that an order was paid. See `PLAN.md` §6.
- **Secrets live on the server**, as App Pool environment variables, and in GitHub
  Actions secrets. Never in `appsettings.json`, never in a commit.

## Status

As of 2026-10-21. **505 tests green**, CI green on all four jobs.

| Phase | State |
| --- | --- |
| 0 Foundations | Done |
| 1 Catalogue | Done — books and categories come from the database, `data.js` deleted |
| 2 Cart, pricing, orders | Done — server-side totals, idempotent creation |
| 3 Payments (Tap) | **Held.** Signature implemented and tested; needs one captured sandbox delivery to verify against. See below |
| 4 Delivery | Done — expiring, count-limited, watermarked downloads |
| 5 Email | Done — outbox and dispatcher. Transport is `None` until a provider is chosen |
| 6 Customer accounts | In progress |
| 7 Control panel | In progress |
| 8 Hardening | Partly: security headers, rate limits, key-ring persistence, schema constraints |
| 9 Go-live | Not started — needs live keys, rights-cleared PDFs, hosting and a domain |

### What is blocking, and on whom

**Stage 3 needs one artefact from you:** a single captured delivery from a Tap sandbox
account — the raw body, the `hashstring` header, and that account's test key. Tap's
published sample cannot be used, because it pairs a real hash from one merchant's sandbox
with a placeholder key from a generic code snippet; the two do not belong together, and
HMAC over the sample reproduces nothing. Paste the three values into
`tests/Nexus.UnitTests/Payments/TapSignatureSampleTests.cs` and delete the `Skip`.

Until then the only test that could prove our implementation agrees with Tap's is skipped,
and the rest assert our code against our own expectation — worth having for the shape of
the string, worth nothing as proof the gateway will accept it.

Also outstanding, none of which block the build: an SMTP provider and sending domain, a
dedicated app pool decision, a custom domain, and written rights per title before any
book is published.

Operational procedures are in [`docs/runbook.md`](docs/runbook.md).
