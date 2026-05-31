import { describe, expect, it } from "vitest";
import { visibleFailures } from "./queueFailures";

describe("visibleFailures", () => {
  it("drops url-kind failures so URL outcomes never raise the badge count", () => {
    const out = visibleFailures([
      { kind: "url", attachmentId: "a1", reason: "http: 404" },
      { kind: "image", attachmentId: "a2", reason: "vlm timeout" },
    ]);
    expect(out).toEqual([
      { kind: "image", attachmentId: "a2", reason: "vlm timeout" },
    ]);
  });

  it("keeps image, voice, and file failures", () => {
    const out = visibleFailures([
      { kind: "image", attachmentId: "i", reason: "x" },
      { kind: "voice", attachmentId: "v", reason: "x" },
      { kind: "file", attachmentId: "f", reason: "x" },
    ]);
    expect(out).toHaveLength(3);
  });

  it("returns empty array for undefined", () => {
    expect(visibleFailures(undefined)).toEqual([]);
  });
});
