import { describe, expect, it } from "vitest";
import { NoteIndex } from "./NoteIndex";

describe("NoteIndex", () => {
  it("rebuilds path↔id from frontmatter, skipping notes without an id", () => {
    const idx = new NoteIndex();
    idx.rebuild([
      { path: "Thany/a.md", noteId: "id-a" },
      { path: "Thany/b.md", noteId: null },
    ]);
    expect(idx.idFor("Thany/a.md")).toBe("id-a");
    expect(idx.idFor("Thany/b.md")).toBeNull();
    expect(idx.pathFor("id-a")).toBe("Thany/a.md");
  });

  it("resolves the id on delete after the frontmatter is gone", () => {
    const idx = new NoteIndex();
    idx.set("Thany/a.md", "id-a");
    expect(idx.onDelete("Thany/a.md")).toBe("id-a");
    expect(idx.idFor("Thany/a.md")).toBeNull();
    expect(idx.has("id-a")).toBe(false);
  });

  it("preserves the id across a rename and remaps the path", () => {
    const idx = new NoteIndex();
    idx.set("Thany/Cars/a.md", "id-a");
    const moved = idx.onRename("Thany/Cars/a.md", "Thany/Inbox/a.md", null);
    expect(moved).toBe("id-a");
    expect(idx.pathFor("id-a")).toBe("Thany/Inbox/a.md");
    expect(idx.idFor("Thany/Cars/a.md")).toBeNull();
  });

  it("reassigns an id when a path's frontmatter id changes", () => {
    const idx = new NoteIndex();
    idx.set("Thany/a.md", "id-old");
    idx.set("Thany/a.md", "id-new");
    expect(idx.idFor("Thany/a.md")).toBe("id-new");
    expect(idx.has("id-old")).toBe(false);
  });
});
