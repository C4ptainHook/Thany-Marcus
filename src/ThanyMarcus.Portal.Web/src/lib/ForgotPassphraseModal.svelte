<script lang="ts">
  import { forgotPassphraseOpen, closeForgotPassphrase } from './forgotPassphraseClient';
  import { emergencyKitStatus, resetViaTotp, resetViaKit } from './recoveryClient';
  import EmergencyKitModal from './EmergencyKitModal.svelte';
  import type { EmergencyKit } from './types/recovery';

  type Step = 'loading' | 'options' | 'totp' | 'kit' | 'show-new-kit' | 'done';

  let step = $state<Step>('loading');
  let totpRecoveryAvailable = $state(false);
  let email = $state('');
  let newKit = $state<EmergencyKit | null>(null);
  let error = $state<string | null>(null);
  let busy = $state(false);

  let totpCode = $state('');
  let recoveryString = $state('');
  let pp = $state('');
  let pp2 = $state('');

  let opened = $state(false);

  $effect(() => {
    if ($forgotPassphraseOpen && !opened) {
      opened = true;
      void load();
    } else if (!$forgotPassphraseOpen && opened) {
      opened = false;
    }
  });

  async function load() {
    reset();
    step = 'loading';
    try {
      const [status, me] = await Promise.all([
        emergencyKitStatus(),
        fetch('/api/auth/me').then(r => (r.ok ? r.json() : null)),
      ]);
      totpRecoveryAvailable = status.totpRecoveryAvailable;
      email = me?.email ?? '';
      step = 'options';
    } catch {
      // Even if status lookup fails, the Emergency Kit path is always available.
      totpRecoveryAvailable = false;
      step = 'options';
    }
  }

  function reset() {
    error = null;
    busy = false;
    totpCode = '';
    recoveryString = '';
    pp = '';
    pp2 = '';
    newKit = null;
  }

  function close() {
    closeForgotPassphrase();
    step = 'loading';
  }

  function validatePassphrase(): string | null {
    if (pp !== pp2) return 'Passphrases do not match.';
    if (pp.length < 8) return 'Passphrase must be at least 8 characters.';
    return null;
  }

  function explainBadRequest(reason: unknown): string {
    return reason === 'too_common'
      ? 'That passphrase is too common — pick something less guessable.'
      : 'Passphrase must be at least 8 characters.';
  }

  async function submitTotp(e: SubmitEvent) {
    e.preventDefault();
    error = null;
    const v = validatePassphrase();
    if (v) { error = v; return; }
    busy = true;
    try {
      const r = await resetViaTotp(totpCode.trim(), pp);
      if (r.status === 204) {
        step = 'done';
      } else if (r.status === 401) {
        error = 'That 2FA code is not valid. Try a fresh code from your app.';
      } else if (r.status === 400) {
        const body = await r.json().catch(() => null);
        error = explainBadRequest(body?.reason);
      } else if (r.status === 409) {
        error = '2FA recovery is not available on this account. Use your Emergency Kit instead.';
      } else {
        error = `Reset failed (status ${r.status}).`;
      }
    } finally {
      busy = false;
    }
  }

  async function submitKit(e: SubmitEvent) {
    e.preventDefault();
    error = null;
    const v = validatePassphrase();
    if (v) { error = v; return; }
    busy = true;
    try {
      const result = await resetViaKit(recoveryString.trim(), pp);
      if (result.ok) {
        newKit = result.kit;
        step = 'show-new-kit';
      } else if (result.status === 401) {
        error = 'That recovery string was not recognized. Check the words on your kit.';
      } else if (result.status === 400) {
        error = 'Passphrase must be at least 8 characters and not a common password.';
      } else {
        error = `Reset failed (status ${result.status}).`;
      }
    } finally {
      busy = false;
    }
  }

  function finish() {
    close();
    window.location.reload();
  }
</script>

