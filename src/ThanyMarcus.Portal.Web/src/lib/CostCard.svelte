<script lang="ts">
  import { computeMonthCosts } from './costs';
  import type { CloudStatusResponse } from './types/cloud';

  type Props = { cloud: CloudStatusResponse };
  let { cloud }: Props = $props();

  let breakdown = $derived(computeMonthCosts({
    provider: cloud.provider,
    provisionedAt: cloud.succeededAt,
    priceHourlyUsd: cloud.priceHourlyUsd,
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
    {#if breakdown.unavailable}
      pricing unavailable for this provider
    {:else}
      {breakdown.sku} · {breakdown.hours?.toFixed(1)} hrs since {cloud.succeededAt ? 'provision' : 'month start'}
      {#if cloud.priceHourlyUsd != null}
        · ${cloud.priceHourlyUsd.toFixed(5)}/h{#if cloud.pricedAt} · locked {new Date(cloud.pricedAt).toLocaleDateString()}{/if}
      {/if}
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
