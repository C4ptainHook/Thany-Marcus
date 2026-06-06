import { describe, expect, it } from "vitest";
import { compareSemver, detectUpdate } from "./detectUpdate";
import type { ReleaseDescriptor } from "./updateTypes";

function release(over: Partial<ReleaseDescriptor>): ReleaseDescriptor {
  return {
    version: "1.0.0",
    strategy: "in-place",
    compose_yaml: "services: {}",
    image_digests: {},
    model_tags: {},
    env_overlay: {},
    schema_min_from: "0.0.0",
    notes: null,
    released_at: "2026-06-06T00:00:00Z",
    ...over,
  };
}

describe("compareSemver", () => {
  it("orders core versions", () => {
    expect(compareSemver("1.0.1", "1.0.0")).toBe(1);
    expect(compareSemver("1.0.0", "1.0.1")).toBe(-1);
    expect(compareSemver("2.0.0", "1.9.9")).toBe(1);
    expect(compareSemver("1.2.3", "1.2.3")).toBe(0);
  });

  it("orders prerelease below its release", () => {
    expect(compareSemver("1.0.0-rc.1", "1.0.0")).toBe(-1);
    expect(compareSemver("1.0.0", "1.0.0-rc.1")).toBe(1);
    expect(compareSemver("1.0.0-rc.10", "1.0.0-rc.2")).toBe(1);
  });

  it("tolerates v-prefix and build metadata", () => {
    expect(compareSemver("v1.2.0", "1.1.0")).toBe(1);
    expect(compareSemver("1.2.0+build.7", "1.2.0")).toBe(0);
  });
});

describe("detectUpdate", () => {
  it("reports none when feed is empty", () => {
    expect(detectUpdate("1.0.0", null).available).toBe(false);
  });

  it("reports none when latest is not newer", () => {
    expect(detectUpdate("1.2.0", release({ version: "1.2.0" })).available).toBe(false);
    expect(detectUpdate("1.2.0", release({ version: "1.1.0" })).available).toBe(false);
  });

  it("reports an in-place update", () => {
    const r = detectUpdate("1.0.0", release({ version: "1.1.0", strategy: "in-place" }));
    expect(r.available).toBe(true);
    expect(r.targetVersion).toBe("1.1.0");
    expect(r.strategy).toBe("in-place");
  });

  it("reports a blue-green update for a structural release", () => {
    const r = detectUpdate("1.0.0", release({ version: "2.0.0", strategy: "blue-green" }));
    expect(r.strategy).toBe("blue-green");
  });

  it("forces blue-green when current is below the schema floor", () => {
    const r = detectUpdate("1.0.0", release({ version: "1.5.0", strategy: "in-place", schema_min_from: "1.3.0" }));
    expect(r.available).toBe(true);
    expect(r.strategy).toBe("blue-green");
  });
});
