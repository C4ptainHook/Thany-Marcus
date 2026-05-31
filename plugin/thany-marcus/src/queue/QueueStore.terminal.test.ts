import { describe, expect, it, vi } from "vitest";
import { QueueStore } from "./QueueStore";

describe("QueueStore terminal-transition listener", () => {
  it("fires once when a row transitions from in-flight to ready", () => {
    const store = new QueueStore();
    const listener = vi.fn();
    store.onTerminalTransition(listener);

    store.upsert("n1", { status: "composing", title: "T" });
    store.markReady("n1", "Thany/n1.md");

    expect(listener).toHaveBeenCalledOnce();
    expect(listener.mock.calls[0][0].status).toBe("composing");
    expect(listener.mock.calls[0][1].status).toBe("ready");
  });

  it("fires once when a row transitions in-flight to failed", () => {
    const store = new QueueStore();
    const listener = vi.fn();
    store.onTerminalTransition(listener);

    store.upsert("n1", { status: "embedding", title: "T" });
    store.markFailed("n1", "vlm timeout");

    expect(listener).toHaveBeenCalledOnce();
    expect(listener.mock.calls[0][1].status).toBe("failed");
    expect(listener.mock.calls[0][1].error).toBe("vlm timeout");
  });

  it("does not re-fire on a no-op terminal->terminal transition", () => {
    const store = new QueueStore();
    const listener = vi.fn();
    store.onTerminalTransition(listener);

    store.upsert("n1", { status: "composing", title: "T" });
    store.markReady("n1", "Thany/n1.md");
    store.markReady("n1", "Thany/n1.md");
    store.upsert("n1", { status: "ready" });

    expect(listener).toHaveBeenCalledOnce();
  });

  it("does not re-fire failed->failed", () => {
    const store = new QueueStore();
    const listener = vi.fn();
    store.onTerminalTransition(listener);

    store.upsert("n1", { status: "embedding", title: "T" });
    store.markFailed("n1", "e1");
    store.markFailed("n1", "e2");

    expect(listener).toHaveBeenCalledOnce();
  });

  it("does not fire for a first-seen row that arrives already terminal", () => {
    const store = new QueueStore();
    const listener = vi.fn();
    store.onTerminalTransition(listener);

    store.upsert("n1", { status: "ready", title: "T" });

    expect(listener).not.toHaveBeenCalled();
  });

  it("does not fire on intermediate phase changes", () => {
    const store = new QueueStore();
    const listener = vi.fn();
    store.onTerminalTransition(listener);

    store.upsert("n1", { status: "queued", title: "T" });
    store.upsert("n1", { status: "extracting_attachments" });
    store.upsert("n1", { status: "composing" });
    store.upsert("n1", { status: "embedding" });

    expect(listener).not.toHaveBeenCalled();
  });

  it("listener errors are swallowed and do not break subscribers", () => {
    const store = new QueueStore();
    store.onTerminalTransition(() => { throw new Error("boom"); });
    const sub = vi.fn();
    store.subscribe(sub);

    store.upsert("n1", { status: "composing", title: "T" });
    expect(() => store.markReady("n1", "p.md")).not.toThrow();
    expect(sub).toHaveBeenCalled();
  });
});
