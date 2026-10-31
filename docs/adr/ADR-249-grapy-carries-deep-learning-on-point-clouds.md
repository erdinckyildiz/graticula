# ADR-249 — grapy carries deep learning on point clouds: training data, classification, object detection and evaluation

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, under the owner's decision of the same day recorded in [ADR-248](ADR-248-grapy-carries-deep-learning-on-pytorch.md) (*"Tam aç"*: deep learning opened fully, with PyTorch in the server image). That decision named the point cloud names as built on ADR-248's foundation; this ADR builds them. The choices the pages leave open are INFERRED and listed in §5.2–§5.5 and [Q-219](../open-questions.md). |
| **Supersedes** | — (it narrows ADR-224 §5.1 and ADR-229's refusals by the names below) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`arcpy.ddd` refused seven names with *"it trains or runs a deep learning model on point clouds, which grapy does not
carry"*: PreparePointCloudTrainingData, PreparePointCloudObjectDetectionTrainingData,
TrainPointCloudClassificationModel, TrainPointCloudObjectDetectionModel, ClassifyPointCloudUsingTrainedModel,
DetectObjectsFromPointCloudUsingTrainedModel and EvaluatePointCloudClassificationModel. ExtractRailsFromPointCloud was
refused partly for the same reason (ADR-229 condition 4). Since ADR-248 grapy carries PyTorch, so the reason is no
longer true. No other grapy module (`ia`, `sa`, `geoai`) names these tools.

What is left to decide is which networks the pages name that can be written from their papers in plain PyTorch, what
grapy's training data folders hold (Pro's are not published), and what the pages leave open.

## 2. Alternatives considered

### Alternative A — refuse the point cloud names until Esri's architectures can be run as Pro runs them

**Argument for.** Pro's models (`.dlpk` from Living Atlas, or trained in Pro) would then be the ones that run.

**Argument against.** Pro's models run on arcgis.learn, Esri's proprietary package, which grapy neither has nor
reproduces (ADR-248 §5.2). The refusal would be permanent, and a schedule besides.

### Alternative B — torch_geometric, torch_scatter or spconv for the networks

**Argument for.** The papers' reference implementations use them, and they are faster.

**Argument against.** spconv and parts of the others are CUDA-first, with CPU builds absent or partial on linux/arm64,
and each would be another large native dependency in the image. Every operation these networks need — k nearest
neighbours, gathers, scatter reductions, sparse convolution on occupied voxels — can be written in plain PyTorch.

### Alternative C — the papers' networks in plain PyTorch, grapy's own training data folders, the user's own models (chosen)

**Argument for.** Every architecture value the classification page names is a published network: RandLA-Net, SQN,
PointCNN and Point Transformer V3; the detection page's SECD is SECOND. Each is written from its paper and runs on the
CPU torch the image carries. A model grapy trains is an `.emd` and `.dlpk` grapy reads back.

**Argument against.** Plain-PyTorch sparse convolution and neighbour search are slower than the CUDA kernels; Pro's
`.pctd`, `.pcotd` and `.dlpk` do not load here, and grapy's do not load in Pro.

## 3. Counterarguments to the preferred option

- *The page's default architecture is Point Transformer V3, the heaviest.* It is built, and the default kept; the page
  itself advises RandLA-Net on a CPU. On the tests' 128-point blocks it trains an epoch in a few seconds; on real
  8,192-point blocks it is the slowest of the four, and nothing has been measured (condition 1, D-341).
- *A network written from a paper is not the reference implementation.* It is not, and the training tools are marked
  equivalents and warn when they run. What the papers leave to their code is INFERRED (§5.3).
- *SECOND's sparse middle extractor is computed densely.* A dense 3D convolution of a zero-filled grid, masked to the
  active sites, is exactly a submanifold convolution (inactive inputs are zero and inactive outputs are masked) and
  exactly a regular sparse convolution at its active outputs; batch normalisation runs over the active sites only. It
  costs memory proportional to the grid, not to the points.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The pages' parameters, outputs and keyword values | read 2026-10-31 for all eight names | doc.esri.com tool reference, 3D Analyst: *Prepare Point Cloud Training Data*, *Prepare Point Cloud Object Detection Training Data*, *Train Point Cloud Classification Model*, *Train Point Cloud Object Detection Model*, *Classify Point Cloud Using Trained Model*, *Detect Objects From Point Cloud Using Trained Model*, *Evaluate Point Cloud Classification Model*, *Extract Rails From Point Cloud* |
