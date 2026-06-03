import { describe, expect, it } from "vitest";
import { IntentJournal, type Intent, type JournalFs } from "./IntentJournal";

class MemFs implements JournalFs {
  files = new Map<string, string>();
  dirs = new Set<string>();
  async exists(p: string): Promise<boolean> {
    return this.files.has(p) || this.dirs.has(p);
  }
  async read(p: string): Promise<string> {
    return this.files.get(p) ?? "";
  }
  async write(p: string, d: string): Promise<void> {
    this.files.set(p, d);
  }
  async append(p: string, d: string): Promise<void> {
    this.files.set(p, (this.files.get(p) ?? "") + d);
  }
  async remove(p: string): Promise<void> {
    this.files.delete(p);
  }
  async mkdir(p: string): Promise<void> {
    this.dirs.add(p);
  }
}

const tombstone = (id: string): Intent => ({
  kind: "tombstone",
  noteId: id,
  opId: `tombstone:${id}`,
  enqueuedAt: 1,
});

describe("IntentJournal", () => {
  it("resumes pending intents across a reload, never resets", async () => {
    const fs = new MemFs();
    const j1 = new IntentJournal(fs, "dir", "devA");
    await j1.enqueue(tombstone("a"));
    await j1.enqueue(tombstone("b"));

    const j2 = new IntentJournal(fs, "dir", "devA");
    const resumed = await j2.load();
    expect(resumed.map((i) => i.opId).sort()).toEqual(["tombstone:a", "tombstone:b"]);
  });

  it("drops acked intents on reload", async () => {
    const fs = new MemFs();
    const j1 = new IntentJournal(fs, "dir", "devA");
    await j1.enqueue(tombstone("a"));
    await j1.enqueue(tombstone("b"));
    await j1.ack("tombstone:a");

    const j2 = new IntentJournal(fs, "dir", "devA");
    const resumed = await j2.load();
    expect(resumed.map((i) => i.opId)).toEqual(["tombstone:b"]);
  });

  it("ignores a torn final line from a crash mid-append", async () => {
    const fs = new MemFs();
    const good = JSON.stringify(tombstone("a"));
    fs.files.set("dir/intent-journal-devA.ndjson", good + '\n{"opId":"tombstone:b","kind":"tom');

    const j = new IntentJournal(fs, "dir", "devA");
    const resumed = await j.load();
    expect(resumed.map((i) => i.opId)).toEqual(["tombstone:a"]);
  });

  it("dedupes by opId so a replayed enqueue is a no-op", async () => {
    const fs = new MemFs();
    const j = new IntentJournal(fs, "dir", "devA");
    await j.enqueue(tombstone("a"));
    await j.enqueue(tombstone("a"));
    expect(j.pending()).toHaveLength(1);
  });

  it("compaction rewrites the file with only live intents", async () => {
    const fs = new MemFs();
    const j = new IntentJournal(fs, "dir", "devA");
    await j.load();
    await j.enqueue(tombstone("a"));
    await j.enqueue(tombstone("b"));
    await j.ack("tombstone:a");
    await j.compact();
    const lines = (fs.files.get("dir/intent-journal-devA.ndjson") ?? "").trim().split("\n");
    expect(lines).toHaveLength(1);
    expect(JSON.parse(lines[0]).opId).toBe("tombstone:b");
  });

  it("namespaces the journal file by device id", async () => {
    const fs = new MemFs();
    await new IntentJournal(fs, "dir", "devA").enqueue(tombstone("a"));
    await new IntentJournal(fs, "dir", "devB").enqueue(tombstone("b"));
    expect([...fs.files.keys()].sort()).toEqual([
      "dir/intent-journal-devA.ndjson",
      "dir/intent-journal-devB.ndjson",
    ]);
  });
});
