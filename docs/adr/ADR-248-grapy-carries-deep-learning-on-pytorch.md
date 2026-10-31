# ADR-248 — grapy carries deep learning on PyTorch: training data, training, inference and accuracy for imagery

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, by owner decision. ADR-224 group 3 refused the deep learning names because *"it needs a pretrained model or a deep learning framework, which a user tool on this server does not have"*. Asked whether to keep that refusal, add deep learning as an optional extra, or open it fully, the owner chose *"Tam aç"*: training and inference go into the server image and into grapy, with PyTorch, accepting that the image grows by about 2 GB and CI gets slower. The choices the tool pages leave open are INFERRED and listed in §5.3–§5.6 and [Q-218](../open-questions.md). |
| **Supersedes** | — (it narrows ADR-224 §5.1's group 3 by the names below; ADR-224 keeps the rest) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) refused sixteen `arcpy.ia` and `arcpy.sa` names as
group 3 (`sa._IA_GROUP_3`, and DetectControlPoints and TransformImageShapes with reasons of their own). The reason was
true of the image as it was: no framework, no model. Nothing about the names themselves forced it. Four of them need no
framework at all — exporting training chips, non-maximum suppression and the two accuracy tools — and the rest need a
framework and a model the user trains or supplies, not one Esri ships.

The owner's decision removes the first half of the reason. What remains is the second: which models grapy can build,
train and read back, and which a page ties to Esri's own trained models or to Esri's proprietary `arcgis.learn`.

The seven point cloud deep learning names in `arcpy.ddd`, and ExtractRailsFromPointCloud, are not this ADR's. They are
built on its foundation, `grapy._deeplearning`, by another ADR.

## 2. Alternatives considered

### Alternative A — keep ADR-224's refusal

**Argument for.** Nothing is claimed that grapy cannot back with Pro's own models; the image stays small.

**Argument against.** It refuses four names that need no framework, and it refuses training a model on the user's own
data, which is what most of these tools are for. The owner rejected it.

### Alternative B — deep learning as an optional extra, installed by the administrator

**Argument for.** The image stays small for the deployments that never train a model.

**Argument against.** A toolbox written against arcpy would then work on one Graticula and fail on another, by
configuration. The owner rejected it.

### Alternative C — PyTorch and torchvision in the image; torchvision's published architectures, trained from random weights; the user's own models, never Esri's (chosen)

**Argument for.** torchvision holds the papers' detectors, segmenters and classifiers under a BSD licence, and runs on
the CPU on both of the image's architectures. Every network is a published architecture with a citation. A model grapy
trains is written as an `.emd` and `.dlpk` that grapy reads back.

**Argument against.** About 2 GB more image. A model trained from random weights needs more data and more epochs than
one fine-tuned from ImageNet. Pro's `.dlpk` files do not run here.

### Alternative D — the same, with torchvision's pretrained weights baked into the image

**Argument for.** Fine-tuning from ImageNet or COCO weights is how these tools are normally used.

**Argument against.** torchvision's README says its pretrained models *"may have their own licenses or terms and
conditions derived from the dataset used for training"*, names the SWAG models as CC-BY-NC 4.0, and leaves it to the user
*"to determine whether you have permission to use the models for your use case"*. ImageNet's and COCO's terms are not
torchvision's to grant. With the terms unclear, nothing is baked (§5.4).

## 3. Counterarguments to the preferred option

- *A deep learning tool that cannot run Pro's models is not the tool.* What the tools do — tile, infer, merge, write
  features or a raster — is built to the pages. What they run is a model the user trained with grapy. A Pro `.dlpk`
  names an `InferenceFunction`, a Python raster function that imports `arcgis.learn`. That is Esri's proprietary code,
  and it is refused by name with that reason.
- *Training from random weights on a CPU is slow.* It is. The fence's CPU and wall-clock limits (ADR-223) apply as to any
  tool, and §11 condition 2 measures training inside the fence.
- *Two gigabytes for a feature few deployments use.* The owner weighed this and chose it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The `.emd` keys are publicly documented | `Framework`, `ModelConfiguration`, `ModelFile`, `InferenceFunction`, `ModelType`, `ImageHeight`, `ImageWidth`, `ExtractBands`, `DataRange`, `BatchSize`, `ModelPadding`, `ModelName`, `Version` | Esri's public `raster-deep-learning` repository, `docs/writing_model_definition.md` (read 2026-10-31) |
| A `.dlpk` holds exactly one `.emd` | Pro's error 260458 | doc.esri.com tool errors 260451–260475 |
| PASCAL VOC boxes are 1-based inclusive pixels; KITTI has 15 columns | written as specified, read back | `test_deep_learning.ExportTrainingData` |
| Greedy NMS keeps the most confident of boxes overlapping above the ratio, in two passes | IoU 90/110 worked by hand | `test_deep_learning.Suppression` |
| AP is VOC's all-point area | TP, FP, TP over two truths gives ½·1 + ½·⅔ | `test_deep_learning.ObjectAccuracy` |
| Kappa is Cohen's | [[45, 5], [10, 40]] gives 0.7 | `test_deep_learning.PixelAccuracy` |
| Every model type grapy builds trains end to end on the CPU, from random weights, in seconds | UNET, PSPNET, DEEPLAB, FASTERRCNN, RETINANET, MASKRCNN, FEATURE_CLASSIFIER, CHANGEDETECTOR, PIX2PIX, CYCLEGAN on 64 × 64 synthetic scenes; six epochs of U-Net classify > 80 % of cells right | `test_deep_learning.Training` |
| An image-space shape reaches the ground through the frame's collinearity model | a 64-pixel nadir photo from 25 m at 5 cm a pixel; 20 m above a 5 m DEM gives four fifths | `test_deep_learning.ImageSpace` |
| torch and torchvision are permissive | BSD-3-Clause and the bundled libraries' licences | [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) |
| The CPU wheels exist for both of the image's architectures | `torch-2.15.0+cpu` and `torchvision-0.29.x+cpu` for `manylinux_2_28_x86_64` and `_aarch64`, CPython 3.12 | `download.pytorch.org/whl/cpu`, listed 2026-10-31 |
| Installed size | torch 549 MB, torchvision 7.7 MB (the Windows CPU build); the Linux image is not measured yet | `du`, 2026-10-31; condition 1 |

## 5. Decision

### 5.1 Built

| Name | How |
|---|---|
| ExportTrainingDataForDeepLearning (ia, sa) | every metadata format of the page (§5.3) |
| NonMaximumSuppression | greedy NMS by intersection over union, per class then across classes, as the page's two passes (Felzenszwalb et al. 2010; Girshick et al. 2014) |
| TrainDeepLearningModel | **equivalent** — the model type's published network (§5.4), Adam under a one-cycle schedule (Kingma and Ba 2015; Smith and Topin 2019), the range test for the learning rate (Smith 2017) |
| TrainUsingAutoDL | **equivalent** — a search over grapy's networks in the time allowed; ADVANCED also over backbones and log-uniform learning rates (Bergstra and Bengio 2012) |
| ClassifyPixelsUsingDeepLearning, DetectChangeUsingDeepLearning, TranslatePixelsUsingDeepLearning | tiles of the model's chip size overlapping by `padding`, each tile's centre kept |
| DetectObjectsUsingDeepLearning | the same tiling; detections in a tile's padding dropped; the tiles merged by NMS at `nms_overlap`; `NMS` runs the two passes at `max_overlap_ratio`; `PIXELSPACE` on Frame Camera items (§5.6) |
| ClassifyObjectsUsingDeepLearning | a chip per feature (or per image), classified |
| ComputeAccuracyForObjectDetection | **equivalent** — PASCAL VOC matching and all-point AP (Everingham et al. 2010; 2015) |
| ComputeAccuracyForPixelClassification | **equivalent** — stratified random points (Congalton and Green 2019), confusion matrix, Cohen's kappa |
| DeepLearningModelToEcd (ia, sa) | a grapy pixel model and the page's class JSON into grapy's `.ecd`, which Classify Raster and Classify run |
| ExtractFeaturesUsingAIModels | **equivalent** — the user's grapy models, then Regularize Building Footprint |
| TransformImageShapes | the `iShape` grapy writes, carried to the ground through the Frame Camera model (ADR-234), over a DEM when given |

Within each, what the page offers and grapy does not do is refused by name: Esri's model types and backbones without a
published random-init network, `tensorboard`, augmentation `FILE`, monitors `CORPUS_BLEU` and `MULTI_LABEL_FBETA`, test
time augmentation for detectors, feature classes of image attachments, `pretrained_models` and two of Extract Features'
workflows (§5.6).

### 5.2 The model definition (INFERRED)

grapy writes and reads `.emd` with the documented keys: `Framework` `PyTorch`, `ModelConfiguration` `grapy`, no
`InferenceFunction`, `ModelType` one of `ObjectDetection`, `ImageClassification` (pixels, as the repository uses it),
`ObjectClassification`, `ChangeDetection` and `PixelTranslation`. `ModelFile` is a PyTorch `state_dict` beside it, read
with `weights_only`, so a checkpoint is tensors and never code. A `"grapy"` object holds the architecture, backbone,
bands, class order, normalisation and default model arguments. The `.dlpk` is a zip of the `.emd` and the weights.

A definition is refused by name when it names an `InferenceFunction` (*Esri's arcgis.learn, which grapy neither has nor
reproduces*), when its `Framework` is not PyTorch, or when its `ModelConfiguration` is not `grapy`.

### 5.3 Training data and accuracy (INFERRED)

1. PASCAL VOC and KITTI follow their devkits. The other formats are grapy's own layouts, given in `_dlchips`'s
   docstring: Classified_Tiles labels as a class raster; RCNN_Masks as two bands, class and instance; Labeled_Tiles and
   Imagenet one chip per sample with a VOC label; MultiLabeled_Tiles one VOC object per class present; Export_Tiles and
   CycleGAN `images2/`; Panoptic `instance_labels/`. Change detection is Classified_Tiles with `in_raster2`. Every export
   writes `map.txt` and `grapy_training.json`.
2. Tile size 256 and stride 128 by default (the page's examples). Only whole windows are cut. NoData is written 0.
   Pixels are sampled at the nearest cell. A rotated chip turns about its centre, anticlockwise. A sample that only
   touches a chip is not on it. A classified raster's samples are its 8-connected regions.
3. With no class field, every sample is class 1.
4. Object accuracy: `min_iou` 0.5; confidence from a `Confidence` field; a `Class` field and an `All` row whose AP is
   the classes' mean. The PDF report is refused.
5. Pixel accuracy: the draw is seeded by `env.randomGenerator`; strata are rounded up. The table follows the page's
   example (`ClassValue`, `C_n`, `Total`, `U_Accuracy`, `Kappa`; rows `P_Accuracy` and `Kappa`), plus an `IoU` row.
6. Training: batch size 2; one chip in ten held out to validate; early stopping after 5 epochs without improvement;
   FREEZE_MODEL freezes nothing without a pretrained model. DEFAULT augmentation is a dihedral turn, the page's
   brightness (0.4, 0.6) and contrast (0.75, 1.5), and zoom and crop each at probability 0.5. Brightness shifts the
   normalised image by 2(b − ½) deviations, and contrast scales it about its mean.
7. Inference: default padding is a quarter of the chip for pixel models and an eighth for detectors; threshold 0.5 and
   nms_overlap 0.1 (the page's); a detection is in the padding when its box centre is; the detector's own score cut-off
   follows `threshold`; the output fields are `Class`, `Classvalue` and the confidence; ClassifyObjects writes
   `ClassLabel` and `Confidence`, its chip the feature's box made square.
8. DeepLearningModelToEcd's background is class 0, `Background`.

### 5.4 The networks, and no pretrained weights (INFERRED)

The networks and their papers are listed in `_dlnets`'s docstring: Faster R-CNN, RetinaNet and Mask R-CNN on a ResNet
FPN, SSD300 on VGG-16, U-Net on a ResNet encoder, DeepLabv3, PSPNet, torchvision's classifiers, FC-Siam-diff on a ResNet
encoder, pix2pix and CycleGAN. TrainUsingAutoDL also uses FCOS and FCN.

- Backbones start from random weights with ordinary batch norm. Detector anchors scale with the chip.
- pix2pix and CycleGAN are trained with one combined loss per step.
- **No pretrained weights are baked into the image.** torchvision's terms for its weights are not torchvision's BSD
  licence and are left to the user, so they are not clear. A user who has weights they may use gives them as a grapy
  `.emd` or `.dlpk` in `pretrained_model`.
- Tests never download.
- Model types are refused when their network is Esri's and unpublished (CONNECTNET, MULTITASK_ROADEXTRACTOR), a
  foundation model's weights (CLIMAX, SAMLORA), another framework (MMDETECTION, MMSEGMENTATION), a loss on pretrained
  weights (SUPERRESOLUTION), or a published network neither in torchvision nor written here (YOLOV3, DETREG, RTDETRV2,
  HED and BDCN, MAXDEEPLAB, PSETAE, SIAMMASK, DEEPSORT, 3DRCNET, PIX2PIXHD). IMAGECAPTIONER is refused because the
  training data carries no vocabulary.

### 5.5 TrainUsingAutoDL (INFERRED)

The best model is the one with the least validation loss. Each network trains up to 20 epochs or its share of the time.
ADVANCED tries ResNet-18, -34 and -50 with rates from 1e-4 to 1e-2 on the best two. AutoDL's MMDetection and
MMSegmentation networks, and those neither in torchvision nor written here, are refused by name when asked for.

### 5.6 Image space, and Extract Features (INFERRED)

- **`iShape`.** It is well-known text in the image's pixel coordinates (column and row from its corner). `ImageID` is
  the Frame Camera item's OBJECTID. The page names the field but not its encoding. With `PIXELSPACE`, Detect Objects
  writes the shape at the item's average height. Transform Image Shapes carries `iShape` again, through a DEM by five
  height iterations.
- **Extract Features Using AI Models.**
  - The outputs are named `<out_prefix>_<n>` in a GeoPackage in `out_location`, and `out_features` names a single
    model's.
  - `out_summary` has the fields `Output` and `Path`.
  - Refused by name: `pretrained_models`, because they are Esri's and downloaded from Living Atlas; the Line and Parcel
    Regularization workflows, because the page gives their parameters without the order or operations; and Polygon
    Segmentation, because it prompts a segmentation model the page does not name.

### 5.7 Still refused

- **DetectObjectsUsingText.** Its reference page answered 404 at both the image-analyst and the geoai paths on
  2026-10-31, so its parameters are unknown. The text-prompted detection Esri documents elsewhere is its Text SAM model:
  Grounding DINO and the Segment Anything Model, with weights from Living Atlas. Grounding DINO's text encoder and
  deformable attention are in neither torchvision nor grapy.
- **DetectControlPoints.** Its page says it uses *"two deep learning models trained by Esri"*. Esri does not publish
  them, and the tool takes no model a user could supply.

## 6. Consequences

**Positive.** A user can now do the whole cycle under arcpy's own names: export chips from their own imagery, train,
detect, classify, translate and measure. The four framework-free tools run anywhere.

**Negative.**
- The image grows, by about 2 GB on the owner's estimate; the Windows CPU build measured 0.55 GB installed. CI installs
  torch twice, and the grapy job's tests run longer (the suite's own time is in the commit).
- Models do not interoperate with Pro in either direction.
- A model trained from random weights is weaker than a fine-tuned one, and training on a CPU is slow.

**State.** None in the catalogue. A user tool writes its own files: chips, models (`.emd`, `.pth`, `.dlpk`), rasters and
feature classes, in its run directory.

**Ports created.** None in Tier 1 signatures: torch is imported inside `_deeplearning`, `_dlnets` and `_dltools` only
when a tool runs, and `import grapy` works without it (tested).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ADR-224 §5.6: a cited equivalent that honours the page's parameters may stand for what the page does not specify | Accepted (ADR-224) |
| — | torch's CPU build runs inside ADR-223's fence (Landlock, seccomp, a 2 GB address space) | Tested in `TrainingInsideTheFence`, Linux only — condition 2 |

## 8. Dependencies

**Depends on:** [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md),
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence),
[ADR-234](ADR-234-grapy-holds-terrain-datasets-and-ortho-mapping.md) (Frame Camera models),
[ADR-243](ADR-243-grapy-builds-the-image-classifiers-and-solar-radiation-as-equivalents.md) (grapy's `.ecd`).

**Depended on by:** the point cloud deep learning names in `arcpy.ddd`, built on `grapy._deeplearning`.

## 9. Revisit triggers

- torchvision's pretrained weights get terms that allow redistribution in a server image.
- Esri publishes the network of a model type refused as unpublished.
- A user tool's training reaches ADR-223's memory or time limit on real imagery.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Measure the image.** Build the server image on linux/arm64 and x86-64 and record its size before and after.
2. **Run torch inside the fence on Linux.** `test_deep_learning.TrainingInsideTheFence` trains a U-Net in a fenced tool
   under the server's default 2 GB limit. It is skipped on Windows and has not run until CI runs it.
3. **Read the Linux wheels' bundled licences.** The licences of the bundled libraries were read from the Windows CPU
   wheel; the aarch64 and x86-64 wheels the image installs (OpenBLAS, the GNU OpenMP runtime) are not read yet.
4. **Confirm Q-218**, the INFERRED choices of §5.2–§5.6, with the owner.
