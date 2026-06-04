import { describe, expect, it } from "vitest";
import { DeletionCoalescer, type CoalescedDeletes } from "./DeletionCoalescer";

function makeCoalescer(): { coalescer: DeletionCoalescer; fire: () => void; out: CoalescedDeletes[] } {
  const out: CoalescedDeletes[] = [];
  let pending: (() => void) | null = null;
  const coalescer = new DeletionCoalescer(
    (o) => out.push(o),
    400,
    (fn) => {
      pending = fn;
      return 1;
    },
    () => {
      pending = null;
    },
  );
  return { coalescer, fire: () => pending?.(), out };
}

describe("DeletionCoalescer", () => {
  it("groups a folder's child deletes under the folder, leaving others as singles", () => {
    const { coalescer, fire, out } = makeCoalescer();
    coalescer.noteDeleted("Thany/Cars/a.md", "id-a");
    coalescer.noteDeleted("Thany/Cars/b.md", "id-b");
    coalescer.noteDeleted("Thany/loose.md", "id-loose");
    coalescer.folderDeleted("Thany/Cars");
    fire();

    expect(out).toHaveLength(1);
    const { folders, singles } = out[0];
    expect(folders).toHaveLength(1);
    expect(folders[0].folder).toBe("Thany/Cars");
    expect(folders[0].noteIds.sort()).toEqual(["id-a", "id-b"]);
    expect(singles).toEqual([{ path: "Thany/loose.md", noteId: "id-loose" }]);
  });

  it("treats a lone note delete as a single", () => {
    const { coalescer, fire, out } = makeCoalescer();
    coalescer.noteDeleted("Thany/x.md", "id-x");
    fire();
    expect(out[0].folders).toHaveLength(0);
    expect(out[0].singles).toEqual([{ path: "Thany/x.md", noteId: "id-x" }]);
  });

  it("debounces a burst into a single flush", () => {
    const { coalescer, fire, out } = makeCoalescer();
    coalescer.noteDeleted("Thany/a.md", "a");
    coalescer.noteDeleted("Thany/b.md", "b");
    fire();
    expect(out).toHaveLength(1);
    expect(out[0].singles).toHaveLength(2);
  });
});
