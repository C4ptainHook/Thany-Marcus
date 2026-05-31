import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  registerPasskey,
  loginWithPasskey,
  isPasskeySupported,
  isPasskeyCancellation,
  PasskeyCancelledError,
  b64urlToBuffer,
  bytesToB64url,
} from './passkey';

function bytesOf(values: number[]): ArrayBuffer {
  return new Uint8Array(values).buffer;
}

function jsonResp(body: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: (k: string) => (k.toLowerCase() === 'content-type' ? 'application/json' : null) },
    json: async () => body,
  };
}

function emptyResp(status = 204) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => null },
    json: async () => undefined,
  };
}

interface RecordedRequest {
  url: string;
  body: string | undefined;
}

/** Records every request and routes challenge endpoints to the supplied options. */
function recordingFetch(challengePath: string, options: unknown, challengeId: string) {
  const requests: RecordedRequest[] = [];
  const fetchMock = vi.fn(async (url: string, init?: { body?: string }) => {
    requests.push({ url, body: init?.body });
    return url.endsWith(challengePath)
      ? jsonResp({ challengeId, options })
      : emptyResp(204);
  });
  vi.stubGlobal('fetch', fetchMock);
  return requests;
}

function setCredentials(create: unknown, get: unknown) {
  Object.defineProperty(globalThis.navigator, 'credentials', {
    configurable: true,
    value: { create, get },
  });
}

const CREATION_OPTIONS = {
  rp: { id: 'thany.click', name: 'Thany-Marcus Portal' },
  user: { id: bytesToB64url(bytesOf([10, 11, 12])), name: 'a@b.com', displayName: 'A' },
  challenge: bytesToB64url(bytesOf([1, 2, 3, 4])),
  pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
  excludeCredentials: [],
};

const REQUEST_OPTIONS = {
  challenge: bytesToB64url(bytesOf([5, 6, 7, 8])),
  rpId: 'thany.click',
  allowCredentials: [],
  userVerification: 'required',
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('base64url round-trip', () => {
  it('encodes and decodes without padding', () => {
    const original = bytesOf([0, 1, 250, 255, 128, 64]);
    const encoded = bytesToB64url(original);
    expect(encoded).not.toContain('=');
    expect(encoded).not.toContain('+');
    expect(encoded).not.toContain('/');
    expect(new Uint8Array(b64urlToBuffer(encoded))).toEqual(new Uint8Array(original));
  });
});

describe('isPasskeySupported', () => {
  it('is false when PublicKeyCredential is absent', () => {
    expect(isPasskeySupported()).toBe(false);
  });

  it('is true when PublicKeyCredential is present', () => {
    vi.stubGlobal('PublicKeyCredential', function () {});
    expect(isPasskeySupported()).toBe(true);
  });
});

describe('isPasskeyCancellation', () => {
  it('recognises the explicit cancel error', () => {
    expect(isPasskeyCancellation(new PasskeyCancelledError())).toBe(true);
  });

  it('recognises NotAllowedError / AbortError DOMExceptions', () => {
    expect(isPasskeyCancellation(new DOMException('x', 'NotAllowedError'))).toBe(true);
    expect(isPasskeyCancellation(new DOMException('x', 'AbortError'))).toBe(true);
  });

  it('does not swallow real errors', () => {
    expect(isPasskeyCancellation(new Error('boom'))).toBe(false);
    expect(isPasskeyCancellation(new DOMException('x', 'SecurityError'))).toBe(false);
  });
});

describe('registerPasskey', () => {
  it('fetches a challenge, creates the credential, then posts the serialized attestation', async () => {
    const requests = recordingFetch('/register/challenge', CREATION_OPTIONS, 'chal-1');

    let createOpts: { publicKey: PublicKeyCredentialCreationOptions } | undefined;
    const create = vi.fn(async (opts: { publicKey: PublicKeyCredentialCreationOptions }) => {
      createOpts = opts;
      return {
        id: 'cred-id',
        rawId: bytesOf([1, 2, 3]),
        type: 'public-key',
        response: {
          attestationObject: bytesOf([4, 5, 6]),
          clientDataJSON: bytesOf([7, 8, 9]),
          getTransports: () => ['internal'],
        },
        getClientExtensionResults: () => ({}),
      };
    });
    const get = vi.fn();
    setCredentials(create, get);

    await registerPasskey();

    // Order: challenge first, then complete.
    expect(requests[0].url).toContain('/register/challenge');
    expect(requests[1].url).toContain('/register/complete');
    expect(create).toHaveBeenCalledTimes(1);
    expect(get).not.toHaveBeenCalled();

    // The decoded challenge handed to the authenticator matches the server's.
    expect(bytesToB64url(createOpts!.publicKey.challenge as ArrayBuffer)).toBe(CREATION_OPTIONS.challenge);

    // The completion body carries the challenge id and base64url-encoded attestation.
    const body = JSON.parse(requests[1].body!);
    expect(body.challengeId).toBe('chal-1');
    expect(body.response.rawId).toBe(bytesToB64url(bytesOf([1, 2, 3])));
    expect(body.response.response.attestationObject).toBe(bytesToB64url(bytesOf([4, 5, 6])));
    expect(body.response.response.transports).toEqual(['internal']);
    expect(body.response.clientExtensionResults).toEqual({});
  });
});

describe('loginWithPasskey', () => {
  it('fetches a challenge, gets the assertion, then posts it', async () => {
    const requests = recordingFetch('/login/challenge', REQUEST_OPTIONS, 'chal-2');

    let getOpts: { publicKey: PublicKeyCredentialRequestOptions } | undefined;
    const get = vi.fn(async (opts: { publicKey: PublicKeyCredentialRequestOptions }) => {
      getOpts = opts;
      return {
        id: 'cred-id',
        rawId: bytesOf([1, 2, 3]),
        type: 'public-key',
        response: {
          authenticatorData: bytesOf([4]),
          signature: bytesOf([5]),
          clientDataJSON: bytesOf([6]),
          userHandle: bytesOf([7]),
        },
        getClientExtensionResults: () => ({}),
      };
    });
    setCredentials(vi.fn(), get);

    await loginWithPasskey();

    expect(requests[0].url).toContain('/login/challenge');
    expect(requests[1].url).toContain('/login/complete');
    expect(get).toHaveBeenCalledTimes(1);

    expect(bytesToB64url(getOpts!.publicKey.challenge as ArrayBuffer)).toBe(REQUEST_OPTIONS.challenge);

    const body = JSON.parse(requests[1].body!);
    expect(body.challengeId).toBe('chal-2');
    expect(body.response.response.signature).toBe(bytesToB64url(bytesOf([5])));
    expect(body.response.response.userHandle).toBe(bytesToB64url(bytesOf([7])));
  });

  it('throws PasskeyCancelledError and does not call complete when the user cancels (null)', async () => {
    const requests = recordingFetch('/login/challenge', REQUEST_OPTIONS, 'chal-3');
    setCredentials(vi.fn(), vi.fn(async () => null));

    await expect(loginWithPasskey()).rejects.toBeInstanceOf(PasskeyCancelledError);

    // Only the challenge call happened — completion was never attempted.
    expect(requests).toHaveLength(1);
    expect(requests[0].url).toContain('/login/challenge');
  });
});
