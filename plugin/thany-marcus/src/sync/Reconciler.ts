export interface ReconcileInput {
  // Notes the plugin believed were synced at last run (last-synced manifest).
  manifestIds: string[];
  // Notes currently present in the vault (rebuilt from metadataCache this launch).
  presentIds: Set<string>;
  // Cloud status per note id; "deleted" means already tombstoned cloud-side.
  cloudStatus: Map<string, string>;
}

export interface ReconcilePlan {
  // Notes that vanished locally while the plugin was closed and are still live in the cloud.
  // These are the deletions the backstop wants to propagate.
  tombstoneCandidates: string[];
  // True when the batch exceeds the safety threshold and must be confirmed, not auto-executed.
  tripped: boolean;
}

const ABSOLUTE_THRESHOLD = 10;
const FRACTION_THRESHOLD = 0.25;

// The self-healing backstop: a deletion that happened while the plugin was closed (or an intent the
// live handler missed) is recovered by diffing the last-synced manifest against what's actually in
// the vault now. The circuit breaker guards ONLY this path — a pass that wants to delete more than
// 10 notes OR more than 25% of the synced set is paused for explicit confirmation, because at that
// scale the likely cause is a detection bug or an unmounted vault, not a real mass-delete.
export function planReconciliation(input: ReconcileInput): ReconcilePlan {
  const candidates: string[] = [];
  for (const id of input.manifestIds) {
    if (input.presentIds.has(id)) continue;
    const status = input.cloudStatus.get(id);
    // Missing locally. Only a candidate if the cloud still thinks it's live. Unknown (absent from
    // the status map) is treated as live too — better to surface than to silently drop.
    if (status === "deleted") continue;
    candidates.push(id);
  }

  const m = input.manifestIds.length;
  const tripped =
    candidates.length > ABSOLUTE_THRESHOLD ||
    (m > 0 && candidates.length > FRACTION_THRESHOLD * m);

  return { tombstoneCandidates: candidates, tripped };
}
