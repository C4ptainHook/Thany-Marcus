<script lang="ts">
  import { onMount } from 'svelte';
  import { challenge } from '$lib/totpClient';

  let code = $state('');
  let error = $state<string | null>(null);
  let submitting = $state(false);

  onMount(async () => {
    const r = await fetch('/api/auth/me');
    if (!r.ok) {
      window.location.href = '/api/auth/signin';
      return;
    }
    const me = await r.json();
    if (me.totp === 'verified' || me.totp === 'not-enabled') {
      window.location.href = '/';
    }
  });

  async function onSubmit(e: SubmitEvent) {
    e.preventDefault();
    submitting = true;
    error = null;
    try {
      await challenge(code.trim());
      window.location.href = '/';
    } catch (err) {
      error = 'Invalid code. Try again or use a backup code.';
      submitting = false;
    }
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
    {#if error}<p class="error">{error}</p>{/if}
    <button type="submit" disabled={submitting || !code.trim()}>
      {submitting ? 'Verifying…' : 'Verify'}
    </button>
  </form>
</main>

<style>
  main {
    font-family: system-ui, -apple-system, sans-serif;
    max-width: 28rem;
    margin: 4rem auto;
    padding: 0 1rem;
  }
  label {
    display: block;
    margin: 1rem 0;
  }
  input {
    display: block;
    width: 100%;
    padding: 0.5rem;
    font-size: 1.25rem;
    letter-spacing: 0.2em;
  }
  .error {
    color: #b00020;
  }
  button {
    padding: 0.5rem 1.5rem;
    font-size: 1rem;
  }
</style>
