<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { subscribeToEvents } from './sse';
  import { apiFetch, parseProblem } from './http';
  import StatusPill from './StatusPill.svelte';
  import { PhaseOrder, type PhaseName, type WizardSseEvent } from './types/provisioning';
  import type { CloudStatusResponse } from './types/cloud';

  type Mode = 'create' | 'destroy';
  type Props = {
    cloud: CloudStatusResponse;
    mode: Mode;
    onTerminal: () => void;
  };
  let { cloud, mode, onTerminal }: Props = $props();

  type PhaseState = 'pending' | 'in_progress' | 'done' | 'failed';

  const DESTROY_PHASES: PhaseName[] | string[] = ['destroying', 'rolling_back_dns', 'rolling_back_tf'] as string[];

  const initial = untrack(() => cloud);
  let order = $derived(mode === 'create' ? (PhaseOrder as readonly string[]) : (DESTROY_PHASES as readonly string[]));

  function buildInitial(c: CloudStatusResponse, ord: readonly string[]): Record<string, PhaseState> {
    const map: Record<string, PhaseState> = {};
    for (const p of ord) map[p] = 'pending';
    const idx = ord.indexOf(c.provisioningStatus);
    if (idx >= 0) {
      for (let i = 0; i < idx; i++) map[ord[i]] = 'done';
      map[ord[idx]] = 'in_progress';
    } else if (c.provisioningStatus === 'succeeded') {
      for (const p of ord) map[p] = 'done';
    }
    return map;
  }

  let phases = $state(buildInitial(initial, untrack(() => mode) === 'create' ? PhaseOrder : (DESTROY_PHASES as string[])));
  let terminalMessage = $state<string | null>(null);

  let confirming = $state(false);
  let confirmHost = $state('');
  let cancelBusy = $state(false);
  let cancelError = $state<string | null>(null);
  let cancelRequested = $state(false);

  function startConfirm() {
    confirming = true;
    confirmHost = '';
    cancelError = null;
  }

  function dismissConfirm() {
    confirming = false;
    confirmHost = '';
    cancelError = null;
  }

  async function submitCancel() {
    if (confirmHost !== initial.hostname) {
      cancelError = 'Hostname does not match.';
      return;
    }
    cancelBusy = true;
    cancelError = null;
    try {
      const r = await apiFetch(`/api/clouds/${initial.cloudId}/cancel`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ confirmHostname: initial.hostname }),
      });
      if (r.status === 202 || r.status === 410) {
        confirming = false;
        cancelRequested = true;
        return;
      }
      const problem = await parseProblem(r);
      cancelError = problem?.error
        ? `Cancel failed: ${problem.error}`
        : `Cancel failed (HTTP ${r.status}).`;
    } catch {
      cancelError = 'Network error. Try again.';
    } finally {
      cancelBusy = false;
    }
  }

  function update(phase: string, s: PhaseState) {
    phases = { ...phases, [phase]: s };
  }

  function advanceTo(phase: string, s: PhaseState) {
    const ord = mode === 'create' ? PhaseOrder : (DESTROY_PHASES as string[]);
    const idx = ord.indexOf(phase);
    if (idx < 0) { update(phase, s); return; }
    const next = { ...phases };
    for (let i = 0; i < idx; i++) {
      if (next[ord[i]] !== 'failed') next[ord[i]] = 'done';
    }
    next[phase] = s;
    phases = next;
  }

  onMount(() => {
    const close = subscribeToEvents<WizardSseEvent>(`/api/clouds/${initial.cloudId}/events`, {
      phase_started:   (e) => { if (e.type === 'phase_started')   advanceTo(e.phase, 'in_progress'); },
      phase_completed: (e) => { if (e.type === 'phase_completed') advanceTo(e.phase, 'done'); },
      phase_failed:    (e) => {
        if (e.type !== 'phase_failed') return;
        update(e.phase, 'failed');
        terminalMessage = e.message || e.reason;
        close();
        setTimeout(onTerminal, 800);
      },
      cloud_ready: () => {
        for (const p of (mode === 'create' ? PhaseOrder : (DESTROY_PHASES as string[]))) update(p, 'done');
        close();
        setTimeout(onTerminal, 400);
      },
      cloud_failed: (e) => {
        if (e.type !== 'cloud_failed') return;
        terminalMessage = e.message || e.reason;
        close();
        setTimeout(onTerminal, 800);
      },
      cloud_rolled_back: () => {
        close();
        setTimeout(onTerminal, 400);
      },
    });
    return close;
  });

  function variant(s: PhaseState): 'success' | 'warn' | 'idle' | 'error' {
    return s === 'done' ? 'success' : s === 'in_progress' ? 'warn' : s === 'failed' ? 'error' : 'idle';
  }

  function label(phase: string): string {
    return phase.replace(/_/g, ' ');
  }
</script>

<section class="card">
  <h2>{mode === 'create' ? 'Provisioning your cloud' : 'Destroying cloud'}</h2>
  <p class="muted">{initial.hostname}</p>

  <ol class="phase-list">
    {#each order as phase (phase)}
      <li class="phase">
        <StatusPill variant={variant(phases[phase])} label={label(phase)} />
      </li>
    {/each}
  </ol>

  {#if terminalMessage}
    <p class="error">{terminalMessage}</p>
  {/if}

  {#if mode === 'create' && !terminalMessage}
    {#if cancelRequested}
      <p class="muted">Cancellation requested — finalizing on the server…</p>
    {:else}
      <div class="actions">
        <button class="btn-danger" type="button" onclick={startConfirm}>Abort provisioning</button>
      </div>
    {/if}
  {/if}
</section>

{#if confirming}
  <div class="cm-backdrop" role="presentation" onclick={dismissConfirm}></div>
  <div class="cm-modal" role="dialog" aria-modal="true" aria-labelledby="cancel-title">
    <h2 id="cancel-title">Abort provisioning?</h2>
    <p>
      Stop the in-flight provisioning for <code class="mono">{initial.hostname}</code> and tear down
      anything already created on the provider. You can start over afterwards.
    </p>
    <label>
      Type the hostname to confirm
      <input type="text" autocomplete="off" bind:value={confirmHost} placeholder={initial.hostname} />
    </label>
    {#if cancelError}<p class="error">{cancelError}</p>{/if}
    <div class="cm-actions">
      <button class="btn-secondary" type="button" onclick={dismissConfirm} disabled={cancelBusy}>Keep going</button>
      <button class="btn-danger"    type="button" onclick={submitCancel}    disabled={cancelBusy || confirmHost !== initial.hostname}>
        {cancelBusy ? 'Aborting…' : 'Abort provisioning'}
      </button>
    </div>
  </div>
{/if}

<style>
  .phase-list {
    list-style: none;
    padding: 0;
    margin: var(--space-3) 0 0;
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
  }
  .actions {
    display: flex;
    justify-content: flex-end;
    margin-top: var(--space-4);
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
