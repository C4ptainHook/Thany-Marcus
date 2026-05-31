export type TotpState = 'not-enabled' | 'not-verified' | 'verified';

export interface MeResponse {
  userId: string;
  email: string | null;
  username: string;
  name: string;
  profilePictureUrl: string | null;
  totp: TotpState | string;
  passphraseSet: boolean;
}

export interface PasskeyInfo {
  id: string;
  authenticatorName: string;
  backedUp: boolean;
  createdAt: string;
  lastUsedAt: string | null;
}
