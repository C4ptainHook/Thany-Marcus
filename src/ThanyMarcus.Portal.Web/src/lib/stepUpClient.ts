import { writable } from 'svelte/store';

type ResolveFn = (passphrase: string | null) => void;
export const stepUpPrompt = writable<{ resolve: ResolveFn } | null>(null);

let pending: Promise<string | null> | null = null;
let pendingResolvers: ResolveFn[] = [];

function promptForPassphrase(): Promise<string | null> {
  if (pending) return pending;
  pending = new Promise<string | null>(resolve => {
    pendingResolvers.push(resolve);
    stepUpPrompt.set({
      resolve: passphrase => {
        const resolvers = pendingResolvers;
        pendingResolvers = [];
        pending = null;
        stepUpPrompt.set(null);
        for (const r of resolvers) r(passphrase);
      },
    });
  });
  return pending;
}

export async function fetchWithStepUp(input: RequestInfo, init?: RequestInit): Promise<Response> {
  const res = await fetch(input, init);
  if (res.status !== 401) return res;
  const body = await res.clone().json().catch(() => null);
  if (body?.error !== 'step_up_required') return res;

  const passphrase = await promptForPassphrase();
  if (passphrase === null) return res;

  const unlockRes = await fetch('/api/auth/unlock', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ passphrase }),
  });
  if (!unlockRes.ok) return unlockRes;
  return fetch(input, init);
}

export async function setPassphrase(passphrase: string): Promise<Response> {
  return fetch('/api/auth/passphrase/init', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ passphrase }),
  });
}
