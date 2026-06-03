import type { TFile, TFolder, Vault, Workspace } from "obsidian";
import { sha256Hex } from "../sync/AttachmentDownloader";
import { ensureFolder, joinPath } from "../sync/pathUtils";
import { hasId3Tags } from "./Id3Sniffer";

export type AttachmentMode = "extract" | "reference" | "metadata";

export interface DraftAttachment {
  clientAttachmentId: string;
  kind: "image" | "voice" | "url" | "file";
  mode: AttachmentMode;
  filename: string | null;
  mimeType: string | null;
  byteSize: number | null;
  sha256: string | null;
  vaultPath: string | null;
  extra: Record<string, unknown>;
}

export function classifyKind(mime: string | null): "image" | "voice" | "file" {
  if (mime?.startsWith("image/")) return "image";
  if (mime?.startsWith("audio/")) return "voice";
  return "file";
}

export function defaultModeFor(
  kind: "image" | "voice" | "url" | "file",
  mime: string | null,
  bytes?: Uint8Array,
): AttachmentMode {
  if (kind === "file" && mime?.startsWith("video/")) return "reference";
  if (kind === "voice" && bytes && hasId3Tags(bytes)) return "reference";
  return "extract";
}

export function nextMode(current: AttachmentMode, kind: string): AttachmentMode {
  if (kind === "url") {
    return current === "extract" ? "metadata" : current === "metadata" ? "reference" : "extract";
  }
  return current === "extract" ? "reference" : "extract";
}

export interface DraftState {
  draftId: string;
  filePath: string;
  folderPath: string;
  attachmentsFolderPath: string;
  attachments: DraftAttachment[];
}

const DRAFTS_SUBPATH = "_drafts";

export class DraftManager {
  private readonly drafts = new Map<string, DraftState>();

  constructor(
    private readonly vault: Vault,
    private readonly workspace: Workspace,
    private readonly vaultRoot: () => string,
  ) {}

  isDraftPath(path: string): boolean {
    const root = this.vaultRoot() || "Thany";
    return path.startsWith(`${root}/${DRAFTS_SUBPATH}/`);
  }

  getByPath(path: string): DraftState | null {
    for (const d of this.drafts.values()) if (d.filePath === path) return d;
    return null;
  }

  async hydrateFromFile(file: TFile): Promise<DraftState> {
    const draftId = file.basename;
    const folderPath = joinPath(this.vaultRoot() || "Thany", DRAFTS_SUBPATH, draftId);
    const attachmentsFolderPath = joinPath(folderPath, "_attachments");
    const state: DraftState = {
      draftId,
      filePath: file.path,
      folderPath,
      attachmentsFolderPath,
      attachments: [],
    };
    await this.rescanAttachmentsFolder(state);
    this.drafts.set(draftId, state);
    return state;
  }

  private async rescanAttachmentsFolder(state: DraftState): Promise<void> {
    const folder = this.vault.getAbstractFileByPath(state.attachmentsFolderPath) as TFolder | null;
    if (!folder || !("children" in folder)) return;
    const files = folder.children.filter((c): c is TFile => "stat" in c);
    for (const f of files) {
      const bytes = new Uint8Array(await this.vault.readBinary(f));
      const sha = await sha256Hex(bytes);
      const mimeType = guessMimeType(f.extension);
      const kind = classifyKind(mimeType);
      state.attachments.push({
        clientAttachmentId: `${kind}-${state.attachments.length + 1}-${shortId()}`,
        kind,
        mode: defaultModeFor(kind, mimeType, bytes),
        filename: f.name,
        mimeType,
        byteSize: bytes.byteLength,
        sha256: sha,
        vaultPath: f.path,
        extra: {},
      });
    }
  }

  async create(): Promise<DraftState> {
    const root = this.vaultRoot() || "Thany";
    const draftsFolder = joinPath(root, DRAFTS_SUBPATH);
    await ensureFolder(this.vault, draftsFolder);

    const draftId = newDraftId();
    const folderPath = joinPath(draftsFolder, draftId);
    const attachmentsFolderPath = joinPath(folderPath, "_attachments");
    const filePath = joinPath(draftsFolder, `${draftId}.md`);

    await ensureFolder(this.vault, folderPath);
    await this.vault.create(filePath, "");

    const state: DraftState = {
      draftId,
      filePath,
      folderPath,
      attachmentsFolderPath,
      attachments: [],
    };
    this.drafts.set(draftId, state);
    return state;
  }

