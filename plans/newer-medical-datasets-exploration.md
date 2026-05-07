# Newer Medical Dataset Exploration

Date checked: 2026-05-04

Purpose: identify better/newer datasets than the original Kaggle LGG MRI segmentation dataset for a medical knowledge-base thesis.

## Short Verdict

The original `lgg-mri-segmentation` dataset is still useful as a small baseline because it is simple and already connected to prior work. However, it is not the strongest main dataset in 2026.

Updated recommendation after deciding to try the most current serious dataset:

> Use **UTSW-Glioma 2026** as the primary target dataset, keep LGG MRI Segmentation as a lightweight continuity/baseline dataset, and keep BRISC as an emergency fallback if the 3D NIfTI workflow becomes too heavy.

This makes the thesis more medically serious because the main dataset is patient-level, multimodal, molecular-marker-aware, and published in 2026.

## Dataset Candidates

### 1. UTSW-Glioma 2026

Sources:

- Paper: https://www.nature.com/articles/s41597-026-07274-4
- TCIA: https://www.cancerimagingarchive.net/collection/utsw-glioma/
- DOI: https://doi.org/10.7937/DFAE-1B86

What it contains:

- 625 glioma patients
- patients treated at UTSW between 2006 and 2023
- multi-contrast preoperative MRI
- four MRI contrasts:
  - pre-contrast T1w
  - post-contrast T1w
  - T2w
  - T2w-FLAIR
- demographics
- histopathological and MRI acquisition metadata
- molecular markers:
  - IDH mutation status
  - 1p/19q co-deletion
  - MGMT promoter methylation
  - tumor type
  - tumor grade
- multi-label tumor segmentations
- NIfTI image format
- tab-separated metadata file
- TCIA collection size: about 22.9 GB
- Scientific Data publication date: 2026-04-22
- CC BY 4.0 article license; TCIA data citation required

Why it is good:

- most current serious candidate found so far
- patient-level dataset, not just isolated 2D images
- directly supports a medical knowledge base:
  - case notes
  - modality notes
  - marker notes
  - segmentation evidence notes
  - model-run notes
- lets the thesis discuss MRI-to-molecular-marker research, not just tumor/no-tumor classification
- better fit for "serious medical dataset" than Kaggle-style 2D images

Critical limitation:

- heavier implementation than LGG/BRISC
- requires NIfTI processing
- 3D multimodal MRI can distract from the cloud/knowledge-base thesis if not narrowed
- 22.9 GB is manageable, but still enough to matter for local/cloud transfer and storage
- label imbalance and missing marker values must be checked before choosing the prediction target

Best thesis use:

> primary serious dataset for patient-level glioma MRI knowledge-base generation and molecular-marker classification experiments.

Recommended first task:

> Predict **IDH mutation status** from selected MRI-derived features or a narrowed 2D/3D DNN pipeline, then generate patient-level medical knowledge notes with provenance.

Alternative tasks:

- MGMT promoter methylation prediction
- 1p/19q co-deletion prediction
- tumor grade classification
- tumor type classification
- segmentation-derived tumor-burden summary

Recommended MVP narrowing:

- start with a subset of subjects
- use one or two modalities first, preferably T2-FLAIR and post-contrast T1w
- extract features from segmentation masks or central tumor slices
- compare NB/ICA baseline with trained DNN
- generate case-level notes rather than slice spam

### 2. BRISC 2025 / Scientific Data 2026

Source:

- Kaggle: https://www.kaggle.com/datasets/briscdataset/brisc2025
- Paper: https://www.nature.com/articles/s41597-026-06753-y
- arXiv/HF paper page: https://huggingface.co/papers/2506.14318

What it contains:

- 6,000 contrast-enhanced T1-weighted brain MRI images
- four classes:
  - glioma
  - meningioma
  - pituitary tumor
  - no tumor
- classification task with 5,000 train / 1,000 test images
- segmentation task with paired image/mask data
- axial, coronal, sagittal planes
- manifest metadata, checksums, and file naming convention
- CC BY 4.0

Why it is good:

