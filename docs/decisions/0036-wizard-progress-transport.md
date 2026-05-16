# ADR-0036: Wizard progress transport — SSE + REST snapshot

Status: Accepted
Date: 2026-05-16
Supersedes: the planned-but-never-written `[[0035-wizard-progress-transport]]` referenced from ADR-0033 (the number 0035 was claimed by burst-worker tier on 2026-05-16; this is its replacement).

## Context

ADR-0033 locked the saga's mutable status + events_log shape. ADR-0020 chose SSE as the
canonical user-facing server-push transport. The wizard (PORTAL-011) is the first consumer
of both; PORTAL-015's destroy modal and PORTAL-012's dashboard will reuse the same channel.

What was not pinned by ADR-0020 / ADR-0033:
1. The exact SSE event vocabulary the wizard subscribes to.
2. How initial state arrives on page load (vs. SSE which is live-only).
3. Reconnect semantics (replay-from-N vs. re-snapshot).
4. How the server multiplexes saga state changes onto SSE subscribers.
5. Auth posture on the channel.

This ADR pins those.

## Decisions

### 1. Curated event vocabulary, not raw events_log

The SSE channel emits a fixed set of 6 named events. The server-side `SagaEventTranslator`
maps internal `events_log` entries to this vocabulary. The frontend does not see raw events_log.

| event              | data                                                       |
|--------------------|------------------------------------------------------------|
| phase_started      | { phase: PhaseName, at: ISO8601 }                          |
| phase_completed    | { phase: PhaseName, at: ISO8601 }                          |
| phase_failed       | { phase: PhaseName, reason: string, message: string }      |
| cloud_ready        | { cloudId: UUID, hostname: string, ip: string }            |
| cloud_failed       | { terminalStatus: string, reason: string, message: string }|
| cloud_rolled_back  | { reason: string }                                         |

PhaseName ∈ { tf_planning | tf_applying | dns_creating | awaiting_cloud_callback | cloud_registered | awaiting_cert }.

Plus an SSE-comment heartbeat (`: heartbeat\n\n`) every 30s.

Rationale: events_log carries internal cardinality (record ids, stderr, sub-phase entries)
the wizard doesn't need. Coupling the wire to events_log makes every internal schema
tweak a frontend-breaking change. Curated vocabulary is small, stable, debuggable.

### 2. Initial state via REST snapshot, not SSE replay

`GET /api/clouds/{id}` returns the full cloud + current-job state synchronously on page
load and on every reconnect. The SSE channel only carries events from the moment of
subscription onward — no replay-from-N logic, no `Last-Event-ID` handling.

Rationale: the REST snapshot is needed anyway for refresh / bookmark / share cases.
Reusing it for SSE reconnection collapses two protocols into one snapshot path.

### 3. Reconnection: re-snapshot + resubscribe

Native `EventSource` auto-reconnects. The client's handler on every (re)connect:
1. Re-fetches `GET /api/clouds/{id}`.
2. Re-renders state.
3. Listeners attached to the EventSource catch subsequent live events.

Belt-and-suspenders against any event loss during the disconnect window.

### 4. Server-side multiplexing: LISTEN/NOTIFY

The saga worker emits `pg_notify('provisioning_job_changed', <jobId>)` inside the same
transaction as every events_log append (and every status transition). The SSE handler
holds a dedicated `NpgsqlConnection` doing `LISTEN provisioning_job_changed`. On each
notification matching a subscribed jobId, the handler re-queries the job row and asks
`SagaEventTranslator` to compute deltas vs. the last-emitted state, then emits curated events.

Rationale: matches ADR-0019's choice for worker dispatch. Zero polling. Sub-second
fan-out latency. One Postgres primitive across two consumers.

### 5. Auth: cookie + ownership check

The endpoint is `[Authorize]` (cookie scheme) + requires `cloud.UserId == claims.SubUs`.
Returns 403 if the caller doesn't own the cloud, 404 if it doesn't exist.

No bearer-token auth on this channel (EventSource doesn't support custom headers).
Same-origin cookie auth is sufficient because the SPA is served from the same origin
as the API (per `portal_web_stack` memory).

### 6. Terminal-state close

On `cloud_ready` / `cloud_failed` / `cloud_rolled_back`, the server emits the event,
flushes a `: closed\n\n` SSE comment, and closes the response. Client detects the
terminal event type and calls `eventSource.close()` to prevent auto-reconnect.

### 7. Heartbeat

`: heartbeat\n\n` every 30 seconds. SSE comments are not delivered to the EventSource
listener, so this is invisible to the frontend code path; it exists to keep proxies
(Caddy, Cloudflare if added later) from idle-killing the connection.

## Consequences

- Frontend has 6 listener types to wire, not "render whatever the wire says." Adding a
  new phase requires a coordinated change to the translator + the listener set + the
  vocabulary in this ADR. That coordination is the desired property — silent schema
  drift is what motivated the curation.
- LISTEN/NOTIFY scales to ~1000s of concurrent SSE subscribers on a 2 vCPU control
  plane; well past thesis budget. If we ever hit it, the bottleneck is Postgres'
  connection count, not the notification fan-out.
- REST snapshot endpoint duplicates information available via the saga's events_log,
  but in a stable shape. The frontend never reads events_log directly; the snapshot
  is the source of truth for "what state is the cloud in right now."

## Related

- [[0019-background-work-and-saga-durability]] — LISTEN/NOTIFY + safety-net poll
- [[0020-server-push-sse]] — the canonical transport choice
- [[0033-provisioning-saga-and-worker]] — saga + events_log shape
- [[0034-cloud-bootstrap-and-portal-handshake]] — what the saga's `awaiting_cert` polls
- [[0035-burst-worker-llm-tier]] — unrelated; just notes the 0035 number was rebound
- `plans/portal-011-handoff.md` — the consumer.
