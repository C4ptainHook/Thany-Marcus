# PORTAL-010b Cloudflare DNS client + saga integration — Handoff Brief

**Goal:** finish the Cloudflare DNS half of the provisioning saga so each per-cloud subdomain on `thany.click` is created on apply and deleted on rollback/destroy. The HTTP client (`Portal.SagaWorker/Infrastructure/Cloudflare/CloudflareDnsClient.cs`), stub variant, saga handlers (`DnsCreatingHandler` + `RollingBackDnsHandler`), DI wiring, Polly retry policy, hostname generator, docker-compose env-var pass-through, and the `DnsCreatingHandlerTests` are **already landed**. This ticket closes the three residual gaps that prevent calling it done: (1) the 81057 "record already exists" path returns the existing record-id but does **not** update its IP, leaving stale records pointing at destroyed droplets, (2) the `AwaitingCallbackTimeout` literal is defined independently in two handlers and they disagree (15 min vs 5 min — PORTAL-016 fixes the second; this ticket centralizes the constant so the divergence cannot reappear), (3) ADR-0034 §5 documents the DNS ordering inversely to the actual implementation. After this ticket, the smoke walks `tf_applying → dns_creating → awaiting_cloud_callback` cleanly even on a re-provision against a domain that has a stale record from a prior failed run.

Estimated **0.25 person-day**. This is a hardening + doc-fix pass. The implementation is otherwise complete and the PORTAL-011 smoke already exercises real Cloudflare against the real `thany.click` zone (see `plans/portal-011-smoke-runbook.md` step 9).

## Where decisions live (read before doing anything)

- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — defines the state machine. `dns_creating` comes after `tf_applying`; this ticket does **not** change the ordering, despite what ADR-0034 §5 currently says (see scope item 3 below).
- **`docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`** — §5 ("What cloud-init does NOT do") asserts: "DNS — PORTAL-010b runs the Cloudflare A-record create *before* terraform apply." That line is wrong — the saga creates the record from `dns_creating` which is *after* `tf_applying`. The implementation is correct (it reads the actual droplet IP from `job.TfOutputs`); the ADR is the part that needs to move.
- **`plans/portal-007-handoff.md`** — defines `SagaStatus`, `SagaTransitions`, `EventsLogAppender`. The `record_id` is appended to `events_log` so `RollingBackDnsHandler` can read it back later.
- **`plans/portal-008-handoff.md`** — the DO terraform module emits `ip` in `tf_outputs`; `DnsCreatingHandler.ReadIpFromOutputs` parses it. No module changes here.
- **`plans/portal-015-handoff.md`** — the destroy path's `Kind`-aware `RollingBackDnsHandler` branch already covers user-initiated DNS deletion. No changes here.
- **`plans/portal-016-handoff.md`** — companion smoke-unblock. PORTAL-016 bumps `AwaitingCloudCallbackHandler.CallbackTimeout` from 5 → 15 min; this ticket centralizes the constant so the two handlers can't disagree again.
- **`plans/tickets-2026-05-13.md`** line 91 — the original spec:
  > PORTAL-010b | Cloudflare DNS client: create / update / delete A record on `thany.click` for per-cloud subdomain | 0.5 | PORTAL-008, PORTAL-009 | HttpClient hitting Cloudflare API v4 with bearer token; **pre-creates record before Terraform, updates with actual IP after**

  The "pre-create before Terraform" idea is **abandoned**: the saga creates the record *after* `tf_applying` because the actual droplet IP is only known then, and Caddy's LE retries (60 s × 30 min per DEC-003) cover the DNS-propagation window without needing a pre-create step. This ticket fixes ADR-0034 §5 to match.
- **Memory files**: `portal_architecture.md` (Postgres job queue, mutable status), `portal_tooling.md` (.NET 10, warnings-as-errors, Shouldly + xUnit v3).

**Do not invent new saga statuses or change ordering.** The implementation's ordering (`tf_applying → dns_creating → awaiting_cloud_callback`) is correct; the ADR is the artifact that drifts.

