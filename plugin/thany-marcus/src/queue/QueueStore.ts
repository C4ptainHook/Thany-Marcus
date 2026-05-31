import { isTerminal, phaseLabel } from "./labels";

export interface QueueExtractionFailure {
  kind: string;
  attachmentId: string;
  reason: string;
}

export interface HydrationJob {
  noteId: string;
  title: string;
  status: string;
  error: string | null;
  vaultPath: string | null;
  extractionFailures: { kind: string; reason: string }[];
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
type TerminalListener = (prev: QueueEntry, next: QueueEntry) => void;

export class QueueStore {
  private readonly entries = new Map<string, QueueEntry>();
  private readonly listeners = new Set<Listener>();
  private terminalListener: TerminalListener | null = null;

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

  onTerminalTransition(l: TerminalListener): void {
    this.terminalListener = l;
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
    this.maybeFireTerminal(existing, next);
    this.emit();
  }

  markReady(noteId: string, vaultPath: string, failures?: QueueExtractionFailure[]): void {
    const e = this.entries.get(noteId);
    if (!e) return;
    const prev: QueueEntry = { ...e };
    e.status = "ready";
    e.label = phaseLabel("ready");
    e.vaultPath = vaultPath;
    e.extractionFailures = failures && failures.length > 0 ? failures : undefined;
    e.updatedAt = Date.now();
    this.maybeFireTerminal(prev, e);
    this.emit();
  }

  markFailed(noteId: string, error: string): void {
    const e = this.entries.get(noteId);
    if (!e) {
      this.upsert(noteId, { status: "failed", error });
      return;
    }
    const prev: QueueEntry = { ...e };
    e.status = "failed";
    e.label = phaseLabel("failed");
    e.error = error;
    e.updatedAt = Date.now();
    this.maybeFireTerminal(prev, e);
    this.emit();
  }

  private maybeFireTerminal(prev: QueueEntry | undefined, next: QueueEntry): void {
    if (!this.terminalListener) return;
    if (!prev) return;
    if (isTerminal(prev.status)) return;
    if (!isTerminal(next.status)) return;
    try { this.terminalListener(prev, next); } catch { /* never let listener errors break the store */ }
  }

  hydrate(jobs: HydrationJob[]): void {
    let changed = false;
    const now = Date.now();
    for (const job of jobs) {
      if (this.entries.has(job.noteId)) continue;
      const status = job.status === "succeeded" ? "ready" : job.status;
      const failures = job.extractionFailures.length > 0
        ? job.extractionFailures.map((f, i) => ({
            kind: f.kind,
            attachmentId: `${job.noteId}:${i}`,
            reason: f.reason,
          }))
        : undefined;
      this.entries.set(job.noteId, {
        noteId: job.noteId,
        draftLeafId: null,
        title: job.title,
        status,
        label: phaseLabel(status),
        submittedAt: now,
        updatedAt: now,
        error: job.error ?? undefined,
        vaultPath: job.vaultPath ?? undefined,
        extractionFailures: failures,
      });
      changed = true;
    }
    if (changed) this.emit();
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
