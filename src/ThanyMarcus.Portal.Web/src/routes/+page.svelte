<script lang="ts">
  import { onMount } from 'svelte';
  import { fetchCaptchaState } from '$lib/turnstileClient';
  import TurnstileWidget from '$lib/TurnstileWidget.svelte';

  let { data }: {
    data: {
      me: {
        userId: string;
        email: string;
        name: string;
        profilePictureUrl: string | null;
        totp: string;
      } | null;
    };
  } = $props();

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
    <p>TOTP state: {data.me.totp}</p>
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
  main {
    font-family: system-ui, -apple-system, sans-serif;
    max-width: 40rem;
    margin: 4rem auto;
    padding: 0 1rem;
    color: #1a1a1a;
  }
  h1 {
    font-size: 2rem;
    margin-bottom: 0.5rem;
  }
  p {
    color: #555;
  }
  .disabled-link {
    color: #999;
    cursor: not-allowed;
  }
</style>
