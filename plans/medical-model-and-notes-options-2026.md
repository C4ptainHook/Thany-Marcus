# Medical Model And Note-Generation Options

Date checked: 2026-05-04

Purpose: expand the medical pivot beyond one trained DNN by identifying additional models, processors, standards, and note types that can make the system more useful as a biomedical/medical-note platform.

## Core Recommendation

Use the trained DNN as the **main proof-of-work processor**, but design the platform as a medical processing pipeline where several optional processors can enrich a Markdown/FHIR-like note.

Best thesis framing:

> A user-owned cloud service for medical/research artifact processing, where a trained DNN and auxiliary lightweight medical models transform imaging/audio/text inputs into auditable Markdown medical notes.

This keeps the original architecture:

- Tauri desktop client for capture/upload/preview
- user-owned cloud backend
- model processors on the VPS
- Obsidian/Markdown as human-readable report surface
- backup/recovery and audit trail

But it makes the implemented vertical slice medical:

```text
medical artifact
  -> local privacy/preflight checks
  -> cloud queue
  -> DICOM/image/audio/text preprocessing
  -> trained DNN inference
  -> optional model enrichment
  -> structured Markdown medical note
  -> Obsidian review/archive
```

## Strongest Processor Options

### 1. Your Existing Trained DNN

Role:

- the main thesis-specific model
- provides the concrete evaluable ML contribution
- produces labels, scores, uncertainty, or measurements depending on its task

Best use:

- make it a named processor such as `medical_image_classifier`
- expose a stable HTTP contract:
  - input artifact id
  - model version
  - preprocessing version
  - output label(s)
  - probability/confidence
  - uncertainty flag
  - optional explanation fields

Why it matters:

- without your own DNN, the project risks becoming just orchestration of existing models
- with your DNN, the cloud architecture has a real reason to exist

### 2. MedGemma 1.5 4B

Role:

- compact medical multimodal model for medical image/text enrichment
- useful for report drafting, image-context explanation, lab-report extraction, and medical text summarization

Relevance:

- Google Research released MedGemma 1.5 4B on 2026-01-13 with improved support for CT, MRI, histopathology, chest X-ray time series, anatomical localization, and lab-report extraction.
- It is small enough to be positioned as a compute-efficient/offline-capable starting point, but still needs use-case adaptation and validation.

Best use in the thesis:

- not as the diagnostic authority
- as a **note enrichment model** after the DNN:
  - rewrite DNN outputs into structured Markdown
  - explain labels in user-readable language
  - extract fields from scanned/report images
  - generate a preliminary report section with clear "AI-generated, not clinically validated" markings

Risk:

- high hallucination/clinical-safety risk if asked to infer diagnosis directly
- should be constrained to summarization, structuring, and uncertainty-aware wording

Sources:

- https://research.google/blog/next-generation-medical-image-interpretation-with-medgemma-15-and-medical-speech-to-text-with-medasr/
- https://developers.google.com/health-ai-developer-foundations/medgemma/model-card

### 3. MedASR

Role:

- medical speech-to-text processor for voice notes or dictated findings

Relevance:

- Google released MedASR as an open medical dictation ASR model alongside MedGemma 1.5.
- Google reports lower word error rate than Whisper large-v3 on medical dictation benchmarks.

Best use:

- voice message -> transcript -> structured note
- researcher/clinician-style dictation -> Markdown sections
- patient/student interview recording -> transcript with caveats

Thesis-safe scope:

- transcription and structuring only
- no autonomous clinical decision-making

Sources:

- https://research.google/blog/next-generation-medical-image-interpretation-with-medgemma-15-and-medical-speech-to-text-with-medasr/
- https://developers.google.com/health-ai-developer-foundations/medasr/model-card

### 4. Medical Image Segmentation Models: MedSAM / MedSAM2

Role:

- optional region-of-interest helper
- can enrich notes with selected regions, masks, simple measurements, or annotation previews

Relevance:

- MedSAM is a major medical segmentation foundation model trained across many modalities.
- MedSAM2 extends the segmentation idea toward 3D medical images and medical videos.

Best use:

- not required for MVP unless your DNN needs ROI support
- useful if you want medical notes to include "evidence regions":
  - image thumbnail
  - bounding/segmentation overlay
  - ROI description
  - measurement table

Good thesis angle:

> The system stores not only the final label but also the visual evidence artifact used to generate the note.

Sources:

- https://www.nature.com/articles/s41467-024-44824-z
- https://medsam2.github.io/

### 5. BiomedCLIP / BiomedGPT / BiomedParse

Role:

- biomedical vision-language backbones for image-text retrieval, captioning, VQA, detection, segmentation, and recognition.

Relevance:

- BiomedCLIP: biomedical image-text representation model trained from PubMed Central image-caption data.
- BiomedGPT: lightweight open-source biomedical vision-language generalist with VQA, report generation, and summarization results.
- BiomedParse: joint segmentation, detection, and recognition across multiple biomedical modalities.

Best use:

- future/optional processors
- literature support for the claim that biomedical notes are naturally multimodal
- useful if your DNN is narrow and you want a broader "medical-note enrichment" path

Sources:

- https://www.microsoft.com/en-us/research/publication/biomedclip-a-multimodal-biomedical-foundation-model-pretrained-from-fifteen-million-scientific-image-text-pairs/
- https://www.nature.com/articles/s41591-024-03185-2
- https://www.nature.com/articles/s41592-024-02499-w

### 6. BioMistral / Meditron / OpenBioLLM

Role:

- text-only medical LLM candidates for note structuring, entity extraction, and biomedical Q&A over local documents.

Relevance:

- BioMistral is a 7B biomedical LLM line based on Mistral and PubMed Central pretraining.
- Meditron is a 7B/70B medical LLM line based on Llama 2 and medical pretraining.
- OpenBioLLM has 8B and 70B variants and is positioned for biomedical tasks such as note summarization, entity recognition, biomarkers extraction, and classification.

Best use:

- compare against MedGemma, not necessarily implement
- useful if the thesis needs a fallback model for text-only note generation
- for local/VPS cost, 7B/8B quantized models are more realistic than 70B

Risk:

- many medical LLMs score well on QA benchmarks but are not validated for real clinical workflows
- use for structuring/extraction, not final clinical judgment

Sources:

- https://huggingface.co/papers/2402.10373
- https://huggingface.co/epfl-llm/meditron-70b
- https://huggingface.co/aaditya/Llama3-OpenBioLLM-70B

## Non-Model Tools That Matter

### DICOM Handling

Use when medical images are real DICOM studies, not screenshots.

Practical options:

- `pydicom` for parsing DICOM metadata and reading files
- Orthanc as an optional DICOM archive
- OHIF as a web viewer if the project needs image viewing
- MONAI Deploy App SDK if packaging medical inference workflows becomes important

Sources:

- https://pydicom.github.io/pydicom/stable/index.html
- https://www.orthanc-server.com/static.php?page=about
- https://docs.ohif.org/
- https://docs.monai.io/projects/monai-deploy-app-sdk/en/2.0.0/introduction/overview.html

### DICOM Privacy Preflight

This should become part of the local/cloud preprocessing layer if real DICOM is used.

MVP approach:

- detect whether an upload is DICOM
- extract a small metadata preview
- flag likely identifying fields before upload or before note generation
- optionally create a research-copy artifact with selected tags removed or redacted
- record what was retained, removed, or transformed in the note sidecar

Important caveat:

- DICOM de-identification is not solved by deleting a few obvious fields. The DICOM standard has an Attribute Confidentiality Profile and options, but even the standard notes that de-identification does not automatically guarantee all identifying information is removed.

Source:

- https://dicom.nema.org/medical/dicom/current/output/html/part15.html

### FHIR And DICOM Report Structure

Use these as inspiration for note schemas even if the MVP outputs Markdown.

Useful concepts:

- `FHIR ImagingStudy`: metadata around a DICOM imaging study and its series
- `FHIR DiagnosticReport`: report plus observations, images, conclusions, and presented form
- `DICOM SR`: structured measurements and report templates

Thesis-safe approach:

- generate Markdown as the primary artifact
- store a parallel JSON sidecar shaped like a simplified `DiagnosticReport`
- do not claim full FHIR/DICOM SR compliance unless implemented and tested

