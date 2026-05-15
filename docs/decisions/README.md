# Architecture Decision Records

This directory captures load-bearing design decisions for Thany-Marcus. Each record explains the *why* alongside the *what* so a future reader (including future-self and AI agents) can judge whether a decision still applies, needs revisiting, or has been superseded.

## Format

Each ADR uses a single Markdown file: `NNNN-short-kebab-title.md`. Structure:

```
# ADR-NNNN: Title

Status: Proposed | Accepted | Superseded by ADR-MMMM | Deprecated
Date: YYYY-MM-DD
Supersedes: ADR-XXXX (if any)

## Context
What problem are we solving and what forced the decision.

## Options considered
- **A. Name** — short pros/cons
- **B. Name** — short pros/cons
- (etc.)

## Decision
What we chose and the shape of the implementation.

## Consequences
- Positive: ...
- Negative: ...
- Neutral: ...

## Related
- [[adr-NNNN-...]] (cross-links to other ADRs)
- Source-of-truth plan section (e.g., `plans/cloud-pivot-plan-2026-05-13.md §27`)
```

## Numbering

- **0001–0016**: reserved for the 2026-05-13 morning grilling (Q1–Q16). Planned in `plans/consolidated-plan-2026-05-13.md`; written when convenient.
- **0017–0027**: the 2026-05-15 PORTAL-001 grilling that locked the portal architecture, tooling, and scaffold layout. Written before any code shipped — they shape PORTAL-001 itself.
- **0028–0032**: the 2026-05-15 PORTAL-002 schema grilling. Lock the foundational conventions, type mappings, auth flow, rate limiting, and FK rules that PORTAL-002 ships.

## Index

| ID | Title | Status |
|---|---|---|
| 0017 | Portal.Web stack — SvelteKit + adapter-static SPA | Accepted |
| 0018 | Portal API style — Minimal APIs + Vertical Slice Architecture | Accepted |
| 0019 | Background work + saga durability — Postgres job queue, no event sourcing | Accepted |
| 0020 | Server-push transport — SSE | Accepted |
| 0021 | Local dev workflow — two terminals + Vite proxy | Accepted |
| 0022 | Solution scope and project layout | Accepted |
| 0023 | Test stack — WebApplicationFactory + Testcontainers + xUnit v3 + Shouldly | Accepted |
| 0024 | DbContext shape — single PortalDbContext + per-feature configs | Accepted |
| 0025 | OpenAPI + Scalar + hand-written TS contracts | Accepted |
| 0026 | Observability + health checks — OTel + Prometheus + /health/{live,ready} | Accepted |
| 0027 | Reverse proxy — Caddy | Accepted |
| 0028 | Schema conventions — migration, test isolation, naming, audit | Accepted |
| 0029 | Type mappings — primary key, timestamp, crypto column shapes | Accepted |
| 0030 | Auth flow — Google SSO + cookie + TOTP + step-up | Accepted |
| 0031 | Rate limiting + persistent lockout + CAPTCHA | Accepted |
| 0032 | FK cascade rules + soft-delete on clouds | Accepted |
