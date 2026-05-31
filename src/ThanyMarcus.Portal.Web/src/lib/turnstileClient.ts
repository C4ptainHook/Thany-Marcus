export type CaptchaStateKind = 'signin' | 'signup' | 'totp' | 'unlock';

export type CaptchaState = {
  required: boolean;
  siteKey: string;
};

let scriptPromise: Promise<void> | null = null;

function loadScript(): Promise<void> {
  if (scriptPromise) return scriptPromise;
  scriptPromise = new Promise((resolve, reject) => {
    const existing = document.querySelector('script[data-turnstile]');
    if (existing) {
      resolve();
      return;
    }
    const s = document.createElement('script');
    s.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
    s.async = true;
    s.defer = true;
    s.setAttribute('data-turnstile', '');
    s.onload = () => resolve();
    s.onerror = () => reject(new Error('turnstile script load failed'));
    document.head.appendChild(s);
  });
  return scriptPromise;
}

export async function fetchCaptchaState(kind: CaptchaStateKind): Promise<CaptchaState> {
  const r = await fetch(`/api/auth/captcha-state?kind=${kind}`);
  if (!r.ok) return { required: false, siteKey: '' };
  const body = await r.json();
  return { required: !!body.required, siteKey: body.site_key ?? '' };
}

export async function renderTurnstile(container: HTMLElement, siteKey: string): Promise<string> {
  await loadScript();
  return new Promise<string>((resolve, reject) => {
    // @ts-expect-error turnstile global injected by Cloudflare script
    if (!window.turnstile) {
      reject(new Error('turnstile global missing after script load'));
      return;
    }
    // @ts-expect-error turnstile global injected by Cloudflare script
    window.turnstile.render(container, {
      sitekey: siteKey,
      callback: (token: string) => resolve(token),
      'error-callback': () => reject(new Error('turnstile widget error')),
    });
  });
}
