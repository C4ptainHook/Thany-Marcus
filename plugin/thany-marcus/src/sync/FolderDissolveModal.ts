import { App, Modal, Setting } from "obsidian";

export type FolderDissolveChoice = "reroute" | "force_delete";

// Post-hoc prompt: the local folder is already gone (no pre-delete veto). The choice governs cloud
// behaviour and whether notes are re-materialised — not whether the local delete happened. Dismiss
// defaults to the safe option (preserve + reroute to Inbox).
export class FolderDissolveModal extends Modal {
  private resolved = false;

  constructor(
    app: App,
    private readonly folder: string,
    private readonly noteCount: number,
    private readonly onChoice: (choice: FolderDissolveChoice) => void,
  ) {
    super(app);
  }

  onOpen(): void {
    const { contentEl } = this;
    contentEl.createEl("h3", { text: `Folder "${this.folder}" deleted` });
    contentEl.createEl("p", {
      text:
        `${this.noteCount} synced note(s) lived in this folder. What should happen to them ` +
        `in the cloud?`,
    });

    new Setting(contentEl)
      .setName("Keep the notes")
      .setDesc("Move them to Inbox and re-pull them locally. Recommended.")
      .addButton((b) =>
        b
          .setButtonText("Keep & move to Inbox")
          .setCta()
          .onClick(() => this.choose("reroute")),
      );

    new Setting(contentEl)
      .setName("Delete everything")
      .setDesc("Tombstone all notes in this folder in the cloud (14-day restore window).")
      .addButton((b) =>
        b
          .setWarning()
          .setButtonText(`Delete ${this.noteCount} note(s)`)
          .onClick(() => this.choose("force_delete")),
      );
  }

  private choose(choice: FolderDissolveChoice): void {
    this.resolved = true;
    this.onChoice(choice);
    this.close();
  }

  onClose(): void {
    this.contentEl.empty();
    if (!this.resolved) this.onChoice("reroute");
  }
}
