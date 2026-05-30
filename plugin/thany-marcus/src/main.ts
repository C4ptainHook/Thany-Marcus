import {
  MarkdownView,
  Notice,
  Plugin,
  WorkspaceLeaf,
  type TFile,
} from "obsidian";
import { ApiClient } from "./api";
import { BrowView } from "./draft/BrowView";
import { DraftManager, type DraftState } from "./draft/DraftManager";
import { Submitter } from "./draft/Submitter";
import { QueueStore } from "./queue/QueueStore";
import { QueueSidebarView, QUEUE_VIEW_TYPE } from "./queue/QueueSidebar";
import { PhaseToastController } from "./queue/PhaseToast";
import { isTerminal } from "./queue/labels";
import { AttachmentIndex } from "./sync/AttachmentIndex";
import { EventStream } from "./sync/EventStream";
import { SyncPullLoop } from "./sync/SyncPullLoop";
import { Writer } from "./sync/Writer";
import {
  DEFAULT_SETTINGS,
  ThanyMarcusSettingTab,
  type ThanyMarcusSettings,
} from "./settings";

interface PersistedState {
  settings: ThanyMarcusSettings;
  attachmentIndex: Record<string, string[]>;
}

export default class ThanyMarcusPlugin extends Plugin {
  settings!: ThanyMarcusSettings;
  api!: ApiClient;

  private attachmentIndex!: AttachmentIndex;
  private queueStore = new QueueStore();
  private toast!: PhaseToastController;
  private drafts!: DraftManager;
  private submitter!: Submitter;
  private writer!: Writer;
  private syncLoop!: SyncPullLoop;
  private events!: EventStream;
  private brow: BrowView | null = null;
  private statusBarEl: HTMLElement | null = null;
  private rememberedDraftLeaves = new Map<string, WorkspaceLeaf>();
  private pendingNoteToLeaf = new Map<string, WorkspaceLeaf>();
  private pendingDrafts = new Map<string, DraftState>();

  async onload(): Promise<void> {
    await this.loadState();

    this.api = new ApiClient(
      () => this.settings.cloudUrl,
      () => this.settings.token,
      () => new Notice("Thany: token revoked. Re-paste from portal."),
    );

    this.drafts = new DraftManager(this.app.vault, this.app.workspace, () => this.settings.vaultFolder);
    this.submitter = new Submitter(this.api, this.app.vault, () => this.settings);
    this.writer = new Writer(this.app.vault, () => this.settings.vaultFolder, this.attachmentIndex);

    this.syncLoop = new SyncPullLoop(
      this.api,
      this.writer,
      this.queueStore,
      () => this.settings.syncCursor,
      {
        onCursorAdvance: async (cursor) => {
          this.settings.syncCursor = cursor;
          await this.saveState();
        },
        onNoteReady: (noteId, vaultPath) => this.onNoteReady(noteId, vaultPath),
        onStatusChange: (status) => this.setStatusBar(status),
      },
    );

    this.events = new EventStream(
      () => `${this.settings.cloudUrl.replace(/\/+$/, "")}/api/sync/events`,
      () => this.settings.token,
      (e) => this.onSseEvent(e),
      (err) => console.warn("Thany SSE:", err.message),
    );

    this.toast = new PhaseToastController(this.queueStore);
    this.toast.start();

    this.registerView(
      QUEUE_VIEW_TYPE,
      (leaf) => new QueueSidebarView(
        leaf,
        this.queueStore,
        (path) => void this.openVaultFile(path),
        (noteId) => void this.reprocess(noteId),
        (noteId) => void this.cancelIngest(noteId),
        {
          list:   () => this.api.listProjects(),
          create: (name, description) => this.api.createProject({ name, description }),
          remove: (id) => this.api.deleteProject(id),
        },
      ),
    );

    this.addSettingTab(new ThanyMarcusSettingTab(this.app, this));

    this.addRibbonIcon("cloud", "Thany: new draft", () => void this.openNewDraft());

    this.addCommand({
      id: "new-draft",
      name: "New draft",
      hotkeys: [{ modifiers: ["Mod", "Shift"], key: "T" }],
      callback: () => void this.openNewDraft(),
    });

    this.addCommand({
      id: "open-queue",
      name: "Open queue sidebar",
      callback: () => void this.activateQueueView(),
    });

    this.addCommand({
      id: "sync-now",
      name: "Sync now",
      callback: () => void this.syncLoop.runNow(),
    });

    this.statusBarEl = this.addStatusBarItem();
    this.setStatusBar("idle");

    this.registerEvent(
      this.app.workspace.on("file-open", (f) => this.onFileOpen(f)),
    );

    this.registerEvent(
      this.app.workspace.on("active-leaf-change", () => {
        const view = this.app.workspace.getActiveViewOfType(MarkdownView);
        this.onFileOpen(view?.file ?? null);
      }),
    );

    this.registerEvent(
      this.app.vault.on("modify", (f) => {
        if (!(f as TFile).path) return;
        const path = (f as TFile).path;
        if (this.attachmentIndex && this.isSyncedNotePath(path)) {
          const view = this.app.workspace.getActiveViewOfType(MarkdownView);
          if (view?.file?.path === path) {
            new Notice("Cloud sync will overwrite. Push-back not supported in v1.");
          }
        }
      }),
    );

    if (this.settings.cloudUrl && this.settings.token) {
      this.syncLoop.start();
      this.events.start();
    }
  }

