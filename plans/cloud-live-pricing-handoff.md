# CLOUD-LIVE-PRICING — Stamp real DO pricing onto each cloud at provision time

**Goal:** stop hardcoding DigitalOcean rates in the frontend. At provision time, fetch the actual `monthly_price` + `price_hourly` from DigitalOcean's `/v2/sizes` API (using the user's OAuth token), persist them on the `clouds` row, and have the frontend display the user's true locked-in cost. Estimated **~1 person-day**.

## Why this exists

`src/lib/costs.ts` currently hardcodes a small DigitalOcean rate table:

```ts
export const HOURLY_RATES = {
  digitalocean: { 's-4vcpu-8gb': 0.07143, ... }
};
```

Three failure modes this produces:

1. **Manual drift.** DO changed pricing twice in 2024-2026; our table doesn't auto-update. We saw this firsthand: the table missed `s-4vcpu-8gb` entirely until 2026-05-31.
2. **Hidden SKU change risk.** If the saga's `DefaultSize` changes (e.g. bumping to a Premium AMD variant for better VLM throughput), the cost line goes stale silently until someone notices the math is wrong.
3. **No per-cloud truth.** Today the frontend computes cost from the SKU-name string + a hardcoded rate. If a cloud was provisioned at one rate but DO later changes the rate, the dashboard shows the new rate as if it applied to past usage — wrong.

The right model: **the price the user is charged is locked in at provision time, persisted on the cloud row, and displayed unchanged thereafter.** This ticket lands that.

## Scope

**In scope:**
- New nullable columns on `clouds`: `price_monthly_usd numeric(10,4)`, `price_hourly_usd numeric(10,6)`, `price_currency text` (default `'USD'`), `priced_at timestamptz`, `priced_source text` (e.g. `'do_api_v2_sizes'`).
- `TfApplyingHandler` (or a new tiny `PricingStampHandler` between apply and DNS) fetches `GET https://api.digitalocean.com/v2/sizes` after `tf apply` succeeds, finds the size matching the just-provisioned droplet's SKU, extracts `price_monthly` + `price_hourly`, persists on the cloud row.
- Server-side cache for the `/v2/sizes` response — 24h TTL is fine; DO pricing is glacial. Keyed on nothing (single global cache; pricing is the same for all users).
- `GetCloudStatusEndpoints` includes the new fields in `CloudStatusResponse`.
- Frontend `CostInputs.priceHourly` becomes optional; when present (always for newly-provisioned clouds), it's used directly. When missing (legacy clouds from before this ticket), fall back to the hardcoded table.
- The hardcoded `HOURLY_RATES` table stays as a fallback for the new-cloud *review* screen (pre-provision, before we know the real price) and for legacy clouds.
- Soft-fail: if the DO API call fails during provisioning, log it, leave price columns null, do not fail the provisioning saga. Frontend's fallback handles the null case.
- Backfill script (or admin endpoint) that retroactively fetches pricing for any `succeeded` clouds with null price columns — runs once after deploy.
- Tests for: successful stamp, DO API failure soft-fall path, cache hit, frontend rendering with both stamped and unstamped clouds.

**Out of scope:**
- Hetzner / Azure pricing — DO is the only provider with active OAuth + provisioning. Other providers stay on the hardcoded table.
- Currency conversion. DO returns USD; the column is `USD`. Multi-currency display is a separate concern.
- Historical price tracking. The cloud row holds one price (the at-provision rate). If DO changes the rate mid-cloud-lifetime, the column doesn't update. That's the *desired* contract — show the user what they agreed to.
- Per-region pricing. DO's `/v2/sizes` returns a global price per SKU; we don't model region-multiplier nuances.
- Bandwidth, snapshots, backups, volumes — extras beyond the droplet itself. Volume cost is significant ($0.10/GB-month for `digitalocean_volume`); a follow-up ticket can stamp that too.

## Concrete files

### Database

#### Migration `0NNN_cloud_pricing_columns.sql`

```sql
ALTER TABLE clouds
  ADD COLUMN price_monthly_usd numeric(10,4),
  ADD COLUMN price_hourly_usd  numeric(10,6),
  ADD COLUMN price_currency    text DEFAULT 'USD',
  ADD COLUMN priced_at         timestamptz,
  ADD COLUMN priced_source     text;
```

Nullable on purpose: legacy `clouds` rows have no stamped price. Frontend handles the null.

### Backend

#### `src/ThanyMarcus.Portal.Api/Features/Provisioning/Pricing/DoSizesCatalog.cs` — NEW

Encapsulates fetching + caching DO's `/v2/sizes`:

