export type TotpEnableInit = { secret: string; qrPngDataUri: string };
export type TotpEnableVerify = { backupCodes: string[] };

export async function enableInit(): Promise<TotpEnableInit> {
  const r = await fetch('/api/auth/totp/enable/init', { method: 'POST' });
  if (!r.ok) throw new Error(`enable/init failed: ${r.status}`);
  return r.json();
}

export async function enableVerify(secret: string, code: string): Promise<TotpEnableVerify> {
  const r = await fetch('/api/auth/totp/enable/verify', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ secret, code }),
  });
  if (!r.ok) throw new Error(`enable/verify failed: ${r.status}`);
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

export async function challenge(code: string): Promise<void> {
  const r = await fetch('/totp-challenge', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ code }),
  });
  if (!r.ok) throw new Error(`challenge failed: ${r.status}`);
}
