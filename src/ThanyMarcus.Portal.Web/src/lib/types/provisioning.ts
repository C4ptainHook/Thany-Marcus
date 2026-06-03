export type PhaseName =
  | 'minting_spaces'
  | 'tf_planning' | 'tf_applying' | 'dns_creating'
  | 'awaiting_cloud_callback' | 'cloud_registered' | 'awaiting_cert'
  | 'issuing_plugin_token';

export type WizardSseEvent =
  | { type: 'phase_started'; phase: PhaseName; at: string }
  | { type: 'phase_completed'; phase: PhaseName; at: string }
  | { type: 'phase_failed'; phase: PhaseName; reason: string; message: string }
  | { type: 'cloud_ready'; cloudId: string; hostname: string; ip: string | null }
  | { type: 'cloud_failed'; terminalStatus: string; reason: string; message: string }
  | { type: 'cloud_rolled_back'; reason: string }
  | { type: 'cloud_cancelled'; reason: string }
  | { type: 'plugin_token_issued'; rawToken: string; deepLink: string };

export const PhaseOrder: PhaseName[] = [
  'minting_spaces',
  'tf_planning',
  'tf_applying',
  'dns_creating',
  'awaiting_cloud_callback',
  'cloud_registered',
  'awaiting_cert',
  'issuing_plugin_token',
];
