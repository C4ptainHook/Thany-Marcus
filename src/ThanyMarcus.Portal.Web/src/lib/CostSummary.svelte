<script lang="ts">
  import { DigitalOceanCosts as Costs } from './costs';
</script>

<section class="cost-summary">
  <h3>Estimated cost</h3>
  <p class="headline">
    ~${Costs.typicalMonthlyMin}–${Costs.typicalMonthlyMax} / month
    <span class="muted">(typical solo use)</span>
  </p>
  <dl>
    <dt>Control plane</dt>
    <dd>${Costs.controlPlaneMonthly} / month (always on, s-2vcpu-4gb)</dd>
    <dt>Burst worker</dt>
    <dd>${Costs.workerHourly}/h (s-4vcpu-16gb, spun up on demand)</dd>
  </dl>
  <p class="muted small">
    Workers stay warm for {Costs.warmWindowMinutes} min after the last task; cold start ~{Costs.coldStartMinutes} min.
    No commitment — destroy at any time.
  </p>
</section>

<style>
  .cost-summary {
    border: 1px solid var(--color-border);
    border-radius: var(--radius);
    padding: var(--space-3);
    background: var(--color-chip-bg);
    margin: var(--space-3) 0;
  }
  .cost-summary h3 {
    margin-top: 0;
  }
  .headline {
    font-size: 1.1rem;
  }
  dl {
    display: grid;
    grid-template-columns: 10rem 1fr;
    gap: var(--space-1) var(--space-3);
    margin: var(--space-2) 0;
  }
  dt {
    color: var(--color-muted);
  }
  .small {
    font-size: 0.9rem;
  }
</style>
