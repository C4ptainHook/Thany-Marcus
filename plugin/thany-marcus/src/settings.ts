import { App, Notice, PluginSettingTab, Setting, requestUrl } from "obsidian";
import type ThanyMarcusPlugin from "./main";
import { detectUpdate } from "./update/detectUpdate";
import type { PrivacyMode, PublicModel, SynthesisPreset } from "./api";
import type { RelatedStrictness } from "./related/strictness";

export interface ThanyMarcusSettings {
  cloudUrl: string;
  token: string;
  portalUrl: string;
  vaultFolder: string;
  recordMimeType: string;
  syncCursor: string | null;
  privacyMode: PrivacyMode;
  publicModel: PublicModel;
  synthesisPreset: SynthesisPreset;
  customPrompt: string;
  googleApiKey: string;
  desktopNotifications: boolean;
  pixelChrome: boolean;
  actionEffects: boolean;
  relatedStrictness: RelatedStrictness;
  deviceId: string;
}

export const DEFAULT_SETTINGS: ThanyMarcusSettings = {
  cloudUrl: "",
  token: "",
  portalUrl: "https://portal.thany.click",
  vaultFolder: "Thany",
  recordMimeType: "audio/webm;codecs=opus",
  syncCursor: null,
  privacyMode: "public",
  publicModel: "gemini-3.5-flash",
  synthesisPreset: "zettelkasten",
  customPrompt: "",
  googleApiKey: "",
  desktopNotifications: true,
  pixelChrome: true,
  actionEffects: true,
  relatedStrictness: "balanced",
  deviceId: "",
};

// PREVIEW ONLY — these strings are NOT sent to the cloud. The cloud applies its own preset
// bodies from SynthesisPresetBodies in cloud-api. If you edit a preset here, edit the
// matching body in src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SynthesisPresets.cs
// or the user will see one preview and get a different output.
const PRESET_BODIES: Record<Exclude<SynthesisPreset, "custom">, string> = {
  zettelkasten:
    "You are writing a Zettelkasten-style atomic note in the user's voice.\n" +
    "Tone: first-person, present-tense, connecting. Short paragraphs. Each note states one idea\n" +
    "and links it to other ideas via [[wikilinks]] drawn from the entity list.",
  journal:
    "You are writing a dated journal entry in the user's voice.\n" +
    "Tone: first-person, narrative, reflective.",
  encyclopedic:
    "You are writing a neutral, third-person summary of the inputs.\n" +
    "Tone: encyclopedic, factual, dispassionate.",
  technical:
    "You are writing a terse, structured technical note.\n" +
    "Tone: precise, code-friendly.",
};

const MODEL_LABEL: Record<PublicModel, string> = {
  "gemini-2.5-flash-lite": "Gemini 2.5 Flash Lite (~$0.0004/note)",
  "gemini-2.5-flash":      "Gemini 2.5 Flash (~$0.002/note)",
  "gemini-3.5-flash":      "Gemini 3.5 Flash (~$0.008/note) — recommended",
};

export class ThanyMarcusSettingTab extends PluginSettingTab {
  constructor(app: App, private readonly plugin: ThanyMarcusPlugin) {
    super(app, plugin);
  }

