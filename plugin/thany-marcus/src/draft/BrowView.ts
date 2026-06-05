import { Notice, setIcon, type App, type MarkdownView, type TFile } from "obsidian";
import { type AttachmentMode, type DraftManager, type DraftState } from "./DraftManager";
import { MicRecorder } from "./MicRecorder";
import { PasteInterceptor } from "./PasteInterceptor";
import { Submitter } from "./Submitter";
import { sha256Hex } from "../sync/AttachmentDownloader";
import { TM_ICON, type ThanyIconId } from "../icons";
import {
  RelatedNotesPanel,
  type RelatedNotesFetcher,
} from "../related/RelatedNotesPanel";
import { BLOCK_MIN_CHARS, extractCursorBlock } from "../related/cursorBlock";
import { type RelatedStrictness } from "../related/strictness";
import { type RelatedNotesItem } from "../api";

export interface BrowHandlers {
  onSubmitted: (noteId: string, draftLeafFile: TFile) => void;
  onDiscarded: () => void;
}

export class BrowView {
  private container: HTMLElement | null = null;
  private attachBar: HTMLElement | null = null;
  private relatedContainer: HTMLElement | null = null;
  private relatedPanel: RelatedNotesPanel | null = null;
  private editorChangeUnsub: (() => void) | null = null;
  private cursorUnsub: (() => void) | null = null;
  private interceptor: PasteInterceptor;
  private recorder = new MicRecorder();
  private recordingTimer: number | null = null;
  private submitting = false;
  private attachExpanded = false;
  private closePopover: (() => void) | null = null;

  constructor(
    private readonly drafts: DraftManager,
    private readonly submitter: Submitter,
    private readonly recordMimeType: () => string,
    private readonly handlers: BrowHandlers,
    private readonly app: App,
    private readonly related: RelatedNotesFetcher,
    private readonly resourceUrl: (vaultPath: string) => string,
    private readonly strictness: () => RelatedStrictness,
    private readonly onStrictnessChange: (s: RelatedStrictness) => void,
  ) {
    this.interceptor = new PasteInterceptor(drafts, () => this.render());
  }

  attach(view: MarkdownView, state: DraftState): void {
    this.detach();
    const contentEl = view.contentEl;
    const root = document.createElement("div");
    root.addClass("tm-brow");
    contentEl.prepend(root);
    this.container = root;

    const attachRoot = document.createElement("div");
    attachRoot.addClass("tm-attach");
    root.insertAdjacentElement("afterend", attachRoot);
    this.attachBar = attachRoot;

    const relatedRoot = document.createElement("div");
    relatedRoot.addClass("tm-related");
    attachRoot.insertAdjacentElement("afterend", relatedRoot);
    this.relatedContainer = relatedRoot;
    this.relatedPanel = new RelatedNotesPanel(this.related, relatedRoot, {
      minChars: BLOCK_MIN_CHARS,
      strictness: this.strictness(),
      onItemClick: (item) => {
        void this.app.workspace.openLinkText(item.relativePath, "", false);
      },
      onItemInsert: (item) => this.insertLink(item),
      onStrictnessChange: (s) => this.onStrictnessChange(s),
    });

    const fire = () => {
      const editor = view.editor;
      if (!editor) return;
      this.relatedPanel?.onContextChange(extractCursorBlock(editor, BLOCK_MIN_CHARS));
    };
    const evt = this.app.workspace.on("editor-change", fire);
    this.editorChangeUnsub = () => this.app.workspace.offref(evt);

    const onCursor = () => fire();
    contentEl.addEventListener("keyup", onCursor);
    contentEl.addEventListener("mouseup", onCursor);
    this.cursorUnsub = () => {
      contentEl.removeEventListener("keyup", onCursor);
      contentEl.removeEventListener("mouseup", onCursor);
    };
    fire();

    const editorRoot = contentEl;
    this.interceptor.attach(editorRoot, state);
    this.recorder = new MicRecorder(this.recordMimeType());
    this.state = state;
    this.view = view;
    this.render();
  }