{#if $forgotPassphraseOpen}
  {#if step === 'show-new-kit' && newKit}
    <EmergencyKitModal
      kit={newKit}
      {email}
      heading="Save your new Emergency Kit"
      intro="Your old Emergency Kit has been used and is no longer valid. Save your new one before continuing."
      onSaved={finish}
    />
  {:else}
    <div class="backdrop" role="presentation" onclick={close}></div>
    <div class="modal" role="dialog" aria-modal="true" aria-labelledby="forgot-title">
      {#if step === 'loading'}
        <p class="muted">Loading…</p>

      {:else if step === 'options'}
        <h2 id="forgot-title">How would you like to recover access?</h2>
        {#if totpRecoveryAvailable}
          <section class="option">
            <h3>Use my 2FA code</h3>
            <p class="muted">Type a fresh 6-digit code from your authenticator app.</p>
            <button class="btn btn-primary" type="button" onclick={() => { error = null; step = 'totp'; }}>
              Use 2FA →
            </button>
          </section>
        {/if}
        <section class="option">
          <h3>Use my Emergency Kit</h3>
          <p class="muted">Type the 8-word recovery string from your kit.</p>
          <button class="btn btn-secondary" type="button" onclick={() => { error = null; step = 'kit'; }}>
            Use Emergency Kit →
          </button>
        </section>
        <button class="btn btn-ghost" type="button" onclick={close}>Cancel</button>

      {:else if step === 'totp'}
        <h2 id="forgot-title">Reset with your 2FA code</h2>
        <form onsubmit={submitTotp}>
          <label>
            6-digit code
            <input type="text" inputmode="numeric" autocomplete="one-time-code" bind:value={totpCode} required />
          </label>
          <label>
            New passphrase
            <input type="password" autocomplete="new-password" bind:value={pp} required />
          </label>
          <label>
            Confirm new passphrase
            <input type="password" autocomplete="new-password" bind:value={pp2} required />
          </label>
          {#if error}<p class="error">{error}</p>{/if}
          <div class="actions">
            <button class="btn btn-ghost" type="button" onclick={() => { step = 'options'; error = null; }}>Back</button>
            <button class="btn btn-primary" type="submit" disabled={busy || !totpCode || !pp || !pp2}>
              {busy ? 'Resetting…' : 'Reset passphrase'}
            </button>
          </div>
        </form>

      {:else if step === 'kit'}
        <h2 id="forgot-title">Reset with your Emergency Kit</h2>
        <form onsubmit={submitKit}>
          <label>
            Recovery string (8 words)
            <input type="text" autocomplete="off" autocapitalize="none" bind:value={recoveryString} required />
          </label>
          <label>
            New passphrase
            <input type="password" autocomplete="new-password" bind:value={pp} required />
          </label>
          <label>
            Confirm new passphrase
            <input type="password" autocomplete="new-password" bind:value={pp2} required />
          </label>
          {#if error}<p class="error">{error}</p>{/if}
          <div class="actions">
            <button class="btn btn-ghost" type="button" onclick={() => { step = 'options'; error = null; }}>Back</button>
            <button class="btn btn-primary" type="submit" disabled={busy || !recoveryString || !pp || !pp2}>
              {busy ? 'Resetting…' : 'Reset passphrase'}
            </button>
          </div>
        </form>

      {:else if step === 'done'}
        <h2 id="forgot-title">Passphrase reset complete</h2>
        <p>Your new passphrase is ready. Your Emergency Kit is unchanged.</p>
        <div class="actions">
          <button class="btn btn-primary" type="button" onclick={finish}>Done</button>
        </div>
      {/if}
    </div>
  {/if}
{/if}

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: var(--color-backdrop);
    z-index: 1000;
  }
  .modal {
    position: fixed;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    background: var(--surface);
    color: var(--text);
    padding: var(--space-5);
    border: var(--border-width) solid var(--border);
    width: min(32rem, calc(100vw - var(--space-5)));
    max-height: calc(100vh - var(--space-5));
    overflow-y: auto;
    z-index: 1001;
  }
  .option {
    border: var(--border-width) solid var(--border);
    padding: var(--space-3);
    margin: var(--space-3) 0;
  }
  .option h3 { margin: 0 0 var(--space-1); }
  .option p { margin: 0 0 var(--space-3); }
  label {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    margin: var(--space-3) 0;
  }
  .modal :global(input) { width: 100%; }
  .actions {
    display: flex;
    gap: var(--space-2);
    justify-content: flex-end;
    margin-top: var(--space-4);
  }
  .error { color: var(--error); }
</style>
