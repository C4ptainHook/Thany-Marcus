import { describe, expect, it } from "vitest";
import { VaultEventGuard } from "./VaultEventGuard";

describe("VaultEventGuard", () => {
  it("consumes a suppressed event exactly once", () => {
    const g = new VaultEventGuard();
    g.suppress("delete", "Thany/a.md");
    expect(g.consume("delete", "Thany/a.md")).toBe(true);
    expect(g.consume("delete", "Thany/a.md")).toBe(false);
  });

  it("does not suppress an unrelated path or kind", () => {
    const g = new VaultEventGuard();
    g.suppress("delete", "Thany/a.md");
    expect(g.consume("create", "Thany/a.md")).toBe(false);
    expect(g.consume("delete", "Thany/b.md")).toBe(false);
  });

  it("expires a stale suppression so a real later event is not swallowed", () => {
    let now = 0;
    const g = new VaultEventGuard(() => now);
    g.suppress("delete", "Thany/a.md");
    now = 10_000; // past the 8s TTL
    expect(g.consume("delete", "Thany/a.md")).toBe(false);
  });
});
