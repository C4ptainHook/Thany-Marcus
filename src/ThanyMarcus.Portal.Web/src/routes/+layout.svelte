<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import { Toaster } from 'svelte-sonner';
  import StepUpModal from '$lib/StepUpModal.svelte';
  import ForgotPassphraseModal from '$lib/ForgotPassphraseModal.svelte';
  import TopBar from '$lib/TopBar.svelte';
  import type { MeResponse } from '$lib/types/auth';

  let {
    data,
    children,
  }: { data: { me: MeResponse | null }; children: Snippet } = $props();
</script>

{#if data.me}
  <TopBar me={data.me} />
{/if}
{@render children()}
<StepUpModal />
<ForgotPassphraseModal />
<Toaster
  theme="dark"
  closeButton
  position="bottom-right"
  style="--normal-bg: var(--surface); --normal-border: var(--border); --normal-text: var(--text); --border-radius: 0px; --width: 22rem;"
/>

<style>
  :global([data-sonner-toaster] [data-sonner-toast][data-styled='true']) {
    font-family: var(--font-pixel);
    font-size: var(--text-sm);
  }
  :global([data-sonner-toaster] [data-sonner-toast][data-styled='true'] [data-description]) {
    font-size: var(--text-sm);
    opacity: 0.85;
  }
  :global([data-sonner-toaster] [data-sonner-toast][data-styled='true'][data-type='success']) {
    border-color: var(--success);
  }
  :global([data-sonner-toaster] [data-sonner-toast][data-styled='true'][data-type='error']) {
    border-color: var(--error);
  }
  :global([data-sonner-toaster] [data-sonner-toast][data-styled='true'][data-type='info']) {
    border-color: var(--border);
  }
</style>
