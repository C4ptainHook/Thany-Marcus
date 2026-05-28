import type { Vault, TAbstractFile } from "obsidian";

const RESERVED_CHARS = /[<>:"\\|?*\x00-\x1f]/g;

export function sanitizeSegment(segment: string): string {
  return segment.replace(RESERVED_CHARS, "_").trim() || "_";
}

export function joinPath(...parts: string[]): string {
  return parts
    .flatMap((p) => p.split("/"))
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
    .join("/");
}

export async function ensureFolder(vault: Vault, path: string): Promise<void> {
  if (!path) return;
  const existing: TAbstractFile | null = vault.getAbstractFileByPath(path);
  if (existing) return;
  try {
    await vault.createFolder(path);
  } catch (e) {
    if (!vault.getAbstractFileByPath(path)) throw e;
  }
}

export function parentOf(path: string): string {
  const i = path.lastIndexOf("/");
  return i < 0 ? "" : path.slice(0, i);
}

export function basenameOf(path: string): string {
  const i = path.lastIndexOf("/");
  return i < 0 ? path : path.slice(i + 1);
}

export function stripExtension(filename: string): string {
  const i = filename.lastIndexOf(".");
  return i <= 0 ? filename : filename.slice(0, i);
}