## Scope boundary (precise)

**In scope:**

1. **81057 "record already exists" — update IP on reuse.** `Portal.SagaWorker/Infrastructure/Cloudflare/CloudflareDnsClient.cs:41-48` currently looks up the existing record by name on 81057, then returns `new DnsRecord(existing.Id, subdomain, ip)` — the *caller's* requested IP, NOT the IP actually live in Cloudflare. If the existing record points at a now-destroyed droplet, the returned `record_id` is fine for the events_log, but **DNS still resolves to the wrong IP** and LE will fail until the record is fixed manually. The fix: on 81057, after `FindByNameAsync` returns the existing record, issue a `PATCH zones/{zone}/dns_records/{id}` with `{"content": ip.ToString()}` to update the A record to the new droplet's IP. Cloudflare's PATCH is a partial update and only the `content` field need be sent. On PATCH failure, throw `CloudflareApiException` so the saga rolls back rather than continuing with a stale record.

   Concretely:
   - Add `private async Task<DnsRecord> UpdateIpAsync(HttpClient client, string zone, CloudflareDnsRecordDto existing, string subdomain, IPAddress newIp, CancellationToken ct)` to `CloudflareDnsClient.cs`. It PATCHes `zones/{zone}/dns_records/{existing.Id}` with `{ content = newIp.ToString() }`, parses the envelope, returns `new DnsRecord(existing.Id, subdomain, newIp)`.
   - Replace the `return new DnsRecord(existing.Id, subdomain, ip);` at line 47 with `return await UpdateIpAsync(client, zone, existing, subdomain, ip, ct);`.
   - Add a test: `Reuses_existing_record_and_patches_ip_on_81057` — `FakeCloudflareDnsClient` cannot reach the real 81057 path because it's a stub; the test belongs in a new `CloudflareDnsClientTests` file using `HttpMessageHandler` faking. xUnit v3 + Shouldly per project convention. Verify: first POST returns 81057, follow-up GET returns the existing record, follow-up PATCH succeeds, final returned `DnsRecord.Ip` matches the **caller's** IP (not the existing record's stale IP).

2. **Centralize `AwaitingCallbackTimeout`.** Currently defined in two places with two different values:
   - `Portal.SagaWorker/Features/Provisioning/Handlers/DnsCreatingHandler.cs:20` — `Duration.FromMinutes(15)` (correct per ADR-0034 §4).
   - `Portal.SagaWorker/Features/Provisioning/Handlers/AwaitingCloudCallbackHandler.cs:15` — `Duration.FromMinutes(5)` (stale; PORTAL-016 changes this to 15).

   Replace both literals with a reference to a single source. Add `Portal.SagaWorker/Features/Provisioning/SagaTimeouts.cs`:
   ```csharp
   namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

   public static class SagaTimeouts
   {
       public static readonly NodaTime.Duration AwaitingCloudCallback = NodaTime.Duration.FromMinutes(15);
   }
   ```
   Both handlers reference `SagaTimeouts.AwaitingCloudCallback`. **This ticket touches only `DnsCreatingHandler`**; PORTAL-016 owns the second handler's update. If PORTAL-016 lands first, fine — both handoffs converge on the same constant. If PORTAL-010b lands first, PORTAL-016 inherits the new constant. The two are commutative.

3. **Fix ADR-0034 §5.** Edit `docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md` line 81. Current text:
   > DNS — PORTAL-010b runs the Cloudflare A-record create *before* terraform apply. Cloud-init assumes `${hostname}` resolves to the VM's public IP.

   Replace with:
   > DNS — PORTAL-010b runs the Cloudflare A-record create *after* terraform apply, in the `dns_creating` saga phase, because the droplet IP is only known once terraform returns. Cloud-init's LE retries (60 s × 30 min per DEC-003) absorb the DNS-propagation window; the system does not need a pre-create step. The earlier "pre-create" framing in `tickets-2026-05-13.md` line 91 is abandoned.

   Also update the ADR's "Status" line at the top to flag the §5 revision: `Accepted (event-driven refactor 2026-05-17, CLOUD-001; §5 DNS ordering clarified 2026-05-17, PORTAL-010b)`.

**Out of scope (named explicitly so it doesn't sneak in):**

- ❌ Adding an `UpdateAAsync` to `ICloudflareDnsClient`. The PATCH lives inside `CreateAAsync`'s 81057-recovery branch. Exposing a separate `Update` API would expand the interface for a caller that doesn't exist — the saga never wants to "update" a record independent of creating one. If a future ticket needs explicit updates, add the method then.
- ❌ Pre-creating records before terraform. ADR-0034 §5 *describes* this; this ticket *deletes that description*. The saga ordering stays put.
- ❌ Changes to `RollingBackDnsHandler`. Its delete path works (already integrated with destroy via PORTAL-015). The 81057-reuse fix is on the create path only.
- ❌ Per-user Cloudflare tokens. The `cloudflareToken` parameter on `ICloudflareDnsClient.CreateAAsync(string subdomain, IPAddress ip, string cloudflareToken, ...)` exists but handlers pass `string.Empty`, falling back to the per-instance `CloudflareOptions.ApiToken`. This is **correct** per DEC-003 (platform-owned domain → one token at the portal level, not per-user) and the parameter is vestigial. Leave it as-is — removing it is a public-API change with no caller benefit.
- ❌ TTL / proxied tuning. Current values (TTL=1 = Cloudflare-auto ~5 min; `proxied=false` so LE can reach the droplet directly) are correct.
- ❌ Smoke runbook updates. The smoke already verifies DNS create + delete (lines 279-286, 379-383). No runbook changes.

## File map

```
Thany-Marcus/
├── docs/decisions/
│   └── 0034-cloud-bootstrap-and-portal-handshake.md         # CHANGED: §5 line 81 rewritten; Status line annotated
├── plans/
│   └── portal-010b-handoff.md                               # THIS FILE
├── src/ThanyMarcus.Portal.SagaWorker/
│   ├── Features/Provisioning/
│   │   ├── SagaTimeouts.cs                                  # NEW: centralizes AwaitingCloudCallback (15 min)
│   │   └── Handlers/
│   │       └── DnsCreatingHandler.cs                        # CHANGED: line 20 → SagaTimeouts.AwaitingCloudCallback
│   └── Infrastructure/Cloudflare/
│       └── CloudflareDnsClient.cs                           # CHANGED: 81057 path PATCHes IP via new UpdateIpAsync
└── tests/ThanyMarcus.Portal.Tests/
    └── SagaWorker/Infrastructure/Cloudflare/                # NEW directory
        └── CloudflareDnsClientTests.cs                      # NEW: 81057 reuse+patch path via HttpMessageHandler fake
```

`DnsCreatingHandlerTests.cs` and `FakeCloudflareDnsClient.cs` need **no changes** — the new behavior is inside the real `CloudflareDnsClient`, not the stub or the handler. The handler-level test seeds via the fake, which is intentionally simple.

## Why the 81057 IP-update fix matters in practice

The smoke creates a fresh hostname per provision (`HostnameGenerator` returns `<random-hex>.thany.click`), so collisions in a clean run are vanishingly unlikely (16 hex chars = 2^64 possibilities). The 81057 path is exercised when:

- A previous smoke crashed mid-rollback (the `RollingBackDnsHandler` delete failed, leaving a stale record).
- A previous smoke used the same hostname (e.g. the user manually replayed a job by reusing the cloud row).
- A user destroyed and immediately re-provisioned with the same hostname (unlikely given random hex, but possible).

Without the fix, the new run silently reuses a stale record-id, the events_log claims success, but DNS resolves to a destroyed IP. LE inside the new droplet's Caddy retries 30 times over 30 min, never succeeds, and the saga times out at `failed_cert`. The user sees "cert acquisition failed" with no hint that DNS is the actual cause. With the fix, the record's IP is patched to the new droplet's IP on reuse; LE issues normally; smoke passes.

## Acceptance criteria

1. ✅ `Portal.SagaWorker/Features/Provisioning/SagaTimeouts.cs` exists, contains `AwaitingCloudCallback = 15 min`, and is referenced by `DnsCreatingHandler.cs`. (PORTAL-016 will land the second reference on `AwaitingCloudCallbackHandler.cs`.)
2. ✅ `CloudflareDnsClient.CreateAAsync` PATCHes the existing record's IP on 81057 before returning. Throws `CloudflareApiException` if the PATCH fails.
3. ✅ `tests/ThanyMarcus.Portal.Tests/SagaWorker/Infrastructure/Cloudflare/CloudflareDnsClientTests.cs` covers:
   - Happy create path → 200 from POST, returns new `DnsRecord`.
   - 81057 path → POST returns 81057 error, GET returns existing record, PATCH returns 200, final `DnsRecord` has caller's IP.
   - 81057 + PATCH failure → throws `CloudflareApiException` with a message naming both error codes.
   - Generic non-81057 error → throws `CloudflareApiException` with the Cloudflare error code in the message (regression guard for existing behavior).
4. ✅ ADR-0034 §5 line 81 rewritten per scope item 3; Status line annotated with the §5 revision.
5. ✅ `dotnet test` is green. Existing `DnsCreatingHandlerTests` continues to pass (its fake-driven path is unchanged).
6. ✅ End-to-end: run the smoke from `plans/portal-011-smoke-runbook.md` step 9. The "Verify Cloudflare A record" command (line 282) returns the freshly-created record with `content` matching the droplet's IP. If a `--soft-delete-and-replay` smoke is run (seed a stale record manually via the Cloudflare API, then re-provision the same hostname), the record's IP updates to the new droplet's IP, LE issues normally, the smoke reaches `succeeded`.

## Risks and gotchas

- **PATCH idempotency.** Cloudflare's PATCH is idempotent; replaying with the same payload returns the same 200. If the PATCH partially succeeds (network drops the response after Cloudflare commits), Polly retries with the same body — safe.
- **PATCH auth.** PATCH uses the same `Authorization: Bearer <token>` header. The `ApplyAuth` helper at line 66 covers all client calls; no change needed.
- **HttpMessageHandler test pattern.** Mocking `HttpClient` via a custom `DelegatingHandler` is the .NET-idiomatic way; xUnit v3 + Shouldly works. The Polly retry policy is attached to the named client at DI time — for unit tests you can either bypass it (construct the client without the policy) or accept that a "fail then succeed" message handler exercises the retry. The simpler test sets up a `HttpClient` with a stub handler directly, no Polly, and tests `CloudflareDnsClient` in isolation. The retry policy is implicitly covered by integration smoke.
- **Test isolation.** Each test instantiates its own `HttpClient` + handler; no shared state. Parallel xUnit collection is safe.
- **CloudflareEnvelope deserialization on PATCH.** PATCH returns the same envelope shape as POST (`CloudflareEnvelope<CloudflareDnsRecordDto>`). Reuse the existing record's parsing code.
- **TTL change on PATCH.** Do NOT send `ttl` in the PATCH body — Cloudflare interprets it as a request to change TTL. Send only `content`. Existing TTL (auto = 1) stays.
- **ADR-0034 cross-reference.** PORTAL-016's handoff already names this ADR for the cloud-side bootstrap contract; PORTAL-010b's §5 edit doesn't conflict (different section, different concern). Both handoffs can edit the file; if they collide at merge time, prefer PORTAL-010b's §5 wording.

## What "done" looks like

Run the smoke per `plans/portal-011-smoke-runbook.md`. At step 9, the wizard SSE log shows:

```
... → tf_applying → dns_creating → awaiting_cloud_callback
                          ↓
                     events_log entry:
                     {"phase":"dns_creating","event":"dns_created",
                      "record_id":"<id>","subdomain":"<hex>","ip":"<droplet-ip>"}
```

The Cloudflare dashboard shows the new A record with `content` matching the droplet IP. After rollback or destroy, the record is gone. After PORTAL-016 also lands, the full smoke walks to `succeeded`.
