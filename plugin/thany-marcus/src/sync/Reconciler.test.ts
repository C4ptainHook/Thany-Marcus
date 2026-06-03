import { describe, expect, it } from "vitest";
import { planReconciliation } from "./Reconciler";

describe("planReconciliation", () => {
  it("flags notes gone locally but still live in the cloud", () => {
    const plan = planReconciliation({
      manifestIds: ["a", "b", "c"],
      presentIds: new Set(["a"]),
      cloudStatus: new Map([
        ["a", "ready"],
        ["b", "ready"],
        ["c", "ready"],
      ]),
    });
    expect(plan.tombstoneCandidates.sort()).toEqual(["b", "c"]);
  });

  it("excludes notes the cloud already tombstoned", () => {
    const plan = planReconciliation({
      manifestIds: ["a", "b"],
      presentIds: new Set([]),
      cloudStatus: new Map([
        ["a", "deleted"],
        ["b", "ready"],
      ]),
    });
    expect(plan.tombstoneCandidates).toEqual(["b"]);
  });

  it("trips the breaker when more than 25% of a small synced set is missing", () => {
    const manifestIds = ["a", "b", "c", "d"];
    const plan = planReconciliation({
      manifestIds,
      presentIds: new Set(["a", "b"]),
      cloudStatus: new Map(manifestIds.map((id) => [id, "ready"])),
    });
    // 2 of 4 missing → 50% > 25% → tripped
    expect(plan.tombstoneCandidates).toHaveLength(2);
    expect(plan.tripped).toBe(true);
  });

  it("trips the breaker when more than 10 notes are missing", () => {
    const manifestIds = Array.from({ length: 100 }, (_, i) => `n${i}`);
    const present = new Set(manifestIds.slice(11)); // 11 missing
    const plan = planReconciliation({
      manifestIds,
      presentIds: present,
      cloudStatus: new Map(manifestIds.map((id) => [id, "ready"])),
    });
    expect(plan.tombstoneCandidates).toHaveLength(11);
    expect(plan.tripped).toBe(true);
  });

  it("does not trip for a single deletion in a large set", () => {
    const manifestIds = Array.from({ length: 100 }, (_, i) => `n${i}`);
    const present = new Set(manifestIds.slice(1)); // 1 missing
    const plan = planReconciliation({
      manifestIds,
      presentIds: present,
      cloudStatus: new Map(manifestIds.map((id) => [id, "ready"])),
    });
    expect(plan.tombstoneCandidates).toEqual(["n0"]);
    expect(plan.tripped).toBe(false);
  });
});
