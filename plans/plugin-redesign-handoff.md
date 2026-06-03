# Plugin redesign — capture chrome + reading surface

**Status:** Draft — design locked in the 2026-06-04 grilling session; not yet implemented.
**Scope:** the Obsidian plugin (`plugin/thany-marcus`) only. Cloud touch is near-zero (see §E).
**Problem that opened it:** "It's in Obsidian style, but Thany functionality is hard to *spot* and *use*." Two failures — discoverability ("spot") and usability ("use") — with one root cause: the plugin is built to **camouflage** (`styles.css:1` — *"uses Obsidian CSS vars so it inherits the theme"*), so every Thany surface dissolves into the user's theme.

---

## North star (the through-line that makes every choice below cohere)

**Thany is a quiet, faithful layer *over* the user's thinking — never a system that takes it over.** Four locked principles fall out of it, and each design decision is just this philosophy applied:

- **Capture stays in the note** — not ripped into a separate composer surface.
- **Calm / juice-on-demand** — nothing loud at rest; effects only on actions the user took.
- **Content is sacred** — pixel/effects live on Thany's *chrome*; never on text the user reads or writes.
- **Origin stays open** — the user's original words are always present, never folded.

When a sub-decision is ambiguous, resolve it toward this north star.

---

## §A — Identity & theming: the "calm pixel" dial

The user runs Obsidian for note-taking and won't tolerate distraction, but wants Thany *spottable* and floated adopting the portal's pixel identity. These pull opposite ways (pixel/VT323 is loud, low-readability by design). The resolution is a **hard line**, not a slider:

**Pixel brands Thany's CHROME. The user's CONTENT stays theme-native.**

- **Chrome → pixel it:** composer buttons, attachment-bar, hub panel headers, status-bar anchor, toasts, modals, callout headers/icons in the reading view, the Thany mark.
- **Content → never touch:** note prose, note titles in lists, snippets, the **essence** body, the **Origin** body. (VT323 over a sentence = illegible; a fixed pixel palette over a reading view = the distraction the user vetoed.)

Three rules that decide "branded" vs "broken":

1. **VT323 is a display font, not a text font.** Headers / buttons / badges / marks only. The instant it touches something the user *reads*, switch to the vault font.
2. **Accent fixed, surroundings follow the vault.** One constant Thany accent color (the brand signal); background / border / dark-vs-light must read from Obsidian's CSS vars. No fixed pixel palette dropped on anyone's theme.
3. **Motion = calm / juice-on-demand.** Effects fire **only on user actions** — `SEND` transmit-glitch, attach card-pop, phase flip processing→ready, mic VU meter. **No ambient scanlines/CRT.** Everything gated behind `prefers-reduced-motion` **and** a settings toggle. CSS `@keyframes` first; `anime.js` (modular, tree-shakeable, vanilla, mobile-safe) only if the send sequence wants orchestration. **No WebGL** (CRTFilter-class libs) — mobile/perf risk inside Obsidian. If scanlines are ever used, opacity ≤ 0.07 (above ~0.15 they become distracting).

**Settings:** add to `ThanyMarcusSettings` (`plugin/thany-marcus/src/settings.ts:5`, defaults `:20`) — `pixelChrome: boolean` and `actionEffects: boolean`, both default-on, surfaced in `ThanyMarcusSettingTab.display()` (`:66`). Pixel skin and effects are **reversible polish**, not a commitment.

---

## §B — Icons: Pixelarticons via Obsidian's own plumbing

User: *"I don't want symbols only, look for icons."* Bare unicode glyphs are out.

