import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RelatedNotesAbortedError, type RelatedNotesItem } from "../api";
import { RelatedNotesPanel, type RelatedNotesFetcher } from "./RelatedNotesPanel";
import type { RelatedStrictness } from "./strictness";

function newPanel(opts: {
  fetcher: RelatedNotesFetcher;
  onClick?: (item: RelatedNotesItem) => void;
  onInsert?: (item: RelatedNotesItem) => void;
  onStrictnessChange?: (s: RelatedStrictness) => void;
  strictness?: RelatedStrictness;
}) {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const panel = new RelatedNotesPanel(opts.fetcher, container, {
    debounceMs: 100,
    minChars: 30,
    k: 5,
    strictness: opts.strictness,
    onItemClick: opts.onClick ?? (() => undefined),
    onItemInsert: opts.onInsert,
    onStrictnessChange: opts.onStrictnessChange,
  });
  return { panel, container };
}

const LONG_BODY = "x".repeat(30);

describe("RelatedNotesPanel", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = "";
  });

  it("debounce collapses rapid keystrokes to a single fetch", async () => {
    const fetcher = { relatedNotes: vi.fn(async () => [] as RelatedNotesItem[]) };
    const { panel } = newPanel({ fetcher });

    for (let i = 0; i < 5; i++) {
      panel.onContextChange(LONG_BODY + i);
    }
    expect(fetcher.relatedNotes).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(100);
    expect(fetcher.relatedNotes).toHaveBeenCalledTimes(1);
    const calls = (fetcher.relatedNotes as unknown as { mock: { calls: Array<[{ body: string }, AbortSignal]> } }).mock.calls;
    expect(calls[0][0].body).toBe(LONG_BODY + "4");
  });

  it("does not refire when the block content is unchanged", async () => {
    const fetcher = { relatedNotes: vi.fn(async () => [] as RelatedNotesItem[]) };
    const { panel } = newPanel({ fetcher });

    panel.onContextChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    expect(fetcher.relatedNotes).toHaveBeenCalledTimes(1);

    // same block text (e.g. a pure cursor move within the block) — no new fetch
    panel.onContextChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    expect(fetcher.relatedNotes).toHaveBeenCalledTimes(1);
  });

  it("forwards excludeNoteId to the fetcher", async () => {
    const fetcher = { relatedNotes: vi.fn(async () => [] as RelatedNotesItem[]) };
    const { panel } = newPanel({ fetcher });

    panel.onContextChange(LONG_BODY, "note-123");
    await vi.advanceTimersByTimeAsync(100);
    const calls = (fetcher.relatedNotes as unknown as {
      mock: { calls: Array<[{ body: string; excludeNoteId?: string }, AbortSignal]> };
    }).mock.calls;
    expect(calls[0][0].excludeNoteId).toBe("note-123");
  });

  it("keeps showing prior results when the block drops below the floor (stale-while-revalidate)", async () => {
    const item: RelatedNotesItem = {
      id: "abc",
      relativePath: "Inbox/foo.md",
      title: "Foo",
      snippet: "snippet",
      distance: 0.1,
    };
    const fetcher = { relatedNotes: vi.fn(async () => [item]) };
    const { panel, container } = newPanel({ fetcher });

    panel.onContextChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    await Promise.resolve();
    await Promise.resolve();
    expect(container.querySelector(".tm-related__pill")).not.toBeNull();

    // user deletes down to a short block — results must not be cleared to empty
    panel.onContextChange("short");
    await vi.advanceTimersByTimeAsync(100);
    expect(container.querySelector(".tm-related__pill")).not.toBeNull();
  });

  it("clears results when the block is emptied entirely", async () => {
    const item: RelatedNotesItem = {
      id: "abc",
      relativePath: "Inbox/foo.md",
      title: "Foo",
      snippet: "snippet",
      distance: 0.1,
    };
    const fetcher = { relatedNotes: vi.fn(async () => [item]) };
    const { panel, container } = newPanel({ fetcher });

    panel.onContextChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    await Promise.resolve();
    await Promise.resolve();
    expect(container.querySelector(".tm-related__pill")).not.toBeNull();

    // the whole block is gone — drop the now-orphaned results
    panel.onContextChange("   ");
    await vi.advanceTimersByTimeAsync(100);
    expect(container.querySelector(".tm-related__pill")).toBeNull();
    expect(container.querySelector(".tm-related__empty")).not.toBeNull();
  });

  it("aborts the in-flight request when a new keystroke fires", async () => {
    let firstSignal: AbortSignal | null = null;
    let pendingResolve: ((v: RelatedNotesItem[]) => void) | null = null;

    const fetcher: RelatedNotesFetcher = {
      relatedNotes: vi.fn(async (
        _req: { body: string; k: number },
        signal: AbortSignal,
      ) => {
        if (firstSignal === null) {
          firstSignal = signal;
          return new Promise<RelatedNotesItem[]>((resolve, reject) => {
            pendingResolve = resolve;
            signal.addEventListener("abort", () => {
              reject(new RelatedNotesAbortedError());
            });
          });
        }
        return [];
      }),
    };

    const { panel } = newPanel({ fetcher });
    panel.onContextChange(LONG_BODY + "first");
    await vi.advanceTimersByTimeAsync(100);
    expect(firstSignal).not.toBeNull();
    expect(firstSignal!.aborted).toBe(false);

    panel.onContextChange(LONG_BODY + "second");
    expect(firstSignal!.aborted).toBe(true);

    pendingResolve!([]);
    await vi.advanceTimersByTimeAsync(100);
    expect(fetcher.relatedNotes).toHaveBeenCalledTimes(2);
  });

  it("short body renders empty state and does not fetch", async () => {
    const fetcher = { relatedNotes: vi.fn(async () => [] as RelatedNotesItem[]) };
    const { panel, container } = newPanel({ fetcher });

    panel.onContextChange("too short");
    await vi.advanceTimersByTimeAsync(500);
    expect(fetcher.relatedNotes).not.toHaveBeenCalled();
    expect(container.querySelector(".tm-related__empty")).not.toBeNull();
  });

  it("expanding a pill then Open invokes onItemClick", async () => {
    const item: RelatedNotesItem = {
      id: "abc",
      relativePath: "Inbox/foo.md",
      title: "Foo",
      snippet: "snippet",
      distance: 0.1,
    };
    const fetcher = { relatedNotes: vi.fn(async () => [item]) };
    const onClick = vi.fn();
    const { panel, container } = newPanel({ fetcher, onClick });

    panel.onContextChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    await Promise.resolve();
    await Promise.resolve();

    const pill = container.querySelector(".tm-related__pill") as HTMLElement | null;
    expect(pill).not.toBeNull();
    expect(pill!.textContent).toBe("Foo");
    pill!.click();
    expect(onClick).not.toHaveBeenCalled();
    expect(container.querySelector(".tm-related__snippet")?.textContent).toBe("snippet");

    const open = container.querySelector(".tm-related__open") as HTMLElement | null;
    expect(open).not.toBeNull();
    open!.click();
    expect(onClick).toHaveBeenCalledWith(item);
  });

  it("renders Insert link in the detail and the click calls onItemInsert", async () => {
    const item: RelatedNotesItem = {
      id: "abc",
      relativePath: "Inbox/foo.md",
      title: "Foo",
      snippet: "snippet",
      distance: 0.1,
    };
    const fetcher = { relatedNotes: vi.fn(async () => [item]) };
    const onInsert = vi.fn();
    const { panel, container } = newPanel({ fetcher, onInsert });

    panel.onContextChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    await Promise.resolve();
    await Promise.resolve();

    const pill = container.querySelector(".tm-related__pill") as HTMLElement | null;
    pill!.click();

    const buttons = Array.from(
      container.querySelectorAll(".tm-related__open"),
    ) as HTMLElement[];
    const insert = buttons.find((b) => b.textContent === "Insert link");
    expect(insert).toBeDefined();
    insert!.click();
    expect(onInsert).toHaveBeenCalledWith(item);
  });

  it("does not render Insert link when onItemInsert is absent", async () => {
    const item: RelatedNotesItem = {
      id: "abc",
      relativePath: "Inbox/foo.md",
      title: "Foo",
      snippet: "snippet",
      distance: 0.1,
    };
    const fetcher = { relatedNotes: vi.fn(async () => [item]) };
    const { panel, container } = newPanel({ fetcher });

    panel.onContextChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    await Promise.resolve();
    await Promise.resolve();
    (container.querySelector(".tm-related__pill") as HTMLElement).click();

    const buttons = Array.from(
      container.querySelectorAll(".tm-related__open"),
    ) as HTMLElement[];
    expect(buttons.some((b) => b.textContent === "Insert link")).toBe(false);
  });

  it("balanced strictness omits maxDistance; selecting Strict re-fires with the mapped value", async () => {
    const fetcher = {
      relatedNotes: vi.fn(async () => [] as RelatedNotesItem[]),
    };
    const onStrictnessChange = vi.fn();
    const { panel, container } = newPanel({ fetcher, onStrictnessChange });

    panel.onContextChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    const calls = (fetcher.relatedNotes as unknown as {
      mock: { calls: Array<[{ maxDistance?: number }, AbortSignal]> };
    }).mock.calls;
    expect(calls[0][0].maxDistance).toBeUndefined();

    const strict = Array.from(
      container.querySelectorAll(".tm-related__strictness-btn"),
    ).find((b) => b.textContent === "Strict") as HTMLElement;
    expect(strict).toBeDefined();
    strict.click();
    await vi.advanceTimersByTimeAsync(100);

    expect(onStrictnessChange).toHaveBeenCalledWith("strict");
    expect(fetcher.relatedNotes).toHaveBeenCalledTimes(2);
    expect(calls[1][0].maxDistance).toBe(0.55);
  });

  it("hides the strictness control when onStrictnessChange is absent", () => {
    const fetcher = { relatedNotes: vi.fn(async () => [] as RelatedNotesItem[]) };
    const { container } = newPanel({ fetcher });
    expect(container.querySelector(".tm-related__strictness")).toBeNull();
  });
});
