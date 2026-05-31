<script lang="ts">
  import { onMount } from 'svelte';
  import { invalidateAll } from '$app/navigation';
  import { fetchCaptchaState } from '$lib/turnstileClient';
  import TurnstileWidget from '$lib/TurnstileWidget.svelte';
  import CloudIdentityCard from '$lib/CloudIdentityCard.svelte';
  import CostCard from '$lib/CostCard.svelte';
  import PluginTokenCard from '$lib/PluginTokenCard.svelte';
  import DangerZone from '$lib/DangerZone.svelte';
  import OnboardingChecklist from '$lib/OnboardingChecklist.svelte';
  import ProvisioningInFlight from '$lib/ProvisioningInFlight.svelte';
  import PluginTokenReveal from '$lib/PluginTokenReveal.svelte';
  import {
    isInFlight, isFailed, isTerminalEmpty,
    IN_FLIGHT_DESTROY_STATUSES,
    type CloudHealthz, type CloudStatusResponse,
  } from '$lib/types/cloud';
  import type { MeResponse } from '$lib/types/auth';

  let {
    data,
  }: { data: { me: MeResponse | null; cloud: CloudStatusResponse | null } } = $props();

  let captchaRequired = $state(false);
  let siteKey = $state('');
  let turnstileToken = $state('');

  let healthz = $state<CloudHealthz | null>(null);

  onMount(async () => {
    if (!data.me) {
      const state = await fetchCaptchaState('signin');
      captchaRequired = state.required;
      siteKey = state.siteKey;
      return;
    }
    if (data.cloud && data.cloud.provisioningStatus === 'succeeded') {
      try {
        const r = await fetch(`https://${data.cloud.hostname}/health/live`, { signal: AbortSignal.timeout(3000) });
        if (r.ok) healthz = { status: 'Healthy' };
      } catch { /* fail soft */ }
    }
  });

  function signInHref() {
    return captchaRequired && turnstileToken
      ? `/api/auth/signin?turnstile=${encodeURIComponent(turnstileToken)}`
      : '/api/auth/signin';
  }

  function onCaptchaToken(token: string) {
    turnstileToken = token;
  }

  let view = $derived.by(() => {
    if (!data.me) return 'signed-out' as const;
    if (!data.cloud) return 'onboarding' as const;
    const s = data.cloud.provisioningStatus;
    if (isInFlight(s)) {
      const mode = (IN_FLIGHT_DESTROY_STATUSES as string[]).includes(s) ? 'destroy' : 'create';
      return { kind: 'in-flight', mode } as const;
    }
    if (s === 'succeeded')      return 'dashboard' as const;
    if (isFailed(s))            return 'failed' as const;
    if (isTerminalEmpty(s))     return 'empty' as const;
    return 'onboarding' as const;
  });
</script>

<main>
  {#if view === 'signed-out'}
    <section class="hero">
      <h1 class="hero-title">Thany Marcus</h1>
      <img class="hero-logo" src="/favicon.png" alt="" aria-hidden="true" />
      <p class="hero-tagline muted">Your own cloud for Obsidian — provisioned in minutes, destroyed any time.</p>
      {#if captchaRequired && siteKey}
        <TurnstileWidget {siteKey} onToken={onCaptchaToken} />
      {/if}
      <p>
        {#if captchaRequired && !turnstileToken}
          <a class="btn-primary disabled" aria-disabled="true">Sign in with Google</a>
        {:else}
          <a class="btn btn-primary" href={signInHref()}>Sign in with Google</a>
        {/if}
      </p>
    </section>

  {:else if view === 'onboarding'}
    {#if data.me}
      <h1>Welcome, {data.me.name.split(' ')[0]}</h1>
      <OnboardingChecklist me={data.me} onPassphraseSet={() => invalidateAll()} />
    {/if}

  {:else if typeof view === 'object' && view.kind === 'in-flight'}
    {#if data.cloud}
      <ProvisioningInFlight cloud={data.cloud} mode={view.mode} onTerminal={() => invalidateAll()} />
      {#if view.mode === 'create'}
        <!-- FORK: task #22's success-view layout owns the surrounding chrome; this reveal is
             a self-contained slot that listens to the same SSE channel for plugin_token_issued. -->
        <PluginTokenReveal cloudId={data.cloud.cloudId} />
      {/if}
    {/if}

  {:else if view === 'dashboard'}
    {#if data.cloud}
      <CloudIdentityCard cloud={data.cloud} />
      <CostCard cloud={data.cloud} />
      <PluginTokenCard cloudId={data.cloud.cloudId} cloudHostname={data.cloud.hostname} />
      <DangerZone
        cloudId={data.cloud.cloudId}
        hostname={data.cloud.hostname}
        onDestroyEnqueued={() => invalidateAll()}
      />
    {/if}

  {:else if view === 'failed'}
    {#if data.cloud}
      <section class="card failure">
        <h2 class="error">Provisioning failed</h2>
        <p class="muted">Status: {data.cloud.provisioningStatus}</p>
        <p>Something went wrong while provisioning your cloud.</p>
        <p>
          <a class="btn btn-primary" href="/clouds/new">Try again</a>
        </p>
      </section>
    {/if}

  {:else if view === 'empty'}
    <section class="card">
      <h2>Provision your cloud</h2>
      <p class="muted">You don&rsquo;t have an active cloud right now.</p>
      <p>
        <a class="btn btn-primary" href="/clouds/new">Provision cloud</a>
      </p>
    </section>
  {/if}
</main>

<style>
  .failure { border-color: var(--error); }
  .disabled {
    background: var(--surface-2);
    color: var(--text-muted);
    border-color: var(--border);
    cursor: not-allowed;
    text-decoration: none;
  }
  .hero {
    display: flex;
    flex-direction: column;
    align-items: center;
    text-align: center;
    gap: var(--space-3);
    padding-block: clamp(var(--space-3), 3vw, 32px);
  }
  .hero-logo {
    width: clamp(64px, 8vw, 120px);
    height: auto;
    display: block;
    image-rendering: pixelated;
  }
  .hero-title {
    font-size: clamp(64px, 12vw, 160px);
    line-height: 1;
    margin: 0;
    letter-spacing: 0.02em;
    text-shadow: -7px 0 #5b8def;
  }
  .hero-tagline {
    font-size: var(--text-lg);
    max-width: 56ch;
    margin: 0;
  }
</style>
