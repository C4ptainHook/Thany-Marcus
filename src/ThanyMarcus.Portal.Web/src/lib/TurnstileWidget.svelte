<script lang="ts">
  import { onMount } from 'svelte';
  import { renderTurnstile } from './turnstileClient';

  let { siteKey, onToken }: { siteKey: string; onToken: (token: string) => void } = $props();
  let container: HTMLDivElement;

  onMount(async () => {
    try {
      const token = await renderTurnstile(container, siteKey);
      onToken(token);
    } catch {
      onToken('');
    }
  });
</script>

<div bind:this={container} class="cf-turnstile"></div>
