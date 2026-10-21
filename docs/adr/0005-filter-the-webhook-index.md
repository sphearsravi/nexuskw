# 0005 — The webhook idempotency index is filtered on signature validity

**Status:** Accepted, 2026-10-20

## Context

Tap retries webhook deliveries, so the same notification arrives more than once and the
system must process it exactly once. The plan specified a unique index on
`WebhookEvents (TapObjectId, Status)` as the idempotency key: a replay collides, is
recorded, and is ignored.

Every delivery is recorded, verified or not — a rejected one is evidence, and throwing it
away would make a signature problem invisible.

The adversarial review of the payments design found the flaw. **The repository is public**,
so the webhook URL and the body shape are known to anyone. With an unfiltered unique index:

1. An attacker POSTs a forged body carrying a real charge id and `CAPTURED`, with a
   garbage signature.
2. It fails verification and is recorded as rejected — correctly.
3. That row now occupies `(chg_realid, CAPTURED)`.
4. The genuine notification arrives, collides with the forged row, and is **swallowed as
   a duplicate**.

The result is a customer who has paid and has no book, caused by an unauthenticated
request, with nothing in the system looking broken.

## Decision

Filter the uniqueness constraint on signature validity:

```sql
CREATE UNIQUE INDEX UX_WebhookEvent_Object_Status
  ON WebhookEvent (TapObjectId, Status) WHERE HashValid = 1;
```

Expressed in the model as `.HasFilter("[HashValid] = 1")` so EF emits it into the
idempotent migration script and a future scaffold does not quietly drop it.

Rejected deliveries are still recorded in full. They simply cannot pre-empt a real one.

## Consequences

- Idempotency now applies only to deliveries we have actually authenticated, which is the
  only set where "the same notification twice" is a meaningful statement.
- An attacker can still write rejected rows, so that table needs a retention policy and is
  worth alerting on. A flood of rejections is itself a signal.
- The uniqueness guarantee is weaker on paper and stronger in practice.

## Alternatives considered

**Record rejected deliveries in a separate table.** Equivalent in effect, and splits one
timeline across two places, which makes diagnosing a signature problem harder than it
needs to be.

**Do not record rejected deliveries at all.** Removes the attack and removes the evidence.
A silent 100% rejection rate is exactly the failure this system is most exposed to, and
the rejected rows are how anyone would spot it.

**Key idempotency on a hash of the raw body.** Does not help: an attacker varies one byte
and gets a different key, so it stops deduplicating genuine retries while still allowing
the forged row.
