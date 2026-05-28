import type { DraftManager, DraftState } from "./DraftManager";
import { sha256Hex } from "../sync/AttachmentDownloader";

type OnAttached = () => void;

/**
 * Captures paste and drop on a draft leaf's content element. Routes images
 * and binary files into the draft's attachments folder instead of inserting
 * `![[]]` into the body. URL text is left alone (the body editor handles it).
 */
export class PasteInterceptor {
  private cleanups: Array<() => void> = [];

  constructor(
    private readonly drafts: DraftManager,
    private readonly onAttached: OnAttached,
  ) {}

  attach(root: HTMLElement, state: DraftState): void {
    const paste = (e: ClipboardEvent) => void this.handlePaste(e, state);
    const dragover = (e: DragEvent) => {
      if (!e.dataTransfer) return;
      const hasFiles = Array.from(e.dataTransfer.items).some((i) => i.kind === "file");
      if (hasFiles) e.preventDefault();
    };
    const drop = (e: DragEvent) => void this.handleDrop(e, state);

    root.addEventListener("paste", paste, true);
    root.addEventListener("dragover", dragover, true);
    root.addEventListener("drop", drop, true);

    this.cleanups.push(() => root.removeEventListener("paste", paste, true));
    this.cleanups.push(() => root.removeEventListener("dragover", dragover, true));
    this.cleanups.push(() => root.removeEventListener("drop", drop, true));
  }

  detach(): void {
    for (const c of this.cleanups) c();
    this.cleanups = [];
  }

  private async handlePaste(e: ClipboardEvent, state: DraftState): Promise<void> {
    const cd = e.clipboardData;
    if (!cd) return;
    const items = Array.from(cd.items);
    const imageItem = items.find((i) => i.kind === "file" && i.type.startsWith("image/"));
    const fileItem = imageItem ?? items.find((i) => i.kind === "file");
    if (!fileItem) return;
    e.preventDefault();
    e.stopPropagation();
    const file = fileItem.getAsFile();
    if (!file) return;
    await this.attachFile(state, file);
  }

  private async handleDrop(e: DragEvent, state: DraftState): Promise<void> {
    const dt = e.dataTransfer;
    if (!dt || dt.files.length === 0) return;
    e.preventDefault();
    e.stopPropagation();
    for (const file of Array.from(dt.files)) {
      await this.attachFile(state, file);
    }
  }

  private async attachFile(state: DraftState, file: File): Promise<void> {
    const bytes = new Uint8Array(await file.arrayBuffer());
    const sha = await sha256Hex(bytes);
    const kind: "image" | "voice" | "file" = file.type.startsWith("image/")
      ? "image"
      : file.type.startsWith("audio/")
        ? "voice"
        : "file";
    const filename = file.name && file.name !== "image.png"
      ? file.name
      : defaultName(file.type, kind);
    await this.drafts.addBinaryAttachment(
      state,
      bytes.buffer as ArrayBuffer,
      kind,
      filename,
      file.type || null,
      sha,
    );
    this.onAttached();
  }
}

function defaultName(mime: string, kind: string): string {
  const ts = new Date().toISOString().replace(/[:.]/g, "-");
  const ext = mime.split("/")[1]?.split(";")[0] || "bin";
  return `${kind}-${ts}.${ext}`;
}
