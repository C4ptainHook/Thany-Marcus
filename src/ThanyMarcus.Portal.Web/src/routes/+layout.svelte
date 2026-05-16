<script lang="ts">
  import '../app.css';
  import StepUpModal from '$lib/StepUpModal.svelte';
  let { children } = $props();
</script>

{@render children()}
<StepUpModal />
