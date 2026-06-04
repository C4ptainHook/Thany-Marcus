import { App, ItemView, Modal, Notice, Setting, WorkspaceLeaf, setIcon } from "obsidian";
import { TM_ICON, type ThanyIconId } from "../icons";
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
  private processingOpen = false;
  private suggestionsOpen = false;

  constructor(
    leaf: WorkspaceLeaf,
    private readonly store: QueueStore,
    private readonly onOpenNote: (vaultPath: string) => void,
    private readonly onReprocess: (noteId: string) => void,
    private readonly onCancel: (noteId: string) => void,
    private readonly suggestionActions: EntitySuggestionActions,
    private readonly hydrator: QueueHydrator,
    private readonly onNewDraft: () => void,
  ) {
    super(leaf);
  }

  getViewType(): string {
    return QUEUE_VIEW_TYPE;
  }

  getDisplayText(): string {
    return "Thany";
  }

  getIcon(): string {
    return TM_ICON.mark;
  }

  async onOpen(): Promise<void> {
    this.suggestionsHost = document.createElement("div");
    this.suggestionsPanel = new EntitySuggestionsPanel(this.suggestionActions, this.suggestionsHost, {
      notify: (m) => new Notice(m),
      onChange: () => this.render(),
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
    root.addClass("tm-hub");

    const header = root.createDiv({ cls: "tm-hub__header" });
    header.createSpan({ cls: "tm-hub__title", text: "Thany-Marcus" });

    const newBtn = root.createEl("button", { cls: "tm-hub__new" });
    setIcon(newBtn.createSpan({ cls: "tm-hub__new-icon" }), TM_ICON.plus);
    newBtn.createSpan({ cls: "tm-hub__new-label", text: "New draft" });
    newBtn.onclick = () => this.onNewDraft();

    this.renderProcessing(root);
    this.renderSuggestions(root);
  }

  private sectionHead(
    section: HTMLElement,
    name: string,
    open: boolean,
    onToggle: () => void,
  ): HTMLElement {
    const head = section.createDiv({ cls: "tm-section__head" });
    setIcon(
      head.createSpan({ cls: "tm-section__chevron" }),
      open ? TM_ICON.collapse : TM_ICON.expand,
    );
    head.createSpan({ cls: "tm-section__name", text: name });
    head.onclick = onToggle;
    return head;
  }

  private renderProcessing(root: HTMLElement): void {
    const entries = this.store.list();
    const inFlight = entries.filter((e) => !isTerminal(e.status)).length;
    const open = inFlight > 0 || this.processingOpen;

    const section = root.createDiv({ cls: "tm-section" });
    const head = this.sectionHead(section, "Processing", open, () => {
      this.processingOpen = !open;
      this.render();
    });
    const summary = head.createSpan({ cls: "tm-section__summary" });
    this.stateChip(summary, inFlight > 0 ? TM_ICON.sync : TM_ICON.check, inFlight > 0
      ? `${inFlight} processing`
      : "all synced");

    if (!open) return;

    const list = section.createDiv({ cls: "tm-queue__list" });
    if (entries.length === 0) {
      list.createDiv({ cls: "tm-queue__empty", text: "Nothing recent." });
      return;
    }
    for (const e of entries) this.renderQueueRow(list, e);
  }

  private renderQueueRow(list: HTMLElement, e: ReturnType<QueueStore["list"]>[number]): void {
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

  private renderSuggestions(root: HTMLElement): void {
    const section = root.createDiv({ cls: "tm-section" });
    const head = this.sectionHead(section, "Suggestions", this.suggestionsOpen, () => {
      this.suggestionsOpen = !this.suggestionsOpen;
      this.render();
    });
    const count = this.suggestionsPanel?.getCount() ?? 0;
    head.createSpan({ cls: "tm-section__badge", text: `${count}` });

    if (this.suggestionsOpen && this.suggestionsHost) {
      section.appendChild(this.suggestionsHost);
    }
  }

  private stateChip(parent: HTMLElement, icon: ThanyIconId, label: string): void {
    setIcon(parent.createSpan({ cls: "tm-section__summary-icon" }), icon);
    parent.createSpan({ cls: "tm-section__summary-label", text: label });
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
