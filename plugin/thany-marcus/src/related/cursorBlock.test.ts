import { describe, expect, it } from "vitest";
import { type CursorEditor, extractCursorBlock } from "./cursorBlock";

function fakeEditor(text: string, cursorLine: number): CursorEditor {
  const lines = text.split("\n");
  return {
    lineCount: () => lines.length,
    getCursor: () => ({ line: cursorLine }),
    getLine: (n: number) => lines[n] ?? "",
  };
}

const MIN = 30;

describe("extractCursorBlock", () => {
  it("returns the paragraph at the cursor, not adjacent paragraphs", () => {
    const doc =
      "First paragraph line one.\nFirst paragraph line two.\n\nSecond paragraph entirely separate here.";
    const block = extractCursorBlock(fakeEditor(doc, 0), MIN);
    expect(block).toBe("First paragraph line one.\nFirst paragraph line two.");
    expect(block).not.toContain("Second paragraph");
  });

  it("widens a sub-floor block to its enclosing section", () => {
    const doc = "# My Heading\n\nintro paragraph that is plenty long here.\n\n- a\n";
    // cursor on the short "- a" list item (below the 30-char floor)
    const block = extractCursorBlock(fakeEditor(doc, 4), MIN);
    expect(block).toContain("My Heading");
    expect(block).toContain("- a");
  });

  it("returns empty string for an empty document", () => {
    expect(extractCursorBlock(fakeEditor("", 0), MIN)).toBe("");
  });
});
