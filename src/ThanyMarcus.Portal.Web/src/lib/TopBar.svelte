<script lang="ts">
  import type { MeResponse } from './types/auth';

  type Props = { me: MeResponse };
  let { me }: Props = $props();

  let open = $state(false);
  let menuEl = $state<HTMLDivElement | null>(null);

  function toggle() { open = !open; }
  function close() { open = false; }

  function onWindowClick(e: MouseEvent) {
    if (!open) return;
    if (menuEl && !menuEl.contains(e.target as Node)) close();
  }

  function onKey(e: KeyboardEvent) {
    if (e.key === 'Escape') close();
  }

  function initial(s: string): string {
    return (s ?? '').trim().slice(0, 1).toUpperCase() || '?';
  }
</script>

<svelte:window onclick={onWindowClick} onkeydown={onKey} />

<header class="top-bar">
  <a class="wordmark" href="/">THANY-MARCUS</a>
  <div class="spacer"></div>
  <div class="menu" bind:this={menuEl}>
    <button class="trigger btn-ghost" type="button" onclick={toggle} aria-haspopup="menu" aria-expanded={open}>
      {#if me.profilePictureUrl}
        <img class="avatar" src={me.profilePictureUrl} alt="" width="28" height="28" referrerpolicy="no-referrer" />
      {:else}
        <span class="avatar-fallback">{initial(me.name || me.username)}</span>
      {/if}
      <span class="email">{me.email ?? me.username}</span>
      <span class="caret">▾</span>
    </button>
    {#if open}
      <div class="dropdown" role="menu">
        <a class="dropdown-item" href="/settings" role="menuitem" onclick={close}>Settings</a>
        <form method="post" action="/api/auth/signout" role="none">
          <button class="dropdown-item signout" type="submit">Sign out</button>
        </form>
      </div>
    {/if}
  </div>
</header>

<style>
  .top-bar {
    height: var(--top-bar-h);
    display: flex;
    align-items: center;
    gap: var(--space-3);
    padding: 0 var(--space-4);
    border-bottom: var(--border-width) solid var(--border);
    background: var(--bg);
    position: sticky;
    top: 0;
    z-index: 50;
  }
  .wordmark {
    font-family: var(--font-pixel);
    font-size: var(--text-xl);
    letter-spacing: 2px;
    color: var(--text);
    text-decoration: none;
  }
  .wordmark:hover { color: var(--primary); }

  .spacer { flex: 1; }

  .menu { position: relative; }
  .trigger {
    height: 40px;
    padding: 0 var(--space-2);
    display: inline-flex;
    align-items: center;
    gap: var(--space-2);
    background: transparent;
    color: var(--text);
    border: var(--border-width) solid transparent;
    cursor: pointer;
    font: inherit;
  }
  .trigger:hover { border-color: var(--border); }

  .avatar {
    display: block;
    border: var(--border-width) solid var(--border);
  }
  .avatar-fallback {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 28px;
    height: 28px;
    background: var(--surface-2);
    color: var(--text);
    border: var(--border-width) solid var(--border);
    font-size: var(--text-sm);
  }
  .email { color: var(--text-dim); }
  .caret { color: var(--text-dim); }

  .dropdown {
    position: absolute;
    right: 0;
    top: calc(100% + var(--space-1));
    min-width: 160px;
    background: var(--surface);
    border: var(--border-width) solid var(--border);
    display: flex;
    flex-direction: column;
    z-index: 60;
  }
  .dropdown-item {
    display: block;
    width: 100%;
    text-align: left;
    padding: var(--space-2) var(--space-3);
    background: transparent;
    color: var(--text);
    border: 0;
    border-radius: 0;
    text-decoration: none;
    font: inherit;
    cursor: pointer;
    height: auto;
  }
  .dropdown-item:hover { background: var(--surface-2); color: var(--text); }
  .signout { color: var(--error); }

  @media (max-width: 600px) {
    .email { display: none; }
  }
</style>