| Pro's training data is HDF5 blocks with no published layout | the page says *"partially overlapping blocks of uncompressed HDF5 files that replicate each point in four blocks"* and gives no schema | Prepare Point Cloud Training Data |
| Each point is in four blocks | 200 random points on a half-block grid | `test_point_cloud_deep_learning.Blocks` |
| A multipatch box is read back with its centre, size and yaw | a 4 × 2 × 2 prism at 30° | `Boxes.test_a_multipatch_box_is_read_back_with_its_yaw` |
| The overlap is intersection volume over union volume | half the footprint or half the height gives ⅓; a square turned a quarter gives 1 | `Boxes.test_3d_iou_…` |
| NMS keeps the most confident of overlapping boxes | IoU 0.82 and 0.43 worked by hand | `Boxes.test_nms_…` |
| AP is VOC's all-point area | TP, FP, TP over two truths gives ½ + ⅓ | `Boxes.test_average_precision_is_voc_all_point` |
| The Hilbert codes are Hilbert codes | on an 8³ grid they are a permutation of 0–511 and consecutive codes are unit steps | `Curves` |
| Every classification architecture trains end to end on the CPU, from random weights | RANDLANET, SQN, POINTCNN and POINT_TRANSFORMER_V3, one epoch each on 128-point blocks of a 20 m scene | `Classification.test_every_architecture_trains_and_writes_its_model` |
| A trained model separates buildings from ground | PointCNN, four epochs: overall accuracy above 0.9 on the reference cloud; PRESERVE_SELECTED leaves every class 2 point as it was and gives more than 90 % of the building points class 6 | `Classification.test_pointcnn_learns_the_buildings_and_classifies_the_cloud` |
| SECOND trains and detects, and its output is merged by NMS | two epochs; every detection a multipatch box, no two overlapping by more than the overlap | `Detection.test_prepare_train_and_detect` |
| `import grapy` does not import torch | a subprocess imports `grapy` and `grapy.ddd` | `Refused.test_import_grapy_does_not_import_torch` |

## 5. Decision

### 5.1 Built

| Name | How |
|---|---|
| PreparePointCloudTrainingData | blocks on a half-block grid (each point in four), dealt into several when over the limit; class codes of interest, excluded classes, relative height from a raster (bilinear), training and validation by boundary or by a second cloud; grapy's `.pctd` (§5.2) |
| PreparePointCloudObjectDetectionTrainingData | the same blocks, and each multipatch object's box (§5.3) in the blocks holding its centre; BLOCKS_WITH_OBJECTS for training only, as documented; each object type's volume quartile ratio reported; grapy's `.pcotd` |
| TrainPointCloudClassificationModel | **equivalent** — RandLA-Net (Hu et al. 2020), SQN (Hu et al. 2022), PointCNN (Li et al. 2018) or Point Transformer V3 (Wu et al. 2024) from random weights, Adam (Kingma and Ba 2015) under one-cycle (Smith and Topin 2019) or a fixed rate, the range test when no rate is given (Smith 2017); cross-entropy or focal loss (Lin et al. 2017); every selection metric; class remap, target and background classes, descriptions, min points, epoch iterations, a pretrained grapy model; each epoch's checkpoint; the two statistics CSVs |
| TrainPointCloudObjectDetectionModel | **equivalent** — SECOND (Yan, Mao and Li 2018) with VFE (Zhou and Tuzel 2018), focal loss and the sine-error angle loss; every architecture setting of the page; remap, target objects, train blocks, descriptions, both selection criteria, both rate strategies; checkpoints and epoch statistics |
| ClassifyPointCloudUsingTrainedModel | the cloud in the model's blocks, each block's class probabilities summed at its points; EDIT_ALL, EDIT_SELECTED, PRESERVE_SELECTED, output classes, boundary, excluded classes, reference height; class codes written to the LAS files, statistics recomputed |
| DetectObjectsFromPointCloudUsingTrainedModel | the model's blocks, each object code's confidence, NMS by 3D IoU at its overlap; multipatch boxes |
| EvaluatePointCloudClassificationModel | each model on the reference cloud: the confusion matrices, model statistics and class code statistics CSVs |

The networks are `_dlpcnets`'s, written in plain PyTorch (no torch_geometric, torch_scatter, spconv or CUDA-only
operator): neighbour search by chunked `cdist`, Point Transformer V3's submanifold convolutions by looking each kernel
offset's neighbour up among the sorted voxel keys, its serialisation by z-order and Hilbert codes (Skilling 2004) and
their transposes, shuffled in training.

Within the tools, refused by name: TrainPointCloudObjectDetectionModel's POINT_TRANSFORMER_V3 (*the Point Transformer
V3 paper defines a backbone and evaluates detection through other frameworks' heads, and the page names no head to put
on it*); a model given as a portal or Living Atlas URL (*a user tool has no network, and Esri's published models run on
arcgis.learn*); a definition naming an `InferenceFunction`, another framework or another configuration (ADR-248 §5.2);
and a training data folder Pro wrote (§5.2).

