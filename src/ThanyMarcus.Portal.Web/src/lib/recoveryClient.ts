import { apiFetch } from './http';
import type { EmergencyKit, EmergencyKitStatus } from './types/recovery';

// Regenerate goes through apiFetch so a locked session is transparently prompted for step-up.
export async function generateEmergencyKit(): Promise<EmergencyKit> {
  const r = await apiFetch('/api/auth/emergency-kit/generate', { method: 'POST' });
  if (!r.ok) throw new Error(`emergency-kit/generate failed: ${r.status}`);
  return r.json();
}

export async function emergencyKitStatus(): Promise<EmergencyKitStatus> {
  const r = await fetch('/api/auth/emergency-kit');
  if (!r.ok) throw new Error(`emergency-kit status failed: ${r.status}`);
  return r.json();
}

// Seed the step-up unlock cache directly — used during onboarding right after the passphrase is
// set, so the Emergency Kit can be generated without prompting the user a second time.
export async function unlockPassphrase(passphrase: string): Promise<Response> {
  return fetch('/api/auth/unlock', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ passphrase }),
  });
}

export async function resetViaTotp(totpCode: string, newPassphrase: string): Promise<Response> {
  return fetch('/api/auth/passphrase/reset-via-totp', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ totpCode, newPassphrase }),
  });
}

export type ResetViaKitResult =
  | { ok: true; kit: EmergencyKit }
  | { ok: false; status: number };

export async function resetViaKit(recoveryString: string, newPassphrase: string): Promise<ResetViaKitResult> {
  const r = await fetch('/api/auth/passphrase/reset-via-kit', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ recoveryString, newPassphrase }),
  });
  if (!r.ok) return { ok: false, status: r.status };
  return { ok: true, kit: await r.json() };
}
