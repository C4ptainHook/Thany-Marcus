import { isFailure, isTerminal } from "./labels";
import type { QueueStore, QueueEntry } from "./QueueStore";

export class PhaseToastController {
  private readonly toasts = new Map<string, HTMLElement>();
  private unsubscribe: (() => void) | null = null;
  private container: HTMLElement | null = null;

  constructor(private readonly store: QueueStore) {}

  start(): void {
    this.container = document.createElement("div");
    this.container.addClass("tm-toast-container");
    document.body.appendChild(this.container);
    this.unsubscribe = this.store.subscribe(() => this.render());
    this.render();
  }

  stop(): void {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.toasts.clear();
    this.container?.remove();
    this.container = null;
  }

  private render(): void {
    if (!this.container) return;
    const entries = this.store.list();
    const seen = new Set<string>();
    for (const e of entries) {
      if (isTerminal(e.status) && !isFailure(e.status)) {
        const dismissTimer = (e as QueueEntry & { __toastDismissAt?: number }).__toastDismissAt;
        if (dismissTimer && Date.now() > dismissTimer) {
          this.toasts.get(e.noteId)?.remove();
          this.toasts.delete(e.noteId);
          continue;
        }
        if (!dismissTimer) {
          (e as QueueEntry & { __toastDismissAt?: number }).__toastDismissAt = Date.now() + 3000;
          setTimeout(() => this.render(), 3100);
        }
      }
      seen.add(e.noteId);
      this.upsertToast(e);
    }
    for (const [id, el] of this.toasts) {
      if (!seen.has(id)) {
        el.remove();
        this.toasts.delete(id);
      }
    }
  }

  private upsertToast(e: QueueEntry): void {
    if (!this.container) return;
    let el = this.toasts.get(e.noteId);
    if (!el) {
      el = document.createElement("div");
      el.addClass("tm-toast");
      this.container.appendChild(el);
      this.toasts.set(e.noteId, el);
    }
    el.removeClass("tm-toast--failed", "tm-toast--ready", "tm-toast--in-flight");
    if (isFailure(e.status)) el.addClass("tm-toast--failed");
    else if (isTerminal(e.status)) el.addClass("tm-toast--ready");
    else el.addClass("tm-toast--in-flight");

    el.empty();
    const title = el.createDiv({ cls: "tm-toast__title" });
    title.setText(e.title);
    const phase = el.createDiv({ cls: "tm-toast__phase" });
    phase.setText(e.label);
    if (e.error) {
      const err = el.createDiv({ cls: "tm-toast__error" });
      err.setText(e.error);
    }
    const close = el.createEl("button", { cls: "tm-toast__close", text: "×" });
    close.onclick = () => {
      this.toasts.get(e.noteId)?.remove();
      this.toasts.delete(e.noteId);
    };
  }
}
