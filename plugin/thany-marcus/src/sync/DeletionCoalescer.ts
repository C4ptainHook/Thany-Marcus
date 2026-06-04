export interface CoalescedDeletes {
  folders: { folder: string; noteIds: string[] }[];
  singles: { path: string; noteId: string }[];
}

type ScheduleFn = (fn: () => void, ms: number) => number;
type ClearFn = (handle: number) => void;

// Obsidian has no pre-delete veto and fires deletes one-by-one. A folder deletion arrives as a
// burst of child deletes (± a TFolder delete) under one prefix. Buffer for a short window, then
// split the burst into folder-deletions vs individual note deletions so a folder gets one prompt
// instead of N tombstones.
export class DeletionCoalescer {
  private readonly notes = new Map<string, string>();
  private readonly folders = new Set<string>();
  private timer: number | null = null;

  constructor(
    private readonly onFlush: (out: CoalescedDeletes) => void,
    private readonly windowMs = 400,
    private readonly schedule: ScheduleFn = (fn, ms) => window.setTimeout(fn, ms),
    private readonly clearTimer: ClearFn = (h) => window.clearTimeout(h),
  ) {}

  noteDeleted(path: string, noteId: string): void {
    this.notes.set(path, noteId);
    this.arm();
  }

  folderDeleted(folder: string): void {
    this.folders.add(folder);
    this.arm();
  }

  private arm(): void {
    if (this.timer !== null) return;
    this.timer = this.schedule(() => {
      this.timer = null;
      this.flush();
    }, this.windowMs);
  }

  flush(): void {
    const usedPaths = new Set<string>();
    const folders: { folder: string; noteIds: string[] }[] = [];
    for (const folder of this.folders) {
      const prefix = folder + "/";
      const noteIds: string[] = [];
      for (const [path, id] of this.notes) {
        if (path === folder || path.startsWith(prefix)) {
          noteIds.push(id);
          usedPaths.add(path);
        }
      }
      folders.push({ folder, noteIds });
    }

    const singles: { path: string; noteId: string }[] = [];
    for (const [path, id] of this.notes) {
      if (!usedPaths.has(path)) singles.push({ path, noteId: id });
    }

    this.notes.clear();
    this.folders.clear();
    this.onFlush({ folders, singles });
  }

  dispose(): void {
    if (this.timer !== null) {
      this.clearTimer(this.timer);
      this.timer = null;
    }
  }
}
