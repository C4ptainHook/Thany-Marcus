# Smoke #17 bug report (2026-05-20)

Bugs surfaced across smoke tests #8–#17. All open items have now been fixed;
see the list below.

## Fixed this session (for context)
- Extraction sequencing race: `ExtractingAttachmentsHandler.HandleAsync` now
  releases the lease and returns `Waiting` whenever any extraction task for
  this job is still `queued` or `processing`, gating new sidecar queueing
  until current work drains. Prevents Parakeet + Ollama running in parallel
  on 4GB droplets.
- Saga crash-loop cap: added `consecutive_crashes` column on `ingest_jobs`
  (migration `AddIngestJobConsecutiveCrashes`). `ClaimNextAsync` increments it
  only when claim observes `lease_owner IS NOT NULL` (prior holder didn't
  release). `JobStateTransitions.TransitionAsync` / `RescheduleAsync` and
  `ExtractingAttachmentsHandler.ReleaseLeaseAsync` reset it to 0 on clean
  release. `IngestPhaseDispatcher.DispatchAsync` terminates the job to the
  phase's failure terminal when `consecutive_crashes >= MaxConsecutiveCrashes`
  (default 3, overridable via `IngestSaga:Phases:{phase}:MaxConsecutiveCrashes`).
- Parakeet OOM on long audio: `ParakeetHttpClient` now downloads the source
  to a temp file, ffprobes duration, and when it exceeds
  `ParakeetOptions.MaxChunkSeconds` (default 30) ffmpeg-segments to 16kHz
  mono WAV chunks and POSTs each in sequence; transcripts are concatenated
  and the first chunk's `language` wins. Short audio still streams as a
  single multipart POST.
- URL ingest: `Attachment.Url` now populated from `extra.url` at init time;
  init rejects URL attachments missing `extra.url` with 400
  (`IngestEndpoints.cs`)
- Re-issued plugin token now syncs to the cloud
  (`PluginTokenEndpoints.cs`). `POST /api/clouds/{id}/plugin-tokens` posts
  the new hash via `IPortalToCloudPluginTokenClient.PostAsync` (lifted out of
  SagaWorker into `Portal.Api/Features/CloudManagement/PluginTokens/Sync`)
  and revokes previously-active hashes via new `RevokeAsync` against new
  cloud endpoint `POST /admin/plugin-tokens/revoke`. Portal metadata is
  only updated after the cloud-side POST succeeds; revoke failures are
  logged but don't block re-issue (portal-side metadata is still revoked).
- Ollama-puller hardened (`cloud-init.yaml.tpl`): `set -eu -o pipefail`,
  bounded 60s readiness wait, explicit `ollama pull` error path, post-pull
  `ollama list | grep` verification. Runcmd drops `--rm`, tees output to
  `/var/log/thany-cloud/ollama-puller.log`, retains container as
  `thany-cloud-ollama-puller-run`, writes `/opt/thany-cloud/.ollama-puller-ok`
  only on success. Certbot/registration block bails when the sentinel is
  missing, so the portal sees a failed-puller cloud as failed provisioning
  rather than silently registering a half-built one.
- bootcmd timezone race → moved to `runcmd`
- Ollama healthcheck `wget` → `ollama list`
- Parakeet healthcheck (no `/health` endpoint, no `wget`) → removed
- Frontend `/healthz` → `/health/live` + drop workerState dependency
- pg backend workspace isolation → `SelectOrCreateWorkspaceAsync` (create) /
  `SelectWorkspaceAsync` (destroy)
- Destroy doesn't render workdir → `RollingBackTfHandler` calls `RenderAsync`
  when missing
- Destroy endpoint doesn't flip `provisioning_status` to `destroying` → fixed
  in `DestroyCloudEndpoints`
- `FailedDestroy` missing from `DestroyableProvisioningStatuses` → added
- Spaces bucket missing `force_destroy = true` → added
- Failed-create rollback doesn't set `destroyed_at` → mirror cleanup added
- `/api/clouds/me` uses `IgnoreQueryFilters()` → removed
- `ParakeetHttpClient` sends `url` instead of `file` multipart → switched to
  `OpenReadAsync` + `StreamContent` + `file` part name
- Custom ollama image (`thany-ollama-minicpm`) for qwen35 architecture
  support (replaces upstream `ollama/ollama:0.24.0`)
- `OLLAMA_KEEP_ALIVE` `30m` → `30s` (free 1.6GB between phases)
- Parakeet `-workers 4` → `-workers 1`
- Callback timeout `15m` → `30m`
- `TransitionToFailureTerminalAsync` extracted for shared use
