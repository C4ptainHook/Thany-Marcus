# Portal.Web UI Design — Execution Spec (temp)

**Status:** Temp spec from 2026-05-17 grilling. Foundation for implementation, not an ADR. Will be promoted / superseded once first pass lands and gaps surface.

**Goal:** Give Portal.Web a coherent design system + IA + dashboard composition. Replace the current raw-browser-default 5-page MVP shell (sign-in, wizard, cloud detail, security, totp-challenge) with the pixel/retro + GCP-accent visual language and dashboard-first information architecture.

**Source:** Decisions locked during 2026-05-17 grilling. Full decision rationale lives in `memory/portal_ux_locks.md`. This file is the *executable* version — what to build, where.

---

## Where decisions live

- **`memory/portal_ux_locks.md`** — full decision log with rationale per branch; read for the "why" behind any spec item here
- **`memory/portal_web_stack.md`** — SvelteKit + adapter-static; not a .NET project; pnpm workspace under `src/ThanyMarcus.Portal.Web/`
- **`memory/portal_architecture.md`** — SSE only for server-push; Minimal APIs + VSA; mutable status enum
- **`memory/thesis_context.md`** — cloud-pivot + Obsidian-plugin pivot context; thesis defense framing
- **`plans/portal-015-handoff.md`** — destroy flow (saga side); UI consumes the SSE endpoint it adds
- **`plans/portal-011-handoff.md`** — provisioning SSE; UI consumes `phase_started/phase_completed/phase_failed/cloud_ready/cloud_failed/cloud_rolled_back`

---

## Scope boundary

### In scope (this spec)
- Design tokens — CSS variables for palette, type, spacing, shape
- Top-level layout — top bar + main content shell
- Route shapes — what each route renders, and which states `/` walks through
- Component specs — status pill, cost card, plugin token card, danger zone, show-once modal
- `/healthz` API contract extension — three new fields the dashboard depends on
- Implementation phases — suggested build order

### Deferred (separate spec passes when surfaces have features ready)
- Step-up auth modal visual treatment (component exists in code; styling pass only)
- Avatar menu specifics (dropdown contents, trigger, direction)
- Loading states (skeleton vs spinner vs "Loading…") for each surface
- Empty-state copywriting (concrete strings)
- Provisioning failure recovery / partial-failure stranded-resource UX
- Provider token entry UX flow detail (step-up + paste UX)
- Sign-in error states (post-callback failure UX)
- Accessibility audit (focus traps in modals, ARIA on status pill, contrast ratios under VT323)

### Out of scope
- Backend changes beyond the `/healthz` extension (covered in a future ticket)
- Mobile-specific feature gating — we are full-responsive with no gating per Q13
- Multi-cloud UI — one cloud per user per Q2

---

## Design tokens

Create `src/ThanyMarcus.Portal.Web/src/app.css` to replace the current minimal tokens.

### Type

Use VT323 from Google Fonts. Self-host the WOFF2 in `static/fonts/VT323/` to avoid a Google CDN dependency at runtime; reference via `@font-face` in `app.css`.

```css
:root {
  --font-pixel: 'VT323', ui-monospace, monospace;
  --font-mono:  ui-monospace, monospace;

  --text-xs:   16px;  /* dense metadata, footnotes */
  --text-sm:   18px;  /* body */
  --text-base: 20px;  /* primary body, inputs, labels, buttons */
  --text-lg:   24px;  /* section h2 */
  --text-xl:   28px;  /* page h1, wordmark */
  --text-2xl:  36px;  /* hero on landing */

  --leading-tight: 1.1;
  --leading-normal: 1.3;

  font-family: var(--font-pixel);
  font-size: var(--text-base);
  line-height: var(--leading-normal);
  font-variant-numeric: tabular-nums;
  -webkit-font-smoothing: none;   /* pixel fonts look better unsmoothed */
  -moz-osx-font-smoothing: unset;
}
```

VT323 below 16px becomes unreadable. **Do not go below 16px.**

### Palette — dark base + Google GCP accents

