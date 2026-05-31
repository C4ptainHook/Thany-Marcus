# PORTAL-AUTH-RECOVERY-MODEL — 8-char passphrase, dual reset paths, Emergency Kit

**Goal:** redesign the step-up unlock + recovery flow. Daily friction is an 8-character passphrase (or a passkey, per the separate passkey ticket). Forgetting the passphrase has **two parallel reset paths** the user can pick from: TOTP (if they set it up — optional) or an Emergency Kit (mandatory single-artifact, generated once at setup). The current 10-code recovery list goes away in favour of a single document the user can grasp as one tangible thing. TOTP is **optional, never forced**. Estimated **~3 person-days**.

## Why this exists

Current model: 16+ character passphrase, no breach-list check, 10 recovery codes shown as a list. Two real demo failures lurk in there:

1. **Passphrase friction.** A 16-char minimum forces the user to memorise something complex or paste from a manager. For a personal-PKM-cloud, this is heavier than the threat model justifies. NIST 800-63B (2017+) explicitly allows 8 chars as the floor when paired with Argon2 + breach-list checks.
2. **Recovery codes that nobody saves.** The current UI surfaces a list of 10 codes with generic "save these somewhere safe" copy. In real user studies, ≥80% of users fail to save such lists. When they then forget the passphrase, the only "recovery" is a full account reset → re-OAuth DigitalOcean → lose all encrypted provider tokens.

The proposed model:

```
Daily step-up:                Passphrase (8+ chars)  OR  Passkey (if registered)

Forgot passphrase             ┌── "Use my TOTP code"    (shown only if TOTP configured)
(choose one path) ─────────── ┤
                              └── "Use my Emergency Kit" (always available — universal)

Both paths converge to:       Set new passphrase  →  show new Emergency Kit
```

Two parallel recovery paths, each independently sufficient. The user picks whichever is more convenient in the moment:
- **TOTP** is the "shortcut" if you set it up — type a fresh 6-digit code from your authenticator app.
- **Emergency Kit** is the universal fallback that every user has — type the 8-word recovery string off your printed kit.

Neither requires the other. Neither blocks setup. TOTP stays **completely optional**; the Emergency Kit is the floor that guarantees no user is ever permanently locked out.

Three secrets total, each with one clear role: passphrase (daily), TOTP (optional 2FA at login + optional fast-lane reset), Emergency Kit (universal recovery floor). The Emergency Kit replaces today's 10-code list with a single named artifact the user is much more likely to actually save.

See [[feedback_totp_login_only]] for the existing TOTP-at-login policy that this ticket preserves.

## Scope

**In scope:**
- Loosen passphrase validator: 8-char minimum (down from current), no composition rules.
- Add breach-list check at passphrase set/change: reject the top-N most common passwords (local list ~10k entries; no external HIBP call to avoid runtime dependency).
- **TOTP stays optional.** Users can enable or skip 2FA during onboarding without affecting the recovery story. The Emergency Kit guarantees recoverability regardless of TOTP choice.
- New "Forgot passphrase" page surfaces two parallel buttons:
  - **"Use my TOTP code"** — visible only if the user has TOTP configured. Verify 6-digit code → new passphrase form.
  - **"Use my Emergency Kit"** — always visible. Verify 8-word recovery string → new passphrase form.
- Both reset paths converge to the same outcome: set new passphrase, then immediately show a fresh Emergency Kit (the redeemed kit was consumed; user must save the new one before leaving).
- Replace the 10-code recovery list with a single **Emergency Kit**:
  - One word-based recovery string (8 words from a 1024-word list = 80 bits of entropy).
  - QR code containing the string for phone scan.
  - Branded PDF download with Thany-Marcus logo, account email, the string, the QR, and "Generated on {date}" footer.
  - Print, Download PDF, Copy buttons.
  - Required "I have saved or printed this kit" checkbox before the modal can close.
  - Shown **exactly once** at setup. After dismissal, the plaintext is unrecoverable server-side.
