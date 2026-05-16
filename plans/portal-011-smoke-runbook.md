# PORTAL-011 manual smoke runbook

**What this runs:** the bundled PORTAL-011 + 010b + 016 demo end-to-end against real DigitalOcean, real Cloudflare DNS on `thany.click`, real Let's Encrypt. From `docker compose up` to a `succeeded` cloud reachable via HTTPS, ~5–10 min, ~6¢ DO cost.

**What this proves:** wizard UI works → saga creates a real DO droplet → Cloudflare A-record propagates → cloud-init brings up the placeholder stack → LE issues a real cert → cloud POSTs back via PORTAL-016 → saga reaches `succeeded` → user can destroy via PORTAL-015.

**When to use prod LE vs staging:** this runbook uses **prod LE**. Reasons: (a) the browser test in step 11 needs a real green padlock; (b) DEC-003's 30-min retry budget plus thesis-scale demo frequency stays well under the 50-cert-per-week limit. If you find yourself doing >10 smokes/week, swap to staging by adding `le_acme_ca = "https://acme-staging-v02.api.letsencrypt.org/directory"` to the Caddyfile.tpl in cloud-init (per PORTAL-010 handoff). The browser will show "Not secure" with a staging cert — that's the trade-off.

---

## Prerequisites (one-time setup per dev box)

### Credentials you need on hand

| Credential | Where to get it | Where it goes |
|---|---|---|
| Google OAuth client ID + secret | Google Cloud Console → APIs & Services → Credentials. Authorized redirect URI: `<ngrok-public-url>/signin-google` | `.env` file at repo root |
| DigitalOcean API token | DO Cloud → API → Generate New Token. Scope: write (drop everything except the four you need: droplets, volumes, firewalls, monitoring) | Pasted into the wizard's Provider credentials section (step 4 below) |
| Cloudflare API token | dash.cloudflare.com → My Profile → API Tokens → Create Token → "Edit zone DNS" → Zone: `thany.click` | `.env` file at repo root |
| Cloudflare Zone ID for `thany.click` | dash.cloudflare.com → `thany.click` → Overview → right sidebar → "Zone ID" | `.env` file at repo root |
| ngrok auth token (one-time per machine) | dashboard.ngrok.com → Your Authtoken | `ngrok config add-authtoken <token>` |

### Tools installed

```bash
# macOS via brew
brew install ngrok doctl  # ngrok for public URL; doctl optional for verification
brew install --cask docker  # Docker Desktop, if not installed

# Verify
docker --version           # 24+
docker compose version     # v2+
ngrok version              # 3+
dotnet --list-runtimes     # net10.0 present (for migrations)
```

### `.env` file at repo root

Create `Thany-Marcus/.env` (gitignored). Do NOT commit.

```bash
# Google OAuth (PORTAL-003)
GOOGLE_CLIENT_ID=<your-client-id>.apps.googleusercontent.com
GOOGLE_CLIENT_SECRET=<your-secret>

# Cloudflare (PORTAL-010b)
CLOUDFLARE_ZONE_ID=<thany.click zone id>
CLOUDFLARE_API_TOKEN=<cf-token-scoped-to-thany.click-DNS>

# Portal callback URL (filled in step 0.3 once ngrok is up)
PROVISIONING_PORTAL_URL=
```

The compose stack reads these. `saga-worker`'s `appsettings.json` defaults are overridden by `Provisioning__PortalUrl`, `Cloudflare__ZoneId`, `Cloudflare__ApiToken` (double-underscore = nested key in .NET config).

**Note:** the current `docker-compose.yml` doesn't yet thread these vars into `saga-worker`. You'll need to add the three `environment:` lines (sub-edit below in step 0.1).

---

## Step 0 — Stack baseline

### 0.1 — wire `.env` vars into `saga-worker`

One-time edit to `docker-compose.yml` if not already done. Under `saga-worker.environment`, add:

```yaml
    Provisioning__PortalUrl: ${PROVISIONING_PORTAL_URL}
    Cloudflare__ZoneId: ${CLOUDFLARE_ZONE_ID}
    Cloudflare__ApiToken: ${CLOUDFLARE_API_TOKEN}
```

### 0.2 — bring up postgres + run migrations

