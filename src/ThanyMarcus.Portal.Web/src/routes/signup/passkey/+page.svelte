<script lang="ts">
  import { onMount } from 'svelte';
  import { goto, invalidateAll } from '$app/navigation';
  import {
    signupWithPasskey, isPasskeySupported, isPasskeyCancellation,
    UsernameTakenError, SignupRejectedError,
  } from '$lib/passkey';
  import { fetchCaptchaState } from '$lib/turnstileClient';
  import TurnstileWidget from '$lib/TurnstileWidget.svelte';

  const passkeySupported = isPasskeySupported();

  let username = $state('');
  let acknowledged = $state(false);
  let captchaRequired = $state(false);
  let siteKey = $state('');
  let turnstileToken = $state('');
  let busy = $state(false);
  let error = $state<string | null>(null);

  onMount(async () => {
    if (!passkeySupported) return;
    const state = await fetchCaptchaState('signup');
    captchaRequired = state.required;
    siteKey = state.siteKey;
  });

  const trimmed = $derived(username.trim());
  const usernameHint = $derived.by(() => {
    if (trimmed.length === 0) return null;
    if (trimmed.length < 3 || trimmed.length > 32) return 'Username must be 3–32 characters.';
    if (!/^[a-zA-Z0-9_-]+$/.test(trimmed)) return 'Use only letters, numbers, _ and -.';
    if (trimmed[0] === '_' || trimmed[0] === '-') return 'Cannot start with _ or -.';
    return null;
  });

  const canSubmit = $derived(
    !busy
      && trimmed.length >= 3
      && usernameHint === null
      && acknowledged
      && (!captchaRequired || turnstileToken.length > 0),
  );

  const messages: Record<string, string> = {
    username_length: 'Username must be 3–32 characters.',
    username_chars: 'Use only letters, numbers, _ and -.',
    username_prefix: 'Username cannot start with _ or -.',
    username_reserved: 'That username is reserved — pick another.',
    username_required: 'Enter a username.',
    acknowledgement_required: 'Please acknowledge the recovery notice.',
    captcha_invalid: 'Verification failed — please try the challenge again.',
  };

  async function onSubmit(e: SubmitEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    error = null;
    busy = true;
    try {
      await signupWithPasskey({
        username: trimmed,
        acknowledged,
        turnstileToken: turnstileToken || undefined,
      });
      await invalidateAll();
      await goto('/');
    } catch (err) {
      if (isPasskeyCancellation(err)) {
        // User dismissed the native prompt — leave the form intact, no error.
      } else if (err instanceof UsernameTakenError) {
        error = 'That username is already taken.';
      } else if (err instanceof SignupRejectedError) {
        error = messages[err.code] ?? 'Could not create your account.';
        turnstileToken = '';
      } else {
        error = 'Could not create your account. Please try again.';
        turnstileToken = '';
      }
    } finally {
      busy = false;
    }
  }
</script>

<main>
  <h1>Sign up with a passkey</h1>

  {#if !passkeySupported}
    <p class="muted">
      This browser doesn&rsquo;t support passkeys. <a href="/">Sign in with Google</a> instead.
    </p>
  {:else}
    <p class="muted">
      No email, no password. You pick a username and your device holds the key — Face ID, Touch ID,
      Windows Hello, or a security key.
    </p>

    <form onsubmit={onSubmit}>
      <label>
        Username <span class="muted">(3–32 chars · letters, numbers, _ and -)</span>
        <input
          type="text"
          autocomplete="username"
          autocapitalize="none"
          autocorrect="off"
          spellcheck="false"
          bind:value={username}
          required />
      </label>
      {#if usernameHint}<p class="hint">{usernameHint}</p>{/if}

      <label class="ack">
        <input type="checkbox" bind:checked={acknowledged} />
        <span>
          I understand that Thany-Marcus has <strong>no email on file</strong> for me. If I lose my
          passkey <strong>and</strong> my Emergency Kit, my account cannot be recovered.
        </span>
      </label>

      {#if captchaRequired && siteKey}
        <TurnstileWidget {siteKey} onToken={(t) => (turnstileToken = t)} />
      {/if}

      {#if error}<p class="error">{error}</p>{/if}

      <button class="btn btn-primary" type="submit" disabled={!canSubmit}>
        {busy ? 'Waiting for passkey…' : 'Create account with passkey'}
      </button>
    </form>

    <p class="muted alt">
      Already have an account? <a href="/">Sign in</a>.
    </p>
  {/if}
</main>

<style>
  main { max-width: 32rem; }
  form {
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    margin-top: var(--space-4);
  }
  label {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
  }
  label.ack {
    flex-direction: row;
    align-items: flex-start;
    gap: var(--space-2);
    color: var(--text-dim);
  }
  label.ack input { margin-top: 0.25rem; }
  input[type='text'] { width: 100%; }
  .hint { color: var(--text-muted); margin: 0; font-size: var(--text-sm); }
  .alt { margin-top: var(--space-4); }
</style>
