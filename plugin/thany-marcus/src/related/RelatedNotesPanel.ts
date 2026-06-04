import { RelatedNotesAbortedError, type RelatedNotesItem } from "../api";

export interface RelatedNotesFetcher {
  relatedNotes(
    req: { body: string; k: number },
    signal: AbortSignal,
  ): Promise<RelatedNotesItem[]>;
}

export interface RelatedNotesPanelOptions {
  debounceMs?: number;
  minChars?: number;
  k?: number;
  onItemClick: (item: RelatedNotesItem) => void;
}

type Timer = { id: ReturnType<typeof setTimeout> } | null;

export class RelatedNotesPanel {
  private readonly debounceMs: number;
  private readonly minChars: number;
  private readonly k: number;
  private readonly onItemClick: (item: RelatedNotesItem) => void;

  private abortCtl: AbortController | null = null;
  private debounceTimer: Timer = null;
  private lastFetchedBody: string | null = null;
  private items: RelatedNotesItem[] = [];
  private expandedKey: string | null = null;

  constructor(
    private readonly api: RelatedNotesFetcher,
    private readonly container: HTMLElement,
    opts: RelatedNotesPanelOptions,
  ) {
    this.debounceMs = opts.debounceMs ?? 600;
    this.minChars = opts.minChars ?? 30;
    this.k = opts.k ?? 5;
    this.onItemClick = opts.onItemClick;
    this.renderEmpty();
  }

  onBodyChange(body: string): void {
    this.cancelPending();

    if (body.length < this.minChars) {
      this.items = [];
      this.lastFetchedBody = null;
      this.renderEmpty();
      return;
    }

    if (body === this.lastFetchedBody) {
      return;
    }

    const id = setTimeout(() => {
      this.debounceTimer = null;
      void this.fire(body);
    }, this.debounceMs);
    this.debounceTimer = { id };
  }

  dispose(): void {
    this.cancelPending();
    this.container.innerHTML = "";
  }

  private cancelPending(): void {
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer.id);
      this.debounceTimer = null;
    }
    if (this.abortCtl) {
      this.abortCtl.abort();
      this.abortCtl = null;
    }
  }

  private async fire(body: string): Promise<void> {
    const ctl = new AbortController();
    this.abortCtl = ctl;
    try {
      const items = await this.api.relatedNotes(
        { body, k: this.k },
        ctl.signal,
      );
      if (ctl.signal.aborted) return;
      this.lastFetchedBody = body;
      this.items = items;
      this.renderItems();
    } catch (e) {
      if (e instanceof RelatedNotesAbortedError) return;
      if ((e as { name?: string }).name === "AbortError") return;
      this.renderError((e as Error).message);
    } finally {
      if (this.abortCtl === ctl) this.abortCtl = null;
    }
  }

  private renderHeader(count: number | null): void {
    this.container.innerHTML = "";
    const header = document.createElement("div");
    header.className = "tm-related__header";
    header.textContent = "Related thoughts";
    if (count != null && count > 0) {
      const badge = document.createElement("span");
      badge.className = "tm-related__count";
      badge.textContent = `${count}`;
      header.appendChild(badge);
    }
    this.container.appendChild(header);
  }

  private renderEmpty(): void {
    this.renderHeader(null);
    const empty = document.createElement("div");
    empty.className = "tm-related__empty";
    empty.textContent = "Related thoughts will appear here as you write.";
    this.container.appendChild(empty);
  }

  private renderError(msg: string): void {
    this.renderHeader(null);
    const err = document.createElement("div");
    err.className = "tm-related__error";
    err.textContent = `unavailable — ${msg}`;
    this.container.appendChild(err);
  }

  private renderItems(): void {
    this.renderHeader(this.items.length);
    if (this.items.length === 0) {
      const empty = document.createElement("div");
      empty.className = "tm-related__empty";
      empty.textContent = "No related thoughts yet.";
      this.container.appendChild(empty);
      return;
    }

    const pills = document.createElement("div");
    pills.className = "tm-related__pills";
    for (const item of this.items) {
      const key = item.relativePath;
      const pill = document.createElement("button");
      pill.className = "tm-related__pill" + (this.expandedKey === key ? " is-expanded" : "");
      pill.textContent = item.title || item.relativePath;
      pill.onclick = () => {
        this.expandedKey = this.expandedKey === key ? null : key;
        this.renderItems();
      };
      pills.appendChild(pill);
    }
    this.container.appendChild(pills);

    const expanded = this.items.find((i) => i.relativePath === this.expandedKey);
    if (expanded) {
      const detail = document.createElement("div");
      detail.className = "tm-related__detail";
      if (expanded.snippet) {
        const snippet = document.createElement("div");
        snippet.className = "tm-related__snippet";
        snippet.textContent = expanded.snippet;
        detail.appendChild(snippet);
      }
      const open = document.createElement("button");
      open.className = "tm-related__open";
      open.textContent = "Open note";
      open.onclick = () => this.onItemClick(expanded);
      detail.appendChild(open);
      this.container.appendChild(detail);
    }
  }
}
