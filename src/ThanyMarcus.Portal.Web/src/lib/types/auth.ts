export type TotpState = 'not-enabled' | 'not-verified' | 'verified';

export interface MeResponse {
  userId: string;
  email: string;
  name: string;
  profilePictureUrl: string | null;
  totp: TotpState | string;
  passphraseSet: boolean;
}
