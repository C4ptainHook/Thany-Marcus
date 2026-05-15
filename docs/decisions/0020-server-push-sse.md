# ADR-0020: Server-push transport — Server-Sent Events

Status: Accepted
Date: 2026-05-15

## Context

Three flows in Portal.Api push data from server to browser without a client-initiated request per update:

1. **Provisioning wizard live progress** (PORTAL-011) — Terraform stdout + saga state transitions stream to the user as provisioning runs (60–120s).
2. **Cloud-list dashboard auto-refresh** (PORTAL-012) — clouds with status `in_progress` update without manual reload.
3. **Destroy operation progress** (PORTAL-015) — symmetric to provisioning.

A complete survey of every interaction in the portal (auth, TOTP setup, plugin-token issuance, recovery-code generation, account settings) found **no bidirectional flows** beyond standard HTTP request/response. The portal is single-user per cloud (DEC-002), so no multi-user real-time collaboration exists. Terraform runs in non-interactive auto-approve mode, so no mid-stream user input is needed.

## Options considered

- **A. Server-Sent Events (SSE).** One-way server→client over plain HTTP/1.1 or HTTP/2. `EventSource` API in the browser handles auto-reconnect and `Last-Event-ID` semantics automatically. No connection-upgrade handshake. Auth flows through normal cookie semantics.
- **B. WebSockets.** Bidirectional, frame protocol, connection upgrade. You would use ~50% of its surface (only server→client). Cookie auth across WS upgrade has Same-Site edge cases. Pure overhead for one-way flows.
- **C. SignalR.** Bidirectional pub/sub abstraction with transport fallback (WS → SSE → long-polling). ASP.NET Core's preferred real-time framework. Requires shipping `@microsoft/signalr` client (~60–80 KB) and architecturally implies "the server expects the client to call methods on it." Companion of Blazor Server, which was rejected in [[0017-portal-web-stack-sveltekit]].
- **D. Long-polling.** Worst latency, more requests, more code. Loses the `EventSource` auto-reconnect free benefit.
- **E. Polling every 2s.** Simplest of all. For a 60–120s flow the wizard feels laggy and the server bears wasted load. Acceptable as a fallback, not as the primary.

## Decision

**Server-Sent Events for the three flows above.** No WebSockets, no SignalR.

Concrete server shape:

```csharp
public static class ProvisioningEvents
{
    public static async Task Handle(
        Guid id,
        HttpContext ctx,
        PortalDbContext db,
        LiveLogBus liveLog,
        CancellationToken ct)
    {
        ctx.Response.Headers["Content-Type"]    = "text/event-stream";
        ctx.Response.Headers["Cache-Control"]   = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";   // prevent Caddy/proxy buffering

        var cloud = await db.Clouds.FindAsync([id], ct);
        await ctx.WriteSseEvent("status", new { cloud.ProvisioningStatus });

        // keep-alive against gateway/browser idle timeouts (every 15s)
        using var keepAlive = StartKeepAliveLoop(ctx, TimeSpan.FromSeconds(15), ct);

        await foreach (var line in liveLog.Subscribe(id, ct))
            await ctx.WriteSseEvent("log", line);
    }
}
```

Concrete client shape (SvelteKit):

```ts
const events = new EventSource(`/api/provisioning/${id}/events`, {
  withCredentials: true   // same-origin cookies
});
events.addEventListener('status', (e) => state = JSON.parse(e.data));
events.addEventListener('log',    (e) => logTail.push(JSON.parse(e.data)));
events.addEventListener('done',   () => events.close());
```

### Locked-in operational details

1. **Caddy buffering**: send `X-Accel-Buffering: no` header and call `Response.Body.FlushAsync()` after each event. Without this, Caddy may buffer the response and the wizard appears frozen.
2. **Keep-alive**: emit a comment line (`: keep-alive\n\n`) every 15s to prevent intermediaries from dropping the idle connection during long Terraform applies.
3. **No replay-on-reconnect**: because there is no durable event log (see [[0019-background-work-and-saga-durability]]), reconnecting clients receive the current `provisioning_status` from the DB and then attach to the live channel. The `Last-Event-ID` header is informational, not load-bearing.
4. **Multi-subscriber fan-out**: the live log bus uses `ConcurrentDictionary<Guid, List<ChannelWriter<LogLine>>>` keyed by `cloud_id`. Two open wizard tabs on the same cloud both receive events.
5. **HTTP/2**: Caddy serves HTTP/2 by default, so the per-origin concurrent-connection limit (~6 for HTTP/1.1) does not apply.

## Consequences

- **Positive:**
  - One-way semantics match the use case exactly — no unused half of a bidirectional protocol.
  - `EventSource` is browser-native, no client library to ship.
  - Same-origin cookie auth flows without special handling.
  - Caddy treats SSE as plain HTTP — no upgrade handshake, no proxy-config gotchas (with the `X-Accel-Buffering` header set).
  - Works through standard HTTP infrastructure (CDNs, gateways, observability).
- **Negative:**
  - Each subscriber holds an HTTP connection. On HTTP/1.1 this is one of the browser's ~6 per-origin connections; on HTTP/2 (default with Caddy) it is one multiplexed stream. Not a problem for an admin portal but worth knowing.
  - No replay-on-reconnect window. If the wizard tab is closed during a 60s apply and reopened, the first 60s of stdout is unrecoverable. The user sees current status from DB instead. Acceptable trade-off for the chosen durability model.
- **Neutral:**
  - If a future feature does need bidirectional comms (it currently doesn't), it would need a separate WebSocket or SignalR endpoint added then. SSE doesn't paint into a corner.

## Related

- [[0017-portal-web-stack-sveltekit]] — SvelteKit consumes SSE via `EventSource`
- [[0019-background-work-and-saga-durability]] — live log bus, no durable event store for replay
- [[0027-reverse-proxy-caddy]] — Caddy must not buffer SSE responses
