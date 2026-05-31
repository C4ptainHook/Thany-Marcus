<script lang="ts">
  import { stepUpPrompt } from './stepUpClient';
  import { openForgotPassphrase } from './forgotPassphraseClient';
  import TurnstileWidget from './TurnstileWidget.svelte';

  let passphrase = $state('');
  let turnstileToken = $state('');
  let error = $state<string | null>(null);

  function submit(e: SubmitEvent) {
    e.preventDefault();
    const prompt = $stepUpPrompt;
    if (!prompt) return;
    if (prompt.captchaRequired && !turnstileToken) {
      error = 'Please complete the verification challenge.';
      return;
    }
    const value = passphrase;
    const token = turnstileToken;
    passphrase = '';
    turnstileToken = '';
    error = null;
    prompt.resolve({ passphrase: value, turnstileToken: token });
  }

  function cancel() {
    const prompt = $stepUpPrompt;
    if (!prompt) return;
    passphrase = '';
    turnstileToken = '';
    error = null;
    prompt.resolve(null);
  }

  function onCaptchaToken(token: string) {
    turnstileToken = token;
  }

  function forgot() {
    const prompt = $stepUpPrompt;
    passphrase = '';
    turnstileToken = '';
    error = null;
    prompt?.resolve(null);
    openForgotPassphrase();
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
      {#if $stepUpPrompt.captchaRequired && $stepUpPrompt.siteKey}
        <TurnstileWidget siteKey={$stepUpPrompt.siteKey} onToken={onCaptchaToken} />
      {/if}
      {#if error}<p class="error">{error}</p>{/if}
      <div class="actions">
        <button type="button" onclick={cancel}>Cancel</button>
        <button
          type="submit"
          disabled={!passphrase || ($stepUpPrompt.captchaRequired && !turnstileToken)}>
          Unlock
        </button>
      </div>
      <button class="forgot" type="button" onclick={forgot}>I forgot my passphrase →</button>
    </form>
  </div>
{/if}

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.6);
    z-index: 1000;
  }
  .modal {
    position: fixed;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    background: var(--surface);
    color: var(--text);
    padding: var(--space-5);
    border: var(--border-width) solid var(--border);
    min-width: 20rem;
    max-width: 32rem;
    z-index: 1001;
  }
  .modal :global(input) {
    display: block;
    width: 100%;
    margin-top: var(--space-2);
  }
  .actions {
    display: flex;
    gap: var(--space-2);
    justify-content: flex-end;
    margin-top: var(--space-4);
  }
  .error {
    color: var(--error);
  }
  .forgot {
    display: block;
    margin-top: var(--space-3);
    background: transparent;
    border: 0;
    padding: 0;
    color: var(--text-dim);
    text-align: left;
    cursor: pointer;
    font: inherit;
    text-decoration: underline;
  }
  @media (max-width: 600px) {
    .modal {
      position: fixed;
      inset: 0;
      top: 0; left: 0;
      transform: none;
      max-width: none;
      width: 100%;
      height: 100%;
      overflow-y: auto;
    }
  }
</style>
