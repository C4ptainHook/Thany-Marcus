# Tickets — MVP build plan

Date: 2026-05-13
Status: Living document. Generated from `plans/consolidated-plan-2026-05-13.md` locks (Q1–Q16).

Each ticket is sized at roughly 1–3 days. Total ~80 tickets to MVP. Five mostly-independent tracks after foundation.

## How to read this

- **ID** is stable; use it in commits (`T52: lock prompt schema`).
- **BlockedBy** lists tickets that MUST be done first. Empty BlockedBy = startable immediately after its phase opens.
- **Track** indicates which parallel work-stream the ticket belongs to. Tickets in the same track typically share an implementer; cross-track tickets are parallelizable.
- **Effort** is a rough estimate; revise as you discover.
- **Notes** flag critical-path items, prompt-schema dependencies, or fallback paths.

## Critical path (longest chain)

Two candidate chains bind the schedule, depending on whether T7 (Parakeet) lands at the low or high end of its 2–3 wk effort range. T52 schema lock is the *fan-out* choke point for stubs (T53/T54/T60/T62/T63) — see "Key sequencing constraints" — but does not sit on the longest dependency chain in either scenario.

**Baseline — Cloud-Sync chain:**

`T1 → T4 → T20 → T21 → T23 → T26 → T72 → T74 → T130 → T131 → T132`

**If T7 stretches — Parakeet chain:**

`T1 → T7 → T44 → T100 → T131 → T132`

The Cloud-Sync chain binds when T7 is at the low end; the Parakeet chain overtakes it as T7 approaches the high end. Either way, terminal node is T132 via T131 (T131 `BlockedBy=T100, T130`).

---

## Phase 0 — Foundation (serialize; blocks everything)

| ID | Subject | BlockedBy | Track | Effort | Notes |
|---|---|---|---|---|---|
| T1 | Solution scaffold + monorepo layout + vertical slice convention | — | Foundation | 2 d | .NET 10, Avalonia 11; folder structure per §26 |
| T2 | CI matrix (Win/Mac/Linux build + test) | T1 | Foundation | 1 d | GitHub Actions matrix |
| T3 | `ThanyMarcus.Shared.Contracts` (Provenance schema, API DTOs, domain models) | T1 | Foundation | 2 d | Frozen interface; dependents recompile on change |
| T4 | Cloud API skeleton (ASP.NET Core .NET 10 + Postgres + EF Core baseline migration) | T1 | Foundation | 2 d | Boots, serves /health |
| T5 | Avalonia shell + DI bootstrap + main window stub | T1 | Foundation | 2 d | Compiles + runs on 3 OSes; no features yet |
| T6 | ADRs 0001–0016 from Q1–Q16 locks | T1 | Foundation | 1 d | One-day batch write; thesis-appendix evidence |

---

## Phase 1 — Infrastructure (parallel after T1+T3+T5)

| ID | Subject | BlockedBy | Track | Effort | Notes |
|---|---|---|---|---|---|
| T7 | Parakeet ONNX integration (Path B audio DSP port) | T1 | Infra-ASR | 2–3 wk | **Critical path.** Fallback: Whisper.net if past 3 wk. |
| T8 | LLamaSharp + Gemma 4 E2B integration | T1 | Infra-LLM | 1 wk | **Critical path.** Lazy-load. |
| T9 | multilingual-e5-small embedding via ONNX | T1 | Infra-Embedding | 3 d | Kept loaded permanently (~150 MB). |
| T10 | sqlite-vec extension loading + brute-force fallback | T1 | Infra-Vector | 3–5 d | If extension loading fails on macOS, brute-force cosine works to ~50k vectors. |
| T11 | PortAudio cross-OS mic capture wrapper | T1 | Infra-Audio | 3 d | |
| T12 | SharpHook hotkey wrapper + Wayland detection | T1 | Infra-Hotkey | 1 wk | Wayland: gray out hotkey config, explain in UI |
| T13 | Screenshot region-select overlay (per-OS implementations) | T1 | Infra-Screenshot | 1 wk | Custom transparent always-on-top window; Wayland uses portal |
| T14 | File watcher abstraction + macOS polling fallback | T1 | Infra-FS | 3 d | .NET FileSystemWatcher + 5s polling sweep |
| T15 | Crypto: AES-GCM + Argon2id + dual-use recovery code gen/validation | T1 | Infra-Crypto | 3 d | 8 codes; KDF per code |
| T16 | SQLite saga state schema + persistence layer | T1 | Infra-State | 3 d | LIFO queue persistence, drop-log, settings |
| T17 | SSH.NET tunnel wrapper | T1 | Infra-Net | 2 d | Key auth; tunnel localhost:8080 |
| T18 | Terraform invocation wrapper | T1 | Infra-Provision | 3 d | Streams progress to UI |
| T19 | Caddy admin API client | T4 | Infra-Net | 2 d | For Tier 1 → Tier 2 reconfig |

