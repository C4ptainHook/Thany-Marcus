<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { setPassphrase } from './stepUpClient';
  import { generateEmergencyKit, emergencyKitStatus, unlockPassphrase } from './recoveryClient';
  import { listPasskeys, registerPasskey, isPasskeySupported, isPasskeyCancellation } from './passkey';
  import EmergencyKitModal from './EmergencyKitModal.svelte';
  import type { MeResponse } from './types/auth';
  import type { EmergencyKit } from './types/recovery';

  let { me, onPassphraseSet }: { me: MeResponse; onPassphraseSet: () => void } = $props();

  let passphraseSet = $state(untrack(() => me.passphraseSet === true));
  let kitSaved = $state(false);
  let kit = $state<EmergencyKit | null>(null);
  const secured = $derived(passphraseSet && kitSaved);
  const totpConfigured = $derived(me.totp === 'verified' || me.totp === 'not-verified');

  let expanded = $state<1 | null>(untrack(() => me.passphraseSet === true) ? null : 1);

  const passkeySupported = isPasskeySupported();
  let passkeyCount = $state(0);
  let passkeyBusy = $state(false);
  let passkeyError = $state<string | null>(null);

  onMount(async () => {
    try {
      const status = await emergencyKitStatus();
      if (status.generatedAt) kitSaved = true;
    } catch { /* fail soft — treated as "needs a kit" */ }
    if (!passkeySupported) return;
    try { passkeyCount = (await listPasskeys()).length; } catch { /* fail soft */ }
  });

  let pp = $state('');
  let pp2 = $state('');
  let ppError = $state<string | null>(null);
  let ppBusy = $state(false);

  async function submitPassphrase(e: SubmitEvent) {
    e.preventDefault();
    ppError = null;
    if (pp !== pp2)     { ppError = 'Passphrases do not match.'; return; }
    if (pp.length < 8)  { ppError = 'Passphrase must be at least 8 characters.'; return; }
    ppBusy = true;
    try {
      const r = await setPassphrase(pp);
      if (r.status === 204) {
        passphraseSet = true;
        await unlockPassphrase(pp);
        kit = await generateEmergencyKit();   // shows the mandatory kit modal
      } else if (r.status === 400) {
        const body = await r.json().catch(() => null);
        ppError = body?.reason === 'too_common'
          ? 'That passphrase is too common — pick something less guessable.'
          : 'Passphrase must be at least 8 characters.';
      } else if (r.status === 409) {
        passphraseSet = true;
        expanded = null;
      } else {
        ppError = `Failed (status ${r.status}).`;
      }
    } catch {
      ppError = 'Could not set up your account. Please try again.';
    } finally {
      ppBusy = false;
      pp = ''; pp2 = '';
    }
  }

  let kitBusy = $state(false);
  let kitError = $state<string | null>(null);

  async function saveKitForExistingPassphrase() {
    kitError = null;
    kitBusy = true;
    try {
      kit = await generateEmergencyKit();   // apiFetch prompts step-up if the session is locked
    } catch {
      kitError = 'Could not generate your Emergency Kit. Please try again.';
    } finally {
      kitBusy = false;
    }
  }

  function onKitSaved() {
    kit = null;
    kitSaved = true;
    expanded = null;
    onPassphraseSet();
  }

  async function addPasskey() {
    passkeyError = null;
    passkeyBusy = true;
    try {
      await registerPasskey();
      passkeyCount = (await listPasskeys()).length;
    } catch (err) {
      if (!isPasskeyCancellation(err)) passkeyError = 'Could not register passkey.';
    } finally {
      passkeyBusy = false;
    }
  }
</script>

