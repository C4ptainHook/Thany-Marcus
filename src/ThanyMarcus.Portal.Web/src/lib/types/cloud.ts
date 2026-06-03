export type Provider = 'digitalocean';
export type JobKind = 'create' | 'destroy';
export type ProvisioningStatus =
  | 'pending' | 'minting_spaces' | 'tf_planning' | 'tf_applying' | 'dns_creating'
  | 'awaiting_cloud_callback' | 'awaiting_cert' | 'issuing_plugin_token' | 'succeeded'
  | 'destroying' | 'rolling_back_dns' | 'rolling_back_tf'
  | 'rolled_back' | 'failed_tf' | 'failed_dns' | 'failed_callback'
  | 'failed_cert' | 'failed_plugin_token' | 'failed_destroy' | 'cancelled';

export type WorkerState = 'warm' | 'waking' | 'idle';

export interface JobSummary {
  jobId: string;
  kind: JobKind | string;
  status: ProvisioningStatus | string;
}

export interface EventSummary {
  phase: string;
  event: string | null;
  error: string | null;
  timestamp: string;
}

export interface CloudStatusResponse {
  cloudId: string;
  hostname: string;
  provider: Provider | string;
  region: string;
  provisioningStatus: ProvisioningStatus | string;
  succeededAt: string | null;
  destroyedAt: string | null;
  cancelRequestedAt: string | null;
  currentJob: JobSummary | null;
  recentEvents: EventSummary[];
  priceMonthlyUsd: number | null;
  priceHourlyUsd: number | null;
  priceCurrency: string | null;
  pricedAt: string | null;
}

export interface CloudHealthz {
  status: string;
  version?: string;
  workerState?: WorkerState;
  workerLastActiveAt?: string | null;
  workerUptimeMonthSeconds?: number | null;
}

export const IN_FLIGHT_CREATE_STATUSES: ProvisioningStatus[] = [
  'pending',
  'minting_spaces',
  'tf_planning',
  'tf_applying',
  'dns_creating',
  'awaiting_cloud_callback',
  'awaiting_cert',
  'issuing_plugin_token',
];

export const IN_FLIGHT_DESTROY_STATUSES: ProvisioningStatus[] = [
  'destroying',
  'rolling_back_dns',
  'rolling_back_tf',
];

export const FAILED_STATUSES: ProvisioningStatus[] = [
  'failed_tf',
  'failed_dns',
  'failed_callback',
  'failed_cert',
  'failed_plugin_token',
  'failed_destroy',
];

export function isInFlight(s: string): boolean {
  return (IN_FLIGHT_CREATE_STATUSES as string[]).includes(s)
      || (IN_FLIGHT_DESTROY_STATUSES as string[]).includes(s);
}
export function isFailed(s: string): boolean {
  return (FAILED_STATUSES as string[]).includes(s);
}
export function isTerminalEmpty(s: string): boolean {
  return s === 'rolled_back' || s === 'cancelled';
}
