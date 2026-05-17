<script lang="ts">
  import { onMount } from 'svelte';
  import { apiFetch, parseProblem } from './http';

  type Props = { provider: 'digitalocean'; onSaved?: () => void };
  let { provider, onSaved }: Props = $props();

  interface TokenSummary { provider: string; createdAt: string; updatedAt: string; }

  let status = $state<'loading' | 'not-set' | 'set'>('loading');
  let summary = $state<TokenSummary | null>(null);
  let tokenInput = $state('');
  let error = $state<string | null>(null);
  let confirmingRemove = $state(false);
  let busy = $state(false);

  const labels: Record<Props['provider'], string> = { digitalocean: 'DigitalOcean' };
  let providerLabel = $derived(labels[provider]);

  async function refresh() {
    status = 'loading';
    error = null;
    try {
      const r = await fetch('/api/clouds/provider-tokens');
      if (!r.ok) { status = 'not-set'; summary = null; return; }
      const list = (await r.json()) as TokenSummary[];
      const me = list.find(p => p.provider === provider) ?? null;
      summary = me;
      status = me ? 'set' : 'not-set';
    } catch {
      status = 'not-set';
      summary = null;
    }
  }

  onMount(refresh);

  function formatDate(iso: string): string {
    try { return new Date(iso).toLocaleString(); }
    catch { return iso; }
  }

  function tokenLooksValid(t: string): boolean {
    return t.trim().length >= 16 && !/\s/.test(t);
  }

  async function save() {
    error = null;
    if (!tokenLooksValid(tokenInput)) {
      error = "That doesn't look like a valid token.";
      return;
    }
    busy = true;
    try {
      const r = await apiFetch('/api/clouds/provider-tokens', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ provider, token: tokenInput, replace: summary !== null }),
      });
      if (r.status === 204) {
        tokenInput = '';
        await refresh();
        onSaved?.();
        return;
      }
      const problem = await parseProblem(r);
      error = problem?.error ?? `Save failed (HTTP ${r.status}).`;
    } finally {
      busy = false;
    }
  }

  async function remove() {
    confirmingRemove = false;
    busy = true;
    try {
      const r = await apiFetch(`/api/clouds/provider-tokens/${provider}`, { method: 'DELETE' });
      if (r.ok || r.status === 204) await refresh();
      else error = `Remove failed (HTTP ${r.status}).`;
    } finally {
      busy = false;
    }
  }

  function rotate() {
    status = 'not-set';
    tokenInput = '';
  }
</script>

<section class="provider-row">
  <h3>{providerLabel}</h3>
  {#if status === 'loading'}
    <p class="muted">Loading…</p>
  {:else if status === 'set' && summary}
    <p>
      Saved · {summary.createdAt === summary.updatedAt ? 'created' : 'last rotated'}
      {formatDate(summary.updatedAt)}
    </p>
    <div class="actions">
      <button onclick={rotate} disabled={busy}>Rotate</button>
      <button onclick={() => confirmingRemove = true} disabled={busy}>Remove</button>
    </div>
  {:else}
    <label>
      API token
      <input type="password" autocomplete="off" bind:value={tokenInput} />
    </label>
    {#if error}<p class="error">{error}</p>{/if}
    <button onclick={save} disabled={!tokenInput || busy}>Save</button>
  {/if}

  {#if confirmingRemove}
    <div
      class="backdrop"
      role="button"
      tabindex="0"
      aria-label="Cancel"
      onclick={() => confirmingRemove = false}
      onkeydown={(e) => { if (e.key === 'Escape') confirmingRemove = false; }}
    ></div>
    <div class="modal" role="dialog" aria-modal="true">
      <h4>Remove {providerLabel} token?</h4>
      <p>
        Removing this token will prevent any further infrastructure operations against your
        {providerLabel} clouds. Continue?
      </p>
      <div class="actions">
        <button onclick={() => confirmingRemove = false}>Cancel</button>
        <button class="danger" onclick={remove}>Remove</button>
      </div>
    </div>
  {/if}
</section>

<style>
  .provider-row {
    border: var(--border-width) solid var(--border);
    padding: var(--space-3);
    margin: var(--space-3) 0;
    background: var(--surface-2);
  }
  .actions {
    display: flex;
    gap: var(--space-2);
    margin-top: var(--space-3);
  }
  label {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    margin-bottom: var(--space-2);
  }
  .backdrop {
    position: fixed; inset: 0;
    background: rgba(0, 0, 0, 0.6);
    z-index: 990;
  }
  .modal {
    position: fixed;
    top: 50%; left: 50%;
    transform: translate(-50%, -50%);
    background: var(--surface);
    color: var(--text);
    border: var(--border-width) solid var(--border);
    padding: var(--space-5);
    z-index: 991;
    max-width: 32rem;
  }
  .danger { color: var(--error); border-color: var(--error); }
  @media (max-width: 600px) {
    .modal {
      position: fixed;
      inset: 0;
      top: 0; left: 0;
      transform: none;
      max-width: none;
      width: 100%;
      height: 100%;
    }
  }
</style>
