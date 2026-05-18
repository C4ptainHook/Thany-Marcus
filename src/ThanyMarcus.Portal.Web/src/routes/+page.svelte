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
        const r = await fetch(`https://${data.cloud.hostname}/healthz`, { signal: AbortSignal.timeout(3000) });
        if (r.ok) healthz = (await r.json()) as CloudHealthz;
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
    <h1>Thany-Marcus</h1>
    <p class="muted">Your own cloud for Obsidian — provisioned in minutes, destroyed any time.</p>
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
      <CostCard cloud={data.cloud} workerUptimeMonthSeconds={healthz?.workerUptimeMonthSeconds ?? null} />
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
</style>
