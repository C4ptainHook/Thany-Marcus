# Final Expanded Project Plan

Date: 2026-05-12

## 1. Final Thesis Direction

The project is a secure cloud service for aggregating heterogeneous medical artifacts, extracting insights with specialized processors, and generating provenance-aware Markdown patient dossiers.

The core transformation is:

```text
messy medical evidence
  -> normalized artifact records
  -> modality-specific processors
  -> structured insights
  -> Markdown patient dossier
  -> JSON provenance
  -> shareable hierarchical vault
```

This direction moves away from an MRI-only thesis. MRI can remain an optional artifact type, but the real contribution is broader: organizing mixed medical evidence into safe, readable, traceable patient dossiers.

## 2. Working Title

Primary:

> **A Processor-Router Architecture for Multimodal Medical Artifact Insight Extraction and Hierarchical Markdown Patient Dossier Generation**

Simpler:

> **A Secure Cloud Service for Multimodal Medical Artifact Aggregation and Provenance-Aware Markdown Patient Dossiers**

Use the primary title if the thesis should sound technical. Use the simpler title if the department prefers understandable product/system titles.

## 3. Problem Statement

Medical information is often fragmented across many artifact types:

- PDF reports
- scans/photos of paper records
- handwritten notes
- lab result images
- imaging screenshots
- MRI/ultrasound/echo outputs
- heart and lung sound recordings
- short clinical videos
- casual voice recordings

The problem is not just "lack of data." The stronger formulation is:

> Medical evidence is heterogeneous, fragmented, unstructured, difficult to index, difficult to share safely, and difficult to turn into a coherent patient-facing or clinician-facing record.

The project addresses this by preserving original artifacts, extracting structured metadata/insights, and producing a linked Markdown dossier that remains readable outside the application.

## 4. What The System Is

It is:

- a medical artifact aggregation system
- a secure cloud processing pipeline
- a processor-router architecture for specialized models
- a provenance-aware Markdown generator
- a hierarchical patient dossier/vault generator
- a research/review support prototype

It is not:

- an EHR replacement
- a clinical diagnostic device
- a hospital PACS/VNA platform
- a full Flywheel competitor
- an autonomous CORAL-style research-agent platform
- a generic second brain
- a universal medical AI model

This boundary is important because the system handles medical artifacts but should not claim autonomous clinical judgment.

## 5. Main Contribution

The novelty is not that the system stores files. The contribution is the combination of:

1. Heterogeneous medical artifact ingestion.
2. Processor-router architecture for modality-specific processing.
3. Strong specialized models used only where appropriate.
4. Markdown patient dossier generation.
5. JSON provenance and audit trail for reproducibility.
6. Hierarchical vault structure where each patient dossier can be exported/shared separately.

The strongest claim:

> The system turns mixed medical artifacts into structured, traceable, shareable Markdown patient dossiers while preserving provenance and separating source data, extracted content, model insights, and human review.

## 6. End-To-End Architecture

```text
Avalonia Desktop Client
        |
        | HTTP/REST upload and job requests
        v
ASP.NET Core Cloud API
        |
        | durable processing jobs
        v
.NET Worker + Processor Router
        |
        | modality-specific processors
        v
Specialized Model / Extraction Layer
        |
        | structured outputs
        v
Knowledge Generator
        |
        | Markdown + JSON provenance + evidence files
        v
Hierarchical Vault Store
        |
        | export/share
        v
Patient / Clinician / Researcher Reader
```

The system is cloud-centered because artifact storage, processing jobs, backup, and sharing require a durable backend. The desktop client is an ingestion/review helper, not the main storage authority.

## 7. Desktop Client

Technology:

- Avalonia UI
- .NET

Responsibilities:

- select patient/case folders
- preview detected artifacts
- show file counts, sizes, types, and warnings
- perform lightweight preflight checks
- upload selected artifacts
- show job status
- export/open generated dossier folders

Why Avalonia:

- aligns with C#/.NET skillset
- avoids Rust/Tauri learning cost
- integrates naturally with ASP.NET contracts
- sufficient for file import, preview, queue, and settings UI

Not responsible for:

- heavy model inference in MVP
- secure long-term storage
- final access-control decisions
- full medical note editing
- replacing Obsidian

## 8. Cloud Backend

Technology:

- ASP.NET Core API
- PostgreSQL
- filesystem-backed artifact storage on VPS volume for MVP
- Caddy reverse proxy
- Docker Compose
- Terraform deployment

Responsibilities:

