export type GuardKind = "create" | "delete" | "modify" | "rename";

const TTL_MS = 8_000;

// vault.delete/create/modify done by the sync Writer re-fire vault.on(...) events. Without this,
// a cloud-driven delete would echo back as a user-initiated tombstone. The Writer marks each path
// just before it mutates; the event handler consumes the mark and skips propagation.
export class VaultEventGuard {
  private readonly entries = new Map<string, number>();

  constructor(private readonly now: () => number = () => Date.now()) {}

  suppress(kind: GuardKind, path: string): void {
    this.entries.set(`${kind}:${path}`, this.now() + TTL_MS);
  }

  consume(kind: GuardKind, path: string): boolean {
    const key = `${kind}:${path}`;
    const expiry = this.entries.get(key);
    if (expiry === undefined) return false;
    this.entries.delete(key);
    return expiry >= this.now();
  }
}