  detach(): void {
    this.closePopover?.();
    this.interceptor.detach();
    this.recorder.cancel();
    if (this.recordingTimer !== null) {
      window.clearInterval(this.recordingTimer);
      this.recordingTimer = null;
    }
    if (this.editorChangeUnsub) {
      this.editorChangeUnsub();
      this.editorChangeUnsub = null;
    }
    if (this.cursorUnsub) {
      this.cursorUnsub();
      this.cursorUnsub = null;
    }
    this.relatedPanel?.dispose();
    this.relatedPanel = null;
    this.relatedContainer?.remove();
    this.relatedContainer = null;
    this.attachBar?.remove();
    this.attachBar = null;
    this.container?.remove();
    this.container = null;
    this.state = null;
    this.view = null;
  }

  private state: DraftState | null = null;
  private view: MarkdownView | null = null;

  private insertLink(item: RelatedNotesItem): void {
    const view = this.view;
    if (!view) return;
    const editor = view.editor;
    if (!editor) return;
    const sourcePath = view.file?.path ?? "";
    const file = this.app.metadataCache.getFirstLinkpathDest(item.relativePath, sourcePath);
    const link = file
      ? this.app.fileManager.generateMarkdownLink(file, sourcePath)
      : buildFallbackWikilink(item.relativePath);
    editor.replaceSelection(link);
    editor.focus();
  }

  private render(): void {
    this.renderStrip();
    this.renderAttachBar();
  }

  private renderStrip(): void {
    if (!this.container || !this.state) return;
    this.container.empty();
    const recording = this.recorder.isRecording();

    const left = this.container.createDiv({ cls: "tm-brow__left" });

    left.createDiv({ cls: "tm-brow__mark", text: "Thany-Marcus" });

    const micBtn = this.iconButton(
      left,
      TM_ICON.mic,
      recording ? `${formatMs(this.recorder.elapsedMs())} · stop` : "mic",
      recording ? "tm-brow__btn--mic is-recording" : "tm-brow__btn--mic",
    );
    micBtn.onclick = () => void this.toggleMic();

    const attachBtn = this.iconButton(left, TM_ICON.attach, "attach");
    attachBtn.onclick = () => this.openFilePicker();

    const urlBtn = this.iconButton(left, TM_ICON.link, "URL");
    urlBtn.onclick = () => this.promptUrl();

    if (recording) {
      const cancelBtn = this.iconButton(left, TM_ICON.close, "cancel", "tm-brow__btn--cancel-rec");
      cancelBtn.onclick = () => {
        this.recorder.cancel();
        if (this.recordingTimer !== null) {
          window.clearInterval(this.recordingTimer);
          this.recordingTimer = null;
        }
        this.render();
      };
    }

    const right = this.container.createDiv({ cls: "tm-brow__right" });
    const discardBtn = right.createEl("button", {
      cls: "tm-brow__btn tm-brow__btn--discard",
      text: "Discard",
    });
    discardBtn.onclick = () => void this.discard();

    const sendBtn = this.iconButton(
      right,
      TM_ICON.check,
      this.submitting ? "Sending…" : "Send",
      "tm-brow__btn--send mod-cta",
    );
    sendBtn.disabled = recording || this.submitting;
    sendBtn.onclick = () => {
      sendBtn.addClass("is-transmitting");
      void this.submit();
    };
  }

  private renderAttachBar(): void {
    if (!this.attachBar || !this.state) return;
    this.attachBar.empty();
    const attachments = this.state.attachments;
    if (attachments.length === 0) {
      this.attachBar.removeClass("is-open");
      return;
    }
    this.attachBar.toggleClass("is-open", this.attachExpanded);

    const summary = this.attachBar.createDiv({ cls: "tm-attach__summary" });
    setIcon(summary.createSpan({ cls: "tm-attach__summary-icon" }), TM_ICON.attach);
    summary.createSpan({
      cls: "tm-attach__summary-label",
      text: `${attachments.length} attached`,
    });
    setIcon(
      summary.createSpan({ cls: "tm-attach__summary-chevron" }),
      this.attachExpanded ? TM_ICON.collapse : TM_ICON.expand,
    );
    summary.onclick = () => {
      this.attachExpanded = !this.attachExpanded;
      this.renderAttachBar();
    };

    if (!this.attachExpanded) return;

    const film = this.attachBar.createDiv({ cls: "tm-attach__film" });
    for (const att of attachments) this.renderCard(film, att);
  }

