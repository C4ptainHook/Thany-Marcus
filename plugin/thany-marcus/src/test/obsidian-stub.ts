export const requestUrl = async () => {
  throw new Error("requestUrl not stubbed in tests");
};
export class Notice {
  constructor(_msg?: string) {}
}
export class Plugin {}
export class MarkdownView {}
export class WorkspaceLeaf {}
export class ItemView {
  constructor(_leaf: unknown) {}
}
export class Modal {
  constructor(_app: unknown) {}
}
export class Setting {
  constructor(_containerEl: unknown) {}
  setName() { return this; }
  setDesc() { return this; }
  addText() { return this; }
  addButton() { return this; }
}
export class App {}
export const TFile = class {};