```csharp
public interface IDoSizesCatalog
{
    Task<DoSize?> GetAsync(string slug, byte[] oauthToken, CancellationToken ct);
}

public sealed record DoSize(
    string Slug,
    decimal MonthlyUsd,
    decimal HourlyUsd,
    int Memory,
    int Vcpus,
    int Disk);

public sealed class DoSizesCatalog : IDoSizesCatalog
{
    private readonly IHttpClientFactory http;
    private readonly IMemoryCache cache;
    private static readonly TimeSpan TTL = TimeSpan.FromHours(24);
    private const string CacheKey = "do:sizes:v1";

    public async Task<DoSize?> GetAsync(string slug, byte[] oauthToken, CancellationToken ct)
    {
        if (cache.TryGetValue<IReadOnlyDictionary<string, DoSize>>(CacheKey, out var sizes))
            return sizes.TryGetValue(slug, out var size) ? size : null;

        var fresh = await FetchAsync(oauthToken, ct);
        if (fresh is null) return null;

        cache.Set(CacheKey, fresh, TTL);
        return fresh.TryGetValue(slug, out var size2) ? size2 : null;
    }

    private async Task<IReadOnlyDictionary<string, DoSize>?> FetchAsync(byte[] oauthToken, CancellationToken ct)
    {
        // GET https://api.digitalocean.com/v2/sizes?per_page=200
        // Authorization: Bearer <token>
        // Response shape: { "sizes": [ { "slug": "...", "price_monthly": 48.0, "price_hourly": 0.07143, ... } ] }
        // Parse into Dictionary<string, DoSize>. Soft-fail to null on HTTP error.
    }
}
```

Register as singleton in `Program.cs`.

#### `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/TfApplyingHandler.cs` — EDIT

After `tfOutputs` is read + `cloud.VmIp` set, before transitioning to `DnsCreating`:

```csharp
cloud.VmIp = TryReadIp(tfOutputs);

// Stamp pricing — soft-fail; null columns are OK on error.
try
{
    var size = await doSizesCatalog.GetAsync(cloud.Size ?? defaultSize, providerToken, ct);
    if (size is not null)
    {
        cloud.PriceMonthlyUsd = size.MonthlyUsd;
        cloud.PriceHourlyUsd  = size.HourlyUsd;
        cloud.PriceCurrency   = "USD";
        cloud.PricedAt        = clock.GetCurrentInstant();
        cloud.PricedSource    = "do_api_v2_sizes";
    }
}
catch (Exception ex)
{
    LogPricingFetchFailed(logger, ex, cloud.Id);
    // Provisioning continues; price columns stay null; frontend falls back.
}
```

`cloud.Size` doesn't exist yet — either add it to the entity (cheap) or pass `defaultSize` from `WorkspaceLayout` through the saga payload.

Inject `IDoSizesCatalog` into the handler (DI registration in `Program.cs`).

#### `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Cloud.cs` — EDIT

Add properties:
```csharp
public decimal? PriceMonthlyUsd { get; set; }
public decimal? PriceHourlyUsd  { get; set; }
public string?  PriceCurrency   { get; set; }
public Instant? PricedAt        { get; set; }
public string?  PricedSource    { get; set; }
```

EF mapping in the configuration file (column names `price_monthly_usd`, etc.).

#### `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Status/GetCloudStatusEndpoints.cs` — EDIT

Extend `CloudStatusResponse`:
```csharp
public record CloudStatusResponse(
    Guid CloudId,
    string Hostname,
    /* ... existing fields ... */,
    decimal? PriceMonthlyUsd,
    decimal? PriceHourlyUsd,
    string?  PriceCurrency,
    DateTimeOffset? PricedAt);
```

### Backfill (optional but recommended)

#### `src/ThanyMarcus.Portal.Api/Features/Admin/PricingBackfillEndpoint.cs` — NEW

Admin-only endpoint that loops over `clouds WHERE provisioning_status = 'succeeded' AND price_monthly_usd IS NULL`, fetches each user's OAuth token, calls `DoSizesCatalog.GetAsync`, persists the prices.

Run once after deploy. Then delete the endpoint, or leave it for future repair.

### Frontend

#### `src/ThanyMarcus.Portal.Web/src/lib/types/cloud.ts` — EDIT

```ts
export interface CloudStatusResponse {
  /* ... existing fields ... */
  priceMonthlyUsd: number | null;
  priceHourlyUsd:  number | null;
  priceCurrency:   string | null;
  pricedAt:        string | null;
}
```

#### `src/ThanyMarcus.Portal.Web/src/lib/costs.ts` — EDIT

`CostInputs` gains a precedence field:

```ts
export interface CostInputs {
  provider: Provider | string;
  sku?: string;
  provisionedAt: string | null;
  priceHourlyUsd?: number | null;   // NEW — when present, used directly; bypasses HOURLY_RATES lookup
}
```

