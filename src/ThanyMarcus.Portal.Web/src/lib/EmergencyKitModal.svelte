<script lang="ts">
  import type { EmergencyKit } from './types/recovery';

  let {
    kit,
    email,
    heading = 'Save your Emergency Kit',
    intro,
    onSaved,
  }: {
    kit: EmergencyKit;
    email: string;
    heading?: string;
    intro?: string;
    onSaved: () => void;
  } = $props();

  let acknowledged = $state(false);
  let copied = $state(false);

  const words = $derived(kit.recoveryString.trim().split(/\s+/));
  const generatedOn = $derived(formatDate(kit.generatedAt));

  function formatDate(iso: string): string {
    try { return new Date(iso).toISOString().slice(0, 10); }
    catch { return iso; }
  }

  async function copy() {
    await navigator.clipboard.writeText(kit.recoveryString);
    copied = true;
    setTimeout(() => (copied = false), 2000);
  }

  function print() {
    const w = window.open('', '_blank', 'width=520,height=720');
    if (!w) return;
    const rows = [words.slice(0, 4).join(' '), words.slice(4).join(' ')];
    w.document.write(`<!doctype html><html><head><meta charset="utf-8"><title>Thany-Marcus Emergency Kit</title>
<style>
  body { font-family: ui-monospace, "Courier New", monospace; color: #111; margin: 32px; }
  .card { border: 2px solid #111; padding: 24px; max-width: 420px; }
  h1 { font-size: 20px; margin: 0 0 4px; }
  .email { color: #444; margin: 0 0 16px; }
  .words { font-size: 22px; line-height: 1.6; letter-spacing: 0.04em; margin: 16px 0; }
  img { display: block; margin: 16px 0; image-rendering: pixelated; }
  .footer { color: #666; font-size: 12px; margin-top: 16px; }
  @media print { body { margin: 0; } .card { border: 2px solid #111; } }
</style></head><body>
  <div class="card">
    <h1>Thany-Marcus Emergency Kit</h1>
    <p class="email">${escapeHtml(email)}</p>
    <div class="words">${escapeHtml(rows[0])}<br>${escapeHtml(rows[1])}</div>
    <img src="${kit.qrPngDataUri}" alt="Recovery QR code" width="200" height="200">
    <p class="footer">Generated ${escapeHtml(generatedOn)} · This is the only way back if you lose both your passphrase and your 2FA device. Keep it somewhere safe.</p>
  </div>
</body></html>`);
    w.document.close();
    w.focus();
    w.print();
  }

  function escapeHtml(s: string): string {
    return s.replace(/[&<>"']/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] as string);
  }
</script>

<div class="backdrop" role="presentation"></div>
<div class="modal" role="dialog" aria-modal="true" aria-labelledby="kit-title">
  <h2 id="kit-title">{heading}</h2>
  <p class="intro">
    {intro ?? 'This is the only way back if you ever lose access to both your passphrase and your 2FA device. We will show this once.'}
  </p>

  <div class="kit-card">
    <div class="kit-brand">Thany-Marcus Emergency Kit</div>
    <div class="kit-email">{email}</div>
    <div class="kit-words">
      <span>{words.slice(0, 4).join(' ')}</span>
      <span>{words.slice(4).join(' ')}</span>
    </div>
    <img class="kit-qr" src={kit.qrPngDataUri} alt="Recovery QR code" width="160" height="160" />
    <div class="kit-generated">Generated {generatedOn}</div>
  </div>

  <div class="kit-actions">
    <button class="btn btn-secondary" type="button" onclick={print}>Print</button>
    <button class="btn btn-secondary" type="button" onclick={copy}>{copied ? 'Copied ✓' : 'Copy'}</button>
  </div>

  <label class="ack">
    <input type="checkbox" bind:checked={acknowledged} />
    I have saved or printed this kit
  </label>

  <button class="btn btn-primary save" type="button" disabled={!acknowledged} onclick={onSaved}>
    I&rsquo;ve saved it
  </button>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: var(--color-backdrop);
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
    width: min(32rem, calc(100vw - var(--space-5)));
    max-height: calc(100vh - var(--space-5));
    overflow-y: auto;
    z-index: 1001;
  }
  .intro { color: var(--text-dim); }
  .kit-card {
    border: var(--border-width) solid var(--border);
    background: var(--surface-2);
    padding: var(--space-4);
    margin: var(--space-4) 0;
    text-align: center;
  }
  .kit-brand { color: var(--primary); }
  .kit-email { color: var(--text-dim); margin-bottom: var(--space-3); }
  .kit-words {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
    font-size: var(--text-lg);
    letter-spacing: 0.04em;
    margin: var(--space-3) 0;
  }
  .kit-qr {
    display: block;
    margin: var(--space-3) auto;
    background: white;
    padding: var(--space-2);
    image-rendering: pixelated;
  }
  .kit-generated { color: var(--text-muted); font-size: var(--text-sm); }
  .kit-actions {
    display: flex;
    gap: var(--space-2);
    justify-content: center;
  }
  .ack {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    margin: var(--space-4) 0 var(--space-3);
  }
  .save { width: 100%; }
</style>
