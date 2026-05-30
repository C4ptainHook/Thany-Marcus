import { App, ItemView, Modal, Notice, Setting, WorkspaceLeaf } from "obsidian";
import type { ProjectDto } from "../api";
import { isCancellable, isFailure, isTerminal } from "./labels";
import type { QueueStore } from "./QueueStore";

export const QUEUE_VIEW_TYPE = "thany-marcus-queue";

export interface ProjectActions {
  list: () => Promise<ProjectDto[]>;
  create: (name: string, description?: string) => Promise<ProjectDto>;
  remove: (id: string) => Promise<void>;
}

export class QueueSidebarView extends ItemView {
  private unsubscribe: (() => void) | null = null;
  private projects: ProjectDto[] = [];

  constructor(
    leaf: WorkspaceLeaf,
    private readonly store: QueueStore,
    private readonly onOpenNote: (vaultPath: string) => void,
    private readonly onReprocess: (noteId: string) => void,
    private readonly onCancel: (noteId: string) => void,
    private readonly projectActions: ProjectActions,
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
    void this.refreshProjects();
  }

  async onClose(): Promise<void> {
    this.unsubscribe?.();
    this.unsubscribe = null;
  }

  private async refreshProjects(): Promise<void> {
    try {
      this.projects = await this.projectActions.list();
      this.render();
    } catch (e) {
      console.error("[Thany] listProjects failed", e);
    }
  }

  private render(): void {
    const root = this.containerEl.children[1] as HTMLElement;
    root.empty();
    root.addClass("tm-queue");

    this.renderProjectsSection(root);

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

  private renderProjectsSection(root: HTMLElement): void {
    const wrap = root.createDiv({ cls: "tm-projects" });
    const head = wrap.createDiv({ cls: "tm-projects__header" });
    head.createSpan({ text: "Projects" });
    const addBtn = head.createEl("button", { cls: "tm-projects__add", text: "+ New" });
    addBtn.onclick = () => void this.promptCreateProject();

    if (this.projects.length === 0) {
      wrap.createDiv({
        cls: "tm-projects__empty",
        text: "No projects yet — new notes land in Inbox.",
      });
      return;
    }

    const list = wrap.createDiv({ cls: "tm-projects__list" });
    for (const p of this.projects) {
      const row = list.createDiv({ cls: "tm-projects__row" });
      const name = row.createDiv({ cls: "tm-projects__name", text: p.name });
      if (p.description) name.setAttr("title", p.description);
      row.createDiv({ cls: "tm-projects__count", text: String(p.mentionCount) });
      const del = row.createEl("button", { cls: "tm-projects__delete", text: "×" });
      del.setAttr("title", `Delete project '${p.name}'`);
      del.onclick = (ev) => {
        ev.stopPropagation();
        void this.confirmDeleteProject(p);
      };
    }
  }

  private promptCreateProject(): void {
    new CreateProjectModal(this.app, async (name, description) => {
      try {
        await this.projectActions.create(name, description);
        new Notice(`Project '${name}' created`);
        await this.refreshProjects();
      } catch (e) {
        new Notice(`Create failed: ${(e as Error).message}`);
      }
    }).open();
  }

  private confirmDeleteProject(p: ProjectDto): void {
    new ConfirmModal(
      this.app,
      `Delete project '${p.name}'?`,
      "Notes already routed there keep their folder.",
      async () => {
        try {
          await this.projectActions.remove(p.id);
          new Notice(`Project '${p.name}' deleted`);
          await this.refreshProjects();
        } catch (e) {
          new Notice(`Delete failed: ${(e as Error).message}`);
        }
      },
    ).open();
  }
}

class CreateProjectModal extends Modal {
  private name = "";
  private description = "";

  constructor(
    app: App,
    private readonly onSubmit: (name: string, description?: string) => Promise<void>,
  ) {
    super(app);
  }

  onOpen(): void {
    const { contentEl } = this;
    contentEl.empty();
    contentEl.createEl("h3", { text: "New project" });

    new Setting(contentEl)
      .setName("Name")
      .setDesc("Will become the vault folder for routed notes")
      .addText((t) => {
        t.setPlaceholder("e.g. Reply.io").onChange((v) => (this.name = v));
        setTimeout(() => t.inputEl.focus(), 0);
      });

    new Setting(contentEl)
      .setName("Description")
      .setDesc("Optional. Helps the router pick the right project.")
      .addText((t) => t.setPlaceholder("optional").onChange((v) => (this.description = v)));

    new Setting(contentEl)
      .addButton((b) =>
        b.setButtonText("Cancel").onClick(() => this.close()),
      )
      .addButton((b) =>
        b
          .setButtonText("Create")
          .setCta()
          .onClick(async () => {
            const name = this.name.trim();
            if (!name) {
              new Notice("Name is required");
              return;
            }
            const description = this.description.trim() || undefined;
            this.close();
            await this.onSubmit(name, description);
          }),
      );
  }

  onClose(): void {
    this.contentEl.empty();
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

