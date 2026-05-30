import { Notice, type MarkdownView, type TFile, type Workspace } from "obsidian";
import type { DraftManager, DraftState } from "./DraftManager";
import { MicRecorder } from "./MicRecorder";
import { PasteInterceptor } from "./PasteInterceptor";
import { Submitter } from "./Submitter";
import { findUrls } from "./UrlPromoter";
import { sha256Hex } from "../sync/AttachmentDownloader";
import {
  RelatedNotesPanel,
  type RelatedNotesFetcher,
} from "../related/RelatedNotesPanel";

export interface BrowHandlers {
  onSubmitted: (noteId: string, draftLeafFile: TFile) => void;
  onDiscarded: () => void;
}

export class BrowView {
  private container: HTMLElement | null = null;
  private relatedContainer: HTMLElement | null = null;
  private relatedPanel: RelatedNotesPanel | null = null;
  private editorChangeUnsub: (() => void) | null = null;
  private interceptor: PasteInterceptor;
  private recorder = new MicRecorder();
  private recordingTimer: number | null = null;
  private submitting = false;

  constructor(
    private readonly drafts: DraftManager,
    private readonly submitter: Submitter,
    private readonly recordMimeType: () => string,
    private readonly handlers: BrowHandlers,
    private readonly workspace: Workspace,
    private readonly related: RelatedNotesFetcher,
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

    const relatedRoot = document.createElement("div");
    relatedRoot.addClass("tm-related");
    root.insertAdjacentElement("afterend", relatedRoot);
    this.relatedContainer = relatedRoot;
    this.relatedPanel = new RelatedNotesPanel(this.related, relatedRoot, {
      onItemClick: (item) => {
        void this.workspace.openLinkText(item.relativePath, "", false);
      },
    });

    const handler = () => {
      const body = view.editor?.getValue() ?? "";
      this.relatedPanel?.onBodyChange(body);
    };
    const evt = this.workspace.on("editor-change", handler);
    this.editorChangeUnsub = () => this.workspace.offref(evt);
    handler();

    const editorRoot = contentEl;
    this.interceptor.attach(editorRoot, state);
    this.recorder = new MicRecorder(this.recordMimeType());
    this.state = state;
    this.view = view;
    this.render();
  }

  detach(): void {
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
    this.relatedPanel?.dispose();
    this.relatedPanel = null;
    this.relatedContainer?.remove();
    this.relatedContainer = null;
    this.container?.remove();
    this.container = null;
    this.state = null;
    this.view = null;
  }

  private state: DraftState | null = null;
  private view: MarkdownView | null = null;

  private render(): void {
    if (!this.container || !this.state) return;
    this.container.empty();
    const state = this.state;
    const recording = this.recorder.isRecording();

    const left = this.container.createDiv({ cls: "tm-brow__left" });

    const micBtn = left.createEl("button", {
      cls: `tm-brow__btn tm-brow__btn--mic${recording ? " is-recording" : ""}`,
      text: recording ? `● ${formatMs(this.recorder.elapsedMs())} stop` : "mic",
    });
    micBtn.onclick = () => void this.toggleMic();

    const attachBtn = left.createEl("button", {
      cls: "tm-brow__btn",
      text: "attach",
    });
    attachBtn.onclick = () => this.openFilePicker();

    const urlBtn = left.createEl("button", {
      cls: "tm-brow__btn",
      text: "+ URL",
    });
    urlBtn.onclick = () => void this.promoteUrl();

    if (recording) {
      const cancelBtn = left.createEl("button", {
        cls: "tm-brow__btn tm-brow__btn--cancel-rec",
        text: "cancel",
      });
      cancelBtn.onclick = () => {
        this.recorder.cancel();
        if (this.recordingTimer !== null) {
          window.clearInterval(this.recordingTimer);
          this.recordingTimer = null;
        }
        this.render();
      };
    }

    const chips = this.container.createDiv({ cls: "tm-brow__chips" });
    for (const att of state.attachments) {
      const chip = chips.createDiv({ cls: `tm-brow__chip tm-brow__chip--${att.kind}` });
      chip.createSpan({ cls: "tm-brow__chip-label", text: chipLabel(att) });
      const remove = chip.createEl("button", { cls: "tm-brow__chip-x", text: "×" });
      remove.onclick = async () => {
        await this.drafts.removeAttachment(state, att.clientAttachmentId);
        this.render();
      };
    }

    const right = this.container.createDiv({ cls: "tm-brow__right" });
    const discardBtn = right.createEl("button", {
      cls: "tm-brow__btn tm-brow__btn--discard",
      text: "Discard",
    });
    discardBtn.onclick = () => void this.discard();

    const sendBtn = right.createEl("button", {
      cls: "tm-brow__btn tm-brow__btn--send mod-cta",
      text: this.submitting ? "Sending…" : "Send",
    });
    sendBtn.disabled = recording || this.submitting;
    sendBtn.onclick = () => void this.submit();
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
      this.render();
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
      this.recordingTimer = window.setInterval(() => this.render(), 500);
      this.render();
    } catch (e) {
      new Notice(`Thany: mic permission denied — ${(e as Error).message}`);
    }
  }

  private async promoteUrl(): Promise<void> {
    if (!this.state || !this.view) return;
    const body = this.view.editor.getValue();
    const urls = findUrls(body);
    if (urls.length === 0) {
      new Notice("Thany: no URLs found in body");
      return;
    }
    const existing = new Set(
      this.state.attachments
        .filter((a) => a.kind === "url")
        .map((a) => String((a.extra as { url?: string }).url ?? "")),
    );
    const candidates = urls.filter((u) => !existing.has(u));
    if (candidates.length === 0) {
      new Notice("Thany: all body URLs already attached");
      return;
    }
    this.showUrlPicker(candidates);
  }

  private showUrlPicker(urls: string[]): void {
    if (!this.container || !this.state) return;
    const popover = this.container.createDiv({ cls: "tm-brow__popover" });
    popover.createDiv({ cls: "tm-brow__popover-title", text: "Promote URL → attachment" });
    for (const url of urls) {
      const row = popover.createEl("button", { cls: "tm-brow__popover-row", text: url });
      row.onclick = () => {
        if (!this.state) return;
        this.drafts.addUrlAttachment(this.state, url);
        popover.remove();
        this.render();
      };
    }
    const close = popover.createEl("button", { cls: "tm-brow__popover-close", text: "Cancel" });
    close.onclick = () => popover.remove();
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

function chipLabel(att: { kind: string; filename: string | null; extra: Record<string, unknown> }): string {
  if (att.kind === "url") return `url:${String(att.extra.url ?? "").replace(/^https?:\/\//, "")}`;
  if (att.kind === "image") return `img:${att.filename ?? "image"}`;
  if (att.kind === "voice") return `voice:${att.filename ?? "voice"}`;
  return `file:${att.filename ?? "file"}`;
}

function formatMs(ms: number): string {
  const total = Math.floor(ms / 1000);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${s.toString().padStart(2, "0")}`;
}
