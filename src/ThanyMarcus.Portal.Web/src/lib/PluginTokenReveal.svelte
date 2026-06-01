<script lang="ts">
  import { onMount } from 'svelte';
  import { apiFetch } from './http';
  import { subscribeToEvents } from './sse';
  import type { WizardSseEvent } from './types/provisioning';

  type Props = { cloudId: string };
  let { cloudId }: Props = $props();

  let rawToken = $state<string | null>(null);
  let deepLink = $state<string | null>(null);
  let copied = $state(false);
  let retrying = $state(false);
  let retryError = $state<string | null>(null);
  let failedPluginToken = $state(false);

  onMount(() => {
    const close = subscribeToEvents<WizardSseEvent>(`/api/clouds/${cloudId}/events`, {
      plugin_token_issued: (e) => {
        if (e.type !== 'plugin_token_issued') return;
        rawToken = e.rawToken;
        deepLink = e.deepLink;
        failedPluginToken = false;
      },
      cloud_failed: (e) => {
        if (e.type !== 'cloud_failed') return;
        if (e.terminalStatus === 'failed_plugin_token') {
          failedPluginToken = true;
        }
      },
    });
    return close;
  });

  async function copyToken() {
    if (!rawToken) return;
    try {
      await navigator.clipboard.writeText(rawToken);
      copied = true;
      setTimeout(() => copied = false, 1500);
    } catch { /* clipboard blocked */ }
  }

  async function retry() {
    retrying = true;
    retryError = null;
    try {
      const r = await apiFetch(`/api/clouds/${cloudId}/plugin-tokens/retry`, { method: 'POST' });
      if (r.status !== 202) {
        retryError = `Retry failed (HTTP ${r.status}).`;
        return;
      }
      failedPluginToken = false;
    } catch {
      retryError = 'Network error. Try again.';
    } finally {
      retrying = false;
    }
  }
</script>

{#if rawToken && deepLink}
  <section class="card reveal">
    <h2 class="card-title">PLUGIN SETUP</h2>
    <p class="muted">Token shown once — copy now. We don't store the raw value.</p>

    <div class="token-row">
      <code class="mono">{rawToken}</code>
      <button class="btn-ghost copy" type="button" onclick={copyToken}>
        {copied ? 'Copied' : 'Copy'}
      </button>
    </div>

    <div class="actions">
      <a class="btn-primary" href={deepLink} title="Opens the Thany-Marcus plugin in Obsidian">
        Open in Obsidian
      </a>
    </div>

    <p class="muted small">
      Lost it later? Use Settings → Plugin token to re-issue.
    </p>
  </section>
{:else if failedPluginToken}
  <section class="card reveal">
    <h2 class="card-title">PLUGIN SETUP</h2>
    <p class="error">Plugin token setup failed.</p>
    {#if retryError}<p class="error">{retryError}</p>{/if}
    <div class="actions">
      <button class="btn-primary" type="button" disabled={retrying} onclick={retry}>
        {retrying ? 'Retrying…' : 'Retry plugin setup'}
      </button>
    </div>
  </section>
{/if}

<style>
  .reveal { margin-top: var(--space-3); }
  .token-row {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    margin: var(--space-2) 0;
    flex-wrap: wrap;
  }
  .mono {
    font-family: var(--font-pixel);
    word-break: break-all;
    flex: 1;
  }
  .copy { height: 28px; }
  .actions {
    display: flex;
    gap: var(--space-2);
    margin: var(--space-3) 0;
  }
  .small { font-size: var(--text-sm); }
</style>
