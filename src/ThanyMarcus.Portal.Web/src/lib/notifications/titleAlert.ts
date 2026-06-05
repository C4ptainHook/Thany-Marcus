let saved: string | null = null;

function restore(): void {
  if (saved === null) return;
  document.title = saved;
  saved = null;
  window.removeEventListener('focus', restore);
  document.removeEventListener('visibilitychange', onVisibility);
}

function onVisibility(): void {
  if (document.visibilityState === 'visible') restore();
}

export function flashTitle(text: string): void {
  if (typeof document === 'undefined' || document.hasFocus()) return;
  if (saved === null) saved = document.title;
  document.title = text;
  window.addEventListener('focus', restore);
  document.addEventListener('visibilitychange', onVisibility);
}
