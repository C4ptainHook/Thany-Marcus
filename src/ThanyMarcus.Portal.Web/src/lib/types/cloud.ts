export type Provider = 'digitalocean';
export type JobKind = 'create' | 'destroy';
export type ProvisioningStatus =
  | 'pending' | 'tf_planning' | 'tf_applying' | 'dns_creating'
  | 'awaiting_cloud_callback' | 'awaiting_cert' | 'succeeded'
  | 'destroying' | 'rolling_back_dns' | 'rolling_back_tf'
  | 'rolled_back' | 'failed_tf' | 'failed_dns' | 'failed_callback'
  | 'failed_cert' | 'failed_destroy' | 'cancelled';

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
  currentJob: JobSummary | null;
  recentEvents: EventSummary[];
}