**All T7–T18 are mutually independent and parallelizable.** T19 depends on cloud API existing.

---

## Phase 2 — Cloud API features (parallel after T3+T4)

| ID | Subject | BlockedBy | Track | Effort | Notes |
|---|---|---|---|---|---|
| T20 | Auth/Register | T4 | Cloud-Auth | 2 d | Argon2id |
| T21 | Auth/Login | T4, T20 | Cloud-Auth | 1 d | |
| T22 | Auth/PasswordReset via dual-use recovery codes | T20, T15 | Cloud-Auth | 2 d | Q15 lock |
| T23 | Artifacts/Upload | T4, T21 | Cloud-Artifacts | 2 d | Filesystem-backed storage |
| T24 | Artifacts/Retrieve | T23 | Cloud-Artifacts | 1 d | |
| T25 | Sync/Push (notes + artifacts + provenance) | T23 | Cloud-Sync | 3 d | |
| T26 | Sync/Pull (delta) | T23 | Cloud-Sync | 3 d | |
| T27 | Projects metadata endpoint (list + create-event log) | T4 | Cloud-Sync | 1 d | |
| T28 | Sharing/Create + ZipBuild (W1+P1+F1+A1 stripping) | T25, T26 | Cloud-Share | 3 d | Q5 lock; zip cached at creation |
| T29 | Sharing/RecipientDownload | T28 | Cloud-Share | 1 d | |
| T30 | Sharing/Revoke (deletes cached zip) | T28 | Cloud-Share | 1 d | |
| T31 | TierUpgrade endpoint (Caddy reconfig + Let's Encrypt) | T19 | Cloud-Tier | 3 d | |
| T32 | Gpu/Spinup (Terraform invocation) | T18 | Cloud-GPU | 3 d | Hetzner GEX |
| T33 | Gpu/CostCap cron (€30 cap + 75% warn + 2h ceiling + concurrency=1) | T32 | Cloud-GPU | 3 d | Q13 lock |
| T34 | Gpu/Teardown | T32 | Cloud-GPU | 2 d | Idle teardown + 2h forced teardown |

**T20–T27 are highly parallel** (different endpoints, mostly independent). T28–T30 chain. T31–T34 are an independent thread.

---

## Phase 3 — Client capture slices (parallel after relevant infra)

| ID | Subject | BlockedBy | Track | Effort |
|---|---|---|---|---|
| T40 | Capture/VoiceMemo slice (button + recording + queue) | T5, T11, T16 | Client-Capture | 3 d |
| T41 | Capture/Screenshot slice (button + overlay + capture + queue) | T5, T13, T16 | Client-Capture | 3 d |
| T42 | Capture/UrlCapture slice (paste + drag + Readability dispatch) | T5, T16 | Client-Capture | 3 d |
| T43 | Capture/DragDropFile slice (drop zone + ingest) | T5, T16 | Client-Capture | 2 d |
| T44 | Capture/Dictation slice (streaming Parakeet UI) | T5, T7, T11 | Client-Capture | 3 d |

T40–T43 parallel after T11+T13+T16. T44 starts later because it needs T7.

---

## Phase 4 — Processing slices

| ID | Subject | BlockedBy | Track | Effort | Notes |
|---|---|---|---|---|---|
| T50 | Processing/ArtifactSaga (orchestrator + LIFO queue + retry + idempotency + warm-up step) | T3, T16 | Client-Processing | 1 wk | Central coordination point |
| T51 | Processing/DropFilter (silence VAD + blur Laplacian + pHash) | T16 | Client-Processing | 3 d | |
| T52 | **Processing/RoutingExtraction (structured LLM call; prompt design)** | T8, T9, T10 | Client-Processing | 1–2 wk | **Critical path.** Output JSON schema defines T53/T54/T60/T62/T63 contracts. Lock schema day 1. |
| T53 | Processing/ProceduralNoteAssembly (pure logic; no LLM) | T3 (schema from T52) | Client-Processing | 3 d | Pure; can stub against schema |
| T54 | Processing/HubRegen (per-affected hub LLM regen with B4c) | T8, T52 | Client-Processing | 1 wk | |
| T55 | Processing/Provenance (JSON writer per §21 schema) | T3 | Client-Processing | 2 d | |

T50 and T52 are the central coordination points. T51/T53/T55 can develop in parallel. T54 waits on T52 schema.

---

## Phase 5 — Knowledge slices

| ID | Subject | BlockedBy | Track | Effort |
|---|---|---|---|---|
| T60 | Knowledge/EntityExtraction (dedup logic + persistence + hub-trigger detection) | T52, T10 | Client-Knowledge | 1 wk |
| T61 | Knowledge/EntityHubs (creation at N=3, Context section maintenance) | T60 | Client-Knowledge | 3 d |
| T62 | Knowledge/WikilinkAnchors (anchor insertion in procedural assembly) | T52, T9, T10 | Client-Knowledge | 3 d |
| T63 | Knowledge/ProjectRouting (project list maintenance, Inbox-until-N=10) | T52 | Client-Knowledge | 3 d |

T60+T62+T63 parallel after T52 schema lock. T61 waits on T60.

---

## Phase 6 — Sync slices

| ID | Subject | BlockedBy | Track | Effort |
|---|---|---|---|---|
| T70 | Sync/FileWatcher (watches vault, dispatches change events) | T14 | Client-Sync | 3 d |
| T71 | Sync/CloudPush | T17, T25 | Client-Sync | 3 d |
| T72 | Sync/CloudPull (bootstrap + delta) | T17, T26 | Client-Sync | 3 d |
| T73 | Sync/ConflictResolution (last-write-wins + .conflict files) | T70 | Client-Sync | 2 d |
| T74 | Sync/ReEmbedOnReceive (B1 behavior on pulled notes) | T9, T10, T72 | Client-Sync | 2 d |

---

## Phase 7 — Client UI shells (parallel, can stub early)

| ID | Subject | BlockedBy | Track | Effort |
|---|---|---|---|---|
| T80 | Main window layout (capture buttons + URL/text inputs + drop zone + queue/recent panel) | T5 | Client-UI | 3 d |
| T81 | Onboarding/StorageScreen | T5 | Client-UI | 2 d |
| T82 | Onboarding/LlmBackendScreen (RAM auto-detect) | T5, T8 | Client-UI | 2 d |
| T83 | Onboarding/CloudProviderScreen | T5 | Client-UI | 1 d |
| T84 | Onboarding/AccountScreen (8 dual-use code display) | T5, T15 | Client-UI | 2 d |
| T85 | Onboarding/ProvisioningScreen (Terraform progress) | T5, T18 | Client-UI | 2 d |
| T86 | Settings/BackendSelection (per-device C2) | T5 | Client-UI | 1 d |
| T87 | Settings/GpuMode (cap + model + batching params) | T5 | Client-UI | 2 d |
| T88 | Settings/VaultPaths (change-with-migration) | T5 | Client-UI | 3 d |
| T89 | Settings/DropLogReview | T5, T51 | Client-UI | 1 d |
| T90 | Settings/RecoveryCodes (rotate + view-once) | T5, T15 | Client-UI | 2 d |
| T91 | CloudAdmin/Provisioning (orchestrates onboarding screens) | T81–T85 | Client-UI | 3 d |
| T92 | CloudAdmin/Destroy (passphrase-confirmed wipe) | T5, T18 | Client-UI | 2 d |
| T93 | CloudAdmin/TierUpgrade (domain entry + DNS instructions) | T5, T31 | Client-UI | 2 d |
| T94 | Sharing client UI (request link + copy + show + revoke) | T5, T28, T30 | Client-UI | 2 d |

T80–T90 can start as stubs immediately after T5; wired to real backends as relevant slices complete.

---

## Phase 8 — Hotkey integration

| ID | Subject | BlockedBy | Track | Effort |
|---|---|---|---|---|
| T100 | Hotkey registration for voice memo / screenshot / dictation | T12, T40, T41, T44 | Client-Capture | 3 d |
| T101 | macOS TCC permission UX (first-run prompt explainer) | T12, T100 | Client-Capture | 2 d |
| T102 | Wayland graceful fallback UI (greyed-out hotkey config) | T12 | Client-UI | 1 d |

---

## Phase 9 — Evaluation + GPU mode

| ID | Subject | BlockedBy | Track | Effort |
|---|---|---|---|---|
| T110 | Eval corpus runner + `eval_corpus_label` plumbing | T55 | Eval | 3 d |
| T111 | Routing baselines: Random, TF-IDF, system-LLM, optional-GPU | T52 | Eval | 1 wk |
| T112 | Routing accuracy reporter (top-1, top-3 vs baselines) | T110, T111 | Eval | 3 d |
| T113 | Entity dedup P/R reporter | T60, T110 | Eval | 3 d |
| T114 | D filter P/R reporter (with constructed test set) | T51 | Eval | 2 d |
| T115 | Wikilink precision-at-K reporter | T62, T110 | Eval | 2 d |
| T116 | Qualitative diary template + capture-rate tracker | T55 | Eval | 1 d |
| T120 | GPU mode end-to-end (Wireguard + Ollama + Qwen 3.6-35B-A3B) | T32, T33, T34 | Cloud-GPU | 1 wk |
| T121 | Vision processor on GPU path | T120 | Client-Processing | 1 wk |
| T122 | Hub-regen escalation to GPU when GPU mode on | T54, T120 | Client-Processing | 3 d |

---

## Phase 10 — Integration, polish, release

| ID | Subject | BlockedBy | Track | Effort |
|---|---|---|---|---|
| T130 | End-to-end integration tests (capture → process → vault → sync) | T50, T74 | Integration | 1 wk |
| T131 | Cross-OS testing matrix in CI (capture surfaces on 3 OSes) | T100, T130 | Integration | 1 wk |
| T132 | Release packaging per OS | T131 | Integration | 1 wk |

---

## Track summary (parallel work assignment)

| Track | Phases | Tickets | Total effort |
|---|---|---|---|
| Foundation | 0 | T1–T6 | ~1 wk |
| Infra-ASR | 1 | T7 | 2–3 wk |
| Infra-LLM | 1 | T8 | 1 wk |
| Infra-Embedding | 1 | T9 | 3 d |
| Infra-Vector | 1 | T10 | 3–5 d |
| Infra-Audio | 1 | T11 | 3 d |
| Infra-Hotkey | 1, 8 | T12, T100–T102 | 1.5 wk |
| Infra-Screenshot | 1 | T13 | 1 wk |
| Infra-FS | 1 | T14 | 3 d |
| Infra-Crypto | 1 | T15 | 3 d |
| Infra-State | 1 | T16 | 3 d |
| Infra-Net | 1 | T17, T19 | 4 d |
| Infra-Provision | 1 | T18 | 3 d |
| Cloud-Auth | 2 | T20–T22 | 5 d |
| Cloud-Artifacts | 2 | T23–T24 | 3 d |
| Cloud-Sync | 2 | T25–T27 | 7 d |
| Cloud-Share | 2 | T28–T30 | 5 d |
| Cloud-Tier | 2 | T31 | 3 d |
| Cloud-GPU | 2, 9 | T32–T34, T120 | 2.5 wk |
| Client-Capture | 3, 8 | T40–T44, T100–T101 | ~3 wk |
| Client-Processing | 4, 9 | T50–T55, T121–T122 | ~6 wk |
| Client-Knowledge | 5 | T60–T63 | ~3 wk |
| Client-Sync | 6 | T70–T74 | ~2 wk |
| Client-UI | 7, 8 | T80–T94, T102 | ~5 wk |
| Eval | 9 | T110–T116 | ~3 wk |
| Integration | 10 | T130–T132 | ~3 wk |

---

## Recommended parallel staffing

### Solo developer

Work the critical path; pick up parallel tickets in idle time. Baseline order:

`T1 → T4 → T20 → T21 → T23 → T26 → T72 → T74 → T130 → T131 → T132`

with T7/T8/T5/T16/T52/T54 interleaved (T7 in particular started as early as possible so its variance does not flip the critical path to the Parakeet chain).

### Two developers

- **Dev A — Client + Parakeet-side chain:** T1 → T5 → T7 → T8 → T11/T13/T14/T15/T16 → T40+T44 → T52 → T54 → T100 → T131
- **Dev B — Cloud + Sync-side chain:** T2 + T3 + T4 → T20+T21+T22 → T23+T25+T26 → T28+T29+T30 → T17+T18+T19+T31 → T32+T33+T34 → T72 → T74 → T130

Convergence at T130/T131/T132.

### Multi-agent (≥3 parallel work-streams)

- **Agent A — Foundation + Critical-path:** T1, T3, T5, T6, T8, T52, T54, T74, T130
- **Agent B — Infrastructure:** T7, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18
- **Agent C — Cloud API:** T2, T4, T19, T20–T34
- **Agent D — Client UI shells:** T80–T94, T102
- **Agent E — Client Capture/Knowledge/Sync slices:** T40–T44, T50, T51, T53, T55, T60–T63, T70–T73
- **Agent F — Eval + GPU:** T110–T116, T120–T122
- **Agent G — Integration + release:** T131, T132

Bottleneck remains the longer of (Cloud-Sync chain through T74) and (Parakeet chain through T44→T100). T52 schema lock must happen early so downstream slices (T53/T54/T60/T62/T63) can stub against it in parallel.

---

## Key sequencing constraints

1. **T52 (RoutingExtraction prompt design) is the central choke point.** Its output JSON schema defines what T53, T54, T60, T62, T63 all consume. **Lock the schema within the first day of T52**; dependent tickets stub against the schema and wire to real LLM output later.
2. **T3 (Shared Contracts) is the cloud↔client schema lock.** Freeze early; bump version if changes are unavoidable.
3. **T7 (Parakeet Path B) is the highest-uncertainty critical-path ticket.** If it stretches past 3 weeks, swap in Whisper.net as the fallback to unblock T40/T44.
4. **Hotkey + permission UX (T100/T101) land late** — they require capture features wired. Don't try to integrate before T40/T41/T44 are real.
5. **End-to-end sync (T74 + T130) is the latest integration point.** Mock the sync transport early in client features; wire to real cloud once T25/T26 land.
6. **Eval corpus collection (T110+) should start during M3-M4 timeframe**, not at M5. Need ~3 weeks of organic use to accumulate 200+ artifacts.

---

## Out of scope (won't appear in this ticket list)

Per `plans/consolidated-plan-2026-05-13.md` §25 future work:

- Mobile native capture
- macOS notarization pipeline (only relevant for public release)
- Hotkey-triggered selected-text capture
- Project explosion mitigation infrastructure
- Privacy detection in pre-filter
- Vault QA / "ask my vault"
- Video file processing
- Twitter/X / Telegram URL support
- CRDT-based concurrent editing
- Real-time collaboration
- Encrypted-at-rest with user-held keys
- Wayland global shortcuts via xdg-desktop-portal-globalshortcuts
- Routing optimization (small dedicated router model)
- Redacted-hub bundling in sharing (W3 alternative)
- Compliance frameworks (HIPAA, GDPR)
