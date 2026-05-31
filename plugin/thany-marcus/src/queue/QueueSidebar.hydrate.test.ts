import { describe, expect, it, vi } from "vitest";
import type { IngestJobDto, ListJobsResponse } from "../api";
import { hydrateQueueFromCloud, type QueueHydrator } from "./QueueSidebar";
import { QueueStore } from "./QueueStore";

function job(over: Partial<IngestJobDto> = {}): IngestJobDto {
  return {
    noteId: over.noteId ?? "note-1",
    title: over.title ?? "A title",
    status: over.status ?? "synthesizing",
    attemptCount: over.attemptCount ?? 0,
    error: over.error ?? null,
    vaultPath: over.vaultPath ?? null,
    extractionFailures: over.extractionFailures ?? [],
  };
}

function hydrator(resp: ListJobsResponse | Error): QueueHydrator {
  return {
    listActiveJobs: vi.fn(async (): Promise<ListJobsResponse> => {
      if (resp instanceof Error) throw resp;
      return resp;
    }),
  };
}

describe("hydrateQueueFromCloud", () => {
  it("calls listActiveJobs with includeRecent=true exactly once", async () => {
    const h = hydrator({ active: [], recent: [] });
    const store = new QueueStore();
    await hydrateQueueFromCloud(h, store);
    expect(h.listActiveJobs).toHaveBeenCalledTimes(1);
    expect(h.listActiveJobs).toHaveBeenCalledWith(true);
  });

  it("populates the store with active + recent before SSE arrives", async () => {
    const h = hydrator({
      active: [job({ noteId: "n-active", title: "Active note", status: "synthesizing" })],
      recent: [job({ noteId: "n-recent", title: "Recent note", status: "succeeded", vaultPath: "Inbox/r.md" })],
    });
    const store = new QueueStore();

    await hydrateQueueFromCloud(h, store);

    const entries = store.list();
    expect(entries).toHaveLength(2);
    const byId = Object.fromEntries(entries.map((e) => [e.noteId, e]));
    expect(byId["n-active"].title).toBe("Active note");
    expect(byId["n-active"].status).toBe("synthesizing");
    // 'succeeded' → 'ready' so the existing isTerminal/phaseLabel logic styles it correctly.
    expect(byId["n-recent"].status).toBe("ready");
    expect(byId["n-recent"].vaultPath).toBe("Inbox/r.md");
  });

  it("SSE row wins when an event has already landed for the same noteId", async () => {
    const store = new QueueStore();
    // Simulate SSE landing first with a fresher status.
    store.upsert("n-1", { title: "SSE title", status: "embedding" });

    const h = hydrator({
      active: [job({ noteId: "n-1", title: "Stale title", status: "queued" })],
      recent: [],
    });

    await hydrateQueueFromCloud(h, store);

    const entry = store.get("n-1");
    expect(entry?.title).toBe("SSE title");
    expect(entry?.status).toBe("embedding");
  });

  it("soft-fails when listActiveJobs throws — leaves store empty, does not throw", async () => {
    const errSpy = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const h = hydrator(new Error("network down"));
    const store = new QueueStore();

    await expect(hydrateQueueFromCloud(h, store)).resolves.toBeUndefined();
    expect(store.list()).toEqual([]);
    expect(errSpy).toHaveBeenCalled();
    errSpy.mockRestore();
  });

  it("surfaces extraction failures from the hydrated job onto the store entry", async () => {
    const h = hydrator({
      active: [job({
        noteId: "n-fail",
        title: "Has failures",
        status: "synthesizing",
        extractionFailures: [
          { kind: "image", reason: "vlm timeout" },
        ],
      })],
      recent: [],
    });
    const store = new QueueStore();

    await hydrateQueueFromCloud(h, store);

    const entry = store.get("n-fail");
    expect(entry?.extractionFailures).toHaveLength(1);
    expect(entry?.extractionFailures?.[0].kind).toBe("image");
    expect(entry?.extractionFailures?.[0].reason).toBe("vlm timeout");
  });
});
