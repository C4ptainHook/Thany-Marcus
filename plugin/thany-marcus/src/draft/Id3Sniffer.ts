const ID3V2_MAGIC = "ID3";
const ID3V2_HEADER_LEN = 10;
const ID3V2_VERSION_OFFSET = 3;
const ID3V2_SIZE_OFFSET = 6;
const TEXT_FRAME_ENCODING_BYTE_LEN = 1;

const ID3V1_MAGIC = "TAG";
const ID3V1_LEN = 128;
const ID3V1_TITLE_OFFSET = 3;
const ID3V1_ARTIST_OFFSET = 33;
const ID3V1_FIELD_LEN = 30;

interface FrameLayout {
  idLength: number;
  headerLength: number;
  titleFrameId: string;
  artistFrameId: string;
  readFrameSize(bytes: Uint8Array, frameStart: number): number;
}

function frameLayout(majorVersion: number): FrameLayout | null {
  switch (majorVersion) {
    case 2:
      return {
        idLength: 3,
        headerLength: 6,
        titleFrameId: "TT2",
        artistFrameId: "TP1",
        readFrameSize: (bytes, frameStart) => readBigEndian(bytes, frameStart + 3, 3),
      };
    case 3:
      return {
        idLength: 4,
        headerLength: 10,
        titleFrameId: "TIT2",
        artistFrameId: "TPE1",
        readFrameSize: (bytes, frameStart) => readBigEndian(bytes, frameStart + 4, 4),
      };
    case 4:
      return {
        idLength: 4,
        headerLength: 10,
        titleFrameId: "TIT2",
        artistFrameId: "TPE1",
        readFrameSize: (bytes, frameStart) => readSynchsafe(bytes, frameStart + 4),
      };
    default:
      return null;
  }
}

export function hasId3Tags(bytes: Uint8Array): boolean {
  return hasPopulatedId3v2Frame(bytes) || hasPopulatedId3v1Tag(bytes);
}

function hasPopulatedId3v2Frame(bytes: Uint8Array): boolean {
  if (bytes.length < ID3V2_HEADER_LEN || !matchesAscii(bytes, 0, ID3V2_MAGIC)) return false;

  const layout = frameLayout(bytes[ID3V2_VERSION_OFFSET]);
  if (layout === null) return false;

  const tagEnd = Math.min(bytes.length, ID3V2_HEADER_LEN + readSynchsafe(bytes, ID3V2_SIZE_OFFSET));

  let pos = ID3V2_HEADER_LEN;
  while (pos + layout.headerLength <= tagEnd) {
    const frameId = readAscii(bytes, pos, layout.idLength);
    if (frameId.charCodeAt(0) === 0) break;

    const frameSize = layout.readFrameSize(bytes, pos);
    if (frameSize <= 0) break;

    const isTitleOrArtist = frameId === layout.titleFrameId || frameId === layout.artistFrameId;
    if (isTitleOrArtist && frameSize > TEXT_FRAME_ENCODING_BYTE_LEN) return true;

    pos += layout.headerLength + frameSize;
  }
  return false;
}

function hasPopulatedId3v1Tag(bytes: Uint8Array): boolean {
  if (bytes.length < ID3V1_LEN) return false;
  const tagStart = bytes.length - ID3V1_LEN;
  if (!matchesAscii(bytes, tagStart, ID3V1_MAGIC)) return false;

  return hasNonPadding(bytes, tagStart + ID3V1_TITLE_OFFSET, ID3V1_FIELD_LEN)
    || hasNonPadding(bytes, tagStart + ID3V1_ARTIST_OFFSET, ID3V1_FIELD_LEN);
}

function matchesAscii(bytes: Uint8Array, offset: number, marker: string): boolean {
  for (let i = 0; i < marker.length; i++) {
    if (bytes[offset + i] !== marker.charCodeAt(i)) return false;
  }
  return true;
}

function readAscii(bytes: Uint8Array, start: number, len: number): string {
  let s = "";
  for (let i = 0; i < len; i++) s += String.fromCharCode(bytes[start + i] ?? 0);
  return s;
}

function readBigEndian(bytes: Uint8Array, offset: number, byteCount: number): number {
  let value = 0;
  for (let i = 0; i < byteCount; i++) value = (value << 8) | (bytes[offset + i] ?? 0);
  return value;
}

function readSynchsafe(bytes: Uint8Array, offset: number): number {
  let value = 0;
  for (let i = 0; i < 4; i++) value = (value << 7) | (bytes[offset + i] & 0x7f);
  return value;
}

function hasNonPadding(bytes: Uint8Array, start: number, len: number): boolean {
  const NUL = 0;
  const SPACE = " ".charCodeAt(0);
  for (let i = start; i < start + len && i < bytes.length; i++) {
    if (bytes[i] !== NUL && bytes[i] !== SPACE) return true;
  }
  return false;
}