- newer than LGG
- directly supports both classification and segmentation
- balanced multi-class classification task
- includes non-tumor class
- has a Scientific Data paper
- easier to use than large 3D NIfTI datasets
- excellent fit for a medical knowledge-base MVP

Critical limitation:

- it is 2D JPEG/PNG, not raw DICOM or full 3D patient studies
- patient-level independence cannot be fully guaranteed because source datasets did not always preserve subject identity
- the paper explicitly says it is not for direct clinical diagnostic use

Best thesis use:

> fallback or secondary dataset for simpler multi-class MRI classification, segmentation evidence, and generated medical research notes.

Possible note types:

- Tumor Classification Note
- Segmentation Evidence Note
- Plane-Generalization Note
- Model Comparison Note

### 3. UCSF-PDGM

Source:

- TCIA: https://www.cancerimagingarchive.net/collection/ucsf-pdgm/
- Paper/PMC: https://pmc.ncbi.nlm.nih.gov/articles/PMC9748624/

What it contains:

- about 501 adult patients with WHO grade 2-4 diffuse gliomas
- standardized 3T preoperative MRI
- advanced diffusion and perfusion imaging
- multicompartment tumor segmentations
- IDH mutation status
- MGMT promoter methylation status
- treatment and survival data
- TCIA DOI: https://doi.org/10.7937/tcia.bdgf-8v37

Why it is good:

- much more medically serious than Kaggle 2D datasets
- patient-level dataset
- supports radiogenomics and survival-analysis style knowledge notes
- strong for future work and thesis domain analysis

Critical limitation:

- large and more complex
- 3D multimodal preprocessing is harder
- likely too heavy for a quick MVP unless narrowed hard

Best thesis use:

> advanced target dataset for future work or small sampled extension after the MVP works on BRISC/LGG.

Possible note types:

- Patient Imaging Summary
- Molecular Marker Note
- Radiogenomics Experiment Note
- Segmentation Provenance Note

### 4. UCSD-PTGBM 2026

Sources:

- Paper: https://www.nature.com/articles/s41597-025-06499-z
- PMC: https://pmc.ncbi.nlm.nih.gov/articles/PMC12886825/
- TCIA: https://www.cancerimagingarchive.net/collection/ucsd-ptgbm/

What it contains:

- 243 timepoints from 178 post-treatment glioblastoma subjects
- advanced 3T multimodal MRI
- diffusion/perfusion sequences
- neuroradiologist-approved voxelwise tumor segmentations
- IDH, MGMT, survival, progression-free survival for a subset
- NIfTI imaging, CSV clinical data
- subject-level identifiers to avoid leakage
- CC BY 4.0

Why it is good:

- very current and clinically interesting
- post-treatment glioblastoma is a harder, more realistic task than simple tumor/no-tumor classification
- supports longitudinal/timepoint knowledge notes

Critical limitation:

- too complex for a bachelor MVP unless the thesis specifically targets post-treatment GBM
- not aligned with existing LGG/NB/ICA work

Best thesis use:

> advanced future-work dataset for longitudinal case-note design.

### 5. BraTS 2024 / 2025 Family

Sources:

- BraTS 2024 Synapse: https://www.synapse.org/Synapse%3Asyn53708249/wiki/626323
- Review: https://www.mdpi.com/1424-8220/25/6/1838
- BraTS-MEN-RT 2024 dataset paper: https://www.nature.com/articles/s41597-026-06649-x

What it contains:

- challenge datasets for brain tumor segmentation
- increasingly diverse tracks:
  - adult glioma
  - meningioma
  - pediatric tumors
  - metastases
  - post-treatment tasks
- 3D multimodal MRI, usually NIfTI
- strong benchmark/evaluation culture

Why it is good:

- best-known benchmark family for brain tumor segmentation
- strong literature support
- good for Chapter 1/3 algorithmic analysis

Critical limitation:

- segmentation-first, not simple classification-first
- data access and challenge terms can be annoying
- 3D segmentation is heavier than 2D classification

Best thesis use:

> literature/SOTA comparison and possible stretch dataset if segmentation becomes central.

## Dataset Ranking For This Thesis

