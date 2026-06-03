<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import StatusPill from './StatusPill.svelte';
  import type { CloudStatusResponse, WorkerState } from './types/cloud';

  let { cloud }: { cloud: CloudStatusResponse } = $props();

  let workerState = $state<WorkerState | null>(null);
  let unavailable = $state(false);
  let timer: ReturnType<typeof setInterval> | null = null;

  const PROVIDER_LABEL: Record<string, string> = {
    digitalocean: 'DIGITALOCEAN',
  };

  async function ping() {
    try {
      const ctrl = AbortSignal.timeout(3000);
      const r = await fetch(`https://${cloud.hostname}/health/live`, { signal: ctrl });
      if (!r.ok) { unavailable = true; return; }
      unavailable = false;
      workerState = 'warm';
    } catch {
      unavailable = true;
    }
  }

  onMount(() => {
    ping();
    timer = setInterval(() => {
      if (workerState === 'waking') ping();
    }, 10_000);
  });

  onDestroy(() => {
    if (timer) clearInterval(timer);
  });

  let pill = $derived.by(() => {
    if (unavailable || workerState == null) return { variant: 'idle' as const, label: 'unknown' };
    if (workerState === 'warm')   return { variant: 'success' as const, label: 'warm' };
    if (workerState === 'waking') return { variant: 'warn'    as const, label: 'waking' };
    return { variant: 'idle' as const, label: 'idle' };
  });

  function fmtDate(iso: string | null): string {
    if (!iso) return '—';
    try { return new Date(iso).toISOString().slice(0, 10); }
    catch { return iso; }
  }
</script>

<section class="card" data-cloud-id={cloud.cloudId}>
  <div class="header">
    <a class="host" href={'https://' + cloud.hostname} target="_blank" rel="noreferrer">
      {cloud.hostname}
    </a>
    <StatusPill variant={pill.variant} label={pill.label} />
  </div>
  <p class="meta muted">
    {PROVIDER_LABEL[cloud.provider] ?? cloud.provider.toUpperCase()}
    · {cloud.region.toUpperCase()}
    · created {fmtDate(cloud.succeededAt ?? null)}
  </p>
</section>

<style>
  .header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--space-3);
    margin-bottom: var(--space-2);
  }
  .host {
    font-size: var(--text-lg);
    color: var(--text);
    text-decoration: none;
  }
  .host:hover { color: var(--primary); }
  .meta { margin: 0; }
</style>
