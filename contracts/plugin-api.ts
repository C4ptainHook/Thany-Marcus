// Mirror of ThanyMarcus.Shared.PluginApi. Snake_case wire shape.
// When the Obsidian plugin is scaffolded (PLUGIN-001), copy this file
// to the plugin repo's src/types/cloud-api.ts.

export type IsoInstant = string;
export type Uuid = string;

export const IngestPartKind = {
  Url:   "url",
  Text:  "text",
  Image: "image",
  Audio: "audio",
  Pdf:   "pdf",
} as const;
export type IngestPartKind = typeof IngestPartKind[keyof typeof IngestPartKind];

export interface IngestCompositePart {
  part_id: string;
  kind: IngestPartKind;
  inline_value?: string;
  multipart_name?: string;
  filename?: string;
  captured_at?: IsoInstant;
}

export interface IngestCompositeManifest {
  draft_id: Uuid;
  body_markdown: string;
  parts: IngestCompositePart[];
  client_timestamp: IsoInstant;
  vault_hint?: string;
}

export const IngestStatus = { Accepted: "accepted" } as const;
export type IngestStatus = typeof IngestStatus[keyof typeof IngestStatus];

export interface IngestResponse {
  artifact_id: Uuid;
  draft_id: Uuid;
  status: IngestStatus;
  received_at: IsoInstant;
}

export const IngestErrorCode = {
  ProcessorFailed:   "processor_failed",
  WorkerUnavailable: "worker_unavailable",
  InvalidManifest:   "invalid_manifest",
  UnsupportedKind:   "unsupported_kind",
  PayloadTooLarge:   "payload_too_large",
} as const;
export type IngestErrorCode = typeof IngestErrorCode[keyof typeof IngestErrorCode];

export interface IngestError {
  code: IngestErrorCode;
  message: string;
  part_id?: string;
}

export const WorkerState = {
  Idle:     "idle",
  Spawning: "spawning",
  Warm:     "warm",
} as const;
export type WorkerState = typeof WorkerState[keyof typeof WorkerState];

export const SyncItemStatus = {
  Processing: "processing",
  Done:       "done",
  Failed:     "failed",
} as const;
export type SyncItemStatus = typeof SyncItemStatus[keyof typeof SyncItemStatus];

export interface ProcessedAsset {
  vault_path: string;
  download_url: string;
  sha256: string;
  size_bytes: number;
}

export interface ProcessedNote {
  vault_path: string;
  frontmatter_yaml: string;
  body_markdown: string;
  assets: ProcessedAsset[];
}

export interface SyncPullItem {
  artifact_id: Uuid;
  draft_id: Uuid;
  status: SyncItemStatus;
  processed_note?: ProcessedNote;
  error?: IngestError;
  updated_at: IsoInstant;
}

export interface SyncPullResponse {
  items: SyncPullItem[];
  worker_state: WorkerState;
  server_timestamp: IsoInstant;
}