  private renderCard(film: HTMLElement, att: DraftState["attachments"][number]): void {
    const state = this.state;
    if (!state) return;
    const card = film.createDiv({ cls: `tm-attach__card tm-attach__card--${att.kind}` });

    const preview = card.createDiv({ cls: "tm-attach__preview" });
    if (att.kind === "image" && att.vaultPath) {
      const img = preview.createEl("img", { cls: "tm-attach__thumb" });
      img.src = this.resourceUrl(att.vaultPath);
      img.alt = att.filename ?? "image";
    } else if (att.kind === "voice") {
      setIcon(preview.createSpan({ cls: "tm-attach__preview-icon" }), TM_ICON.voice);
      if (att.vaultPath) {
        const play = preview.createEl("button", { cls: "tm-attach__play", text: "▶" });
        play.onclick = (ev) => {
          ev.stopPropagation();
          void new Audio(this.resourceUrl(att.vaultPath!)).play().catch(() => undefined);
        };
      }
    } else if (att.kind === "url") {
      setIcon(preview.createSpan({ cls: "tm-attach__preview-icon" }), TM_ICON.link);
    } else {
      setIcon(preview.createSpan({ cls: "tm-attach__preview-icon" }), TM_ICON.file);
    }

    const meta = card.createDiv({ cls: "tm-attach__meta" });
    meta.createDiv({ cls: "tm-attach__name", text: cardTitle(att) });
    const sub = cardSubtitle(att);
    if (sub) meta.createDiv({ cls: "tm-attach__sub", text: sub });

    const modes = card.createDiv({ cls: "tm-attach__modes" });
    for (const m of validModes(att.kind)) {
      const seg = modes.createEl("button", {
        cls: `tm-attach__mode${att.mode === m ? " is-active" : ""}`,
        text: modeLabel(m),
      });
      seg.setAttr("aria-label", modeTooltip(m));
      seg.title = modeTooltip(m);
      seg.onclick = (ev) => {
        ev.stopPropagation();
        att.mode = m;
        this.renderAttachBar();
      };
    }

    const remove = card.createEl("button", { cls: "tm-attach__remove" });
    setIcon(remove.createSpan({ cls: "tm-attach__remove-icon" }), TM_ICON.close);
    remove.setAttr("aria-label", "Remove attachment");
    remove.onclick = async (ev) => {
      ev.stopPropagation();
      await this.drafts.removeAttachment(state, att.clientAttachmentId);
      this.renderAttachBar();
    };
  }

  private iconButton(
    parent: HTMLElement,
    icon: ThanyIconId,
    label: string,
    extraCls = "",
  ): HTMLButtonElement {
    const btn = parent.createEl("button", { cls: `tm-brow__btn ${extraCls}`.trim() });
    setIcon(btn.createSpan({ cls: "tm-brow__btn-icon" }), icon);
    btn.createSpan({ cls: "tm-brow__btn-label", text: label });
    return btn;
  }

  private openFilePicker(): void {
    const input = document.createElement("input");
    input.type = "file";
    input.multiple = true;
    input.onchange = async () => {
      if (!this.state) return;
      const files = input.files ? Array.from(input.files) : [];
      for (const file of files) {
        const bytes = new Uint8Array(await file.arrayBuffer());
        const sha = await sha256Hex(bytes);
        const kind: "image" | "voice" | "file" = file.type.startsWith("image/")
          ? "image"
          : file.type.startsWith("audio/")
            ? "voice"
            : "file";
        await this.drafts.addBinaryAttachment(
          this.state,
          bytes.buffer as ArrayBuffer,
          kind,
          file.name,
          file.type || null,
          sha,
        );
      }
      this.attachExpanded = true;
      this.renderAttachBar();
    };
    input.click();
  }

  private async toggleMic(): Promise<void> {
    if (!this.state) return;
    if (this.recorder.isRecording()) {
      try {
        const { blob, mimeType } = await this.recorder.stop();
        const bytes = new Uint8Array(await blob.arrayBuffer());
        const sha = await sha256Hex(bytes);
        const ext = mimeType.includes("webm") ? "webm" : mimeType.includes("ogg") ? "ogg" : "bin";
        const name = `voice-${Date.now()}.${ext}`;
        await this.drafts.addBinaryAttachment(
          this.state,
          bytes.buffer as ArrayBuffer,
          "voice",
          name,
          mimeType,
          sha,
        );
        this.attachExpanded = true;
      } catch (e) {
        new Notice(`Thany: mic error — ${(e as Error).message}`);
      }
      if (this.recordingTimer !== null) {
        window.clearInterval(this.recordingTimer);
        this.recordingTimer = null;
      }
      this.render();
      return;
    }
    try {
      await this.recorder.start();
      this.recordingTimer = window.setInterval(() => this.renderStrip(), 500);
      this.render();
    } catch (e) {
      new Notice(`Thany: mic permission denied — ${(e as Error).message}`);
    }
  }

