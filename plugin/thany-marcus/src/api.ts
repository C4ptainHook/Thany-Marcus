import { requestUrl, type RequestUrlParam } from "obsidian";

export interface IngestInitAttachmentDto {
  clientAttachmentId: string;
  kind: "image" | "voice" | "url" | "file";
  mode: "extract" | "reference" | "metadata";
  mimeType: string | null;
  byteSize: number | null;
  sha256: string | null;
  filename: string | null;
  extra: Record<string, unknown>;
}

export type PrivacyMode = "private" | "public";

export type PublicModel =
  | "gemini-2.5-flash-lite"
  | "gemini-2.5-flash"
  | "gemini-3.5-flash";

export type SynthesisPreset =
  | "zettelkasten"
  | "journal"
  | "encyclopedic"
  | "technical"
  | "custom";

export interface IngestInitRequest {
  clientNoteId: string;
  capturedAt: string;
  body: string;
  attachments: IngestInitAttachmentDto[];
  privacyMode: PrivacyMode;
  publicModel?: PublicModel;
  llmApiKey?: string;
  synthesisPreset: SynthesisPreset;
  customPrompt?: string;
}

export interface IngestInitUpload {
  clientAttachmentId: string;
  attachmentId: string;
  uploadUrl: string;
  requiredHeaders: Record<string, string>;
  expiresAt: string;
}

export interface IngestInitResponse {
  noteId: string;
  uploads: IngestInitUpload[];
}

export interface IngestFinalizeUploadedAttachment {
  attachmentId: string;
  sha256: string;
  byteSize: number;
}

export interface IngestFinalizeResponse {
  noteId: string;
  status: string;
}

export interface SyncPullAttachment {
  attachmentId: string;
  kind: string;
  filename: string | null;
  mimeType: string | null;
  byteSize: number | null;
  sha256: string | null;
  downloadUrl: string | null;
  downloadUrlExpiresAt: string | null;
  extra: Record<string, unknown> | null;
}

export interface ExtractionFailure {
  kind: "image" | "voice" | "url" | "file";
  attachmentId: string;
  reason: string;
  soft: boolean;
}

export interface SyncPullItem {
  noteId: string;
  relativePath: string;
  body: string;
  suggestedProject: string | null;
  tags: string[];
  llmMode: string | null;
  attachments: SyncPullAttachment[];
  updatedAt: string;
  deleted?: boolean;
  status?: string | null;
  deletedAt?: string | null;
  provenance?: unknown;
  extractionFailures?: ExtractionFailure[];
}

export interface SyncPullResponse {
  items: SyncPullItem[];
  nextSince: string | null;
}

export interface RelatedNotesItem {
  id: string;
  relativePath: string;
  title: string;
  snippet: string;
  distance: number;
}

interface RelatedNotesResponse {
  items: RelatedNotesItem[];
}

export interface RelatedNotesRequest {
  body?: string;
  noteId?: string;
  k?: number;
}

export interface IngestJobDto {
  noteId: string;
  title: string;
  status: string;
  attemptCount: number;
  error: string | null;
  vaultPath: string | null;
  extractionFailures: { kind: string; reason: string }[];
}

export interface ListJobsResponse {
  active: IngestJobDto[];
  recent: IngestJobDto[];
}

export interface EntitySuggestionOccurrence {
  noteId: string;
  anchorText: string;
  surroundingText: string;
}

export interface EntitySuggestion {
  id: string;
  canonicalText: string;
  kind: string;
  aliases: string[];
  occurrenceCount: number;
  distinctNoteCount: number;
  firstSeenAt: string;
  lastSeenAt: string;
  sampleOccurrence?: EntitySuggestionOccurrence | null;
}

interface ListEntitySuggestionsResponse {
  suggestions: EntitySuggestion[];
}

export interface EditEntitySuggestionRequest {
  canonicalText?: string;
  aliases?: string[];
}

export type AcceptEntitySuggestionResult =
  | { ok: true; entityId: string }
  | { ok: false; conflict: "path" | "rename"; existingKind: string };

export class RelatedNotesAbortedError extends Error {
  constructor() {
    super("related notes request aborted");
    this.name = "RelatedNotesAbortedError";
  }
}

export class TokenRevokedError extends Error {
  constructor() {
    super("Plugin token revoked or invalid");
    this.name = "TokenRevokedError";
  }
}

export class ApiClient {
  constructor(
    private readonly cloudUrl: () => string,
    private readonly token: () => string,
    private readonly onAuthFailure: () => void,
  ) {}

  private base(): string {
    return this.cloudUrl().replace(/\/+$/, "");
  }

  private authHeader(): Record<string, string> {
    return { Authorization: `Bearer ${this.token()}` };
  }