  async openInNewLeaf(state: DraftState): Promise<TFile | null> {
    const file = this.vault.getAbstractFileByPath(state.filePath) as TFile | null;
    if (!file) return null;
    const leaf = this.workspace.getLeaf("tab");
    await leaf.openFile(file);
    return file;
  }

  async addBinaryAttachment(
    state: DraftState,
    bytes: ArrayBuffer,
    kind: "image" | "voice" | "file",
    filename: string,
    mimeType: string | null,
    sha256: string,
  ): Promise<DraftAttachment> {
    await ensureFolder(this.vault, state.attachmentsFolderPath);
    const safeName = await uniqueName(this.vault, state.attachmentsFolderPath, filename);
    const dest = joinPath(state.attachmentsFolderPath, safeName);
    await this.vault.createBinary(dest, bytes);
    const att: DraftAttachment = {
      clientAttachmentId: `${kind}-${state.attachments.length + 1}-${shortId()}`,
      kind,
      mode: defaultModeFor(kind, mimeType, new Uint8Array(bytes)),
      filename: safeName,
      mimeType,
      byteSize: bytes.byteLength,
      sha256,
      vaultPath: dest,
      extra: {},
    };
    state.attachments.push(att);
    return att;
  }

  addUrlAttachment(state: DraftState, url: string): DraftAttachment {
    const att: DraftAttachment = {
      clientAttachmentId: `url-${state.attachments.length + 1}-${shortId()}`,
      kind: "url",
      mode: defaultModeFor("url", null),
      filename: null,
      mimeType: null,
      byteSize: null,
      sha256: null,
      vaultPath: null,
      extra: { url },
    };
    state.attachments.push(att);
    return att;
  }

  async removeAttachment(state: DraftState, clientAttachmentId: string): Promise<void> {
    const idx = state.attachments.findIndex((a) => a.clientAttachmentId === clientAttachmentId);
    if (idx < 0) return;
    const [att] = state.attachments.splice(idx, 1);
    if (att.vaultPath) {
      const f = this.vault.getAbstractFileByPath(att.vaultPath);
      if (f) {
        try {
          await this.vault.delete(f);
        } catch {
          /* ignore */
        }
      }
    }
  }

  async discard(state: DraftState): Promise<void> {
    this.drafts.delete(state.draftId);
    const folder = this.vault.getAbstractFileByPath(state.folderPath) as TFolder | null;
    if (folder) {
      try {
        await this.vault.delete(folder, true);
      } catch {
        /* ignore */
      }
    }
    const file = this.vault.getAbstractFileByPath(state.filePath);
    if (file) {
      try {
        await this.vault.delete(file);
      } catch {
        /* ignore */
      }
    }
  }
}

function newDraftId(): string {
  const d = new Date();
  const pad = (n: number, w = 2) => n.toString().padStart(w, "0");
  return (
    `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-` +
    `${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}-${shortId()}`
  );
}

function shortId(): string {
  return Math.random().toString(36).slice(2, 8);
}

function guessMimeType(ext: string): string | null {
  const e = ext.toLowerCase();
  if (["png", "jpg", "jpeg", "gif", "webp", "bmp", "svg"].includes(e)) {
    return e === "jpg" ? "image/jpeg" : `image/${e}`;
  }
  if (["webm", "ogg", "mp3", "wav", "m4a", "flac", "opus"].includes(e)) {
    if (e === "mp3") return "audio/mpeg";
    if (e === "m4a") return "audio/mp4";
    return `audio/${e}`;
  }
  return null;
}

async function uniqueName(vault: Vault, folder: string, desired: string): Promise<string> {
  const parts = desired.split(".");
  const ext = parts.length > 1 ? `.${parts.pop()}` : "";
  const stem = parts.join(".") || "attachment";
  let name = `${stem}${ext}`;
  let i = 1;
  while (vault.getAbstractFileByPath(joinPath(folder, name))) {
    i += 1;
    name = `${stem}-${i}${ext}`;
  }
  return name;
}
