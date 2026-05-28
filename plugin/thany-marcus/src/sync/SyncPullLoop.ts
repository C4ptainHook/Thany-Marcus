import type { ApiClient, SyncPullResponse } from "../api";
import type { QueueStore } from "../queue/QueueStore";
import type { Writer } from "./Writer";

const PAGE_LIMIT = 200;
const DEBOUNCE_MS = 500;
const FALLBACK_INTERVAL_MS = 60_000;

export interface SyncPullLoopCallbacks {
  onCursorAdvance: (cursor: string | null) => Promise<void>;
  onNoteReady: (noteId: string, vaultPath: string) => void;
  onStatusChange: (status: string) => void;
}

export class SyncPullLoop {
  private debounceTimer: number | null = null;
  private fallbackTimer: number | null = null;
  private running = false;
  private pendingFollowup = false;

  constructor(
    private readonly api: ApiClient,
    private readonly writer: Writer,
    private readonly queueStore: QueueStore,
    private readonly getCursor: () => string | null,
    private readonly callbacks: SyncPullLoopCallbacks,
  ) {}

  start(): void {
    this.scheduleFallback();
    this.trigger();
  }

  stop(): void {
    if (this.debounceTimer !== null) {
      window.clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }
    if (this.fallbackTimer !== null) {
      window.clearInterval(this.fallbackTimer);
      this.fallbackTimer = null;
    }
  }

  /** Debounced trigger — call freely from SSE handlers. */
  trigger(): void {
    if (this.debounceTimer !== null) return;
    this.debounceTimer = window.setTimeout(() => {
      this.debounceTimer = null;
      void this.runOnce();
    }, DEBOUNCE_MS);
  }

  /** Bypass debounce — for explicit user actions. */
  async runNow(): Promise<void> {
    await this.runOnce();
  }

  private scheduleFallback(): void {
    this.fallbackTimer = window.setInterval(() => this.trigger(), FALLBACK_INTERVAL_MS);
  }

  private async runOnce(): Promise<void> {
    if (this.running) {
      this.pendingFollowup = true;
      return;
    }
    this.running = true;
    this.callbacks.onStatusChange("syncing");
    try {
      // Drain pages until empty.
      // eslint-disable-next-line no-constant-condition
      while (true) {
        const cursor = this.getCursor();
        const res: SyncPullResponse = await this.api.syncPull(cursor, PAGE_LIMIT);
        if (res.items.length === 0 && res.projects.length === 0) break;

        for (const item of res.items) {
          try {
            const result = await this.writer.apply(item);
            if (!item.deleted) {
              if (result) this.callbacks.onNoteReady(item.noteId, result.vaultPath);
              const entry = this.queueStore.get(item.noteId);
              if (entry && result) {
                const failures = item.extractionFailures?.map((f) => ({
                  kind: f.kind,
                  attachmentId: f.attachmentId,
                  reason: f.reason,
                }));
                this.queueStore.markReady(item.noteId, result.vaultPath, failures);
              }
            } else {
              this.queueStore.remove(item.noteId);
            }
          } catch (e) {
            console.error("Thany: writer apply failed", e);
            this.queueStore.markFailed(item.noteId, (e as Error).message);
          }
        }

        if (res.nextSince) {
          await this.callbacks.onCursorAdvance(res.nextSince);
        } else {
          break;
        }
      }
      this.callbacks.onStatusChange("synced");
    } catch (e) {
      console.error("Thany: sync pull failed", e);
      this.callbacks.onStatusChange("error");
    } finally {
      this.running = false;
      if (this.pendingFollowup) {
        this.pendingFollowup = false;
        this.trigger();
      }
    }
  }
}
