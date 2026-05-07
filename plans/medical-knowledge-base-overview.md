# Medical Knowledge Base Overview

Date: 2026-05-04

Purpose: describe how the thesis can evolve from "MRI classifier" into a medical/research knowledge base while still keeping the cloud architecture, Obsidian integration, and model-comparison work.

## Core Idea

The system should not be framed only as:

> upload MRI image -> classify image -> show result

That is too narrow and makes the thesis look like a small Kaggle model experiment.

The stronger framing is:

> upload or capture medical/research artifacts -> process them with validated pipelines -> generate structured, auditable medical knowledge notes -> connect them into a searchable Obsidian-compatible knowledge base.

In this framing, ML is one part of the system. The bigger product is a **medical research knowledge base** that stores:

- source artifacts
- preprocessing metadata
- model outputs
- uncertainty and confidence
- masks/evidence images
- experiment runs
- patient/case summaries
- dataset citations
- human review notes
- literature/protocol links

## What Makes It A Knowledge Base

The system becomes a knowledge base when it stores relationships, not only files.

Example relationships:

```text
Dataset
  -> contains Case
  -> has License
  -> cited by Paper

Case
  -> contains MRI Slice
  -> has Patient-Level Metadata
  -> has Case Summary Note

MRI Slice
  -> has Source Image
  -> has Ground-Truth Mask
  -> processed by Model Run
  -> produces Classification Result
  -> produces Segmentation Evidence

Model Run
  -> uses Model Version
  -> uses Preprocessing Version
  -> produces Metrics
  -> produces Note

Note
  -> links Source Artifact
  -> links Model Output
  -> links Human Review
  -> links Related Literature
```

This is where Obsidian becomes useful: not as an editor replacement, but as a graph/read/review surface for connected research artifacts.

## Main Knowledge Objects

### 1. Source Artifact

Represents the original uploaded/captured item.

Examples:

- MRI `.tif` image
- segmentation mask
- case folder
- DICOM study, if implemented later
- voice note
- article PDF
- experiment protocol

Stored data:

- source path/id
- checksum
- file type
- dataset/source name
- ingestion time
- privacy flags
- license/citation requirement

### 2. MRI Slice Note

Generated per processed image slice.

Contents:

- case id
- slice id
- image thumbnail/link
- mask link if available
- abnormality flag
- NB result
- ICA + NB result
- optional CNN/U-Net result
- confidence/probability
- uncertainty label
- preprocessing version
- model version
- warning if models disagree

Purpose:

- gives traceability from model output back to exact source image
- allows human review of uncertain or representative slices

### 3. Case Summary Note

Generated per patient/case folder.

Contents:

- case id
- number of slices
- number of abnormal slices
- percentage of abnormal slices
- max/mean mask area, if masks are used
- model agreement summary
- links to important slice notes
- links to source dataset metadata
- review status

Purpose:

- makes the knowledge base usable at patient/case level, not only slice level
- reduces note overload

### 4. Model Comparison Note

Generated per experiment run.

Contents:

- compared models
- split strategy
- metrics
- confusion matrix
- runtime/cost
- strengths/weaknesses
- selected model for production pipeline
- known limitations

Purpose:

- directly supports the thesis requirement to compare models
- makes ML experiments reproducible and inspectable

### 5. Dataset Note

Generated once per dataset.

Contents:

- dataset name
- source URL
- citation
- license
- patient count
- artifact types
- label definitions
- train/validation/test split policy
- known limitations

Purpose:

- prevents dataset provenance from being lost
- gives every generated medical note a citation trail

### 6. Literature / Protocol Note

Imported manually or through browser capture.

Contents:

- paper/protocol title
- source link
- summary
- relevant methods
- relation to dataset/model/pipeline
- citations

Purpose:

- connects model development with domain literature
- preserves the original "second brain" idea in a medical research setting

### 7. Review Queue Note

Generated when the system is uncertain or when policy requires human attention.

Contents:

- artifact id
- reason for review
- model disagreement
- low confidence
- privacy warning
- failed preprocessing
- suggested next action

Purpose:

- avoids requiring manual approval of every generated note
- focuses human attention on risky/uncertain outputs

## Suggested Obsidian Vault Structure

```text
MedicalKB/
  00_Index/
    Home.md
    Review Queue.md
    Model Registry.md

  01_Datasets/
    LGG MRI Segmentation.md

  02_Cases/
    TCGA_DU_6400.md
    TCGA_HT_7690.md

  03_Slices/
    TCGA_DU_6400_slice_001.md
    TCGA_DU_6400_slice_002.md

  04_Model Runs/
    2026-05-04_nb_ica_baseline.md
    2026-05-04_resnet18_baseline.md

  05_Evidence/
    overlays/
    masks/
    thumbnails/

  06_Literature/
    Buda 2019.md
    Mazurowski 2017.md

  07_Protocols/
    Preprocessing Pipeline.md
    Evaluation Protocol.md
```

## Example Note Flow

### Step 1: Dataset Ingestion

System creates:

- `Dataset Note`
- source artifact records
- case index
- privacy/license metadata

### Step 2: Case Processing

System processes all slices in a case folder.

It creates:

- slice-level result records
- optional slice notes for selected/important slices
- case summary note

### Step 3: Model Comparison

System runs NB, ICA + NB, and optional modern model.

It creates:

- model run note
- metrics table
- disagreement list
- links to uncertain cases/slices

### Step 4: Knowledge Base Update