### 5.2 The training data folders and the model definition (INFERRED)

1. **Folders.** `<name>.pctd` (classification) and `<name>.pcotd` (detection) are grapy's own, given in `_dlpoints`'s
   docstring: `grapy_pointcloud_training.json` (format, kind, block size and limit, the attributes carried, whether
   relative height was computed, excluded classes, srid, counts and histograms per split), and `training/blocks.npz`
   and `validation/blocks.npz` (points, eight attribute columns, class codes, block offsets and corners; boxes for
   detection). numpy's uncompressed `.npz`, not HDF5: h5py is not among grapy's libraries, and the layout would be
   grapy's either way. A folder without the manifest is refused by name: *ArcGIS Pro's .pctd and .pcotd are folders of
   HDF5 blocks whose layout Esri does not publish*.
2. **Histogram.** The page's points-per-block histogram is written as `histogram_<split>.csv` (ten bins) and reported
   in the messages; no image, since grapy carries no plotting library.
3. **Model definition.** ADR-248's `.emd` keys, with `ModelType` `PointCloudClassification` or
   `PointCloudObjectDetection`, `Classes` the class or object codes with their descriptions, and a `"grapy"` object
   holding the architecture, attributes, their mean and deviation, block size and points per block (and, for SECOND,
   the voxel grid, height range, settings and anchor sizes). Out model is the `.dlpk`.

### 5.3 What the pages leave open (INFERRED)

