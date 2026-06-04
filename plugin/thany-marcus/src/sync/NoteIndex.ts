// Path ↔ thany_note_id, rebuilt from metadataCache frontmatter on every launch. Rebuildable state,
// not durable — losing it is a non-event. Its one job: on a delete the file (and its frontmatter)
// is already gone, so the id must be resolved from the map maintained while the file existed.
export class NoteIndex {
  private readonly pathToId = new Map<string, string>();
  private readonly idToPath = new Map<string, string>();

  rebuild(entries: Array<{ path: string; noteId: string | null }>): void {
    this.pathToId.clear();
    this.idToPath.clear();
    for (const e of entries) {
      if (e.noteId) this.set(e.path, e.noteId);
    }
  }

  set(path: string, noteId: string): void {
    const prevId = this.pathToId.get(path);
    if (prevId && prevId !== noteId) this.idToPath.delete(prevId);
    this.pathToId.set(path, noteId);
    this.idToPath.set(noteId, path);
  }

  onCreateOrModify(path: string, noteId: string | null): void {
    if (noteId) this.set(path, noteId);
    else this.removeByPath(path);
  }

  /** Returns the id carried across the move (prefer the pre-move mapping; frontmatter may lag). */
  onRename(oldPath: string, newPath: string, noteId: string | null): string | null {
    const id = this.pathToId.get(oldPath) ?? noteId;
    this.removeByPath(oldPath);
    if (id) this.set(newPath, id);
    return id;
  }

  onDelete(path: string): string | null {
    const id = this.pathToId.get(path) ?? null;
    this.removeByPath(path);
    return id;
  }

  idFor(path: string): string | null {
    return this.pathToId.get(path) ?? null;
  }

  pathFor(noteId: string): string | null {
    return this.idToPath.get(noteId) ?? null;
  }

  has(noteId: string): boolean {
    return this.idToPath.has(noteId);
  }

  /** All synced note paths currently known (used by the reconciliation backstop). */
  paths(): string[] {
    return [...this.pathToId.keys()];
  }

  ids(): string[] {
    return [...this.idToPath.keys()];
  }

  size(): number {
    return this.idToPath.size;
  }

  private removeByPath(path: string): void {
    const id = this.pathToId.get(path);
    if (id !== undefined) {
      this.pathToId.delete(path);
      if (this.idToPath.get(id) === path) this.idToPath.delete(id);
    }
  }
}
