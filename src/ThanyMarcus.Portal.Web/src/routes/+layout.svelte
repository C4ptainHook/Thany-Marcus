<script lang="ts">
  import StepUpModal from '$lib/StepUpModal.svelte';
  let { children } = $props();
</script>

{@render children()}
<StepUpModal />
