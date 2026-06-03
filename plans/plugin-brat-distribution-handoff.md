# PLUGIN-BRAT — Public distribution via BRAT + portal one-click setup

**Goal:** make the Thany-Marcus Obsidian plugin publicly installable via BRAT (the community-standard pre-store distribution channel), and add a portal "Install" page that hands users a one-click `obsidian://` URI to auto-configure their portal URL + token. Eliminates manual token-paste, gives every new user a 60-second onboarding from "I just provisioned my cloud" to "the plugin is running in my vault." Estimated **1 person-day** with AI-agent assistance.

## Why this exists

Today the plugin is built and copied into a local demo vault by hand (task #8 in the recent task history). There is no public install path. For a bachelor's thesis demonstrating a complete personal-cloud → plugin → synthesis pipeline, the "how does anyone other than the author install this" question is real, and the answer must hold up under defense scrutiny.

BRAT (Beta Reviewers Auto-update Tool by TfTHacker) is the de-facto standard for Obsidian plugins outside the official Community Plugins store. It is itself a community-store plugin with thousands of users; once installed, it auto-updates any registered plugin from its GitHub releases on Obsidian startup. Smart Connections, Templater, Obsidian-Git, and most popular plugins lived on BRAT before (and sometimes instead of) the official store. Distributing via BRAT is not a hobby compromise — it is the recognized community-standard channel, and the thesis claim of "publicly distributable plugin" is fully supportable through it.

The official Community Plugins store is a parallel, slower path (2–4 week review queue). It can be pursued independently and lands when it lands; the thesis is not blocked on it. This ticket sets up BRAT today and leaves the store-submission door open as a follow-on.

The portal-side "Install" page solves a real onboarding friction that even BRAT doesn't address: after a user provisions their cloud and installs the plugin, they still have to copy a portal URL and a long-lived plugin token from the portal browser tab into the plugin's settings panel. That manual paste is the single ugliest step in onboarding. Registering an `obsidian://thany-marcus/configure` URI handler in the plugin and emitting a pre-populated link from the portal collapses it to one click.

## Scope

**In scope:**
- New public GitHub repository for the plugin (`boiko/thany-marcus-obsidian`), seeded from the current `plugin/thany-marcus/` source tree.
- Repository contents: source, `manifest.json`, `versions.json`, `LICENSE` (MIT), `README.md` with BRAT install instructions and a clear "requires a Thany-Marcus cloud" notice.
- GitHub Actions release workflow that triggers on `v*` tags, builds the plugin, and attaches `manifest.json` + `main.js` + `styles.css` to a GitHub Release with the matching version.
- Version-consistency check in the workflow: tag → `manifest.json.version` must match; fail the workflow otherwise.
- First tagged release `v0.1.0` with verified BRAT-installability in a fresh vault.
- `obsidian://thany-marcus/configure?token=...&portalUrl=...&portalDeviceCode=...` URI handler in the plugin's `onload`, which writes the params into plugin settings and shows a "Connected to <portalUrl>" toast.
- Portal route `/install` (SvelteKit) shown after cloud provisioning completes. Three sections: BRAT install, BRAT-register-plugin, auto-configure button. The auto-configure button is an `<a href="obsidian://thany-marcus/configure?...">`.
- Token strategy for the URI: portal mints a short-lived (5-minute) one-shot token specifically for the configure flow; the plugin exchanges it for the persistent plugin bearer on first connect. Avoids putting long-lived tokens into clickable URIs that may end up in browser history.
- Smoke test: clean vault + fresh portal account → install BRAT → register plugin via BRAT → click portal's auto-configure button → verify the plugin connects without manual paste.

**Out of scope:**
- Submission to the official Obsidian Community Plugins store. Separate follow-on (`PLUGIN-STORE-SUBMISSION`); requires its own PR to `obsidianmd/obsidian-releases` and a 2–4 week review queue.
- A custom native installer (Avalonia / Tauri / Electron). Considered and explicitly rejected: ~2 days of work, requires code-signing recurring costs, creates an update path competitor to BRAT. Reconsiderable if BRAT proves insufficient in practice.
- Mobile support claims. `manifest.json` keeps `isDesktopOnly: true` for v1; mobile is a separate ticket (`PLUGIN-MOBILE`) that requires actual on-device verification before flipping the flag.
- Auto-update of plugin token (rotation, expiry handling). The token written by the URI handler is the persistent bearer; rotation is a follow-on.
- Repository mirroring between the thesis monorepo and the new public repo. Decision below: split the plugin out cleanly rather than mirror. The plugin is reasonably self-contained and isn't earning anything by living in the monorepo.

## State machine semantics

None on the plugin side. URI-handler is a single synchronous write of settings + a connect attempt; success or failure is reflected immediately in the settings panel.

On the portal side, the install page is rendered statefully against the user's provisioned cloud (must have a cloud, must have a valid session). One-shot token minting is a single endpoint call with a 5-minute TTL; expired tokens fail closed with a clear error.

## Concrete files

### New public repo `boiko/thany-marcus-obsidian` (split from monorepo)

Mirror the contents of `plugin/thany-marcus/` minus `node_modules/` and `main.js` (the built artifact ships only in releases, not in the repo):

```
.github/workflows/release.yml
src/
  ... (current plugin source unchanged)
esbuild.config.mjs
manifest.json
versions.json
package.json
pnpm-lock.yaml
tsconfig.json
vitest.config.ts
styles.css
LICENSE              ← NEW
README.md            ← REWRITE for public audience
.gitignore           ← node_modules, main.js, dist/
```

### `LICENSE` — NEW

MIT. Standard short-form. `Copyright (c) 2026 Bohdan Boiko`.

### `README.md` — REWRITE

Sections:
1. **What this is** — one paragraph: Obsidian plugin that captures composite notes and syncs synthesized results from a user-owned Thany-Marcus cloud. Built as part of a bachelor's thesis.
2. **What you need** — a provisioned Thany-Marcus cloud (link to the portal). Make this prominent; users without a cloud cannot use the plugin and should not install it.
3. **Install via BRAT** — three numbered steps:
   - Install BRAT from the Obsidian Community Plugins store
   - In BRAT settings, add beta plugin → paste `boiko/thany-marcus-obsidian`
   - Enable "Thany-Marcus" in Community Plugins
4. **Configure** — either click the auto-configure link from your portal's `/install` page, or paste your portal URL + plugin token manually in plugin settings.
5. **Status: thesis-stage** — explicit note that this is bachelor's-thesis work, not a production-supported product.
6. **License** — MIT.

### `.github/workflows/release.yml` — NEW

```yaml
name: Release
on:
  push:
    tags: ['v*']

jobs:
  release:
    runs-on: ubuntu-latest
    permissions:
      contents: write
    steps:
      - uses: actions/checkout@v4

      - uses: pnpm/action-setup@v3
        with:
          version: 9

      - uses: actions/setup-node@v4
        with:
          node-version: 20
          cache: pnpm

      - name: Install
        run: pnpm install --frozen-lockfile

      - name: Verify version matches tag
        run: |
          TAG_VERSION="${GITHUB_REF_NAME#v}"
          MANIFEST_VERSION=$(jq -r .version manifest.json)
          if [ "$TAG_VERSION" != "$MANIFEST_VERSION" ]; then
            echo "Tag $GITHUB_REF_NAME does not match manifest.json version $MANIFEST_VERSION"
            exit 1
          fi

      - name: Build
        run: pnpm run build

      - name: Verify build artifacts
        run: |
          test -f main.js || (echo "main.js missing"; exit 1)
          test -f styles.css || (echo "styles.css missing"; exit 1)
          test -f manifest.json || (echo "manifest.json missing"; exit 1)

      - name: Create Release
        uses: softprops/action-gh-release@v2
        with:
          files: |
            manifest.json
            main.js
            styles.css
          generate_release_notes: true
```

Use `pnpm` because the existing repo uses `pnpm-lock.yaml`. The version-match step is the load-bearing one — without it, a release with `v0.1.1` tag but a stale `manifest.json` ships broken to every BRAT user.

### `plugin/thany-marcus/manifest.json` — small update

Current state:
```json
"authorUrl": "https://github.com/",
```

Update to the real repo URL once the split is done: `https://github.com/boiko/thany-marcus-obsidian`. Empty/placeholder author URLs will be flagged by reviewers if you ever submit to the store and look unfinished to BRAT users today.

Optionally add `fundingUrl` if you want it (not required for BRAT or the store).

### Plugin URI handler

#### `plugin/thany-marcus/src/main.ts` — extend `onload`

Add after existing init:

```ts
this.registerObsidianProtocolHandler('configure', async (params) => {
  const portalUrl = params['portalUrl'];
  const deviceCode = params['deviceCode'];
  if (!portalUrl || !deviceCode) {
    new Notice('Thany-Marcus: invalid configure link');
    return;
  }
  try {
    const bearer = await this.api.exchangeDeviceCode(portalUrl, deviceCode);
    this.settings.portalUrl = portalUrl;
    this.settings.bearerToken = bearer;
    await this.saveSettings();
    new Notice(`Thany-Marcus: connected to ${new URL(portalUrl).host}`);
  } catch (e) {
    new Notice(`Thany-Marcus: setup failed (${(e as Error).message})`);
  }
});
```

The exchange step (one-shot device code → persistent bearer) is the security-relevant part. Long-lived bearers should never appear in `obsidian://` URIs, which may be persisted in browser history, clipboard managers, or screenshots.

#### `plugin/thany-marcus/src/api.ts` — new method

```ts
async exchangeDeviceCode(portalUrl: string, deviceCode: string): Promise<string> {
  const res = await requestUrl({
    url: `${portalUrl}/api/plugin/configure/exchange`,
    method: 'POST',
    contentType: 'application/json',
    body: JSON.stringify({ deviceCode }),
    throw: false,
  });
  if (res.status !== 200) throw new Error(`exchange failed: HTTP ${res.status}`);
  return (res.json as { bearerToken: string }).bearerToken;
}
```

### Portal install page

#### `src/ThanyMarcus.Portal.Api/Features/Plugin/PluginConfigureEndpoints.cs` — NEW

```csharp
public static class PluginConfigureEndpoints {
    public static void MapPluginConfigureEndpoints(this IEndpointRouteBuilder app) {
        var grp = app.MapGroup("/api/plugin/configure");

        // Mint a one-shot device code (5-min TTL) for the calling user.
        // Authenticated via the portal session cookie.
        grp.MapPost("/mint", MintAsync).RequireAuthorization();

        // Exchange a device code for a persistent plugin bearer.
        // Unauthenticated — the device code itself is the proof.
        grp.MapPost("/exchange", ExchangeAsync);
    }

    private static async Task<IResult> MintAsync(...) {
        // 1. Resolve userId from session.
        // 2. Generate cryptographically random 32-byte device code, base64url.
        // 3. INSERT into plugin_device_codes (code_hash, user_id, expires_at).
        //    Store SHA-256 hash, not the raw code.
        // 4. Return { deviceCode, expiresAt }.
    }

    private static async Task<IResult> ExchangeAsync(...) {
        // 1. Hash the incoming deviceCode.
        // 2. SELECT FROM plugin_device_codes WHERE code_hash = @h AND expires_at > now()
        //    AND consumed_at IS NULL.
        // 3. If not found: 401.
        // 4. Mint a persistent plugin bearer for that user (existing flow).
        // 5. UPDATE plugin_device_codes SET consumed_at = now() WHERE code_hash = @h.
        // 6. Return { bearerToken }.
    }
}
```

One-shot semantics: `consumed_at` is the gate. Race-safe via a row-level lock + check.

#### Postgres migration `0NNN_plugin_device_codes.sql`

```sql
CREATE TABLE plugin_device_codes (
    code_hash BYTEA PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    expires_at TIMESTAMPTZ NOT NULL,
    consumed_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX plugin_device_codes_expires ON plugin_device_codes (expires_at)
    WHERE consumed_at IS NULL;
```

Add a background job that prunes rows where `expires_at < now() - interval '1 hour'`. Cheap; runs daily.

#### `src/ThanyMarcus.Portal.Web/src/routes/install/+page.svelte` — NEW

Static-ish SvelteKit page. Three sections matching the README. The auto-configure button:

```svelte
<script lang="ts">
  let deviceCode = $state<string | null>(null);
  async function mint() {
    const res = await fetch('/api/plugin/configure/mint', { method: 'POST' });
    deviceCode = (await res.json()).deviceCode;
  }
  $effect(() => { mint(); });
  const portalUrl = $derived(window.location.origin);
  const configureUri = $derived(
    deviceCode
      ? `obsidian://thany-marcus/configure?portalUrl=${encodeURIComponent(portalUrl)}&deviceCode=${encodeURIComponent(deviceCode)}`
      : null
  );
