<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import StepUpModal from '$lib/StepUpModal.svelte';
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
