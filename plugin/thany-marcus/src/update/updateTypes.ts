export type UpdateStrategy = "in-place" | "blue-green";

export interface ReleaseDescriptor {
  version: string;
  strategy: UpdateStrategy;
  compose_yaml: string;
  image_digests: Record<string, string>;
  model_tags: Record<string, string>;
  env_overlay: Record<string, string>;
  schema_min_from: string;
  notes: string | null;
  released_at: string;
}

export interface CloudHealth {
  cert_ready: boolean;
  cloud_id: string;
  registration_status: string;
  api_version: string;
  current_version: string;
}

export interface CloudUpdateStatus {
  phase: string;
  current_version: string;
  target_version: string | null;
  message: string | null;
  updated_at: string | null;
}

export interface ApplyUpdateResponse {
  accepted: boolean;
  phase: string;
  target_version: string;
  message: string;
}

export interface RedeemRecoveryResponse {
  token: string;
  recovery_code: string;
}

export interface UpdateAvailability {
  available: boolean;
  currentVersion: string;
  targetVersion: string | null;
  strategy: UpdateStrategy | null;
  costText: string | null;
}
