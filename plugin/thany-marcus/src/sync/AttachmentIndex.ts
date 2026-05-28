export type AttachmentIndexData = Record<string, string[]>;

export class AttachmentIndex {
  constructor(private data: AttachmentIndexData = {}) {}

  static fromJson(raw: unknown): AttachmentIndex {
    if (!raw || typeof raw !== "object") return new AttachmentIndex();
    return new AttachmentIndex({ ...(raw as AttachmentIndexData) });
  }

  toJson(): AttachmentIndexData {
    return { ...this.data };
  }

  set(noteId: string, paths: string[]): void {
    if (paths.length === 0) {
      delete this.data[noteId];
    } else {
      this.data[noteId] = [...paths];
    }
  }

  get(noteId: string): string[] {
    return this.data[noteId] ?? [];
  }

  delete(noteId: string): string[] {
    const paths = this.data[noteId] ?? [];
    delete this.data[noteId];
    return paths;
  }
}
