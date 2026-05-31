import type { PasskeyInfo } from './types/auth';

export class PasskeyCancelledError extends Error {
  constructor() {
    super('Passkey prompt was cancelled.');
    this.name = 'PasskeyCancelledError';
  }
}

export function isPasskeySupported(): boolean {
  return typeof window !== 'undefined' && typeof window.PublicKeyCredential !== 'undefined';
}

/** A user dismissing/timing-out the native prompt surfaces as NotAllowedError/AbortError. */
export function isPasskeyCancellation(err: unknown): boolean {
  return err instanceof PasskeyCancelledError
    || (err instanceof DOMException && (err.name === 'NotAllowedError' || err.name === 'AbortError'));
}

interface CreationOptionsJSON {
  rp: PublicKeyCredentialRpEntity;
  user: { id: string; name: string; displayName: string };
  challenge: string;
  pubKeyCredParams: PublicKeyCredentialParameters[];
  timeout?: number;
  excludeCredentials?: { type: 'public-key'; id: string; transports?: string[] }[];
  authenticatorSelection?: AuthenticatorSelectionCriteria;
  attestation?: AttestationConveyancePreference;
  extensions?: Record<string, unknown>;
}

interface RequestOptionsJSON {
  challenge: string;
  timeout?: number;
  rpId?: string;
  allowCredentials?: { type: 'public-key'; id: string; transports?: string[] }[];
  userVerification?: UserVerificationRequirement;
  extensions?: Record<string, unknown>;
}

interface ChallengeEnvelope<T> {
  challengeId: string;
  options: T;
}

export async function registerPasskey(): Promise<void> {
  const { challengeId, options } = await postJson<ChallengeEnvelope<CreationOptionsJSON>>(
    '/api/auth/passkey/register/challenge');

  const credential = (await navigator.credentials.create({
    publicKey: toCreationOptions(options),
  })) as PublicKeyCredential | null;
  if (!credential) throw new PasskeyCancelledError();

  await postJson('/api/auth/passkey/register/complete', {
    challengeId,
    response: serializeAttestation(credential),
  });
}

export class UsernameTakenError extends Error {
  constructor() {
    super('That username is already taken.');
    this.name = 'UsernameTakenError';
  }
}

/** A field-specific signup rejection (e.g. username_reserved, captcha_invalid). */
export class SignupRejectedError extends Error {
  constructor(public readonly code: string) {
    super(code);
    this.name = 'SignupRejectedError';
  }
}

export interface SignupOptions {
  username: string;
  acknowledged: boolean;
  turnstileToken?: string;
}

export async function signupWithPasskey(opts: SignupOptions): Promise<void> {
  const challengeRes = await fetch('/api/auth/passkey/signup/challenge', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      username: opts.username,
      acknowledgedNoRecovery: opts.acknowledged,
      turnstileToken: opts.turnstileToken ?? null,
    }),
  });
  if (challengeRes.status === 409) throw new UsernameTakenError();
  if (challengeRes.status === 400) {
    const body = await challengeRes.json().catch(() => null);
    throw new SignupRejectedError(body?.error ?? 'invalid');
  }
  if (!challengeRes.ok) throw new Error(`signup challenge failed: ${challengeRes.status}`);
  const { challengeId, options } = (await challengeRes.json()) as ChallengeEnvelope<CreationOptionsJSON>;

  const credential = (await navigator.credentials.create({
    publicKey: toCreationOptions(options),
  })) as PublicKeyCredential | null;
  if (!credential) throw new PasskeyCancelledError();

  const completeRes = await fetch('/api/auth/passkey/signup/complete', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ challengeId, response: serializeAttestation(credential) }),
  });
  if (completeRes.status === 409) throw new UsernameTakenError();
  if (!completeRes.ok) throw new Error(`signup complete failed: ${completeRes.status}`);
}

