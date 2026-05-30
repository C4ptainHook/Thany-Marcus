import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RelatedNotesAbortedError, type RelatedNotesItem } from "../api";
import { RelatedNotesPanel, type RelatedNotesFetcher } from "./RelatedNotesPanel";

function newPanel(opts: {
  fetcher: RelatedNotesFetcher;
  onClick?: (item: RelatedNotesItem) => void;
}) {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const panel = new RelatedNotesPanel(opts.fetcher, container, {
    debounceMs: 100,
    minChars: 30,
    k: 5,
    onItemClick: opts.onClick ?? (() => undefined),
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
      panel.onBodyChange(LONG_BODY + i);
    }
    expect(fetcher.relatedNotes).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(100);
    expect(fetcher.relatedNotes).toHaveBeenCalledTimes(1);
    const calls = (fetcher.relatedNotes as unknown as { mock: { calls: Array<[{ body: string }, AbortSignal]> } }).mock.calls;
    expect(calls[0][0].body).toBe(LONG_BODY + "4");
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
    panel.onBodyChange(LONG_BODY + "first");
    await vi.advanceTimersByTimeAsync(100);
    expect(firstSignal).not.toBeNull();
    expect(firstSignal!.aborted).toBe(false);

    panel.onBodyChange(LONG_BODY + "second");
    expect(firstSignal!.aborted).toBe(true);

    pendingResolve!([]);
    await vi.advanceTimersByTimeAsync(100);
    expect(fetcher.relatedNotes).toHaveBeenCalledTimes(2);
  });

  it("short body renders empty state and does not fetch", async () => {
    const fetcher = { relatedNotes: vi.fn(async () => [] as RelatedNotesItem[]) };
    const { panel, container } = newPanel({ fetcher });

    panel.onBodyChange("too short");
    await vi.advanceTimersByTimeAsync(500);
    expect(fetcher.relatedNotes).not.toHaveBeenCalled();
    expect(container.querySelector(".tm-related__empty")).not.toBeNull();
  });

  it("clicking an item invokes onItemClick", async () => {
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

    panel.onBodyChange(LONG_BODY);
    await vi.advanceTimersByTimeAsync(100);
    await Promise.resolve();
    await Promise.resolve();

    const row = container.querySelector(".tm-related__row") as HTMLElement | null;
    expect(row).not.toBeNull();
    row!.click();
    expect(onClick).toHaveBeenCalledWith(item);
  });
});
