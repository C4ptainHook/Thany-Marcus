import { requestUrl, type RequestUrlParam } from "obsidian";

export interface IngestInitAttachmentDto {
  clientAttachmentId: string;
  kind: "image" | "voice" | "url" | "file";
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

export interface SyncPullProject {
  entityId: string;
  canonicalName: string;
  aliases: string[];
  description: string | null;
  vaultFolder: string | null;
  isUserSource: boolean;
  updatedAt: string;
  deletedAt?: string | null;
}

export interface SyncPullResponse {
  items: SyncPullItem[];
  projects: SyncPullProject[];
  nextSince: string | null;
}

export interface ProjectDto {
  id: string;
  name: string;
  description: string | null;
  mentionCount: number;
}

export interface ListProjectsResponse {
  projects: ProjectDto[];
}

export interface CreateProjectRequest {
  name: string;
  description?: string;
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

  async listProjects(): Promise<ProjectDto[]> {
    const res = await this.json<ListProjectsResponse>({
      url: `${this.base()}/api/projects`,
      method: "GET",
    });
    return res.projects;
  }

  async createProject(req: CreateProjectRequest): Promise<ProjectDto> {
    return this.json<ProjectDto>({
      url: `${this.base()}/api/projects`,
      method: "POST",
      contentType: "application/json",
      body: JSON.stringify(req),
    });
  }

  async deleteProject(id: string): Promise<void> {
    const res = await requestUrl({
      url: `${this.base()}/api/projects/${id}`,
      method: "DELETE",
      headers: this.authHeader(),
      throw: false,
    });
    if (res.status === 401) {
      this.onAuthFailure();
      throw new TokenRevokedError();
    }
    if (res.status < 200 || res.status >= 300) {
      throw new Error(`DELETE /api/projects/${id} → HTTP ${res.status}`);
    }
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