```css
:root {
  --bg:         #0a0d0e;
  --surface:    #14181a;
  --surface-2:  #1c2124;
  --border:     #2a3034;
  --border-dim: #1f2326;

  --text:       #e8eaed;
  --text-dim:   #9aa0a6;
  --text-muted: #5f6368;

  --primary:    #4285F4;  /* Google Blue — primary action, focus, accent */
  --success:    #34A853;  /* Google Green — ready, completed */
  --warn:       #FBBC04;  /* Google Yellow — warming, attention */
  --error:      #EA4335;  /* Google Red — destroy, failed */

  --primary-dim: #2c5fc8; /* hover/active for primary */
  --error-dim:   #b8362d; /* hover for error */
}
```

No light mode. No alternate theme.

### Spacing & shape

```css
:root {
  --space-1: 4px;
  --space-2: 8px;
  --space-3: 12px;
  --space-4: 16px;
  --space-5: 24px;
  --space-6: 32px;
  --space-7: 48px;
  --space-8: 64px;

  --radius:   0;      /* default — hard edges */
  --radius-sm: 2px;   /* only where 0 looks wrong (inputs in some browsers) */

  --border-width: 1px;

  --content-max: 720px;
  --top-bar-h: 56px;
}
```

### Component base styles

Add to `app.css`:

```css
body {
  background: var(--bg);
  color: var(--text);
  margin: 0;
}

main {
  max-width: var(--content-max);
  margin: 0 auto;
  padding: var(--space-5) var(--space-4);
}

h1 { font-size: var(--text-xl); line-height: var(--leading-tight); margin: 0 0 var(--space-4); }
h2 { font-size: var(--text-lg); line-height: var(--leading-tight); margin: 0 0 var(--space-3); color: var(--text); }

.muted     { color: var(--text-dim); }
.muted-2   { color: var(--text-muted); }
.success   { color: var(--success); }
.warn      { color: var(--warn); }
.error     { color: var(--error); }

a {
  color: var(--primary);
  text-decoration: underline;
  text-underline-offset: 2px;
}
a:hover { color: var(--text); }

/* Buttons — 3 variants, all 40px tall */
button, .btn {
  font: inherit;
  height: 40px;
  padding: 0 var(--space-4);
  border-radius: var(--radius);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
}
.btn-primary {
  background: var(--primary);
  color: var(--bg);
  border: var(--border-width) solid var(--primary);
}
.btn-primary:hover    { background: var(--primary-dim); border-color: var(--primary-dim); }
.btn-primary:disabled { background: var(--surface-2); color: var(--text-muted); border-color: var(--border); cursor: not-allowed; }

.btn-secondary {
  background: transparent;
  color: var(--text);
  border: var(--border-width) solid var(--text);
}
.btn-secondary:hover { background: var(--surface); }

.btn-ghost {
  background: transparent;
  color: var(--text-dim);
  border: var(--border-width) solid transparent;
  padding: 0 var(--space-2);
}
.btn-ghost:hover { color: var(--text); }

.btn-danger {
  background: transparent;
  color: var(--error);
  border: var(--border-width) solid var(--error);
}
.btn-danger:hover { background: var(--error); color: var(--bg); }

/* Inputs */
input, select, textarea {
  font: inherit;
  height: 40px;
  padding: 0 var(--space-3);
  background: var(--surface);
  color: var(--text);
  border: var(--border-width) solid var(--border);
  border-radius: var(--radius);
}
input:focus, select:focus, textarea:focus {
  outline: 2px solid var(--primary);
  outline-offset: -2px;
}
textarea { height: auto; padding: var(--space-3); }

/* Cards */
.card {
  background: var(--surface);
  border: var(--border-width) solid var(--border);
  padding: var(--space-4);
  margin-bottom: var(--space-4);
}

/* Status pill — dot + label */
.pill {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  padding: 0 var(--space-3);
  height: 28px;
  border: var(--border-width) solid var(--border);
  font-size: var(--text-sm);
}
.pill .dot { font-size: var(--text-base); line-height: 1; }
.pill.success .dot { color: var(--success); }
.pill.warn    .dot { color: var(--warn); }
.pill.idle    .dot { color: var(--text-muted); }
.pill.error   .dot { color: var(--error); }
```

### Responsive rule

