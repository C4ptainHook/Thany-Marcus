<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { subscribeToEvents } from './sse';
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
</section>

<style>
  .phase-list {
    list-style: none;
    padding: 0;
    margin: var(--space-3) 0 0;
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
  }
</style>
