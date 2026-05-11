# System Reconciliation And Mismatches

Date: 2026-05-11

Purpose: piece together the current thesis system and identify mismatches between the older second-brain architecture and the newer medical knowledge-base direction.

## Current Best Whole Picture

The system should now be understood as:

> A cloud service for brain MRI research data aggregation, machine-learning processing, provenance tracking, and Markdown knowledge-base generation.

End-to-end flow:

```text
Avalonia Desktop Client
  -> imports dataset/case/artifact folders
  -> previews local files and metadata
  -> performs lightweight privacy/preflight checks
  -> uploads selected artifacts to cloud

ASP.NET Core Cloud API
  -> authenticates user
  -> stores raw artifacts
  -> creates processing jobs
  -> exposes job, note, artifact, and provenance APIs

.NET Worker / Processor Router
  -> selects dataset adapter
  -> selects configured processors
  -> runs deterministic feature extraction
  -> calls ML processors
  -> generates structured outputs

ML Processor Layer
  -> NB baseline
  -> ICA + NB baseline
  -> trained DNN or small CNN
  -> optional segmentation/evidence processor

Knowledge Generator
  -> creates Markdown notes
  -> creates JSON provenance sidecars
  -> creates evidence files such as overlays/thumbnails

Obsidian Vault / Plugin
  -> reads generated Markdown
  -> shows dataset/case/model/evidence notes
  -> supports human edits and review queue
```

## Recommended Final Thesis Claim

Use:

> A cloud-based system for aggregating heterogeneous brain MRI research datasets, processing them with configurable machine-learning processors, and organizing results as provenance-aware Markdown knowledge bases.

Avoid:

> A generic second brain for all personal data.

Avoid:

> A clinical diagnostic system.

Avoid:

> A universal biomedical AI platform.

## Core Architecture Decision

Use a processor-router architecture:

```text
Artifact
  -> Dataset Adapter
  -> Processor Router
  -> Processor(s)
  -> Structured Output
  -> Markdown Note
  -> JSON Provenance
  -> Obsidian Knowledge Base
```

The router is rule/config based, not an autonomous agent.

Example:

```yaml
routes:
  lgg_case:
    processors:
      - lgg_adapter
      - mask_feature_extractor
      - nb_classifier
      - ica_nb_classifier
      - dnn_classifier
      - case_note_generator
      - provenance_writer

  utsw_case:
    processors:
      - utsw_adapter
      - nifti_metadata_extractor
      - segmentation_feature_extractor
      - marker_classifier
      - case_note_generator
      - provenance_writer
```

## Mismatches To Fix

### 1. Tauri/Svelte vs Avalonia

Mismatch:

- Older notes still describe a Tauri/Svelte desktop agent.
- The current decision is Avalonia/.NET.

Fix:

- Replace MVP desktop stack with Avalonia.
- Keep Tauri only as rejected/earlier option if needed.

Correct wording:

> The desktop client is implemented with Avalonia/.NET to reduce learning overhead and align with the ASP.NET Core backend.

### 2. Generic Context Capture vs Medical Dataset Import

Mismatch:

- Older plan centers on browser capture, screenshots, voice messages, Slack/Telegram, and general second-brain ingestion.
- Current thesis centers on MRI datasets and biomedical research artifacts.

Fix:

- MVP input should be dataset/case/artifact import.
- Browser capture becomes optional literature/protocol capture, not core.
- Native capture becomes file/folder ingestion and local preview, not general OS capture.

Correct MVP:

```text
dataset/case folder import
  -> artifact preview
  -> cloud upload
  -> model processing
  -> generated medical KB notes
```

### 3. "Cloud Service For Markdown Data" Is Too Generic

Mismatch:

- The claim "cloud service for aggregation and organization of data in Markdown format" is technically true but too broad.

Fix:

- Add domain and processing specificity.

Better claim:

> Cloud service for brain MRI research data aggregation, machine-learning processing, and Markdown knowledge-base generation.

### 4. One Model vs Router

Mismatch:

- Some wording implies one trained model at the center.
- Current architecture should compare multiple processors and route artifacts.

Fix:

- The system is not locked to one model.
- NB and ICA+NB are baseline processors.
- The trained DNN is the main modern comparator/production processor.
- Optional processors can be added later.

Correct model hierarchy:

```text
NB baseline
ICA + NB baseline
trained DNN / small CNN
optional segmentation/evidence processor
optional medical LLM note enricher
```

### 5. LGG Task vs UTSW Task

Mismatch:

- LGG supports simple abnormal-slice classification from masks.
- UTSW supports patient-level molecular-marker/tumor-grade tasks.
- These are not the same label space.

Fix:

