<script lang="ts">
  import { onMount } from 'svelte';
  import { challenge } from '$lib/totpClient';
  import { fetchCaptchaState } from '$lib/turnstileClient';
  import TurnstileWidget from '$lib/TurnstileWidget.svelte';

  let code = $state('');
  let error = $state<string | null>(null);
  let submitting = $state(false);
  let captchaRequired = $state(false);
  let siteKey = $state('');
  let turnstileToken = $state('');

  onMount(async () => {
    const r = await fetch('/api/auth/me');
    if (!r.ok) {
      window.location.href = '/api/auth/signin';
      return;
    }
    const me = await r.json();
    if (me.totp === 'verified' || me.totp === 'not-enabled') {
      window.location.href = '/';
      return;
    }
    const state = await fetchCaptchaState('totp');
    captchaRequired = state.required;
    siteKey = state.siteKey;
  });

  async function onSubmit(e: SubmitEvent) {
    e.preventDefault();
    submitting = true;
    error = null;
    try {
      const res = await challenge(code.trim(), turnstileToken || undefined);
      if (res.ok) {
        window.location.href = '/';
        return;
      }
      if (res.status === 428) {
        const body = await res.json().catch(() => null);
        captchaRequired = true;
        if (body?.site_key) siteKey = body.site_key;
        turnstileToken = '';
        error = 'Please complete the verification challenge.';
      } else {
        error = 'Invalid code. Try again or use a backup code.';
      }
    } finally {
      submitting = false;
    }
  }

  function onCaptchaToken(token: string) {
    turnstileToken = token;
  }
</script>

<main>
  <h1>Two-factor sign-in</h1>
  <p>Enter the 6-digit code from your authenticator app, or one of your backup codes.</p>
  <form onsubmit={onSubmit}>
    <label>
      Code
      <input
        type="text"
        autocomplete="one-time-code"
        inputmode="text"
        bind:value={code}
        required />
    </label>
    {#if captchaRequired && siteKey}
      <TurnstileWidget {siteKey} onToken={onCaptchaToken} />
    {/if}
    {#if error}<p class="error">{error}</p>{/if}
    <button
      type="submit"
      disabled={submitting || !code.trim() || (captchaRequired && !turnstileToken)}>
      {submitting ? 'Verifying…' : 'Verify'}
    </button>
  </form>
</main>

<style>
  main {
    max-width: 28rem;
  }
  label {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    margin: var(--space-4) 0;
  }
  input {
    display: block;
    width: 100%;
    letter-spacing: 0.2em;
  }
</style>