`computeMonthCosts`:
```ts
const rate = inputs.priceHourlyUsd ?? rates[sku] ?? 0;
```

`projectedRates` stays as-is — used only for the pre-provision review screen, where we don't yet have a stamped rate.

#### `src/ThanyMarcus.Portal.Web/src/lib/CostCard.svelte` — EDIT

```svelte
let breakdown = $derived(computeMonthCosts({
  provider: cloud.provider,
  provisionedAt: cloud.succeededAt,
  priceHourlyUsd: cloud.priceHourlyUsd,
}));
```

Meta line additionally mentions when the rate was locked:
```svelte
{breakdown.sku} @ ${cloud.priceHourlyUsd?.toFixed(5) ?? '—'}/h
{#if cloud.pricedAt}
  · locked {new Date(cloud.pricedAt).toLocaleDateString()}
{/if}
```

#### `src/ThanyMarcus.Portal.Web/src/routes/clouds/new/+page.svelte` — no change

The review screen still uses `projectedRates` (table-based estimate). Add a small note: "Final price will be locked in at provision time from DigitalOcean's current rate." This sets the user's expectation that the review-screen number is an estimate.

## Tests

### Backend
- `DoSizesCatalogTests`: first call hits HTTP + caches; second call returns cached; cache miss after TTL; HTTP error returns null and doesn't poison cache; unknown slug returns null.
- `TfApplyingHandlerPricingTests`: successful pricing stamp sets all four columns; DO API failure leaves columns null and saga still transitions to `DnsCreating`.
- `GetCloudStatusEndpointsTests`: response includes pricing fields for stamped clouds; nulls for legacy clouds.

### Frontend
- `costs.test.ts`: when `priceHourlyUsd` is provided, it's used regardless of the table; when null, falls back to `HOURLY_RATES`.
- Manual smoke: provision a new cloud, observe the dashboard's CostCard shows the exact rate DigitalOcean billed (cross-check against the DO account console).

## Migration / deployment notes

- Migration is purely additive (nullable columns). No data loss on rollback.
- DO's `/v2/sizes` is rate-limited (5000 req/h per token); with 24h cache + ~3 user clouds in dev, we burn maybe 1 call per day. No risk.
- The OAuth token is already encrypted at rest (`encrypted_provider_tokens`); the catalog client reuses the same decryption path the saga uses.
- After deploy: run the backfill endpoint once to stamp prices on existing `succeeded` clouds. Verify dashboards now show correct rates.

## Risks / open questions

- **DO returns slightly different prices than the table.** The frontend hardcoded `s-4vcpu-8gb` as `0.07143`; DO's API may report `0.07143` exactly or some rounding variant. Persist the API value as the truth; if it disagrees with the hardcoded fallback, the fallback is what gets corrected.
- **OAuth token at provision is the user's, not the portal admin's.** This is by design — each user's price is fetched with their own token, and DO returns the same global pricing regardless of which token asks. No security implication; just clarity.
- **Empty `/v2/sizes` response.** If DO returns an empty list or a malformed payload, we get null — frontend falls back to the table. Acceptable.
- **Price-change auditability.** This ticket doesn't track price history per cloud. If you want to know "what did this cloud cost on May 15," you can't reconstruct it from `priced_at` + current `price_hourly_usd` alone. Add a `cloud_price_history` table only if billing audit becomes a requirement.
- **Pre-Provision price.** The new-cloud review screen still uses the hardcoded table. There's no way to show the user the *exact* price they'll be charged until after `tf apply`. A future enhancement could fetch live pricing on the review step when the user has DO connected; for v1 the "estimated" qualifier is enough.
- **What about `digitalocean_volume`?** A 50 GB block storage volume is $5/month on top of the droplet. This ticket doesn't stamp volume pricing. Add a follow-up to fetch `/v2/volumes/prices` (if such an endpoint exists) or hardcode the well-known $0.10/GB-month rate × volume size.

## Done = ?

1. Provision a new DO cloud. Migration applies cleanly.
2. After `tf apply` completes, the `clouds` row has `price_monthly_usd`, `price_hourly_usd`, `priced_at`, `priced_source` populated. Values match what DigitalOcean's API returns for `s-4vcpu-8gb`.
3. Dashboard's CostCard reads from the stamped value, displays the real rate with "locked at {date}".
4. The new-cloud review screen still shows an estimated rate (hardcoded fallback table) with a note clarifying it's an estimate.
5. Provoke a DO API failure (e.g. revoke the OAuth token transiently mid-provision). Provisioning still completes successfully. Price columns remain null. CostCard falls back to the hardcoded table without throwing.
6. Run the backfill endpoint. Existing `succeeded` clouds get their prices stamped retroactively.
7. Cache hit on a second provisioning within 24h does NOT trigger a new HTTP call to DO — observable via OTel http-client metrics.
