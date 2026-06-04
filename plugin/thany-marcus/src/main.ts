import {
  MarkdownView,
  Notice,
  Plugin,
  TFolder,
  WorkspaceLeaf,
  setIcon,
  type TAbstractFile,
  type TFile,
} from "obsidian";
import { ApiClient } from "./api";
import { TM_ICON, registerThanyIcons, type ThanyIconId } from "./icons";
import { BrowView } from "./draft/BrowView";
import { DraftManager, type DraftState } from "./draft/DraftManager";
import { Submitter } from "./draft/Submitter";
import { ElectronDesktopNotifier, type DesktopNotifier } from "./notifications/DesktopNotifier";
import { QueueStore, type QueueEntry } from "./queue/QueueStore";
import { QueueSidebarView, QUEUE_VIEW_TYPE } from "./queue/QueueSidebar";
import { PhaseToastController } from "./queue/PhaseToast";
import { isTerminal } from "./queue/labels";
import { AttachmentIndex } from "./sync/AttachmentIndex";
import { EventStream } from "./sync/EventStream";
import { SyncPullLoop } from "./sync/SyncPullLoop";
import { Writer } from "./sync/Writer";
import { IntentJournal, type Intent, type JournalFs } from "./sync/IntentJournal";
import { IntentQueue, type IntentExecutor } from "./sync/IntentQueue";
import { NoteIndex } from "./sync/NoteIndex";
import { VaultEventGuard } from "./sync/VaultEventGuard";
import { DeletionCoalescer, type CoalescedDeletes } from "./sync/DeletionCoalescer";
import { planReconciliation } from "./sync/Reconciler";
import { planFolderReconciliation } from "./sync/FolderReconciler";
import { FolderDissolveModal, type FolderDissolveChoice } from "./sync/FolderDissolveModal";
import { ConfirmMassDeleteModal } from "./sync/ConfirmMassDeleteModal";
import {
  DEFAULT_SETTINGS,
  ThanyMarcusSettingTab,
  type ThanyMarcusSettings,
} from "./settings";

interface PersistedState {
  settings: ThanyMarcusSettings;
  attachmentIndex: Record<string, string[]>;
  // Disposable caches — rebuildable from the cloud + metadataCache; persisted only so a reopen
  // shows last-known state instantly.
  queueSnapshot?: QueueEntry[];
  manifestIds?: string[];
  tombstonedIds?: string[];
  folderManifest?: string[];
}

export default class ThanyMarcusPlugin extends Plugin {
  settings!: ThanyMarcusSettings;
  api!: ApiClient;

  private attachmentIndex!: AttachmentIndex;
  private queueStore = new QueueStore();
  private notifier!: DesktopNotifier;
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

  private readonly noteIndex = new NoteIndex();
  private readonly guard = new VaultEventGuard();
  private journal!: IntentJournal;
  private intents!: IntentQueue;
  private coalescer!: DeletionCoalescer;
  private tombstonedIds = new Set<string>();
  private manifestIds: string[] = [];
  private folderManifest: string[] = [];
  private started = false;

