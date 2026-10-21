# Nexus runbook

What to do when something is wrong in production. Written to be usable at speed, by
whoever is holding the problem — not every section assumes an engineer.

**Rule zero: find out whether customers are affected before you fix anything.** A broken
admin screen can wait until morning. A customer who has paid and has no book cannot.

---

## Quick reference

| Thing | Where |
| --- | --- |
| Health | `https://<host>/health` — JSON, one entry per check |
| Local run | `dotnet run --project src/Nexus.Web --no-launch-profile` → port **5091** |
| Logs | `App_Data/logs/` on the server, and the IIS stdout log |
| Database | SmarterASP SQL, connection string in the App Pool environment, never in a file |
| PDFs | `App_Data/library/` — **never** in the deploy, **never** in a backup you delete |
| Key ring | `App_Data/dpkeys/` — see "everyone got logged out" |

`/health` returning **Degraded** with `outbox` degraded is **expected** until an SMTP
provider is configured. It is not a fault. It still returns HTTP 200 and will not fail a
deploy.

---

## The webhook stopped arriving

Symptom: orders sit at `Pending`, customers say they paid, nothing is delivered.

This is the worst failure the system has, because **nothing looks broken**. The storefront
is up, the catalogue works, and money is being taken.

1. **Is it all of them, or some?**
   ```sql
   SELECT Status, COUNT(*) FROM Orders
   WHERE CreatedAt > DATEADD(hour, -6, SYSDATETIMEOFFSET()) GROUP BY Status;
   ```
   All pending → the gateway cannot reach us, or every signature is failing.
   Some pending → ordinary stragglers; reconciliation will collect them.

2. **Are deliveries arriving at all?**
   ```sql
   SELECT TOP 20 ReceivedAt, TapObjectId, HashValid, Outcome, RejectionReason
   FROM WebhookEvents ORDER BY ReceivedAt DESC;
   ```
   - **No rows at all** → the gateway is not reaching us. Check the webhook URL in the Tap
     dashboard, that it is HTTPS, and that the host is reachable from outside.
   - **Rows with `HashValid = 0`** → signatures are failing. Go to the next step.
   - **Rows with `HashValid = 1` and a rejection** → read `RejectionReason`; it names the
     mismatch.

3. **If signatures are failing, suspect the amount first.**
   KWD is a **three-decimal** currency. If the amount is being formatted as `6.50` where
   the gateway hashed `6.500`, *every* signature fails and nothing completes. Compare a
   rejected event's `RawBody` amount against what `TapSignature.BuildToBeHashed` produces
   for that order. This is the single most likely cause and has its own unit tests for
   exactly that reason.

   The other candidate is the **secret key**: a key rotated in the dashboard but not in
   the App Pool environment fails every signature in precisely the same way.

4. **Recovering the stuck orders.** Do not mark them paid by hand. The reconciliation job
   re-reads each pending charge from the gateway and settles it. Confirm it is running —
   `JobLeases` should show a recent `payment-reconciliation` row — and let it work.

**Never** disable signature verification to "get orders flowing". An unverified webhook
endpoint on a public repository is an invitation to mark any order paid.

---

## A customer did not get their download link

Work in this order; most cases stop at step 2.

1. **Did the order actually complete?**
   ```sql
   SELECT OrderNumber, Status, PaidAt, BuyerEmailSnapshot FROM Orders
   WHERE BuyerEmailSnapshot = '<their email>' ORDER BY CreatedAt DESC;
   ```
   Still `Pending` → this is the webhook problem above, not an email problem.

2. **Was the email queued, and what happened to it?**
   ```sql
   SELECT Kind, Status, AttemptCount, LastError, CreatedAt, SentAt
   FROM OutboxEmails WHERE ToEmail = '<their email>' ORDER BY CreatedAt DESC;
   ```
   - `Suppressed` → no transport is configured, or the address is on the suppression list.
     Check `EmailSuppressions`.
   - `Failed` with attempts → read `LastError`. A hard bounce means the address is wrong;
     ask them.
   - **No row at all** → the order never reached `Paid`. Back to step 1.

3. **Just fix it for them.** The admin's Orders screen has **re-send the download email**.
   That is the intended remedy and it is rate-limited, not forbidden.

4. **If their link expired**, re-issue rather than extending: Downloads → re-issue. A
   signed-in customer can do this themselves from their library, which is why that feature
   exists.

---

## Refunding

Refunds are **Admin-only** and go through the control panel, never by editing the
database. The panel calls the gateway, and the gateway's own notification is what moves
the order.

**A refund revokes the download grants on that order.** That is deliberate. If you want
to refund and let them keep the book, say so explicitly rather than working around it.

Partial refunds are driven by the running total on the order, not by one refund's size,
so two partials that sum to the full amount leave the order `Refunded`.

---

## Everyone got logged out after a deploy

The DataProtection key ring was not preserved. Keys live in `App_Data/dpkeys/`, and the
deploy must not touch `App_Data`.

Check `deploy-staging.yml` still excludes `**/App_Data/**`. If a deploy wiped it,
everyone's session and every antiforgery token is invalid; there is no recovery beyond
people signing in again. Make sure it cannot happen twice.

This has bitten a sibling project. It is the reason the exclusion exists.

---

## The catalogue is empty / books vanished

**Check `App_Data/library/` still has files before anything else.** If a deploy mirrored
over `App_Data`, the PDFs are gone and must be restored from backup — the database rows
will still be there, pointing at files that no longer exist.

If the files are fine but nothing shows in the shop, the books are probably unpublished.
In production the seeded demo titles arrive **unpublished on purpose** (rights are
confirmed per title before publishing), so an empty catalogue on a fresh deployment is
correct, not a fault.

---

## Rolling back a deploy

1. Put the site into maintenance: upload `deploy/app_offline.htm` to the site root. IIS
   stops the app and releases the locked DLLs.
2. Re-deploy the previous successful build artefact from the GitHub Actions run.
3. Delete `app_offline.htm`.
4. Check `/health`.

**Migrations are purely additive and are applied before the new binaries serve traffic**,
so a code rollback does not need a schema rollback. If you ever write a destructive
migration, this stops being true — do not.

---

## The site is down

1. `/health` — no response at all means the app is not running; a JSON body with
   `Unhealthy` tells you which dependency is at fault.
2. Enable the IIS stdout log and read it. The failure is almost always configuration: a
   missing connection string or a missing key, not code.
3. The app pool may be shared, 32-bit and 512 MB. A memory spike in a *different* site in
   that pool can take Nexus down with it. If this recurs, that is the argument for a
   dedicated pool rather than a code change.

---

## Things that look like bugs and are not

| Looks wrong | Actually |
| --- | --- |
| `/health` says Degraded | The outbox has no SMTP transport. Expected until one is configured |
| Fresh production catalogue is empty | Seeded titles are unpublished until rights are cleared |
| `paymentUrl` is `null` at checkout | Payments are held pending a gateway sandbox sample |
| A download link 404s after three uses | Working as designed. Re-issue it |
| Prices show `6.500` not `6.50` | KWD has three decimals. `6.50` would be the bug |
| Arabic shows `6.500`, not `٦٫٥٠٠` | Deliberate: digits stay Western in both languages |

---

## Escalation

Record anything that reached production in this file afterwards — symptom first, because
that is what the next person will search for. A deployment failure that is not written
down is one you will have again.
