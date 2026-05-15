<script lang="ts">
  import { onMount } from 'svelte';
  import { enableInit, enableVerify, disable, type TotpEnableInit } from '$lib/totpClient';

  type Phase = 'loading' | 'idle' | 'enabling' | 'showing-codes' | 'disabling';

  let phase = $state<Phase>('loading');
  let totpState = $state<string>('');
  let init = $state<TotpEnableInit | null>(null);
  let enableCode = $state('');
  let disableCode = $state('');
  let backupCodes = $state<string[]>([]);
  let error = $state<string | null>(null);

  async function refreshMe() {
    const r = await fetch('/api/auth/me');
    if (!r.ok) {
      window.location.href = '/api/auth/signin';
      return;
    }
    const me = await r.json();
    totpState = me.totp;
    phase = 'idle';
  }

  onMount(refreshMe);

  async function startEnable() {
    error = null;
    if (totpState === 'verified' || totpState === 'not-verified') {
      if (!confirm('TOTP is already enabled. Re-enabling will replace your secret and invalidate previous backup codes. Continue?')) {
        return;
      }
    }
    try {
      init = await enableInit();
      phase = 'enabling';
    } catch (err) {
      error = 'Failed to start TOTP enable flow.';
    }
  }

  async function submitEnable(e: SubmitEvent) {
    e.preventDefault();
    if (!init) return;
    error = null;
    try {
      const result = await enableVerify(init.secret, enableCode.trim());
      backupCodes = result.backupCodes;
      enableCode = '';
      init = null;
      phase = 'showing-codes';
    } catch (err) {
      error = 'Invalid code.';
    }
  }

  async function submitDisable(e: SubmitEvent) {
    e.preventDefault();
    error = null;
    try {
      await disable(disableCode.trim());
      disableCode = '';
      phase = 'loading';
      await refreshMe();
    } catch (err) {
      error = 'Invalid code.';
    }
  }

  async function copyAllCodes() {
    await navigator.clipboard.writeText(backupCodes.join('\n'));
  }

  async function dismissCodes() {
    backupCodes = [];
    phase = 'loading';
    await refreshMe();
  }
</script>

<main>
  <h1>Security</h1>

  {#if phase === 'loading'}
    <p>Loading…</p>
  {:else if phase === 'showing-codes'}
    <section>
      <h2>Backup codes</h2>
      <p>Save these codes now — they will not be shown again. Each can be used once if you lose your authenticator.</p>
      <ul class="codes">
        {#each backupCodes as code}
          <li>{code}</li>
        {/each}
      </ul>
      <button type="button" onclick={copyAllCodes}>Copy all</button>
      <button type="button" onclick={dismissCodes}>I&rsquo;ve saved them</button>
    </section>
  {:else if phase === 'enabling' && init}
    <section>
      <h2>Enable TOTP</h2>
      <p>Scan this code with your authenticator app, then enter the 6-digit code below.</p>
      <img src={init.qrPngDataUri} alt="TOTP QR code" width="240" height="240" />
      <details>
        <summary>Or enter the secret manually</summary>
        <code>{init.secret}</code>
      </details>
      <form onsubmit={submitEnable}>
        <label>
          Code
          <input type="text" autocomplete="one-time-code" bind:value={enableCode} required />
        </label>
        {#if error}<p class="error">{error}</p>{/if}
        <button type="submit" disabled={!enableCode.trim()}>Verify and enable</button>
      </form>
    </section>
  {:else if phase === 'disabling'}
    <section>
      <h2>Disable TOTP</h2>
      <p>Enter a current 6-digit code from your authenticator to disable TOTP. Backup codes are not accepted here.</p>
      <form onsubmit={submitDisable}>
        <label>
          Code
          <input type="text" autocomplete="one-time-code" bind:value={disableCode} required />
        </label>
        {#if error}<p class="error">{error}</p>{/if}
        <button type="submit" disabled={!disableCode.trim()}>Disable</button>
        <button type="button" onclick={() => { phase = 'idle'; disableCode = ''; error = null; }}>Cancel</button>
      </form>
    </section>
  {:else}
    <section>
      <p>TOTP state: <strong>{totpState}</strong></p>
      {#if totpState === 'verified' || totpState === 'not-verified'}
        <button type="button" onclick={() => { phase = 'disabling'; }}>Disable TOTP</button>
        <button type="button" onclick={startEnable}>Reset TOTP</button>
      {:else}
        <button type="button" onclick={startEnable}>Enable TOTP</button>
      {/if}
    </section>
  {/if}
</main>

<style>
  main {
    font-family: system-ui, -apple-system, sans-serif;
    max-width: 36rem;
    margin: 4rem auto;
    padding: 0 1rem;
  }
  .codes {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.5rem;
    list-style: none;
    padding: 0;
    font-family: ui-monospace, monospace;
    font-size: 1.1rem;
  }
  .codes li {
    padding: 0.5rem;
    background: #f5f5f5;
    border-radius: 4px;
    text-align: center;
  }
  .error {
    color: #b00020;
  }
  button {
    padding: 0.5rem 1rem;
    margin-right: 0.5rem;
  }
  input {
    display: block;
    padding: 0.5rem;
    font-size: 1.1rem;
  }
</style>