- **[Pixelarticons](https://pixelarticons.com/)** — 816 free icons (MIT, `npm i pixelarticons`, maintained 2026). 24×24 strict pixel grid, rectangle-derived SVG paths, `fill="currentColor"`. Pixel-perfect at any size; inherits the surrounding color (so it takes the Thany accent where set, the vault text color elsewhere — satisfies §A rule 2 for free).
- **Integration = zero runtime dep.** Inline the ~8 SVGs actually used (capture/Thany-mark, mic, paperclip, link, waveform, check, sync, alert) → register via Obsidian [`addIcon(id, svgInner)`](https://docs.obsidian.md/Plugins/User+interface/Icons) (SVG content **without** the `<svg>` wrapper) → use `setIcon()` everywhere. Same plumbing the plugin already uses for Lucide (`main.ts:145` ribbon `"cloud"`, `settings.ts:208` `"rotate-ccw"`). Size via the `--icon-size` CSS var per surface.
- **Placement:** pixel icons on **Thany's own chrome only** — composer buttons, hub header, status anchor, and **replace the generic Lucide `cloud` ribbon (`main.ts:145`) + the queue view icon (`QueueSidebar.ts:54`) with a Thany pixel mark.** Leave Lucide on Obsidian-owned surfaces you can't restyle (command palette, right-click menus). The pixel-vs-Lucide clash on Thany surfaces *is* the spottability — that's the point.
- States are **icon + label**, never symbol-only.

---

## §C — Composer rework (`plugin/thany-marcus/src/draft/BrowView.ts`)

Capture **stays welded to the note** (decision: do *not* build a separate composer view/leaf — that conflates editing with capture and fails mobile). The strip just stops being invisible.

**1. The strip shrinks to actions only.** `BrowView.render()` (`:97`) today crams three things into `.tm-brow`: action buttons, **attachment chips** (`.tm-brow__chips`, `:138`), and Discard/Send. Keep only the actions: a pixel `▌THANY▐` mark + `mic` / `attach` / `+URL` (left, pixelarticons icons) and `Discard` / `Send` (right). `SEND` gets the transmit-glitch on click (§A rule 3).

**2. Attachments leave the strip → collapsed bar directly under it.** This fixes the user's named bug ("captured attachments displayed in brow hurts visibility"): chips in the toolbar don't scale (they wrap and grow the bar, shoving the note down) and hide the one decision that matters (extract/meta/ref, currently cryptic emoji `🔗/📇/⚙️` at `BrowView.ts:311–321`). Replace with:
   - A **one-line collapsed bar** under the strip: `📎 3 attached ▸`. Expands to a **fixed-height, horizontal-scroll filmstrip of preview cards**, then folds back — so it never reflows the editor unless opened.
   - Each card = a real preview + a **legible segmented mode toggle `EXTRACT / META / REF`** (not emoji):
     - image → thumbnail · voice → duration + ▶ · url → favicon + fetched title · file → type icon + name + size.
   - **Retracted:** an earlier "bottom dock" idea — it fights Obsidian's scrolling editor. Use the collapsed-under-strip bar instead.

**3. Related-thoughts stays on top** (the user explicitly likes the heads-up there). Today it's inserted `afterend` of `.tm-brow` (`BrowView.attach()` `:47–55`), between strip and editor — keep that position. Tighten `RelatedNotesPanel.renderItems()` (`related/RelatedNotesPanel.ts:124`) from a tall list to a **glanceable row of pills that expands on click**, so it doesn't push the writing down.

**Resulting vertical order in a draft:** strip (actions) → `[collapsed attach bar]` → Related heads-up → editor. **Mental model: thoughts up, artifacts in their own bar, your writing always visible.**

---

## §D — IA: one quiet hub + a status anchor

Today the product surface is scattered: Suggestions stacked above "Recent notes" in a summon-only sidebar (`queue/QueueSidebar.ts`), Related-thoughts under the composer, status as one word (`main.ts:179`), three different list names. Even the stylesheet has drifted (`styles.css` carries a dead `.tm-projects` block — projects were dropped).

**Recommended (lean, not yet ratified — see Open Questions): a consolidated hub that is *present but silent*.** Rationale: the original complaint was "can't *spot* it"; near-zero ambient would re-create exactly that. One Thany side panel (evolve `QueueSidebarView`, don't rebuild) with sections collapsed-when-idle:

- `[+ NEW DRAFT]` — the primary action finally gets a home.
- **Processing** — one line "all synced" at rest; auto-expands to the queue when something's in flight (current "Recent notes" list).
- **Suggestions** — collapsed with a count badge; **stop the in-your-face 60s poll** (`sidebar/EntitySuggestionsPanel.ts:36`, `pollMs`) — badge, not push; expand to govern entities.
- **Related-thoughts is NOT in the hub** — it's a capture-time aid, lives beside the composer.
- **Kill the three names.** Hub = "Thany"; sections = Processing / Suggestions.

**Status-bar anchor = the one ambient presence** (`main.ts:179`, `setStatusBar`): pixel Thany mark + state **icon + label** (`▌T▐ ✓ synced` / `▌T▐ ⟳ 2 processing`), click opens the hub. Icons, not unicode (§B).

---

## §E — Reading surface (the highest-leverage work)

The processed note is **already well-structured Markdown that rides native Obsidian rendering** — *not* raw markdown, and *not* zero-UI. So **do NOT build a custom renderer or a markdown post-processor** (both fight Obsidian, break editing/themes, and are large). The contribution is a **thin CSS skin + a couple of format hooks.**

**The format on disk** (`src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/EssenceLayout.cs:14`):
- `%% thany:essence %%` … `%% /thany:essence %%` — invisible Obsidian comment-fences (machine anchors; vanish in reading view; also the anchors for future "living Origin" write-back). Inside: `# Title`, `#tags [[wikilinks]]`, essence body (prose/bullets/checklist/table — `EssenceRenderer.cs`).
- `---` then `## Origin` + the verbatim user original (`EssenceLayout.cs:33–35`).
- `%% thany:sources %%` wrapping `## Sources` + per-attachment blocks (`SourcesRenderer.cs`): **visual media (image/video) render as visible embeds + caption; audio/url/file render as collapsed `> [!source]-` callouts.**
- `%% thany:processing %%` wrapping a collapsed `> [!info]- Processing details` callout (`ProcessingDetailsRenderer.cs`) — model/mode/preset/seed/date. Already demoted and folded.

**What native rendering already gets right (don't touch):** essence on top, fences invisible, sources/processing folded, processing demoted to the bottom.

**The real gaps + the fixes (thin CSS, content stays vault-font):**

1. **Layer legibility — the Origin boundary.** Essence and Origin both render as plain sections split by a bare `---` + `## Origin`. Replace that *visual* with a **pixel-accent provenance band** — e.g. `◇ ORIGIN · your words, untouched` — so crossing into Origin reads as the trust promise ("there are my actual words, preserved"), not an unmarked wall. **Verify-first:** target the boundary in reading mode via Obsidian's reading-view heading attribute (`h2[data-heading="Origin"]` — **confirm this attribute exists before building**); if it doesn't, have the cloud emit Origin with a stable class hook (only cloud change in this handoff, and only if needed).
2. **Origin stays OPEN and at EQUAL weight** (user decision). Do not fold it; do not grey/shrink it into a "substrate" — that's soft-hiding, which contradicts the choice. Same font/size as the essence, below the skinned boundary. The essence leads by **position** (on top, tight, the entry point), not by demoting Origin.
3. **`[!source]` is a custom callout type Obsidian draws with a generic pencil icon.** Skin `.callout[data-callout="source"]` with **pixelarticons per kind** (🎙 voice / 🔗 url / 🖼 image / 📄 file) and a provenance look. Keep collapsed.

**Calm/pixel locks carry straight over:** essence + Origin bodies = content = vault font, sacred. Callout *chrome* (Origin/Sources/Processing headers, source icons) = pixel accent.

---

## Scope & sequencing (from the productive-vs-destructive triage)

1. **Surgical wins first** (cheap, fix real failures, low risk): attachment preview cards + collapsed bar (§C2); Pixelarticons + Thany mark (§B); status anchor (§D).
2. **Reading-surface skin** (§E) — thin CSS: Origin provenance band + `[!source]` styling. *Verify `data-heading` before starting.* This is the highest leverage — it makes the "AI doesn't overwrite you" promise *visible*.
3. **Pixel chrome skin** (§A) — restrained, behind the settings toggle. Reversible polish.
4. **Hub consolidation** (§D) — pending the hub-vs-near-zero confirm below.

**Non-goals / explicitly retracted:** custom markdown renderer or post-processor (§E); bottom attachment dock (§C2); WebGL/CRT effect libs and ambient scanlines (§A); a separate composer view/leaf (§C); reading-view chrome on the essence body (it *is* the content).

---

## Open questions / parked

- **Hub IA not finally ratified:** consolidated-but-silent (§D, recommended) vs near-zero ambient (anchor + open-on-demand, no pinned panel). Confirm before building §D.
- **Pixel loudness:** "calm/juice-on-demand" is locked; within that, VT323-on-labels (the lean) vs accent-only-with-readable-labels was never explicitly nailed. Default to VT323 on labels per §A rule 1.
- **Post-Send murk (a "use" confusion, parked twice):** after `Send`, what is that draft note — still a draft, or the processed result that syncs over it? Resolve separately; not blocking this handoff.
- **`%% thany:essence %%` fences = anchors for "living Origin" write-back** (`/api/sync/push`, currently "push-back not supported in v1", `main.ts`). Out of scope here, but the reading skin must not break those anchors.
- **Precondition flag:** `manifest.json` declares `"isDesktopOnly": true`, which contradicts the standing mobile requirement that justified several choices here (no WebGL, horizontal-scroll filmstrip, pills). Resolve mobile capability separately; this handoff's choices are already mobile-safe.

## Code-seam index

| Concern | File:line |
| --- | --- |
| Camouflage principle | `plugin/thany-marcus/styles.css:1` |
| Ribbon icon (→ Thany mark) | `plugin/thany-marcus/src/main.ts:145` |
| Status-bar anchor | `plugin/thany-marcus/src/main.ts:179` (`setStatusBar`) |
| Composer render / strip | `plugin/thany-marcus/src/draft/BrowView.ts:97` |
| Attachment chips (→ move out) | `…/BrowView.ts:138` |
| Attachment mode emoji (→ segmented) | `…/BrowView.ts:311–321` |
| Related panel position / list | `…/BrowView.ts:47–55`, `related/RelatedNotesPanel.ts:124` |
| Queue/hub view | `plugin/thany-marcus/src/queue/QueueSidebar.ts` (icon `:54`) |
| Suggestions poll | `plugin/thany-marcus/src/sidebar/EntitySuggestionsPanel.ts:36` |
| Settings shape / toggle home | `plugin/thany-marcus/src/settings.ts:5`, `:20`, `:66` |
| Note assembly | `src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/EssenceLayout.cs:14` |
| Origin block | `…/EssenceLayout.cs:33–35` |
| Sources / `[!source]` callouts | `…/Synthesis/SourcesRenderer.cs` |
| Processing details callout | `…/Synthesis/ProcessingDetailsRenderer.cs` |
