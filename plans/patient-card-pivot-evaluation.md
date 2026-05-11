# Patient Card / Medical Record Aggregation Pivot Evaluation

Date checked: 2026-05-12

Prompt:

> Medics face data sparsity in the form of patient recordings, MRI scans, echo results, handwriting, and other artifacts. It is hard to maintain, keep safe, secure, and accessible a patient card.

## Strong Evaluation

This idea has real potential, but the raw version is too large and too regulated for a bachelor MVP.

The important correction:

> The problem is not only data sparsity. It is fragmented, heterogeneous, unstructured, poorly indexed, and hard-to-share patient evidence.

The valuable thesis direction is not "replace the EHR." The valuable direction is:

> a secure patient evidence dossier that aggregates heterogeneous medical artifacts, extracts metadata, generates structured Markdown summaries, and keeps provenance/audit records.

## Potential Score

Potential:

- high as a real-world problem
- medium-high as a bachelor thesis if narrowed
- low if framed as a full clinical patient card/EHR replacement

Overall:

> 8/10 problem potential, 5/10 implementation feasibility if unrestricted, 8/10 feasibility if narrowed to artifact aggregation and Markdown dossier generation.

## Why It Is Strong

Medical evidence is often spread across:

- EHR entries
- hospital portals
- PDFs
- scans/photos of paper documents
- handwritten notes
- lab reports
- imaging studies
- echo reports
- audio/video recordings
- patient-provided files

Clinicians often need a compact view:

- What artifacts exist?
- Where did they come from?
- Which patient/case do they belong to?
- What is their date/type/source?
- What was extracted from them?
- What needs review?
- Can the original artifact be retrieved?

This maps well to the existing architecture:

```text
medical artifact
  -> adapter/extractor
  -> processor router
  -> structured metadata
  -> Markdown patient dossier
  -> JSON provenance
  -> secure cloud storage
```

## Existing Standards Support The Idea

FHIR already has concepts that match the system:

- `DocumentReference` for documents, notes, scanned paper, images, video, audio, PDFs, and other binary objects.
- `ImagingStudy` for DICOM imaging study metadata.
- `DiagnosticReport` for report-style clinical results.
- `Provenance` for tracking origin and history of records.

This means the idea is not strange. It is close to established healthcare data modeling.

Sources:

- FHIR DocumentReference: https://fhir.hl7.org/fhir/documentreference.html
- FHIR ImagingStudy: https://hl7.org/fhir/imagingstudy.html
- FHIR DiagnosticReport: https://hl7.org/fhir/R4/diagnosticreport.html
- FHIR Provenance: https://hl7.org/fhir/provenance.html

## Existing Product Overlap

This overlaps with:

- EHRs
- personal health record apps
- patient portals
- PACS/VNA imaging archives
- research imaging platforms such as Flywheel
- document-management systems

Examples:

- KeepMD positions itself as a privacy-first personal health record for organizing records and scanned paperwork.
- Andaman7 is a personal health record platform with privacy/security and hospital/lab/device connections.
- Flywheel handles imaging research data management and AI workflows at enterprise scale.

Sources:

- https://keepmd.org/
- https://play.google.com/store/apps/details?id=com.andaman7.android
- https://flywheel.io/flywheel-is-research-data-management/

## Where This System Can Still Differentiate

The system should not compete as a full EHR or PHR.

Differentiation:

- self-hosted/user-owned deployment
- Markdown-first medical dossier
- artifact provenance as first-class output
- configurable processor router
- Obsidian-compatible human review
- suitable for research/small-clinic/student workflow
- designed around heterogeneous artifacts, not only structured EHR fields

Good positioning:

> a secure medical artifact-to-dossier pipeline, not a hospital EHR.

## Strongest Narrow MVP

Do not start with every artifact type.

Best MVP:

```text
Patient/case folder
  -> import PDFs/images/MRI-derived outputs
  -> extract metadata
  -> classify artifact type
  -> generate patient dossier Markdown
  -> create JSON provenance
  -> store originals securely
  -> show review queue
```

MVP artifact types:

- PDF report
- scanned/photographed document
- MRI image/result from previous pipeline
- handwritten note image

Stretch:

- echo report parsing
- voice recording transcription
- DICOM imaging studies
- FHIR export
- role-based clinician sharing

## Relation To Current MRI Research KB Plan

There are two possible paths.

### Path A: Keep MRI Research KB As Core

Use patient-card idea as future generalization.

Thesis remains:

> brain MRI research data aggregation and Markdown knowledge-base generation.

Patient-card framing becomes:

> the same architecture could support broader patient artifact dossiers.

Best if time is short.

### Path B: Pivot To Patient Evidence Dossier

Thesis becomes:

> secure cloud service for aggregating heterogeneous patient medical artifacts into provenance-aware Markdown dossiers.

MRI processing becomes one processor among several.

Best if the user wants broader practical healthcare relevance.

Risk:

- bigger product surface
- more overlap with PHR/EHR tools
- stronger privacy/regulatory expectations
- harder to evaluate without real clinical users/data

## Recommended Thesis Framing If Pivoting

Use:

> A secure cloud service for aggregating heterogeneous patient medical artifacts and generating provenance-aware Markdown clinical research dossiers.

Even safer:

> A cloud-based prototype for organizing heterogeneous medical artifacts into provenance-aware Markdown patient dossiers for research and review workflows.

Avoid:

> clinical patient card replacement

Avoid:

> diagnostic system

Avoid:

> EHR replacement

## Architecture If Pivoting

```text
Avalonia Desktop Client
  -> patient/case folder import
  -> local artifact preview
  -> privacy/preflight checks

ASP.NET Core Cloud API
  -> secure upload
  -> artifact storage
  -> job queue
  -> access/audit logs

Processor Router
  -> PDF extractor
  -> OCR/handwriting processor
  -> MRI processor
  -> report metadata extractor
  -> dossier note generator

Knowledge Store
  -> originals
  -> extracted metadata
  -> Markdown dossiers
  -> JSON provenance
  -> evidence links

Obsidian-Compatible Vault
  -> patient/case dossier
  -> review queue
  -> artifact timeline
```

## Major Risks

### Clinical And Regulatory Scope

If the system is for real patient care, expectations become much higher:

- HIPAA/GDPR-style privacy
- audit logs
- access control
- encryption
- consent
- retention/deletion policy
- emergency access
- data integrity
- liability

Mitigation:

- frame as research/review prototype
- use public/synthetic/de-identified data
- do not claim clinical deployment

### Too Many Modalities

MRI, echo, handwriting, audio, PDFs, and patient records are each large enough.

Mitigation:

- MVP uses artifact classification and metadata extraction
- deep parsing is optional per artifact type

### Evaluation Difficulty

It is hard to prove usefulness without clinicians.

Mitigation:

- evaluate technical metrics:
  - artifact type classification accuracy
  - metadata extraction completeness
  - successful provenance creation
  - retrieval time
  - generated dossier completeness
  - security/access-control checks

## Final Verdict

This has potential, but only if narrowed.

Best version:

> Secure medical artifact aggregation and Markdown dossier generation.

Do not build:

> a full patient card or EHR.

Recommended decision:

> Keep the current MRI research KB as the core implemented thesis, but explicitly design the architecture as a patient-artifact dossier system. If the MRI pipeline works, expand the demo with PDFs/scanned notes as additional artifact processors.