  async health(): Promise<void> {
    const res = await requestUrl({
      url: `${this.base()}/health/ready`,
      method: "GET",
      throw: false,
    });
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`Health check failed: HTTP ${res.status}`);
    }
  }

  async ingestInit(req: IngestInitRequest): Promise<IngestInitResponse> {
    return this.json<IngestInitResponse>({
      url: `${this.base()}/api/ingest/init`,
      method: "POST",
      contentType: "application/json",
      body: JSON.stringify(req),
    });
  }

  async ingestFinalize(
    noteId: string,
    uploaded: IngestFinalizeUploadedAttachment[],
  ): Promise<IngestFinalizeResponse> {
    return this.json<IngestFinalizeResponse>({
      url: `${this.base()}/api/ingest/${noteId}/finalize`,
      method: "POST",
      contentType: "application/json",
      body: JSON.stringify({ uploaded }),
    });
  }

  async syncPull(
    since: string | null,
    limit: number,
    includeProvenance = false,
  ): Promise<SyncPullResponse> {
    const params = new URLSearchParams();
    if (since) params.set("since", since);
    params.set("limit", String(limit));
    if (includeProvenance) params.set("include", "provenance");
    return this.json<SyncPullResponse>({
      url: `${this.base()}/api/sync/pull?${params.toString()}`,
      method: "GET",
    });
  }

  async deleteNote(noteId: string): Promise<void> {
    const res = await requestUrl({
      url: `${this.base()}/api/notes/${noteId}`,
      method: "DELETE",
      headers: this.authHeader(),
      throw: false,
    });
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`DELETE /api/notes/${noteId} failed: HTTP ${res.status}`);
    }
  }

  async cancelIngest(noteId: string): Promise<{ ok: true } | { ok: false; conflict: true; reason: string }> {
    const res = await requestUrl({
      url: `${this.base()}/api/ingest/${noteId}/cancel`,
      method: "POST",
      headers: this.authHeader(),
      throw: false,
    });
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status === 204) return { ok: true };
    if (res.status === 409) {
      let reason = "already terminal";
      try {
        const body = res.json as { detail?: string; title?: string } | undefined;
        reason = body?.detail ?? body?.title ?? reason;
      } catch { /* ignore */ }
      return { ok: false, conflict: true, reason };
    }
    if (res.status === 404) {
      return { ok: false, conflict: true, reason: "note not found" };
    }
    throw new Error(`cancelIngest failed: HTTP ${res.status}`);
  }

  async relatedNotes(
    req: RelatedNotesRequest,
    signal?: AbortSignal,
  ): Promise<RelatedNotesItem[]> {
    let res: Response;
    try {
      res = await fetch(`${this.base()}/api/notes/related`, {
        method: "POST",
        headers: {
          ...this.authHeader(),
          "Content-Type": "application/json",
        },
        body: JSON.stringify(req),
        signal,
      });
    } catch (e) {
      if ((e as { name?: string }).name === "AbortError") {
        throw new RelatedNotesAbortedError();
      }
      throw e;
    }
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status === 400 || res.status === 404) {
      return [];
    }
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`POST /api/notes/related → HTTP ${res.status}`);
    }
    const data = (await res.json()) as RelatedNotesResponse;
    return data.items ?? [];
  }

  async listActiveJobs(includeRecent = true): Promise<ListJobsResponse> {
    const qs = includeRecent ? "status=active&include=recent" : "status=active";
    return this.json<ListJobsResponse>({
      url: `${this.base()}/api/ingest/jobs?${qs}`,
      method: "GET",
    });
  }

  async reprocess(noteId: string): Promise<void> {
    const res = await requestUrl({
      url: `${this.base()}/api/notes/${noteId}/reprocess`,
      method: "POST",
      headers: this.authHeader(),
      throw: false,
    });
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`reprocess failed: HTTP ${res.status}`);
    }
  }

  async listEntitySuggestions(signal?: AbortSignal): Promise<EntitySuggestion[]> {
    let res: Response;
    try {
      res = await fetch(`${this.base()}/api/entity-suggestions`, {
        method: "GET",
        headers: this.authHeader(),
        signal,
      });
    } catch (e) {
      if ((e as { name?: string }).name === "AbortError") return [];
      throw e;
    }
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`GET /api/entity-suggestions → HTTP ${res.status}`);
    }
    const data = (await res.json()) as ListEntitySuggestionsResponse;
    return data.suggestions ?? [];
  }

  async acceptEntitySuggestion(id: string): Promise<AcceptEntitySuggestionResult> {
    const res = await requestUrl({
      url: `${this.base()}/api/entity-suggestions/${id}/accept`,
      method: "POST",
      headers: this.authHeader(),
      throw: false,
    });
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status >= 200 && res.status < 300) {
      const body = res.json as { entityId: string };
      return { ok: true, entityId: body.entityId };
    }
    if (res.status === 409) {
      const body = res.json as { conflict?: "path" | "rename"; existingKind?: string } | undefined;
      return {
        ok: false,
        conflict: body?.conflict ?? "path",
        existingKind: body?.existingKind ?? "",
      };
    }
    throw new Error(`accept entity-suggestion ${id} → HTTP ${res.status}`);
  }

  async dismissEntitySuggestion(id: string): Promise<void> {
    const res = await requestUrl({
      url: `${this.base()}/api/entity-suggestions/${id}/dismiss`,
      method: "POST",
      headers: this.authHeader(),
      throw: false,
    });
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`dismiss entity-suggestion ${id} → HTTP ${res.status}`);
    }
  }

  async editEntitySuggestion(id: string, patch: EditEntitySuggestionRequest): Promise<void> {
    const res = await requestUrl({
      url: `${this.base()}/api/entity-suggestions/${id}/edit`,
      method: "POST",
      headers: this.authHeader(),
      contentType: "application/json",
      body: JSON.stringify(patch),
      throw: false,
    });
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`edit entity-suggestion ${id} → HTTP ${res.status}`);
    }
  }

  private async json<T>(req: RequestUrlParam): Promise<T> {
    const headers: Record<string, string> = {
      ...this.authHeader(),
      ...(req.headers ?? {}),
    };
    if (req.contentType) headers["Content-Type"] = req.contentType;
    const res = await requestUrl({ ...req, headers, throw: false });
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`${req.method ?? "GET"} ${req.url} → HTTP ${res.status}: ${res.text}`);
    }
    return res.json as T;
  }
}
