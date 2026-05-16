# ADR-0032: FK cascade rules + soft-delete on clouds

Status: Accepted
Date: 2026-05-15

## Context

PORTAL-002's schema has eight tables (see [[0028-schema-conventions]], [[0030-auth-flow]], [[0031-rate-limiting-and-lockout]]). Most foreign-key relationships have an obvious cascade rule (delete the parent, delete the children). Two FKs do not, because deletion involves real-world side effects or audit-load-bearing history:

- **`clouds.user_id`** — each `clouds` row corresponds to a live VM with provider billing. Cascading on `users` delete would silently orphan VMs that keep running and accruing costs.
- **`provisioning_jobs.cloud_id`** — the saga history (`status`, `last_error`, `attempts`) is the audit trail for a cloud's lifetime. Cascading on `clouds` delete erases that history.

Related: **do `clouds` rows hard-delete on cloud destruction, or soft-delete?** The two questions interact — if `clouds` rows never get hard-deleted, the cascade-vs-restrict choice for `provisioning_jobs.cloud_id` becomes nearly moot.

## Options considered

### Cascade map decision rule

Default: ON DELETE CASCADE for all FKs. Reserve RESTRICT for cases where deletion would either:

1. **Leave irreversible real-world side effects** (orphan a running VM, fail to revoke an issued credential, etc.).
2. **Erase audit-load-bearing history** (provisioning attempts, etc.).

Applied to the two notable FKs:

**`clouds.user_id`:**
- **A. RESTRICT.** DB-enforced. Must destroy clouds first; account-deletion flow goes "enqueue destroy jobs → wait for completion → delete user."
- **B. CASCADE.** App-enforced. App is responsible for orchestration; bugs leak orphan VMs that still bill.

**`provisioning_jobs.cloud_id`:**
- **A. RESTRICT.** Preserves provisioning audit history even after cloud is gone.
- **B. CASCADE.** Destroyed cloud takes its history with it. Simpler.

### Soft-delete on `clouds`

- **A. Soft-delete via `destroyed_at` lifecycle timestamp.** `clouds` rows are never `DELETE`d; the destruction flow sets `destroyed_at`. EF Core `HasQueryFilter(c => c.DestroyedAt == null)` makes "active clouds only" the default; opt-in to history via `IgnoreQueryFilters()`.
- **B. Hard delete + archive table.** On destruction, copy to `destroyed_clouds` and DELETE from `clouds`. Audit lives elsewhere; live queries don't filter.

## Decision

- **`clouds.user_id`: A (RESTRICT).** DB-enforced. Defense in depth — even an app bug can't delete a user with live clouds.
- **`provisioning_jobs.cloud_id`: B (CASCADE).** Combined with soft-delete-via-`destroyed_at` below, this rule rarely fires in normal flow. But if a real hard-delete path is ever added (e.g., GDPR right-to-be-forgotten), cleanup is automatic.
- **Soft-delete `clouds`: A.** Set `destroyed_at` rather than DELETE. EF Core global query filter ignores destroyed rows by default; opt-in for history queries.

### Final cascade map

```
users (1) ─┬─ (0..1) totp_secrets              [CASCADE]
           ├─ (0..N) totp_backup_codes          [CASCADE]
           ├─ (0..N) recovery_codes             [CASCADE]
           ├─ (0..N) encrypted_provider_tokens  [CASCADE]
           ├─ (0..N) auth_lockouts              [CASCADE]
           └─ (0..N) clouds                     [RESTRICT — soft-delete via destroyed_at]
                       │
                       ├─ (0..N) plugin_tokens       [CASCADE]
                       └─ (0..N) provisioning_jobs   [CASCADE — rarely fires due to soft-delete]
```

### Account-deletion flow

```
User clicks "Delete account"
  → app enumerates user's clouds where destroyed_at IS NULL
  → app enqueues destroy jobs for each
  → app shows "Deleting your N clouds... this may take 10-15 minutes"
  → background workers Terraform-destroy each
  → on all-destroyed (destroyed_at set on every cloud), app shows "Confirm final deletion"
  → user confirms
  → app sets clouds.user_id rows still with destroyed_at IS NULL? (should be zero) → reject if any
  → app DELETEs FROM users WHERE id = me
  → DB cascades: totp_secrets, totp_backup_codes, recovery_codes, encrypted_provider_tokens, auth_lockouts all CASCADE
  → clouds remain (since they're soft-deleted and FK is RESTRICT); they become "orphan tombstones"
     -- conscious decision: history of "this user once existed and had these clouds" is preserved
     -- alternative would be to hard-delete clouds rows once destroyed_at is set during account deletion
        (cascade to plugin_tokens and provisioning_jobs); revisit in PORTAL-015 (destroy flow ticket)
```

### EF Core wiring

```csharp
// Features/CloudManagement/CloudConfiguration.cs
public sealed class CloudConfiguration : IEntityTypeConfiguration<Cloud>
{
    public void Configure(EntityTypeBuilder<Cloud> b)
    {
        b.HasOne<User>().WithMany().HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasQueryFilter(c => c.DestroyedAt == null);
        // ...
    }
}

// Features/Provisioning/ProvisioningJobConfiguration.cs
public sealed class ProvisioningJobConfiguration : IEntityTypeConfiguration<ProvisioningJob>
{
    public void Configure(EntityTypeBuilder<ProvisioningJob> b)
    {
        b.HasOne<Cloud>().WithMany().HasForeignKey(j => j.CloudId)
            .OnDelete(DeleteBehavior.Cascade);
        // Note: query filter on Cloud doesn't propagate; provisioning_jobs are not filtered.
        // ...
    }
}
```

## Consequences

- **Positive:**
  - User-delete-without-cloud-destroy fails fast at the DB level. App bug → loud FK violation, not silently-billing orphan VMs.
  - Cloud destruction preserves a tombstone (`destroyed_at IS NOT NULL`) for "this user once had this cloud" history.
  - Provisioning history survives cloud destruction by virtue of soft-delete — `provisioning_jobs` cascade rule rarely fires.
  - GDPR-style hard-delete remains an additive path: change DELETE semantics for `clouds` rows + cascade fires through.
  - `HasQueryFilter` makes "active clouds only" the default — feature code doesn't need to remember `WHERE destroyed_at IS NULL` on every query.
- **Negative:**
  - `clouds` rows never reduce in count in the normal flow. Thesis-scale (~5-10 destroyed clouds max); not a concern.
  - `HasQueryFilter` makes "include history" require explicit `IgnoreQueryFilters()` — easy to forget when explicitly wanting destroyed rows.
  - Account deletion leaves orphan tombstone `clouds` rows. Revisit in PORTAL-015 (destroy flow) to decide if those should be hard-deleted at user-delete time.
- **Neutral:**
  - `plugin_tokens` CASCADE on `clouds` delete is aligned with the tombstone model — rarely fires, correct when it does.
  - `auth_lockouts` CASCADE on `users` delete is the simplest correct rule (no audit value once the user is gone).

## Related

- [[0019-background-work-and-saga-durability]] — `provisioning_jobs` is the saga table whose audit value motivates preservation
- [[0028-schema-conventions]] — `destroyed_at` follows the lifecycle-timestamp convention; no generic `state_changed_at`
- [[0030-auth-flow]] — `users` is the parent of most cascades
- PORTAL-015 (Destroy flow) — will revisit whether account-deletion hard-deletes orphan tombstone clouds
