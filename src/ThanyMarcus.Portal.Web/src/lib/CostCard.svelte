<script lang="ts">
  import { computeMonthCosts } from './costs';
  import type { CloudStatusResponse } from './types/cloud';

  type Props = {
    cloud: CloudStatusResponse;
    workerUptimeMonthSeconds: number | null;
  };
  let { cloud, workerUptimeMonthSeconds }: Props = $props();

  let breakdown = $derived(computeMonthCosts({
    provider: cloud.provider,
    provisionedAt: cloud.succeededAt,
    workerUptimeMonthSeconds,
  }));

  function fmt(n: number | null): string {
    if (n == null) return '—';
    return '$' + n.toFixed(2);
  }
</script>

<section class="card">
  <p class="total">
    {fmt(breakdown.totalThisMonth)} this month
  </p>
  <p class="meta muted">
    {fmt(breakdown.controlPlaneCost)} control plane (always-on)
    {#if breakdown.workerUnavailable}
      · worker stats unavailable
    {:else}
      · {fmt(breakdown.workerCost)} worker ({breakdown.workerHours?.toFixed(1)} hrs)
    {/if}
  </p>
</section>

<style>
  .total {
    font-size: var(--text-lg);
    margin: 0 0 var(--space-1);
  }
  .meta { margin: 0; }
</style>
