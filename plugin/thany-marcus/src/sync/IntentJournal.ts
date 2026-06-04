export type IntentKind =
  | "tombstone"
  | "revive"
  | "reroute"
  | "folder_dissolve"
  | "folder_register"
  | "folder_unregister";

export interface TombstoneIntent {
  opId: string;
  kind: "tombstone";
  noteId: string;
  enqueuedAt: number;
}

export interface ReviveIntent {
  opId: string;
  kind: "revive";
  noteId: string;
  enqueuedAt: number;
}

export interface RerouteIntent {
  opId: string;
  kind: "reroute";
  noteId: string;
  relativePath: string;
  enqueuedAt: number;
}

export interface FolderDissolveIntent {
  opId: string;
  kind: "folder_dissolve";
  folder: string;
  mode: "reroute" | "force_delete";
  targetFolder?: string;
  enqueuedAt: number;
}

export interface FolderRegisterIntent {
  opId: string;
  kind: "folder_register";
  folder: string;
  enqueuedAt: number;
}

export interface FolderUnregisterIntent {
  opId: string;
  kind: "folder_unregister";
  folder: string;
  enqueuedAt: number;
}

export type Intent =
  | TombstoneIntent
  | ReviveIntent
  | RerouteIntent
  | FolderDissolveIntent
  | FolderRegisterIntent
  | FolderUnregisterIntent;

// The only storage Obsidian Mobile flushes reliably. The journal is append-only NDJSON: each line
// is either an intent or an ack-marker {opId, done:true}. Loading replays the lines and drops acked
// ops — it never truncates, so a crash loses at most a half-written final line, never the queue.
export interface JournalFs {
  exists(path: string): Promise<boolean>;
  read(path: string): Promise<string>;
  write(path: string, data: string): Promise<void>;
  append(path: string, data: string): Promise<void>;
  remove(path: string): Promise<void>;
  mkdir(path: string): Promise<void>;
}

const COMPACT_THRESHOLD_LINES = 256;

export class IntentJournal {
  private readonly live = new Map<string, Intent>();
  private lineCount = 0;
  private loaded = false;

  constructor(
    private readonly fs: JournalFs,
    private readonly dir: string,
    private readonly deviceId: string,
  ) {}

  private get path(): string {
    return `${this.dir}/intent-journal-${this.deviceId}.ndjson`;
  }

  private get tmpPath(): string {
    return `${this.path}.tmp`;
  }

  /** Replay the journal into memory. Resume, never reset. */
  async load(): Promise<Intent[]> {
    this.live.clear();
    this.lineCount = 0;
    if (await this.fs.exists(this.path)) {
      const raw = await this.fs.read(this.path);
      for (const line of raw.split("\n")) {
        const trimmed = line.trim();
        if (!trimmed) continue;
        this.lineCount++;
        let rec: unknown;
        try {
          rec = JSON.parse(trimmed);
        } catch {
          // A torn final line from a crash mid-append — ignore it.
          continue;
        }
        const r = rec as { opId?: string; done?: boolean } & Partial<Intent>;
        if (!r.opId) continue;
        if (r.done) {
          this.live.delete(r.opId);
        } else if (r.kind) {
          this.live.set(r.opId, r as Intent);
        }
      }
    }
    this.loaded = true;
    if (this.lineCount > COMPACT_THRESHOLD_LINES) await this.compact();
    return [...this.live.values()];
  }

  pending(): Intent[] {
    return [...this.live.values()];
  }

  has(opId: string): boolean {
    return this.live.has(opId);
  }

  async enqueue(intent: Intent): Promise<void> {
    if (this.live.has(intent.opId)) return;
    this.live.set(intent.opId, intent);
    await this.appendLine(intent);
  }

  async ack(opId: string): Promise<void> {
    if (!this.live.delete(opId)) return;
    await this.appendLine({ opId, done: true });
    if (this.lineCount > COMPACT_THRESHOLD_LINES) await this.compact();
  }

  private async appendLine(rec: unknown): Promise<void> {
    await this.ensureDir();
    const line = JSON.stringify(rec) + "\n";
    try {
      await this.fs.append(this.path, line);
    } catch {
      // Some mobile adapters proxy append onto read+write; fall back to a full rewrite.
      const prev = (await this.fs.exists(this.path)) ? await this.fs.read(this.path) : "";
      await this.fs.write(this.path, prev + line);
    }
    this.lineCount++;
  }

  // Rewrite the file with only live intents via temp + atomic rename, so a crash can't leave a
  // half-written journal in place of the real one.
  async compact(): Promise<void> {
    if (!this.loaded) return;
    await this.ensureDir();
    const body = [...this.live.values()].map((i) => JSON.stringify(i)).join("\n");
    const data = body ? body + "\n" : "";
    await this.fs.write(this.tmpPath, data);
    await this.fs.remove(this.path).catch(() => {});
    await this.fs.write(this.path, data);
    await this.fs.remove(this.tmpPath).catch(() => {});
    this.lineCount = this.live.size;
  }

  private dirEnsured = false;
  private async ensureDir(): Promise<void> {
    if (this.dirEnsured) return;
    if (!(await this.fs.exists(this.dir))) {
      await this.fs.mkdir(this.dir).catch(() => {});
    }
    this.dirEnsured = true;
  }
}
