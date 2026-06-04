import { describe, expect, it } from "vitest";
import { planFolderReconciliation } from "./FolderReconciler";

describe("planFolderReconciliation", () => {
  it("registers vault folders the cloud is missing", () => {
    const plan = planFolderReconciliation({
      vaultFolders: ["Projects", "Archive"],
      cloudFolders: ["Projects"],
    });
    expect(plan.toRegister).toEqual(["Archive"]);
    expect(plan.toUnregister).toEqual([]);
  });

  it("unregisters cloud folders no longer in the vault", () => {
    const plan = planFolderReconciliation({
      vaultFolders: ["Projects"],
      cloudFolders: ["Projects", "Gone"],
    });
    expect(plan.toRegister).toEqual([]);
    expect(plan.toUnregister).toEqual(["Gone"]);
  });

  it("is a no-op when the sets already match", () => {
    const plan = planFolderReconciliation({
      vaultFolders: ["A", "B"],
      cloudFolders: ["B", "A"],
    });
    expect(plan.toRegister).toEqual([]);
    expect(plan.toUnregister).toEqual([]);
  });

  it("diffs both directions at once", () => {
    const plan = planFolderReconciliation({
      vaultFolders: ["Keep", "New"],
      cloudFolders: ["Keep", "Stale"],
    });
    expect(plan.toRegister).toEqual(["New"]);
    expect(plan.toUnregister).toEqual(["Stale"]);
  });
});
