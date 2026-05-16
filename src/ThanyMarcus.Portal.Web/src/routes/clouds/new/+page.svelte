<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { apiFetch, parseProblem } from '$lib/http';
  import CostSummary from '$lib/CostSummary.svelte';
  import type { RegionInfo } from '$lib/types/providerMeta';

  let step = $state<'target' | 'review'>('target');
  let region = $state('');
  let regions = $state<RegionInfo[]>([]);
  let loadingRegions = $state(true);
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

  onMount(async () => {
    try {
      const r = await fetch('/api/clouds/provider-meta/digitalocean');
      if (!r.ok) { regionsError = `Failed to load regions (HTTP ${r.status}).`; return; }
      const data = await r.json() as { regions: RegionInfo[] };
      regions = data.regions;
    } catch {
      regionsError = 'Failed to load regions.';
    } finally {
      loadingRegions = false;
    }
  });

  function mapErrorToMessage(code: string | undefined, status: number): string {
    switch (code) {
      case 'user_create_in_flight': return 'You already have a cloud being provisioned. Wait for it to finish.';
      case 'invalid_region':        return 'That region is not supported.';
      case 'unsupported_provider':  return 'Provider not supported.';
      default:                      return `Provisioning failed (HTTP ${status}). Try again.`;
    }
  }

  async function submit() {
    submitting = true;
    submitError = null;
    try {
      const r = await apiFetch('/api/clouds', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ provider: 'digitalocean', region }),
      });
      if (r.status === 202) {
        const { cloudId } = await r.json() as { cloudId: string };
        goto(`/clouds/${cloudId}`);
        return;
      }
      if (r.status === 401) {
        submitError = 'Passphrase required to provision.';
        return;
      }
      if (r.status === 400 && (await parseProblem(r))?.error === 'invalid_region') {
        submitError = 'That region is not supported.';
        step = 'target';
        return;
      }
      const problem = await parseProblem(r);
      submitError = mapErrorToMessage(problem?.error, r.status);
    } catch {
      submitError = 'Network error. Try again.';
    } finally {
      submitting = false;
    }
  }
</script>

<main>
  <h1>Create cloud</h1>
  <p class="muted">Provider: DigitalOcean</p>

  {#if step === 'target'}
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
        <button disabled={!region} onclick={() => step = 'review'}>Next</button>
      </div>
    </section>
  {:else}
    <section>
      <h2>Review</h2>
      <dl>
        <dt>Region</dt>
        <dd>{regionLabel(region)}</dd>
        <dt>Hostname</dt>
        <dd class="muted">Will be assigned when you provision (random name on thany.click).</dd>
      </dl>
      <CostSummary />
      {#if submitError}<p class="error">{submitError}</p>{/if}
      <div class="actions">
        <button onclick={() => step = 'target'} disabled={submitting}>Edit</button>
        <button class="primary" disabled={submitting} onclick={submit}>
          {submitting ? 'Provisioning…' : 'Provision'}
        </button>
      </div>
    </section>
  {/if}
</main>

<style>
  .actions {
    display: flex;
    gap: var(--space-2);
    margin-top: var(--space-3);
  }
  label {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    margin: var(--space-2) 0;
  }
  select {
    padding: var(--space-1) var(--space-2);
    font: inherit;
  }
  dl {
    display: grid;
    grid-template-columns: 8rem 1fr;
    gap: var(--space-1) var(--space-3);
  }
  dt { color: var(--color-muted); }
  .primary {
    background: var(--color-text);
    color: var(--color-bg);
  }
</style>
