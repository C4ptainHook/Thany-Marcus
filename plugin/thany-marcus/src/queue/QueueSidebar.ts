import { ItemView, WorkspaceLeaf } from "obsidian";
import { isFailure, isTerminal } from "./labels";
import type { QueueStore } from "./QueueStore";

export const QUEUE_VIEW_TYPE = "thany-marcus-queue";

export class QueueSidebarView extends ItemView {
  private unsubscribe: (() => void) | null = null;

  constructor(
    leaf: WorkspaceLeaf,
    private readonly store: QueueStore,
    private readonly onOpenNote: (vaultPath: string) => void,
    private readonly onReprocess: (noteId: string) => void,
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
    this.unsubscribe = this.store.subscribe(() => this.render());
    this.render();
  }

  async onClose(): Promise<void> {
    this.unsubscribe?.();
    this.unsubscribe = null;
  }

  private render(): void {
    const root = this.containerEl.children[1] as HTMLElement;
    root.empty();
    root.addClass("tm-queue");

    const header = root.createDiv({ cls: "tm-queue__header" });
    header.setText("Recent notes");

    const list = root.createDiv({ cls: "tm-queue__list" });
    const entries = this.store.list();
    if (entries.length === 0) {
      list.createDiv({ cls: "tm-queue__empty", text: "No recent submissions" });
      return;
    }

    for (const e of entries) {
      const hasFailures = (e.extractionFailures?.length ?? 0) > 0;
      const row = list.createDiv({ cls: "tm-queue__row" });
      if (isFailure(e.status)) row.addClass("tm-queue__row--failed");
      else if (hasFailures) row.addClass("tm-queue__row--ready", "tm-queue__row--has-failures");
      else if (isTerminal(e.status)) row.addClass("tm-queue__row--ready");
      else row.addClass("tm-queue__row--in-flight");

      const title = row.createDiv({ cls: "tm-queue__title", text: e.title });
      title.setAttr("title", e.noteId);
      const badgeText = hasFailures
        ? `${e.label} · ${e.extractionFailures!.length} failure${e.extractionFailures!.length === 1 ? "" : "s"}`
        : e.label;
      row.createDiv({ cls: "tm-queue__badge", text: badgeText });

      if (e.error) {
        row.createDiv({ cls: "tm-queue__error", text: e.error });
      }

      if (hasFailures) {
        const fb = row.createDiv({ cls: "tm-queue__failures" });
        for (const f of e.extractionFailures!) {
          fb.createDiv({ text: `${f.kind}: ${f.reason}` });
        }
        const btn = row.createEl("button", { cls: "tm-queue__reprocess", text: "Reprocess" });
        btn.onclick = (ev) => {
          ev.stopPropagation();
          this.onReprocess(e.noteId);
        };
      }

      if (e.vaultPath) {
        row.addEventListener("click", () => this.onOpenNote(e.vaultPath!));
        row.addClass("tm-queue__row--clickable");
      }
    }
  }
}

