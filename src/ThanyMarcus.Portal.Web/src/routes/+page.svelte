<script lang="ts">
  import { onMount } from 'svelte';
  import { fetchCaptchaState } from '$lib/turnstileClient';
  import TurnstileWidget from '$lib/TurnstileWidget.svelte';
  import type { MeResponse } from '$lib/types/auth';

  let { data }: { data: { me: MeResponse | null } } = $props();

  let captchaRequired = $state(false);
  let siteKey = $state('');
  let turnstileToken = $state('');

  onMount(async () => {
    if (data.me) return;
    const state = await fetchCaptchaState('signin');
    captchaRequired = state.required;
    siteKey = state.siteKey;
  });

  function onCaptchaToken(token: string) {
    turnstileToken = token;
  }

  function signInHref() {
    return captchaRequired && turnstileToken
      ? `/api/auth/signin?turnstile=${encodeURIComponent(turnstileToken)}`
      : '/api/auth/signin';
  }
</script>

<main>
  <h1>Thany-Marcus Portal</h1>
  {#if data.me}
    <p>Signed in as <strong>{data.me.name}</strong> ({data.me.email})</p>
    {#if data.me.profilePictureUrl}
      <img src={data.me.profilePictureUrl} alt="" width="64" height="64" />
    {/if}
    <p class="muted">TOTP state: {data.me.totp}</p>
    <p>
      <a class="primary-button" href="/clouds/new">Create cloud</a>
    </p>
    <p>
      <a href="/settings/security">Security settings</a>
    </p>
    <form method="post" action="/api/auth/signout">
      <button type="submit">Sign out</button>
    </form>
  {:else}
    {#if captchaRequired && siteKey}
      <TurnstileWidget {siteKey} onToken={onCaptchaToken} />
    {/if}
    {#if captchaRequired && !turnstileToken}
      <p><a aria-disabled="true" class="disabled-link">Sign in with Google</a></p>
    {:else}
      <p><a href={signInHref()}>Sign in with Google</a></p>
    {/if}
  {/if}
</main>

<style>
  h1 {
    font-size: 2rem;
    margin-bottom: var(--space-2);
  }
  .disabled-link {
    color: var(--color-disabled);
    cursor: not-allowed;
  }
  .primary-button {
    display: inline-block;
    padding: var(--space-2) var(--space-3);
    background: var(--color-text);
    color: var(--color-bg);
    border-radius: var(--radius);
    text-decoration: none;
  }
</style>