  display(): void {
    const { containerEl } = this;
    containerEl.empty();

    new Setting(containerEl)
      .setName("Cloud URL")
      .setDesc("Your Thany-Marcus cloud base URL — issued by the portal.")
      .addText((t) =>
        t
          .setPlaceholder("https://your-cloud.thany.click")
          .setValue(this.plugin.settings.cloudUrl)
          .onChange(async (v) => {
            this.plugin.settings.cloudUrl = v.trim();
            await this.plugin.saveSettings();
          }),
      );

    new Setting(containerEl)
      .setName("Plugin token")
      .setDesc("tm_… token from the portal's Issue plugin token card.")
      .addText((t) => {
        t.inputEl.type = "password";
        t.setPlaceholder("tm_...")
          .setValue(this.plugin.settings.token)
          .onChange(async (v) => {
            this.plugin.settings.token = v.trim();
            await this.plugin.saveSettings();
          });
      });

    new Setting(containerEl)
      .setName("Vault folder")
      .setDesc("Root folder for synced notes and drafts. Default: Thany")
      .addText((t) =>
        t
          .setPlaceholder("Thany")
          .setValue(this.plugin.settings.vaultFolder)
          .onChange(async (v) => {
            this.plugin.settings.vaultFolder = v.trim() || "Thany";
            await this.plugin.saveSettings();
          }),
      );

    new Setting(containerEl)
      .setName("Test connection")
      .setDesc("Hits /health/ready on the cloud.")
      .addButton((b) =>
        b.setButtonText("Test").onClick(async () => {
          try {
            await this.plugin.api.health();
            new Notice("Thany: connection OK");
          } catch (e) {
            new Notice(`Thany: connection failed — ${(e as Error).message}`);
          }
        }),
      );

    containerEl.createEl("h3", { text: "Cloud updates" });

    new Setting(containerEl)
      .setName("Portal URL")
      .setDesc("Where the public release feed lives. Used only to check for new cloud versions.")
      .addText((t) =>
        t
          .setPlaceholder("https://portal.thany.click")
          .setValue(this.plugin.settings.portalUrl)
          .onChange(async (v) => {
            this.plugin.settings.portalUrl = v.trim();
            await this.plugin.saveSettings();
          }),
      );

    new Setting(containerEl)
      .setName("Check for updates")
      .setDesc("Compares your cloud's version against the release feed.")
      .addButton((b) =>
        b.setButtonText("Check").onClick(async () => {
          try {
            const current = await this.plugin.api.cloudVersion();
            if (current === null) {
              new Notice("Thany: could not read your cloud's version.");
              return;
            }
            const latest = await this.plugin.api.latestRelease(this.plugin.settings.portalUrl);
            const verdict = detectUpdate(current, latest);
            if (!verdict.available) {
              new Notice(`Thany: up to date (v${current}).`);
              return;
            }
            if (verdict.strategy === "in-place") {
              const res = await this.plugin.api.applyUpdate(latest!);
              new Notice(
                res.accepted
                  ? `Thany: updating to v${verdict.targetVersion} in place. ${verdict.costText}`
                  : `Thany: update not started — ${res.message}.`,
              );
            } else {
              const portal = this.plugin.settings.portalUrl.replace(/\/+$/, "");
              window.open(`${portal}/clouds?migrate=${encodeURIComponent(verdict.targetVersion ?? "")}`);
              new Notice(`Thany: v${verdict.targetVersion} needs a re-provision. Continue in the portal.`);
            }
          } catch (e) {
            new Notice(`Thany: update check failed — ${(e as Error).message}`);
          }
        }),
      );

    new Setting(containerEl)
      .setName("Update status")
      .setDesc("Shows the cloud's current in-place update phase.")
      .addButton((b) =>
        b.setButtonText("Refresh").onClick(async () => {
          try {
            const status = await this.plugin.api.updateStatus();
            const target = status.target_version ? ` → v${status.target_version}` : "";
            new Notice(`Thany update: ${status.phase}${target}${status.message ? ` — ${status.message}` : ""}`);
          } catch (e) {
            new Notice(`Thany: status failed — ${(e as Error).message}`);
          }
        }),
      );

    containerEl.createEl("h3", { text: "Recovery" });

    new Setting(containerEl)
      .setName("Save recovery code")
      .setDesc("Shown once. Store it like an Emergency Kit — it is the only way to recover access if you lose this device.")
      .addButton((b) =>
        b.setButtonText("Reveal").onClick(async () => {
          try {
            const code = await this.plugin.api.provisionRecoveryCode();
            new Notice(
              code === null
                ? "Thany: a recovery code was already issued for this cloud."
                : `Thany recovery code (save now): ${code}`,
              code === null ? 6000 : 0,
            );
          } catch (e) {
            new Notice(`Thany: could not issue a recovery code — ${(e as Error).message}`);
          }
        }),
      );

    const recovery = { code: "" };
    new Setting(containerEl)
      .setName("Recover access")
      .setDesc("On a fresh device: set the Cloud URL above, paste your recovery code, then recover.")
      .addText((t) => t.setPlaceholder("tmr_...").onChange((v) => { recovery.code = v.trim(); }))
      .addButton((b) =>
        b.setButtonText("Recover").onClick(async () => {
          if (!this.plugin.settings.cloudUrl || !recovery.code) {
            new Notice("Thany: set the Cloud URL and paste a recovery code first.");
            return;
          }
          try {
            const result = await this.plugin.api.redeemRecovery(this.plugin.settings.cloudUrl, recovery.code);
            if (result === null) {
              new Notice("Thany: recovery code rejected (or too many attempts — wait and retry).");
              return;
            }
            this.plugin.settings.token = result.token;
            await this.plugin.saveSettings();
            new Notice(`Thany: access recovered. New recovery code (save now): ${result.recovery_code}`, 0);
            this.display();
          } catch (e) {
            new Notice(`Thany: recovery failed — ${(e as Error).message}`);
          }
        }),
      );

    containerEl.createEl("h3", { text: "Synthesis" });

    new Setting(containerEl)
      .setName("Privacy mode")
      .setDesc(
        "Private = everything runs locally on your cloud (Qwen3 1.7B). " +
        "Public = synthesis runs on a hosted model with the API key you provide; extraction stays local.",
      )
      .addDropdown((d) =>
        d
          .addOption("private", "Private")
          .addOption("public",  "Public")
          .setValue(this.plugin.settings.privacyMode)
          .onChange(async (v) => {
            this.plugin.settings.privacyMode = v as PrivacyMode;
            await this.plugin.saveSettings();
            this.display();
          }),
      );

    if (this.plugin.settings.privacyMode === "public") {
      new Setting(containerEl)
        .setName("Synthesis model")
        .setDesc("API key for the matching provider is required below.")
        .addDropdown((d) => {
          for (const [v, label] of Object.entries(MODEL_LABEL)) {
            d.addOption(v, label);
          }
          d.setValue(this.plugin.settings.publicModel).onChange(async (v) => {
            this.plugin.settings.publicModel = v as PublicModel;
            await this.plugin.saveSettings();
          });
        });
    }

    new Setting(containerEl)
      .setName("Synthesis style")
      .setDesc("Editing the prompt below switches to 'custom'.")
      .addDropdown((d) =>
        d
          .addOption("zettelkasten", "Zettelkasten")
          .addOption("journal",      "Journal")
          .addOption("encyclopedic", "Encyclopedic")
          .addOption("technical",    "Technical")
          .addOption("custom",       "Custom")
          .setValue(this.plugin.settings.synthesisPreset)
          .onChange(async (v) => {
            const preset = v as SynthesisPreset;
            this.plugin.settings.synthesisPreset = preset;
            if (preset !== "custom") {
              this.plugin.settings.customPrompt = "";
            }
            await this.plugin.saveSettings();
            this.display();
          }),
      );

    const promptDescription = this.plugin.settings.synthesisPreset === "custom"
      ? "Your custom system prompt (the server appends the entity list and inputs)."
      : "Preview of the selected preset. Editing this will switch to 'custom'.";
    const presetPreview = this.plugin.settings.synthesisPreset === "custom"
      ? this.plugin.settings.customPrompt
      : PRESET_BODIES[this.plugin.settings.synthesisPreset];

    new Setting(containerEl)
      .setName("System prompt")
      .setDesc(promptDescription)
      .addTextArea((t) => {
        t.inputEl.rows = 8;
        t.inputEl.style.width = "100%";
        t.setValue(presetPreview).onChange(async (v) => {
          const currentPreset = this.plugin.settings.synthesisPreset;
          if (currentPreset !== "custom" && v !== PRESET_BODIES[currentPreset]) {
            // Flip to custom silently — deliberately NOT calling this.display() here so
            // the user's typing focus isn't stolen mid-keystroke. The Style dropdown will
            // show the stale preset name until the panel is reopened; the description text
            // ("Editing this will switch to 'custom'") is the only inline hint.
            this.plugin.settings.synthesisPreset = "custom";
          }
          this.plugin.settings.customPrompt = v;
          await this.plugin.saveSettings();
        });
      })
      .addExtraButton((b) =>
        b
          .setIcon("rotate-ccw")
          .setTooltip("Reset to preset default")
          .onClick(async () => {
            this.plugin.settings.synthesisPreset = "zettelkasten";
            this.plugin.settings.customPrompt = "";
            await this.plugin.saveSettings();
            this.display();
          }),
      );

    containerEl.createEl("h3", { text: "API keys" });
    containerEl.createEl("p", {
      text: "Your Google AI Studio key is stored in plaintext in this plugin's data.json. It is sent to the cloud per-ingest; the cloud never persists keys.",
      cls: "setting-item-description",
    });

    new Setting(containerEl)
      .setName("Google AI Studio key")
      .addText((t) => {
        t.inputEl.type = "password";
        t.setPlaceholder("AIza…")
          .setValue(this.plugin.settings.googleApiKey)
          .onChange(async (v) => {
            this.plugin.settings.googleApiKey = v.trim();
            await this.plugin.saveSettings();
          });
      })
      .addButton((b) =>
        b.setButtonText("Test").onClick(() => testGoogleKey(this.plugin.settings.googleApiKey)),
      );

    new Setting(containerEl)
      .setName("Desktop notifications")
      .setDesc("Show a desktop notification when a note finishes processing.")
      .addToggle((t) =>
        t.setValue(this.plugin.settings.desktopNotifications).onChange(async (v) => {
          this.plugin.settings.desktopNotifications = v;
          await this.plugin.saveSettings();
        }),
      );

    new Setting(containerEl)
      .setName("Related thoughts strictness")
      .setDesc(
        "How close a note must be to surface in the composer. Balanced uses the cloud's " +
        "calibrated default; you can also switch this from the composer panel.",
      )
      .addDropdown((d) =>
        d
          .addOption("loose", "Loose — surface more")
          .addOption("balanced", "Balanced")
          .addOption("strict", "Strict — surface fewer")
          .setValue(this.plugin.settings.relatedStrictness)
          .onChange(async (v) => {
            this.plugin.settings.relatedStrictness = v as RelatedStrictness;
            await this.plugin.saveSettings();
          }),
      );

    containerEl.createEl("h3", { text: "Appearance" });

    new Setting(containerEl)
      .setName("Pixel chrome")
      .setDesc("Brand Thany's own surfaces (composer, hub, status, callouts) with the pixel identity. Your note text is never touched.")
      .addToggle((t) =>
        t.setValue(this.plugin.settings.pixelChrome).onChange(async (v) => {
          this.plugin.settings.pixelChrome = v;
          this.plugin.applyChromeClasses();
          await this.plugin.saveSettings();
        }),
      );

    new Setting(containerEl)
      .setName("Action effects")
      .setDesc("Brief motion on actions you take (send, attach, phase flip). Honors your system 'reduce motion' setting. No ambient animation.")
      .addToggle((t) =>
        t.setValue(this.plugin.settings.actionEffects).onChange(async (v) => {
          this.plugin.settings.actionEffects = v;
          this.plugin.applyChromeClasses();
          await this.plugin.saveSettings();
        }),
      );

    new Setting(containerEl)
      .setName("Reset sync cursor")
      .setDesc("Force a full re-sync on next pull (does not delete vault files).")
      .addButton((b) =>
        b.setButtonText("Reset").onClick(async () => {
          this.plugin.settings.syncCursor = null;
          await this.plugin.saveSettings();
          new Notice("Thany: sync cursor reset");
        }),
      );
  }
}

async function testGoogleKey(key: string): Promise<void> {
  if (!key) {
    new Notice("Thany: enter a Google key first");
    return;
  }
  try {
    const res = await requestUrl({
      url: `https://generativelanguage.googleapis.com/v1beta/models?key=${encodeURIComponent(key)}`,
      method: "GET",
      throw: false,
    });
    if (res.status >= 200 && res.status < 300) new Notice("Thany: Google key OK");
    else new Notice(`Thany: Google key rejected (HTTP ${res.status})`);
  } catch (e) {
    new Notice(`Thany: Google key test failed — ${(e as Error).message}`);
  }
}

