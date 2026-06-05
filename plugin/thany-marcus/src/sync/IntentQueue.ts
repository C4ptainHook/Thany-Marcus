import type { Intent, IntentJournal } from "./IntentJournal";

export interface FolderDissolveResult {
  affectedCount: number;
  desired: { noteId: string; relativePath: string }[];
}

export interface IntentExecutor {
  tombstone(noteId: string): Promise<void>;
  revive(noteId: string): Promise<void>;
  reroute(noteId: string, relativePath: string): Promise<void>;
  folderDissolve(
    folder: string,
    mode: "reroute" | "force_delete",
    targetFolder: string | undefined,
  ): Promise<void>;
  folderRegister(folder: string): Promise<void>;
  folderUnregister(folder: string): Promise<void>;
  inboxReroute(): Promise<void>;
}

const BASE_BACKOFF_MS = 2_000;
const MAX_BACKOFF_MS = 5 * 60_000;

type ScheduleFn = (fn: () => void, ms: number) => number;
type ClearFn = (handle: number) => void;

// Drains pending intents against the (idempotent) cloud endpoints. Stable op-ids + idempotent
// endpoints make every retry a no-op, so a resumed intent is safe to replay. Failures stay in the
// journal and back off; they are removed only once the cloud acks.
export class IntentQueue {
  private draining = false;
  private retryTimer: number | null = null;
  private readonly attempts = new Map<string, number>();
  private destroyed = false;

  constructor(
    private readonly journal: IntentJournal,
    private readonly exec: IntentExecutor,
    private readonly schedule: ScheduleFn = (fn, ms) => window.setTimeout(fn, ms),
    private readonly clearTimer: ClearFn = (h) => window.clearTimeout(h),
  ) {}

  async enqueue(intent: Intent): Promise<void> {
    await this.journal.enqueue(intent);
    this.kick();
  }

  kick(): void {
    if (!this.destroyed) void this.drain();
  }

  async drain(): Promise<void> {
    if (this.draining || this.destroyed) return;
    this.draining = true;
    try {
      let anyFailed = false;
      for (const intent of this.journal.pending()) {
        if (this.destroyed) break;
        try {
          await this.run(intent);
          await this.journal.ack(intent.opId);
          this.attempts.delete(intent.opId);
        } catch (e) {
          anyFailed = true;
          this.attempts.set(intent.opId, (this.attempts.get(intent.opId) ?? 0) + 1);
          console.warn(`Thany: intent ${intent.kind} (${intent.opId}) failed, will retry`, e);
        }
      }
      if (anyFailed) this.scheduleRetry();
    } finally {
      this.draining = false;
    }
  }

  private scheduleRetry(): void {
    if (this.retryTimer !== null || this.destroyed) return;
    const maxAttempts = this.attempts.size === 0 ? 0 : Math.max(...this.attempts.values());
    const delay = Math.min(MAX_BACKOFF_MS, BASE_BACKOFF_MS * 2 ** Math.min(maxAttempts, 8));
    this.retryTimer = this.schedule(() => {
      this.retryTimer = null;
      void this.drain();
    }, delay);
  }

  private run(intent: Intent): Promise<void> {
    switch (intent.kind) {
      case "tombstone":
        return this.exec.tombstone(intent.noteId);
      case "revive":
        return this.exec.revive(intent.noteId);
      case "reroute":
        return this.exec.reroute(intent.noteId, intent.relativePath);
      case "folder_dissolve":
        return this.exec.folderDissolve(intent.folder, intent.mode, intent.targetFolder);
      case "folder_register":
        return this.exec.folderRegister(intent.folder);
      case "folder_unregister":
        return this.exec.folderUnregister(intent.folder);
      case "inbox_reroute":
        return this.exec.inboxReroute();
    }
  }

  destroy(): void {
    this.destroyed = true;
    if (this.retryTimer !== null) {
      this.clearTimer(this.retryTimer);
      this.retryTimer = null;
    }
  }
}