Sources:

- https://hl7.org/fhir/imagingstudy.html
- https://hl7.org/fhir/R4/diagnosticreport.html
- https://dicom.nema.org/medical/dicom/current/output/html/part16.html

### Medical Terminologies

Useful for structured note tags:

- LOINC for labs, observations, document/report concepts
- SNOMED CT for clinical findings, procedures, body structures, and observations

MVP approach:

- use free-text tags first
- optionally map common fields to terminology codes later
- avoid making terminology mapping a core MVP unless the DNN output naturally fits a known concept set

Sources:

- https://loinc.org/get-started/scope-of-loinc/
- https://www.nlm.nih.gov/research/umls/Snomed/snomed_main.html

## Medical Note Types The System Could Produce

### A. DNN Inference Note

Most thesis-friendly.

Contents:

- input artifact summary
- preprocessing details
- model version
- predicted class/label
- confidence/uncertainty
- evidence image/thumbnail
- limitations
- audit trail

This is the strongest MVP note.

### B. Imaging Study Summary Note

Useful if the input is DICOM.

Contents:

- study id
- modality
- series list
- inferred or extracted sequence/category
- key metadata
- processing status
- links to source artifacts

This fits cardiac MRI sequence classification well.

### C. Voice Dictation Note

Useful if adding MedASR.

Contents:

- audio metadata
- transcript
- structured sections
- extracted terms
- unresolved/uncertain phrases

This connects back to the original capture idea.

### D. Biomedical Research Case Note

Good if the project is not clinical.

Contents:

- research subject/sample/study identifier
- imaging or signal artifacts
- DNN result
- experiment protocol
- observations
- links to papers/protocols
- generated summary

This is safer than clinical reporting and likely better for a bachelor thesis.

### E. Review Queue Note

Useful for Obsidian/plugin integration.

Contents:

- generated proposal
- risk label
- confidence label
- required human actions
- source links
- model provenance

This solves the "AI notes can be too large to approve manually" problem by separating high-risk parts from low-risk auto-writes.

## Updated MVP Possibility

If the DNN is ready, the strongest one-week thesis-hackathon discovery target is:

```text
Upload DICOM/image/audio artifact
  -> parse metadata and strip/flag sensitive fields
  -> run trained DNN
  -> produce DNN Inference Note
  -> optionally enrich with MedGemma/MedASR
  -> save Markdown + JSON sidecar
  -> expose in Obsidian
```

Minimum implemented set:

- image/DICOM upload
- DNN inference processor
- Markdown note generator
- JSON sidecar with provenance
- simple cloud queue
- Obsidian vault write/review
- Terraform for two VPS providers

Optional stretch:

- MedGemma note enrichment
- MedASR voice notes
- OHIF/Orthanc viewing
- MedSAM ROI evidence

## Architecture Change

The original platform should gain a processor registry:

```text
processors:
  - id: medical_image_classifier
    type: custom_dnn
    required: true
  - id: medgemma_note_enricher
    type: medical_multimodal_llm
    required: false
  - id: medasr_transcriber
    type: medical_asr
    required: false
  - id: dicom_metadata_parser
    type: deterministic
    required: true for DICOM
  - id: medsam_roi_extractor
    type: segmentation
    required: false
```

This preserves the cloud thesis core: queueing, storage, model serving, audit, policy, backup, and Markdown integration.

## What To Avoid

- Do not claim clinical diagnosis.
- Do not claim FHIR/DICOM SR compliance unless actually implemented.
- Do not make 70B models part of the MVP on a cheap VPS.
- Do not let a medical LLM overwrite DNN outputs.
- Do not use an LLM to invent findings that the DNN or source artifact did not support.
- Do not use screenshots as the only medical image path if real DICOM data is available.

## Verdict

The medical pivot is stronger if the thesis becomes:

> **Medical artifact-to-note pipeline with a trained DNN as the core processor and optional medical models for transcription, enrichment, and structured Markdown report generation.**

The best implemented vertical slice is not "medical second brain" in general. It is:

> **A cloud-backed, auditable medical inference-note generator that integrates with an Obsidian-compatible research notebook.**
