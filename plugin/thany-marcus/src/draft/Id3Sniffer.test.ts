import { describe, expect, it } from "vitest";
import { hasId3Tags } from "./Id3Sniffer";

const ISO_8859_1 = 0x00;

function writeAscii(buf: Uint8Array, offset: number, text: string): void {
  for (let i = 0; i < text.length; i++) buf[offset + i] = text.charCodeAt(i);
}

function textFrameData(text: string): number[] {
  return [ISO_8859_1, ...Array.from(text, (ch) => ch.charCodeAt(0))];
}

function id3v2(frames: Array<{ id: string; data: number[] }>): Uint8Array {
  const body: number[] = [];
  for (const f of frames) {
    body.push(...Array.from(f.id, (ch) => ch.charCodeAt(0)));
    const size = f.data.length;
    body.push((size >>> 24) & 0xff, (size >>> 16) & 0xff, (size >>> 8) & 0xff, size & 0xff);
    body.push(0x00, 0x00); // frame flags
    body.push(...f.data);
  }
  const s = body.length;
  const header = [
    ...Array.from("ID3", (ch) => ch.charCodeAt(0)),
    0x03, 0x00, 0x00, // version 2.3, no flags
    (s >>> 21) & 0x7f, (s >>> 14) & 0x7f, (s >>> 7) & 0x7f, s & 0x7f,
  ];
  return new Uint8Array([...header, ...body]);
}

function id3v1(opts: { title?: string; artist?: string }): Uint8Array {
  const buf = new Uint8Array(256);
  const tag = buf.length - 128;
  writeAscii(buf, tag, "TAG");
  if (opts.title) writeAscii(buf, tag + 3, opts.title.slice(0, 30));
  if (opts.artist) writeAscii(buf, tag + 33, opts.artist.slice(0, 30));
  return buf;
}

describe("hasId3Tags", () => {
  it("detects ID3v2 with a populated title frame", () => {
    expect(hasId3Tags(id3v2([{ id: "TIT2", data: textFrameData("My Song") }]))).toBe(true);
  });

  it("detects ID3v2 with a populated artist frame", () => {
    expect(hasId3Tags(id3v2([{ id: "TPE1", data: textFrameData("The Band") }]))).toBe(true);
  });

  it("detects an ID3v1 trailer with a title", () => {
    expect(hasId3Tags(id3v1({ title: "A Track" }))).toBe(true);
  });

  it("returns false for an ID3v2 tag whose title/artist frames hold only the encoding byte", () => {
    expect(hasId3Tags(id3v2([{ id: "TIT2", data: [ISO_8859_1] }, { id: "TPE1", data: [ISO_8859_1] }]))).toBe(false);
  });

  it("returns false for an ID3v1 trailer with blank fields", () => {
    expect(hasId3Tags(id3v1({}))).toBe(false);
  });

  it("returns false for a buffer with no tags", () => {
    expect(hasId3Tags(new Uint8Array([0xff, 0xfb, 0x90, 0x00, 0x01, 0x02, 0x03]))).toBe(false);
  });

  it("returns false for an empty buffer", () => {
    expect(hasId3Tags(new Uint8Array(0))).toBe(false);
  });
});
