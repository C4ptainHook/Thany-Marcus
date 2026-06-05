export type RelatedStrictness = "loose" | "balanced" | "strict";

export const RELATED_STRICTNESS_LEVELS: readonly RelatedStrictness[] = [
  "loose",
  "balanced",
  "strict",
];

export const RELATED_STRICTNESS_LABEL: Record<RelatedStrictness, string> = {
  loose: "Loose",
  balanced: "Balanced",
  strict: "Strict",
};

// Balanced omits MaxDistance so the request uses the cloud's configured (graph-calibrated)
// default; Strict/Loose bracket it. The cloud clamps these into its [floor, ceiling].
export function strictnessToMaxDistance(s: RelatedStrictness): number | undefined {
  switch (s) {
    case "strict":
      return 0.12;
    case "loose":
      return 0.19;
    case "balanced":
      return undefined;
  }
}