| Rank | Dataset | Use | Why |
| --- | --- | --- | --- |
| 1 | UTSW-Glioma 2026 | primary serious target | Most current, patient-level, multimodal MRI, molecular markers, segmentations |
| 2 | LGG MRI Segmentation | baseline/continuity | Existing work already uses it; good for comparing old vs serious dataset |
| 3 | BRISC 2025/2026 | fallback/secondary | New, simple enough, classification + segmentation, strong note-generation fit |
| 4 | UCSF-PDGM | backup serious dataset | Strong patient-level glioma/radiogenomics dataset |
| 5 | BraTS 2024/2025 | SOTA/segmentation | Benchmark family, but heavier |
| 6 | UCSD-PTGBM 2026 | future | Excellent but too specialized/post-treatment |

## Recommended Dataset Strategy

### MVP Dataset Pair

Use two datasets:

1. UTSW-Glioma 2026
   - primary serious dataset
   - patient-level multimodal MRI
   - molecular markers and segmentations
   - best match for a medical knowledge base

2. LGG MRI Segmentation
   - continuity with prior NB/ICA work
   - smaller and simpler baseline
   - useful for proving the pipeline before full UTSW processing

This lets the thesis say:

> The system was first validated on a lightweight established LGG dataset and then applied to the 2026 UTSW-Glioma dataset for patient-level MRI, molecular-marker, segmentation, and provenance-aware medical notes.

### Fallback Dataset

Use BRISC 2025 if UTSW-Glioma preprocessing becomes too costly for the implementation window.

### Advanced Dataset Discussion

Use UCSF-PDGM, UCSD-PTGBM, and BraTS in Chapter 1/3 as SOTA datasets and future expansion targets.

Do not implement all of them.

## Multi-Dataset Strategy

Using several datasets is a good idea if the thesis contribution is the **medical knowledge-base pipeline**, not only the best classifier on one dataset.

Best framing:

> The system ingests heterogeneous brain MRI datasets through dataset adapters, normalizes their metadata into a common provenance schema, runs dataset-appropriate model processors, and generates comparable Obsidian-compatible medical research notes.

Do not merge all datasets blindly into one training set. Instead, use them in different roles.

Recommended roles:

| Dataset | Role | Implementation depth |
| --- | --- | --- |
| UTSW-Glioma 2026 | primary serious dataset | full primary pipeline on subset |
| LGG MRI Segmentation | continuity/baseline dataset | lightweight full pipeline |
| BRISC 2025 | fallback / simpler external dataset | optional smoke-test |
| UCSF-PDGM | serious external validation target | metadata/design only unless time allows |
| BraTS | segmentation benchmark reference | literature/future work |

### Why Multiple Datasets Help

- Shows the system is not hard-coded to one Kaggle folder.
- Makes the knowledge-base idea stronger because each dataset becomes a first-class knowledge object.
- Supports comparison of dataset quality, labels, modalities, licensing, and preprocessing.
- Lets the thesis discuss generalization and external validity.
- Makes provenance and dataset adapters meaningful.
- Creates richer Obsidian graph links:
  - dataset -> cases
  - cases -> modalities
  - modalities -> model runs
  - model runs -> metrics
  - metrics -> limitations

### Why Multiple Datasets Are Risky

- Labels do not always match:
  - tumor/no-tumor
  - tumor type
  - IDH
  - MGMT
  - 1p/19q
  - segmentation labels
- Image formats differ:
  - TIFF/JPEG/PNG
  - DICOM
  - NIfTI
- Some datasets are 2D image collections, others are 3D multimodal patient studies.
- Patient identifiers and split rules differ.
- Licensing and citation rules differ.
- Preprocessing can consume the whole thesis schedule.

### Recommended Common Schema

Use a common internal schema instead of pretending all datasets are the same.

Core entities:

```text
Dataset
Case
ImagingArtifact
Modality
Segmentation
Label
ModelRun
Prediction
GeneratedNote
ProvenanceRecord
```

Each dataset gets an adapter:

```text
LGG adapter
  -> reads TIFF images and masks
  -> creates abnormal-slice labels from mask presence

UTSW adapter
  -> reads NIfTI modalities and segmentations
  -> reads TSV molecular-marker metadata
  -> creates patient/case-level marker labels

BRISC adapter
  -> reads 2D classification/segmentation folders
  -> creates tumor-type classification labels
```

