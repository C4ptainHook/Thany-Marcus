# Multimodal Medical Artifact Plan

Date: 2026-05-12

Purpose: correct the risk of replacing "MRI-only" with "audio-only" and define a broader but bounded multimodal medical artifact strategy.

## Short Verdict

Do not focus only on audio.

The real system should handle heterogeneous medical artifacts:

- documents
- images
- audio
- video
- structured metadata
- model outputs

However, the MVP should not implement deep intelligence for every modality. It should implement a **common artifact pipeline** and then add a few representative processors.

Best framing:

> A secure cloud service for aggregating heterogeneous medical artifacts and generating provenance-aware Markdown patient dossiers using modality-specific processors.

## Core Mental Model

```text
patient/case folder
  -> artifact detector
  -> artifact adapter
  -> processor router
  -> modality-specific processor
  -> structured insight
  -> Markdown dossier
  -> JSON provenance
  -> secure artifact storage
```

The system is not about one model or one modality.

It is about this transformation:

```text
messy medical evidence -> structured, traceable, readable patient dossier
```

## Artifact Types

### 1. Documents

Examples:

- PDF reports
- lab reports
- discharge summaries
- scanned paper
- referral letters
- handwritten notes

Processors:

- PDF text extraction
- OCR
- handwriting OCR, if feasible
- document type classifier
- date/doctor/institution extraction
- key-field extraction

MVP value:

- very high
- documents are common in real patient records
- easiest to show as "patient dossier aggregation"

### 2. Images / Pictures

Examples:

- photo of handwritten note
- photo of printed lab result
- wound/skin image
- ultrasound screenshot
- ECG photo
- medical chart image
- MRI/CT/echo screenshot

Processors:

- image metadata extractor
- OCR if text-heavy
- image quality check
- image type classifier
- specialist model only for selected image type

MVP value:

- high
- pictures are representative of real fragmented medical evidence

Warning:

- do not claim diagnosis from images unless using a validated public dataset and clearly scoped model.

### 3. Audio

Examples:

- heart sounds
- lung sounds
- cough/breath recordings
- doctor voice memo
- patient symptom recording

Processors:

- audio metadata extractor
- spectrogram generation
- heart/lung sound classifier
- speech-to-text for casual recordings
- review-required flag

MVP value:

- high if using one public dataset such as CirCor or ICBHI
- good example of specialized model processor

### 4. Video

Examples:

- short ultrasound clip
- echocardiography clip
- gait/movement recording
- endoscopy clip
- patient symptom video
- screen recording of medical viewer/report

Processors:

- video metadata extractor
- frame sampling
- keyframe extraction
- audio track extraction, if present
- OCR on frames
- thumbnail/contact sheet generation
- specialized model only for one narrow video task

MVP value:

- medium
- excellent for demonstrating multimodal architecture
- dangerous if deep video understanding becomes core too early

Recommended MVP treatment:

> Process video as an artifact with metadata, keyframes, thumbnails, OCR, and links. Do not make medical video diagnosis the first model task.

## Levels Of Processing

Use processing levels so every artifact can enter the system, even if no specialist model exists.

### Level 0: Store And Provenance

For every artifact:

- store original
- compute hash
- detect file type
- record source/time/user
- link to patient/case dossier

This is mandatory.

### Level 1: Generic Metadata

For every artifact where possible:

- file metadata
- duration/resolution/page count
- basic text extraction
- preview thumbnail
- artifact type guess

This is MVP.

### Level 2: Generic Extraction

For common formats:

- OCR
- PDF text extraction
- speech-to-text
- keyframe extraction
- table extraction if feasible

This is good MVP/stretch.

### Level 3: Specialized Medical Model

Only for selected modalities:

- heart sound classifier
- lung sound classifier
- ECG image classifier
- wound image classifier
- echo video model
- MRI classifier

This should be limited to one or two processors for the thesis.

### Level 4: Human Review

For risky or uncertain outputs:

- mark as needs review
- show source artifact
- show extracted fields
- show model confidence
- do not auto-assert clinical facts

This is mandatory for medical safety.

## Recommended MVP Modality Set

Use three representative artifact types:

1. **PDF/scanned document**
   - proves document/patient-card workflow
   - processor: text/OCR + metadata extraction

2. **Medical image/photo**
   - proves visual artifact support
   - processor: metadata + OCR/thumbnail + optional classifier if dataset exists

3. **Medical audio**
   - proves specialized model processor
   - processor: heart or lung sound classifier

Optional video:

4. **Short video**
   - process as metadata + keyframes + artifact note
   - do not implement deep diagnosis

This is more representative than MRI-only or audio-only.

## Processor Router Example

```yaml
routes:
  pdf_report:
    processors:
      - pdf_text_extractor
      - document_type_classifier
      - medical_metadata_extractor
      - dossier_note_generator
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
      - audio_artifact_note_generator
      - provenance_writer

  lung_sound_recording:
    processors:
      - audio_metadata_extractor
      - spectrogram_generator
      - lung_sound_classifier
      - audio_artifact_note_generator
      - provenance_writer

  short_medical_video:
    processors:
      - video_metadata_extractor
      - keyframe_extractor
      - frame_ocr_processor
      - video_artifact_note_generator
      - provenance_writer
```

## How Video Should Be Handled

Video can be included without making the thesis impossible.

First implementation:

- extract duration/resolution/codec
- extract N keyframes
- generate thumbnail/contact sheet
- OCR visible text from keyframes
- extract audio track if present
- link video to patient timeline
- create a video artifact note

Example generated note:

```markdown
---
type: medical_video_artifact
case_id: case_001
artifact_id: art_video_001
duration_sec: 82
review_status: needs_review
---

# Video Artifact

Source video: `artifacts/case_001/video_001.mp4`

## Extracted Metadata

- Duration: 82 seconds
- Resolution: 1920x1080
- Keyframes extracted: 8
- OCR text found: "ECHO", "LV", "EF"

## Evidence

- Contact sheet: `05_Evidence/video_001_contact_sheet.jpg`

## Limitations

This artifact was indexed and summarized for review. No diagnostic video interpretation was performed.
```

This supports video while keeping claims safe.

## Revised Thesis Direction

Best title:

> A Cloud-Based System for Aggregating Heterogeneous Medical Artifacts into Provenance-Aware Markdown Patient Dossiers

More technical title:

> A Processor-Router Architecture for Secure Multimodal Medical Artifact Aggregation and Markdown Dossier Generation

## What To Avoid

- Do not build separate full systems for MRI, audio, video, and documents.
- Do not promise deep understanding for every modality.
- Do not claim diagnostic interpretation from pictures/video unless the model, dataset, and validation are explicitly implemented.
- Do not make video the primary ML challenge unless the thesis pivots there.
- Do not let "multimodal" become uncontrolled scope.

## Final Recommendation

The system should be multimodal by architecture, but selective by implementation.

Implement:

- document/PDF processor
- image/OCR processor
- one specialized audio processor
- video indexing/keyframe processor if time allows

Do not implement:

- deep diagnosis for every modality
- full EHR
- full medical imaging platform

This gives the broad patient-dossier idea without losing thesis feasibility.

