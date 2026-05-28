# Thany-Marcus Obsidian plugin

Capture composite notes (body + image + URL + voice) from inside Obsidian
and pull processed results back from your Thany-Marcus cloud. v1 is
sideload-only, desktop-only. See `docs/plugin-001-obsidian-handoff.md` in
the parent repo for the full spec.

## Build

```
pnpm install   # or npm install / yarn
pnpm build
```

Outputs `main.js` next to `manifest.json`.

## Sideload

1. Build (above) so `main.js` exists.
2. Copy this folder (`thany-marcus/`) into
   `<vault>/.obsidian/plugins/thany-marcus/`. Required files:
   `manifest.json`, `main.js`, `styles.css`.
3. Obsidian → Settings → Community plugins → enable **Thany-Marcus**.
4. In the plugin's settings tab paste **Cloud URL** + **Token** from
   the portal's *Issue plugin token* card. Click *Test connection*.

## Use

- **Cmd+Shift+T** (or ribbon icon) opens a new draft leaf.
- Paste images, drop files, record voice, or promote URLs to attachments
  via the brow above the editor.
- **Send** posts the composite to `/api/ingest/init` + `/finalize`.
- ~5–60s later the server-composed note arrives via sync and the draft
  leaf auto-switches to it.

## Known limits (v1)

- Cloud-wins on conflicts; local edits to synced notes are overwritten.
- No push-back (`POST /api/sync/push`).
- Desktop only — mobile lacks reliable `MediaRecorder`.
- One cloud per vault.
