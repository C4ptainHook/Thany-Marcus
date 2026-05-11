# Specialized Models And Hierarchical Vaults

Date checked: 2026-05-12

Purpose: capture the refined goal: use strong specialized models for medical artifact insights and organize outputs into hierarchical/shareable patient dossier vaults.

## Updated Core Goal

The system should not only store medical artifacts.

The goal is:

> aggregate heterogeneous medical artifacts, process them with strong specialized models, and organize the extracted insights into secure, provenance-aware, shareable Markdown patient dossiers.

The system is multimodal by architecture and selective by implementation.

## Strong Evaluation

This is a stronger idea than MRI-only or audio-only.

Why:

- it targets the real pain: scattered medical evidence across formats
- it allows specialized models instead of weak generic summarization
- it makes the processor-router architecture necessary
- it gives a clear knowledge product: patient dossier vault
- it can be scoped as research/review support instead of EHR replacement

Main danger:

- if the system claims too much insight across too many modalities, it becomes impossible to validate and medically risky

Correct balance:

> Every artifact gets storage, metadata, provenance, and a note. Only selected artifact types get specialized medical-model processing.

## Specialized Model Strategy

Use specialized processors per modality:

```text
artifact
  -> modality adapter
  -> processor router
  -> specialized model
  -> structured insight
  -> Markdown note + provenance
```

Examples:

```text
heart_sound.wav
  -> audio adapter
  -> spectrogram processor
  -> murmur classifier
  -> Heart Sound Insight Note

lung_sound.wav
  -> respiratory audio adapter
  -> crackle/wheeze classifier
  -> Respiratory Sound Insight Note

scanned_lab_report.jpg
  -> image adapter
  -> OCR/table extractor
  -> Lab Result Artifact Note

short_echo_clip.mp4
  -> video adapter
  -> keyframe extractor
  -> frame OCR/metadata extraction
  -> Echo Video Artifact Note

medical_report.pdf
  -> PDF adapter
  -> report classifier/field extractor
  -> Report Summary Note
```

Important:

- Specialized model outputs should be structured.
- Notes should not invent clinical conclusions.
- Every insight should link back to source artifact and processor provenance.
- Human review should be required for high-risk or uncertain insights.

## Hierarchical Vault Concept

The knowledge base should be hierarchical:

```text
Clinic/Owner Vault
  -> Patient Dossier Vault A
  -> Patient Dossier Vault B
  -> Patient Dossier Vault C
```

Conceptually:

- the owner/doctor/researcher has a main vault
- each patient/case has a bounded dossier subtree
- a patient dossier can be exported/shared independently
- patient sees only their dossier, not the whole owner vault

Recommended structure:

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
      Review.md
      Artifacts/
      Insights/
      Reports/
      Evidence/
      .provenance/

    patient_002/
      Home.md
      Timeline.md
      Summary.md
      Review.md
      Artifacts/
      Insights/
      Reports/
      Evidence/
      .provenance/
```

Each `patient_xxx` folder is a **subvault candidate**.

## Sharing Model

Do not rely on "an Obsidian link" as the security model.

Verified Obsidian constraints:

- Obsidian URI can open a vault or file, but it assumes the vault/file already exists locally.
- Obsidian Sync shared vaults require collaborators to have Sync subscriptions.
- Obsidian Sync shared vaults do not currently provide fine-grained permissions; collaborators generally receive the same permissions as the owner except inviting others.

Sources:

- Obsidian URI docs: https://help.obsidian.md/uri
- Obsidian shared vault docs: https://obsidian.md/help/sync/collaborate

Implication:

> The system should own secure sharing. Obsidian should be an optional reader, not the access-control layer.

## Recommended Sharing Options

### Option 1: Patient Dossier Export Package

The system creates a shareable package:

```text
patient_001_dossier.zip
  Home.md
  Timeline.md
  Summary.md
  Artifacts/
  Evidence/
  .provenance/
```

Patient can:

- open folder as an Obsidian vault
- read Markdown directly
- keep a local copy

Pros:

- simple
- thesis-friendly
- matches Markdown ownership idea

Cons:

- revocation is hard after export
- patient copy can become stale

### Option 2: Secure Patient Portal

The system provides a web view:

```text
share link -> authenticated patient portal -> rendered Markdown dossier
```

Pros:

- access control
- revocation
- audit logs
- no Obsidian requirement for patient

Cons:

- more implementation work

### Option 3: Obsidian-Compatible Sync Bundle

The system syncs only the patient subtree to a patient-controlled folder.

Patient opens that folder as an Obsidian vault.

Pros:

- good user experience for Obsidian users
- keeps the "subvault" idea

Cons:

- needs careful sync/conflict model
- access control still belongs to the system, not Obsidian

## Recommended MVP Sharing

For thesis MVP:

> implement patient dossier export as an Obsidian-compatible folder/package.

Stretch:

> add secure patient portal with authenticated read-only dossier view.

Do not use Obsidian Sync as the primary sharing mechanism.

## Insight Safety Levels

Use safety labels for model outputs:

```text
Level 0: Stored artifact only
Level 1: Metadata extracted
Level 2: Generic text/OCR/transcript extracted
Level 3: Specialized model insight
Level 4: Human-reviewed insight
```

Patient-facing dossiers should clearly separate:

- source artifacts
- extracted text
- model-generated insights
- human-reviewed notes
- limitations

Example:

```markdown
## Respiratory Sound Insight

Source: [[Artifacts/lung_recording_001.wav]]

Model output:

- Detected pattern: wheeze-like segments
- Confidence: 0.78
- Review status: not reviewed

Limitations:

This is an automated research/workflow insight and is not a diagnosis.
```

## Revised System Shape

```text
Avalonia Desktop Client
  -> imports patient/case artifact folder
  -> previews files
  -> uploads selected artifacts

ASP.NET Core Cloud API
  -> secure storage
  -> patient/case registry
  -> job queue
  -> share/export API

Processor Router
  -> document processors
  -> image processors
  -> audio processors
  -> video processors
  -> specialized medical models

Knowledge Generator
  -> patient dossier Markdown
  -> insight notes
  -> timeline
  -> JSON provenance

Hierarchical Vault Store
  -> owner vault
  -> patient dossier subvaults
  -> export/share packages
```

## Best Updated Thesis Claim

> A secure cloud service for aggregating heterogeneous medical artifacts, extracting insights with specialized processors, and generating shareable provenance-aware Markdown patient dossiers.

More technical:

> A processor-router architecture for multimodal medical artifact insight extraction and hierarchical Markdown patient dossier generation.

## What This Changes

Compared to earlier MRI plan:

- MRI is no longer central.
- Specialized models are central.
- Patient dossier hierarchy is central.
- Obsidian is a reader/export format, not the security boundary.
- Evaluation should focus on artifact processing, insight extraction, provenance correctness, and shareable dossier generation.

