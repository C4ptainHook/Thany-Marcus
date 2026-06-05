export const BLOCK_MIN_CHARS = 30;

export interface CursorEditor {
  lineCount(): number;
  getCursor(): { line: number };
  getLine(n: number): string;
}

function isBlank(line: string): boolean {
  return line.trim().length === 0;
}

function isHeading(line: string): boolean {
  return /^#{1,6}\s/.test(line);
}

export function extractCursorBlock(editor: CursorEditor, minChars: number): string {
  const lineCount = editor.lineCount();
  if (lineCount === 0) return "";
  const cursorLine = Math.min(Math.max(editor.getCursor().line, 0), lineCount - 1);

  const lineAt = (n: number) => editor.getLine(n);

  let start = cursorLine;
  let end = cursorLine;
  if (!isBlank(lineAt(cursorLine))) {
    while (start > 0 && !isBlank(lineAt(start - 1))) start--;
    while (end < lineCount - 1 && !isBlank(lineAt(end + 1))) end++;
  }

  const join = (from: number, to: number): string => {
    const out: string[] = [];
    for (let i = from; i <= to; i++) out.push(lineAt(i));
    return out.join("\n").trim();
  };

  const block = join(start, end);
  if (block.length >= minChars) return block;

  let sectionStart = cursorLine;
  while (sectionStart > 0 && !isHeading(lineAt(sectionStart))) sectionStart--;
  if (!isHeading(lineAt(sectionStart))) sectionStart = 0;

  let sectionEnd = cursorLine;
  while (sectionEnd < lineCount - 1 && !isHeading(lineAt(sectionEnd + 1))) sectionEnd++;

  return join(sectionStart, sectionEnd);
}