- "Generate Emergency Kit" deferred until both passphrase + TOTP are confirmed (since the Kit IS the catastrophic fallback for both).
- Settings → Danger Zone: a single small section showing "Emergency Kit · Generated 2026-05-31 · Last used: never" with a single "Regenerate" button (requires step-up; invalidates old).
- Backend: simplify `recovery_codes` table from N-rows-per-user to one-row-per-user (or repurpose: existing table works fine, just we only ever insert one row at a time and invalidate the previous).
- All four flows wired end-to-end: passphrase setup, forgot-passphrase-via-TOTP, Emergency Kit redeem, regenerate.
- Tests for each flow + the validator + the breach check.

**Out of scope:**
- Replacing passphrase entirely with passkey-only. The passphrase remains the universal floor for users who don't register a passkey. See the separate passkey handoff for the additive passkey path.
- Passphrase autocomplete via password manager integration. Browsers do this for free; no special hooks needed.
- "Did you save your Emergency Kit recently?" periodic re-prompts. Show once, trust user, allow regeneration on demand.
- Word-list localisation. English-only for v1.
- SMS / email recovery channels. Not in scope; the threat model treats those as the user's IdP problem (Google account recovery), not ours.
- Migration UI for existing users with old 10-code lists. On their next passphrase set/change, they get the new Emergency Kit; old codes are invalidated quietly.

## User flow detail

### First-time setup (after Google sign-in, during onboarding)

```
[Onboarding step N: Secure your account]

  1. Set a passphrase (8+ characters)
     • Strength indicator (weak / OK / strong)
     • Rejected: top-10000 common passwords
     • Confirm by typing again

  2. Save your Emergency Kit                               (MANDATORY — universal recovery)
     [modal opens]
     ┌─────────────────────────────────────────────┐
     │ Save your Emergency Kit                     │
     │                                             │
     │ This is the only way back if you ever lose  │
     │ access to both your passphrase and your     │
     │ 2FA device. We'll show this once.           │
     │                                             │
     │ ┌─────────────────────────────────────────┐ │
     │ │ Thany-Marcus Emergency Kit              │ │
     │ │ user@example.com                        │ │
     │ │                                         │ │
     │ │ swift river amber cloud                 │ │
     │ │ iron stone vivid 9347                   │ │
     │ │                                         │ │
     │ │ [QR code]                               │ │
     │ │                                         │ │
     │ │ Generated 2026-05-31                    │ │
     │ └─────────────────────────────────────────┘ │
     │                                             │
     │ [Print] [Download PDF] [Copy]               │
     │                                             │
     │ ☐ I have saved or printed this kit          │
     │                                             │
     │ [I've saved it (disabled until ☑)]          │
     └─────────────────────────────────────────────┘
```

After dismissal: the server has only `hash(emergency_string)` + the DEK wrapped under a key derived from the string. The plaintext is gone.

  3. Set up 2-factor authentication                        (OPTIONAL — fast-lane recovery)
     [explanation card]
     ┌─────────────────────────────────────────────┐
     │ Add 2-factor authentication                 │
     │                                             │
     │ Optional. If enabled, you get:              │
     │  • Stronger sign-in (Google + 2FA)          │
     │  • A second way to reset your passphrase    │
     │    (faster than typing your Emergency Kit)  │
     │                                             │
     │ [Set up 2FA]    [Skip for now]              │
     └─────────────────────────────────────────────┘

     User who skips: account is fully usable; Emergency Kit
     is their only passphrase-reset path until they enable
     TOTP later in Settings.

### Daily step-up

User clicks "Provision cloud" → step-up modal appears → user types 8-char passphrase OR taps passkey (per separate ticket) → DEK unlocked → action proceeds.

### Forgot passphrase (user picks one of two paths)

```
[Step-up modal]
  Enter your passphrase to continue
  [_______________]
  [Unlock]

  I forgot my passphrase →

[Clicked — recovery options page]
  How would you like to recover access?

  ┌─ Option 1 (visible only if TOTP configured) ────────┐
  │ Use my 2FA code                                     │
  │ Type a fresh 6-digit code from your authenticator   │
  │ app.                                                │
  │ [Use 2FA →]                                         │
  └─────────────────────────────────────────────────────┘

  ┌─ Option 2 (always visible) ─────────────────────────┐
  │ Use my Emergency Kit                                │
  │ Type the 8-word recovery string from your kit.      │
  │ [Use Emergency Kit →]                               │
  └─────────────────────────────────────────────────────┘

[Either option succeeds → same set-new-passphrase flow]
  Set a new passphrase
  [_______________]  (8+ chars; breach check)
  [_______________]  Confirm
  [Save new passphrase]

[After new passphrase saved → new Emergency Kit modal]
  Your old Emergency Kit has been used and is no longer valid.
  Save your new Emergency Kit before continuing.
  [...same modal as first-time setup...]
  ☐ I have saved or printed this kit
  [I've saved it (disabled until ☑)]

[Done]
  Passphrase reset complete. New Emergency Kit saved.
```

