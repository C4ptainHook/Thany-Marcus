import { describe, expect, it } from "vitest";
import { classifyKind, defaultModeFor, nextMode } from "./DraftManager";

function id3v1WithTitle(): Uint8Array {
  const buf = new Uint8Array(256);
  const tag = buf.length - 128;
  const marker = "TAG";
  const title = "Tagged Song";
  for (let i = 0; i < marker.length; i++) buf[tag + i] = marker.charCodeAt(i);
  for (let i = 0; i < title.length; i++) buf[tag + 3 + i] = title.charCodeAt(i);
  return buf;
}

describe("classifyKind", () => {
  it("folds video/* into file", () => {
    expect(classifyKind("video/mp4")).toBe("file");
    expect(classifyKind("video/quicktime")).toBe("file");
  });

  it("classifies image and audio", () => {
    expect(classifyKind("image/png")).toBe("image");
    expect(classifyKind("audio/mpeg")).toBe("voice");
  });

  it("falls back to file for unknown/null", () => {
    expect(classifyKind("application/pdf")).toBe("file");
    expect(classifyKind(null)).toBe("file");
  });
});

describe("defaultModeFor", () => {
  it("image always defaults to extract", () => {
    expect(defaultModeFor("image", "image/png")).toBe("extract");
  });

  it("video file defaults to reference", () => {
    expect(defaultModeFor("file", "video/mp4")).toBe("reference");
  });

  it("non-video file defaults to extract", () => {
    expect(defaultModeFor("file", "application/pdf")).toBe("extract");
  });

  it("audio without ID3 tags defaults to extract", () => {
    expect(defaultModeFor("voice", "audio/mpeg", new Uint8Array([0xff, 0xfb, 0x00]))).toBe("extract");
  });

  it("audio with ID3 tags defaults to reference", () => {
    expect(defaultModeFor("voice", "audio/mpeg", id3v1WithTitle())).toBe("reference");
  });

  it("url defaults to extract", () => {
    expect(defaultModeFor("url", null)).toBe("extract");
  });
});

describe("nextMode", () => {
  it("flips extract and reference for non-url kinds", () => {
    expect(nextMode("extract", "voice")).toBe("reference");
    expect(nextMode("reference", "voice")).toBe("extract");
    expect(nextMode("extract", "file")).toBe("reference");
    expect(nextMode("reference", "image")).toBe("extract");
  });

  it("cycles extract → metadata → reference → extract for urls", () => {
    expect(nextMode("extract", "url")).toBe("metadata");
    expect(nextMode("metadata", "url")).toBe("reference");
    expect(nextMode("reference", "url")).toBe("extract");
  });
});
