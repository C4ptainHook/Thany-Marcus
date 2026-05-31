# DEMO-PUSH-NOTIFICATIONS — Terminal-state push notifications, plugin + portal

**Goal:** desktop notifications when a long-running operation hits a terminal state, so the user (and demo audience) doesn't have to keep an eye on the queue or the provisioning panel. Plugin sends an OS notification when an ingest reaches `ready` / `failed` / `cancelled`; Portal sends a browser notification when a cloud provisioning reaches `succeeded` / `failed_*`. Zero new infrastructure (no Web Push, no service worker, no VAPID). Estimated **~1 person-day** total.

## Why this exists

Two demo embarrassments today:

1. **Cloud provisioning takes 5-15 minutes.** The Portal tile updates in-place, but the user has to keep the tab in focus and watch. If they switch windows to grab a slide, they miss the completion.
2. **Note synthesis takes 1-3 minutes.** The plugin queue updates row status, but the user often switches back to writing/reading. A `ready` note that quietly drops into the vault feels like "where did that come from?"

Both are solved by an OS-level notification at the terminal state. Both surfaces (Obsidian/Electron and browser/web) have native APIs for this — no server-side push infra required. The user keeps the tab/app open during the demo flow, so we don't need full Web Push.

## Scope

**In scope:**
- Plugin: Electron `Notification` (preferred for desktop OS visibility) with Obsidian `Notice` fallback for in-app context.
- Plugin notification triggers: SSE events resolving to `ready`, `failed_*`, or `cancelled`.
- Portal: browser `Notification` API (the simple flavour, not Web Push).
- Portal notification triggers: provisioning saga reaching `succeeded`, `failed_tf`, `failed_dns`, `failed_callback`, `failed_cert`, `failed_destroy`.
- Lazy permission UX: prompt the first time a user *initiates* the relevant action (first note submit → ask permission for plugin notifications; first provisioning start → ask permission for Portal notifications).
- Click-to-focus: plugin notification clicks open the produced note in Obsidian; Portal notification clicks bring the tab to front and scroll to the just-finished cloud tile.
- Settings toggle: a single checkbox per surface ("Show desktop notifications when a note finishes") — defaults to enabled after first permission grant.
- No sound (use the OS's silent default). Notification body wording stays factual and brief.

**Out of scope:**
- Full Web Push with service workers + VAPID + push subscription endpoint. The "works with browser closed" requirement isn't justified for a thesis demo.
- Mobile push (no mobile target yet).
- Phase-by-phase progress notifications. Only terminal states fire.
- Custom notification icons / branding. Use whatever default the OS / Obsidian / browser provides.
- Notification batching ("3 notes finished") — single notification per terminal state. Re-evaluate if demo flow produces obvious spam.

## The contract

| Trigger | Notification title | Body | On click |
|---|---|---|---|
| Plugin: note → `ready` | "Note ready" | First H1 of the synthesised note, ≤60 chars | Focus Obsidian, open the note at its vault path |
| Plugin: note → `failed_*` | "Note failed" | Phase that failed + short reason | Focus Obsidian, open the queue sidebar |
| Plugin: note → `cancelled` | "Note cancelled" | "Your draft has been restored to the composer" | Focus Obsidian |
| Portal: cloud → `succeeded` | "Cloud ready" | Hostname (`14b...thany.click`) | Focus the Portal tab, scroll to that tile |
| Portal: cloud → `failed_*` | "Cloud provisioning failed" | Stage + last_error (truncated to 100 chars) | Focus Portal tab, scroll to tile |

## Concrete files

### Plugin

#### `plugin/thany-marcus/src/notifications/DesktopNotifier.ts` — NEW

```ts
export interface DesktopNotifier {
    requestPermission(): Promise<boolean>;
    notify(opts: { title: string; body: string; onClick?: () => void }): void;
    isEnabled(): boolean;
}

export class ElectronDesktopNotifier implements DesktopNotifier {
    private permission: NotificationPermission = "default";

    isEnabled(): boolean {
        return this.permission === "granted";
    }

    async requestPermission(): Promise<boolean> {
        // Renderer-side Notification API works in Electron too.
        // Permission persists across sessions.
        if (this.permission === "granted") return true;
        const result = await Notification.requestPermission();
        this.permission = result;
        return result === "granted";
    }

    notify({ title, body, onClick }: { title: string; body: string; onClick?: () => void }): void {
        if (!this.isEnabled()) return;
        const n = new Notification(title, { body, silent: true });
        if (onClick) n.onclick = () => { window.focus(); onClick(); };
    }
}
```

#### `plugin/thany-marcus/src/queue/QueueStore.ts` — EDIT

In the reducer that handles SSE events, when the row transitions to a terminal status, call `notifier.notify(...)`:

```ts
private handleEvent(ev: SseEvent): void {
    const prev = this.byId.get(ev.noteId);
    // ... existing reducer logic that updates the row ...
    const next = this.byId.get(ev.noteId)!;

    if (prev && !isTerminal(prev.status) && isTerminal(next.status)) {
        this.notifier.notify({
            title: titleFor(next.status),
            body: bodyFor(next),
            onClick: next.vaultPath ? () => this.openNoteByPath(next.vaultPath!) : undefined,
        });
    }
    this.notify();
}
```

The `notifier` is injected via the store's constructor (passed down from `main.ts`).

#### `plugin/thany-marcus/src/draft/Submitter.ts` — EDIT

After the first successful submit, opportunistically request notification permission (one-shot, fire-and-forget):

```ts
async submit(state: DraftState, file: TFile): Promise<{ noteId: string }> {
    const result = await this.api.ingestInit(...);
    void this.notifier.requestPermission(); // non-blocking; user can dismiss
    return result;
}
```

#### `plugin/thany-marcus/src/main.ts` — EDIT

- Construct `ElectronDesktopNotifier`
- Pass it into `QueueStore`, `Submitter`
- Read the enabled toggle from plugin settings; if disabled, wrap notifier in a no-op shim

#### `plugin/thany-marcus/src/settings/SettingsTab.ts` — EDIT (or create)

Add one checkbox: **"Show desktop notifications when a note finishes"** (default: true). Persists to plugin settings JSON.

### Portal

#### `src/ThanyMarcus.Portal.Web/src/lib/notifications/browserNotifier.ts` — NEW

```ts
let permission: NotificationPermission = typeof Notification !== "undefined"
    ? Notification.permission
    : "denied";

export async function requestPermission(): Promise<boolean> {
    if (typeof Notification === "undefined") return false;
    if (permission === "granted") return true;
    const result = await Notification.requestPermission();
    permission = result;
    return result === "granted";
}

export function isEnabled(): boolean {
    return permission === "granted";
}

export function notify(opts: { title: string; body: string; onClick?: () => void }): void {
    if (!isEnabled()) return;
    const n = new Notification(opts.title, { body: opts.body, silent: true });
    if (opts.onClick) n.onclick = () => { window.focus(); opts.onClick!(); };
}
```

#### Portal SSE consumer (provisioning page) — EDIT

The page (likely `routes/clouds/+page.svelte` or wherever the provisioning tile lives) subscribes to the saga-events SSE stream. When the cloud's `provisioning_status` flips to a terminal state, call `browserNotifier.notify(...)`.

Detection logic:
- Keep `previousStatus` per cloud id
- On each SSE update, compare prev vs current
- If `prev` was non-terminal and `current` is terminal → notify

On-click: `window.focus()` + scroll the matching cloud tile into view (use existing `data-cloud-id` selector + `scrollIntoView({ behavior: "smooth" })`).

#### Portal: "first provisioning" permission prompt

In the "+ New Cloud" submit handler (wherever the user kicks off provisioning), call `void browserNotifier.requestPermission()` non-blocking. The browser shows the permission prompt; user decides.

#### Settings (optional for this ticket)

Portal already has user-account settings. Add a single toggle "Notify me when a cloud finishes provisioning" — defaults to true after permission grant. Persist server-side or in localStorage (localStorage is enough; cloud provisioning is rare).

## Tests

### Plugin
- `DesktopNotifier.test.ts`: `notify()` is a no-op when permission denied; `notify()` calls `new Notification(...)` with expected args when granted; `requestPermission()` updates internal state from `"default"` to `"granted"`.
- `QueueStore.test.ts` (extension): submitting a row with status `composing` then transitioning to `ready` triggers `notifier.notify(...)` once. Going from `ready` to `ready` (no-op event) does NOT re-fire. Going from `failed` to `failed` (no-op) does NOT re-fire.
- Manual smoke: actual OS notification appears.

### Portal
- `browserNotifier.test.ts`: same pattern as plugin — no-op when denied, calls API when granted.
- Component test for the provisioning page: terminal-status transition fires `notify`. Mid-flow update does not. Subsequent SSE events for the same already-terminal cloud don't re-fire.
- Manual smoke: actual browser OS notification appears (Chrome + Firefox + Safari at minimum).

## Permission UX flow

1. **First-time user, first note submit:**
   - Notification permission prompt appears (browser/OS dialog)
   - User clicks Allow → notifications enabled going forward
   - User clicks Block → notifier becomes no-op; the in-app `Notice` toast still fires as fallback
2. **First-time user, first provisioning kickoff:**
   - Same flow as above, but in the Portal tab
3. **Returning user:**
   - Permission state is remembered by the OS/browser
   - No prompt
   - Notifications fire as configured

## Done = ?

1. Plugin: submit a note. While it's processing, switch to another app. When the note hits `ready`, a desktop notification appears with the note's title. Click it → Obsidian comes to front, the produced note opens.
2. Plugin: submit a note that fails (e.g. provoke a vision error). When it hits `failed_*`, a notification appears with the failure reason. Click it → Obsidian focuses, queue sidebar visible.
3. Plugin: disable the setting toggle. Submit a note → no desktop notification when it finishes (in-app `Notice` still fires).
4. Portal: kick off a new cloud provisioning. Switch tabs. When the cloud hits `succeeded`, a browser notification appears with the hostname. Click → Portal tab focused, the tile is visible.
5. Portal: kick off provisioning that fails (e.g. revoke the DO token mid-apply). Browser notification fires for the `failed_tf` state.
6. Block notification permission in browser. Provisioning still works; no notification (graceful no-op).

## Risks / open questions

- **Notification spam in rapid demos.** If the demo presenter submits 5 notes in a row to show throughput, 5 notifications stack up. v1 accepts this. If observed as a problem, add a 30-second debounce window: "if N completions in 30s, show one summary notification."
- **Browser Notification API quirks.** Safari has stricter permission semantics than Chrome/Firefox; sometimes requires user gesture proximity. The "request on first submit" flow satisfies this — submit is a user gesture.
- **Electron Notification API in Obsidian.** Obsidian's Electron version is recent enough to expose the browser-style `Notification` API in the renderer process. If it doesn't (unlikely), fall back to the `electron.remote.Notification` (deprecated) path or just use the Obsidian `Notice` as the only surface.
- **Lock-screen / DND interaction.** Notifications respect the OS's Do Not Disturb. If the user is in DND, the notification queues silently — desired behaviour.
- **Click-to-focus reliability.** Some OS/browser combinations don't bring the tab/app to front on click. The notification body alone is informative; focus is a nice-to-have, not a contract.
