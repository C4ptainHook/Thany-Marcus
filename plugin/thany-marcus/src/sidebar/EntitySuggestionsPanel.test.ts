import { afterEach, describe, expect, it, vi } from "vitest";
import type { AcceptEntitySuggestionResult, EntitySuggestion } from "../api";
import {
  EntitySuggestionsPanel,
  type EntitySuggestionActions,
} from "./EntitySuggestionsPanel";

function suggestion(over: Partial<EntitySuggestion> = {}): EntitySuggestion {
  return {
    id: over.id ?? "s1",
    canonicalText: over.canonicalText ?? "Michael Jackson",
    kind: over.kind ?? "person",
    aliases: over.aliases ?? ["Mike", "MJ"],
    occurrenceCount: over.occurrenceCount ?? 4,
    distinctNoteCount: over.distinctNoteCount ?? 2,
    firstSeenAt: over.firstSeenAt ?? "2026-05-30T00:00:00Z",
    lastSeenAt: over.lastSeenAt ?? "2026-05-30T01:00:00Z",
    sampleOccurrence: over.sampleOccurrence ?? {
      noteId: "n1",
      anchorText: "Mike",
      surroundingText: "Mike said hi",
    },
  };
}

function newPanel(actions: Partial<EntitySuggestionActions>) {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const full: EntitySuggestionActions = {
    list: actions.list ?? vi.fn(async () => []),
    accept: actions.accept ?? vi.fn(async () => ({ ok: true, entityId: "e1" }) as AcceptEntitySuggestionResult),
    dismiss: actions.dismiss ?? vi.fn(async () => undefined),
    edit: actions.edit ?? vi.fn(async () => undefined),
  };
  const panel = new EntitySuggestionsPanel(full, container, { pollMs: 60_000 });
  return { panel, container, actions: full };
}

describe("EntitySuggestionsPanel", () => {
  afterEach(() => {
    document.body.innerHTML = "";
    vi.restoreAllMocks();
  });

  it("renders a row per suggestion with a count badge", async () => {
    const { panel, container } = newPanel({
      list: vi.fn(async () => [suggestion({ id: "a" }), suggestion({ id: "b", canonicalText: "Quincy Jones" })]),
    });
    await panel.refresh();

    expect(container.querySelectorAll(".tm-suggestions__row").length).toBe(2);
    expect(container.querySelector(".tm-suggestions__badge")?.textContent).toBe("(2)");
    const names = Array.from(container.querySelectorAll(".tm-suggestions__name")).map((n) => n.textContent);
    expect(names).toContain("Michael Jackson");
    expect(names).toContain("Quincy Jones");
  });

  it("empty list shows the empty state and a zero badge", async () => {
    const { panel, container } = newPanel({ list: vi.fn(async () => []) });
    await panel.refresh();
    expect(container.querySelector(".tm-suggestions__empty")).not.toBeNull();
    expect(container.querySelector(".tm-suggestions__badge")?.textContent).toBe("(0)");
  });

  it("Accept calls the API, removes the row, and reports the new entity", async () => {
    const accept = vi.fn(async () => ({ ok: true, entityId: "ent-7" }) as AcceptEntitySuggestionResult);
    const onAccepted = vi.fn();
    const container = document.createElement("div");
    document.body.appendChild(container);
    const panel = new EntitySuggestionsPanel(
      {
        list: vi.fn(async () => [suggestion({ id: "a" })]),
        accept,
        dismiss: vi.fn(async () => undefined),
        edit: vi.fn(async () => undefined),
      },
      container,
      { onAccepted },
    );
    await panel.refresh();

    const btn = container.querySelector(".tm-suggestions__accept") as HTMLButtonElement;
    btn.click();
    await Promise.resolve();
    await Promise.resolve();

    expect(accept).toHaveBeenCalledWith("a");
    expect(onAccepted).toHaveBeenCalledWith("ent-7");
    expect(container.querySelectorAll(".tm-suggestions__row").length).toBe(0);
    expect(container.querySelector(".tm-suggestions__badge")?.textContent).toBe("(0)");
  });

  it("Dismiss calls the API and removes the row", async () => {
    const dismiss = vi.fn(async () => undefined);
    const { panel, container } = newPanel({
      list: vi.fn(async () => [suggestion({ id: "a" })]),
      dismiss,
    });
    await panel.refresh();

    (container.querySelector(".tm-suggestions__dismiss") as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();

    expect(dismiss).toHaveBeenCalledWith("a");
    expect(container.querySelectorAll(".tm-suggestions__row").length).toBe(0);
  });

  it("Edit opens an inline form and Save submits the patch", async () => {
    const edit = vi.fn(async () => undefined);
    const list = vi.fn(async () => [suggestion({ id: "a", aliases: ["Mike"] })]);
    const { panel, container } = newPanel({ list, edit });
    await panel.refresh();

    (container.querySelector(".tm-suggestions__edit") as HTMLButtonElement).click();

    const canonical = container.querySelector(".tm-suggestions__canonical-input") as HTMLInputElement;
    const aliases = container.querySelector(".tm-suggestions__aliases-input") as HTMLInputElement;
    expect(canonical).not.toBeNull();
    canonical.value = "Michael Jackson";
    aliases.value = "Mike, MJ, Jackson";

    (container.querySelector(".tm-suggestions__save") as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();

    expect(edit).toHaveBeenCalledWith("a", {
      canonicalText: "Michael Jackson",
      aliases: ["Mike", "MJ", "Jackson"],
    });
  });

  it("path conflict on Accept keeps the row and notifies", async () => {
    const accept = vi.fn(async () => ({ ok: false, conflict: "path", existingKind: "synth_note" }) as AcceptEntitySuggestionResult);
    const notify = vi.fn();
    const container = document.createElement("div");
    document.body.appendChild(container);
    const panel = new EntitySuggestionsPanel(
      {
        list: vi.fn(async () => [suggestion({ id: "a" })]),
        accept,
        dismiss: vi.fn(async () => undefined),
        edit: vi.fn(async () => undefined),
      },
      container,
      { notify },
    );
    await panel.refresh();

    (container.querySelector(".tm-suggestions__accept") as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();

    expect(notify).toHaveBeenCalled();
    expect(container.querySelectorAll(".tm-suggestions__row").length).toBe(1);
  });
});
