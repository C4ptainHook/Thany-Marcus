<script lang="ts">
  type Props = {
    token: string;
    cloudUrl: string;
    onClose: () => void;
  };
  let { token, cloudUrl, onClose }: Props = $props();

  const deepLink = $derived(`obsidian://thany-marcus-connect?cloudUrl=${cloudUrl}&token=${token}`);

  let copiedToken = $state(false);
  let copiedUrl = $state(false);
  let copiedJson = $state(false);

  async function copy(text: string, which: 'token' | 'url' | 'json') {
    try {
      await navigator.clipboard.writeText(text);
      if (which === 'token') copiedToken = true;
      if (which === 'url')   copiedUrl = true;
      if (which === 'json')  copiedJson = true;
      setTimeout(() => {
        if (which === 'token') copiedToken = false;
        if (which === 'url')   copiedUrl = false;
        if (which === 'json')  copiedJson = false;
      }, 1500);
    } catch { /* clipboard blocked — user can select+copy manually */ }
  }

  function copyJsonConfig() {
    copy(JSON.stringify({ cloudUrl, token }), 'json');
  }
</script>

<div class="backdrop" role="presentation"></div>
<div class="modal" role="dialog" aria-modal="true" aria-labelledby="ptm-title">
  <h2 id="ptm-title">NEW PLUGIN TOKEN</h2>

  <p class="warn-box">
    ⚠ Shown once. Save it now — you cannot retrieve it later.
  </p>

  <label class="field">
    <span class="field-label muted">Token</span>
    <div class="row">
      <input class="mono" type="text" readonly value={token} />
      <button type="button" class="btn-secondary copy" onclick={() => copy(token, 'token')}>
        {copiedToken ? 'Copied' : 'Copy'}
      </button>
    </div>
  </label>

  <label class="field">
    <span class="field-label muted">Cloud URL</span>
    <div class="row">
      <input class="mono" type="text" readonly value={cloudUrl} />
      <button type="button" class="btn-secondary copy" onclick={() => copy(cloudUrl, 'url')}>
        {copiedUrl ? 'Copied' : 'Copy'}
      </button>
    </div>
  </label>

  <div class="actions">
    <button type="button" class="btn-secondary" onclick={copyJsonConfig}>
      {copiedJson ? 'Copied JSON' : 'Copy plugin config (JSON)'}
    </button>
    <div class="actions-right">
      <a class="btn-primary" href={deepLink} title="Opens the Thany-Marcus plugin in Obsidian">
        Open in Obsidian
      </a>
      <button type="button" class="btn-secondary" onclick={onClose}>I&rsquo;ve saved it</button>
    </div>
  </div>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.7);
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
    min-width: 32rem;
    max-width: 40rem;
    z-index: 1001;
  }
  .warn-box {
    border: var(--border-width) solid var(--warn);
    color: var(--warn);
    padding: var(--space-2) var(--space-3);
    margin: var(--space-3) 0;
  }
  .field {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    margin-bottom: var(--space-3);
  }
  .field-label {
    font-size: var(--text-sm);
  }
  .row {
    display: flex;
    gap: var(--space-2);
  }
  .row input { flex: 1; }
  .mono { font-family: var(--font-pixel); letter-spacing: 0.5px; }
  .copy { white-space: nowrap; }
  .actions {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: var(--space-2);
    margin-top: var(--space-5);
    flex-wrap: wrap;
  }
  .actions-right {
    display: flex;
    gap: var(--space-2);
  }
  @media (max-width: 600px) {
    .modal {
      position: fixed;
      inset: 0;
      top: 0; left: 0;
      transform: none;
      min-width: 0;
      max-width: none;
      width: 100%;
      height: 100%;
      overflow-y: auto;
    }
  }
</style>
