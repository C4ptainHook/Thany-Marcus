# Medical Audio Artifact Direction

Date checked: 2026-05-12

Purpose: evaluate medical audio as a core or supporting modality for the patient-artifact dossier system.

## Short Verdict

Medical audio is a strong fit for the broader patient artifact aggregation idea.

It may be more representative than MRI because:

- files are smaller
- recordings are naturally artifact-like
- audio appears in real patient workflows
- there are public benchmark datasets
- specialized models can extract insights
- generated notes/dossiers are easy to demonstrate

Best narrowed direction:

> A secure cloud service for aggregating heterogeneous medical audio and document artifacts into provenance-aware Markdown patient dossiers, with specialized processors for lung/heart sound analysis and note extraction.

Even safer:

> A cloud-based prototype for organizing medical audio artifacts and related documents into auditable Markdown case dossiers.

## Medical Audio Types

### 1. Lung / Respiratory Sounds

Examples:

- auscultation recordings
- wheezes
- crackles
- normal respiratory cycles
- COPD/asthma/pneumonia-related recordings, depending on dataset labels

Good public dataset:

- ICBHI 2017 Respiratory Sound Database

Known facts:

- 920 recordings
- 126 subjects
- about 5.5 hours
- 6898 annotated respiratory cycles
- labels include normal, crackles, wheezes, both
- recordings from heterogeneous equipment and sample rates

Sources:

- https://www.auditory.org/postings/2018/8.html
- https://pmc.ncbi.nlm.nih.gov/articles/PMC8838187/
- https://www.mdpi.com/2079-9292/14/14/2794

Fit:

- excellent MVP candidate for specialized model processor
- compact and demonstrable
- good for artifact-to-note pipeline

### 2. Heart Sounds / Phonocardiograms

Examples:

- heart murmur recordings
- normal/abnormal PCG
- S1/S2 segmentation
- auscultation locations

Good public datasets:

- CirCor DigiScope Phonocardiogram Dataset
- PhysioNet/CinC Challenge 2016
- George B. Moody PhysioNet Challenge 2022

Known facts for CirCor:

- 5272 recordings
- 1568 subjects
- over 33.5 hours in latest version description
- pediatric population
- labels for murmur presence/absence/unknown
- clinical outcome labels
- recordings from multiple auscultation locations
- `.wav`, `.hea`, `.tsv`, and subject description files

Sources:

- https://physionet.org/content/circor-heart-sound/
- https://physionet.org/content/challenge-2022/
- https://physionet.org/challenge/2016/

Fit:

- excellent serious audio dataset
- better structured than many casual-audio datasets
- strong for patient/case dossier generation

### 3. Cough / Breath / Voice Screening

Examples:

- cough recordings
- breathing recordings
- spoken symptom descriptions
- voice biomarkers, depending on dataset/task

Fit:

- useful future artifact category
- public COVID-era cough datasets exist, but many are noisy and clinically fragile
- better as future work than first MVP unless a strong dataset is selected

### 4. Casual Clinical Recordings

Examples:

- doctor voice memo
- patient symptom recording
- consultation snippet
- spoken history

Fit:

- useful for dossier generation
- process as transcription and note extraction
- do not use for diagnosis

Processors:

- speech-to-text
- speaker/time metadata
- symptom/medication/date extraction
- Markdown note generation

## Recommended Audio MVP

If moving away from MRI, use **medical audio + documents** as the core demo.

Best MVP:

```text
patient/case folder
  -> lung or heart sound recording
  -> optional PDF/scanned report
  -> audio metadata extractor
  -> specialized audio classifier
  -> note generator
  -> JSON provenance
  -> Markdown patient/case dossier
```

Choose one primary audio dataset:

Option A:

- ICBHI 2017 respiratory sounds
- task: crackle/wheeze/normal classification
- best if lung screening is the focus

Option B:

- CirCor heart sounds / PhysioNet Challenge 2022
- task: murmur presence/absence/unknown or normal/abnormal outcome
- best if patient-level dossier is the focus