- Do not claim one shared classifier across both datasets.
- Use dataset-specific tasks under one common knowledge-base schema.

Correct framing:

> The system demonstrates heterogeneous dataset support, not one universal model trained across all datasets.

Recommended tasks:

- LGG: abnormal slice/case classification using mask presence and mask-derived features.
- UTSW: patient-level marker/tumor attribute prediction or segmentation-derived case summary, depending on metadata completeness.

### 6. Several Datasets vs Bachelor Scope

Mismatch:

- Several datasets make the system stronger but can explode implementation scope.

Fix:

- Implement two dataset adapters at most:
  - LGG full lightweight pipeline.
  - UTSW subset serious pipeline.
- Keep BRISC as fallback.
- Keep UCSF-PDGM, BraTS, UCSD-PTGBM as related work/future work.

### 7. DICOM Claims vs Actual Data

Mismatch:

- Some architecture notes mention DICOM.
- LGG is TIFF.
- UTSW is NIfTI.

Fix:

- Do not claim DICOM support in MVP unless actually implemented.
- Say "MRI image/research artifacts" or "NIfTI/TIFF MRI artifacts."
- DICOM can be future work.

### 8. Medical LLMs vs Deterministic Notes

Mismatch:

- MedGemma/MedASR and other medical LLMs are discussed.
- The core MVP does not need them.

Fix:

- Use deterministic note templates first.
- Use MedGemma only as optional note-enrichment stretch.
- Do not rely on an LLM for labels, diagnosis, or findings.

### 9. Obsidian Plugin vs Plain Vault Output

Mismatch:

- Older plan assumes an Obsidian plugin applies write plans.
- For MVP, a plugin may be extra work.

Fix:

- Minimum viable integration can be writing Markdown into an Obsidian vault folder.
- Plugin becomes stretch for review queue/status UI.

Correct MVP wording:

> The system produces an Obsidian-compatible Markdown vault. A plugin may be implemented for review/status workflows if time allows.

### 10. Cloud Queue vs Local Queue

Mismatch:

- Older notes say queue should live in cloud, but desktop also has local journal.

Fix:

- Keep both with different responsibilities:
  - local queue/journal: pending upload and offline resilience.
  - cloud queue: durable processing state.

### 11. Privacy Model vs Public Research Datasets

Mismatch:

- Original system emphasizes personal/private notes.
- Current implementation mostly uses public research datasets.

Fix:

- Keep privacy/security as architecture quality attributes.
- Do not overclaim real patient privacy handling unless using private data.
- For public datasets, emphasize license, citation, provenance, access control, and safe storage.

### 12. GPepT / Chemistry Expansion vs MRI Thesis

Mismatch:

- GPepT is useful conceptually but belongs to medicinal chemistry, not MRI.

Fix:

- Mention it only as future extensibility evidence for artifact adapters/processors.
- Do not include peptide generation in MVP.

## Correct MVP Boundary

Implement:

- Avalonia desktop importer.
- ASP.NET Core backend.
- Cloud processing queue.
- Processor router.
- LGG adapter.
- UTSW adapter for a controlled subset.
- NB and ICA+NB processors.
- Trained DNN or small CNN processor.
- Markdown note generator.
- JSON provenance writer.
- Evidence file output.
- Obsidian-compatible vault structure.
- Docker Compose deployment.
- Terraform for two VPS providers.

Do not implement unless time remains:

- full Obsidian plugin.
- browser extension.
- medical LLM enrichment.
- DICOM import.
- GPepT/chemistry pipeline.
- full multi-dataset training harmonization.
- Kubernetes.
- general second-brain capture.

## Revised Vault Shape

```text
MedicalKB/
  00_Index/
    Home.md
    Review Queue.md
    Model Registry.md

  01_Datasets/
    LGG MRI Segmentation.md
    UTSW Glioma 2026.md

  02_Cases/
    LGG/
    UTSW/

  03_Artifacts/
    images/
    nifti/
    masks/

  04_Model_Runs/
    2026-05-11_lgg_nb_ica.md
    2026-05-11_utsw_dnn_subset.md

  05_Evidence/
    overlays/
    thumbnails/
    feature_tables/

  06_Literature/
    dataset-papers/
    model-papers/

  07_Protocols/
    LGG Preprocessing.md
    UTSW Preprocessing.md
    Evaluation Protocol.md

  .provenance/
    datasets/
    cases/
    model-runs/
```

## Final Clean Mental Model

The system is not centered on Obsidian, a model, or a dataset. It is centered on this transformation:

```text
biomedical research artifact
  -> normalized object
  -> configured processors
  -> auditable result
  -> Markdown knowledge object
```

That is the stable architecture.

