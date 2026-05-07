# LGG MRI Dataset Thesis Integration

Date checked: 2026-05-04

User-provided prior work:

> Develop and compare optimized machine learning models for classification of MRI images using Naive Bayes and Independent Component Analysis, implemented in Python/Kaggle on the `lgg-mri-segmentation` dataset.

## Verified Dataset Context

Dataset:

- Kaggle: `mateuszbuda/lgg-mri-segmentation`
- Name: Brain MRI segmentation / LGG Segmentation Dataset
- Content: brain MRI images with manual FLAIR abnormality segmentation masks
- Source: TCIA / TCGA lower-grade glioma collection
- Patient count in Kaggle description: 110
- Format: `.tif` images and paired `_mask.tif` masks
- Metadata: `data.csv` includes tumor genomic clusters and patient data
- License: CC BY-NC-SA 4.0

Primary sources:

- https://www.kaggle.com/datasets/mateuszbuda/lgg-mri-segmentation
- https://www.cancerimagingarchive.net/collection/tcga-lgg/
- https://www.cancerimagingarchive.net/analysis-result/brats-tcga-lgg/

Important detail:

- This Kaggle dataset is not raw hospital DICOM workflow data. It is a preprocessed research dataset derived from TCIA/TCGA.
- Therefore, the thesis should avoid claiming full clinical DICOM integration unless the system also supports real DICOM input separately.

## Immediate Correction To Framing

The previous phrasing says:

> AI can contribute to ongoing medical research, uncovering new insights into diseases and treatments.

This is too broad for a bachelor thesis and too close to clinical/biomedical discovery claims.

Safer thesis phrasing:

> The system supports reproducible biomedical image-analysis workflows by converting model outputs, segmentation evidence, metadata, and processing provenance into auditable Markdown notes.

This keeps the medical value without claiming diagnosis or treatment discovery.

## What The Existing NB / ICA Work Can Become

Naive Bayes and ICA are useful as **baseline processors**, not as the whole modern model story.

Possible interpretation:

- ICA: feature extraction / dimensionality reduction from MRI slices or mask-derived features
- NB: probabilistic baseline classifier
- output: tumor-present/tumor-absent, mask-present/mask-absent, or genomic/clinical class if labels are available

But this needs a precise task definition.

The strongest thesis use:

> Preserve NB + ICA as interpretable baseline models, then compare them against one or two stronger modern alternatives, and feed all outputs into a medical-note generation pipeline.

## Better Model Comparison Options

### Option 1: Classical ML Baseline Set

Use if time is tight and implementation must stay close to the existing work.

Models:

- NB on handcrafted/radiomic-like features
- ICA + NB
- PCA + SVM
- Random Forest or XGBoost on mask/image-derived features

What notes can include:

- predicted class
- feature summary
- model comparison table
- confidence/probability
- mask-derived measurements
- uncertainty warning

Pros:

- realistic for bachelor scope
- easy to explain
- fast on CPU/VPS

Cons:

- less SOTA
- weaker medical-imaging novelty

### Option 2: Add A CNN Classifier

Use if you want the project to look more current without becoming too large.

Models:

- small custom CNN
- EfficientNet-B0 / ResNet18 transfer learning
- MobileNetV3 for lightweight inference

Task options:

- classify slice as abnormal vs normal using mask presence as label
- classify patient-level abnormality from slice aggregation
- classify genomic cluster only if labels are reliable and split is patient-safe

What notes can include:

- top predicted class
- confidence
- patient-level aggregation
- Grad-CAM or saliency image if implemented
- comparison against NB/ICA

Pros:

- more credible current ML comparison
- easy to serve as a cloud inference processor

Cons:

- must avoid data leakage
- requires careful train/validation/test split by patient, not random slice

### Option 3: U-Net / Segmentation-Oriented Pipeline

Use if the masks are central.

Models:

- U-Net
- Attention U-Net
- lightweight nnU-Net-style baseline if feasible
- compare against existing masks with Dice/IoU

What notes can include:

- predicted mask
- reference mask if available
- Dice/IoU score
- tumor area estimate
- overlay thumbnail
- model/version/provenance

Pros:

- matches the dataset better than generic classification
- produces richer medical notes with visual evidence

Cons:

- more implementation work
- note pipeline must handle images/masks, not just text