</script>

{#if configureUri}
  <a class="primary" href={configureUri}>Open in Obsidian and connect</a>
{:else}
  <button disabled>Preparing...</button>
{/if}
```

Page should re-mint when the device code expires (re-poll on focus, or on a 4-minute timer). Cheap; the mint endpoint is sub-10ms.

#### Portal IA — link from dashboard

After cloud provisioning succeeds, the dashboard should surface a one-time "Install the plugin" callout linking to `/install`. Subsequent visits show a quieter link in the user menu. Matches the dashboard-first IA decision in `portal_ux_locks`.

## Tests

Backend:
- `PluginConfigureEndpointsTests.Mint_creates_one_shot_code_with_5min_ttl`.
- `PluginConfigureEndpointsTests.Exchange_returns_bearer_for_valid_code`.
- `PluginConfigureEndpointsTests.Exchange_rejects_consumed_code` — same code used twice → second call 401.
- `PluginConfigureEndpointsTests.Exchange_rejects_expired_code`.
- `PluginConfigureEndpointsTests.Exchange_rejects_unknown_code`.
- `PluginConfigureEndpointsTests.Mint_requires_session_authentication` — unauth → 401.
- `PluginConfigureEndpointsTests.Pruner_removes_expired_codes`.

Plugin:
- `MainTests.Configure_uri_writes_settings_and_exchanges_token` — mocked `requestUrl`, assert settings updated.
- `MainTests.Configure_uri_missing_params_shows_error_notice`.
- `MainTests.Configure_uri_exchange_failure_shows_error_notice`.

Manual smoke (the load-bearing test for this ticket):
1. Cut tagged release `v0.1.0` on the new public repo.
2. Verify release page has `manifest.json` + `main.js` + `styles.css` as downloadable assets.
3. Fresh Obsidian vault.
4. Install BRAT from community store.
5. Open BRAT settings → Add beta plugin → enter `boiko/thany-marcus-obsidian` → confirm.
6. Enable Thany-Marcus in Community Plugins.
7. From a separate fresh portal account, provision a cloud.
8. Navigate to portal `/install`. Click "Open in Obsidian and connect."
9. Obsidian focuses, plugin shows "connected to <host>" notice.
10. Capture flow works (create draft → submit → synthesized note arrives).

## Risks and edge cases

- **Tag/manifest version mismatch ships broken to BRAT users immediately.** The workflow's version-match step is the only thing preventing this; treat it as load-bearing and don't disable.
- **BRAT update timing.** BRAT auto-updates on Obsidian startup, not in real time. A user with Obsidian open during a release window won't see the update until next launch. Acceptable; document it in the README's "Updates" section if it matters.
- **First impression is the release page.** If `v0.1.0` ships with a missing artifact or a misnamed file, every BRAT user who installs in that window gets a broken plugin and may not retry. Cut a `v0.0.1-test` release first, verify the workflow, then cut `v0.1.0` clean.
- **Device code in browser history.** The `obsidian://` URI lives in the user's browser address bar history. Mitigation: device codes are 5-minute one-shot, hashed at rest, consumed on first exchange. Even if the URI leaks via screenshot or history sync, it's useless after 5 minutes or after the first successful exchange.
- **Obsidian protocol handler registration.** `obsidian://` requires the OS to associate Obsidian as the URL-scheme handler. Obsidian installs this on first launch on macOS/Windows; some Linux setups may need manual `.desktop` file configuration. If the auto-configure click does nothing, fallback is the manual paste flow — instructions should be visible on `/install` as a "didn't work? configure manually" expander.
- **Public repo exposes plugin source to scrutiny.** This is intentional and expected for any community-distributed plugin. Run a once-over for hardcoded URLs, leftover debug logging, anything that names internal infrastructure. The plugin should reference the portal URL only via settings, never hardcoded.
- **Monorepo divergence after split.** The monorepo's `plugin/thany-marcus/` becomes stale once development moves to the new public repo. Options: (a) delete the monorepo copy after split, develop only in the public repo; (b) keep monorepo as the dev primary and use a `release.sh` that syncs to the public repo on tag. Recommended: (a) — single source of truth, less drift risk, simpler mental model. The split is a one-way move.
- **Long-running portal session vs short-lived device code.** If a user opens `/install`, walks away for an hour, then clicks the configure button, the device code is expired and the exchange fails. The page should re-mint on visibility-change or after a 4-minute timer (under the 5-minute TTL). Existing `$effect` with a timer handles this.

## Effort breakdown

- New public repo setup (split, README, LICENSE, gitignore, push): 1h
- GitHub Actions release workflow + first test release: 2h
- `manifest.json` cleanup + first real release `v0.1.0`: 0.5h
- BRAT smoke test in clean vault: 0.5h
- URI handler in plugin (`main.ts` + `api.ts` exchange method + tests): 2h
- Portal `/api/plugin/configure/mint` + `/exchange` endpoints + migration + tests: 2h
- Portal `/install` SvelteKit page + dashboard callout: 1.5h
- End-to-end manual smoke (provision → install → click → capture): 0.5h

Total: **~1 person-day**. MVP cut (skip the URI handler + portal install page, just publish to BRAT with manual token paste): **0.5 person-day**. The polish layer is half the value of the ticket — skip only if the thesis timeline forces it.

## Decision points to confirm before starting

1. **Public repo name.** Recommended: `boiko/thany-marcus-obsidian`. Alternatives: `obsidian-thany-marcus` (matches `obsidian-git` convention), `thany-marcus-plugin`. The `-obsidian` suffix is the most common community pattern and most discoverable.
2. **License.** Recommended: MIT (community default for Obsidian plugins). Apache 2.0 is acceptable; GPL is uncommon and would push some users away.
3. **Split vs mirror the monorepo.** Recommended: split cleanly (option a in Risks). Single source of truth, removes drift risk, the plugin is self-contained enough that the monorepo isn't doing it favors.
4. **Device-code TTL.** Recommended: 5 minutes. Long enough for the user to context-switch, short enough that a leaked URI is functionally dead before a typical attacker could act on it.
5. **Pre-release channel.** Recommended: skip for v1; cut only stable releases. BRAT supports pre-releases via the "Beta version" toggle, useful later if you want a "thesis defense build" pinned while continuing to iterate.
6. **Dashboard callout copy.** Owner picks; suggest something concrete like "Install the plugin in your Obsidian vault" with a clear CTA, not "complete your setup" or other vague nudges.

Owner picks at kickoff; ticket doesn't block on the answers.

## Follow-on tickets this unlocks

- **PLUGIN-STORE-SUBMISSION** — PR to `obsidianmd/obsidian-releases` adding the plugin to the official Community Plugins store. Mostly a packaging exercise once BRAT distribution is proven; review queue is the long pole. Independent of this ticket.
- **PLUGIN-MOBILE** — flip `isDesktopOnly: false`, add CORS origins for `capacitor://localhost` + `http://localhost`, verify each surface on iOS/Android. Worth doing only after BRAT distribution is stable, because the audience reachable on mobile is via BRAT (BRAT itself supports mobile).
- **PLUGIN-AUTO-UPDATE-IN-APP** — surface "update available" hints in the plugin itself rather than relying solely on BRAT's startup check. Useful if BRAT proves too low-discoverability for updates. Out of scope for thesis.
- **PLUGIN-DIAGNOSTICS** — settings panel "Run diagnostics" button that pings the configured portal, runs a synth round-trip on a test draft, and reports the results. Helps users diagnose their own setup without filing issues.
- **PORTAL-INSTALL-COPY-PATHS** — variants of the install page tailored to "I already have a vault" vs "I'm new to Obsidian" — the latter needs Obsidian-install steps first. Marginal until you have non-developer users.
