import { describe, expect, it } from "vitest";
import { buildFallbackWikilink } from "./BrowView";

describe("buildFallbackWikilink", () => {
  it("strips the folder path and .md extension", () => {
    expect(buildFallbackWikilink("Inbox/foo.md")).toBe("[[foo]]");
  });

  it("handles nested paths", () => {
    expect(buildFallbackWikilink("Projects/Sub/My Note.md")).toBe("[[My Note]]");
  });

  it("strips .md case-insensitively", () => {
    expect(buildFallbackWikilink("foo.MD")).toBe("[[foo]]");
  });

  it("leaves a bare basename untouched", () => {
    expect(buildFallbackWikilink("foo")).toBe("[[foo]]");
  });
});