### Option 4: Hybrid Medical Note Pipeline

Use as the recommended architecture framing.

Models/processors:

- deterministic metadata parser
- NB/ICA baseline classifier
- CNN or U-Net as stronger image model
- optional MedGemma for note wording and medical-text structuring

Flow:

```text
MRI slice/study
  -> metadata extraction
  -> mask/image preprocessing
  -> NB/ICA baseline
  -> CNN or segmentation model
  -> comparison/evidence package
  -> Markdown medical research note
  -> Obsidian archive/review
```

This is the best fit with the existing distributed cloud/Obsidian plan.

## Critical Methodology Risks

### 1. Patient-Level Leakage

The dataset has many slices per patient. A random slice-level split can put slices from the same patient in both train and test sets, inflating results.

Requirement:

> Split by patient/case folder, not by image slice.

This should be written explicitly in Chapter 3 and used in evaluation.

### 2. Classification Target Ambiguity

The dataset is named as a segmentation dataset. A "classification of MRI images" task must define the label clearly.

Possible labels:

- abnormal vs non-abnormal slice based on whether mask has non-zero pixels
- patient has tumor slices vs no tumor slices
- genomic subtype from `data.csv`
- grade / histology if available and sufficiently balanced

Recommended MVP label:

> abnormal-slice classification using mask presence, then patient-level aggregation.

This is easiest to justify and evaluate.

### 3. Kaggle TIFF Is Not DICOM

The existing dataset uses `.tif` files, not raw DICOM studies.

Implication:

- MVP can process TIFF/images.
- DICOM support should be designed as an extension unless implemented with TCIA DICOM data.
- Notes should say "MRI image artifact", not "DICOM study", unless real DICOM input is used.

### 4. NB/ICA Are Not SOTA

NB and ICA are acceptable baselines. They are not a strong 2026 medical-imaging model claim.

Use them as:

- interpretable baseline
- low-compute local/VPS model
- comparison point against CNN/U-Net

Do not position them as state-of-the-art.

## Medical Notes That Become Possible

### 1. MRI Classification Note

Fields:

- dataset/source
- patient/case id
- slice id
- preprocessing version
- model results
- probability/confidence
- abnormality flag
- image thumbnail
- mask/evidence link
- limitations

### 2. Model Comparison Note

Fields:

- NB result
- ICA + NB result
- CNN/U-Net result
- agreement/disagreement
- selected final label
- uncertainty warning

This is especially good for the thesis because it connects model comparison with note generation.

### 3. Segmentation Evidence Note

Fields:

- original MRI
- ground-truth mask if available
- predicted mask if implemented
- Dice/IoU
- tumor area estimate
- overlay image

### 4. Patient/Case Summary Note

Fields:

- all processed slices
- number of abnormal slices
- max abnormal area
- model agreement rate
- links to slice notes
- provenance

This makes Obsidian graph/usefulness more obvious.

### 5. Dataset/Experiment Note

Fields:

- dataset citation
- license
- split definition
- model versions
- metrics
- run id
- artifact hashes

This supports reproducibility, which is safer and more thesis-friendly than clinical claims.

## Recommended Thesis Pivot

New working title:

> **A Cloud-Based System for MRI Image Classification and Auditable Medical Note Generation Using Interpretable and Lightweight Machine Learning Models**

Alternative:

> **A User-Owned Cloud Pipeline for Brain MRI Analysis Artifacts and Obsidian-Compatible Medical Research Notes**

Best thesis contribution:

> Not just model accuracy. The contribution is the full pipeline from MRI artifact to auditable Markdown note, including model comparison, provenance, uncertainty, and backup/recovery.

## Recommended MVP

Minimum:

- upload/select LGG MRI image or case folder
- run NB/ICA baseline
- run one stronger model if feasible, preferably small CNN
- generate Markdown note with result/provenance
- store JSON sidecar
- display in Obsidian
- deploy cloud backend via Terraform for two VPS providers

Strong stretch:

- segmentation model or mask overlay
- patient-level aggregation
- MedGemma-generated note wording with strict source-grounding

Avoid:

- clinical diagnosis claims
- treatment recommendations
- random slice-level split
- claiming DICOM if only TIFF input is used
- claiming NB/ICA are SOTA

