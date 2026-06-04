import type {
  AcceptEntitySuggestionResult,
  EditEntitySuggestionRequest,
  EntitySuggestion,
} from "../api";

export interface EntitySuggestionActions {
  list(signal?: AbortSignal): Promise<EntitySuggestion[]>;
  accept(id: string): Promise<AcceptEntitySuggestionResult>;
  dismiss(id: string): Promise<void>;
  edit(id: string, patch: EditEntitySuggestionRequest): Promise<void>;
}

export interface EntitySuggestionsPanelOptions {
  pollMs?: number;
  onAccepted?: (entityId: string) => void;
  onChange?: () => void;
  notify?: (message: string) => void;
}

export class EntitySuggestionsPanel {
  private readonly onAccepted: (entityId: string) => void;
  private readonly onChange: () => void;
  private readonly notify: (message: string) => void;

  private suggestions: EntitySuggestion[] = [];
  private editingId: string | null = null;
  private abortCtl: AbortController | null = null;
  private readonly onFocus = () => void this.refresh();

  constructor(
    private readonly actions: EntitySuggestionActions,
    private readonly container: HTMLElement,
    opts: EntitySuggestionsPanelOptions = {},
  ) {
    this.onAccepted = opts.onAccepted ?? (() => undefined);
    this.onChange = opts.onChange ?? (() => undefined);
    this.notify = opts.notify ?? ((m) => console.info("[Thany]", m));
    this.render();
  }

  getCount(): number {
    return this.suggestions.length;
  }

  start(): void {
    void this.refresh();
    if (typeof window !== "undefined") {
      window.addEventListener("focus", this.onFocus);
    }
  }

  async refresh(): Promise<void> {
    this.abortCtl?.abort();
    const ctl = new AbortController();
    this.abortCtl = ctl;
    try {
      const items = await this.actions.list(ctl.signal);
      if (ctl.signal.aborted) return;
      this.suggestions = items;
      this.render();
      this.onChange();
    } catch (e) {
      if ((e as { name?: string }).name === "AbortError") return;
      // Polling errors are non-fatal — keep the last good render.
      console.error("[Thany] listEntitySuggestions failed", e);
    } finally {
      if (this.abortCtl === ctl) this.abortCtl = null;
    }
  }

  dispose(): void {
    this.abortCtl?.abort();
    this.abortCtl = null;
    if (typeof window !== "undefined") {
      window.removeEventListener("focus", this.onFocus);
    }
    this.container.innerHTML = "";
  }

  private render(): void {
    this.container.innerHTML = "";
    this.container.className = "tm-suggestions";

    const header = el("div", "tm-suggestions__header");
    header.textContent = "Suggestions";
    const badge = el("span", "tm-suggestions__badge");
    badge.textContent = `(${this.suggestions.length})`;
    header.appendChild(badge);
    this.container.appendChild(header);

    if (this.suggestions.length === 0) {
      const empty = el("div", "tm-suggestions__empty");
      empty.textContent = "No entity suggestions yet.";
      this.container.appendChild(empty);
      return;
    }

    const list = el("div", "tm-suggestions__list");
    for (const s of this.suggestions) {
      list.appendChild(this.editingId === s.id ? this.renderEditForm(s) : this.renderRow(s));
    }
    this.container.appendChild(list);
  }

