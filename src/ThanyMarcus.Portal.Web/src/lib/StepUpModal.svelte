<script lang="ts">
  import { stepUpPrompt } from './stepUpClient';

  let passphrase = $state('');
  let error = $state<string | null>(null);

  function submit(e: SubmitEvent) {
    e.preventDefault();
    const prompt = $stepUpPrompt;
    if (!prompt) return;
    const value = passphrase;
    passphrase = '';
    error = null;
    prompt.resolve(value);
  }

  function cancel() {
    const prompt = $stepUpPrompt;
    if (!prompt) return;
    passphrase = '';
    error = null;
    prompt.resolve(null);
  }
</script>

{#if $stepUpPrompt}
  <div class="backdrop" role="presentation" onclick={cancel}></div>
  <div class="modal" role="dialog" aria-modal="true" aria-labelledby="stepup-title">
    <h2 id="stepup-title">Confirm passphrase</h2>
    <p>This action requires you to re-enter your passphrase.</p>
    <form onsubmit={submit}>
      <label>
        Passphrase
        <!-- svelte-ignore a11y_autofocus -->
        <input type="password" autocomplete="current-password" bind:value={passphrase} required autofocus />
      </label>
      {#if error}<p class="error">{error}</p>{/if}
      <div class="actions">
        <button type="button" onclick={cancel}>Cancel</button>
        <button type="submit" disabled={!passphrase}>Unlock</button>
      </div>
    </form>
  </div>
{/if}

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.4);
    z-index: 1000;
  }
  .modal {
    position: fixed;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    background: white;
    padding: 1.5rem;
    border-radius: 8px;
    box-shadow: 0 4px 24px rgba(0, 0, 0, 0.2);
    min-width: 20rem;
    max-width: 32rem;
    z-index: 1001;
  }
  input {
    display: block;
    width: 100%;
    padding: 0.5rem;
    font-size: 1.1rem;
    box-sizing: border-box;
  }
  .actions {
    display: flex;
    gap: 0.5rem;
    justify-content: flex-end;
    margin-top: 1rem;
  }
  button {
    padding: 0.5rem 1rem;
  }
  .error {
    color: #b00020;
  }
</style>
