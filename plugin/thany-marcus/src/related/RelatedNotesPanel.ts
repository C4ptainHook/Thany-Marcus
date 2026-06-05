import { RelatedNotesAbortedError, type RelatedNotesItem } from "../api";
import {
  RELATED_STRICTNESS_LABEL,
  RELATED_STRICTNESS_LEVELS,
  strictnessToMaxDistance,
  type RelatedStrictness,
} from "./strictness";

export interface RelatedNotesFetcher {
  relatedNotes(
    req: { body: string; k: number; excludeNoteId?: string; maxDistance?: number },
    signal: AbortSignal,
  ): Promise<RelatedNotesItem[]>;
}

export interface RelatedNotesPanelOptions {
  debounceMs?: number;
  minChars?: number;
  k?: number;
  strictness?: RelatedStrictness;
  onItemClick: (item: RelatedNotesItem) => void;
  onItemInsert?: (item: RelatedNotesItem) => void;
  onStrictnessChange?: (s: RelatedStrictness) => void;
}

type Timer = { id: ReturnType<typeof setTimeout> } | null;

export class RelatedNotesPanel {
  private readonly debounceMs: number;
  private readonly minChars: number;
  private readonly k: number;
  private readonly onItemClick: (item: RelatedNotesItem) => void;
  private readonly onItemInsert?: (item: RelatedNotesItem) => void;
  private readonly onStrictnessChange?: (s: RelatedStrictness) => void;
  private strictness: RelatedStrictness;

  private abortCtl: AbortController | null = null;
  private debounceTimer: Timer = null;
  private lastBlock: string | null = null;
  private lastExcludeNoteId: string | undefined = undefined;
  private hasFetched = false;
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
    this.onItemInsert = opts.onItemInsert;
    this.onStrictnessChange = opts.onStrictnessChange;
    this.strictness = opts.strictness ?? "balanced";
    this.renderEmpty();
  }

  onContextChange(blockText: string, excludeNoteId?: string): void {
    if (blockText === this.lastBlock && excludeNoteId === this.lastExcludeNoteId) {
      return;
    }
    this.lastBlock = blockText;
    this.lastExcludeNoteId = excludeNoteId;

    this.cancelPending();

    if (blockText.length < this.minChars) {
      // Block emptied entirely — the text the results related to is gone, so clear.
      // Block merely short (still being typed) — keep prior results (anti-flicker).
      if (blockText.trim().length === 0) {
        this.items = [];
        this.hasFetched = false;
        this.renderEmpty();
      } else if (!this.hasFetched) {
        this.renderEmpty();
      }
      return;
    }

    const id = setTimeout(() => {
      this.debounceTimer = null;
      void this.fire(blockText, excludeNoteId);
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

  private changeStrictness(s: RelatedStrictness): void {
    if (s === this.strictness) return;
    this.strictness = s;
    this.onStrictnessChange?.(s);
    if (this.hasFetched) this.renderItems();
    else this.renderEmpty();
    if (this.lastBlock && this.lastBlock.length >= this.minChars) {
      this.cancelPending();
      void this.fire(this.lastBlock, this.lastExcludeNoteId);
    }
  }

  private async fire(body: string, excludeNoteId?: string): Promise<void> {
    const ctl = new AbortController();
    this.abortCtl = ctl;
    try {
      const items = await this.api.relatedNotes(
        { body, k: this.k, excludeNoteId, maxDistance: strictnessToMaxDistance(this.strictness) },
        ctl.signal,
      );
      if (ctl.signal.aborted) return;
      this.hasFetched = true;
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
    if (this.onStrictnessChange) this.renderStrictnessControl(header);
    this.container.appendChild(header);
  }

  private renderStrictnessControl(header: HTMLElement): void {
    const control = document.createElement("div");
    control.className = "tm-related__strictness";
    control.setAttribute("role", "group");
    control.setAttribute("aria-label", "Related notes strictness");
    for (const level of RELATED_STRICTNESS_LEVELS) {
      const btn = document.createElement("button");
      btn.className =
        "tm-related__strictness-btn" + (this.strictness === level ? " is-active" : "");
      btn.textContent = RELATED_STRICTNESS_LABEL[level];
      btn.title = `Show ${level === "loose" ? "more" : level === "strict" ? "fewer" : "a balanced set of"} related thoughts`;
      btn.onclick = () => this.changeStrictness(level);
      control.appendChild(btn);
    }
    header.appendChild(control);
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
      const actions = document.createElement("div");
      actions.className = "tm-related__actions";
      const open = document.createElement("button");
      open.className = "tm-related__open";
      open.textContent = "Open note";
      open.onclick = () => this.onItemClick(expanded);
      actions.appendChild(open);
      if (this.onItemInsert) {
        const insert = document.createElement("button");
        insert.className = "tm-related__open";
        insert.textContent = "Insert link";
        insert.onclick = () => this.onItemInsert!(expanded);
        actions.appendChild(insert);
      }
      detail.appendChild(actions);
      this.container.appendChild(detail);
    }
  }
}
