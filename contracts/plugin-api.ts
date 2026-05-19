// Mirror of ThanyMarcus.Shared.PluginApi. camelCase wire shape.
// When the Obsidian plugin is scaffolded (PLUGIN-001), copy this file
// to the plugin repo's src/types/cloud-api.ts.

export type IsoInstant = string;
export type Uuid = string;

// ---------- Ingest ----------

export const AttachmentKind = {
  TextBody:    "text_body",
  TextSnippet: "text_snippet",
  Url:         "url",
  Image:       "image",
  Audio:       "audio",
  Pdf:         "pdf",
  Document:    "document",
  Video:       "video",
} as const;
export type AttachmentKind = typeof AttachmentKind[keyof typeof AttachmentKind];

export interface IngestInitAttachment {
  clientAttachmentId: string;
  kind: AttachmentKind | string;
  mimeType?: string;
  byteSize?: number;
  sha256?: string;
  filename?: string;
  extra: unknown;
}

export interface IngestInitRequest {
  clientNoteId: string;
  capturedAt: IsoInstant;
  body: string;
  attachments: IngestInitAttachment[];
}

export interface IngestInitUpload {
  clientAttachmentId: string;
  attachmentId: Uuid;
  uploadUrl: string;
  requiredHeaders: Record<string, string>;
  expiresAt: IsoInstant;
}

export interface IngestInitResponse {
  noteId: Uuid;
  uploads: IngestInitUpload[];
}

// ---------- Sync pull ----------

export interface SyncPullAttachment {
  attachmentId: Uuid;
  kind: AttachmentKind | string;
  filename?: string | null;
  mimeType?: string | null;
  byteSize?: number | null;
  sha256?: string | null;
  downloadUrl?: string | null;
  downloadUrlExpiresAt?: IsoInstant | null;
  extra: unknown;
}

export interface SyncPullItem {
  noteId: Uuid;
  relativePath: string;
  body: string;
  suggestedProject?: string | null;
  tags: string[];
  llmMode?: string | null;
  attachments: SyncPullAttachment[];
  updatedAt: IsoInstant;
  deleted: boolean;
  provenance?: unknown;
  status?: string;
  deletedAt?: IsoInstant;
}

export interface SyncPullProject {
  entityId: Uuid;
  canonicalName: string;
  aliases: string[];
  description?: string | null;
  vaultFolder?: string | null;
  isUserSource: boolean;
  updatedAt: IsoInstant;
  deletedAt?: IsoInstant;
}

export interface SyncPullResponse {
  items: SyncPullItem[];
  projects: SyncPullProject[];
  nextSince: IsoInstant | null;
}

// ---------- Sync push ----------

export interface SyncPushRequest {
  noteId: Uuid;
  body: string;
  baseUpdatedAt: IsoInstant;
  deleted?: boolean;
}

export interface SyncPushResponse {
  noteId: Uuid;
  updatedAt: IsoInstant;
  transitionVersion: number;
}

export const SyncPushConflictCode = {
  StaleBaseline: "stale_baseline",
} as const;
export type SyncPushConflictCode = typeof SyncPushConflictCode[keyof typeof SyncPushConflictCode];

export interface SyncPushConflict {
  code: SyncPushConflictCode | string;
  currentUpdatedAt: IsoInstant;
  currentTransitionVersion: number;
}

export interface SyncPushNotFound {
  code: "note_not_found";
}
