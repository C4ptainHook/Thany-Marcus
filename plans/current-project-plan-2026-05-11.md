# Current Project Plan

Date: 2026-05-11

Status: current working plan after pivot from generic second brain to medical research knowledge base.

## Working Title

Primary:

> **A Cloud Service for Brain MRI Research Data Aggregation, Machine-Learning Processing, and Markdown Knowledge-Base Generation**

Alternative:

> **A Cloud-Based Medical Knowledge Base for Brain MRI Research Artifacts, Model Comparison, and Provenance-Aware Markdown Notes**

## Core Claim

The project is a cloud-backed system that ingests heterogeneous brain MRI research artifacts, processes them with configurable machine-learning processors, and organizes the results as auditable Markdown notes in an Obsidian-compatible knowledge base.

Short form:

> biomedical research artifact -> normalized object -> configured processors -> auditable result -> Markdown knowledge object

## What The System Is

It is:

- a medical research artifact organizer
- a cloud processing pipeline
- a model-comparison system
- a provenance-aware Markdown generator
- an Obsidian-compatible biomedical knowledge base

It is not:

- a clinical diagnostic product
- an EHR
- a PACS/VNA replacement
- a Flywheel competitor
- a generic personal second brain
- an autonomous multi-agent research system like CORAL
- a universal biomedical AI platform

## Main Differentiation

Against Flywheel:

> Flywheel is an enterprise medical imaging research data and AI workflow platform. This project is a smaller, self-hosted, Markdown-first system focused on brain MRI research artifacts, model comparison, provenance, and Obsidian-compatible review.

Against CORAL:

> CORAL is an autonomous multi-agent research/evolution framework. This project uses a narrower deterministic processor-router architecture for medical data processing and knowledge-base generation.

Against a plain MRI classifier:

> The project does not only output predictions. It stores source artifacts, metadata, model runs, metrics, evidence, limitations, and provenance as linked Markdown knowledge objects.

## Architecture

```text
Avalonia Desktop Client
        |
        | HTTP/REST
        v
ASP.NET Core Cloud API
        |
        | creates processing jobs
        v
.NET Worker + Processor Router
        |
        | calls dataset adapters and ML processors
        v
Structured Outputs
        |
        v
Markdown Notes + JSON Provenance + Evidence Files
        |
        v
Obsidian-Compatible Medical Knowledge Base
```

## Component Plan

### 1. Avalonia Desktop Client

Purpose:

- select dataset folders or case artifacts
- preview files and metadata before upload
- show upload status
- perform simple local preflight checks
- send selected artifacts to cloud

MVP responsibilities:

- choose LGG dataset folder
- choose UTSW subset folder
- show detected dataset type
- show artifact count and approximate size
- upload to backend
- show job status

Not MVP:

- deep OS capture
- Slack/Telegram/browser capture
- local heavy model inference
- full note editor

### 2. ASP.NET Core Cloud API

Purpose:

- receive artifacts
- store metadata
- create jobs
- expose job/result/note/provenance APIs
- support backup/recovery

MVP endpoints:

```text
POST /artifacts/upload
POST /datasets/import
POST /jobs
GET  /jobs/{id}
GET  /notes
GET  /notes/{id}
GET  /provenance/{id}
```

### 3. Cloud Worker

Purpose:

- execute processing jobs
- run dataset adapters
- run model processors
- generate notes and provenance

MVP:

- one worker service
- background queue in database or simple durable job table
- deterministic processor routing from config

### 4. Processor Router

Purpose:

- avoid locking the system to one model
- route each artifact/dataset type to configured processors

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

The router is config/rule based, not an autonomous agent.

### 5. ML Processor Layer

MVP processors:

- Naive Bayes baseline
- ICA + Naive Bayes baseline
- trained DNN or small CNN comparator
- deterministic feature extractor
- optional segmentation/evidence extractor

Implementation options:

- Python service called by HTTP from .NET worker
- or Python scripts wrapped by the worker
- or ONNX Runtime for the trained DNN if export is practical

Recommended:

> Keep ML in Python for MVP and call it from the .NET worker through a stable processor contract.

### 6. Knowledge Generator

Outputs:

- Markdown notes for humans
- JSON provenance sidecars for audit/reproducibility
- evidence files such as overlays, thumbnails, feature tables

MVP note types:

- Dataset Note
- Case Summary Note
- Model Run Note
- Prediction/Result Note
- Evidence Note
- Review Queue Note

### 7. Obsidian Integration

MVP:

- generate an Obsidian-compatible vault folder
- use Markdown links and YAML frontmatter
- user opens the folder in Obsidian

Stretch:

- Obsidian plugin for review/status UI
- backend sync status
- apply/reject generated notes

## Dataset Plan

### Primary Serious Dataset

**UTSW-Glioma 2026**

Use:

- primary serious dataset
- patient-level multimodal MRI
- molecular markers
- segmentations
- NIfTI + TSV metadata

MVP narrowing:

- process a controlled subset
- choose one target after metadata inspection
- likely first target: IDH mutation status, if label availability is good
- otherwise use tumor grade/type or segmentation-derived case summary

### Continuity Dataset

**LGG MRI Segmentation**

Use:

- continuity with existing NB/ICA work
- lightweight full pipeline
- TIFF images and masks
- abnormal slice/case classification from mask presence

### Fallback Dataset

**BRISC 2025**

Use only if UTSW becomes too heavy:

- simpler 2D classification/segmentation dataset
- four tumor classes
- easier MVP fallback

### Related Work / Future Datasets

- UCSF-PDGM
- BraTS 2024/2025
- UCSD-PTGBM

## Data Model

Core entities:

```text
Dataset
Case
Artifact
Modality
Segmentation
Label
Model
ModelRun
Prediction
Evidence
Paper
Protocol
GeneratedNote
ProvenanceRecord
ReviewItem
```

## Vault Structure

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

## Infrastructure

MVP:

- Docker Compose on VPS
- ASP.NET Core API container
- worker container
- PostgreSQL container
- Python ML processor container
- filesystem-backed object/artifact storage on VPS volume
- Caddy reverse proxy
- Terraform for two VPS providers

Providers:

- Hetzner
- DigitalOcean

Not MVP:

- Kubernetes
- S3-compatible object storage
- full multi-node scaling
- managed database requirement

## Protocol Plan

Public app API:

- HTTP/REST

Reason:

- easy from Avalonia
- easy from ASP.NET
- easy for uploads/job APIs
- easy to debug and deploy

Internal processor calls:

- HTTP or RPC-style contract
- MCP-like processor semantics are allowed conceptually but not required

Rule:

> REST for application lifecycle; processor-router calls for medical processing modules.

## MVP Implementation Scope

Implement:

- Avalonia desktop importer
- ASP.NET Core backend
- durable job table/queue
- LGG adapter
- UTSW adapter for subset
- NB processor
- ICA + NB processor
- trained DNN or small CNN processor
- Markdown note generator
- JSON provenance writer
- evidence output
- Obsidian-compatible vault export
- Docker Compose deployment
- Terraform for Hetzner and DigitalOcean

Defer:

- Obsidian plugin
- browser extension
- medical LLM enrichment
- MedGemma/MedASR
- DICOM input
- Flywheel-like enterprise workflows
- CORAL-style autonomous agents
- GPepT/chemistry pipeline
- Kubernetes

## Main Risks

### Dataset Complexity

UTSW is serious but heavier than LGG.

Mitigation:

- inspect metadata first
- process subset first
- keep BRISC fallback

### Label Mismatch

LGG and UTSW do not share one label space.

Mitigation:

- use dataset-specific tasks
- compare the pipeline, not a universal classifier

### Scope Creep

Obsidian plugin, browser capture, medical LLMs, DICOM, and autonomous agents can expand the thesis too far.

Mitigation:

- keep them as future work unless MVP is complete

### Clinical Claims

Medical topic can sound diagnostic.

Mitigation:

- frame as research/workflow support
- include limitations in every generated note
- no treatment or diagnosis claims

## One-Week Discovery Plan

Day 1:

- inspect UTSW metadata
- confirm target label availability
- define subset strategy

Day 2:

- implement/validate LGG adapter and simple note generation
- define common schema

Day 3:

- implement UTSW metadata/NIfTI adapter for subset
- create dataset and case notes

Day 4:

- run NB and ICA+NB baseline on LGG
- define equivalent UTSW feature table if feasible

Day 5:

- integrate trained DNN/small CNN processor
- generate model-run notes and provenance

Day 6:

- wire cloud job flow:
  - upload
  - create job
  - process
  - write notes

Day 7:

- review generated vault in Obsidian
- decide final dataset/model scope
- write final thesis architecture and limitation sections

## Current Lock-In Decisions

- Desktop: Avalonia/.NET
- Backend: ASP.NET Core
- Worker: .NET Worker Service
- ML: Python processor layer
- Architecture: processor router
- Output: Markdown + JSON provenance
- Viewer: Obsidian-compatible vault first, plugin later
- Primary dataset: UTSW-Glioma 2026 subset
- Continuity dataset: LGG MRI Segmentation
- Deployment: Docker Compose + Terraform for Hetzner and DigitalOcean

