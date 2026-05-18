# CLOUD-SCHEMA-V2 Composite-note schema migration — Handoff Brief

Date: 2026-05-19
Status: Draft. First ticket of the ADR-0045 implementation series. **Migration-only.** No endpoints, no workers, no handlers, no saga code, no business logic of any kind.

**Goal:** land a single EF Core migration that brings the cloud DB from the CLOUD-002 shape ("composite-ingest spine v1") to the ADR-0045 shape ("composite-note schema v2"). Concretely: install the `vector` extension, create three new tables (`entities`, `mentions`, `extraction_tasks`), alter the three existing tables (`notes`, `attachments`, `ingest_jobs`) per ADR-0045 §§5–6 + §"Migration plan", and add every index ADR-0045 names (including the two HNSW indexes for pgvector). After this ticket, every column, FK, constraint, and index needed by CLOUD-003 (extraction-task workers), CLOUD-009/010 (Granite embedding + pgvector helpers), CLOUD-019 (entity dedup), CLOUD-020 (entity hubs), and the M6 routing pipeline exists in the cloud DB schema — but **not a single line of code reads or writes any of the new columns**. The migration plus the matching entity-class + `IEntityTypeConfiguration<T>` scaffolds (the minimum EF needs to round-trip the snapshot) are the entire deliverable.

Estimated **0.5 person-day** with AI-agent assistance. This is intentionally a small, mechanically-checkable ticket: the design work is in ADR-0045; this ticket is the transcription. The reason it ships on its own — instead of bundled with CLOUD-003 — is so the schema can land, be reviewed against ADR-0045 row-by-row, and stop drifting from prose while the consumer tickets are still being scoped.

## Where decisions live (read before doing anything)

- **`docs/decisions/0045-composite-note-schema.md`** — the contract. §§5–6 specify the new tables column-for-column; §"Migration plan" gives the SQL skeleton; §11 (idempotency) and §12 (soft-delete) lock the partial-unique and CASCADE behavior. **Every column/index/check in this ticket must trace to a section of ADR-0045 verbatim.**
- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — owns the saga state machine that consumes the new schema. §3 defines the `ingest_jobs.status` vocabulary that the new check constraint must enumerate (`queued | extracting_attachments | composing | routing | extracting_entities | embedding | succeeded | failed_extraction | failed_composition | failed_route | failed_entities | failed_embedding | dead_lettered`) and the terminal-state list used by the new partial-unique index. §3 also defines `extraction_tasks.status` (`queued | processing | succeeded | failed | skipped`).
- **`docs/decisions/0043-cloud-side-model-lineup.md`** — pins the embedding dimension at **256** (Granite 278m R2 Matryoshka cut). The `vector(256)` columns on `notes` and `entities` are not negotiable in this ticket.
- **`docs/decisions/0028-schema-conventions.md`** — snake_case via `EFCore.NamingConventions`, NodaTime `Instant`, `created_at` / `updated_at` everywhere, app calls `db.Database.MigrateAsync()` at startup. **Applies as-is.**
- **`docs/decisions/0032-fk-cascades-and-soft-delete.md`** — CASCADE vs SET NULL conventions used by ADR-0045 §12. Echoed here for safety in the table below.
- **`docs/decisions/0024-dbcontext-shape.md`** — single `CloudDbContext`; one `IEntityTypeConfiguration<T>` per entity, registered via `ApplyConfigurationsFromAssembly`.
- **`docs/decisions/0023-test-stack.md`** §143 — explicit note that `Cloud.Api`'s Testcontainers fixture "will need pgvector" via `pgvector/pgvector:pg16`. **This ticket is the one that bumps the image.**
- **`plans/cloud-002-handoff.md`** — current schema baseline; the migration `20260517215436_CompositeIngestSpine.cs` is what this ticket diffs against.
- **Memory `composite_ingest_decision.md`** (2026-05-17) — one composite draft → one processed note; the schema must continue to honor this (1 `notes` row per ingest, attachments as children).
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3. The migration project compiles under warnings-as-errors; the test project consumes the bumped fixture.

**Do not litigate ADR-0045.** If you find a column you think is wrong, raise it as a follow-up; do not change the column shape in this ticket. The ticket is "transcribe the ADR," not "improve the ADR."

## Scope boundary (precise)

### In scope

