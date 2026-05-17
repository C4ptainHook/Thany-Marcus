<script lang="ts">
  import { apiFetch, parseProblem } from './http';

  type Props = {
    cloudId: string;
    hostname: string;
    onDestroyEnqueued: () => void;
  };
  let { cloudId, hostname, onDestroyEnqueued }: Props = $props();

  let confirming = $state(false);
  let confirmHost = $state('');
  let busy = $state(false);
  let error = $state<string | null>(null);

  function startConfirm() {
    confirming = true;
    confirmHost = '';
    error = null;
  }

  function cancelConfirm() {
    confirming = false;
    confirmHost = '';
    error = null;
  }

  async function destroy() {
    if (confirmHost !== hostname) {
      error = 'Hostname does not match.';
      return;
    }
    busy = true;
    error = null;
    try {
      const r = await apiFetch(`/api/clouds/${cloudId}/destroy`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ confirmHostname: hostname }),
      });
      if (r.status === 202) {
        confirming = false;
        onDestroyEnqueued();
        return;
      }
      const problem = await parseProblem(r);
      error = problem?.error
        ? `Destroy failed: ${problem.error}`
        : `Destroy failed (HTTP ${r.status}).`;
    } catch {
      error = 'Network error. Try again.';
    } finally {
      busy = false;
    }
  }
</script>

<section class="card danger-zone">
  <h2 class="card-title error">DANGER ZONE</h2>

  <p>Destroy this cloud and all data on the provider.</p>
  <p class="muted">Your local Obsidian vault is NOT affected.</p>

  <div class="actions">
    <button class="btn-danger" type="button" onclick={startConfirm}>Destroy cloud</button>
  </div>
</section>

{#if confirming}
  <div class="cm-backdrop" role="presentation" onclick={cancelConfirm}></div>
  <div class="cm-modal" role="dialog" aria-modal="true" aria-labelledby="dz-title">
    <h2 id="dz-title">Destroy cloud?</h2>
    <p>
      This is permanent. All infrastructure for <code class="mono">{hostname}</code> will be
      deleted on the provider. Your local Obsidian vault is not affected.
    </p>
    <label>
      Type the hostname to confirm
      <input type="text" autocomplete="off" bind:value={confirmHost} placeholder={hostname} />
    </label>
    {#if error}<p class="error">{error}</p>{/if}
    <div class="cm-actions">
      <button class="btn-secondary" type="button" onclick={cancelConfirm} disabled={busy}>Cancel</button>
      <button class="btn-danger"    type="button" onclick={destroy} disabled={busy || confirmHost !== hostname}>
        {busy ? 'Destroying…' : 'Destroy permanently'}
      </button>
    </div>
  </div>
{/if}

<style>
  .actions {
    display: flex;
    justify-content: flex-end;
    margin-top: var(--space-3);
  }
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
    border: var(--border-width) solid var(--error);
    max-width: 36rem;
    z-index: 991;
  }
  .cm-modal label {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    margin-top: var(--space-3);
  }
  .cm-modal input { width: 100%; box-sizing: border-box; }
  .cm-actions {
    display: flex;
    gap: var(--space-2);
    justify-content: flex-end;
    margin-top: var(--space-4);
  }
  .mono { font-family: var(--font-pixel); }
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
