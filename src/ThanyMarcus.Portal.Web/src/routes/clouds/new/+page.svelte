<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { apiFetch, parseProblem } from '$lib/http';
  import { projectedRates } from '$lib/costs';
  import type { RegionInfo } from '$lib/types/providerMeta';
  import type { Provider } from '$lib/types/cloud';

  const provider: Provider = 'digitalocean';

  type Step = 'region' | 'review';

  let step = $state<Step>('region');
  let region = $state('');
  let regions = $state<RegionInfo[]>([]);
  let loadingRegions = $state(false);
  let regionsError = $state<string | null>(null);
  let submitting = $state(false);
  let submitError = $state<string | null>(null);

  const continentOrder = ['North America', 'Europe', 'Asia-Pacific'];

  let continents = $derived(groupContinents(regions));

  function groupContinents(rs: RegionInfo[]) {
    return continentOrder
      .map(name => ({ name, regions: rs.filter(r => r.continent === name) }))
      .filter(c => c.regions.length > 0);
  }

  function regionLabel(slug: string): string {
    return regions.find(r => r.slug === slug)?.label ?? slug;
  }

  async function loadRegions() {
    loadingRegions = true;
    regionsError = null;
    regions = [];
    try {
      const r = await fetch(`/api/clouds/provider-meta/${provider}`);
      if (!r.ok) { regionsError = `Failed to load regions (HTTP ${r.status}).`; return; }
      const data = await r.json() as { regions: RegionInfo[] };
      regions = data.regions;
    } catch {
      regionsError = 'Failed to load regions.';
    } finally {
      loadingRegions = false;
    }
  }

  function mapErrorToMessage(code: string | undefined, status: number): string {
    switch (code) {
      case 'user_create_in_flight': return 'You already have a cloud being provisioned. Wait for it to finish.';
      case 'invalid_region':        return 'That region is not supported.';
      case 'unsupported_provider':  return 'Provider not supported.';
      case 'provider_token_missing':return 'Add a provider API token in settings first.';
      default:                      return `Provisioning failed (HTTP ${status}). Try again.`;
    }
  }

  async function submit() {
    if (!region) return;
    submitting = true;
    submitError = null;
    try {
      const r = await apiFetch('/api/clouds', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ provider, region }),
      });
      if (r.status === 202) {
        goto('/');
        return;
      }
      if (r.status === 412) {
        const problem = await parseProblem(r);
        if (problem?.error === 'connect_required') {
          const returnTo = `/clouds/new?region=${encodeURIComponent(region)}`;
          window.location.href = `/oauth/digitalocean/start?return_to=${encodeURIComponent(returnTo)}`;
          return;
        }
        submitError = mapErrorToMessage(problem?.error, r.status);
        return;
      }
      if (r.status === 401) {
        submitError = 'Passphrase required to provision.';
        return;
      }
      const problem = await parseProblem(r);
      if (r.status === 400 && problem?.error === 'invalid_region') {
        submitError = 'That region is not supported.';
        step = 'region';
        return;
      }
      submitError = mapErrorToMessage(problem?.error, r.status);
    } catch {
      submitError = 'Network error. Try again.';
    } finally {
      submitting = false;
    }
  }

  onMount(async () => {
    const params = new URLSearchParams(window.location.search);
    const err = params.get('error');
    if (err) {
      switch (err) {
        case 'oauth_failed':   submitError = 'DigitalOcean authorization did not complete. Try again.'; break;
        case 'step_up_required': submitError = 'Passphrase required to connect. Unlock and try again.'; break;
        default:               submitError = `Connection failed (${err}).`;
      }
    }
    await loadRegions();
    if (params.get('connected') === '1') {
      const presetRegion = params.get('region');
      if (presetRegion && isKnownRegion(presetRegion)) {
        region = presetRegion;
        step = 'review';
      }
    }
  });

  function isKnownRegion(slug: string): boolean {
    return regions.some(r => r.slug === slug) || /^[a-z]{3}[0-9]$/.test(slug);
  }
</script>

<main>
  <h1>Create cloud</h1>
  <p class="muted">Step {step === 'region' ? 1 : 2} of 2</p>

  {#if step === 'region'}
    <section>
      <h2>Choose a region</h2>
      {#if loadingRegions}
        <p class="muted">Loading regions…</p>
      {:else if regionsError}
        <p class="error">{regionsError}</p>
      {:else}
        <label>
          Region
          <select bind:value={region}>
            <option value="" disabled>Select a region…</option>
            {#each continents as continent (continent.name)}
              <optgroup label={continent.name}>
                {#each continent.regions as r (r.slug)}
                  <option value={r.slug}>{r.label}</option>
                {/each}
              </optgroup>
            {/each}
          </select>
        </label>
      {/if}
      <div class="actions">
        <button class="btn-primary" disabled={!region} onclick={() => step = 'review'}>Next</button>
      </div>
    </section>

  {:else}
    <section>
      <h2>Review</h2>
      <dl>
        <dt class="muted">Provider</dt>
        <dd>DigitalOcean</dd>
        <dt class="muted">Region</dt>
        <dd>{regionLabel(region)}</dd>
        <dt class="muted">Hostname</dt>
        <dd class="muted">Random name on thany.click — assigned at provision</dd>
      </dl>
      {#if provider}
        {@const rates = projectedRates(provider)}
        {#if rates}
          <section class="card">
            <p class="cost-total">${rates.monthly.toFixed(2)} / month</p>
            <p class="muted cost-detail">
              {rates.sku} · ${rates.hourly.toFixed(5)}/h, billed continuously while the droplet is up
            </p>
            <p class="muted cost-detail">Estimate. Final price is locked in at provision time from DigitalOcean's current rate.</p>
            <p class="muted cost-detail">Real accrued cost shows on the dashboard once provisioned.</p>
          </section>
        {/if}
      {/if}
      {#if submitError}<p class="error">{submitError}</p>{/if}
      <div class="actions">
        <button class="btn-secondary" onclick={() => step = 'region'} disabled={submitting}>Edit</button>
        <button class="btn-primary" disabled={submitting} onclick={submit}>
          {submitting ? 'Connecting…' : 'Connect DigitalOcean'}
        </button>
      </div>
    </section>
  {/if}
</main>

<style>
  .actions {
    display: flex;
    gap: var(--space-2);
    margin-top: var(--space-4);
  }
  label {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    margin: var(--space-2) 0;
  }
  select { width: 100%; max-width: 32rem; }
  dl {
    display: grid;
    grid-template-columns: 10rem 1fr;
    gap: var(--space-2) var(--space-4);
    margin: var(--space-3) 0;
  }
  dt { color: var(--text-dim); margin: 0; }
  dd { margin: 0; }
  .cost-total { font-size: var(--text-lg); margin: 0 0 var(--space-1); }
  .cost-detail { margin: 0; }
</style>
