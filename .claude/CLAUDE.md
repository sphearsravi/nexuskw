# Nexus — project workflow

You are working on **Nexus**, a bilingual (EN/AR, RTL) digital bookstore for
Kuwait and the UAE. Readers buy finance and business books as PDFs and download
them. There is no physical product.

**Stack:** ASP.NET Core 8 · EF Core · SQL Server · Tap Payments · static
storefront in `wwwroot` · SmarterASP.NET IIS.

Read before working:

- [PLAN.md](../PLAN.md) — architecture, the nine phases, the Tap design, locked decisions
- [docs/PRD.md](../docs/PRD.md) — what the product must do and why
- [README.md](../README.md) — how to run it, and the traps

---

## The five rules that break things

1. **KWD has three decimals.** Prices are `6.500`. The Tap webhook signature
   hashes the amount at the currency's own scale, so `6.5` where Tap hashed
   `6.500` means every signature fails, silently, and no order ever completes.
   Money goes through `Nexus.Domain.Currency`, always.
2. **The webhook is the only authoritative payment signal.** The browser
   redirect displays a result and writes nothing.
3. **`App_Data` is server-owned** — the PDF catalogue and the DataProtection
   key ring. Never web-served, never in a publish, never mirrored over.
4. **Arabic is a first-class edition.** A new user-visible string lands in both
   languages in the same change, or it is not done.
5. **The repository is public.** No secret in a commit, and nothing relies on
   an attacker not knowing a URL or a token format.

Run locally on **port 5091** — DeskPilot holds 5080 on this machine.

---

## The agent team

Ten agents in `.claude/agents/`. Use them without being asked when the trigger
hits.

### The SDLC pipeline, in order

| Phase | Agent | Produces |
| --- | --- | --- |
| 1 Requirements | `analyst` | `docs/sdlc/<feature>/01-requirements.md` |
| 2 Design | `architect` | `docs/sdlc/<feature>/02-design.md` + the contract |
| 3 Build | `backend-dev`, `storefront-dev` | Code, against that contract |
| 4 Verify | `qa-tester` | `docs/sdlc/<feature>/04-test-report.md` |
| 5 Review | `security-reviewer` | `docs/review/<feature>.md` |
| 6 Ship | `devops` | A deployed, rolled-back-able release |
| 7 Record | `tech-writer` | Docs that are still true |

`backend-dev` and `storefront-dev` work in parallel against a disjoint file
split the architect defines. `backend-dev` owns `src/Nexus.Web` outside
`wwwroot`; `storefront-dev` owns `wwwroot`.

### The two specialists — pull them in, do not wait for a phase

**`tap-payments`** — auto-trigger on any change to checkout, the Tap client,
the webhook endpoint, order state or refunds. This is where a bug costs real
money, so it reviews before the change lands, not after.

**`kuwait-domain`** — auto-trigger on anything touching money, currency, VAT,
or Arabic content. Spot-checks against Gulf conventions that code can violate
while looking perfectly correct.

### Skipping phases

A one-line fix does not need seven agents. Use judgement, but:

- Anything touching **money or payments** gets `tap-payments` and
  `security-reviewer`, always.
- Anything touching **downloads or `App_Data`** gets `security-reviewer`,
  always. That is the product being given away.
- Anything **customer-facing** gets checked in Arabic before it is called done.

---

## Working agreement

- `main` is always deployable. Work on `feat/*` and merge by PR.
- Warnings are errors. Fix them rather than suppressing them.
- A pricing, currency or payment change without a test is not finished.
- If tests fail, say so with the output. Never report a pass you did not get.
