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

Phase 0 complete: solution scaffolded, storefront served verbatim from `wwwroot`, CI
green. Phase 1 (domain, database, catalogue API) is next. See `PLAN.md` §5.
