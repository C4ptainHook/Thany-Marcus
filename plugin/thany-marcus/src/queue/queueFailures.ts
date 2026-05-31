import type { QueueExtractionFailure } from "./QueueStore";

// URL extraction always succeeds (full preview or minimal); url-kind entries are never user-visible failures.
export function visibleFailures(
  failures: QueueExtractionFailure[] | undefined,
): QueueExtractionFailure[] {
  return (failures ?? []).filter((f) => f.kind !== "url");
}