Recommendation:

> Use CirCor/PhysioNet 2022 if you want a patient-card/dossier story, because it has subjects, multiple auscultation locations, metadata, labels, and segmentations.

Use ICBHI if you want:

- simpler respiratory classification
- crackle/wheeze insights
- lung-screening emphasis

## How Specialized Models Fit

The system should not use one generic LLM for everything.

Use specialized processors:

```text
audio artifact
  -> audio adapter
  -> signal preprocessing
  -> spectrogram/features
  -> specialized classifier
  -> structured output
  -> dossier note
```

Example output:

```json
{
  "artifact_type": "heart_sound_recording",
  "processor": "heart_murmur_classifier",
  "recording_location": "mitral",
  "prediction": "murmur_present",
  "confidence": 0.82,
  "segments": [
    {
      "start_sec": 1.2,
      "end_sec": 1.8,
      "label": "S1"
    }
  ],
  "limitations": [
    "research workflow output",
    "not a diagnostic decision"
  ]
}
```

## Dossier Notes Enabled By Audio

### Audio Artifact Note

Fields:

- source recording
- patient/case id
- duration
- sample rate
- recording location
- device if known
- signal quality
- processor result
- confidence
- waveform/spectrogram link

### Patient Audio Summary Note

Fields:

- number of recordings
- auscultation locations
- abnormal/uncertain recordings
- model agreement
- review-needed flags

### Clinical Artifact Timeline

Fields:

- date/time
- artifact type
- source
- extracted finding
- linked original
- review status

### Model Run Note

Fields:

- dataset
- train/test split
- preprocessing
- model
- metrics
- limitations

## Why Audio May Be Better Than MRI For This Thesis

Advantages:

- closer to "patient card with scattered artifacts"
- easier to store and process than large MRI datasets
- public patient-level datasets exist
- allows a compact MVP
- demonstrates specialized model processors clearly
- supports casual recordings as a natural extension

Disadvantages:

- less connected to the user's previous NB/ICA MRI work
- may require new model training/evaluation
- audio preprocessing is its own domain
- clinical interpretation is still sensitive

## Updated System Shape With Audio

```text
Avalonia Desktop Client
  -> imports patient/case folder
  -> detects audio/PDF/image artifacts
  -> previews metadata
  -> uploads to cloud

ASP.NET Core Cloud API
  -> stores raw artifacts
  -> creates processing jobs
  -> manages access/audit

Processor Router
  -> audio adapter
  -> PDF/OCR adapter
  -> casual-recording transcription adapter
  -> specialized model processor
  -> note/provenance writer

Knowledge Store
  -> raw audio
  -> derived spectrograms
  -> extracted metadata
  -> Markdown dossiers
  -> JSON provenance

Obsidian Vault
  -> patient/case dossier
  -> artifact timeline
  -> model-run notes
  -> review queue
```

## Recommended Thesis Reframe

If audio becomes central:

> A cloud-based system for aggregating heterogeneous medical audio and document artifacts into provenance-aware Markdown patient dossiers.

If keeping broader artifacts:

> A secure cloud service for aggregating heterogeneous medical artifacts and generating provenance-aware Markdown patient dossiers with specialized model processors.

## What To Avoid

- Do not claim diagnosis.
- Do not process casual recordings as medical findings without review.
- Do not mix lung and heart model tasks in one classifier.
- Do not start with MRI, lung sounds, heart sounds, handwriting, and voice all at once.
- Do not make a full EHR.

## Best Next Decision

Choose one primary medical audio domain:

1. Lung sounds:
   - ICBHI 2017
   - crackle/wheeze/normal classification

2. Heart sounds:
   - CirCor / PhysioNet Challenge 2022
   - murmur/outcome classification

Recommended:

> Pick heart sounds if the patient-dossier idea matters most. Pick lung sounds if the lung-screening idea matters most.