```bash
cd /Users/bboiko/Personal/Thesis/Thany-Marcus
docker compose up -d postgres
docker compose exec postgres pg_isready -U postgres -d portal_dev

# Apply migrations
dotnet ef database update --project src/ThanyMarcus.Portal.Api
```

Verify the schema includes the latest migration:

```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c "\d provisioning_jobs" | grep -E "user_id|enrollment_token"
# Should show both: user_id uuid NOT NULL, enrollment_token text
```

### 0.3 — start ngrok and capture the public URL

```bash
ngrok http 80 --log=stdout > /tmp/ngrok.log &
sleep 3
NGROK_URL=$(curl -s http://localhost:4040/api/tunnels | jq -r '.tunnels[0].public_url')
echo "ngrok URL: $NGROK_URL"
# Example: https://abc123-1-2-3-4.ngrok-free.app
```

**Update `.env`** with the ngrok URL:

```bash
sed -i.bak "s|^PROVISIONING_PORTAL_URL=.*|PROVISIONING_PORTAL_URL=$NGROK_URL|" .env
grep PROVISIONING_PORTAL_URL .env
```

**Update your Google OAuth client's authorized redirect URI** to `${NGROK_URL}/signin-google`:

1. Open [Google Cloud Console → APIs & Services → Credentials](https://console.cloud.google.com/apis/credentials).
2. Click your OAuth 2.0 Client ID for this project.
3. Under **Authorized redirect URIs**, add `${NGROK_URL}/signin-google` (paste the full URL including the ngrok subdomain — the trailing path is literally `/signin-google`, not `/signin-google/`).
4. **Save**.

Google won't accept the OAuth callback otherwise — you'll get `redirect_uri_mismatch` in step 1. **This must be redone every time the ngrok URL changes** (free ngrok rotates URLs across sessions; paid ngrok keeps a stable subdomain).

### 0.4 — bring up the rest of the stack

```bash
docker compose up -d --build
docker compose ps    # all 4 services healthy
```

Watch saga-worker logs to confirm the v2 plugin cache warm-up:

```bash
docker compose logs saga-worker | grep -E "(Plugin cache|warmed)"
# Expect: "Pre-warming terraform plugin cache (v2: correct registry sources)..."
#         "Plugin cache warmed"  (first boot)
#         "Plugin cache already warmed (v2); skipping"  (subsequent boots)
```

Sanity check terraform sees the digitalocean module offline:

```bash
docker compose exec saga-worker terraform -chdir=/app/terraform-modules/digitalocean init -backend=false -no-color
# Expect: "Terraform has been successfully initialized!" with no provider download
```

### 0.5 — confirm the SPA loads through ngrok

```bash
open $NGROK_URL   # or paste into browser
```

You should see the landing page with **"Sign in with Google"**. If you hit ngrok's interstitial warning page, click "Visit Site" — ngrok free shows this once per session.

---

## Step 1 — Sign in with Google

In the browser:
1. Click **Sign in with Google**.
2. Google OAuth flow. Pick your test account.
3. After redirect you should be back on the landing page, signed in, showing "Signed in as <your name>" + a **Create cloud** button + a "Sign out" form.

If Google's redirect lands on a 400 page, the OAuth redirect URI in Google Cloud Console doesn't match `${NGROK_URL}/signin-google`. Fix it there and reload.

**Verify via DB:**
```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c "SELECT id, email FROM users ORDER BY created_at DESC LIMIT 1;"
```

---

## Step 2 — Enable TOTP

1. Navigate to `${NGROK_URL}/settings/security`.
2. Click **Enable TOTP**.
3. Scan QR with your authenticator (or copy the secret).
4. Enter the 6-digit code → **Verify and enable**.
5. **Save the 8 backup codes shown.** They render once. Click **I've saved them**.

Page reloads. TOTP state: `verified`.

---

## Step 3 — Set passphrase

Same page (`settings/security`). Below the Set Passphrase section:
1. Pick a passphrase. **Min 12 chars; don't lose it** — destruct/rotate paths need it.
2. Confirm. **Set passphrase**.

Status flips to "Passphrase: set."

---

## Step 4 — Save DO API token

Still on `settings/security`. Scroll to **Provider credentials**. DigitalOcean row:

1. Paste the DO token (`dop_v1_...`) into the password input.
2. Click **Save**.
3. **Step-up modal pops** — enter your passphrase. **Unlock**.
4. Modal closes, row flips to "Saved · created \<now>". Buttons: [Rotate] [Remove].

**Verify via DB** (token should be encrypted at rest):
```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c \
  "SELECT provider, length(ciphertext) AS ct_len, created_at FROM encrypted_provider_tokens;"
# Expect one row: provider=digitalocean, ct_len>0
```

The unlock cache TTL is sliding 10 min. The next infra-op within that window won't prompt.

---

## Step 5 — Launch the wizard

1. Navigate to `${NGROK_URL}/` (or click any "back to home" link).
2. Click **Create cloud**.
3. Lands on `/clouds/new`. Target step renders.

Region picker should be populated (10 DO regions grouped by continent). Wait for it to load (~100ms).

---

## Step 6 — Pick region + review

1. Region dropdown → **Frankfurt** (`fra1`) — closest to most EU thesis defenders, lowest CF→cloud latency.
2. Click **Next**.
3. Review step renders:
   - Region: Frankfurt
   - Hostname: "Will be assigned when you provision"
   - Cost block: headline **~$28–32/mo**, breakdown $24/mo control plane + $0.125/hr worker
   - Cold-start callout
   - "No commitment" line

---

## Step 7 — Provision

Click **Provision**.

- If passphrase was set < 10 min ago: 202 fires immediately, browser navigates to `/clouds/<uuid>`.
- If > 10 min: step-up modal pops; enter passphrase; modal closes; same navigation.

**Within 1–2s** you should see:
- URL bar: `/clouds/<some-uuid>`
- Phase tracker showing `tf_planning` in progress
- Hostname shown: `<random8>.thany.click`

**Verify via DB:**
```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c "
  SELECT c.id, c.hostname, c.provisioning_status, j.kind, j.status, j.enrollment_token IS NOT NULL AS has_token
  FROM clouds c
  JOIN provisioning_jobs j ON j.cloud_id = c.id
  ORDER BY c.created_at DESC LIMIT 1;"
```

---

## Step 8 — Watch terraform apply happen

Phase tracker advances: `tf_planning → tf_applying`. The latter is the slow one — ~60–120s for DO to create droplet + volume + firewall + attach.

**Verify in DO console:** dashboard → Droplets. A new droplet `thany-<short-uuid>` appears in `fra1` with size `s-2vcpu-4gb`. Status: New → Active.

**Or via doctl** (if installed and auth'd):
```bash
doctl compute droplet list --format ID,Name,PublicIPv4,Status,Region
```

**Saga-worker log tail** (in another terminal):
```bash
docker compose logs -f saga-worker | grep -v -E "(otel|telemetry|http\.|Activity)"
```

You'll see terraform output streaming through. On apply success, the handler transitions to `dns_creating`.

---

## Step 9 — DNS + cloud-init

Phase tracker: `dns_creating → awaiting_cloud_callback`.

`dns_creating` hits Cloudflare's API to create the A record. ~1–3s.

**Verify Cloudflare A record:**
```bash
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$CLOUDFLARE_ZONE_ID/dns_records?type=A&name=$HOSTNAME.thany.click" \
  | jq '.result[] | {name, content, ttl}'
# Expect: { name: "abc12345.thany.click", content: "<droplet IP>", ttl: 1 }
```
(Substitute `$HOSTNAME` with the random8 from step 7.)

**Verify DNS resolves externally** (~30–60s for propagation):
```bash
dig +short <random8>.thany.click @1.1.1.1
# Expect: <droplet IP>
```

Once cloud-init runs (Docker install + image pulls + Caddy boot, ~2–3 min), Caddy attempts LE HTTP-01 challenge. The cert-watcher sidecar flips `cert_ready: true` in `/admin/health` once the cert lands. Then `register-with-portal.sh` POSTs to `${PORTAL_CALLBACK_URL}`.

**Saga-worker should see the callback hit PORTAL-016:**
```bash
docker compose logs saga-worker | grep -E "(callback|register)" | tail -5
```

---

## Step 10 — Saga reaches `succeeded`

Phase tracker: `awaiting_cloud_callback → cloud_registered → awaiting_cert → succeeded`.

The transition from `cloud_registered` to `awaiting_cert` is PORTAL-016's flip. From `awaiting_cert` to `succeeded` is `AwaitingCertHandler`'s poll seeing `cert_ready: true` on `<random8>.thany.click/admin/health`.

**SSE channel emits `cloud_ready`** → SPA shows success card with hostname + IP.

**Verify via DB:**
```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c "
  SELECT c.hostname, c.provisioning_status, c.succeeded_at, c.cloud_admin_token IS NOT NULL AS has_admin_token,
         j.status AS job_status
  FROM clouds c
  JOIN provisioning_jobs j ON j.cloud_id = c.id
  ORDER BY c.created_at DESC LIMIT 1;"
# Expect: provisioning_status='succeeded', succeeded_at NOT NULL, has_admin_token=true, job_status='succeeded'
```

---

## Step 11 — Browser-verify HTTPS

In the browser:
```
https://<random8>.thany.click/admin/health
```

You should see:
- Green padlock (real LE cert, no warnings)
- JSON response: `{"cert_ready": true, "cloud_id": "<uuid>"}`

If the cert is invalid: check `docker compose logs caddy` *on the user's cloud* (you'd need SSH — out of scope for thesis; alternative is to wait the 30-min LE retry window).

---

## Step 12 — Destroy via PORTAL-015

The SPA doesn't have a Destroy button yet (PORTAL-012 territory). Drive it via curl from the host:

```bash
# Get cloudId from DB
CLOUD_ID=$(docker compose exec -T postgres psql -U postgres -d portal_dev -tA -c \
  "SELECT id FROM clouds WHERE destroyed_at IS NULL ORDER BY created_at DESC LIMIT 1;")
HOSTNAME=$(docker compose exec -T postgres psql -U postgres -d portal_dev -tA -c \
  "SELECT hostname FROM clouds WHERE id='$CLOUD_ID';")

# Grab the session cookie. Easiest: copy from browser devtools (cookie name: .AspNetCore.Cookies)
COOKIE='YOUR_COOKIE_HERE'

# Unlock (step-up) — passphrase
curl -s -X POST "$NGROK_URL/api/auth/unlock" \
  -H "Cookie: .AspNetCore.Cookies=$COOKIE" \
  -H "content-type: application/json" \
  -d "{\"passphrase\":\"YOUR_PASSPHRASE\"}"
# Expect: 204

# Destroy
curl -s -X POST "$NGROK_URL/api/clouds/$CLOUD_ID/destroy" \
  -H "Cookie: .AspNetCore.Cookies=$COOKIE" \
  -H "content-type: application/json" \
  -d "{\"confirmHostname\":\"$HOSTNAME\"}"
# Expect: 202 { jobId, cloudId }
```

Watch the destroy saga:

```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c "
  SELECT id, kind, status, updated_at FROM provisioning_jobs
  WHERE cloud_id='$CLOUD_ID' ORDER BY created_at DESC;"
# destroy job walks: destroying → rolling_back_dns → rolling_back_tf → rolled_back
```

**Verify in DO console:** droplet vanishes within ~60s.

**Verify Cloudflare A record gone:**
```bash
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$CLOUDFLARE_ZONE_ID/dns_records?name=$HOSTNAME"
# Expect: empty result array
```

**Verify cloud row soft-deleted:**
```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c "
  SELECT hostname, destroyed_at FROM clouds WHERE id='$CLOUD_ID';"
# destroyed_at NOT NULL
```

---

## Step 13 — Stack teardown (optional)

```bash
docker compose down            # stops services, keeps volumes
# OR
docker compose down -v         # nukes volumes (postgres, terraform state, caddy data)
```

Stop ngrok:
```bash
kill %1   # or pkill ngrok
```

---

## Troubleshooting

### Wizard 401 on Provision after Unlock modal

The cookie domain may not match ngrok's. Check `Set-Cookie` on `/signin-google` response — `Path=/` is required. If you see `Domain=` with a different value, fix the Cookie configuration in Portal.Api Program.cs (PORTAL-003 area).

### Saga stuck at `tf_planning` indefinitely

Check the unlock cache hasn't expired:
```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c \
  "SELECT user_id, expires_at FROM step_up_unlocks;"
```
If empty or expired and the user already navigated away, the `TfPlanningHandler` can't decrypt the DO token. The saga will land in `failed_tf` with `error: unlock_required`.

### Saga stuck at `awaiting_cloud_callback` → timeout

Most common: ngrok URL is stale or unreachable from the droplet. Test from your dev machine first:
```bash
curl -v "$NGROK_URL/health/ready"   # should 200
```
Then from inside the droplet (need SSH or DO web console). If the droplet can't resolve/reach the ngrok URL, the cloud-init's `register-with-portal.sh` will exhaust retries and the saga will time out.

Less common: `ENROLLMENT_TOKEN` mismatch. Check `events_log`:
```bash
docker compose exec -T postgres psql -U postgres -d portal_dev -c \
  "SELECT jsonb_pretty(events_log) FROM provisioning_jobs WHERE cloud_id='$CLOUD_ID';"
```

### Saga stuck at `awaiting_cert` → timeout

Caddy on the cloud isn't acquiring LE. Two usual causes:
1. **DNS hasn't propagated to LE's resolvers.** Wait. Check from a non-Cloudflare resolver:
   ```bash
   dig +short <random8>.thany.click @8.8.8.8
   dig +short <random8>.thany.click @9.9.9.9
   ```
   All should return the droplet IP. If only some resolvers see it, give it another minute.
2. **LE rate-limited.** Check Cloudflare's analytics for `<random8>.thany.click`. If you've hit the rate limit (50 certs per domain per week), switch to staging — see the top of this runbook.

### `dotnet ef database update` fails

Make sure postgres is up first and the connection string matches. The compose stack's DB is `portal_dev` on `localhost:5432` (mapped) or `postgres:5432` (in-network).

If running migrations from the host:
```bash
DB_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=portal_dev" \
  dotnet ef database update --project src/ThanyMarcus.Portal.Api \
  --connection "$DB_CONNECTION"
```

(Add `5432:5432` to the postgres service's compose ports if not already exposed.)

### Cleanup orphan resources (after a failed smoke)

```bash
# Cloudflare: list and delete any thany-prefixed A records
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$CLOUDFLARE_ZONE_ID/dns_records?type=A" \
  | jq '.result[] | select(.name | test("^[a-f0-9]{8}\\.thany\\.click$")) | {id, name, content}'

# Delete by id:
curl -X DELETE -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$CLOUDFLARE_ZONE_ID/dns_records/<id>"

# DigitalOcean: list droplets with the thany prefix
doctl compute droplet list --format ID,Name,Status | grep '^.*thany-'
doctl compute droplet delete <id> --force

# Postgres: list non-terminal jobs (force-cleanup if smoke crashed mid-saga)
docker compose exec -T postgres psql -U postgres -d portal_dev -c "
  SELECT id, cloud_id, status, created_at FROM provisioning_jobs
  WHERE status NOT IN ('succeeded','rolled_back','failed_tf','failed_dns','failed_callback','failed_cert','failed_destroy');"
```

---

## Cost watch

- Successful smoke (droplet up ~10 min): **~$0.02 DO + 1 CF API call (free)**.
- Stuck-at-cert smoke (waits the 30-min LE budget then rolls back): **~$0.06**.
- Worst case (auto-rollback fails, droplet survives): up to **$84/mo prorated until you destroy manually**. Use the cleanup section above.

Tag every resource `managed-by-portal` in PORTAL-008's module → bulk delete with `doctl compute droplet delete --tag managed-by-portal` in extremis.

---

## What this runbook does NOT cover

- Plugin-token issuance (PORTAL-013) — there's no UI yet; cloud is `succeeded` and reachable but no plugin can authenticate against it.
- Cloud-side data plane (CLOUD-001..006) — `/admin/health` works (placeholder nginx); `/api/*` returns 404 from Caddy's catch-all.
- Multi-cloud (PORTAL-009 Azure) — wizard only offers DO.
- Cloud-list dashboard (PORTAL-012) — `/` only has a "Create cloud" button; no listing of past clouds.
- Production deployment of the portal itself (PORTAL-017) — runbook uses ngrok against localhost.

These are deliberate scope cuts per the 011-bundle handoff.
