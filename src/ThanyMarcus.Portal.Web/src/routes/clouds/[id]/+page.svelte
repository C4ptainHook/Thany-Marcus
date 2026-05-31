<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { subscribeToEvents } from '$lib/sse';
  import CostCard from '$lib/CostCard.svelte';
  import StatusPill from '$lib/StatusPill.svelte';
  import { PhaseOrder, type PhaseName, type WizardSseEvent } from '$lib/types/provisioning';
  import type { CloudStatusResponse } from '$lib/types/cloud';

  let { data }: { data: { cloud: CloudStatusResponse } } = $props();
  const initialCloud = untrack(() => data.cloud);
  let cloud = $state<CloudStatusResponse>(initialCloud);

  type PhaseState = 'pending' | 'in_progress' | 'done' | 'failed';
  type TerminalCard =
    | { kind: 'ready'; cloudId: string; hostname: string; ip: string | null }
    | { kind: 'failed'; terminalStatus: string; reason: string; message: string }
    | { kind: 'rolled_back'; reason: string };

  function initialPhaseStates(c: CloudStatusResponse): Record<PhaseName, PhaseState> {
    const map: Record<PhaseName, PhaseState> = {
      minting_spaces: 'pending',
      tf_planning: 'pending',
      tf_applying: 'pending',
      dns_creating: 'pending',
      awaiting_cloud_callback: 'pending',
      cloud_registered: 'pending',
      awaiting_cert: 'pending',
      issuing_plugin_token: 'pending',
    };
    const currentIdx = PhaseOrder.indexOf(c.provisioningStatus as PhaseName);
    if (currentIdx >= 0) {
      for (let i = 0; i < currentIdx; i++) map[PhaseOrder[i]] = 'done';
      map[PhaseOrder[currentIdx]] = 'in_progress';
    } else if (c.provisioningStatus === 'succeeded') {
      for (const p of PhaseOrder) map[p] = 'done';
    }
    return map;
  }

  function initialTerminal(c: CloudStatusResponse): TerminalCard | null {
    if (c.provisioningStatus === 'succeeded') {
      return { kind: 'ready', cloudId: c.cloudId, hostname: c.hostname, ip: null };
    }
    if (c.provisioningStatus === 'rolled_back') {
      return { kind: 'rolled_back', reason: 'rolled_back' };
    }
    if (c.provisioningStatus.startsWith('failed_')) {
      return { kind: 'failed', terminalStatus: c.provisioningStatus, reason: c.provisioningStatus, message: '' };
    }
    return null;
  }

  let phases = $state(initialPhaseStates(initialCloud));
  let terminal = $state<TerminalCard | null>(initialTerminal(initialCloud));

  function updatePhase(phase: PhaseName, state: PhaseState) {
    phases = { ...phases, [phase]: state };
  }

  onMount(() => {
    if (terminal) return;
    const close = subscribeToEvents<WizardSseEvent>(`/api/clouds/${cloud.cloudId}/events`, {
      phase_started:   (e) => { if (e.type === 'phase_started')   updatePhase(e.phase, 'in_progress'); },
      phase_completed: (e) => { if (e.type === 'phase_completed') updatePhase(e.phase, 'done'); },
      phase_failed:    (e) => {
        if (e.type !== 'phase_failed') return;
        updatePhase(e.phase, 'failed');
        terminal = { kind: 'failed', terminalStatus: 'failed_' + e.phase, reason: e.reason, message: e.message };
        close();
      },
      cloud_ready: (e) => {
        if (e.type !== 'cloud_ready') return;
        cloud = { ...cloud, hostname: e.hostname, succeededAt: new Date().toISOString(), provisioningStatus: 'succeeded' };
        for (const p of PhaseOrder) updatePhase(p, 'done');
        terminal = { kind: 'ready', cloudId: e.cloudId, hostname: e.hostname, ip: e.ip };
        close();
      },
      cloud_failed: (e) => {
        if (e.type !== 'cloud_failed') return;
        terminal = { kind: 'failed', terminalStatus: e.terminalStatus, reason: e.reason, message: e.message };
        close();
      },
      cloud_rolled_back: (e) => {
        if (e.type !== 'cloud_rolled_back') return;
        terminal = { kind: 'rolled_back', reason: e.reason };
        close();
      },
    });
    return close;
  });

  function variant(s: PhaseState): 'success' | 'warn' | 'idle' | 'error' {
    return s === 'done' ? 'success' : s === 'in_progress' ? 'warn' : s === 'failed' ? 'error' : 'idle';
  }
</script>

<main>
  <h1>{cloud.hostname}</h1>
  <p class="muted">{cloud.provider} · {cloud.region}</p>

  <section class="card">
    <h2>Progress</h2>
    <ol class="phase-list">
      {#each PhaseOrder as phase (phase)}
        <li class="phase">
          <StatusPill variant={variant(phases[phase])} label={phase.replace(/_/g, ' ')} />
        </li>
      {/each}
    </ol>
  </section>

  {#if terminal?.kind === 'ready'}
    <section class="card success-card">
      <h2 class="success">Cloud ready</h2>
      <p>
        Your cloud is live at
        <a href={'https://' + terminal.hostname} target="_blank" rel="noreferrer">
          {terminal.hostname}
        </a>.
      </p>
      <p class="muted">
        First request after idle ~5 min may take a moment while the burst worker warms up.
      </p>
      <p><a href="/">Back to dashboard</a></p>
    </section>
  {:else if terminal?.kind === 'failed'}
    <section class="card failure-card">
      <h2 class="error">Provisioning failed</h2>
      <p>{terminal.message || terminal.reason}</p>
      <p class="muted">Status: {terminal.terminalStatus}</p>
      <p>
        <a class="btn btn-primary" href="/clouds/new">Try again</a>
      </p>
    </section>
  {:else if terminal?.kind === 'rolled_back'}
    <section class="card">
      <h2>Rolled back</h2>
      <p class="muted">All resources have been cleaned up.</p>
      <p><a class="btn btn-primary" href="/clouds/new">Create a new cloud</a></p>
    </section>
  {/if}

  <CostCard cloud={cloud} />
</main>

<style>
  .phase-list {
    list-style: none;
    padding: 0;
    margin: var(--space-3) 0 0;
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
  }
  .success-card { border-color: var(--success); }
  .failure-card { border-color: var(--error); }
</style>
