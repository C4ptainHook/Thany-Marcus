<script lang="ts">
  import { onMount } from 'svelte';
  import { apiFetch } from './http';
  import PluginTokenModal from './PluginTokenModal.svelte';

  type Props = { cloudId: string; cloudHostname: string };
  let { cloudId, cloudHostname }: Props = $props();

  type State =
    | { kind: 'loading' }
    | { kind: 'none' }
    | { kind: 'active'; createdAt: string };

  let view = $state<State>({ kind: 'loading' });
  let busy = $state(false);
  let error = $state<string | null>(null);
  let confirmReissue = $state(false);
  let issued = $state<{ token: string; cloudUrl: string } | null>(null);
  let copiedUrl = $state(false);

  async function refresh() {
    const r = await fetch(`/api/clouds/${cloudId}/plugin-tokens`);
    if (!r.ok) { view = { kind: 'none' }; return; }
    const body = (await r.json()) as { active: null | { createdAt: string } };
    view = body.active ? { kind: 'active', createdAt: body.active.createdAt } : { kind: 'none' };
  }

  onMount(refresh);

  async function issue() {
    busy = true;
    error = null;
    try {
      const r = await apiFetch(`/api/clouds/${cloudId}/plugin-tokens`, { method: 'POST' });
      if (!r.ok) { error = `Failed (HTTP ${r.status}).`; return; }
      const body = (await r.json()) as { token: string; cloudUrl: string };
      issued = { token: body.token, cloudUrl: body.cloudUrl };
      await refresh();
    } finally {
      busy = false;
    }
  }

  function startReissue() {
    confirmReissue = true;
  }

  async function confirmAndIssue() {
    confirmReissue = false;
    await issue();
  }

  async function copyUrl() {
    try {
      await navigator.clipboard.writeText('https://' + cloudHostname);
      copiedUrl = true;
      setTimeout(() => copiedUrl = false, 1500);
    } catch { /* clipboard blocked */ }
  }

  function fmtDate(iso: string): string {
    try { return new Date(iso).toISOString().slice(0, 10); }
    catch { return iso; }
  }
</script>

<section class="card">
  <h2 class="card-title">PLUGIN CONNECTION</h2>

  {#if view.kind === 'loading'}
    <p class="muted">Loading…</p>
  {:else if view.kind === 'none'}
    <p class="muted">The Obsidian plugin uses this token to authenticate.</p>
    {#if error}<p class="error">{error}</p>{/if}
    <button class="btn-primary" type="button" disabled={busy} onclick={issue}>
      {busy ? 'Issuing…' : 'Issue plugin token'}
    </button>
  {:else}
    <p>Token issued {fmtDate(view.createdAt)}</p>
    <div class="url-row">
      <span class="muted">Cloud URL:</span>
      <code class="mono">{cloudHostname}</code>
      <button class="btn-ghost copy" type="button" onclick={copyUrl}>
        {copiedUrl ? 'Copied' : 'Copy'}
      </button>
    </div>
    {#if error}<p class="error">{error}</p>{/if}
    <button class="btn-secondary" type="button" disabled={busy} onclick={startReissue}>
      Re-issue token
    </button>
    <span class="muted">(revokes current)</span>
  {/if}
</section>

{#if confirmReissue}
  <div class="cm-backdrop" role="presentation" onclick={() => confirmReissue = false}></div>
  <div class="cm-modal" role="dialog" aria-modal="true">
    <h2>Re-issue plugin token?</h2>
    <p>
      This revokes the current token. The plugin will disconnect on next sync until you paste the new token.
    </p>
    <div class="cm-actions">
      <button class="btn-secondary" type="button" onclick={() => confirmReissue = false}>Cancel</button>
      <button class="btn-danger"    type="button" onclick={confirmAndIssue}>Continue and issue</button>
    </div>
  </div>
{/if}

{#if issued}
  <PluginTokenModal token={issued.token} cloudUrl={issued.cloudUrl} onClose={() => issued = null} />
{/if}

<style>
  .url-row {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    margin: var(--space-2) 0 var(--space-3);
    flex-wrap: wrap;
  }
  .mono { font-family: var(--font-pixel); }
  .copy { height: 28px; }

  .cm-backdrop {
    position: fixed; inset: 0;
    background: rgba(0, 0, 0, 0.6);
    z-index: 990;
  }
  .cm-modal {
    position: fixed;
    top: 50%; left: 50%;
    transform: translate(-50%, -50%);
    background: var(--surface);
    color: var(--text);
    padding: var(--space-5);
    border: var(--border-width) solid var(--border);
    max-width: 32rem;
    z-index: 991;
  }
  .cm-actions {
    display: flex;
    gap: var(--space-2);
    justify-content: flex-end;
    margin-top: var(--space-4);
  }
  @media (max-width: 600px) {
    .cm-modal {
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