### Best Multi-Dataset MVP

Implement two datasets, not five.

Minimum:

1. LGG MRI Segmentation
   - proves current NB/ICA work still works
   - simple artifact-to-note pipeline

2. UTSW-Glioma 2026
   - proves serious patient-level medical KB pipeline
   - subset processing is acceptable

Optional:

3. BRISC 2025
   - fast fallback or extra external smoke-test

Do not fully implement UCSF-PDGM, BraTS, and UCSD-PTGBM unless the main two are already stable.

### Possible Thesis Claim

Good claim:

> The implemented system was evaluated on two heterogeneous brain MRI datasets: a lightweight LGG image/mask dataset and the 2026 UTSW-Glioma patient-level multimodal MRI dataset. This demonstrates that the architecture can normalize different medical-imaging sources into a unified, provenance-aware knowledge base.

Avoid claim:

> The model was trained on all available glioma datasets and is clinically generalizable.

That would require much more rigorous harmonization, external validation, and clinical evaluation.

## How Newer Datasets Improve The Knowledge Base

UTSW-Glioma enables:

- patient-level glioma case notes
- MRI modality notes
- molecular-marker notes
- segmentation evidence notes
- tumor-grade/tumor-type notes
- radiogenomics experiment notes

BRISC enables:

- tumor-type notes
- plane-specific performance notes
- segmentation evidence notes
- multi-class confusion notes

UCSF-PDGM enables:

- patient-level imaging notes
- molecular-marker notes
- survival/outcome-linked notes
- multimodal MRI provenance

UTSW-Glioma enables:

- modern glioma molecular-marker notes
- IDH / 1p19q / MGMT result notes
- richer dataset metadata notes

UCSD-PTGBM enables:

- longitudinal timepoint notes
- post-treatment progression notes
- advanced diffusion/perfusion evidence notes

BraTS enables:

- benchmark-comparable segmentation notes
- Dice/Hausdorff model-run notes
- challenge-style reproducibility notes

## Recommended Thesis Adjustment

Old title:

> MRI image classification using Naive Bayes and ICA.

Better title:

> A Cloud-Based Medical Knowledge Base for MRI Tumor Classification, Segmentation Evidence, and Model Provenance.

More conservative title:

> A Cloud-Based System for Brain MRI Classification and Auditable Medical Research Note Generation.

## Recommended One-Week Discovery Plan For UTSW-Glioma

Day 1:

- download/read metadata first, before full imaging
- inspect label completeness for IDH, MGMT, 1p/19q, tumor type, and tumor grade
- pick the first prediction target based on label availability
- generate a dataset note automatically

Day 2:

- download a small controlled subset of NIfTI cases
- inspect modalities, segmentations, naming, and voxel shapes
- build a NIfTI metadata extractor

Day 3:

- create first feature table:
  - demographics/metadata where allowed
  - segmentation-derived tumor volume
  - modality availability
  - simple intensity statistics inside tumor mask
- run NB / ICA+NB baseline on the selected target

Day 4:

- adapt or train a small DNN on a narrowed input:
  - selected slices around tumor
  - or mask-cropped 2D images
  - or simple 3D volume subset if feasible

Day 5:

- generate Markdown notes:
  - dataset note
  - patient/case note
  - molecular-marker prediction note
  - segmentation evidence note
  - model-run comparison note

Day 6:

- cloud worker proof:
  - upload a case artifact bundle
  - queue a processing job
  - run processor
  - write generated note and JSON provenance

Day 7:

- compare UTSW against LGG baseline
- decide whether BRISC is needed as fallback
- write thesis limitations and final dataset strategy

## Final Recommendation

Use **UTSW-Glioma 2026** as the main serious dataset.

Keep **LGG MRI Segmentation** as continuity and baseline.

Keep **BRISC 2025** as fallback if the 3D NIfTI pipeline becomes too slow.

Discuss **UCSF-PDGM**, **UCSD-PTGBM 2026**, and **BraTS 2024/2025** as evidence that the architecture can grow into broader patient-level medical research workflows.
