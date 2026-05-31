import { App, ItemView, Modal, Notice, Setting, WorkspaceLeaf } from "obsidian";
import type { ListJobsResponse } from "../api";
import {
  EntitySuggestionsPanel,
  type EntitySuggestionActions,
} from "../sidebar/EntitySuggestionsPanel";
import { isCancellable, isFailure, isTerminal } from "./labels";
import { visibleFailures } from "./queueFailures";
import type { QueueStore } from "./QueueStore";

export const QUEUE_VIEW_TYPE = "thany-marcus-queue";

export interface QueueHydrator {
  listActiveJobs: (includeRecent?: boolean) => Promise<ListJobsResponse>;
}

export async function hydrateQueueFromCloud(
  hydrator: QueueHydrator,
  store: QueueStore,
): Promise<void> {
  try {
    const { active, recent } = await hydrator.listActiveJobs(true);
    store.hydrate([...active, ...recent]);
  } catch (e) {
    console.error("[Thany] listActiveJobs failed", e);
  }
}

export class QueueSidebarView extends ItemView {
  private unsubscribe: (() => void) | null = null;
  private suggestionsPanel: EntitySuggestionsPanel | null = null;
  private suggestionsHost: HTMLElement | null = null;

  constructor(
    leaf: WorkspaceLeaf,
    private readonly store: QueueStore,
    private readonly onOpenNote: (vaultPath: string) => void,
    private readonly onReprocess: (noteId: string) => void,
    private readonly onCancel: (noteId: string) => void,
    private readonly suggestionActions: EntitySuggestionActions,
    private readonly hydrator: QueueHydrator,
  ) {
    super(leaf);
  }

  getViewType(): string {
    return QUEUE_VIEW_TYPE;
  }

  getDisplayText(): string {
    return "Thany queue";
  }

  getIcon(): string {
    return "cloud";
  }

  async onOpen(): Promise<void> {
    this.suggestionsHost = document.createElement("div");
    this.suggestionsPanel = new EntitySuggestionsPanel(this.suggestionActions, this.suggestionsHost, {
      notify: (m) => new Notice(m),
    });
    this.suggestionsPanel.start();

    this.unsubscribe = this.store.subscribe(() => this.render());

    await hydrateQueueFromCloud(this.hydrator, this.store);

    this.render();
  }

  async onClose(): Promise<void> {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.suggestionsPanel?.dispose();
    this.suggestionsPanel = null;
    this.suggestionsHost = null;
  }

  private render(): void {
    const root = this.containerEl.children[1] as HTMLElement;
    root.empty();
    root.addClass("tm-queue");

    // The suggestions panel owns its own host element so its DOM + polling survive the
    // queue's full re-render on every store change — we just re-parent it each time.
    if (this.suggestionsHost) root.appendChild(this.suggestionsHost);

    const header = root.createDiv({ cls: "tm-queue__header" });
    header.setText("Recent notes");

    const list = root.createDiv({ cls: "tm-queue__list" });
    const entries = this.store.list();
    if (entries.length === 0) {
      list.createDiv({ cls: "tm-queue__empty", text: "No recent submissions" });
      return;
    }

    for (const e of entries) {
      const displayFailures = visibleFailures(e.extractionFailures);
      const hasFailures = displayFailures.length > 0;
      const row = list.createDiv({ cls: "tm-queue__row" });
      if (isFailure(e.status)) row.addClass("tm-queue__row--failed");
      else if (hasFailures) row.addClass("tm-queue__row--ready", "tm-queue__row--has-failures");
      else if (isTerminal(e.status)) row.addClass("tm-queue__row--ready");
      else row.addClass("tm-queue__row--in-flight");

      const title = row.createDiv({ cls: "tm-queue__title", text: e.title });
      title.setAttr("title", e.noteId);
      const badgeText = hasFailures
        ? `${e.label} · ${displayFailures.length} failure${displayFailures.length === 1 ? "" : "s"}`
        : e.label;
      row.createDiv({ cls: "tm-queue__badge", text: badgeText });

      if (e.error) {
        row.createDiv({ cls: "tm-queue__error", text: e.error });
      }

      if (hasFailures) {
        const fb = row.createDiv({ cls: "tm-queue__failures" });
        for (const f of displayFailures) {
          fb.createDiv({ text: `${f.kind}: ${f.reason}` });
        }
        const btn = row.createEl("button", { cls: "tm-queue__reprocess", text: "Reprocess" });
        btn.onclick = (ev) => {
          ev.stopPropagation();
          this.onReprocess(e.noteId);
        };
      }

      if (isCancellable(e.status)) {
        const cancelBtn = row.createEl("button", { cls: "tm-queue__cancel", text: "Cancel" });
        cancelBtn.onclick = (ev) => {
          ev.stopPropagation();
          new ConfirmModal(
            this.app,
            "Cancel this note?",
            "Your draft will be restored to the composer. Work already done will be discarded.",
            async () => this.onCancel(e.noteId),
          ).open();
        };
      }

      if (e.vaultPath) {
        row.addEventListener("click", () => this.onOpenNote(e.vaultPath!));
        row.addClass("tm-queue__row--clickable");
      }
    }
  }
}

class ConfirmModal extends Modal {
  constructor(
    app: App,
    private readonly title: string,
    private readonly message: string,
    private readonly onConfirm: () => Promise<void>,
  ) {
    super(app);
  }

  onOpen(): void {
    const { contentEl } = this;
    contentEl.empty();
    contentEl.createEl("h3", { text: this.title });
    contentEl.createEl("p", { text: this.message });

    new Setting(contentEl)
      .addButton((b) => b.setButtonText("Cancel").onClick(() => this.close()))
      .addButton((b) =>
        b
          .setButtonText("Delete")
          .setWarning()
          .onClick(async () => {
            this.close();
            await this.onConfirm();
          }),
      );
  }

  onClose(): void {
    this.contentEl.empty();
  }
}