Key invariants:
- The redeemed Emergency Kit is single-use; it's invalidated the moment the new passphrase is saved, and a fresh kit is generated. The user can't proceed without acknowledging save of the new kit.
- TOTP path does NOT consume the Emergency Kit. A user who resets via TOTP keeps their existing kit valid. (Only redeeming the kit invalidates it.)
- TOTP secret is unchanged by the reset. The user re-uses their existing authenticator app.

### Settings (Danger Zone)

```
Account security
  Passphrase                 last changed 12 days ago    [Change]
  2-factor authentication    Not configured              [Set up 2FA]
                             (or)
  2-factor authentication    Configured                  [Reset]

Emergency Kit (Danger Zone)
  Generated 2026-05-31. Last used: never.                [Regenerate]
```

`Regenerate` requires step-up. Invalidates the old kit. Same modal as first-time setup.

Users without TOTP see the "Set up 2FA" CTA in account security. Clear, optional, never blocking.

## Concrete files

### Backend

#### `src/ThanyMarcus.Portal.Api/Features/Auth/PassphraseValidator.cs` — EDIT (or NEW)

```csharp
public sealed class PassphraseValidator
{
    private readonly HashSet<string> commonPasswords;
    public PassphraseValidator(IReadOnlyCollection<string> commonPasswords)
        => this.commonPasswords = new(commonPasswords, StringComparer.OrdinalIgnoreCase);

    public ValidationResult Validate(string passphrase)
    {
        if (passphrase.Length < 8) return ValidationResult.TooShort;
        if (commonPasswords.Contains(passphrase)) return ValidationResult.TooCommon;
        return ValidationResult.Ok;
    }
}
```

Load common-passwords list from an embedded resource (top-10000 from SecLists or similar; small file ~80 KB).

#### `src/ThanyMarcus.Portal.Api/Features/Auth/EmergencyKit.cs` — NEW

```csharp
public sealed class EmergencyKit
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string HashedString { get; init; } = null!;     // Argon2id hash of the recovery string

    // Same wrapping scheme as existing recovery_codes
    public byte[] WrapArgon2Salt { get; init; } = null!;
    public JsonDocument WrapArgon2Params { get; init; } = null!;
    public byte[] WrappedDek { get; init; } = null!;
    public byte[] WrapNonce { get; init; } = null!;
    public byte[] WrapTag { get; init; } = null!;

    public Instant CreatedAt { get; init; }
    public Instant? UsedAt { get; set; }      // set when the kit is redeemed
    public Instant? RevokedAt { get; set; }   // set when superseded by a regeneration
}
```

Database: reuse existing `recovery_codes` table or rename via migration to `emergency_kits`. **Recommendation**: rename — the new semantics are clearer, and we already had to clean up the multi-row-per-user assumption.

#### Migration `0NNN_emergency_kit_rename.sql`

```sql
ALTER TABLE recovery_codes RENAME TO emergency_kits;
ALTER TABLE emergency_kits RENAME COLUMN hashed_code TO hashed_string;
ALTER TABLE emergency_kits ADD COLUMN revoked_at TIMESTAMPTZ;

-- Invalidate any existing rows from the old multi-code regime — they'll be
-- regenerated as single kits next time users go through the new flow.
UPDATE emergency_kits SET revoked_at = NOW() WHERE revoked_at IS NULL;

CREATE UNIQUE INDEX idx_emergency_kits_active_per_user
    ON emergency_kits (user_id)
    WHERE used_at IS NULL AND revoked_at IS NULL;
```

#### `src/ThanyMarcus.Portal.Api/Features/Auth/WordList.cs` — NEW

A 1024-word list (10 bits per word × 8 words = 80 bits entropy per kit). Use a curated short-word list (BIP39 is the canonical option — 2048 words, each ≤8 chars, no homophones, no embarrassing words; works fine here, just emit 8 of them).

