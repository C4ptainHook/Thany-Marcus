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
    default:
      if (s.startsWith("failed")) return "Failed";
      return status;
  }
}

export function isTerminal(status: string | null | undefined): boolean {
  if (!status) return false;
  const s = status.toLowerCase();
  return s === "succeeded" || s === "ready" || s.startsWith("failed");
}

export function isFailure(status: string | null | undefined): boolean {
  return !!status && status.toLowerCase().startsWith("failed");
}
