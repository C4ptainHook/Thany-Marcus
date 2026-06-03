import { fetchWithStepUp } from './stepUpClient';

export type TotpEnableInit = { secret: string; qrPngDataUri: string };
export type TotpEnableVerify = { backupCodes: string[] };

export async function enableInit(): Promise<TotpEnableInit> {
  const r = await fetch('/api/auth/totp/enable/init', { method: 'POST' });
  if (!r.ok) throw new Error(`enable/init failed: ${r.status}`);
  return r.json();
}

export async function enableVerify(secret: string, code: string, currentCode?: string): Promise<TotpEnableVerify> {
  const r = await fetchWithStepUp('/api/auth/totp/enable/verify', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ secret, code, currentCode }),
  });
  if (!r.ok) {
    const err: Error & { status?: number } = new Error(`enable/verify failed: ${r.status}`);
    err.status = r.status;
    throw err;
  }
  return r.json();
}

export async function disable(code: string): Promise<void> {
  const r = await fetch('/api/auth/totp/disable', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ code }),
  });
  if (!r.ok) throw new Error(`disable failed: ${r.status}`);
}

export async function challenge(code: string, turnstileToken?: string): Promise<Response> {
  const headers: Record<string, string> = { 'content-type': 'application/json' };
  if (turnstileToken) headers['cf-turnstile-response'] = turnstileToken;
  return fetch('/totp-challenge', {
    method: 'POST',
    headers,
    body: JSON.stringify({ code }),
  });
}
