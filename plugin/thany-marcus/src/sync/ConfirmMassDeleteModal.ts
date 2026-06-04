import { App, Modal, Setting } from "obsidian";

// Shown when the reconciliation backstop trips the circuit breaker. A real user mass-delete is
// rare; a detection bug or an unmounted vault is the usual cause — so nothing is tombstoned until
// the user explicitly confirms.
export class ConfirmMassDeleteModal extends Modal {
  private resolved = false;

  constructor(
    app: App,
    private readonly paths: string[],
    private readonly onChoice: (confirm: boolean) => void,
  ) {
    super(app);
  }

  onOpen(): void {
    const { contentEl } = this;
    contentEl.createEl("h3", { text: "Unusually many notes are missing" });
    contentEl.createEl("p", {
      text:
        `${this.paths.length} synced notes are gone locally but still live in the cloud. ` +
        `This can happen if the vault wasn't fully loaded. Tombstone them in the cloud too?`,
    });

    const list = contentEl.createEl("ul");
    for (const p of this.paths.slice(0, 20)) {
      list.createEl("li", { text: p });
    }
    if (this.paths.length > 20) {
      contentEl.createEl("p", { text: `…and ${this.paths.length - 20} more.` });
    }

    new Setting(contentEl)
      .addButton((b) =>
        b.setButtonText("Keep them (do nothing)").onClick(() => this.choose(false)),
      )
      .addButton((b) =>
        b
          .setWarning()
          .setButtonText(`Tombstone ${this.paths.length} notes`)
          .onClick(() => this.choose(true)),
      );
  }

  private choose(confirm: boolean): void {
    this.resolved = true;
    this.onChoice(confirm);
    this.close();
  }

  onClose(): void {
    this.contentEl.empty();
    if (!this.resolved) this.onChoice(false);
  }
}