<section class="card">
  <h2 class="card-title">GET YOUR CLOUD RUNNING</h2>

  <ol class="steps">
    <li class={'step ' + (secured ? 'done' : 'open')}>
      <button
        class="step-head"
        type="button"
        disabled={secured}
        onclick={() => (expanded = expanded === 1 ? null : 1)}>
        <span class="icon success">{secured ? '[●]' : '[○]'}</span>
        <span class="title">1. Secure your account</span>
      </button>
      <p class="desc">
        Set a passphrase, then save your Emergency Kit — your way back if you ever forget it.
      </p>

      {#if expanded === 1 && !passphraseSet}
        <form class="expand" onsubmit={submitPassphrase}>
          <label>
            Passphrase <span class="muted">(8+ characters)</span>
            <input type="password" autocomplete="new-password" bind:value={pp} required />
          </label>
          <label>
            Confirm
            <input type="password" autocomplete="new-password" bind:value={pp2} required />
          </label>
          {#if ppError}<p class="error">{ppError}</p>{/if}
          <button class="btn-primary" type="submit" disabled={ppBusy || !pp || !pp2}>
            {ppBusy ? 'Setting up…' : 'Set passphrase'}
          </button>
        </form>
      {:else if passphraseSet && !kitSaved}
        <div class="expand">
          <p class="desc">Passphrase set. Now save your Emergency Kit to finish.</p>
          {#if kitError}<p class="error">{kitError}</p>{/if}
          <button class="btn btn-primary go" type="button" disabled={kitBusy} onclick={saveKitForExistingPassphrase}>
            {kitBusy ? 'Preparing…' : 'Save your Emergency Kit'}
          </button>
        </div>
      {/if}
    </li>

    <li class={'step ' + (secured ? 'open' : 'locked')}>
      <div class="step-head static">
        <span class="icon success">{secured ? '[○]' : '[—]'}</span>
        <span class="title">2. Provision your cloud</span>
        {#if !secured}<span class="muted lock-note">(locked)</span>{/if}
      </div>
      <p class="desc">
        {#if secured}
          Pick a region. Authorization happens inline if needed.
        {:else}
          Needs your account secured first.
        {/if}
      </p>
      {#if secured}
        <a class="btn btn-primary go" href="/clouds/new">Provision cloud</a>
      {/if}
    </li>

    <li class={'step ' + (totpConfigured ? 'done' : 'open')}>
      <div class="step-head static">
        <span class="icon success">{totpConfigured ? '[●]' : '[○]'}</span>
        <span class="title">3. Add 2-factor authentication <span class="muted">(optional)</span></span>
      </div>
      <p class="desc">
        {#if totpConfigured}
          Two-factor is on — and gives you a faster way to reset your passphrase.
        {:else}
          Optional. Adds a second sign-in factor and a faster passphrase-reset path than your Emergency Kit.
        {/if}
      </p>
      {#if !totpConfigured}
        <a class="btn btn-secondary go" href="/settings">Set up 2FA</a>
      {/if}
    </li>

    {#if passkeySupported}
      <li class={'step ' + (passkeyCount > 0 ? 'done' : 'open')}>
        <div class="step-head static">
          <span class="icon success">{passkeyCount > 0 ? '[●]' : '[○]'}</span>
          <span class="title">4. Register a passkey <span class="muted">(optional)</span></span>
        </div>
        <p class="desc">
          {#if passkeyCount > 0}
            Faster sign-in next time — Face ID, Touch ID, Windows Hello, or a security key.
          {:else}
            Want faster login next time? Sign in with a passkey instead of Google.
          {/if}
        </p>
        {#if passkeyError}<p class="error desc">{passkeyError}</p>{/if}
        {#if passkeyCount === 0}
          <button class="btn btn-secondary go" type="button" disabled={passkeyBusy} onclick={addPasskey}>
            {passkeyBusy ? 'Waiting for passkey…' : 'Register a passkey'}
          </button>
        {/if}
      </li>
    {/if}
  </ol>
</section>

{#if kit}
  <EmergencyKitModal {kit} email={me.email ?? me.username} onSaved={onKitSaved} />
{/if}

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
