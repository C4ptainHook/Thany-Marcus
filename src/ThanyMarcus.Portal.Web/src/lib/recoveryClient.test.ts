import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  generateEmergencyKit,
  emergencyKitStatus,
  unlockPassphrase,
  resetViaTotp,
  resetViaKit,
} from './recoveryClient';

interface RecordedRequest {
  url: string;
  method: string | undefined;
  body: string | undefined;
}

function jsonResp(body: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: (k: string) => (k.toLowerCase() === 'content-type' ? 'application/json' : null) },
    clone() { return this; },
    json: async () => body,
  };
}

function emptyResp(status = 204) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => null },
    clone() { return this; },
    json: async () => undefined,
  };
}

function recordFetch(handler: (url: string) => unknown) {
  const requests: RecordedRequest[] = [];
  const fetchMock = vi.fn(async (url: string, init?: { method?: string; body?: string }) => {
    requests.push({ url, method: init?.method, body: init?.body });
    return handler(url);
  });
  vi.stubGlobal('fetch', fetchMock);
  return requests;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const KIT = {
  recoveryString: 'swift river amber cloud iron stone vivid coral',
  qrPngDataUri: 'data:image/png;base64,AAAA',
  generatedAt: '2026-05-31T12:00:00Z',
};

describe('generateEmergencyKit', () => {
  it('POSTs to the generate endpoint and returns the kit', async () => {
    const requests = recordFetch(() => jsonResp(KIT));

    const kit = await generateEmergencyKit();

    expect(kit.recoveryString).toBe(KIT.recoveryString);
    expect(requests[0].url).toBe('/api/auth/emergency-kit/generate');
    expect(requests[0].method).toBe('POST');
  });

  it('throws on a non-OK response', async () => {
    recordFetch(() => emptyResp(500));
    await expect(generateEmergencyKit()).rejects.toThrow();
  });
});

describe('emergencyKitStatus', () => {
  it('GETs the status endpoint', async () => {
    const status = { generatedAt: '2026-05-31T12:00:00Z', lastUsedAt: null, totpRecoveryAvailable: true };
    const requests = recordFetch(() => jsonResp(status));

    const result = await emergencyKitStatus();

    expect(result.totpRecoveryAvailable).toBe(true);
    expect(requests[0].url).toBe('/api/auth/emergency-kit');
  });
});

describe('unlockPassphrase', () => {
  it('POSTs the passphrase to the unlock endpoint', async () => {
    const requests = recordFetch(() => emptyResp(204));

    await unlockPassphrase('hunter2hunter2');

    expect(requests[0].url).toBe('/api/auth/unlock');
    expect(JSON.parse(requests[0].body!)).toEqual({ passphrase: 'hunter2hunter2' });
  });
});

describe('resetViaTotp', () => {
  it('POSTs the code and new passphrase, returning the raw response', async () => {
    const requests = recordFetch(() => emptyResp(204));

    const res = await resetViaTotp('123456', 'brand-new-pass-9');

    expect(res.status).toBe(204);
    expect(requests[0].url).toBe('/api/auth/passphrase/reset-via-totp');
    expect(JSON.parse(requests[0].body!)).toEqual({ totpCode: '123456', newPassphrase: 'brand-new-pass-9' });
  });
});

describe('resetViaKit', () => {
  it('returns the fresh kit on success', async () => {
    const requests = recordFetch(() => jsonResp(KIT));

    const result = await resetViaKit('swift river amber cloud iron stone vivid coral', 'brand-new-pass-9');

    expect(result.ok).toBe(true);
    if (result.ok) expect(result.kit.recoveryString).toBe(KIT.recoveryString);
    expect(requests[0].url).toBe('/api/auth/passphrase/reset-via-kit');
    expect(JSON.parse(requests[0].body!)).toEqual({
      recoveryString: 'swift river amber cloud iron stone vivid coral',
      newPassphrase: 'brand-new-pass-9',
    });
  });

  it('reports the status code on failure', async () => {
    recordFetch(() => emptyResp(401));

    const result = await resetViaKit('wrong words here', 'brand-new-pass-9');

    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.status).toBe(401);
  });
});
