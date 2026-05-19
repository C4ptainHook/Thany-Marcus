<script lang="ts">
  import { untrack } from 'svelte';
  import { setPassphrase } from './stepUpClient';
  import type { MeResponse } from './types/auth';

  let { me, onPassphraseSet }: { me: MeResponse; onPassphraseSet: () => void } = $props();

  let passphraseSet = $state(untrack(() => me.passphraseSet === true));

  let expanded = $state<1 | null>(null);

  let pp = $state('');
  let pp2 = $state('');
  let ppError = $state<string | null>(null);
  let ppBusy = $state(false);

  async function submitPassphrase(e: SubmitEvent) {
    e.preventDefault();
    ppError = null;
    if (pp !== pp2)      { ppError = 'Passphrases do not match.'; return; }
    if (pp.length < 12)  { ppError = 'Passphrase must be at least 12 characters.'; return; }
    ppBusy = true;
    try {
      const r = await setPassphrase(pp);
      if (r.status === 204) {
        pp = ''; pp2 = '';
        passphraseSet = true;
        expanded = null;
        onPassphraseSet();
      } else if (r.status === 409) {
        passphraseSet = true;
        expanded = null;
      } else {
        ppError = `Failed (status ${r.status}).`;
      }
    } finally {
      ppBusy = false;
    }
  }

  function toggle(step: 1) {
    expanded = expanded === step ? null : step;
  }
</script>

<section class="card">
  <h2 class="card-title">GET YOUR CLOUD RUNNING</h2>

  <ol class="steps">
    <li class={'step ' + (passphraseSet ? 'done' : 'open')}>
      <button class="step-head" type="button" disabled={passphraseSet} onclick={() => toggle(1)}>
        <span class="icon success">{passphraseSet ? '[●]' : '[○]'}</span>
        <span class="title">1. Set a passphrase</span>
      </button>
      <p class="desc">
        Protects destructive operations like destroy and token rotation. Asked for in-context.
      </p>
      {#if expanded === 1 && !passphraseSet}
        <form class="expand" onsubmit={submitPassphrase}>
          <label>
            Passphrase
            <input type="password" autocomplete="new-password" bind:value={pp} required />
          </label>
          <label>
            Confirm
            <input type="password" autocomplete="new-password" bind:value={pp2} required />
          </label>
          {#if ppError}<p class="error">{ppError}</p>{/if}
          <button class="btn-primary" type="submit" disabled={ppBusy || !pp || !pp2}>
            {ppBusy ? 'Setting…' : 'Set passphrase'}
          </button>
        </form>
      {/if}
    </li>

    <li class={'step ' + (passphraseSet ? 'open' : 'locked')}>
      <div class="step-head static">
        <span class="icon success">{passphraseSet ? '[○]' : '[—]'}</span>
        <span class="title">2. Provision your cloud</span>
        {#if !passphraseSet}<span class="muted lock-note">(locked)</span>{/if}
      </div>
      <p class="desc">
        {#if passphraseSet}
          Pick a region and connect DigitalOcean — one click, no token paste.
        {:else}
          Needs passphrase set first.
        {/if}
      </p>
      {#if passphraseSet}
        <a class="btn btn-primary go" href="/clouds/new">Provision cloud</a>
      {/if}
    </li>
  </ol>
</section>

<style>
  .steps {
    list-style: none;
    padding: 0;
    margin: 0;
    display: flex;
    flex-direction: column;
    gap: var(--space-4);
  }
  .step {
    border-top: var(--border-width) solid var(--border-dim);
    padding-top: var(--space-3);
  }
  .step:first-child { border-top: 0; padding-top: 0; }

  .step-head {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    width: 100%;
    background: transparent;
    border: 0;
    padding: 0;
    color: var(--text);
    cursor: pointer;
    font: inherit;
    text-align: left;
    height: auto;
  }
  .step-head.static { cursor: default; }
  .step-head:disabled { cursor: default; }

  .icon { font-family: var(--font-pixel); width: 2.5rem; display: inline-block; }
  .step.done  .icon { color: var(--success); }
  .step.open  .icon { color: var(--text); }
  .step.locked .icon { color: var(--text-muted); }

  .step.done .title { color: var(--text-muted); text-decoration: line-through; }
  .step.locked .title { color: var(--text-muted); }

  .lock-note { margin-left: var(--space-2); }

  .desc {
    margin: var(--space-1) 0 0 calc(2.5rem + var(--space-2));
    color: var(--text-dim);
  }
  .expand {
    margin: var(--space-3) 0 0 calc(2.5rem + var(--space-2));
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    max-width: 28rem;
  }
  .go {
    margin: var(--space-3) 0 0 calc(2.5rem + var(--space-2));
    text-decoration: none;
  }
</style>
