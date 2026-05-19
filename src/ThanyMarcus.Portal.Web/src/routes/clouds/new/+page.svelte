<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { apiFetch, parseProblem } from '$lib/http';
  import { projectedRates } from '$lib/costs';
  import type { RegionInfo } from '$lib/types/providerMeta';
  import type { Provider } from '$lib/types/cloud';

  type ProviderOption = {
    id: Provider;
    name: string;
    tagline: string;
    price: string;
    disabled?: boolean;
    preview?: boolean;
  };

  const providers: ProviderOption[] = [
    { id: 'digitalocean', name: 'DigitalOcean', tagline: 'Simple droplets, fast spin-up.',  price: 'from $14/mo' },
    { id: 'hetzner',      name: 'Hetzner',      tagline: 'Cheap EU compute.',                price: 'from $9/mo'  },
    { id: 'azure',        name: 'Azure',        tagline: 'Preview — validation only.',       price: '—', disabled: true, preview: true },
  ];

  type Step = 'provider' | 'region' | 'review';

  let step = $state<Step>('provider');
  let provider = $state<Provider | ''>('');
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

  async function loadRegionsFor(p: Provider) {
    loadingRegions = true;
    regionsError = null;
    regions = [];
    try {
      const r = await fetch(`/api/clouds/provider-meta/${p}`);
      if (!r.ok) { regionsError = `Failed to load regions (HTTP ${r.status}).`; return; }
      const data = await r.json() as { regions: RegionInfo[] };
      regions = data.regions;
    } catch {
      regionsError = 'Failed to load regions.';
    } finally {
      loadingRegions = false;
    }
  }

  function pickProvider(p: Provider) {
    if (provider === p) return;
    provider = p;
    region = '';
  }

  async function nextFromProvider() {
    if (!provider) return;
    step = 'region';
    if (regions.length === 0 || !loadingRegions) await loadRegionsFor(provider);
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
    if (!provider || !region) return;
    if (provider === 'digitalocean') {
      submitting = true;
      window.location.href = `/oauth/digitalocean/start?region=${encodeURIComponent(region)}`;
      return;
    }
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

  onMount(() => {
    const params = new URLSearchParams(window.location.search);
    const err = params.get('error');
    if (err) {
      switch (err) {
        case 'oauth_failed':   submitError = 'DigitalOcean authorization did not complete. Try again.'; break;
        case 'step_up_required': submitError = 'Passphrase required to connect. Unlock and try again.'; break;
        default:               submitError = `Connection failed (${err}).`;
      }
    }
  });
</script>

<main>
  <h1>Create cloud</h1>
  <p class="muted">Step {step === 'provider' ? 1 : step === 'region' ? 2 : 3} of 3</p>

  {#if step === 'provider'}
    <section>
      <h2>Choose a provider</h2>
      <div class="providers">
        {#each providers as p (p.id)}
          <button
            type="button"
            class={'provider-card ' + (provider === p.id ? 'selected' : '') + (p.disabled ? ' disabled' : '')}
            disabled={p.disabled}
            onclick={() => pickProvider(p.id)}
          >
            <span class="radio">{provider === p.id ? '●' : p.disabled ? '○' : '○'}</span>
            <span class="body">
              <span class="name">{p.name}</span>
              <span class="tagline muted">{p.tagline}</span>
            </span>
            <span class="price muted">{p.price}</span>
          </button>
        {/each}
      </div>
      <div class="actions">
        <button class="btn-primary" disabled={!provider} onclick={nextFromProvider}>Next</button>
      </div>
    </section>

  {:else if step === 'region'}
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
        <button class="btn-secondary" onclick={() => step = 'provider'}>Back</button>
        <button class="btn-primary" disabled={!region} onclick={() => step = 'review'}>Next</button>
      </div>
    </section>

  {:else}
    <section>
      <h2>Review</h2>
      <dl>
        <dt class="muted">Provider</dt>
        <dd>{providers.find(p => p.id === provider)?.name ?? provider}</dd>
        <dt class="muted">Region</dt>
        <dd>{regionLabel(region)}</dd>
        <dt class="muted">Hostname</dt>
        <dd class="muted">Random name on thany.click — assigned at provision</dd>
      </dl>
      {#if provider}
        {@const rates = projectedRates(provider)}
        {#if rates}
          <section class="card">
            <p class="cost-total">From ${rates.controlPlaneMonthly.toFixed(2)} / month</p>
            <p class="muted cost-detail">
              ${rates.controlPlaneMonthly.toFixed(2)} control plane (always-on, {rates.controlPlaneSku})
              · ${rates.workerHourly.toFixed(3)}/h worker ({rates.workerSku}, billed only when active)
            </p>
            <p class="muted cost-detail">Real accrued cost shows on the dashboard once provisioned.</p>
          </section>
        {/if}
      {/if}
      {#if submitError}<p class="error">{submitError}</p>{/if}
      <div class="actions">
        <button class="btn-secondary" onclick={() => step = 'provider'} disabled={submitting}>Edit</button>
        <button class="btn-primary" disabled={submitting} onclick={submit}>
          {submitting ? (provider === 'digitalocean' ? 'Connecting…' : 'Provisioning…') : (provider === 'digitalocean' ? 'Connect DigitalOcean' : 'Provision')}
        </button>
      </div>
    </section>
  {/if}
</main>

<style>
  .providers {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
    margin: var(--space-3) 0;
  }
  .provider-card {
    height: auto;
    display: flex;
    align-items: center;
    gap: var(--space-3);
    padding: var(--space-3) var(--space-4);
    background: var(--surface);
    border: var(--border-width) solid var(--border);
    color: var(--text);
    cursor: pointer;
    text-align: left;
    font: inherit;
    border-radius: 0;
  }
  .provider-card:hover:not(.disabled):not(.selected) { border-color: var(--text-dim); }
  .provider-card.selected { border-color: var(--primary); }
  .provider-card.disabled { cursor: not-allowed; opacity: 0.5; }

  .radio { font-size: var(--text-lg); color: var(--primary); width: 1.5rem; }
  .body { display: flex; flex-direction: column; gap: 2px; flex: 1; }
  .name { font-size: var(--text-base); }
  .tagline { font-size: var(--text-sm); }
  .price { font-size: var(--text-sm); }

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
