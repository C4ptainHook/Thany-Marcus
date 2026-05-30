export function phaseLabel(status: string | null | undefined): string {
  if (!status) return "Queued";
  const s = status.toLowerCase();
  switch (s) {
    case "queued":
    case "pending":
      return "Queued";
    case "extracting_attachments":
      return "Reading attachments";
    case "composing":
      return "Building note";
    case "routing":
      return "Picking project";
    case "extracting_entities":
      return "Finding entities";
    case "embedding":
      return "Indexing";
    case "succeeded":
    case "ready":
      return "Ready";
    case "cancelled":
      return "Cancelled";
    default:
      if (s.startsWith("failed")) return "Failed";
      return status;
  }
}

export function isTerminal(status: string | null | undefined): boolean {
  if (!status) return false;
  const s = status.toLowerCase();
  return s === "succeeded" || s === "ready" || s === "cancelled" || s.startsWith("failed");
}

export function isCancellable(status: string | null | undefined): boolean {
  if (!status) return true;
  const s = status.toLowerCase();
  if (s === "succeeded" || s === "ready" || s === "cancelled") return false;
  if (s.startsWith("failed")) return false;
  if (s === "dead_lettered") return false;
  return true;
}

export function isFailure(status: string | null | undefined): boolean {
  return !!status && status.toLowerCase().startsWith("failed");
}
