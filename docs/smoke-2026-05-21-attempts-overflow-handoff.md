# `attempts smallint` overflow crashes cloud-api (handoff, 2026-05-21)

## Symptom

During smoke #24, cloud-api repeatedly crashed and restarted while an ingest
job was in `extracting_attachments` waiting for the last extraction task to
finish. Each crash left the VlmWorker no chance to claim its task, so the
job appeared stuck at `processing` despite the extraction sidecars being
healthy.

Postgres exception in the crash:
```
Npgsql.PostgresException (0x80004005): 22003: smallint out of range
  at JobOrchestratorWorker.ClaimNextAsync(line 134)
  File: int.c, Routine: i4toi2
```

The host's `BackgroundServiceExceptionBehavior=StopHost` policy treats the
unhandled exception as fatal, so the whole `cloud-api` process exits and
docker restarts it. Restart, repeat, every ~3 minutes.

## Root cause

`ingest_jobs.attempts` (and `extraction_tasks.attempts`) are declared as
`smallint` (16-bit, max 32767). The orchestrator's claim query does
`attempts = attempts + 1` on every claim. The handler architecture means:

- `ExtractingAttachmentsHandler.HandleAsync` releases the job's lease and
  returns `Waiting` whenever any extraction task is still `queued` or
  `processing` (the sequencing fix from earlier in the session).
- The orchestrator immediately re-claims the job — the `WHERE` clause
  matches it again because lease is empty and status is one of the in-flight
  values.
- Each re-claim increments `attempts`. Observed rate: ~150 increments/sec.

In smoke #24 the job spent 32767 / 150 ≈ 3 minutes waiting for ollama, hit
overflow, crashed cloud-api, restarted, re-claimed, overflowed again.
Manual `UPDATE ingest_jobs SET attempts = 0` reset it but only bought
another 3-minute window.

## Suggested fix

Two layers, both worth doing:

### Layer 1: widen the column to `int` (4-byte)

```sql
ALTER TABLE ingest_jobs ALTER COLUMN attempts TYPE integer;
ALTER TABLE extraction_tasks ALTER COLUMN attempts TYPE integer;
ALTER TABLE ingest_jobs ALTER COLUMN consecutive_crashes TYPE integer;
```

EF Core model: change `short` → `int` on the three properties; add migration.
Removes the bomb. 32k → 2.1B; even a tight spin loop now needs hundreds of
hours to detonate, by which time it would be a different bug.

### Layer 2: stop the spin

Even with `int`, hammering claim 150x/sec while waiting is wasted CPU and
DB I/O. The "extraction still pending" path in `ExtractingAttachmentsHandler`
should `RescheduleAsync(delay: 2-5 seconds)` instead of just releasing the
lease. The orchestrator's `scheduled_at <= now()` filter then naturally
backs off the re-claim.

The right delay depends on responsiveness expectations — too long and the
saga feels sluggish, too short and we're back to spinning. ~2 seconds is a
reasonable default; longer phases (vision inference taking 70s+) tolerate
longer waits.

## Reproduce

1. Provision a cloud with the current binary.
2. Submit a compound ingest with both `parakeet` and `ollama` extraction
   tasks queued.
3. Once URL + Parakeet succeed and ollama is `processing`, watch
   `SELECT attempts FROM ingest_jobs WHERE note_id = ...` climb by ~150/sec.
4. Around the 3-minute mark, watch cloud-api logs spit
   `22003: smallint out of range` and restart.

## Workaround (already used in this session)

Periodic SQL reset while the saga is healthy. NOT a long-term answer:

```sql
UPDATE ingest_jobs SET attempts = 0 WHERE note_id = '...';
UPDATE extraction_tasks SET attempts = 0 WHERE ingest_job_id IN (
  SELECT id FROM ingest_jobs WHERE note_id = '...'
);
```

Buys ~3 minutes before the next overflow. Useful only as a "get out of the
crash loop while we file this" maneuver.

## Related but separate

- The two-model split handoff
  (`docs/smoke-2026-05-21-two-model-handoff.md`) is the architectural fix
  for the `failed_route` problem that exposed this overflow bug. The
  overflow surfaced only because routing took forever (and never succeeded),
  which gave the spin time to detonate. Once routing works on first attempt
  with a real text-only LLM, real ingests won't reach 32k attempts naturally
  — but the bomb is still there for any future slow phase.
- Cancel-leak (`docs/smoke-2026-05-21-cancel-leak-handoff.md`) is unrelated;
  it's a destroy-path bug, this is an ingest-path bug.