Single breakpoint at 600px. Below, `main` gets `padding: var(--space-3)`; grids collapse to single column; modals become full-screen sheets (`position: fixed; inset: 0`).

---

## Routes & states

### `/` — canonical home (the dashboard route)

Walks through five states based on the user's account + cloud:

| Condition | State rendered |
|---|---|
| Not signed in | Sign-in screen (Google button, Turnstile when required) |
| Signed in, no passphrase OR no provider token OR no cloud | **Onboarding checklist card** |
| Cloud exists, status ∈ in-flight phases | **In-flight provisioning** (existing phase progress UI, restyled) |
| Cloud exists, status = `succeeded` | **Management dashboard** (cloud identity + cost + plugin + danger zone) |
| Cloud exists, status ∈ `failed_*` | **Failure card** + "Try again" → `/clouds/new` |
| Cloud exists, status = `rolled_back` OR soft-deleted | Empty state — "Provision your cloud" (deferred copy) |

The current `/clouds/[id]` route can stay as an alias for deep links (provisioning SSE callbacks etc.), but `/` is canonical and any "back home" links go to `/`.

### `/clouds/new` — provisioning wizard (3 steps)

Step 1 — **Provider**
- Radio cards: DO (●), Hetzner (●), Azure (○ "preview — validation only", disabled)
- Each card: provider name + 1-line tagline + price hint ("from $14/mo")
- "Next" disabled until selection

Step 2 — **Region**
- Region select, grouped by continent (existing pattern, restyled)
- Filtered by provider chosen in step 1

Step 3 — **Review**
- Summary: provider, region, hostname (auto-assigned random name on `thany.click`)
- Cost breakdown (use the same `<CostSummary>` component as dashboard, fed the projected SKUs rather than accrued)
- Buttons: "Edit" (back to step 1) · "Provision" (primary)

On submit (`POST /api/clouds`), if `202`, navigate to `/`. `/` will render in-flight phase progress because the cloud row now exists with an in-flight status.

### `/settings`

Rename `/settings/security` → `/settings`. Single page, 3 sections (cards):
1. **Two-factor** — TOTP enable/disable/reset; backup codes flow as existing
2. **Passphrase** — set or "set ✓"
3. **Provider credentials** — `<ProviderTokenSection provider="digitalocean" />` (and Hetzner once wired)

### `/totp-challenge`

Restyled with the design tokens. Otherwise unchanged.

---

## Component specs

### Top bar — new component `src/lib/TopBar.svelte`

```
┌──────────────────────────────────────────────────────────────┐
│ THANY-MARCUS                                  bboiko@…  [▾] │
└──────────────────────────────────────────────────────────────┘
```

- Height 56px (`--top-bar-h`)
- 1px bottom border (`--border`)
- Wordmark in VT323 28px, links to `/`, `letter-spacing: 2px`
- Right side: avatar (or email if no avatar URL) + dropdown caret
- Dropdown contents (deferred for visual detail; structure: Settings link · Sign out form)
- Mount in `+layout.svelte` above `{@render children()}`, only when signed in

### Status pill — used for worker state and cloud status

Renders `<span class="pill {variant}"><span class="dot">{glyph}</span> {label}</span>`.

Variants: `success` (●), `warn` (◐), `idle` (○), `error` (✗). Glyphs are literal Unicode dots.

### Cloud identity card

```
┌──────────────────────────────────────────────────────────────┐
│ my-cloud-x9k2.thany.click                       [● warm]    │
│ DIGITALOCEAN · FRA1 · created 2026-04-12                     │
└──────────────────────────────────────────────────────────────┘
```

Worker pill fetched from `/healthz` on page load. If `workerState === "waking"`, start a 10s interval re-pinging until state changes or component unmounts.

### Cost card

```
┌──────────────────────────────────────────────────────────────┐
│ $12.43 this month                                            │
│ $8.14 control plane (always-on) · $4.29 worker (14.3 hrs)    │
└──────────────────────────────────────────────────────────────┘
```

Computation lives in `src/lib/costs.ts` (already exists — extend). Hourly rates table:

```ts
const HOURLY_RATES: Record<Provider, Record<string, number>> = {
  digitalocean: {
    's-2vcpu-4gb':  0.03571,   // control plane
    's-4vcpu-16gb': 0.11905,   // burst worker
  },
  hetzner: {
    'cpx21': 0.0119,
    'cpx41': 0.0524,
  },
  // azure rates when M2 wires it
};
```

Inputs to the component: `{ provider, controlPlaneSku, workerSku, provisionedAt, workerUptimeMonthSeconds }`. Output: total + breakdown lines as above. If `workerUptimeMonthSeconds` is unavailable (healthz failed), render `$Y control plane · worker stats unavailable`.

### Plugin connection card

Two visual states:

**No token issued yet:**
```
┌──────────────────────────────────────────────────────────────┐
│ PLUGIN CONNECTION                                            │
│ The Obsidian plugin uses this token to authenticate.         │
│ [ Issue plugin token ]                                       │
└──────────────────────────────────────────────────────────────┘
```

**Token issued:**
```
┌──────────────────────────────────────────────────────────────┐
│ PLUGIN CONNECTION                                            │
│ Token issued 2026-04-12                                      │
│ Cloud URL: my-cloud-x9k2.thany.click       [Copy]            │
│ [ Re-issue token ]  (revokes current)                        │
└──────────────────────────────────────────────────────────────┘
```

### Show-once token modal — new `src/lib/PluginTokenModal.svelte`

Triggered by "Issue / Re-issue plugin token." Cannot be dismissed by `Esc` or backdrop click — only by the explicit "I've saved it" button.

```
┌──────────────────────────────────────────────────────────────┐
│ NEW PLUGIN TOKEN                                             │
│                                                              │
│ ⚠ Shown once. Save it now — you cannot retrieve it later.    │
│                                                              │
│   Token:                                                     │
│   ┌──────────────────────────────────────────────────────┐   │
│   │  tm_a1b2c3d4e5f6...                          [Copy]  │   │
│   └──────────────────────────────────────────────────────┘   │
│                                                              │
│   Cloud URL:                                                 │
│   ┌──────────────────────────────────────────────────────┐   │
│   │  https://my-cloud-x9k2.thany.click           [Copy]  │   │
│   └──────────────────────────────────────────────────────┘   │
│                                                              │
│ [ Copy plugin config (JSON) ]   [ I've saved it ]            │
└──────────────────────────────────────────────────────────────┘
```

JSON config copy emits:
```json
{ "cloudUrl": "https://my-cloud-x9k2.thany.click", "token": "tm_..." }
```

If re-issuing, prepend a confirmation gate before opening this modal: "This revokes the current token. The plugin will disconnect on next sync until you paste the new token. Continue?" — secondary "Cancel" · primary "Continue and issue."

### Onboarding checklist card

Shown on `/` when user has no cloud and missing prereqs. Card title: "GET YOUR CLOUD RUNNING".

