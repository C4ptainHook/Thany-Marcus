<script lang="ts">
  import { onMount } from 'svelte';
  import { enableInit, enableVerify, disable, type TotpEnableInit } from '$lib/totpClient';
  import { setPassphrase } from '$lib/stepUpClient';
  import { apiFetch } from '$lib/http';

  type DoConnection = { connected: false } | { connected: true; status: string; expiresAt: string };

  type Phase = 'loading' | 'idle' | 'enabling' | 'showing-codes' | 'disabling';

  let phase = $state<Phase>('loading');
  let totpState = $state<string>('');
  let passphraseSet = $state(false);
  let init = $state<TotpEnableInit | null>(null);
  let enableCode = $state('');
  let currentCode = $state('');
  let isReset = $state(false);
  let disableCode = $state('');
  let newPassphrase = $state('');
  let confirmPassphrase = $state('');
  let passphraseError = $state<string | null>(null);
  let passphraseBusy = $state(false);
  let backupCodes = $state<string[]>([]);
  let error = $state<string | null>(null);
  let doConnection = $state<DoConnection | null>(null);
  let doDisconnectBusy = $state(false);

  async function refreshDoConnection() {
    try {
      const r = await fetch('/api/clouds/connections/digitalocean');
      if (!r.ok) { doConnection = { connected: false }; return; }
      doConnection = await r.json();
    } catch {
      doConnection = { connected: false };
    }
  }

  async function disconnectDo() {
    if (!confirm('Disconnect DigitalOcean? Provisioning will require re-authorization.')) return;
    doDisconnectBusy = true;
    try {
      const r = await apiFetch('/api/clouds/connections/digitalocean', { method: 'DELETE' });
      if (r.ok || r.status === 204) await refreshDoConnection();
    } finally {
      doDisconnectBusy = false;
    }
  }

  async function refreshMe() {
    const r = await fetch('/api/auth/me');
    if (!r.ok) {
      window.location.href = '/api/auth/signin';
      return;
    }
    const me = await r.json();
    totpState = me.totp;
    passphraseSet = me.passphraseSet === true;
    phase = 'idle';
  }

  async function submitPassphrase(e: SubmitEvent) {
    e.preventDefault();
    passphraseError = null;
    if (newPassphrase !== confirmPassphrase) {
      passphraseError = 'Passphrases do not match.';
      return;
    }
    if (newPassphrase.length < 12) {
      passphraseError = 'Passphrase must be at least 12 characters.';
      return;
    }
    passphraseBusy = true;
    try {
      const res = await setPassphrase(newPassphrase);
      if (res.status === 204) {
        newPassphrase = '';
        confirmPassphrase = '';
        passphraseSet = true;
      } else if (res.status === 409) {
        passphraseError = 'Passphrase is already set.';
        passphraseSet = true;
      } else {
        passphraseError = `Failed (status ${res.status}).`;
      }
    } finally {
      passphraseBusy = false;
    }
  }

  onMount(async () => { await refreshMe(); await refreshDoConnection(); });

  async function startEnable() {
    error = null;
    isReset = totpState === 'verified' || totpState === 'not-verified';
    if (isReset) {
      if (!confirm('TOTP is already enabled. Re-enabling will replace your secret and invalidate previous backup codes. Continue?')) {
        return;
      }
    }
    try {
      init = await enableInit();
      phase = 'enabling';
    } catch {
      error = 'Failed to start TOTP enable flow.';
    }
  }

  async function submitEnable(e: SubmitEvent) {
    e.preventDefault();
    if (!init) return;
    if (isReset && !currentCode.trim()) return;
    error = null;
    try {
      const result = await enableVerify(
        init.secret,
        enableCode.trim(),
        isReset ? currentCode.trim() : undefined,
      );
      backupCodes = result.backupCodes;
      enableCode = '';
      currentCode = '';
      init = null;
      isReset = false;
      phase = 'showing-codes';
    } catch (err) {
      const status = (err as { status?: number } | null)?.status;
      error = status === 401 ? 'Invalid current code.' : 'Invalid code.';
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
    } catch {
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
  <h1>Settings</h1>

  {#if phase === 'loading'}
    <p class="muted">Loading…</p>

  {:else if phase === 'showing-codes'}
    <section class="card">
      <h2>Backup codes</h2>
      <p>Save these codes now — they will not be shown again. Each can be used once if you lose your authenticator.</p>
      <ul class="codes">
        {#each backupCodes as code}
          <li>{code}</li>
        {/each}
      </ul>
      <div class="actions">
        <button class="btn-secondary" type="button" onclick={copyAllCodes}>Copy all</button>
        <button class="btn-primary" type="button" onclick={dismissCodes}>I&rsquo;ve saved them</button>
      </div>
    </section>

  {:else if phase === 'enabling' && init}
    <section class="card">
      <h2>{isReset ? 'Reset TOTP' : 'Enable TOTP'}</h2>
      <p>Scan this code with your authenticator app, then enter the 6-digit code below.</p>
      <img class="qr" src={init.qrPngDataUri} alt="TOTP QR code" width="240" height="240" />
      <details>
        <summary>Or enter the secret manually</summary>
        <code>{init.secret}</code>
      </details>
      <form onsubmit={submitEnable}>
        {#if isReset}
          <label>
            Current code (from your existing authenticator)
            <input type="text" autocomplete="one-time-code" bind:value={currentCode} required />
          </label>
        {/if}
        <label>
          {isReset ? 'New code (from the QR above)' : 'Code'}
          <input type="text" autocomplete="one-time-code" bind:value={enableCode} required />
        </label>
        {#if error}<p class="error">{error}</p>{/if}
        <div class="actions">
          <button class="btn-primary" type="submit" disabled={!enableCode.trim() || (isReset && !currentCode.trim())}>
            {isReset ? 'Verify and reset' : 'Verify and enable'}
          </button>
        </div>
      </form>
    </section>

  {:else if phase === 'disabling'}
    <section class="card">
      <h2>Disable TOTP</h2>
      <p>Enter a current 6-digit code from your authenticator to disable TOTP. Backup codes are not accepted here.</p>
      <form onsubmit={submitDisable}>
        <label>
          Code
          <input type="text" autocomplete="one-time-code" bind:value={disableCode} required />
        </label>
        {#if error}<p class="error">{error}</p>{/if}
        <div class="actions">
          <button class="btn-secondary" type="button" onclick={() => { phase = 'idle'; disableCode = ''; error = null; }}>Cancel</button>
          <button class="btn-danger" type="submit" disabled={!disableCode.trim()}>Disable</button>
        </div>
      </form>
    </section>

  {:else}
    <section class="card">
      <h2 class="card-title">TWO-FACTOR</h2>
      <p>TOTP state: <strong class={totpState === 'verified' ? 'success' : ''}>{totpState}</strong></p>
      <div class="actions">
        {#if totpState === 'verified' || totpState === 'not-verified'}
          <button class="btn-secondary" type="button" onclick={() => { phase = 'disabling'; }}>Disable</button>
          <button class="btn-secondary" type="button" onclick={startEnable}>Reset</button>
        {:else}
          <button class="btn-primary" type="button" onclick={startEnable}>Enable TOTP</button>
        {/if}
      </div>
    </section>

    <section class="card">
      <h2 class="card-title">PASSPHRASE</h2>
      {#if !passphraseSet}
        <p>The passphrase protects destructive infrastructure operations. You will be asked for it before each destroy or rotate action.</p>
        <form onsubmit={submitPassphrase}>
          <label>
            Passphrase
            <input type="password" autocomplete="new-password" bind:value={newPassphrase} required />
          </label>
          <label>
            Confirm
            <input type="password" autocomplete="new-password" bind:value={confirmPassphrase} required />
          </label>
          {#if passphraseError}<p class="error">{passphraseError}</p>{/if}
          <div class="actions">
            <button class="btn-primary" type="submit" disabled={passphraseBusy || !newPassphrase || !confirmPassphrase}>
              {passphraseBusy ? 'Setting…' : 'Set passphrase'}
            </button>
          </div>
        </form>
      {:else}
        <p>Passphrase: <strong class="success">set ✓</strong></p>
      {/if}
    </section>

    {#if passphraseSet}
      <section class="card">
        <h2 class="card-title">CONNECTIONS</h2>
        <p>
          DigitalOcean:
          {#if doConnection === null}
            <span class="muted">loading…</span>
          {:else if !doConnection.connected}
            <strong>not connected</strong>
            <a class="btn btn-primary" href="/oauth/digitalocean/start?return_to=/settings" style="margin-left: var(--space-2);">Connect DigitalOcean</a>
          {:else if doConnection.status === 'needs_reauth'}
            <strong class="error">needs re-authorization</strong>
            <a class="btn btn-primary" href="/oauth/digitalocean/start?return_to=/settings" style="margin-left: var(--space-2);">Reconnect</a>
          {:else}
            <strong class="success">connected</strong>
            <button class="btn-secondary" type="button" onclick={disconnectDo} disabled={doDisconnectBusy} style="margin-left: var(--space-2);">
              {doDisconnectBusy ? 'Disconnecting…' : 'Disconnect'}
            </button>
          {/if}
        </p>
      </section>
    {/if}

  {/if}
</main>

<style>
  .codes {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: var(--space-2);
    list-style: none;
    padding: 0;
    font-family: var(--font-pixel);
    font-size: var(--text-base);
    margin: var(--space-3) 0;
  }
  .codes li {
    padding: var(--space-2);
    background: var(--surface-2);
    text-align: center;
  }
  .actions {
    display: flex;
    gap: var(--space-2);
    margin-top: var(--space-3);
  }
  label {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    margin: var(--space-3) 0;
  }
  .qr {
    display: block;
    background: white;
    padding: var(--space-2);
    margin-bottom: var(--space-3);
  }
  details summary { cursor: pointer; }
  details code {
    display: block;
    font-family: var(--font-pixel);
    padding: var(--space-2);
    background: var(--surface-2);
    margin-top: var(--space-2);
  }
</style>