  async onunload(): Promise<void> {
    this.brow?.detach();
    this.brow = null;
    this.toast.stop();
    this.syncLoop.stop();
    this.events.stop();
    this.app.workspace.detachLeavesOfType(QUEUE_VIEW_TYPE);
  }

  async saveSettings(): Promise<void> {
    await this.saveState();
    // Restart connections if cloud config changed.
    if (this.settings.cloudUrl && this.settings.token) {
      this.events.stop();
      this.events.start();
      this.syncLoop.start();
    }
  }

  private async loadState(): Promise<void> {
    const raw = (await this.loadData()) as PersistedState | null;
    this.settings = { ...DEFAULT_SETTINGS, ...(raw?.settings ?? {}) };
    this.attachmentIndex = AttachmentIndex.fromJson(raw?.attachmentIndex ?? {});
  }

  private async saveState(): Promise<void> {
    const data: PersistedState = {
      settings: this.settings,
      attachmentIndex: this.attachmentIndex.toJson(),
    };
    await this.saveData(data);
  }

  private async openNewDraft(): Promise<void> {
    if (!this.settings.cloudUrl || !this.settings.token) {
      new Notice("Thany: configure cloud URL and token first.");
      return;
    }
    const state = await this.drafts.create();
    const file = await this.drafts.openInNewLeaf(state);
    if (!file) return;
    const view = this.app.workspace.getActiveViewOfType(MarkdownView);
    if (view) await this.attachBrow(view);
  }

  private async attachBrow(view: MarkdownView): Promise<void> {
    if (!view.file) return;
    const state =
      this.drafts.getByPath(view.file.path) ?? (await this.drafts.hydrateFromFile(view.file));
    this.brow?.detach();
    this.brow = new BrowView(
      this.drafts,
      this.submitter,
      () => this.settings.recordMimeType,
      {
        onSubmitted: (noteId, draftFile) => this.onDraftSubmitted(noteId, draftFile, view.leaf),
        onDiscarded: () => {
          this.brow?.detach();
          this.brow = null;
        },
      },
      this.app.workspace,
      {
        relatedNotes: (req, signal) => this.api.relatedNotes(req, signal),
      },
    );
    this.brow.attach(view, state);
  }

  private onFileOpen(file: TFile | null): void {
    const view = this.app.workspace.getActiveViewOfType(MarkdownView);
    if (!view || !view.file) {
      this.brow?.detach();
      this.brow = null;
      return;
    }
    if (this.drafts.isDraftPath(view.file.path)) {
      void this.attachBrow(view);
    } else {
      this.brow?.detach();
      this.brow = null;
    }
    void file;
  }

  private async onDraftSubmitted(
    noteId: string,
    draftFile: TFile,
    leaf: WorkspaceLeaf,
  ): Promise<void> {
    this.rememberedDraftLeaves.set(draftFile.path, leaf);
    this.pendingNoteToLeaf.set(noteId, leaf);
    this.queueStore.upsert(noteId, {
      title: draftFile.basename,
      status: "queued",
      draftLeafId: null,
    });

    const state = this.drafts.getByPath(draftFile.path);
    this.brow?.detach();
    this.brow = null;
    if (state) {
      this.pendingDrafts.set(noteId, state);
    }

    this.syncLoop.trigger();
  }

  private onSseEvent(e: { kind: string; data: Record<string, unknown> }): void {
    const noteId = typeof e.data.noteId === "string" ? e.data.noteId : (e.data.NoteId as string | undefined);
    if (e.kind === "note_phase_changed") {
      const to = String(e.data.to ?? e.data.To ?? "");
      if (noteId) this.queueStore.upsert(noteId, { status: to });
    } else if (e.kind === "note_succeeded") {
      if (noteId) this.queueStore.upsert(noteId, { status: "ready" });
    } else if (e.kind === "note_failed") {
      const err = String(e.data.error ?? e.data.Error ?? "failed");
      if (noteId) this.queueStore.markFailed(noteId, err);
    } else if (e.kind === "note_cancelled") {
      if (noteId) void this.onNoteCancelled(noteId);
    }
    this.syncLoop.trigger();
  }

