import { describe, expect, it, vi } from "vitest";
import { IntentJournal, type Intent, type JournalFs } from "./IntentJournal";
import { IntentQueue, type IntentExecutor } from "./IntentQueue";

class MemFs implements JournalFs {
  files = new Map<string, string>();
  async exists(p: string) { return this.files.has(p); }
  async read(p: string) { return this.files.get(p) ?? ""; }
  async write(p: string, d: string) { this.files.set(p, d); }
  async append(p: string, d: string) { this.files.set(p, (this.files.get(p) ?? "") + d); }
  async remove(p: string) { this.files.delete(p); }
  async mkdir() { /* noop */ }
}

const tombstone = (id: string): Intent => ({
  kind: "tombstone",
  noteId: id,
  opId: `tombstone:${id}`,
  enqueuedAt: 1,
});

function noopExec(over: Partial<IntentExecutor> = {}): IntentExecutor {
  return {
    tombstone: vi.fn(async () => {}),
    revive: vi.fn(async () => {}),
    reroute: vi.fn(async () => {}),
    folderDissolve: vi.fn(async () => {}),
    folderRegister: vi.fn(async () => {}),
    folderUnregister: vi.fn(async () => {}),
    ...over,
  };
}

describe("IntentQueue", () => {
  it("drains a pending intent and acks it on success", async () => {
    const fs = new MemFs();
    const journal = new IntentJournal(fs, "d", "devA");
    const exec = noopExec();
    const q = new IntentQueue(journal, exec);

    await q.enqueue(tombstone("a"));
    await q.drain();

    expect(exec.tombstone).toHaveBeenCalledWith("a");
    expect(journal.pending()).toHaveLength(0);
  });

  it("keeps a failed intent in the journal and schedules a retry, then acks on a later drain", async () => {
    const fs = new MemFs();
    const journal = new IntentJournal(fs, "d", "devA");
    await journal.enqueue(tombstone("a")); // journal.enqueue does not auto-drain

    let attempts = 0;
    const exec = noopExec({
      tombstone: vi.fn(async () => {
        attempts++;
        if (attempts === 1) throw new Error("boom");
      }),
    });
    const scheduled: Array<() => void> = [];
    const q = new IntentQueue(journal, exec, (fn) => {
      scheduled.push(fn);
      return scheduled.length;
    });

    await q.drain(); // attempt 1 fails
    expect(journal.pending()).toHaveLength(1);
    expect(scheduled).toHaveLength(1);

    await q.drain(); // the scheduled retry firing → attempt 2 succeeds
    expect(attempts).toBe(2);
    expect(journal.pending()).toHaveLength(0);
  });

  it("routes folder_register and folder_unregister intents to the executor", async () => {
    const fs = new MemFs();
    const journal = new IntentJournal(fs, "d", "devA");
    const exec = noopExec();
    const q = new IntentQueue(journal, exec);

    await q.enqueue({
      kind: "folder_register",
      folder: "Projects/Foo",
      opId: "folder_register:Projects/Foo",
      enqueuedAt: 1,
    });
    await q.enqueue({
      kind: "folder_unregister",
      folder: "Archive",
      opId: "folder_unregister:Archive",
      enqueuedAt: 2,
    });
    await q.drain();

    expect(exec.folderRegister).toHaveBeenCalledWith("Projects/Foo");
    expect(exec.folderUnregister).toHaveBeenCalledWith("Archive");
    expect(journal.pending()).toHaveLength(0);
  });

  it("replays resumed intents idempotently after a reload", async () => {
    const fs = new MemFs();
    // A crash before ack leaves just the enqueue line in the journal.
    const j1 = new IntentJournal(fs, "d", "devA");
    await j1.enqueue(tombstone("a"));

    const j2 = new IntentJournal(fs, "d", "devA");
    await j2.load();
    const exec = noopExec();
    const q = new IntentQueue(j2, exec);
    await q.drain();

    expect(exec.tombstone).toHaveBeenCalledTimes(1);
    expect(j2.pending()).toHaveLength(0);
  });
});