1. **Blocks.** A block is a square whose side is `block_size` (the page's "diameter") on a grid of half a block, so each
   point is in four. A block over the limit is dealt at random (seeded by `env.randomGenerator`) into as many blocks as
   it takes. Withheld points are never exported. Codes of interest select the blocks of both splits. Without a
   validation cloud or boundary, the validation points are the input's outside the training boundary.
2. **Network input.** A block is resampled to the largest training block (at most the limit), repeating points when it
   holds fewer; x and y about the block's centre and z above its lowest point, in half block sizes; attributes
   standardised by the training data's mean and deviation.
3. **Networks.** The papers' widths and depths: RandLA-Net 16-64-128-256, K 16, sampling ratio 4; SQN its encoder and a
   256-128-64 query head over the 3 nearest neighbours at every level; PointCNN 64-128-256-384 with K and dilation (8,1),
   (12,2), (16,2), (16,4) at 1, 3/8, 3/16 and 1/16 of the points; Point Transformer V3 32-64-128-256-512 with depths
   2-2-2-6-2 and a 64-64-128-256 decoder, patches of 1024, the block on a 64-voxel grid. Random sampling takes a block's
   first points; the blocks are shuffled when read, so an inference repeats.
4. **Classification training.** Selection metrics are the classes' means; target classes need a background class and
   choose the blocks trained on; SQN takes class 0 (never classified) as unlabelled, and the other networks learn it as
   a class; focal γ 2; early stopping after 5 epochs (the page's five); checkpoints `<name>_epoch_<k>.pth`; the per-class
   CSV `<name>.checkpoints/<name>_stats.csv`, the per-epoch one `<name>/<name>_epoch_stats.csv`.
5. **Inference.** Each point's class is the most probable after summing its four blocks' probabilities; a predicted
   class not among `output_classes` leaves the point's code; without a boundary every point is read; batch size
   defaults to the training's.
6. **Evaluation.** A model's precision, recall and F1 are its classes' means; a class's accuracy is (TP + TN) over all
   points; the CSV columns are `Model`, `Reference_Class`, `C_<code>`, `Total`; `Model`, `Accuracy`, `Precision`,
   `Recall`, `F1_Score`; and `Model`, `Class_Code` and the same four. The confusion matrix PNGs are not drawn.
7. **Boxes.** A multipatch object's box is the minimum rotated rectangle of its vertices in x and y and their z range;
   its length is the longer side and its yaw that side's angle in [−π/2, π/2). An object is in a block when its centre
   is. A box has no heading, so SECOND's direction classifier is not trained.
8. **SECOND.** VOXEL_WIDTH a 64th of the block and VOXEL_HEIGHT a 16th of the height range, which is 1.25 times the
   highest training object's top above its block's lowest point; VOXEL_POINT_LIMIT 5 and 16,000 and 40,000 voxels
   (SECOND's); two anchors per object code (its training objects' mean size and height, yaw 0 and π/2) at each cell of a
   bird's-eye view of half the voxel grid; positive at bird's-eye IoU 0.6, negative under 0.45, each object's best
   anchor positive; loss weights 1 and 2. Validation AP is VOC's all-point AP at 3D IoU 0.5, over detections scoring
   0.05 or more after NMS at 0.5.
9. **Detection output.** Fields `Object_Code`, `Description` and `Confidence`; each box a prism over its yawed
   footprint.

### 5.4 Weights

As ADR-248 §5.4: nothing is baked in and nothing is downloaded. A pretrained model is a grapy `.emd` or `.dlpk`.

### 5.5 ExtractRailsFromPointCloud stays refused, for its own reason

Read again on 2026-10-31. The page takes classified rail points, names the tolerances that group them into rails, the
crown detection radius, the smoothing kernels and the simplification tolerances, and says the tool needs ArcGIS's deep
learning frameworks. It takes no model, and does not say what those frameworks compute, how the points are grouped,
how the crown is traced or how the centerline is aligned. *"grapy carries no deep learning"* is no longer the reason;
the reason is that neither the geometry nor a network is specified, as with ExtractPowerLinesFromPointCloud.

## 6. Consequences

**Positive.** The point cloud cycle runs under arcpy's names: export blocks, train a classifier or a detector, classify
a LAS dataset, detect objects, and compare models.

**Negative.**
- Nothing is measured on real lidar: block sizes of 8,192 points, Point Transformer V3's speed on a CPU, or SECOND's
  memory on a large voxel grid (D-341).
- Models do not interoperate with Pro in either direction, and neither do training data folders.
- The networks train from random weights.

**State.** None in the catalogue. A user tool writes its training data folders, models and checkpoints in its run
directory, and Classify Point Cloud rewrites the LAS files it reaches, as ADR-229's classifiers do.

**Ports created.** None: torch is imported by `_dlpcnets` and `_dlpoints` only when a tool runs.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ADR-224 §5.6: a cited equivalent that honours the page's parameters may stand for what the page does not specify | Accepted (ADR-224) |
| — | ADR-248's foundation (framework loader, definition, checkpoints, training loop) | Accepted (ADR-248) |

## 8. Dependencies

**Depends on:** [ADR-248](ADR-248-grapy-carries-deep-learning-on-pytorch.md),
[ADR-229](ADR-229-grapy-holds-las-datasets-and-tins.md) (LAS datasets),
[ADR-238](ADR-238-grapy-holds-multipatch-features.md) (multipatch features),
[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md).

**Depended on by:** —

## 9. Revisit triggers

- Esri publishes the `.pctd`/`.pcotd` layout or its point cloud `.emd` keys.
- A user tool's training reaches ADR-223's memory or time limit on real lidar.
- The ExtractRailsFromPointCloud page describes its grouping and tracing.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Measure on real lidar.** Train RandLA-Net and Point Transformer V3 on a real LAS tile of 8,192-point blocks inside
   ADR-223's limits, and record the time per epoch and the memory (D-341). **PARTLY DISCHARGED 2026-10-31**: both
   trained on 3DEP CA_SanFrancisco_1_B23 (1.44 million points, 538 training blocks of 8,192 points, 15 m). Time per
   epoch on 10 % of the blocks with 94 validation blocks: RandLA-Net about 150 s of CPU, Point Transformer V3 about
   230 s; peak commit 2,241 MB and 3,221 MB; PTv3 scored 0.920 on points it never saw. Not shown: that a training step
   fits 2 GB — on Windows neither does, and the Linux fence was not available to this run (D-341 has the numbers).
   (2026-10-31: **the Linux half is written and not yet run.** `.github/workflows/measure.yml`, `workflow_dispatch`
   only, installs what `ci.yml`'s grapy job installs and runs `tools/measure_point_cloud_fence.py`: a synthetic
   1.44-million-point cloud in D-341's 15 m, 8,192-point blocks, then RandLA-Net and Point Transformer V3 one epoch
   each through `grapy.runner` inside the fence, at ADR-223's 2 GiB of address space and 600 s of CPU, printing
   peak address space, peak resident memory, CPU and wall time. Checked unfenced on Windows with 150,000 points,
   both networks trained; the fenced numbers come from the first dispatched run.)
2. **Run the point cloud tests in CI.** `test_point_cloud_deep_learning.py` runs where torch is installed; it has run
   only on Windows until CI runs it. **DISCHARGED 2026-10-31**: it runs in `ci.yml`'s grapy job, which installs torch
   and torchvision (CPU) on Linux. In main's CI run 41211660456 (75d2398e, v1.0.490), job 124567754106, all 25 of the
   file's tests report `ok` and none is skipped, in a suite of 1,285 that ends `OK` — read from the job's own log.
3. **Confirm Q-219**, the INFERRED choices of §5.2–§5.3, with the owner. **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-219 (*"Hepsini onayla"*).