  private onNoteReady(noteId: string, vaultPath: string): void {
    const leaf = this.pendingNoteToLeaf.get(noteId);
    if (leaf) {
      this.pendingNoteToLeaf.delete(noteId);
      void this.openVaultFileInLeaf(vaultPath, leaf);
    }
    const held = this.pendingDrafts.get(noteId);
    if (held) {
      this.pendingDrafts.delete(noteId);
      void this.drafts.discard(held);
    }
    this.queueStore.pruneCompleted(15 * 60 * 1000);
    if (this.queueStore.get(noteId) && isTerminal(this.queueStore.get(noteId)!.status)) {
      // noop — marker for future cleanup hooks
    }
  }

  private async onNoteCancelled(noteId: string): Promise<void> {
    this.pendingNoteToLeaf.delete(noteId);
    const held = this.pendingDrafts.get(noteId);
    this.pendingDrafts.delete(noteId);
    this.queueStore.remove(noteId);
    if (!held) {
      return;
    }
    const file = this.app.vault.getAbstractFileByPath(held.filePath) as TFile | null;
    if (!file) {
      new Notice("Thany: ingest cancelled (draft file already gone)");
      return;
    }
    const leaf = this.app.workspace.getLeaf("tab");
    try {
      await leaf.openFile(file);
      const view = this.app.workspace.getActiveViewOfType(MarkdownView);
      if (view) await this.attachBrow(view);
      new Notice("Thany: ingest cancelled, draft restored");
    } catch {
      new Notice("Thany: ingest cancelled");
    }
  }

  private async cancelIngest(noteId: string): Promise<void> {
    try {
      const result = await this.api.cancelIngest(noteId);
      if (result.ok) {
        new Notice("Thany: cancel sent");
      } else {
        new Notice(`Thany: cancel rejected — ${result.reason}`);
      }
    } catch (e) {
      new Notice(`Thany: cancel failed — ${(e as Error).message}`);
    }
  }

  private async reprocess(noteId: string): Promise<void> {
    try {
      await this.api.reprocess(noteId);
      new Notice("Thany: reprocess queued");
      this.syncLoop.trigger();
    } catch (e) {
      new Notice(`Thany: reprocess failed — ${(e as Error).message}`);
    }
  }

  private async openVaultFile(path: string): Promise<void> {
    const file = this.app.vault.getAbstractFileByPath(path);
    if (!file || !("stat" in file)) return;
    const leaf = this.app.workspace.getLeaf(false);
    await leaf.openFile(file as TFile);
  }

  private async openVaultFileInLeaf(path: string, leaf: WorkspaceLeaf): Promise<void> {
    const file = this.app.vault.getAbstractFileByPath(path);
    if (!file || !("stat" in file)) return;
    try {
      await leaf.openFile(file as TFile);
    } catch {
      await this.openVaultFile(path);
    }
  }

  private isSyncedNotePath(path: string): boolean {
    const root = this.settings.vaultFolder || "Thany";
    if (!path.startsWith(`${root}/`)) return false;
    if (path.startsWith(`${root}/_drafts/`)) return false;
    return path.endsWith(".md");
  }

  private setStatusBar(state: "idle" | "syncing" | "synced" | "error" | string): void {
    if (!this.statusBarEl) return;
    let text = "Thany";
    switch (state) {
      case "idle":
        text = "Thany: idle";
        break;
      case "syncing":
        text = "Thany: syncing…";
        break;
      case "synced":
        text = `Thany: synced ${formatTime(new Date())}`;
        break;
      case "error":
        text = "Thany: error (click to retry)";
        break;
      default:
        text = `Thany: ${state}`;
    }
    this.statusBarEl.setText(text);
    this.statusBarEl.onclick = state === "error" ? () => void this.syncLoop.runNow() : null;
  }

  private async activateQueueView(): Promise<void> {
    const existing = this.app.workspace.getLeavesOfType(QUEUE_VIEW_TYPE);
    if (existing.length > 0) {
      this.app.workspace.revealLeaf(existing[0]);
      return;
    }
    const leaf = this.app.workspace.getRightLeaf(false);
    if (!leaf) return;
    await leaf.setViewState({ type: QUEUE_VIEW_TYPE, active: true });
    this.app.workspace.revealLeaf(leaf);
  }
}

function formatTime(d: Date): string {
  const pad = (n: number) => n.toString().padStart(2, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