Each row:
- Step number, title, status icon (`[●] done` / `[○] not done` / `[—] locked`)
- Click → deep link or inline expand (implementer's choice — inline expand preferred for steps 1 and 2; deep link to `/clouds/new` for step 3)

```
┌──────────────────────────────────────────────────────────────┐
│ GET YOUR CLOUD RUNNING                                       │
│                                                              │
│ [○] 1. Set a passphrase                                      │
│        Protects destructive operations like destroy and      │
│        token rotation. Asked for in-context.                 │
│                                                              │
│ [—] 2. Add a provider API token              (locked)        │
│        Needs passphrase set first.                           │
│                                                              │
│ [—] 3. Provision your cloud                  (locked)        │
│        Needs provider token saved first.                     │
└──────────────────────────────────────────────────────────────┘
```

A completed step renders with `[●] done` in `--success`, and crosses out the title (`text-decoration: line-through; color: var(--text-muted)`).

### Danger zone — on the dashboard

Bottom of the management dashboard, 48px (`--space-7`) gap above. 1px solid `--error` border.

```
┌──────────────────────────────────────────────────────────────┐
│ DANGER ZONE                                                  │
│                                                              │
│ Destroy this cloud and all data on the provider.             │
│ Your local Obsidian vault is NOT affected.                   │
│                                                              │
│                                          [ Destroy cloud ]   │
└──────────────────────────────────────────────────────────────┘
```

Click → passphrase challenge modal (reuse `StepUpModal`; the destroy endpoint already enforces step-up server-side per `portal-015-handoff.md`). On submit, the dashboard transitions to in-flight destroy phase progress (SSE on the same destroy events). On terminal `rolled_back`, page reloads to the empty state.

---

## API contract addition — `/healthz` extension

The cloud's control plane needs to expose worker telemetry the portal-knowable boundary allows.

**Endpoint:** `GET https://{cloud-hostname}/healthz` (extend existing endpoint)

**Response (200):**
```json
{
  "status": "ok",
  "version": "...",
  "workerState": "warm" | "waking" | "idle",
  "workerLastActiveAt": "2026-05-17T08:23:00Z",
  "workerUptimeMonthSeconds": 51480
}
```

- `workerState` from `WorkerLifecycleService`'s in-memory state machine
- `workerLastActiveAt` last time a job ran on the worker
- `workerUptimeMonthSeconds` accumulator reset at month boundary (UTC)

Portal calls this from the dashboard on page load via the SvelteKit `+page.ts` `load` function. Timeout 3000ms. Fail soft — render dashboard with `worker stats unavailable`.

This is a CLOUD-side change. Captured here so the UI spec is self-contained; a separate CLOUD-XXX ticket implements it.

---

## Implementation phases — suggested order

Each phase ends in a working portal. Don't try to land everything in one PR.

**Phase 1 — Design tokens + top bar shell.** Replace `app.css` with the full token spec. Add `<TopBar>` component, mount in `+layout.svelte` when signed-in. Restyle the existing sign-in landing page using new tokens (no IA change yet). **Outcome:** portal looks pixel/GCP across all 5 existing pages; no broken flows.

**Phase 2 — Dashboard route restructure.** Make `/` resolve to the dashboard (or empty state / failure / in-flight) based on the user's single cloud row. Move existing `/clouds/[id]` UI to render at `/` for the cloud-exists case; keep `/clouds/[id]` as alias. **Outcome:** `/` is now the canonical home.

**Phase 3 — Cloud identity + cost cards.** Build the management-dashboard composition: identity card with worker pill (mocked state until /healthz extension lands), cost card driven by existing `costs.ts` extended with the rates table. **Outcome:** dashboard photographs as the thesis "cost story" screenshot, minus real worker telemetry.

**Phase 4 — Plugin token card + show-once modal.** Add `<PluginTokenCard>` + `<PluginTokenModal>`. Hook to the existing token endpoints. **Outcome:** end-to-end plugin token issuance UX works.

**Phase 5 — Onboarding checklist.** Build the dependency-gated checklist; show on `/` when prereqs missing. **Outcome:** new users have a clear path from sign-in to first cloud.

**Phase 6 — Provisioning wizard rework.** Add the provider step; restyle the existing 2-step wizard to 3 steps with the new design language. After-submit navigation to `/`. **Outcome:** wizard reflects multi-provider story.

**Phase 7 — Danger zone + destroy SSE.** Build the danger-zone card; wire destroy via existing `StepUpModal` + PORTAL-015 endpoint. Render destroy SSE phases inline on `/`. **Outcome:** complete cloud lifecycle in UI.

**Phase 8 — `/healthz` extension consumed.** Once the cloud-side ticket lands, switch worker pill + cost card from mock data to live data. Add the 10s waking-poll. **Outcome:** real worker telemetry on the dashboard.

**Phase 9 — Settings restructure.** Rename `/settings/security` → `/settings`. Stack the 3 sections as cards. **Outcome:** settings page matches the new design language.

**Phase 10 — Responsive pass.** Single breakpoint at 600px. Modals as full-screen sheets on mobile. **Outcome:** portal is usable from phone.

After phase 10, return for the deferred branches in `portal_ux_locks.md`.

---

## Open items the next grilling pass should cover

(Reproduced from `portal_ux_locks.md` for convenience.)

- Step-up auth modal visual treatment
- Avatar menu specifics
- Loading states (skeleton vs spinner vs "Loading…")
- Empty state copywriting
- Provisioning failure recovery / partial-failure stranded-resource UX
- Provider token entry UX flow detail
- Sign-in error states
- Accessibility audit
