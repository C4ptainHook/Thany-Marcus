import { writable } from 'svelte/store';
import { fetchCaptchaState } from './turnstileClient';

export type StepUpResolution = { passphrase: string; turnstileToken: string } | null;
type ResolveFn = (value: StepUpResolution) => void;

export const stepUpPrompt = writable<{
  resolve: ResolveFn;
  siteKey: string;
  captchaRequired: boolean;
} | null>(null);

let pending: Promise<StepUpResolution> | null = null;
let pendingResolvers: ResolveFn[] = [];

async function promptForPassphrase(): Promise<StepUpResolution> {
  if (pending) return pending;
  const captcha = await fetchCaptchaState('unlock');
  pending = new Promise<StepUpResolution>(resolve => {
    pendingResolvers.push(resolve);
    stepUpPrompt.set({
      resolve: value => {
        const resolvers = pendingResolvers;
        pendingResolvers = [];
        pending = null;
        stepUpPrompt.set(null);
        for (const r of resolvers) r(value);
      },
      siteKey: captcha.siteKey,
      captchaRequired: captcha.required,
    });
  });
  return pending;
}

export async function fetchWithStepUp(input: RequestInfo, init?: RequestInit): Promise<Response> {
  const res = await fetch(input, init);
  if (res.status !== 401) return res;
  const body = await res.clone().json().catch(() => null);
  if (body?.error !== 'step_up_required') return res;

  const resolution = await promptForPassphrase();
  if (resolution === null) return res;

  const headers: Record<string, string> = { 'content-type': 'application/json' };
  if (resolution.turnstileToken) headers['cf-turnstile-response'] = resolution.turnstileToken;
  const unlockRes = await fetch('/api/auth/unlock', {
    method: 'POST',
    headers,
    body: JSON.stringify({ passphrase: resolution.passphrase }),
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
