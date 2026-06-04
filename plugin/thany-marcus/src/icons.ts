import { addIcon } from "obsidian";

export const TM_ICON = {
  mark: "tm-mark",
  mic: "tm-mic",
  attach: "tm-attach",
  link: "tm-link",
  check: "tm-check",
  sync: "tm-sync",
  reload: "tm-reload",
  alert: "tm-alert",
  image: "tm-image",
  voice: "tm-voice",
  file: "tm-file",
  close: "tm-close",
  expand: "tm-expand",
  collapse: "tm-collapse",
  plus: "tm-plus",
} as const;

export type ThanyIconId = (typeof TM_ICON)[keyof typeof TM_ICON];

const ICON_PATHS: Record<ThanyIconId, string> = {
  [TM_ICON.mark]: `<path d="M3 4h18v5h-6v11h-6V9H3V4z"/>`,
  [TM_ICON.mic]:
    `<path d="M10 3h4v1h1v8h-1v1h-4v-1H9V4h1V3z"/>` +
    `<path d="M7 10h2v4h6v-4h2v5h-2v1H9v-1H7v-5z"/>` +
    `<path d="M11 16h2v3h3v2H8v-2h3v-3z"/>`,
  [TM_ICON.attach]: `<path d="M5 5h16v10H7V9h10v2H9v2h10V7H5v10h14v2H3V5h2z"/>`,
  [TM_ICON.link]: `<path d="M4 6h7v2H4v8h7v2H2V6h2zm16 0h-7v2h7v8h-7v2h9V6h-2zm-3 5H7v2h10v-2z"/>`,
  [TM_ICON.check]:
    `<path d="M18 6h2v2h-2V6zm-2 4V8h2v2h-2zm-2 2v-2h2v2h-2zm-2 2h2v-2h-2v2zm-2 2h2v-2h-2v2zm-2 0v2h2v-2H8zm-2-2h2v2H6v-2zm0 0H4v-2h2v2z"/>`,
  [TM_ICON.sync]:
    `<path d="M4 9V7h12V5h2v2h2v2h-2v2h-2V9H4zm12 2h-2v2h2v-2zm0-6h-2V3h2v2zm4 12v-2H8v-2h2v-2H8v2H6v2H4v2h2v2h2v2h2v-2H8v-2h12z"/>`,
  [TM_ICON.reload]:
    `<path d="M16 2h-2v2h2v2H4v2H2v5h2V8h12v2h-2v2h2v-2h2V8h2V6h-2V4h-2V2zM6 20h2v2h2v-2H8v-2h12v-2h2v-5h-2v5H8v-2h2v-2H8v2H6v2H4v2h2v2z"/>`,
  [TM_ICON.alert]:
    `<path d="M13 1h-2v2H9v2H7v2H5v2H3v2H1v2h2v2h2v2h2v2h2v2h2v2h2v-2h2v-2h2v-2h2v-2h2v-2h2v-2h-2V9h-2V7h-2V5h-2V3h-2V1zm0 2v2h2v2h2v2h2v2h2v2h-2v2h-2v2h-2v2h-2v2h-2v-2H9v-2H7v-2H5v-2H3v-2h2V9h2V7h2V5h2V3h2zm0 4h-2v6h2V7zm0 8h-2v2h2v-2z"/>`,
  [TM_ICON.image]:
    `<path d="M4 3H2v18h20V3H4zm16 2v14H4V5h16zm-6 4h-2v2h-2v2H8v2H6v2h2v-2h2v-2h2v-2h2v2h2v2h2v-2h-2v-2h-2V9zM8 7H6v2h2V7z"/>`,
  [TM_ICON.voice]:
    `<path d="M8 4h12v16h-8v-8h6V8h-8v12H2v-8h6V4zm0 10H4v4h4v-4zm10 0h-4v4h4v-4z"/>`,
  [TM_ICON.file]: `<path d="M3 22h18V8h-2V6h-2v2h-2V6h2V4h-2V2H3v20zm2-2V4h8v6h6v10H5z"/>`,
  [TM_ICON.close]:
    `<path d="M5 5h2v2H5V5zm4 4H7V7h2v2zm2 2H9V9h2v2zm2 0h-2v2H9v2H7v2H5v2h2v-2h2v-2h2v-2h2v2h2v2h2v2h2v-2h-2v-2h-2v-2h-2v-2zm2-2v2h-2V9h2zm2-2v2h-2V7h2zm0 0V5h2v2h-2z"/>`,
  [TM_ICON.expand]:
    `<path d="M8 5v2h2V5H8zm4 4V7h-2v2h2zm2 2V9h-2v2h2zm0 2h2v-2h-2v2zm-2 2v-2h2v2h-2zm0 0h-2v2h2v-2zm-4 4v-2h2v2H8z"/>`,
  [TM_ICON.collapse]:
    `<path d="M7 8H5v2h2v2h2v2h2v2h2v-2h2v-2h2v-2h2V8h-2v2h-2v2h-2v2h-2v-2H9v-2H7V8z"/>`,
  [TM_ICON.plus]: `<path d="M11 4h2v7h7v2h-7v7h-2v-7H4v-2h7V4z"/>`,
};

const VIEWBOX_SCALE = 100 / 24;

let registered = false;

export function registerThanyIcons(): void {
  if (registered) return;
  registered = true;
  for (const [id, paths] of Object.entries(ICON_PATHS)) {
    addIcon(id, `<g transform="scale(${VIEWBOX_SCALE})" fill="currentColor">${paths}</g>`);
  }
}