  private promptUrl(): void {
    if (!this.container || !this.state) return;
    this.closePopover?.();
    const popover = this.container.createDiv({ cls: "tm-brow__popover" });
    popover.createDiv({ cls: "tm-brow__popover-title", text: "Attach a URL" });
    const input = popover.createEl("input", {
      cls: "tm-brow__popover-input",
      attr: { type: "url", placeholder: "https://…" },
    });

    const onOutside = (e: MouseEvent) => {
      if (!popover.contains(e.target as Node)) close();
    };
    const close = () => {
      document.removeEventListener("mousedown", onOutside, true);
      this.closePopover = null;
      popover.remove();
    };
    this.closePopover = close;
    window.setTimeout(() => document.addEventListener("mousedown", onOutside, true), 0);

    const submit = () => {
      if (!this.state) return;
      const raw = input.value.trim();
      if (!raw) return;
      const url = /^https?:\/\//i.test(raw) ? raw : `https://${raw}`;
      const existing = new Set(
        this.state.attachments
          .filter((a) => a.kind === "url")
          .map((a) => String((a.extra as { url?: string }).url ?? "")),
      );
      close();
      if (existing.has(url)) {
        new Notice("Thany: URL already attached");
        return;
      }
      this.drafts.addUrlAttachment(this.state, url);
      this.attachExpanded = true;
      this.render();
    };

    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        submit();
      } else if (e.key === "Escape") {
        close();
      }
    });

    const actions = popover.createDiv({ cls: "tm-brow__popover-actions" });
    const add = actions.createEl("button", { cls: "tm-brow__popover-add", text: "Attach" });
    add.onclick = submit;
    const cancel = actions.createEl("button", { cls: "tm-brow__popover-close", text: "Cancel" });
    cancel.onclick = () => close();

    input.focus();
  }

  private async submit(): Promise<void> {
    if (!this.state || !this.view) return;
    const file = this.view.file;
    if (!file) return;
    if (this.recorder.isRecording()) return;

    this.submitting = true;
    this.render();
    try {
      const result = await this.submitter.submit(this.state, file);
      this.handlers.onSubmitted(result.noteId, file);
    } catch (e) {
      this.submitting = false;
      new Notice(`Thany: submit failed — ${(e as Error).message}`);
      this.render();
    }
  }

  private async discard(): Promise<void> {
    if (!this.state) return;
    await this.drafts.discard(this.state);
    this.handlers.onDiscarded();
  }
}

export function buildFallbackWikilink(relativePath: string): string {
  const base = relativePath.split("/").pop() ?? relativePath;
  const name = base.replace(/\.md$/i, "");
  return `[[${name}]]`;
}

function validModes(kind: string): AttachmentMode[] {
  return kind === "url" ? ["extract", "metadata", "reference"] : ["extract", "reference"];
}

function modeLabel(mode: AttachmentMode): string {
  if (mode === "reference") return "REF";
  if (mode === "metadata") return "META";
  return "EXTRACT";
}

function modeTooltip(mode: AttachmentMode): string {
  if (mode === "reference") return "Reference — attached, not processed into the note";
  if (mode === "metadata") return "Metadata — fetch title/description only";
  return "Extract — process the content into the note";
}

function cardTitle(att: { kind: string; filename: string | null; extra: Record<string, unknown> }): string {
  if (att.kind === "url") {
    const url = String(att.extra.url ?? "");
    try {
      return new URL(url).hostname.replace(/^www\./, "");
    } catch {
      return url.replace(/^https?:\/\//, "");
    }
  }
  return att.filename ?? att.kind;
}

function cardSubtitle(att: { kind: string; byteSize: number | null; extra: Record<string, unknown> }): string | null {
  if (att.kind === "url") {
    const url = String(att.extra.url ?? "");
    return url.replace(/^https?:\/\//, "");
  }
  return att.byteSize != null ? formatBytes(att.byteSize) : null;
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

function formatMs(ms: number): string {
  const total = Math.floor(ms / 1000);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${s.toString().padStart(2, "0")}`;
}