  async onload(): Promise<void> {
    await this.loadState();

    registerThanyIcons();
    this.applyChromeClasses();

    this.api = new ApiClient(
      () => this.settings.cloudUrl,
      () => this.settings.token,
      () => new Notice("Thany: token revoked. Re-paste from portal."),
    );

    this.notifier = new ElectronDesktopNotifier();
    this.queueStore.onTerminalTransition((prev, next) => this.onTerminalTransition(prev, next));

    this.drafts = new DraftManager(this.app.vault, this.app.workspace, () => this.settings.vaultFolder);
    this.submitter = new Submitter(this.api, this.app.vault, () => this.settings, this.notifier);
    this.writer = new Writer(this.app.vault, () => this.settings.vaultFolder, this.attachmentIndex, this.guard);

    this.journal = new IntentJournal(this.journalFs(), this.stateDir(), this.settings.deviceId);
    this.intents = new IntentQueue(this.journal, this.intentExecutor());
    this.coalescer = new DeletionCoalescer((out) => void this.onCoalescedDeletes(out));

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
          list:    (signal) => this.api.listEntitySuggestions(signal),
          accept:  (id) => this.api.acceptEntitySuggestion(id),
          dismiss: (id) => this.api.dismissEntitySuggestion(id),
          edit:    (id, patch) => this.api.editEntitySuggestion(id, patch),
        },
        {
          listActiveJobs: (includeRecent) => this.api.listActiveJobs(includeRecent),
        },
        () => void this.openNewDraft(),
      ),
    );

    this.addSettingTab(new ThanyMarcusSettingTab(this.app, this));

    this.addRibbonIcon(TM_ICON.mark, "Thany: new draft", () => void this.openNewDraft());

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

    this.registerObsidianProtocolHandler("thany-marcus-connect", async (params) => {
      const cloudUrl = params.cloudUrl;
      const token = params.token;
      if (!cloudUrl || !token) {
        new Notice("Thany: connect link missing cloudUrl or token.");
        return;
      }
      this.settings.cloudUrl = cloudUrl.replace(/\/+$/, "");
      this.settings.token = token;
      await this.saveSettings();
      new Notice(`Thany: connected to ${new URL(this.settings.cloudUrl).hostname}`);
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

    this.registerVaultEvents();

    // Rebuild the path↔ID index from metadataCache, resume the durable queue, and reconcile against
    // the cloud — only once the vault layout (and frontmatter) is ready.
    this.app.workspace.onLayoutReady(() => void this.runStartupSequence());
  }

  async onunload(): Promise<void> {
    document.body.removeClasses(["tm-pixel-chrome", "tm-action-effects"]);
    this.brow?.detach();
    this.brow = null;
    this.toast.stop();
    this.syncLoop.stop();
    this.events.stop();
    this.intents?.destroy();
    this.coalescer?.dispose();
    await this.journal?.compact().catch(() => {});
    await this.saveState().catch(() => {});
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
    this.queueStore.loadSnapshot(raw?.queueSnapshot);
    this.manifestIds = raw?.manifestIds ?? [];
    this.tombstonedIds = new Set(raw?.tombstonedIds ?? []);
    this.folderManifest = raw?.folderManifest ?? [];
    if (!this.settings.deviceId) {
      this.settings.deviceId = newDeviceId();
      await this.saveState();
    }
  }

  private async saveState(): Promise<void> {
    const data: PersistedState = {
      settings: this.settings,
      attachmentIndex: this.attachmentIndex.toJson(),
      queueSnapshot: this.queueStore.toSnapshot(),
      manifestIds: this.manifestIds,
      tombstonedIds: [...this.tombstonedIds],
      folderManifest: this.folderManifest,
    };
    await this.saveData(data);
  }

  // ── Durable foundation (Pass A) ──────────────────────────────────────────

  private stateDir(): string {
    return `${this.app.vault.configDir}/plugins/${this.manifest.id}/thany-state`;
  }

  private journalFs(): JournalFs {
    const a = this.app.vault.adapter;
    return {
      exists: (p) => a.exists(p),
      read: (p) => a.read(p),
      write: (p, d) => a.write(p, d),
      append: (p, d) => a.append(p, d),
      remove: (p) => a.remove(p),
      mkdir: (p) => a.mkdir(p),
    };
  }

  private registerVaultEvents(): void {
    this.registerEvent(this.app.vault.on("create", (f) => this.onVaultCreate(f)));
    this.registerEvent(this.app.vault.on("modify", (f) => this.onVaultModify(f)));
    this.registerEvent(this.app.vault.on("rename", (f, oldPath) => void this.onVaultRename(f, oldPath)));
    this.registerEvent(this.app.vault.on("delete", (f) => this.onVaultDelete(f)));
    this.registerEvent(
      this.app.metadataCache.on("changed", (file) => this.onMetaChanged(file)),
    );
  }

  private async runStartupSequence(): Promise<void> {
    if (this.started) return;
    this.started = true;

    this.rebuildNoteIndex();

    try {
      const pending = await this.journal.load();
      if (pending.length > 0) this.intents.kick();
    } catch (e) {
      console.warn("Thany: intent journal load failed", e);
    }

    if (this.settings.cloudUrl && this.settings.token) {
      this.syncLoop.start();
      this.events.start();
      void this.reconcileOnStartup();
      void this.reconcileFoldersOnStartup();
    }

    this.manifestIds = this.noteIndex.ids();
    await this.saveState();
  }

  private rebuildNoteIndex(): void {
    const files = this.app.vault.getMarkdownFiles();
    const entries = files
      .filter((f) => this.isSyncedNotePath(f.path))
      .map((f) => ({ path: f.path, noteId: this.frontmatterNoteId(f) }));
    this.noteIndex.rebuild(entries);
  }

  private frontmatterNoteId(file: TFile): string | null {
    const fm = this.app.metadataCache.getFileCache(file)?.frontmatter as
      | Record<string, unknown>
      | undefined;
    const id = fm?.["thany_note_id"];
    return typeof id === "string" && id.length > 0 ? id : null;
  }

  private cloudRelativePath(vaultPath: string): string {
    const root = (this.settings.vaultFolder || "Thany") + "/";
    return vaultPath.startsWith(root) ? vaultPath.slice(root.length) : vaultPath;
  }

  // ── Vault event handlers (Pass A index + Pass B/C intents) ───────────────

  private onVaultCreate(file: TAbstractFile): void {
    const path = file.path;
    if (file instanceof TFolder) {
      if (this.isSyncedFolderPath(path)) void this.registerFolder(path);
      return;
    }
    if (!this.isSyncedNotePath(path)) return;
    this.noteIndex.onCreateOrModify(path, this.frontmatterNoteId(file as TFile));
    // Revive is detected in onMetaChanged, where frontmatter is reliably parsed.
  }

  private onVaultModify(file: TAbstractFile): void {
    const path = (file as TFile).path;
    if (!path || !this.isSyncedNotePath(path)) return;
    this.noteIndex.onCreateOrModify(path, this.frontmatterNoteId(file as TFile));
    if (this.guard.consume("modify", path)) return;
    const view = this.app.workspace.getActiveViewOfType(MarkdownView);
    if (view?.file?.path === path) {
      new Notice("Cloud sync will overwrite. Push-back not supported in v1.");
    }
  }

  private onMetaChanged(file: TFile): void {
    const path = file.path;
    if (!this.isSyncedNotePath(path)) return;
    const id = this.frontmatterNoteId(file);
    this.noteIndex.onCreateOrModify(path, id);
    if (this.guard.consume("create", path)) return;
    if (id && this.tombstonedIds.has(id)) {
      this.tombstonedIds.delete(id);
      void this.enqueueIntent({ kind: "revive", noteId: id, opId: `revive:${id}`, enqueuedAt: Date.now() });
    }
  }

  private async onVaultRename(file: TAbstractFile, oldPath: string): Promise<void> {
    if (file instanceof TFolder) {
      await this.onFolderRenamed(oldPath, file.path);
      return;
    }
    const newPath = (file as TFile).path;
    const wasSynced = this.isSyncedNotePath(oldPath);
    const isSynced = this.isSyncedNotePath(newPath);
    if (!wasSynced && !isSynced) return;
    const id = this.noteIndex.onRename(oldPath, newPath, this.frontmatterNoteId(file as TFile));
    if (this.guard.consume("rename", newPath)) return;
    if (id && isSynced) {
      // rename ≠ delete: tell the cloud where the note lives now, never tombstone.
      await this.enqueueIntent({
        kind: "reroute",
        noteId: id,
        relativePath: this.cloudRelativePath(newPath),
        opId: `reroute:${id}:${newPath}`,
        enqueuedAt: Date.now(),
      });
    }
  }

  private async onFolderRenamed(oldFolder: string, newFolder: string): Promise<void> {
    const prefix = oldFolder + "/";
    for (const path of this.noteIndex.paths()) {
      if (!path.startsWith(prefix)) continue;
      const newPath = newFolder + "/" + path.slice(prefix.length);
      const id = this.noteIndex.onRename(path, newPath, null);
      if (id && this.isSyncedNotePath(newPath)) {
        await this.enqueueIntent({
          kind: "reroute",
          noteId: id,
          relativePath: this.cloudRelativePath(newPath),
          opId: `reroute:${id}:${newPath}`,
          enqueuedAt: Date.now(),
        });
      }
    }
    if (this.isSyncedFolderPath(oldFolder)) await this.unregisterFolder(oldFolder);
    if (this.isSyncedFolderPath(newFolder)) await this.registerFolder(newFolder);
  }

  private onVaultDelete(file: TAbstractFile): void {
    const path = file.path;
    if (file instanceof TFolder) {
      if (this.isSyncedFolderPath(path)) {
        this.coalescer.folderDeleted(path);
        void this.unregisterFolder(path);
      }
      return;
    }
    if (!this.isSyncedNotePath(path)) return;
    const id = this.noteIndex.onDelete(path);
    if (this.guard.consume("delete", path)) return;
    if (!id) return;
    this.coalescer.noteDeleted(path, id);
  }

  private async onCoalescedDeletes(out: CoalescedDeletes): Promise<void> {
    for (const single of out.singles) {
      this.tombstonedIds.add(single.noteId);
      await this.enqueueIntent({
        kind: "tombstone",
        noteId: single.noteId,
        opId: `tombstone:${single.noteId}`,
        enqueuedAt: Date.now(),
      });
    }
    for (const folder of out.folders) {
      await this.onFolderDeleted(folder.folder, folder.noteIds);
    }
    await this.saveState();
  }

  // ── Folder dissolve (Pass C) ─────────────────────────────────────────────

  private async onFolderDeleted(localFolder: string, bufferedIds: string[]): Promise<void> {
    const cloudFolder = this.cloudRelativePath(localFolder);
    const count = bufferedIds.length;
    if (count === 0) return;

    if (this.app.workspace.layoutReady) {
      new FolderDissolveModal(this.app, cloudFolder, count, (choice) =>
        void this.dispatchFolderDissolve(cloudFolder, choice),
      ).open();
    } else {
      // Un-askable (startup / no UI): the safe default is preserve-and-reroute.
      await this.dispatchFolderDissolve(cloudFolder, "reroute");
    }
  }

  private async dispatchFolderDissolve(
    cloudFolder: string,
    mode: FolderDissolveChoice,
  ): Promise<void> {
    await this.enqueueIntent({
      kind: "folder_dissolve",
      folder: cloudFolder,
      mode,
      targetFolder: mode === "reroute" ? "Inbox" : undefined,
      opId: `folder:${mode}:${cloudFolder}`,
      enqueuedAt: Date.now(),
    });
    await this.saveState();
  }

  private registerFolder(localPath: string): Promise<void> {
    return this.enqueueFolderRegister(this.cloudRelativePath(localPath));
  }

  private unregisterFolder(localPath: string): Promise<void> {
    return this.enqueueFolderUnregister(this.cloudRelativePath(localPath));
  }

  private enqueueFolderRegister(folder: string): Promise<void> {
    return this.enqueueIntent({
      kind: "folder_register",
      folder,
      opId: `folder_register:${folder}`,
      enqueuedAt: Date.now(),
    });
  }

  private enqueueFolderUnregister(folder: string): Promise<void> {
    return this.enqueueIntent({
      kind: "folder_unregister",
      folder,
      opId: `folder_unregister:${folder}`,
      enqueuedAt: Date.now(),
    });
  }

  // ── Intent executor (drives the durable queue) ───────────────────────────

  private intentExecutor(): IntentExecutor {
    return {
      tombstone: (noteId) => this.api.tombstoneNote(noteId),
      revive: async (noteId) => {
        const revived = await this.api.reviveNote(noteId);
        if (!revived) {
          new Notice("Thany: restored locally, but its cloud copy expired — re-submit to re-process.");
        }
      },
      reroute: (noteId, relativePath) => this.api.moveNote(noteId, relativePath),
      folderDissolve: async (folder, mode, target) => {
        const res = await this.api.folderDissolve(folder, mode, target);
        if (mode === "reroute") {
          // Rerouted notes get updated_at bumped cloud-side → the pull loop re-materialises them
          // into the target folder. Declarative + resumable: re-running converges.
          this.syncLoop.trigger();
          new Notice(`Thany: rerouting ${res.affectedCount} note(s) to ${target ?? "Inbox"}…`);
        } else {
          new Notice(`Thany: deleted ${res.affectedCount} note(s) from ${folder}`);
        }
      },
      folderRegister: (folder) => this.api.registerFolder(folder),
      folderUnregister: (folder) => this.api.unregisterFolder(folder),
    };
  }

  private async enqueueIntent(intent: Intent): Promise<void> {
    if (!this.settings.cloudUrl || !this.settings.token) return;
    await this.intents.enqueue(intent);
  }

  // ── Processing-state continuity + reconciliation backstop (Pass B/D) ─────

  private async reconcileOnStartup(): Promise<void> {
    const ids = this.noteIndex.ids();
    const cloudStatus = new Map<string, string>();
    try {
      const candidates = Array.from(new Set([...ids, ...this.manifestIds]));
      for (let i = 0; i < candidates.length; i += 200) {
        const chunk = candidates.slice(i, i + 200);
        const items = await this.api.statusPull(chunk);
        for (const it of items) cloudStatus.set(it.noteId, it.status);
      }
    } catch (e) {
      console.warn("Thany: status pull failed", e);
      return;
    }

    // Light status dots: reflect last-known cloud status for notes still in the queue.
    for (const [noteId, status] of cloudStatus) {
      const entry = this.queueStore.get(noteId);
      if (entry && !isTerminal(entry.status)) {
        if (status === "ready") this.queueStore.upsert(noteId, { status: "ready" });
        else if (status !== "deleted") this.queueStore.upsert(noteId, { status });
      }
    }

    const plan = planReconciliation({
      manifestIds: this.manifestIds,
      presentIds: new Set(ids),
      cloudStatus,
    });
    if (plan.tombstoneCandidates.length === 0) return;

    if (plan.tripped) {
      new ConfirmMassDeleteModal(
        this.app,
        plan.tombstoneCandidates.map((id) => id),
        (confirm) => {
          if (confirm) void this.tombstoneBatch(plan.tombstoneCandidates);
        },
      ).open();
    } else {
      await this.tombstoneBatch(plan.tombstoneCandidates);
    }
  }

  private async tombstoneBatch(noteIds: string[]): Promise<void> {
    for (const id of noteIds) {
      this.tombstonedIds.add(id);
      await this.enqueueIntent({
        kind: "tombstone",
        noteId: id,
        opId: `tombstone:${id}`,
        enqueuedAt: Date.now(),
      });
    }
    await this.saveState();
  }

  private currentVaultFolders(): string[] {
    return this.app.vault
      .getAllLoadedFiles()
      .filter((f): f is TFolder => f instanceof TFolder)
      .map((f) => f.path)
      .filter((p) => this.isSyncedFolderPath(p))
      .map((p) => this.cloudRelativePath(p));
  }

  private async reconcileFoldersOnStartup(): Promise<void> {
    const vaultFolders = this.currentVaultFolders();
    let cloudFolders: string[];
    try {
      cloudFolders = await this.api.listFolders();
    } catch (e) {
      console.warn("Thany: folder list failed", e);
      return;
    }
    const plan = planFolderReconciliation({ vaultFolders, cloudFolders });
    for (const folder of plan.toRegister) await this.enqueueFolderRegister(folder);
    for (const folder of plan.toUnregister) await this.enqueueFolderUnregister(folder);
    this.folderManifest = vaultFolders;
    await this.saveState();
  }

  private isSyncedFolderPath(path: string): boolean {
    const root = this.settings.vaultFolder || "Thany";
    if (path === root) return false;
    if (!path.startsWith(`${root}/`)) return false;
    const firstSegment = path.slice(root.length + 1).split("/", 1)[0];
    if (firstSegment.startsWith("_") || firstSegment.startsWith(".")) return false;
    return firstSegment !== "Inbox";
  }

  // ── Existing behaviour ───────────────────────────────────────────────────

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
      (vaultPath) => this.app.vault.adapter.getResourcePath(vaultPath),
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
    this.noteIndex.set(vaultPath, noteId);
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
    void this.saveState();
  }

  private async onNoteCancelled(noteId: string): Promise<void> {
    const entry = this.queueStore.get(noteId);
    if (entry) this.fireDesktopNotification("cancelled", entry, null);
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

  private onTerminalTransition(_prev: QueueEntry, next: QueueEntry): void {
    const status = next.status.toLowerCase();
    if (status === "ready" || status === "succeeded") {
      this.fireDesktopNotification("ready", next, next.vaultPath ?? null);
    } else if (status.startsWith("failed")) {
      this.fireDesktopNotification("failed", next, null);
    }
    void this.saveState();
  }

  private fireDesktopNotification(
    kind: "ready" | "failed" | "cancelled",
    entry: QueueEntry,
    vaultPath: string | null,
  ): void {
    if (!this.settings.desktopNotifications) return;
    if (!this.notifier.isEnabled()) return;
    if (kind === "ready") {
      this.notifier.notify({
        title: "Note ready",
        body: truncate(entry.title, 60),
        onClick: vaultPath ? () => void this.openVaultFile(vaultPath) : undefined,
      });
    } else if (kind === "failed") {
      const reason = entry.error ? truncate(entry.error, 100) : entry.label;
      this.notifier.notify({
        title: "Note failed",
        body: `${entry.label} — ${reason}`,
        onClick: () => void this.activateQueueView(),
      });
    } else {
      this.notifier.notify({
        title: "Note cancelled",
        body: "Your draft has been restored to the composer.",
      });
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

  applyChromeClasses(): void {
    document.body.toggleClass("tm-pixel-chrome", this.settings.pixelChrome);
    document.body.toggleClass("tm-action-effects", this.settings.actionEffects);
  }

  private setStatusBar(state: "idle" | "syncing" | "synced" | "error" | string): void {
    if (!this.statusBarEl) return;
    const el = this.statusBarEl;
    el.empty();
    el.addClass("tm-status");

    let icon: ThanyIconId;
    let label: string;
    let mood: "idle" | "ok" | "busy" | "error";
    const inFlight = this.queueStore.list().filter((e) => !isTerminal(e.status)).length;

    switch (state) {
      case "idle":
        icon = TM_ICON.check;
        label = "idle";
        mood = "idle";
        break;
      case "syncing":
        icon = TM_ICON.sync;
        label = inFlight > 0 ? `${inFlight} processing` : "syncing";
        mood = "busy";
        break;
      case "synced":
        icon = inFlight > 0 ? TM_ICON.sync : TM_ICON.check;
        label = inFlight > 0 ? `${inFlight} processing` : `synced ${formatTime(new Date())}`;
        mood = inFlight > 0 ? "busy" : "ok";
        break;
      case "error":
        icon = TM_ICON.alert;
        label = "error";
        mood = "error";
        break;
      default:
        icon = TM_ICON.sync;
        label = inFlight > 0 ? `${inFlight} processing` : state;
        mood = "busy";
    }

    el.createSpan({ cls: "tm-status__wordmark", text: "Thany-Marcus" });
    setIcon(el.createSpan({ cls: "tm-status__icon" }), icon);
    el.createSpan({ cls: "tm-status__label", text: label });

    el.removeClasses(["tm-status--idle", "tm-status--ok", "tm-status--busy", "tm-status--error"]);
    el.addClass(`tm-status--${mood}`);
    el.setAttr("aria-label", state === "error" ? "Thany: error — click to retry" : "Thany — click to open");
    el.onclick =
      state === "error" ? () => void this.syncLoop.runNow() : () => void this.activateQueueView();
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

function truncate(s: string, n: number): string {
  if (s.length <= n) return s;
  return `${s.slice(0, n - 1)}…`;
}

function newDeviceId(): string {
  const c = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (c?.randomUUID) return c.randomUUID();
  return "dev-" + Math.random().toString(36).slice(2) + Date.now().toString(36);
}