  private renderRow(s: EntitySuggestion): HTMLElement {
    const row = el("div", "tm-suggestions__row");
    row.setAttribute("data-id", s.id);

    const top = el("div", "tm-suggestions__top");
    const name = el("span", "tm-suggestions__name");
    name.textContent = s.canonicalText;
    top.appendChild(name);
    const chip = el("span", "tm-suggestions__kind");
    chip.textContent = s.kind;
    top.appendChild(chip);
    const count = el("span", "tm-suggestions__count");
    count.textContent = `${s.occurrenceCount}× · ${s.distinctNoteCount} notes`;
    top.appendChild(count);
    row.appendChild(top);

    if (s.sampleOccurrence?.anchorText) {
      const sample = el("div", "tm-suggestions__sample");
      sample.textContent = `“${s.sampleOccurrence.anchorText}”`;
      row.appendChild(sample);
    }

    const actions = el("div", "tm-suggestions__actions");
    actions.appendChild(button("Accept", "tm-suggestions__accept", () => void this.onAccept(s)));
    actions.appendChild(button("Edit", "tm-suggestions__edit", () => this.openEdit(s.id)));
    actions.appendChild(button("Dismiss", "tm-suggestions__dismiss", () => void this.onDismiss(s)));
    row.appendChild(actions);

    return row;
  }

  private renderEditForm(s: EntitySuggestion): HTMLElement {
    const form = el("div", "tm-suggestions__row tm-suggestions__edit-form");
    form.setAttribute("data-id", s.id);

    const canonicalInput = document.createElement("input");
    canonicalInput.className = "tm-suggestions__canonical-input";
    canonicalInput.value = s.canonicalText;
    form.appendChild(canonicalInput);

    const aliasesInput = document.createElement("input");
    aliasesInput.className = "tm-suggestions__aliases-input";
    aliasesInput.placeholder = "aliases, comma-separated";
    aliasesInput.value = s.aliases.join(", ");
    form.appendChild(aliasesInput);

    const actions = el("div", "tm-suggestions__actions");
    actions.appendChild(button("Save", "tm-suggestions__save", () =>
      void this.onEditSave(s, canonicalInput.value, aliasesInput.value)));
    actions.appendChild(button("Cancel", "tm-suggestions__cancel", () => {
      this.editingId = null;
      this.render();
    }));
    form.appendChild(actions);

    return form;
  }

  private openEdit(id: string): void {
    this.editingId = id;
    this.render();
  }

  private async onAccept(s: EntitySuggestion): Promise<void> {
    try {
      const result = await this.actions.accept(s.id);
      if (result.ok) {
        this.removeRow(s.id);
        this.onAccepted(result.entityId);
      } else if (result.conflict === "path") {
        this.notify(`"${s.canonicalText}" collides with an existing ${result.existingKind} file. Rename it and retry.`);
      } else {
        this.notify(`Cannot rename an accepted entity here — rename "${s.canonicalText}" in Obsidian.`);
      }
    } catch (e) {
      this.notify(`Accept failed: ${(e as Error).message}`);
    }
  }

  private async onDismiss(s: EntitySuggestion): Promise<void> {
    try {
      await this.actions.dismiss(s.id);
      this.removeRow(s.id);
    } catch (e) {
      this.notify(`Dismiss failed: ${(e as Error).message}`);
    }
  }

  private async onEditSave(s: EntitySuggestion, canonical: string, aliasesRaw: string): Promise<void> {
    const patch: EditEntitySuggestionRequest = {
      canonicalText: canonical.trim(),
      aliases: aliasesRaw
        .split(",")
        .map((a) => a.trim())
        .filter((a) => a.length > 0),
    };
    try {
      await this.actions.edit(s.id, patch);
      this.editingId = null;
      await this.refresh();
    } catch (e) {
      this.notify(`Edit failed: ${(e as Error).message}`);
    }
  }

  private removeRow(id: string): void {
    this.suggestions = this.suggestions.filter((x) => x.id !== id);
    if (this.editingId === id) this.editingId = null;
    this.render();
    this.onChange();
  }
}

function el(tag: string, className: string): HTMLElement {
  const node = document.createElement(tag);
  node.className = className;
  return node;
}

function button(label: string, className: string, onClick: () => void): HTMLButtonElement {
  const b = document.createElement("button");
  b.className = className;
  b.textContent = label;
  b.onclick = (ev) => {
    ev.stopPropagation();
    onClick();
  };
  return b;
}
