# Cancel-destroy leaks DO resources (handoff, 2026-05-21)

## Symptom

When a user cancels an in-flight provision via the UI:
- Portal marks `clouds.destroyed_at` and `clouds.provisioning_status='cancelled'`.
- A destroy `provisioning_jobs` row is created and lands at `status='succeeded'`.
- **DO resources (droplet, firewall, volume, Spaces bucket) remain alive.**

The portal is convinced the cloud is gone; DO is convinced the cloud is fine.
This leaks billable infra and confuses any later "re-provision" since the
cloud_id-scoped names (`thany-<short>-fw`, `thany-cloud-<short>` bucket) are
still taken.

## Repro from this session

- Cloud `019e4748-467b-718a-b920-befdeca0c111`, droplet IP `157.230.21.19`.
- Saga reached `awaiting_cloud_callback` (tf_applying had completed — droplet
  was healthy enough to accept SSH at minute 2).
- User clicked Cancel in UI.
- Portal: `provisioning_status=cancelled`, `destroyed_at=2026-05-20 21:36:21`,
  destroy job `019e4751-1a3a-77c2-83db-11e8b0bd56e2` `status=succeeded`,
  `attempt_count=1`.
- ~70s later, `ssh thanyadmin@157.230.21.19 uptime` still returns. Droplet
  alive, firewall + volume + bucket still in DO.

## Root cause hypothesis

`src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/RollingBackTfHandler.cs:79-102`
treats `terraform workspace select <cloud_id>` failure as "no workspace exists,
nothing to destroy":

```csharp
var wsResult = await tf.SelectWorkspaceAsync(workdir, cloud.Id.ToString(), ct);
if (wsResult.Success)
{
    // tf destroy
}
else
{
    EventsLogAppender.Append(job, clock, Phase, new JsonObject
    {
        ["event"] = "no_workspace_to_destroy",
    });
}
// proceeds to mark destroy succeeded regardless
```

The "no workspace" optimistic-skip is correct for the case where TfPlanning
failed before any pg state was written — but it's wrong whenever the workspace
select fails for any *other* reason (transient pg error, name mismatch,
backend init quirk, etc.) while DO resources do exist.

For the smoke #20 case specifically, the apply had completed (droplet was
serving SSH), so the pg state MUST have contained the droplet. Either:
- (a) `terraform workspace select` returned non-zero despite the workspace
  existing in pg — needs `tf_stderr` from the destroy job's events log to
  confirm; or
- (b) the new destroy job's workdir + backend init wired the wrong pg state
  table / wrong `conn_str` / wrong creds, and select genuinely couldn't find
  the workspace.

## Reproduce locally

```sql
-- get destroy job's events log to see workspace select stderr
SELECT events_log
FROM provisioning_jobs
WHERE id = '019e4751-1a3a-77c2-83db-11e8b0bd56e2';

-- list pg-backend workspaces directly
SELECT name FROM terraform_remote_state.states ORDER BY name;
```

Then check whether `019e4748-467b-718a-b920-befdeca0c111` appears in the
workspaces list, and whether the destroy job's tf_stderr mentions "workspace
... does not exist" or something more interesting (auth, schema, lock).

## Suggested fix

Two-layer defense:

1. **Don't mark user-destroy succeeded on `no_workspace_to_destroy`.** A
   user-initiated destroy of a cloud that previously reached `tf_applying` or
   later must actually run `tf destroy`. If the workspace can't be selected,
   that's a real error, not a no-op. Transition to `FailedDestroy` and let
   retries (or operator) handle it.

   - Gate the no-op optimization on `cloud.VmIp IS NULL`
     (or on the saga state at rollback time being one that pre-dates apply).
     If `VmIp` is set, resources exist on DO and tf MUST destroy.

2. **Tighten `SelectWorkspaceAsync` failure handling.** Distinguish "workspace
   doesn't exist" (specific stderr fingerprint) from "other tf/backend error".
   Only the first should fall through to the no-op path; the rest should
   transition to `FailedDestroy`.

## Manual cleanup needed now

Resources from cloud `019e4748-467b-718a-b920-befdeca0c111` still live in DO:
- Droplet: `thany-019e4748` (IP `157.230.21.19`)
- Firewall: `thany-019e4748-fw`
- Volume: `thany-019e4748-data`
- Spaces bucket: `thany-cloud-019e4748`

Either destroy via DO dashboard or run `terraform destroy` against the cloud's
workspace from the saga-worker container.

## Related but distinct

The earlier `failed_callback`-terminal clouds in this session
(`019e4726-...`, `019e46fb-...`) DID get their DO resources destroyed (the
timeout-triggered rollback path is different from the user-Cancel path — it
goes through the destroy job kicked by the saga itself, which appears to be
working correctly). The leak is specific to the user-Cancel path.