- authentication
- artifact upload
- patient/case registry
- artifact metadata store
- job creation and status
- secure artifact storage
- note/provenance APIs
- audit logs
- dossier export/share API

Candidate MVP endpoints:

```text
POST /patients
POST /cases
POST /artifacts/upload
POST /jobs
GET  /jobs/{id}
GET  /patients/{id}/dossier
GET  /notes/{id}
GET  /provenance/{id}
POST /exports/patient/{id}
```

## 9. Processor Router

The processor router is the architectural center.

It decides which adapters and processors run for each artifact type. It should be deterministic/config-driven for MVP, not autonomous AI.

Example:

```yaml
routes:
  pdf_report:
    processors:
      - pdf_text_extractor
      - document_type_classifier
      - medical_metadata_extractor
      - report_note_generator
      - provenance_writer

  scanned_document_image:
    processors:
      - image_metadata_extractor
      - ocr_processor
      - document_type_classifier
      - dossier_note_generator
      - provenance_writer

  heart_sound_recording:
    processors:
      - audio_metadata_extractor
      - spectrogram_generator
      - heart_sound_classifier
      - audio_insight_note_generator
      - provenance_writer

  short_medical_video:
    processors:
      - video_metadata_extractor
      - keyframe_extractor
      - frame_ocr_processor
      - video_artifact_note_generator
      - provenance_writer
```

Why this matters:

- avoids locking the system to one model
- lets each modality use a specialized processor
- makes future processors easy to add
- keeps the thesis evaluable because every route is explicit

## 10. Processing Levels

Not every artifact should get deep AI processing. Use levels:

```text
Level 0: Store original + hash + provenance
Level 1: Extract generic metadata
Level 2: Extract generic content: OCR, text, transcript, keyframes
Level 3: Run specialized medical model
Level 4: Human-reviewed insight
```

This prevents overclaiming. Every artifact is organized, but only selected artifact types receive specialized model insight.

## 11. Artifact Types

### Documents

Examples:

- PDF reports
- discharge summaries
- referral letters
- lab reports

Processors:

- PDF text extraction
- document type classification
- date/source/doctor extraction
- report note generation

Why important:

- most patient histories contain documents
- easiest way to demonstrate patient dossier value

### Scanned / Photographed Documents

Examples:

- handwritten notes
- photographed prescriptions
- photographed printed results
- paper forms

Processors:

- image metadata extraction
- OCR
- handwriting OCR if feasible
- document type classifier
- artifact note generator

Why important:

- real patient evidence is often captured as photos
- good fit for artifact aggregation

### Medical Images / Pictures

Examples:

- wound photo
- ultrasound screenshot
- ECG chart photo
- lab sheet photo
- imaging screenshot

MVP processing:

- metadata
- thumbnail
- OCR if text-heavy
- artifact note

Specialized diagnosis from images should be optional and carefully scoped.

### Medical Audio

Examples:

- heart sounds
- lung sounds
- cough/breath recordings
- doctor voice memo
- patient symptom recording

Processors:

- audio metadata
- spectrogram generation
- heart or lung classifier
- speech-to-text for casual recordings
- audio insight note generator

Recommended first specialized model:

- heart sounds with CirCor/PhysioNet 2022, or
- lung sounds with ICBHI 2017

### Video

Examples:

- short echo clip
- ultrasound clip
- patient symptom video
- clinical screen recording

MVP processing:

- duration/resolution/codec
- keyframes
- contact sheet
- OCR on frames
- optional audio extraction
- video artifact note

Do not make diagnostic video understanding core unless the thesis pivots specifically to that.

## 12. Specialized Model Strategy

The system should use strong specialized models, but not one universal model.

Recommended MVP model strategy:

- one specialized medical-audio classifier
- OCR/text extraction for documents/images
- video keyframe/OCR indexing
- optional document classifier

Possible specialized model examples:

- heart murmur classifier
- lung crackle/wheeze classifier
- document type classifier
- table/lab-result extractor
- image classifier for one carefully chosen artifact type

Medical LLMs may be used later for note enrichment, but should not be the source of clinical truth.

## 13. Knowledge Generation

The output is not a chatbot answer. The output is a structured knowledge base.

Generated artifacts:

- Markdown patient dossier
- Markdown artifact notes
- Markdown insight notes
- Markdown timeline
- Markdown review queue
- JSON provenance records
- evidence files: thumbnails, spectrograms, keyframes, OCR text, extracted tables

Markdown is for human reading.

JSON provenance is for audit, regeneration, consistency checks, and reproducibility.

## 14. Hierarchical Vault Design

The vault should be hierarchical:

```text
MedicalKB/
  00_Registry/
    Patients.md
    Artifact Types.md
    Model Registry.md

  Patients/
    patient_001/
      Home.md
      Timeline.md
      Summary.md
      Artifacts/
      Insights/
      Reports/
      Evidence/
      .provenance/

    patient_002/
      Home.md
      Timeline.md
      Summary.md
      Artifacts/
      Insights/
      Reports/
      Evidence/
      .provenance/
```

Each patient folder is a dossier/subvault candidate.

Owner view:

- sees all patients/cases
- sees model registry and processing history
- can export selected patient dossier

Patient view:

- sees only their own dossier
- can read Markdown in Obsidian or another Markdown reader
- can inspect source artifacts and model limitations

## 15. Sharing Model

Do not use Obsidian as the access-control layer.

MVP sharing:

```text
export patient dossier folder/zip
  -> contains Markdown, evidence files, provenance
  -> patient opens it in Obsidian or reads Markdown directly
```

Stretch sharing:

```text
secure patient portal
  -> authenticated read-only dossier
  -> revocable access
  -> audit logs
  -> rendered Markdown
```

Why:

- Obsidian URI only opens local vaults/files
- Obsidian Sync is not a fine-grained medical sharing system
- medical access control must belong to your backend

## 16. Security And Privacy

MVP security requirements:

- authenticated backend access
- encrypted HTTPS traffic
- artifact hashes
- audit log for uploads, processing, exports
- separation of owner vault and patient dossiers
- export only selected patient subtree
- clear model-output limitations

Important thesis boundary:

> The system is a research/review prototype, not a production clinical record system.

Do not overclaim HIPAA/GDPR compliance unless implemented and assessed.

## 17. Deployment

MVP deployment:

- Docker Compose
- VPS
- PostgreSQL
- API container
- worker container
- Python processor container
- filesystem artifact volume
- Caddy reverse proxy
- Terraform for two providers

Providers:

- Hetzner
- DigitalOcean

Not MVP:

- Kubernetes
- enterprise object storage
- multi-node scaling
- managed database dependency

## 18. Evaluation Plan

Evaluate the system, not just a model.

Possible metrics:

- artifact type detection accuracy
- OCR/text extraction completeness
- specialized model performance on chosen public dataset
- provenance completeness
- dossier generation success rate
- retrieval time for finding an artifact/insight
- export correctness: patient dossier contains only selected patient data
- security checks: unauthorized access blocked

For specialized model:

- use dataset-specific accuracy/F1/AUC as appropriate
- report limitations and uncertainty

For dossier quality:

- measure whether required sections are generated:
  - summary
  - timeline
  - artifacts
  - insights
  - provenance
  - limitations

## 19. MVP Scope

Implement:

- Avalonia importer
- ASP.NET Core API
- PostgreSQL metadata store
- local VPS artifact storage
- durable job table/queue
- processor router
- PDF processor
- image/OCR processor
- one specialized audio processor
- video metadata/keyframe processor if time allows
- Markdown dossier generator
- JSON provenance writer
- patient dossier export
- Docker Compose
- Terraform for Hetzner and DigitalOcean

Defer:

- full Obsidian plugin
- real-time sync
- full EHR features
- DICOM/PACS integration
- deep diagnostic video understanding
- multiple specialized medical models
- medical LLM as core reasoning engine
- Flywheel-style research platform
- CORAL-style autonomous agents
- Kubernetes

## 20. One-Week Discovery Plan

Day 1:

- choose primary specialized model domain: heart sounds or lung sounds
- inspect dataset and labels
- define artifact schema

Day 2:

- prototype patient dossier folder structure
- generate sample Markdown notes manually or with simple templates

Day 3:

- implement PDF/image metadata extraction prototype
- test OCR on scanned/photographed documents

Day 4:

- run baseline specialized audio model or dataset preprocessing
- generate spectrogram/evidence files

Day 5:

- implement processor-router mock flow
- generate Markdown + JSON provenance from processor outputs

Day 6:

- wire minimal backend job flow:
  - upload artifact
  - create job
  - process
  - write dossier

Day 7:

- export patient dossier folder
- open in Obsidian
- check security/provenance boundaries
- lock final thesis scope

## 21. Final Current Decision

The best plan is:

> Build a multimodal medical artifact dossier system, not an MRI system and not an audio-only system.

The MVP should prove:

1. Heterogeneous artifact ingestion.
2. Processor-router execution.
3. At least one strong specialized medical model.
4. Markdown patient dossier generation.
5. JSON provenance.
6. Hierarchical patient subvault export.