Generator:
```csharp
public static string Generate()
{
    var bytes = RandomNumberGenerator.GetBytes(10);  // 80 bits
    return string.Join(' ', Take8Words(bytes));      // "swift river amber cloud iron stone vivid coral"
}
```

Take a deterministic 8-word slice from the bytes via bit-packing. No ambiguity in parsing back: split on whitespace, look up each word's index, reconstruct.

#### `src/ThanyMarcus.Portal.Api/Features/Auth/EmergencyKitEndpoints.cs` — NEW

```csharp
group.MapPost("/generate", GenerateAsync).RequireAuthorization();
group.MapPost("/redeem",   RedeemAsync  ).AllowAnonymous();   // sign-in step-up flow allows this from a session that already has SSO

private static async Task<IResult> GenerateAsync(
    PortalDbContext db, IDekService dek, IClock clock, ClaimsPrincipal user, CancellationToken ct)
{
    var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
    // Require step-up — caller must have unlocked DEK already.
    var unwrappedDek = dek.RequireUnlockedDek(userId);

    var phrase = WordList.Generate();
    var (wrappedDek, salt, prms, nonce, tag) = WrapDek(unwrappedDek, phrase);

    // Invalidate any existing active kit.
    await db.EmergencyKits.Where(k => k.UserId == userId && k.UsedAt == null && k.RevokedAt == null)
        .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, clock.GetCurrentInstant()), ct);

    db.EmergencyKits.Add(new EmergencyKit {
        UserId = userId, HashedString = Argon2id.Hash(phrase),
        WrapArgon2Salt = salt, WrapArgon2Params = prms,
        WrappedDek = wrappedDek, WrapNonce = nonce, WrapTag = tag,
        CreatedAt = clock.GetCurrentInstant(),
    });
    await db.SaveChangesAsync(ct);

    return Results.Ok(new { recoveryString = phrase });   // RETURNED ONCE — never persisted in plaintext
}
```

`RedeemAsync` accepts the phrase, looks up by Argon2 hash match, unwraps DEK, signals success to the caller (which proceeds with set-new-passphrase flow). Marks kit `UsedAt`.

#### `src/ThanyMarcus.Portal.Api/Features/Auth/PassphraseResetViaTotpEndpoint.cs` — NEW

```csharp
app.MapPost("/api/auth/passphrase/reset-via-totp", async (
    ResetViaTotpRequest req, ClaimsPrincipal user,
    PortalDbContext db, ITotpVerifier totp, IDekService dek,
    IClock clock, CancellationToken ct) =>
{
    var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
    if (!await totp.VerifyAsync(userId, req.TotpCode, ct))
        return Results.Unauthorized();

    // Note: this path requires the user to be signed in (has Google SSO session).
    // We DON'T verify the old passphrase here — the whole point is the user forgot it.
    // We DO verify a fresh TOTP code, which proves possession of the second factor.

    var validator = new PassphraseValidator(CommonPasswords.Load());
    if (validator.Validate(req.NewPassphrase) != ValidationResult.Ok)
        return Results.BadRequest(new { error = "passphrase_invalid" });

    // The DEK was wrapped under the OLD passphrase. But we don't have the old.
    // We have to use ANOTHER way to recover the DEK — the only options are:
    //   (a) The Emergency Kit (separate flow)
    //   (b) A TOTP-wrapped copy of the DEK, stored alongside the passphrase wrap
    //
    // Implementation choice: at passphrase setup time, ALSO wrap the DEK under a
    // key derived from the user's TOTP shared secret. Store as a second blob on
    // the user row (totp_wrapped_dek). This is the "TOTP recovery wrapper" that
    // makes the reset-via-totp flow possible without storing plaintext anywhere.

    var totpSecret = await db.TotpSecrets.SingleAsync(t => t.UserId == userId, ct);
    var unwrappedDek = UnwrapDekWithTotpSecret(totpSecret, user.TotpWrappedDek);

    // Rewrap under the new passphrase.
    var (newWrapped, salt, prms, nonce, tag) = WrapDekWithPassphrase(unwrappedDek, req.NewPassphrase);
    // (and update the totp-wrapped copy too, since the DEK didn't actually change here —
    //  but rotation hygiene says we re-derive everything anyway)

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();
```

