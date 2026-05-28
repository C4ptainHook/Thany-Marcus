import type { Vault } from "obsidian";
import { ensureFolder } from "./pathUtils";

export class AttachmentDownloader {
  constructor(private readonly vault: Vault) {}

  async download(
    url: string,
    destPath: string,
    expectedSha256: string | null,
  ): Promise<{ path: string; bytes: number }> {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`download ${url} → HTTP ${res.status}`);
    const buf = new Uint8Array(await res.arrayBuffer());

    if (expectedSha256) {
      const actual = await sha256Hex(buf);
      if (actual !== expectedSha256.toLowerCase()) {
        throw new Error(
          `sha256 mismatch at ${destPath}: expected ${expectedSha256}, got ${actual}`,
        );
      }
    }

    await ensureFolder(this.vault, parentOf(destPath));

    const existing = this.vault.getAbstractFileByPath(destPath);
    if (existing && "stat" in existing) {
      await this.vault.modifyBinary(existing as never, buf.buffer as ArrayBuffer);
    } else {
      await this.vault.createBinary(destPath, buf.buffer as ArrayBuffer);
    }
    return { path: destPath, bytes: buf.byteLength };
  }
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes as unknown as BufferSource);
  const arr = Array.from(new Uint8Array(digest));
  return arr.map((b) => b.toString(16).padStart(2, "0")).join("");
}

function parentOf(path: string): string {
  const i = path.lastIndexOf("/");
  return i < 0 ? "" : path.slice(0, i);
}
