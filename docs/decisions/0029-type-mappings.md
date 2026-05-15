# ADR-0029: Type mappings — primary key, timestamp, crypto column shapes

Status: Accepted
Date: 2026-05-15

## Context

Three foundational C#-to-Postgres type-mapping decisions span every table in PORTAL-002:

1. **Primary key strategy.** Type (UUID vs `bigint`) intertwines with generation site (app-side vs DB-side).
2. **Timestamp type.** Postgres `timestamptz` semantically stores a UTC instant; C# offers several types that lie about this to varying degrees.
3. **Encrypted column shapes.** AES-GCM ciphertext layout and Argon2id-hashed verification token storage affect every encrypted column (`totp_secrets`, `encrypted_provider_tokens`, `clouds.cloud_admin_token`, `users.passphrase_*`, `recovery_codes.wrap_*`) and every verification-hash column (`recovery_codes.hashed_code`, `totp_backup_codes.hashed_code`).

These need to be locked before any migration is written so the schema is uniform.

## Options considered

### 1. Primary key

- **A. UUIDv4 app-side (`Guid.NewGuid()`).** Random. Scatters across B-tree pages — measurable insert overhead at high write volume, irrelevant at thesis scale. Stored as `uuid`.
- **B. UUIDv7 app-side (`Guid.CreateVersion7()`).** Timestamp-prefixed UUID, built into `System.Guid` since .NET 9 (we're on .NET 10). Time-ordered → B-tree locality. Opaque IDs in URLs. ID available before INSERT.
- **C. UUID generated DB-side via `gen_random_uuid()`.** Postgres-internal. EF Core wires `HasDefaultValueSql` + `ValueGeneratedOnAdd`. INSERT…RETURNING round-trips the generated ID back. Cannot reference the ID before `SaveChangesAsync` flush.
- **D. `bigint identity` auto-increment.** Smallest, fastest, best index locality. Enumerable URLs (`/clouds/1`, `/clouds/2`) — loses URL opacity.
- **E. Composite — internal `bigint identity` + external `uuid public_id`.** Best of both at the cost of one extra column per table. Over-engineered for thesis scale.

### 2. Timestamps

- **A. `DateTime` (Kind=Utc discipline) → `timestamp`.** Common in legacy .NET. Easy to mix `Local` and `Utc` since both share the `DateTime` type.
- **B. `DateTime` (Kind=Utc) → `timestamptz`.** Npgsql 6+ default. EF Core throws if you try to save a `DateTime` with `Kind != Utc` — loud footgun, not silent. Most-used modern .NET pattern.
- **C. `DateTimeOffset` → `timestamptz`.** Carries an offset in C# memory, but Postgres normalizes to UTC on write. The offset round-trip is a lie.
- **D. NodaTime `Instant` → `timestamptz` via `Npgsql.NodaTime`.** Type-honest: `Instant` is by construction a UTC moment. Adds NodaTime as a NuGet dependency. Same author as Npgsql.

### 3a. AES-GCM ciphertext layout

`AesGcm.Encrypt` produces three byte sequences: **nonce** (12 bytes), **ciphertext** (|plaintext|), **authentication tag** (16 bytes).

- **(i) Three separate columns**: `*_ciphertext bytea`, `*_nonce bytea`, `*_tag bytea`. Maps 1:1 to .NET's API.
- **(ii) Combined single bytea**: `nonce || ciphertext || tag` packed into one column. Slice by known lengths on read; fragile if tag length ever changes (we'd be locked to 16 bytes forever).
- **(iii) Two columns**: nonce alone + `ciphertext || tag` combined. Middle ground.

### 3b. Argon2id-hashed verification token

- **(i) PHC string format in a single `text` column.** Industry standard: `$argon2id$v=19$m=65536,t=3,p=4$<base64-salt>$<base64-hash>`. Self-contained.
- **(ii) Separate columns**: `hash bytea`, `salt bytea`, `argon2_params jsonb`, `algorithm_version smallint`. More explicit.
- **(iii) Raw Konscious output bytes** in a `bytea` column + separate params. Lightest weight but no standard format.

### 3c. Algorithm/key versioning columns

- **(i) Add `*_algorithm_version smallint NOT NULL DEFAULT 1`** per encrypted blob.
- **(ii) Encode version in the master key itself** (keyfile contains `(version, key_bytes)` pairs).
- **(iii) Don't bother for MVP.** Add the column later if rotation is ever needed.

## Decision

- **PK: B** — UUIDv7 app-side via `Guid.CreateVersion7()`, mapped to Postgres `uuid` column. Default set in entity constructor.
- **Timestamp: D** — NodaTime `Instant` mapped to `timestamptz` via `Npgsql.NodaTime`. `IClock` (from [[0028-schema-conventions]]) is the only source of "now" in the app.
- **AES-GCM ciphertext: (i) three columns** — `*_ciphertext bytea`, `*_nonce bytea`, `*_tag bytea`.
- **Argon2id verification: (i) PHC string** in `text` column.
- **Algorithm versioning: (iii) skip** for MVP.

### Concrete shape

```csharp
// Every entity:
public sealed class Cloud : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Instant CreatedAt { get; init; }            // set via injected IClock at construction
    public Instant UpdatedAt { get; set; }             // set by SaveChangesInterceptor
    public Instant? DestroyedAt { get; set; }          // lifecycle timestamp; see [[0032-fk-cascades-and-soft-delete]]
    // ...
}

// Encrypted blob — three columns per AES-GCM ciphertext:
public sealed class EncryptedProviderToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string Provider { get; init; } = null!;
    public byte[] Ciphertext { get; set; } = null!;
    public byte[] Nonce { get; set; } = null!;
    public byte[] Tag { get; set; } = null!;
    // ...
}

// PHC-format hash:
public sealed class RecoveryCode
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string HashedCode { get; init; } = null!;   // "$argon2id$v=19$m=65536,t=3,p=4$<salt>$<hash>"
    public byte[] WrapArgon2Salt { get; init; } = null!;
    public JsonDocument WrapArgon2Params { get; init; } = null!;
    public byte[] WrappedDek { get; init; } = null!;
    public byte[] WrapNonce { get; init; } = null!;
    public byte[] WrapTag { get; init; } = null!;
    public Instant? UsedAt { get; set; }
    public Instant CreatedAt { get; init; }
}
```

Program.cs wiring (combined with [[0028-schema-conventions]]):

```csharp
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddDbContext<PortalDbContext>(opts => opts
    .UseNpgsql(cs, npg => npg.UseNodaTime())
    .UseSnakeCaseNamingConvention());
```

A small `PhcEncoder` helper (~40 LOC) formats and parses PHC strings from Konscious's `Argon2id` output, since Konscious doesn't emit PHC natively.

## Consequences

- **Positive:**
  - UUIDv7 gives B-tree index locality with opaque URLs and app-side ID-before-INSERT for cross-row references in one `SaveChangesAsync` (e.g., create cloud + enqueue provisioning_job in one transaction).
  - `Instant` honestly models `timestamptz`; `DateTime.Now` accidents become impossible (no `Local` kind to confuse with `Utc`).
  - Tests inject `FakeClock` for deterministic lease-expiration and recovery-expiry logic.
  - PHC strings are self-contained and parseable by external Argon2 tools (Python, Go, CLI `argon2` binary). Future Argon2 work-factor upgrades automatically version themselves inside the string.
  - Three-column AES-GCM layout maps 1:1 to `AesGcm.Encrypt(nonce, plaintext, ciphertext, tag)` — no `Span.Slice` arithmetic on read.
- **Negative:**
  - NodaTime adds one NuGet dependency and a small learning curve for `IClock`, `Instant`, `Duration` (no `DateTime.AddHours` muscle memory).
  - Konscious doesn't emit PHC natively, so a ~40 LOC `PhcEncoder` helper is part of the auth implementation.
- **Neutral:**
  - Adding `*_algorithm_version` later is one additive migration: `ADD COLUMN algorithm_version smallint NOT NULL DEFAULT 1`. No corner painted.
  - UUIDv7 is the IETF-standardized successor to v4 (RFC 9562, 2024); future Postgres versions are expected to support `uuid_generate_v7()` natively but we don't need it.

## Related

- [[0028-schema-conventions]] — `IClock` registration, audit conventions, naming
- [[0030-auth-flow]] — passphrase / recovery code crypto wiring uses these column shapes
- [[0024-dbcontext-shape]] — single `PortalDbContext` houses all entities under these conventions