**Key design decision** noted in the code: to make TOTP-as-recovery actually work, the DEK must be wrapped under the TOTP secret in addition to the passphrase. This is a second blob stored per-user (`users.totp_wrapped_dek`). It is created when the user enables TOTP, and **only then**. Users who skip TOTP never have this blob — their sole DEK wrapper outside the passphrase is the Emergency Kit.

When the user later opts into TOTP from settings, the enable-TOTP flow does:
1. Verify current passphrase (step-up).
2. Unwrap DEK using the passphrase.
3. Re-wrap a copy of DEK under a key derived from the new TOTP secret.
4. Store as `users.totp_wrapped_dek`.

When the user disables TOTP, the column is cleared. The TOTP-reset path silently becomes unavailable; only Emergency Kit remains. The recovery-options page in the UI auto-hides the TOTP button when this column is null.

The threat model is:
- Server compromise (DB + master key) → attacker can decrypt TOTP secret → unwrap `totp_wrapped_dek` → DEK theirs. Same threat as today's recovery codes if their hashes were stored alongside plaintext-equivalent material.
- This is a worse threat model than the current "recovery codes are only hashed" approach in the abstract, but in practice the server already needs to store the TOTP secret for login verification, so it's not strictly new exposure.
- Users who care about this threat model can simply skip TOTP. Their DEK is then protected only by the passphrase (online attack: bcrypt-equivalent of the passphrase) and the Emergency Kit (hash-only, brute-force-resistant).

### Frontend

#### `src/ThanyMarcus.Portal.Web/src/lib/auth/EmergencyKitModal.svelte` — NEW

The modal described in "User flow detail" above. Branded card with the recovery string + QR code (use `qrcode` npm package, ~3 KB gzipped) + Print/Download/Copy buttons + required checkbox + disabled-until-checked "I've saved it" button.

Print uses `window.print()` with a print stylesheet scoped to the card only.

Download PDF uses `jsPDF` to render the same card layout. ~30 LoC.

#### `src/ThanyMarcus.Portal.Web/src/routes/settings/+page.svelte` — EDIT

Add the Danger Zone "Emergency Kit" row + Regenerate button.

#### `src/ThanyMarcus.Portal.Web/src/lib/auth/ForgotPassphraseModal.svelte` — NEW

The step-by-step flow: TOTP entry → new passphrase → success. Falls through to "I've also lost my 2FA device" → Emergency Kit redeem flow.

#### `src/ThanyMarcus.Portal.Web/src/lib/OnboardingChecklist.svelte` — EDIT

Onboarding: a single combined step "Secure your account" that covers passphrase → TOTP → Emergency Kit in order.

## Tests

### Backend
- `PassphraseValidatorTests`: rejects <8 chars; rejects top-1000 common; accepts a random 8-char string.
- `WordListTests`: Generate produces an 8-word string from the curated list; parsing back recovers the original bytes.
- `EmergencyKitEndpointsTests`: Generate requires unlocked DEK; previous kit invalidated when a new one is generated; Redeem unwraps DEK correctly; reused kit fails; revoked kit fails.
- `PassphraseResetViaTotpEndpointTests`: rejects wrong TOTP; accepts valid TOTP; rewraps DEK; new passphrase is usable for subsequent unlocks.
- `EmergencyKitToTotpInteropTests`: redeeming a kit during the reset flow ALSO clears any pending TOTP-required steps, since the user has now provably authenticated via the strongest available secret.

### Frontend
- `EmergencyKitModal.test.ts`: checkbox required to close; Copy/Print/Download all callable; QR renders.
- `ForgotPassphraseModal.test.ts`: TOTP step → passphrase step → success; "lost 2FA" link transitions to redeem flow.
- E2E: full setup → step-up unlocks → forgot-passphrase → reset → redeem flow.

## Migration / deployment notes

- Migration renames `recovery_codes` to `emergency_kits` + invalidates existing rows. Users who had old-style 10-code lists won't have a usable Emergency Kit until they go through "Regenerate" — surfaced as a banner on next login: "Your emergency recovery has been upgraded — generate your new Emergency Kit." Banner persists until acknowledged.
- Common-passwords list shipped as embedded resource. No external API at runtime.
- `qrcode` and `jspdf` npm packages added to the Portal SPA. Both are well-maintained, MIT-licensed, no native deps.
- After deploy: log in as a test user, go through "Regenerate Emergency Kit," verify the PDF renders correctly, simulate forgot-passphrase → reset via TOTP, then redeem the Emergency Kit on a fresh session.