export async function loginWithPasskey(): Promise<void> {
  const { challengeId, options } = await postJson<ChallengeEnvelope<RequestOptionsJSON>>(
    '/api/auth/passkey/login/challenge');

  const assertion = (await navigator.credentials.get({
    publicKey: toRequestOptions(options),
  })) as PublicKeyCredential | null;
  if (!assertion) throw new PasskeyCancelledError();

  await postJson('/api/auth/passkey/login/complete', {
    challengeId,
    response: serializeAssertion(assertion),
  });
}

export async function listPasskeys(): Promise<PasskeyInfo[]> {
  const r = await fetch('/api/auth/passkeys');
  if (!r.ok) throw new Error(`passkeys list failed: ${r.status}`);
  return r.json();
}

export async function revokePasskey(id: string): Promise<void> {
  const r = await fetch(`/api/auth/passkeys/${id}`, { method: 'DELETE' });
  if (!r.ok) throw new Error(`passkey revoke failed: ${r.status}`);
}

async function postJson<T = void>(url: string, body?: unknown): Promise<T> {
  const r = await fetch(url, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) throw new Error(`${url} failed: ${r.status}`);
  return r.headers.get('content-type')?.includes('application/json')
    ? r.json()
    : (undefined as T);
}

function toCreationOptions(o: CreationOptionsJSON): PublicKeyCredentialCreationOptions {
  return {
    rp: o.rp,
    user: { id: b64urlToBuffer(o.user.id), name: o.user.name, displayName: o.user.displayName },
    challenge: b64urlToBuffer(o.challenge),
    pubKeyCredParams: o.pubKeyCredParams,
    timeout: o.timeout,
    excludeCredentials: o.excludeCredentials?.map((c) => ({
      type: 'public-key',
      id: b64urlToBuffer(c.id),
      transports: c.transports as AuthenticatorTransport[] | undefined,
    })),
    authenticatorSelection: o.authenticatorSelection,
    attestation: o.attestation,
    extensions: o.extensions as AuthenticationExtensionsClientInputs | undefined,
  };
}

function toRequestOptions(o: RequestOptionsJSON): PublicKeyCredentialRequestOptions {
  return {
    challenge: b64urlToBuffer(o.challenge),
    timeout: o.timeout,
    rpId: o.rpId,
    allowCredentials: o.allowCredentials?.map((c) => ({
      type: 'public-key',
      id: b64urlToBuffer(c.id),
      transports: c.transports as AuthenticatorTransport[] | undefined,
    })),
    userVerification: o.userVerification,
    extensions: o.extensions as AuthenticationExtensionsClientInputs | undefined,
  };
}

function serializeAttestation(cred: PublicKeyCredential) {
  const r = cred.response as AuthenticatorAttestationResponse;
  return {
    id: cred.id,
    rawId: bytesToB64url(cred.rawId),
    type: cred.type,
    response: {
      attestationObject: bytesToB64url(r.attestationObject),
      clientDataJSON: bytesToB64url(r.clientDataJSON),
      transports: typeof r.getTransports === 'function' ? r.getTransports() : [],
    },
    clientExtensionResults: cred.getClientExtensionResults(),
  };
}

function serializeAssertion(cred: PublicKeyCredential) {
  const r = cred.response as AuthenticatorAssertionResponse;
  return {
    id: cred.id,
    rawId: bytesToB64url(cred.rawId),
    type: cred.type,
    response: {
      authenticatorData: bytesToB64url(r.authenticatorData),
      signature: bytesToB64url(r.signature),
      clientDataJSON: bytesToB64url(r.clientDataJSON),
      userHandle: r.userHandle ? bytesToB64url(r.userHandle) : null,
    },
    clientExtensionResults: cred.getClientExtensionResults(),
  };
}

export function b64urlToBuffer(s: string): ArrayBuffer {
  const pad = s.length % 4 === 0 ? '' : '='.repeat(4 - (s.length % 4));
  const b64 = (s + pad).replace(/-/g, '+').replace(/_/g, '/');
  const bin = atob(b64);
  const buffer = new ArrayBuffer(bin.length);
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return buffer;
}

export function bytesToB64url(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
