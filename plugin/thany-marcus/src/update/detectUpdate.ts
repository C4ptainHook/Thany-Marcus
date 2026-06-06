import type { ReleaseDescriptor, UpdateAvailability, UpdateStrategy } from "./updateTypes";

interface ParsedVer {
  core: [number, number, number];
  pre: string | null;
}

function parse(value: string): ParsedVer | null {
  if (!value) return null;
  let s = value.trim();
  if (s.startsWith("v") || s.startsWith("V")) s = s.slice(1);
  const plus = s.indexOf("+");
  if (plus >= 0) s = s.slice(0, plus);

  let pre: string | null = null;
  const dash = s.indexOf("-");
  if (dash >= 0) {
    pre = s.slice(dash + 1);
    s = s.slice(0, dash);
    if (pre.length === 0) return null;
  }

  const parts = s.split(".");
  if (parts.length !== 3) return null;
  const nums = parts.map((p) => (/^\d+$/.test(p) ? Number(p) : NaN));
  if (nums.some((n) => Number.isNaN(n))) return null;
  return { core: [nums[0], nums[1], nums[2]], pre };
}

export function compareSemver(a: string, b: string): number {
  const pa = parse(a);
  const pb = parse(b);
  if (!pa && !pb) return 0;
  if (!pa) return -1;
  if (!pb) return 1;

  for (let i = 0; i < 3; i++) {
    if (pa.core[i] !== pb.core[i]) return pa.core[i] < pb.core[i] ? -1 : 1;
  }
  if (pa.pre === null && pb.pre === null) return 0;
  if (pa.pre === null) return 1;
  if (pb.pre === null) return -1;
  return comparePre(pa.pre, pb.pre);
}

function comparePre(a: string, b: string): number {
  const ai = a.split(".");
  const bi = b.split(".");
  const n = Math.min(ai.length, bi.length);
  for (let i = 0; i < n; i++) {
    const aNum = /^\d+$/.test(ai[i]);
    const bNum = /^\d+$/.test(bi[i]);
    let c: number;
    if (aNum && bNum) c = Number(ai[i]) - Number(bi[i]);
    else if (aNum) c = -1;
    else if (bNum) c = 1;
    else c = ai[i] < bi[i] ? -1 : ai[i] > bi[i] ? 1 : 0;
    if (c !== 0) return c < 0 ? -1 : 1;
  }
  return ai.length === bi.length ? 0 : ai.length < bi.length ? -1 : 1;
}

export function detectUpdate(
  currentVersion: string,
  latest: ReleaseDescriptor | null,
): UpdateAvailability {
  if (!latest) {
    return { available: false, currentVersion, targetVersion: null, strategy: null, costText: null };
  }
  if (compareSemver(latest.version, currentVersion) <= 0) {
    return { available: false, currentVersion, targetVersion: latest.version, strategy: null, costText: null };
  }

  const belowFloor =
    latest.schema_min_from.length > 0 && compareSemver(currentVersion, latest.schema_min_from) < 0;
  const strategy: UpdateStrategy = latest.strategy === "blue-green" || belowFloor ? "blue-green" : "in-place";
  const costText =
    strategy === "blue-green"
      ? "Re-provisions your cloud (~5–10 min). Your data is carried over; the old cloud is kept until the new one is verified."
      : "Applies in place (~1–2 min). Your cloud self-rolls-back if the new version fails its health check.";

  return { available: true, currentVersion, targetVersion: latest.version, strategy, costText };
}
