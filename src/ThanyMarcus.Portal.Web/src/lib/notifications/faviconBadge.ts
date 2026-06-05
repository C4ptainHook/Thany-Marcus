let originalHref: string | null = null;

function setIcon(href: string): void {
  document.querySelectorAll("link[rel~='icon']").forEach((el) => el.remove());
  const link = document.createElement('link');
  link.rel = 'icon';
  link.type = 'image/png';
  link.href = href;
  document.head.appendChild(link);
}

function restore(): void {
  if (originalHref !== null) {
    setIcon(originalHref);
    originalHref = null;
  }
  window.removeEventListener('focus', restore);
  document.removeEventListener('visibilitychange', onVisibility);
}

function onVisibility(): void {
  if (document.visibilityState === 'visible') restore();
}

export function flashFavicon(color: string): void {
  if (typeof document === 'undefined' || document.hasFocus()) return;
  if (originalHref === null) {
    const current = document.querySelector<HTMLLinkElement>("link[rel~='icon']");
    originalHref = current ? current.href : '';
  }
  if (!originalHref) return;

  const img = new Image();
  img.onload = () => {
    if (originalHref === null) return;
    const size = 64;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.drawImage(img, 0, 0, size, size);
    const r = size * 0.3;
    ctx.beginPath();
    ctx.arc(size - r, r, r, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
    ctx.lineWidth = size * 0.08;
    ctx.strokeStyle = '#14181a';
    ctx.stroke();
    setIcon(canvas.toDataURL('image/png'));
  };
  img.src = originalHref;

  window.addEventListener('focus', restore);
  document.addEventListener('visibilitychange', onVisibility);
}