1. **One new EF migration** under `src/ThanyMarcus.Cloud.Api/Infrastructure/Database/Migrations/` named `CompositeNoteSchemaV2` (file name `<timestamp>_CompositeNoteSchemaV2.cs`). The migration body is the SQL from ADR-0045 §"Migration plan" translated to EF `MigrationBuilder` calls, **with two raw-SQL escape hatches** for the pieces EF Core 10 + Npgsql provider don't model:
   - `migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;")` — run **first** so the `vector(256)` columns can be added in the same migration.
   - Each HNSW index — `migrationBuilder.Sql("CREATE INDEX ix_notes_embedding ON notes USING hnsw (embedding vector_cosine_ops);")` and the equivalent on `entities`. The EF model snapshot will not reflect HNSW specifics (Npgsql doesn't surface `USING hnsw` to `HasIndex`); that's acceptable — the snapshot only needs to know an index by name exists, and we pair the raw `Sql(...)` with `migrationBuilder.DropIndex(...)` in `Down` so a rollback works.
   - Everything else (column adds, column drops, plain B-tree indexes, FKs, check constraints, partial-unique indexes with `filter: "..."`) goes through normal `MigrationBuilder` calls.

2. **Entity classes + `IEntityTypeConfiguration<T>` scaffolds** so the EF model round-trips and the snapshot file regenerates cleanly:
   - `src/ThanyMarcus.Cloud.Api/Features/Entities/Entity.cs` + `EntityConfiguration.cs` + a `static class EntityKind { Person, Organization, Project, Place, Concept, Other }` and `static class EntitySource { User, Llm }` (constants, not enums — match the `NoteStatus` / `AttachmentKind` pattern at `Features/Ingest/Note.cs:26` and `Features/Ingest/Attachment.cs:34`).
   - `src/ThanyMarcus.Cloud.Api/Features/Entities/Mention.cs` + `MentionConfiguration.cs`.
   - `src/ThanyMarcus.Cloud.Api/Features/Processing/ExtractionTask.cs` + `ExtractionTaskConfiguration.cs` + `static class ExtractionTaskStatus` constants (`Queued`, `Processing`, `Succeeded`, `Failed`, `Skipped`) — **not** living next to `IngestJob.cs` in a sub-namespace; same `Features/Processing/` folder.
   - Extend `src/ThanyMarcus.Cloud.Api/Features/Ingest/Note.cs` with the new properties: `Vector? Embedding`, `Instant? DeletedAt`, `bool IsHub`, `Guid? ProjectId`, `Guid? HubEntityId`, `long TransitionVersion`. **Drop** the existing `string? SuggestedProject` property — it is being replaced by `ProjectId`.
   - Extend `src/ThanyMarcus.Cloud.Api/Features/Ingest/Attachment.cs` with `Guid? ParentAttachmentId`, `string? ExtractionCacheKey`, `string? Url`.
   - Extend `src/ThanyMarcus.Cloud.Api/Features/Processing/IngestJob.cs` with `string Kind` (defaulting to `IngestJobKind.Capture`), `JsonDocument EventsLog` (initialized to `JsonDocument.Parse("[]")`), `long TransitionVersion`. Add `static class IngestJobKind { Capture, Reprocess, HubRegen }` and **extend** `static class IngestJobStatus` to add the new phase constants from ADR-0042 §3 (six in-flight phase states + five failure terminals). The existing `Queued`, `Processing`, `Succeeded`, `DeadLettered` stay; **`Processing` is removed from the vocabulary in the new check constraint** (replaced by the per-phase states), so callers that wrote `IngestJobStatus.Processing` must be updated to use the appropriate per-phase state — but the only such caller today is `IngestSagaWorker`, and **this ticket does not modify the saga**. The saga rewrite lands in a later ticket; until then the check constraint must continue to allow `'processing'` so the existing worker still runs. See "Status vocabulary" below for the exact resolution.

3. **Register the three new `DbSet<>`s on `CloudDbContext`** (`src/ThanyMarcus.Cloud.Api/Infrastructure/Database/CloudDbContext.cs`). `Entities`, `Mentions`, `ExtractionTasks`. The existing `ApplyConfigurationsFromAssembly(...)` call picks up the new configurations automatically.

4. **Bump the test-fixture Postgres image** to `pgvector/pgvector:pg16` in **both** fixtures:
   - `tests/ThanyMarcus.Cloud.Tests/Infrastructure/PostgresFixture.cs:11` — required (the migration will fail with `ERROR: type "vector" does not exist` on stock `postgres:16-alpine`).
   - `tests/ThanyMarcus.Portal.Tests/Infrastructure/PostgresFixture.cs:11` — **not required for this ticket** (Portal DB has no vector columns), but bump it anyway to keep the two clouds' fixtures symmetric and to avoid a future "why does this differ?" puzzle. If the bump causes any unrelated portal-test flake, revert the portal fixture and document in the PR description; do not block this ticket on it.

5. **Regenerate the model snapshot** by running `dotnet ef migrations add CompositeNoteSchemaV2 --project src/ThanyMarcus.Cloud.Api`. The snapshot file `CloudDbContextModelSnapshot.cs` updates atomically; commit it alongside the migration.

6. **One end-to-end migration smoke test** under `tests/ThanyMarcus.Cloud.Tests/Infrastructure/SchemaV2MigrationTests.cs`. Single test: spin up a fresh `pgvector/pgvector:pg16` container, run `db.Database.MigrateAsync()`, then run the assertions below:
   - `SELECT extname FROM pg_extension WHERE extname='vector'` returns one row.
   - `SELECT 1 FROM pg_indexes WHERE indexname='ix_notes_embedding' AND indexdef LIKE '%USING hnsw%'` returns one row (and the equivalent for `ix_entities_embedding`).
   - `SELECT column_name FROM information_schema.columns WHERE table_name='notes' AND column_name IN ('embedding','deleted_at','is_hub','project_id','hub_entity_id','transition_version')` returns six rows.
   - `SELECT column_name FROM information_schema.columns WHERE table_name='notes' AND column_name='suggested_project'` returns **zero** rows.
   - `INSERT INTO entities (id, kind, canonical_name, source) VALUES (gen_random_uuid(), 'project', 'TestProj', 'user')` succeeds, then a second insert with `(kind='project', canonical_name='TestProj')` fails with a unique-constraint violation (validates the soft-delete-aware partial unique index).
   - `INSERT INTO notes (... required cols ..., embedding) VALUES (..., '[0.1, 0.2, ...]'::vector(256))` succeeds (validates the column type is actually `vector(256)`, not `text` or `bytea`).
   - **Do not** test query semantics, HNSW correctness, or any business-rule behavior. The test answers exactly one question: "did the migration apply cleanly and produce the columns + indexes ADR-0045 names?"

### Out of scope (named so they don't sneak in)

- ❌ **Saga rewrites.** `IngestSagaWorker`, `CompositeIngestHandler`, `CompositeMarkdownAssembler` — all stay byte-for-byte the same. Even though the new columns exist, no code reads them yet. The saga is still routing-by-string-and-suggested-project — wait, no — see "Knock-on for the existing saga" below for the one tiny adjustment needed.
- ❌ **Endpoints.** No new HTTP route. The existing `/api/ingest/init`, `/api/ingest/{id}/finalize`, `/api/sync/pull` shapes do not change. ADR-0045 §10's `/api/admin/projects/*`, `/api/sync/push`, `/api/sync/events`, `DELETE /api/notes/{id}`, `POST /api/notes/{id}/reprocess` are all later tickets.
- ❌ **Plugin contract changes.** The Obsidian plugin sees the same JSON shapes from this ticket as it does today. Don't update `ThanyMarcus.Shared` TS types for any of the new columns — they're internal until consumers ship.
- ❌ **Granite embedding model / ONNX integration.** ADR-0043's Granite Embedding 278m R2 lands in CLOUD-009. The `vector(256)` column is created here; nothing writes to it until then.
- ❌ **pgvector helper methods on `CloudDbContext` / repos.** Top-K cosine queries land in CLOUD-010.
- ❌ **Backfill or data migration.** `notes.suggested_project` is dropped; any non-null value is lost. **Acceptable** because no production cloud has yet been provisioned beyond smoke instances (per the cloud-pivot rollout state on 2026-05-19); smoke instances are wiped between runs. If a long-lived dev cloud exists with real data, drop the DB and re-migrate — explicitly documented in the migration's `Down` (which does **not** preserve `suggested_project` data either).
- ❌ **`notes.llm_mode` check-constraint update.** ADR-0045 §1 implies an `llm_mode` value space of `'safe' | 'unsafe'`; the current constraint allows `'safe' | 'unsafe_anthropic' | 'unsafe_openai'`. Reconciling these is part of the LLM-tier ticket (CLOUD-008 / later) that decides what the API actually returns. Leave the current constraint alone in this ticket.
- ❌ **`cloud_settings` changes.** No new columns. The single-row table stays as-is.
- ❌ **Hub note materialization, project auto-creation, mention threshold logic.** All deferred to consumer tickets per ADR-0045 §5 keys "behavior" — none of that code runs in this ticket.
- ❌ **Provenance schema enforcement.** `notes.provenance` is already `jsonb nullable` and stays that way; ADR-0042 §10c run-history schema is consumer-side concern.

### Status vocabulary — exact resolution

The collision between today's `ingest_jobs.status` check constraint (`'queued','processing','succeeded','dead_lettered'`) and ADR-0042 §3's full phase vocabulary is the one knot in this ticket. Resolution:

**Replace the existing check constraint** with the union of *both* old and new vocabularies, so the existing saga keeps running with `'processing'` and the new schema simultaneously supports the per-phase states for the rewrite-saga ticket to come:

```
check (status in (
    -- legacy (pre-phase-rewrite saga; CLOUD-002 vintage)
    'processing',
    -- ADR-0042 §3 phase states
    'queued',
    'extracting_attachments',
    'composing',
    'routing',
    'extracting_entities',
    'embedding',
    'succeeded',
    -- ADR-0042 §3 failure terminals
    'failed_extraction',
    'failed_composition',
    'failed_route',
    'failed_entities',
    'failed_embedding',
    'dead_lettered'
))
```

The migration `DropCheckConstraint("ck_ingest_jobs_status")` then `AddCheckConstraint` with the union. Both work in EF Core's `MigrationBuilder`. **Note** that the legacy `'processing'` value is a transitional convenience; the saga-rewrite ticket removes it from the constraint and converts any in-flight rows. Adding a TODO at the top of `IngestJobStatus` (`// 'processing' is legacy CLOUD-002; saga-rewrite ticket removes it`) is the rare "non-obvious why" comment allowed by `feedback_no_code_comments`. Otherwise no comments.

The new partial-unique index `ix_ingest_jobs_active_per_note` filters on **terminal-state list** = `('succeeded', 'failed_extraction', 'failed_composition', 'failed_route', 'failed_entities', 'failed_embedding', 'dead_lettered')`. `'processing'` is **not** terminal (it's the legacy in-flight state); it gets included in the "active" set so that today's saga continues to enforce "one active job per note" exactly as before.

```csharp
migrationBuilder.CreateIndex(
    name: "ix_ingest_jobs_active_per_note",
    table: "ingest_jobs",
    column: "note_id",
    unique: true,
    filter: "status NOT IN ('succeeded','failed_extraction','failed_composition'," +
            "'failed_route','failed_entities','failed_embedding','dead_lettered')");
```

Existing index `ix_ingest_jobs_queued_scheduled_at` (filter `status = 'queued'`) stays untouched. The new `ix_extraction_tasks_claim` per ADR-0045 §4 indexes `(target_sidecar, status, scheduled_at) WHERE status IN ('queued','processing')`.

### Knock-on for the existing saga (the one tiny adjustment)

`Features/Ingest/Note.cs` currently exposes `SuggestedProject : string?` which is read by `CompositeIngestHandler` (`Features/Processing/CompositeIngestHandler.cs`) when computing `relative_path`. Dropping the column without updating that reference will fail the build under warnings-as-errors. Resolution:

- Keep `SuggestedProject` as a **mapped-to-nothing** transient C# property (no DB column) for the duration of this ticket — i.e., `[NotMapped] public string? SuggestedProject { get; set; }` so the saga compiles. The value is computed at write time and used to compute `RelativePath`; it never round-trips through the DB after this ticket. The mapping-replacement (read from `Note.ProjectId` → join `Entity.CanonicalName` → fold into `RelativePath`) is the M6-routing ticket's job.
- Alternative: temporarily map `SuggestedProject` to a **new, separate** transient column. **Don't** do this — it adds DB surface area for no benefit and creates a second drop migration later.

If `[NotMapped]` feels wrong, the cleaner option is to leave `SuggestedProject` as a plain in-memory C# property without an EF mapping at all (don't list it in `EntityConfiguration`); EF auto-ignores untracked properties. Pick whichever is more idiomatic in the existing codebase — both work, neither is load-bearing.

This is the entire saga-side change. No handler logic moves; no route logic moves; no LLM prompt moves.

### pgvector C# type — the one library decision

The `embedding vector(256)` columns need a C# representation. Two options:

| Option | Why | Drawback |
|---|---|---|
| **`Pgvector` + `Pgvector.EntityFrameworkCore` NuGet** | Official-pattern: `Vector` type, EF mapping, query operators (`<=>`, `<->`). Used by every Npgsql + pgvector project in the ecosystem. | Adds two NuGet refs; pin to a 0.x version (CVE-warnings-as-errors should pass — verify). |
| **Raw `float[]` with custom value converter** | Zero new deps. | Custom converter, no operator overloads — every query becomes raw SQL. Painful by CLOUD-010. |

**Choose `Pgvector` + `Pgvector.EntityFrameworkCore`.** Add to `Directory.Packages.props` under a new comment block (`<!-- pgvector EF mapping (CLOUD-SCHEMA-V2). -->`); add the references to `ThanyMarcus.Cloud.Api.csproj`. The configuration is one line per embedding-bearing entity: `optionsBuilder.UseNpgsql(connStr, npg => npg.UseNodaTime().UseVector())`.

The `Embedding` property type is `Pgvector.Vector?` (nullable). Construction is `new Vector(new ReadOnlyMemory<float>(arr))`; persistence is automatic. Verify the package is Apache-2.0 / MIT compatible (it is at time of writing; reconfirm during the PR review).

## File map

```
Thany-Marcus/
├── docs/decisions/
│   └── 0045-composite-note-schema.md                       # (read-only) the contract
├── plans/
│   └── cloud-schema-v2-handoff.md                          # THIS FILE
├── Directory.Packages.props                                # CHANGED: + Pgvector, Pgvector.EntityFrameworkCore
├── src/ThanyMarcus.Cloud.Api/
│   ├── ThanyMarcus.Cloud.Api.csproj                        # CHANGED: + Pgvector, Pgvector.EntityFrameworkCore PackageReferences
│   ├── Infrastructure/Database/
│   │   ├── CloudDbContext.cs                               # CHANGED: + DbSet<Entity> Entities, + DbSet<Mention> Mentions, + DbSet<ExtractionTask> ExtractionTasks; .UseVector() on Npgsql options where the context is constructed
│   │   ├── DesignTimeDbContextFactory.cs                   # CHANGED: + .UseVector() on the same npg options builder
│   │   └── Migrations/
│   │       ├── <ts>_CompositeNoteSchemaV2.cs               # NEW: the migration
│   │       ├── <ts>_CompositeNoteSchemaV2.Designer.cs      # NEW: EF-generated
│   │       └── CloudDbContextModelSnapshot.cs              # CHANGED: EF-regenerated
│   ├── Features/
│   │   ├── Ingest/
│   │   │   ├── Note.cs                                     # CHANGED: + Embedding, + DeletedAt, + IsHub, + ProjectId, + HubEntityId, + TransitionVersion; - SuggestedProject as DB column (kept as [NotMapped] transient or just unmapped)
│   │   │   ├── NoteConfiguration.cs                        # CHANGED: + property mappings, + 4 new indexes (client_note_id-partial, embedding-HNSW-via-Sql, updated_at-partial, project, hub_entity-partial), - SuggestedProject mapping
│   │   │   ├── Attachment.cs                               # CHANGED: + ParentAttachmentId, + ExtractionCacheKey, + Url
│   │   │   └── AttachmentConfiguration.cs                  # CHANGED: + new column mappings, + ix_attachments_parent, + ix_attachments_cache (sha256+cache_key, filtered)
│   │   ├── Entities/                                       # NEW directory
│   │   │   ├── Entity.cs                                   # NEW: ADR-0045 §5 columns; EntityKind + EntitySource constants alongside
│   │   │   ├── EntityConfiguration.cs                      # NEW
│   │   │   ├── Mention.cs                                  # NEW: ADR-0045 §6 columns
│   │   │   └── MentionConfiguration.cs                     # NEW
│   │   └── Processing/
│   │       ├── IngestJob.cs                                # CHANGED: + Kind, + EventsLog, + TransitionVersion; IngestJobStatus extended with phase + failure-terminal constants; IngestJobKind constants added
│   │       ├── IngestJobConfiguration.cs                   # CHANGED: drop+re-add ck_ingest_jobs_status with union vocab; + ck_ingest_jobs_kind; + ix_ingest_jobs_active_per_note
│   │       ├── ExtractionTask.cs                           # NEW: ADR-0045 §4 columns; ExtractionTaskStatus + ExtractionTaskSidecar constants
│   │       └── ExtractionTaskConfiguration.cs              # NEW
│   └── (no other files touched)
└── tests/ThanyMarcus.Cloud.Tests/
    ├── Infrastructure/
    │   ├── PostgresFixture.cs                              # CHANGED: image → pgvector/pgvector:pg16
    │   └── SchemaV2MigrationTests.cs                       # NEW: single migration-smoke test
    └── (no other test files touched)

tests/ThanyMarcus.Portal.Tests/Infrastructure/PostgresFixture.cs   # CHANGED (best-effort symmetry): image → pgvector/pgvector:pg16
```

## Schema reference (column-by-column, in migration order)

### 0. pgvector extension — first

```csharp
migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");
```

Runs once per cloud DB. Idempotent. **Must be the first statement** so the `vector(256)` columns below can be declared.

### 1. NEW: `entities` (ADR-0045 §5)

```
entities:
  id                  uuid pk
  kind                text not null check (kind in ('person','organization','project','place','concept','other'))
  canonical_name      text not null
  aliases             text[] not null default '{}'
  description         text null
  embedding           vector(256) null
  hub_note_id         uuid null  -- FK→notes(id) ON DELETE SET NULL — added after notes alter (cyclic FK; see below)
  mention_count       int  not null default 0
  source              text not null check (source in ('user','llm'))
  is_provisional      bool not null default false
  vault_folder        text null
  deleted_at          timestamptz null
  created_at          timestamptz not null
  updated_at          timestamptz not null

indexes:
  unique partial (kind, canonical_name) where deleted_at is null
                                          -- migrationBuilder.CreateIndex(... filter: "deleted_at IS NULL", unique: true)
  hnsw   (embedding vector_cosine_ops)    -- via migrationBuilder.Sql
  btree  (kind, source)
```

**FK `hub_note_id → notes.id ON DELETE SET NULL`** is added in a *later* step in the migration so the cyclic FK with `notes.project_id → entities.id` lands cleanly. Build order:

  1. `CREATE TABLE entities` without `hub_note_id` FK constraint.
  2. `CREATE TABLE mentions`.
  3. `CREATE TABLE extraction_tasks`.
  4. `ALTER TABLE notes ADD COLUMN project_id ... REFERENCES entities(id) ON DELETE SET NULL`.
  5. `ALTER TABLE notes ADD COLUMN hub_entity_id ... REFERENCES entities(id) ON DELETE SET NULL`.
  6. `ALTER TABLE entities ADD CONSTRAINT fk_entities_hub_note FOREIGN KEY (hub_note_id) REFERENCES notes(id) ON DELETE SET NULL`.

EF Core handles cyclic FKs by deferring the closing leg to step 6; expressing this in `MigrationBuilder` requires splitting the entity's table creation from one of the FK additions. The simplest pattern: declare `Entity.HubNoteId` as `Guid?` in the entity class, do **not** call `builder.HasOne<Note>().WithMany().HasForeignKey(e => e.HubNoteId)` in `EntityConfiguration` (which would force EF to emit the FK at `CREATE TABLE` time); instead, add `migrationBuilder.AddForeignKey(...)` manually after the `notes` alters. Add the equivalent `RemoveForeignKey` in `Down`.

### 2. NEW: `mentions` (ADR-0045 §6)

```
mentions:
  id           uuid pk
  entity_id    uuid not null references entities(id) on delete cascade
  note_id      uuid not null references notes(id)    on delete cascade
  anchor_text  text not null
  start_offset int  not null
  end_offset   int  not null
  confidence   real null
  created_at   timestamptz not null

indexes:
  btree (entity_id)
  btree (note_id)
```

No soft-delete (mentions are regenerated on re-extraction per ADR-0045 §6 "Re-extraction semantics"). No `updated_at` (immutable per row; rewritten by delete+insert in the consumer ticket).

### 3. NEW: `extraction_tasks` (ADR-0045 §4 + ADR-0042 §3)

```
extraction_tasks:
  id                  uuid pk
  ingest_job_id       uuid not null references ingest_jobs(id) on delete cascade
  attachment_id       uuid not null references attachments(id) on delete cascade
  target_sidecar      text not null check (target_sidecar in ('ollama','docling','parakeet','url','video'))
  status              text not null check (status in ('queued','processing','succeeded','failed','skipped'))
  attempts            smallint not null default 0
  last_error          text null
  lease_owner         text null
  lease_expires_at    timestamptz null
  scheduled_at        timestamptz not null
  started_at          timestamptz null
  finished_at         timestamptz null
  events_log          jsonb not null default '[]'::jsonb
  transition_version  bigint not null default 0
  created_at          timestamptz not null
  updated_at          timestamptz not null

indexes:
  btree (target_sidecar, status, scheduled_at) filter: status in ('queued','processing')
  btree (ingest_job_id)
```

### 4. ALTER: `notes` (ADR-0045 §1 + §"Migration plan")

Column adds (all nullable or defaulted; migration safe on populated DB):

```
+ embedding           vector(256)  null
+ deleted_at          timestamptz  null
+ is_hub              bool         not null default false
+ project_id          uuid         null references entities(id) on delete set null
+ hub_entity_id       uuid         null references entities(id) on delete set null
+ transition_version  bigint       not null default 0
- suggested_project   text         (DROPPED)
```

Index adds:

```
- (existing) ix_notes_client_note_id (client_note_id) UNIQUE  → REPLACE with unique partial filter "client_note_id IS NOT NULL"
                                       (today's index has no filter; the new shape allows null client_note_id, e.g. hub notes generated cloud-side)
- (existing) ix_notes_status_updated_at — keep as-is, do NOT drop
+ hnsw ix_notes_embedding (embedding vector_cosine_ops)        -- via Sql
+ btree partial ix_notes_updated_at (updated_at) filter "deleted_at IS NULL OR status = 'ready'"
+ btree ix_notes_project (project_id)
+ btree partial ix_notes_hub_entity (hub_entity_id) filter "is_hub"
```

Note the `ix_notes_client_note_id` shape change — today's index is unique non-partial, which means `client_note_id` must be unique *across all rows including NULLs* per Postgres semantics (multiple NULLs are allowed in unique non-partial indexes, so this happens to behave correctly). The new shape makes the filter explicit. The migration must `DropIndex` then `CreateIndex` with the filter.

### 5. ALTER: `attachments` (ADR-0045 §2)

Column adds:

```
+ parent_attachment_id uuid null references attachments(id) on delete cascade
+ extraction_cache_key text null
+ url                  text null   -- previously stored in extra.url; promote
```

Index adds:

```
+ btree ix_attachments_parent (parent_attachment_id)
+ btree partial ix_attachments_cache (sha256, extraction_cache_key) filter "extracted_text IS NOT NULL"
```

Existing indexes (`ix_attachments_note_id`, `ix_attachments_note_id_client_attachment_id` UNIQUE, `ix_attachments_storage_key` UNIQUE) all stay.

**Data preservation** — `extra->>'url'` is **not** backfilled into the new `url` column in this migration. The consumer ticket (URL extractor work) can either backfill at read time or do a one-off SQL update later; thesis-scale data volume + smoke-only existing rows make this safe to defer.

### 6. ALTER: `ingest_jobs` (ADR-0042 §3 + ADR-0045)

Column adds:

```
+ kind               text  not null default 'capture' check (kind in ('capture','hub_regen','reprocess'))
+ events_log         jsonb not null default '[]'::jsonb
+ transition_version bigint not null default 0
```

Constraint changes:

```
- ck_ingest_jobs_status (drop)
+ ck_ingest_jobs_status (recreate with union vocab — see "Status vocabulary" above)
+ ck_ingest_jobs_kind   (new)
```

Index adds:

```
+ unique partial ix_ingest_jobs_active_per_note (note_id) filter "status NOT IN (terminal-list)"
```

Existing `ix_ingest_jobs_queued_scheduled_at` and `ix_ingest_jobs_note_id` stay.

## Migration `Down` shape

`Down` reverses each step in opposite order. The HNSW indexes and `vector` extension get explicit drops:

```csharp
migrationBuilder.Sql("DROP INDEX IF EXISTS ix_notes_embedding;");
migrationBuilder.Sql("DROP INDEX IF EXISTS ix_entities_embedding;");
// ... (everything else via standard MigrationBuilder.DropX)
migrationBuilder.Sql("DROP EXTENSION IF EXISTS vector;");
```

`Down` does **not** restore `notes.suggested_project` data (the column is recreated, but rows revert with `NULL`). Acceptable per the "no production data yet" condition.

## Acceptance criteria

1. ✅ A new migration file `<ts>_CompositeNoteSchemaV2.cs` exists; its `Up()` performs every operation in the "Schema reference" section in the documented order; its `Down()` reverses cleanly.
2. ✅ `dotnet ef migrations script <prev> <new> --project src/ThanyMarcus.Cloud.Api` produces a SQL script that:
   - Begins with `CREATE EXTENSION IF NOT EXISTS vector;`
   - Contains exactly two `USING hnsw` index creations.
   - Contains the union check constraint on `ingest_jobs.status` with all 13 values from "Status vocabulary".
   - Contains `DROP COLUMN suggested_project`.
3. ✅ `dotnet build` is green under `Directory.Build.props` warnings-as-errors.
4. ✅ `tests/ThanyMarcus.Cloud.Tests/Infrastructure/SchemaV2MigrationTests.cs` passes, asserting the seven checks under "In scope" point 6.
5. ✅ The existing `Cloud.Tests` suite continues to pass after the fixture image bump (the existing CLOUD-002 tests don't read any new columns).
6. ✅ The model snapshot `CloudDbContextModelSnapshot.cs` is checked in; `dotnet ef migrations add Noop --no-build` produces an empty migration (proves snapshot == DbContext).
7. ✅ `CompositeIngestHandler` still compiles and its tests still pass (the `[NotMapped] SuggestedProject` keep-alive is the only saga-side change in this ticket).

## Risks and gotchas

- **HNSW build time.** On an empty `notes` table, `CREATE INDEX ... USING hnsw` takes milliseconds. On a populated table (none today), it can take minutes — and pgvector's HNSW build is single-threaded by default. Not a problem at thesis scale, but if you smoke-test against a 10k-row dev DB, expect a pause. Document expectation in the migration's `Up()` only if you hit it.
- **`Pgvector.EntityFrameworkCore` package versioning.** Lock to a known-good version (check the package's compatibility matrix against `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x). If 10.x compatibility is missing at the time of this ticket, pin to the latest 9.x of *both* Npgsql and Pgvector; flag the discrepancy in the PR and open a follow-up to upgrade once available. Do not push EF Core down to 9.x just to land this.
- **Snapshot HNSW representation.** EF's snapshot will record the index by name but without the `USING hnsw` clause (only `migrationBuilder.Sql` knows about it). Future migrations that touch `notes` might emit a spurious "create index ix_notes_embedding" — handle by post-editing the generated migration to drop the spurious creation, OR exclude the HNSW indexes from the snapshot via `HasAnnotation("Relational:CreateIndex", false)` if EF supports it for your version. Try the simpler fix first.
- **Cyclic FK insertion in tests.** Inserting a `notes` row with `project_id` referencing an `entities` row requires the entity to exist first; inserting an entity with `hub_note_id` requires the note to exist first. In smoke tests, set one of the two FKs to NULL on initial insert, then UPDATE after both rows exist. This is standard for cyclic-FK schemas; document in `SchemaV2MigrationTests` if a future reader trips on it.
- **`Pgvector.Vector` JSON serialization.** If a future endpoint serializes a `Note` to JSON, `Vector` may not serialize cleanly (it's a custom struct). Out of scope here, but note: the `Note` entity is internal to the cloud DB and never serialized to the plugin today, so the risk is theoretical until an admin/debug endpoint exposes it.
- **Testcontainers image pull.** `pgvector/pgvector:pg16` is ~50 MB larger than `postgres:16-alpine`. CI cold-start adds a few seconds. Acceptable.
- **Postgres version drift.** `pgvector/pgvector:pg16` ships pgvector 0.7+ against Postgres 16. ADR-0038 (managed Postgres) doesn't pin a version yet; the production DO Managed Postgres version must be ≥ 16 with the `vector` extension available. This is an operations concern that lands when CLOUD-008 / portal-provisioning wires real managed Postgres; in MVP we use a self-hosted Postgres container with pgvector bundled, so the version is whatever the image gives us. Flag if a future ticket assumes pg17.

## Cross-references and follow-ups (NOT in this ticket)

- **CLOUD-003 (extraction-task workers)** — consumes `extraction_tasks` and the new `attachments` columns (`parent_attachment_id`, `extraction_cache_key`).
- **CLOUD-008 (LLM tier rework)** — reconciles `notes.llm_mode` vocabulary (`'safe'|'unsafe'` per ADR-0045 vs today's three-value enum). May land sooner than CLOUD-003 if priorities shift.
- **CLOUD-009 + CLOUD-010 (Granite ONNX + pgvector helpers)** — first consumers of the new `vector(256)` columns and HNSW indexes.
- **CLOUD-017 / CLOUD-019 / CLOUD-020 (routing + entity dedup + hub regen)** — first consumers of `entities` + `mentions` + `notes.project_id` + `notes.is_hub`.
- **Saga rewrite (no ticket ID yet)** — moves `ingest_jobs.status` from legacy `'processing'` to per-phase states; converts in-flight rows; drops `'processing'` from the check constraint.
- **`Note.SuggestedProject` removal** — once routing reads `notes.project_id`, the `[NotMapped]` transient property gets deleted entirely.

## What "done" looks like

```
$ dotnet ef migrations script --project src/ThanyMarcus.Cloud.Api --output schema-v2.sql
$ head -1 schema-v2.sql
CREATE EXTENSION IF NOT EXISTS vector;
$ grep -c "USING hnsw" schema-v2.sql
2
$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter SchemaV2MigrationTests
Passed!  - Failed: 0, Passed: 1, Skipped: 0
$ dotnet test tests/ThanyMarcus.Cloud.Tests
Passed!  - Failed: 0, Passed: N, Skipped: 0     # N = pre-existing CLOUD-002 test count, unchanged
```

The cloud DB schema now matches ADR-0045 §§4–6 column-for-column. No endpoint exposes any new column. No worker reads any new column. The next ticket in the series picks up immediately.
