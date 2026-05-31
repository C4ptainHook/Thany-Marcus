export interface EmergencyKit {
  recoveryString: string;
  qrPngDataUri: string;
  generatedAt: string;
}

export interface EmergencyKitStatus {
  generatedAt: string | null;
  lastUsedAt: string | null;
  totpRecoveryAvailable: boolean;
}