## Risks / open questions

- **TOTP-wrapped DEK stores the DEK reachable via TOTP secret.** Server-side DB + master key compromise → attacker can reset passphrase. This is documented as the trade-off for TOTP-as-recovery; it's strictly weaker than the current recovery-code-hash-only model. **Decision needed: accept this trade-off?** My recommendation: yes, the UX win is worth it for thesis demo, and the threat model upgrade can be a future ticket.
- **Word list source.** BIP39 is the safest established list (well-audited, no homophones, no profanity). Slightly long license note required in attribution. Alternative: hand-curated 1024-word list specific to Thany-Marcus.
- **Word-list typos at redeem time.** A user typing the recovery string by hand may mistype a word. We can add a Levenshtein-distance-1 fuzzy match against the word list (suggests "did you mean 'amber'?"). Out of scope for v1; raw equality match only.
- **Emergency Kit visible in browser memory for the lifetime of the modal.** Unavoidable; modal owns the plaintext until the user closes it. Clear via `crypto.subtle` after the user clicks "I've saved it," and reload the page to flush the JS scope. Document this.
- **Onboarding length.** Required steps are now passphrase + Emergency Kit (2 sub-steps). TOTP is an offered card that the user can skip. Demo users who want to show off the full security model can opt in; the floor experience is light.
- **Single-path users (no TOTP) and the "only one chance" feel.** A user who skipped TOTP and forgets the passphrase has exactly one path: the Emergency Kit. If they also lost the kit, they're locked out — there's no third option. This is by design: the model trades the "hide the recovery codes in a drawer forever" pattern for "save your one Emergency Kit somewhere you can find it." The PDF/print emphasis exists precisely because the kit must actually be saved. Worth A/B-testing copy: "Save your Emergency Kit — this is your only key back to your data if you forget your passphrase."

## Done = ?

**Path A: user with TOTP enabled**

1. New user signs up via Google SSO → onboarding step "Secure your account" → set 8-char passphrase → "weak" indicator on `password` (rejected), strong on `summit-roses-galaxy-7`; see Emergency Kit modal; copy/print/PDF the recovery string; check the box; click "I've saved it"; modal closes; then card prompts "Add 2FA?" → user clicks "Set up 2FA" → scans QR → confirms 6-digit code. Onboarding done.
2. Try to provision a cloud → step-up modal asks for passphrase → unlocks → flow proceeds.
3. Sign out + back in. Step-up modal appears. Click "I forgot my passphrase" → recovery-options page shows BOTH "Use my 2FA code" AND "Use my Emergency Kit". Pick TOTP → enter 6-digit code → set new passphrase. Emergency Kit remains valid (unused).
4. Sign out + back in. Step-up modal. "I forgot my passphrase" → pick "Use my Emergency Kit" → type 8-word recovery string. Set new passphrase. **New Emergency Kit modal appears immediately** — old kit invalidated, user must save the new one before proceeding.
5. Settings → Danger Zone → Regenerate Emergency Kit. Step-up required. New modal appears. Old kit invalidated immediately.

**Path B: user without TOTP**

6. New user signs up → set passphrase → save Emergency Kit → onboarding card says "Add 2FA?" → user clicks "Skip for now". Account is fully usable.
7. Try to provision a cloud → passphrase step-up → works.
8. Forgot passphrase → recovery-options page shows ONLY "Use my Emergency Kit" (no TOTP button — they don't have it). Redeem → set new passphrase → new Emergency Kit modal. Works exactly the same as path A's Emergency Kit redemption.
9. Later, Settings → "Set up 2FA" link visible. User can enable TOTP at any time. From then on, the recovery-options page surfaces both buttons.

**Shared:**

10. `recovery_codes` table no longer exists; `emergency_kits` table has exactly one active row per user.
11. Try to set passphrase `"qwerty12"` — rejected with `passphrase_invalid` (in top-10000 list).
12. Try to set passphrase `"swift-river"` — accepted.
