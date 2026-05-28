import { isTerminal, phaseLabel } from "./labels";

export interface QueueExtractionFailure {
  kind: string;
  attachmentId: string;
  reason: string;
}

export interface QueueEntry {
  noteId: string;
  draftLeafId: string | null;
  title: string;
  status: string;
  label: string;
  submittedAt: number;
  updatedAt: number;
  error?: string;
  vaultPath?: string;
  extractionFailures?: QueueExtractionFailure[];
}

type Listener = () => void;

export class QueueStore {
  private readonly entries = new Map<string, QueueEntry>();
  private readonly listeners = new Set<Listener>();

  list(): QueueEntry[] {
    return Array.from(this.entries.values()).sort((a, b) => b.updatedAt - a.updatedAt);
  }

  get(noteId: string): QueueEntry | undefined {
    return this.entries.get(noteId);
  }

  subscribe(l: Listener): () => void {
    this.listeners.add(l);
    return () => this.listeners.delete(l);
  }

  upsert(noteId: string, patch: Partial<QueueEntry> & { status?: string }): void {
    const now = Date.now();
    const existing = this.entries.get(noteId);
    const next: QueueEntry = existing
      ? { ...existing, ...patch, updatedAt: now, label: phaseLabel(patch.status ?? existing.status) }
      : {
          noteId,
          draftLeafId: patch.draftLeafId ?? null,
          title: patch.title ?? noteId.slice(0, 8),
          status: patch.status ?? "queued",
          label: phaseLabel(patch.status ?? "queued"),
          submittedAt: now,
          updatedAt: now,
          error: patch.error,
          vaultPath: patch.vaultPath,
        };
    this.entries.set(noteId, next);
    this.emit();
  }

  markReady(noteId: string, vaultPath: string, failures?: QueueExtractionFailure[]): void {
    const e = this.entries.get(noteId);
    if (!e) return;
    e.status = "ready";
    e.label = phaseLabel("ready");
    e.vaultPath = vaultPath;
    e.extractionFailures = failures && failures.length > 0 ? failures : undefined;
    e.updatedAt = Date.now();
    this.emit();
  }

  markFailed(noteId: string, error: string): void {
    const e = this.entries.get(noteId);
    if (!e) {
      this.upsert(noteId, { status: "failed", error });
      return;
    }
    e.status = "failed";
    e.label = phaseLabel("failed");
    e.error = error;
    e.updatedAt = Date.now();
    this.emit();
  }

  remove(noteId: string): void {
    if (this.entries.delete(noteId)) this.emit();
  }

  pruneCompleted(maxAgeMs: number): void {
    const cutoff = Date.now() - maxAgeMs;
    let changed = false;
    for (const [id, e] of this.entries) {
      if (isTerminal(e.status) && e.updatedAt < cutoff) {
        this.entries.delete(id);
        changed = true;
      }
    }
    if (changed) this.emit();
  }

  private emit(): void {
    for (const l of this.listeners) l();
  }
}
