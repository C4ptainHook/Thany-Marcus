import { describe, expect, it } from "vitest";
import { strictnessToMaxDistance } from "./strictness";

describe("strictnessToMaxDistance", () => {
  it("maps strict to the precision-max dial", () => {
    expect(strictnessToMaxDistance("strict")).toBe(0.12);
  });

  it("maps loose to the overlap-tail dial", () => {
    expect(strictnessToMaxDistance("loose")).toBe(0.19);
  });

  it("omits maxDistance for balanced so the server applies its Auto/default", () => {
    expect(strictnessToMaxDistance("balanced")).toBeUndefined();
  });
});