Obsidian plugin writes or updates:

- dataset note
- case notes
- model run notes
- review queue

The cloud stores:

- raw artifacts
- derived artifacts
- JSON sidecars
- embeddings/index
- backup snapshots

## Example Markdown Note

```markdown
---
type: mri_case_summary
dataset: LGG MRI Segmentation
case_id: TCGA_DU_6400
source_type: kaggle_tif
review_status: needs_review
generated_by: medical-kb-pipeline
model_run: 2026-05-04_nb_ica_baseline
---

# TCGA_DU_6400 Case Summary

## Source

- Dataset: [[LGG MRI Segmentation]]
- Artifact type: MRI TIFF slices with FLAIR abnormality masks
- Source license: CC BY-NC-SA 4.0

## Processing Summary

| Field | Value |
| --- | --- |
| Total slices | 44 |
| Abnormal slices | 17 |
| Max mask area | 12.4% |
| Model agreement | 81% |
| Review reason | NB and ICA+NB disagreed on 5 slices |

## Model Results

| Model | Output | Confidence |
| --- | --- | --- |
| NB baseline | abnormal case | 0.74 |
| ICA + NB | abnormal case | 0.68 |
| CNN baseline | abnormal case | 0.91 |

## Evidence

- Key slice: [[TCGA_DU_6400_slice_023]]
- Mask overlay: `05_Evidence/overlays/TCGA_DU_6400_slice_023.png`

## Limitations

This note is generated for research workflow support. It is not a diagnosis or clinical decision.
```

## How This Integrates With The Existing Distributed Plan

### Desktop App

Original role:

- capture selected context
- preview before upload
- local preprocessing

Medical role:

- select dataset folder or MRI artifacts
- show upload/privacy preview
- optionally detect file type
- queue upload to cloud
- allow local-only artifact policy if needed

### Cloud Backend

Original role:

- processing queue
- model execution
- durable storage
- backup/recovery

Medical role:

- run MRI processing jobs
- store source and derived artifacts
- run NB/ICA/CNN/U-Net processors
- generate Markdown and JSON sidecars
- maintain model registry and audit logs

### Obsidian Plugin

Original role:

- review/write client for generated notes

Medical role:

- render dataset/case/slice/model notes
- manage review queue
- show provenance and warnings
- support human edits without losing generated provenance

### Browser Extension

Original role:

- web capture

Medical role:

- capture papers, dataset pages, protocol pages, model docs
- attach literature notes to dataset/model notes

This keeps browser capture relevant, but no longer makes it the center of the MVP.

## Protocol Decision: HTTP Plus Optional RPC/MCP Layer

Do not make RPC/MCP the public application protocol for the MVP.

Recommended split:

```text
Tauri / Browser Extension / Obsidian Plugin
        |
        | HTTP/REST
        v
ASP.NET Core Backend
        |
        | queue/job contract
        v
Worker
        |
        | RPC/MCP-style processor calls
        v
Medical model and tool processors
```

Reasoning:

- HTTP/REST fits ordinary app operations: upload artifact, create job, check status, fetch generated note, restore backup.
- Tauri, browser extensions, Obsidian plugins, ASP.NET Core, reverse proxies, authentication, logging, and file uploads are simpler over HTTP.
- Most practical model servers already expose HTTP or OpenAI-compatible HTTP APIs.
- RPC/MCP is more useful inside the processing layer, where processors behave like callable tools with schemas.

Good RPC/MCP-style processor calls:

- `classify_mri_slice`
- `extract_mask_features`
- `generate_case_note`
- `compare_model_runs`
- `search_medical_kb`
- `fetch_artifact_metadata`

Thesis framing:

> REST is used for application lifecycle and durable cloud workflows, while RPC/MCP-style calls are reserved for extensible medical processors.

## Where The Thesis Contribution Lives

The contribution is not only in model training.

Contribution layers:

1. **Model comparison layer**
   - NB
   - ICA + NB
   - optional CNN/U-Net
   - patient-safe evaluation

2. **Medical artifact processing layer**
   - image/mask preprocessing
   - source tracking
   - derived artifact generation

3. **Knowledge-base generation layer**
   - Markdown notes
   - JSON sidecars
   - graph links
   - review queue

4. **Cloud architecture layer**
   - queue
   - storage
   - model serving
   - backup/recovery
   - Terraform deployment for two providers

## Why This Is Better Than A Plain Classifier

A plain classifier answers:

> What label did the model predict?

The medical knowledge base answers:

> What source artifact was processed, by which model version, using which preprocessing, with what evidence, under what uncertainty, and how does it connect to the case, dataset, experiment, and literature?

That is much stronger for a thesis because it combines:

- machine learning
- cloud systems
- data provenance
- reproducibility
- medical research workflow
- knowledge management

## Recommended Scope

MVP:

- LGG dataset ingestion
- image/mask artifact storage
- NB and ICA + NB baseline processors
- one stronger comparison model if feasible
- case-level and model-run Markdown notes
- JSON provenance sidecars
- Obsidian vault output
- cloud queue/storage
- Terraform for two providers

Stretch:

- segmentation overlay notes
- MedGemma wording/enrichment
- browser literature capture
- DICOM ingestion
- voice notes with MedASR

Avoid:

- claiming clinical deployment
- claiming diagnosis
- trying to become an EHR
- implementing full DICOM/FHIR compliance
- generating one note per every slice by default if that creates unreadable noise
