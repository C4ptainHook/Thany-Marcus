const URL_RE = /\bhttps?:\/\/[^\s<>()'"`]+/g;

export function findUrls(text: string): string[] {
  const matches = text.match(URL_RE);
  if (!matches) return [];
  const trimmed = matches.map((u) => u.replace(/[.,;:)\]]+$/, ""));
  return Array.from(new Set(trimmed));
}
