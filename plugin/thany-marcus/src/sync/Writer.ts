import type { TFile, TFolder, Vault } from "obsidian";
import type { SyncPullItem } from "../api";
import { mapWithConcurrency } from "../concurrency";
import { AttachmentDownloader } from "./AttachmentDownloader";
import { AttachmentIndex } from "./AttachmentIndex";
import type { VaultEventGuard } from "./VaultEventGuard";
import {
  ensureFolder,
  joinPath,
  parentOf,
  sanitizeSegment,
} from "./pathUtils";

const DOWNLOAD_CONCURRENCY = 4;

export interface WriterResult {
  vaultPath: string;
  attachmentPaths: string[];
}

export class Writer {
  private readonly downloader: AttachmentDownloader;

  constructor(
    private readonly vault: Vault,
    private readonly vaultRoot: () => string,
    private readonly index: AttachmentIndex,
    private readonly guard: VaultEventGuard | null = null,
  ) {
    this.downloader = new AttachmentDownloader(vault);
  }

  async apply(item: SyncPullItem): Promise<WriterResult | null> {
    const root = sanitizeSegment(this.vaultRoot() || "Thany");
    const relParts = item.relativePath.split("/").map(sanitizeSegment).filter(Boolean);
    const vaultPath = joinPath(root, ...relParts);

    if (item.deleted) {
      await this.removeNote(vaultPath, item.noteId);
      return null;
    }

    const noteFolder = parentOf(vaultPath);
    await ensureFolder(this.vault, noteFolder);

    const attachmentsFolder = joinPath(noteFolder, "_attachments");
    await ensureFolder(this.vault, attachmentsFolder);

    const downloadables = item.attachments.filter(
      (att) => (att.kind || "").toLowerCase() !== "url" && !!att.downloadUrl,
    );

    const downloaded = await mapWithConcurrency(
      downloadables,
      DOWNLOAD_CONCURRENCY,
      async (att) => {
        const filename = att.filename
          ? sanitizeSegment(att.filename)
          : `${item.noteId.slice(0, 8)}-${att.attachmentId.slice(0, 8)}`;
        const destPath = joinPath(attachmentsFolder, filename);
        try {
          const result = await this.downloader.download(
            att.downloadUrl!,
            destPath,
            att.sha256,
          );
          return result.path;
        } catch (e) {
          console.error("Thany: attachment download failed", e);
          return null;
        }
      },
    );

    const attachmentPaths = downloaded.filter((p): p is string => p !== null);

    const previous = this.index.get(item.noteId);
    const toRemove = previous.filter((p) => !attachmentPaths.includes(p));
    for (const p of toRemove) await this.deletePath(p);
    this.index.set(item.noteId, attachmentPaths);

    // The cloud's synthesized body already contains the full frontmatter + ## Sources block,
    // including all attachment embeds. Write verbatim.
    const final = item.body ?? "";
    await this.writeFile(vaultPath, final);
    return { vaultPath, attachmentPaths };
  }

  private async writeFile(path: string, contents: string): Promise<void> {
    const existing = this.vault.getAbstractFileByPath(path);
    if (existing && "stat" in existing) {
      this.guard?.suppress("modify", path);
      await this.vault.modify(existing as TFile, contents);
    } else {
      this.guard?.suppress("create", path);
      await this.vault.create(path, contents);
    }
  }

  private async removeNote(vaultPath: string, noteId: string): Promise<void> {
    const f = this.vault.getAbstractFileByPath(vaultPath);
    if (f) {
      try {
        this.guard?.suppress("delete", vaultPath);
        await this.vault.delete(f);
      } catch (e) {
        console.warn("Thany: failed to delete note file", e);
      }
    }
    for (const p of this.index.delete(noteId)) {
      await this.deletePath(p);
    }
    const folder = parentOf(vaultPath);
    const attachments = joinPath(folder, "_attachments");
    await this.cleanupEmptyFolder(attachments);
  }

  private async deletePath(path: string): Promise<void> {
    const f = this.vault.getAbstractFileByPath(path);
    if (!f) return;
    try {
      this.guard?.suppress("delete", path);
      await this.vault.delete(f);
    } catch (e) {
      console.warn("Thany: failed to delete attachment", e);
    }
  }

  private async cleanupEmptyFolder(path: string): Promise<void> {
    const f = this.vault.getAbstractFileByPath(path) as TFolder | null;
    if (!f || !("children" in f)) return;
    if (f.children.length === 0) {
      try {
        this.guard?.suppress("delete", path);
        await this.vault.delete(f);
      } catch {
        /* ignore */
      }
    }
  }
}

