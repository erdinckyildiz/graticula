"""Deep learning on imagery for arcpy.ia and arcpy.sa: training, inference, AutoDL and model conversion — ADR-248.

The networks are ``_dlnets``'s, the framework, model definition and training loop ``_deeplearning``'s, and the training
data ``_dlchips``'s. Training is an equivalent (ADR-224 §5.6): Pro trains with Esri's arcgis.learn, whose code is
proprietary and is not reproduced; grapy trains the published architecture the model type names, from random weights,
with Adam under a one-cycle schedule, and says so when it runs.

Inference follows the tool pages: the raster is cut into tiles of the model's chip size that overlap by ``padding``
pixels; a pixel model keeps each tile's centre and a detector drops the detections in the padding
(``exclude_pad_detections``) and merges the tiles' detections with non-maximum suppression at ``nms_overlap``.
"""
from __future__ import annotations

import json
import math
import os

import numpy as np
import shapely

from . import _deeplearning as dl
from . import _dlchips as chips
from ._runtime import AddWarning, ExecuteError, NotBuiltError, _is_given, env

TOOL_TRAIN = "TrainDeepLearningModel"

# ---- model types


DETECTION = ("FASTERRCNN", "RETINANET", "MASKRCNN", "SSD", "FCOS")
PIXEL = ("UNET", "DEEPLAB", "PSPNET", "FCN")
TASK = {**{a: "ObjectDetection" for a in DETECTION}, **{a: "ImageClassification" for a in PIXEL},
        "FEATURE_CLASSIFIER": "ObjectClassification", "CHANGEDETECTOR": "ChangeDetection",
        "PIX2PIX": "PixelTranslation", "CYCLEGAN": "PixelTranslation"}
FORMATS_OF = {
    "FASTERRCNN": ("PASCAL_VOC_rectangles", "KITTI_rectangles"), "RETINANET": ("PASCAL_VOC_rectangles", "KITTI_rectangles"),
    "FCOS": ("PASCAL_VOC_rectangles", "KITTI_rectangles"), "SSD": ("PASCAL_VOC_rectangles", "KITTI_rectangles"),
    "MASKRCNN": ("RCNN_Masks",), "UNET": ("Classified_Tiles",), "DEEPLAB": ("Classified_Tiles",),
    "PSPNET": ("Classified_Tiles",), "FCN": ("Classified_Tiles",),
    "FEATURE_CLASSIFIER": ("Labeled_Tiles", "Imagenet", "MultiLabeled_Tiles"), "CHANGEDETECTOR": ("Classified_Tiles",),
    "PIX2PIX": ("Export_Tiles",), "CYCLEGAN": ("CycleGAN",),
}
_UNPUBLISHED = "it is a model of Esri's arcgis.learn whose network Esri does not publish as a paper or a public architecture"
_NOT_IN_TORCHVISION = ("its network ({}) is published but is neither in torchvision nor among the architectures grapy "
                       "writes from their papers (INFERRED, ADR-248 §5.4: U-Net, PSPNet, the siamese change detector, "
                       "pix2pix and CycleGAN)")
REFUSED_TYPES = {
    "3DRCNET": _NOT_IN_TORCHVISION.format("a 3D residual network for multidimensional pixel classification"),
    "BDCN_EDGEDETECTOR": _NOT_IN_TORCHVISION.format("BDCN, He et al. 2019"),
    "HED_EDGEDETECTOR": _NOT_IN_TORCHVISION.format("HED, Xie and Tu 2015"),
    "CLIMAX": "ClimaX is a foundation model used through its pretrained weights, which grapy does not carry",
    "CONNECTNET": _UNPUBLISHED,
    "MULTITASK_ROADEXTRACTOR": _UNPUBLISHED,
    "DEEPSORT": _NOT_IN_TORCHVISION.format("Deep SORT's tracker and re-identification network, Wojke et al. 2017"),
    "DETREG": _NOT_IN_TORCHVISION.format("DETReg, Bar et al. 2022"),
    "IMAGECAPTIONER": "an image captioner needs a vocabulary and a language model, which Train Deep Learning Model's "
                      "training data (image chips and classes) does not carry",
    "MAXDEEPLAB": _NOT_IN_TORCHVISION.format("MaX-DeepLab, Wang et al. 2021"),
    "MMDETECTION": "it runs OpenMMLab's MMDetection framework, which is not among grapy's libraries",
    "MMSEGMENTATION": "it runs OpenMMLab's MMSegmentation framework, which is not among grapy's libraries",
    "PIX2PIXHD": _NOT_IN_TORCHVISION.format("pix2pixHD, Wang et al. 2018"),
    "PSETAE": _NOT_IN_TORCHVISION.format("PSE-TAE, Garnot et al. 2020"),
    "RTDETRV2": _NOT_IN_TORCHVISION.format("RT-DETRv2, Lv et al. 2024"),
    "SAMLORA": "it fine-tunes the Segment Anything Model's pretrained weights, which grapy does not carry",
    "SIAMMASK": _NOT_IN_TORCHVISION.format("SiamMask, Wang et al. 2019"),
    "SUPERRESOLUTION": "its loss is a perceptual loss on a pretrained VGG network (Johnson et al. 2016), whose weights "
                       "grapy does not carry",
    "YOLOV3": _NOT_IN_TORCHVISION.format("YOLOv3, Redmon and Farhadi 2018"),
}
DEFAULT_TYPE = {"PASCAL_VOC_rectangles": "FASTERRCNN", "KITTI_rectangles": "FASTERRCNN", "RCNN_Masks": "MASKRCNN",
                "Classified_Tiles": "UNET", "Labeled_Tiles": "FEATURE_CLASSIFIER", "Imagenet": "FEATURE_CLASSIFIER",
                "MultiLabeled_Tiles": "FEATURE_CLASSIFIER", "Export_Tiles": "PIX2PIX", "CycleGAN": "CYCLEGAN"}
MONITORS = {"VALID_LOSS": ("valid_loss", False), "AVERAGE_PRECISION": ("average_precision", True),
            "ACCURACY": ("accuracy", True), "F1_SCORE": ("f1", True), "MIOU": ("miou", True), "DICE": ("dice", True),
            "PRECISION": ("precision", True), "RECALL": ("recall", True)}


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _word(value, default, allowed, tool, parameter):
    return chips._word(value, default, allowed, tool, parameter)


def arguments_of(value):
    """Model arguments as a dict: a value table (list of [name, value]), a dict, or ``"name value;name value"``."""
    if not _is_given(value):
        return {}
    if isinstance(value, dict):
        return {str(k).strip().lower(): v for k, v in value.items()}
    if isinstance(value, str):
        pairs = [p.strip().split(None, 1) for p in value.split(";") if p.strip()]
    else:
        pairs = [list(p) for p in value]
    return {str(p[0]).strip().lower(): (p[1] if len(p) > 1 else None) for p in pairs}


def _boolean(value):
    return str(value).strip().lower() in ("true", "1", "yes")


# ---- the training data as tensors


class Data:
    """A training folder's chips, read into memory: images (bands × rows × columns, float32), targets by task."""

    def __init__(self, folders, architecture, tool):
        manifests, rows = [], []
        for folder in folders:
            manifest, found = chips.read_training(folder, tool)
            manifests.append(manifest)
            rows.extend(found)
        formats = {m["metadata_format"] for m in manifests}
        if len(formats) != 1:
            raise ValueError(f"{tool}: the training folders hold different metadata formats ({', '.join(sorted(formats))}).")
        self.format = formats.pop()
        if self.format not in FORMATS_OF[architecture]:
            raise ValueError(f"{tool}: model type {architecture} trains on {' or '.join(FORMATS_OF[architecture])} "
                             f"training data, and the folder holds {self.format}.")
        if architecture == "CHANGEDETECTOR" and not all(m.get("change_detection") for m in manifests):
            raise ValueError(f"{tool}: CHANGEDETECTOR trains on Classified_Tiles exported with in_raster2 (images before "
                             "and after); the folder holds one image per chip.")
        bands = {m["bands"] for m in manifests}
        if len(bands) != 1:
            raise ValueError(f"{tool}: the training folders' images have different band counts.")
        self.bands = bands.pop()
        self.tile = tuple(manifests[0]["tile_size"])
        classes = {}
        for m in manifests:
            for c in m["classes"]:
                classes.setdefault(int(c["value"]), c["name"])
        self.classes = [{"value": v, "name": classes[v]} for v in sorted(classes) if v != 0]
        self.index = {c["value"]: k + 1 for k, c in enumerate(self.classes)}
        self.architecture = architecture
        self.items = [self._read(image, label, second) for image, label, second in rows]
        self.domain_b = []
        if self.format == "CycleGAN":
            for m in manifests:
                self.domain_b.extend(_read_image(p) for p in m.get("domain_b", []))
        if not self.items:
            raise ExecuteError(f"ERROR: {tool}: the training data holds no chip.")
        stacked = np.stack([i["image"] for i in self.items])
        self.mean = stacked.mean(axis=(0, 2, 3))
        self.std = np.where(stacked.std(axis=(0, 2, 3)) > 0, stacked.std(axis=(0, 2, 3)), 1.0)
        self.range = (float(stacked.min()), float(stacked.max()))
        self.target_range = None
        if architecture in ("PIX2PIX", "CYCLEGAN"):
            targets = [i["image2"] for i in self.items if i.get("image2") is not None] + self.domain_b
            if not targets:
                raise ValueError(f"{tool}: {architecture} needs a second image per chip (images2), exported with in_raster2.")
            t = np.stack(targets)
            self.target_range = (float(t.min()), float(t.max()))
            self.target_bands = int(t.shape[1])

    def _read(self, image, label, second):
        item = {"image": _read_image(image)}
        if second is not None:
            item["image2"] = _read_image(second)
        fmt = self.format
        if fmt in ("PASCAL_VOC_rectangles", "KITTI_rectangles"):
            objects = chips.read_voc(label) if fmt == "PASCAL_VOC_rectangles" else chips.read_kitti(label)
            item["boxes"] = np.array([b for _, b in objects], np.float32).reshape(-1, 4)
            item["labels"] = np.array([self._class(v) for v, _ in objects], np.int64)
        elif fmt == "RCNN_Masks":
            bands = _read_image(label).astype(np.int64)
            classes, ids = bands[0], bands[1]
            masks, labels = [], []
            for k in [int(v) for v in np.unique(ids) if v != 0]:
                m = ids == k
                masks.append(m)
                labels.append(self._class(int(np.bincount(classes[m]).argmax())))
            item["masks"] = np.array(masks, np.uint8).reshape(-1, *ids.shape)
            item["labels"] = np.array(labels, np.int64)
            item["boxes"] = np.array([_mask_box(m) for m in masks], np.float32).reshape(-1, 4)
        elif fmt == "Classified_Tiles":
            values = _read_image(label)[0].astype(np.int64)
            out = np.zeros(values.shape, np.int64)
            for v in np.unique(values):
                if int(v) != 0:
                    out[values == v] = self._class(int(v))
            item["mask"] = out
        elif fmt in ("Labeled_Tiles", "Imagenet", "MultiLabeled_Tiles"):
            objects = chips.read_voc(label)
            if fmt == "MultiLabeled_Tiles":
                item["multi"] = sorted({self._class(v) for v, _ in objects})
            else:
                item["label"] = self._class(objects[0][0]) if objects else 0
        return item

    def _class(self, value):
        value = int(value)
        if value not in self.index:
            self.classes.append({"value": value, "name": str(value)})
            self.index[value] = len(self.classes)
        return self.index[value]


def _read_image(path):
    import rasterio
    with rasterio.open(path) as source:
        return source.read().astype(np.float32)


def _mask_box(mask):
    rows, cols = np.nonzero(mask)
    return [float(cols.min()), float(rows.min()), float(cols.max() + 1), float(rows.max() + 1)]


# ---- augmentation: one affine for the image, its second image, its masks and boxes


class Augmentation:
    """Train Deep Learning Model's augmentation. DEFAULT is the page's ``crop``, ``dihedral_affine``, ``brightness``,
    ``contrast`` and ``zoom``; CUSTOM takes the page's ``rotate``, ``brightness``, ``contrast``, ``zoom`` and ``crop``.

    INFERRED (ADR-248 §5.3): DEFAULT's values are those the page gives CUSTOM's defaults — brightness (0.4, 0.6) and
    contrast (0.75, 1.5), each always applied — with a dihedral turn always and zoom ``scale(1.0, 1.1)`` and crop
    (0.9 of the chip at a random place, resized back) each with probability 0.5; brightness b shifts the normalised
    image by 2(b − 0.5) standard deviations and contrast c scales it about its mean by c; rotate is a turn by up to the
    angle (or within the range) either way."""

    def __init__(self, kind, parameters, tool):
        self.kind = kind
        self.plan = {}
        if kind == "DEFAULT":
            self.plan = {"dihedral": 1.0, "brightness": ((0.4, 0.6), 1.0), "contrast": ((0.75, 1.5), 1.0),
                         "zoom": ((1.0, 1.1), 0.5), "crop": ((0.9, 0.5, None, None), 0.5)}
        elif kind == "CUSTOM":
            for name, value in arguments_of(parameters).items():
                if name not in ("rotate", "brightness", "contrast", "zoom", "crop"):
                    _refuse(tool, "augmentation_parameters", name, "the page's augmentations are rotate, brightness, "
                                                                   "contrast, zoom and crop")
                self.plan[name] = _augmentation_value(name, str(value), tool)
            if not self.plan:
                raise ValueError(f"{tool}: CUSTOM augmentation needs augmentation_parameters.")

    def __call__(self, item, rng, torch):
        if not self.plan:
            return item
        item = dict(item)
        size = item["image"].shape[-2:]
        theta = np.eye(3)
        if "dihedral" in self.plan:
            k, flip = int(rng.integers(4)), bool(rng.integers(2))
            turn = np.array([[0, -1, 0], [1, 0, 0], [0, 0, 1]], float)
            theta = theta @ np.linalg.matrix_power(turn, k)
            if flip:
                theta = theta @ np.diag([-1.0, 1.0, 1.0])
        if "rotate" in self.plan:
            (low, high), p = self.plan["rotate"]
            if rng.random() < p:
                a = math.radians(rng.uniform(low, high))
                theta = theta @ np.array([[math.cos(a), -math.sin(a), 0], [math.sin(a), math.cos(a), 0], [0, 0, 1]])
        if "zoom" in self.plan:
            (low, high), p = self.plan["zoom"]
            if rng.random() < p:
                s = 1.0 / rng.uniform(low, high)
                cx, cy = rng.uniform(-(1 - s), 1 - s), rng.uniform(-(1 - s), 1 - s)
                theta = theta @ np.array([[s, 0, cx], [0, s, cy], [0, 0, 1]])
        if "crop" in self.plan:
            (fraction, p, row, col), chance = self.plan["crop"]
            if rng.random() < chance:
                s = float(fraction) if float(fraction) <= 1 else float(fraction) / max(size)
                col = rng.random() if col is None else float(col)
                row = rng.random() if row is None else float(row)
                theta = theta @ np.array([[s, 0, (2 * col - 1) * (1 - s)], [0, s, (2 * row - 1) * (1 - s)], [0, 0, 1]])
        if not np.allclose(theta, np.eye(3)):
            item = _warp(item, theta, size, torch)
        image = item["image"]
        if "brightness" in self.plan:
            (low, high), p = self.plan["brightness"]
            if rng.random() < p:
                image = image + 2.0 * (rng.uniform(low, high) - 0.5)
        if "contrast" in self.plan:
            (low, high), p = self.plan["contrast"]
            if rng.random() < p:
                mean = image.mean()
                image = (image - mean) * rng.uniform(low, high) + mean
        item["image"] = image
        return item


def _augmentation_value(name, text, tool):
    """The page's forms: ``30.0; 0.5`` or ``(a, b); p``; ``scale(a, b); p``; ``size; p; row_pct; col_pct``."""
    parts = [p.strip() for p in text.split(";")]
    try:
        if name == "crop":
            size = float(parts[0])
            p = float(parts[1]) if len(parts) > 1 else 1.0
            row = float(parts[2]) if len(parts) > 2 else None
            col = float(parts[3]) if len(parts) > 3 else None
            return (size, p, row, col), p
        head = parts[0].lower().replace("scale", "").strip()
        p = float(parts[1]) if len(parts) > 1 else 1.0
        if head.startswith("("):
            low, high = (float(v) for v in head.strip("()").split(","))
        else:
            v = float(head)
            low, high = (-v, v) if name == "rotate" else (v, v)
        return (low, high), p
    except (ValueError, IndexError) as error:
        raise ValueError(f"{tool}: augmentation_parameters {name} '{text}' is not in the page's form.") from error


def _warp(item, theta, size, torch):
    F = torch.nn.functional
    h, w = size
    matrix = torch.tensor(theta[:2], dtype=torch.float32).unsqueeze(0)
    grid = F.affine_grid(matrix, [1, 1, h, w], align_corners=False)
    out = dict(item)
    for key in ("image", "image2"):
        if out.get(key) is not None and out[key].numel():
            out[key] = F.grid_sample(out[key].unsqueeze(0), grid, mode="bilinear", padding_mode="zeros",
                                     align_corners=False)[0]
    for key in ("mask", "masks"):
        if out.get(key) is not None and out[key].numel():
            m = out[key].float()
            m = m.unsqueeze(0) if m.dim() == 3 else m.unsqueeze(0).unsqueeze(0)
            warped = F.grid_sample(m, grid, mode="nearest", padding_mode="zeros", align_corners=False)[0]
            out[key] = (warped if out[key].dim() == 3 else warped[0]).to(item[key].dtype)
    if out.get("boxes") is not None and len(out["boxes"]):
        inverse = np.linalg.inv(theta)
        boxes = out["boxes"].numpy()
        corners = []
        for x0, y0, x1, y1 in boxes:
            pts = np.array([[x0, y0], [x1, y0], [x1, y1], [x0, y1]], float)
            norm = np.stack([2 * pts[:, 0] / w - 1, 2 * pts[:, 1] / h - 1, np.ones(4)])
            moved = inverse @ norm
            xs, ys = (moved[0] + 1) * w / 2, (moved[1] + 1) * h / 2
            corners.append([max(0, xs.min()), max(0, ys.min()), min(w, xs.max()), min(h, ys.max())])
        boxes = np.array(corners, np.float32)
        keep = (boxes[:, 2] - boxes[:, 0] >= 1) & (boxes[:, 3] - boxes[:, 1] >= 1)
        if out.get("masks") is not None:
            keep &= out["masks"].flatten(1).any(1).numpy() if len(out["masks"]) else keep
            out["masks"] = out["masks"][torch.from_numpy(keep)]
            boxes = np.array([_mask_box(m) for m in out["masks"].numpy()], np.float32).reshape(-1, 4)
        else:
            boxes = boxes[keep]
        out["boxes"] = torch.from_numpy(boxes.reshape(-1, 4))
        out["labels"] = out["labels"][torch.from_numpy(keep)]
    return out


# ---- the trainer: batches, steps, evaluation for each task


class Trainer:
    def __init__(self, data, architecture, model, batch_size, augmentation, resize_to, chip_size, tool, arguments):
        self.torch = dl.framework(tool)
        self.data, self.architecture, self.model = data, architecture, model
        self.task = TASK[architecture]
        self.batch_size = max(1, int(batch_size))
        self.augmentation, self.tool = augmentation, tool
        self.resize_to = int(resize_to) if _is_given(resize_to) else None
        self.chip_size = int(chip_size) if _is_given(chip_size) else None
        self.arguments = arguments
        self.mean = self.torch.tensor(data.mean, dtype=self.torch.float32).view(-1, 1, 1)
        self.std = self.torch.tensor(data.std, dtype=self.torch.float32).view(-1, 1, 1)
        self.classes = len(data.classes)
        self.ignore = [data.index[int(v)] if int(v) in data.index else (0 if int(v) == 0 else None)
                       for v in str(arguments.get("ignore_classes", "")).replace(",", " ").split() if v.strip()]
        self.weights = None
        if self.task in ("ImageClassification", "ChangeDetection") and _boolean(arguments.get("class_balancing", "false")):
            counts = np.bincount(np.concatenate([i["mask"].ravel() for i in data.items]), minlength=self.classes + 1)
            self.weights = self.torch.tensor(np.where(counts > 0, counts.sum() / np.maximum(counts, 1) /
                                                      (self.classes + 1), 0.0), dtype=self.torch.float32)
        self.focal = _boolean(arguments.get("focal_loss", "false"))

    # tensors

    def tensor(self, item):
        torch = self.torch
        out = {"image": (torch.from_numpy(item["image"]) - self.mean) / self.std}
        if item.get("image2") is not None:
            if self.task == "PixelTranslation":
                out["image2"] = self.scale_target(torch.from_numpy(item["image2"]))
            else:
                out["image2"] = (torch.from_numpy(item["image2"]) - self.mean) / self.std
        if self.task == "PixelTranslation":
            out["image"] = self.scale_input(torch.from_numpy(item["image"]))
        for key in ("mask",):
            if key in item:
                out[key] = torch.from_numpy(item[key])
        if "masks" in item:
            out["masks"] = torch.from_numpy(item["masks"])
        if "boxes" in item:
            out["boxes"] = torch.from_numpy(item["boxes"])
            out["labels"] = torch.from_numpy(item["labels"])
        for key in ("label", "multi"):
            if key in item:
                out[key] = item[key]
        return self.fit_size(out)

    def scale_input(self, x):
        low, high = self.data.range
        return 2 * (x - low) / max(high - low, 1e-12) - 1

    def scale_target(self, x):
        low, high = self.data.target_range
        return 2 * (x - low) / max(high - low, 1e-12) - 1

    def fit_size(self, item):
        """chip_size crops the chip's centre (INFERRED: the centre, so validation is stable); resize_to resizes."""
        torch = self.torch
        F = torch.nn.functional
        if self.chip_size and self.chip_size < item["image"].shape[-1]:
            h, w = item["image"].shape[-2:]
            top, left = (h - self.chip_size) // 2, (w - self.chip_size) // 2
            item = _crop(item, top, left, self.chip_size, torch)
        if self.resize_to and self.resize_to != item["image"].shape[-1]:
            factor = self.resize_to / item["image"].shape[-1]
            for key in ("image", "image2"):
                if item.get(key) is not None:
                    item[key] = F.interpolate(item[key].unsqueeze(0), size=(self.resize_to, self.resize_to),
                                              mode="bilinear", align_corners=False)[0]
            if item.get("mask") is not None:
                item["mask"] = F.interpolate(item["mask"][None, None].float(), size=(self.resize_to,) * 2,
                                             mode="nearest")[0, 0].long()
            if item.get("masks") is not None and len(item["masks"]):
                item["masks"] = F.interpolate(item["masks"][None].float(), size=(self.resize_to,) * 2,
                                              mode="nearest")[0].to(torch.uint8)
            if item.get("boxes") is not None:
                item["boxes"] = item["boxes"] * factor
        return item

    def batches(self, items, shuffle, rng):
        order = rng.permutation(len(items)) if shuffle else np.arange(len(items))
        groups = [list(order[k:k + self.batch_size]) for k in range(0, len(order), self.batch_size)]
        if len(groups) > 1 and len(groups[-1]) == 1:
            groups[-2].extend(groups.pop())
        return [[items[i] for i in g] for g in groups]

    # loss

    def step(self, model, batch):
        torch = self.torch
        F = torch.nn.functional
        if self.task == "ObjectDetection":
            images = [b["image"] for b in batch]
            targets = [{"boxes": b["boxes"].float(), "labels": b["labels"].long(),
                        **({"masks": b["masks"]} if "masks" in b else {})} for b in batch]
            losses = model(images, targets)
            return sum(losses.values())
        if self.task == "ImageClassification":
            x = torch.stack([b["image"] for b in batch])
            y = torch.stack([b["mask"] for b in batch]).long()
            return self.pixel_loss(model(x), y)
        if self.task == "ChangeDetection":
            a = torch.stack([b["image"] for b in batch])
            c = torch.stack([b["image2"] for b in batch])
            y = torch.stack([b["mask"] for b in batch]).long()
            return self.pixel_loss(model(a, c), y)
        if self.task == "ObjectClassification":
            x = torch.stack([b["image"] for b in batch])
            if self.data.format == "MultiLabeled_Tiles":
                y = torch.zeros(len(batch), self.classes)
                for k, b in enumerate(batch):
                    for c in b["multi"]:
                        y[k, c - 1] = 1
                return F.binary_cross_entropy_with_logits(model(x), y)
            y = torch.tensor([b["label"] - 1 for b in batch])
            return F.cross_entropy(model(x), y)
        return self.translation_loss(model, batch)

    def pixel_loss(self, logits, y):
        torch = self.torch
        F = torch.nn.functional
        y = y.clone()
        for k in self.ignore:
            if k is not None:
                y[y == k] = -100
        loss = F.cross_entropy(logits, y, weight=self.weights, ignore_index=-100, reduction="none")
        if self.focal:
            p = torch.exp(-loss)
            loss = (1 - p) ** 2 * loss
        valid = y != -100
        return loss[valid].mean() if valid.any() else loss.sum() * 0

    def translation_loss(self, model, batch):
        """pix2pix: the generator's adversarial loss plus 100 × L1, and the discriminator's on real and generated
        pairs (Isola et al. 2017, eq. 4); CycleGAN: least-squares adversarial losses and a cycle loss of weight 10
        (Zhu et al. 2017, eq. 3). One loss is answered for both players: the discriminators' terms are computed on
        detached outputs, so that one step trains both (INFERRED, ADR-248 §5.4)."""
        torch = self.torch
        F = torch.nn.functional
        if self.architecture == "PIX2PIX":
            x = torch.stack([b["image"] for b in batch])
            y = torch.stack([b["image2"] for b in batch])
            fake = model.generator(x)
            d = model.discriminator
            judged = d(torch.cat([x, fake], 1))
            generator = F.binary_cross_entropy_with_logits(judged, torch.ones_like(judged)) + 100 * F.l1_loss(fake, y)
            real = d(torch.cat([x, y], 1))
            false = d(torch.cat([x, fake.detach()], 1))
            discriminator = 0.5 * (F.binary_cross_entropy_with_logits(real, torch.ones_like(real))
                                   + F.binary_cross_entropy_with_logits(false, torch.zeros_like(false)))
            return generator + discriminator
        a = torch.stack([b["image"] for b in batch])
        b_images = torch.stack([b["image2"] for b in batch])
        fake_b, fake_a = model.a_to_b(a), model.b_to_a(b_images)
        judged_b, judged_a = model.discriminator_b(fake_b), model.discriminator_a(fake_a)
        generator = F.mse_loss(judged_b, torch.ones_like(judged_b)) + F.mse_loss(judged_a, torch.ones_like(judged_a))
        cycle = F.l1_loss(model.b_to_a(fake_b), a) + F.l1_loss(model.a_to_b(fake_a), b_images)
        discriminator = 0.5 * sum(F.mse_loss(d(r), torch.ones_like(d(r))) + F.mse_loss(d(f.detach()), torch.zeros_like(d(f)))
                                  for d, r, f in ((model.discriminator_b, b_images, fake_b),
                                                  (model.discriminator_a, a, fake_a)))
        return generator + 10 * cycle + discriminator

    # evaluation

    def evaluate(self, model, batches):
        torch = self.torch
        losses = []
        with torch.no_grad():
            if self.task == "ObjectDetection":
                _train_without_statistics(model)
                losses = [float(self.step(model, b)) for b in batches]
                model.eval()
                return {"valid_loss": float(np.mean(losses)), "average_precision": self.detection_ap(model, batches)}
            losses = [float(self.step(model, b)) for b in batches]
            out = {"valid_loss": float(np.mean(losses))}
            if self.task in ("ImageClassification", "ChangeDetection"):
                out.update(self.pixel_metrics(model, batches))
            elif self.task == "ObjectClassification" and self.data.format != "MultiLabeled_Tiles":
                out.update(self.class_metrics(model, batches))
            return out

    def detection_ap(self, model, batches):
        scores, matched, positives = {}, {}, {}
        for batch in batches:
            predictions = model([b["image"] for b in batch])
            for b, p in zip(batch, predictions):
                truths = b["boxes"].numpy()
                labels = b["labels"].numpy()
                for c in range(1, self.classes + 1):
                    mine = p["labels"].numpy() == c
                    detections = [(shapely.box(*box), s) for box, s in zip(p["boxes"].numpy()[mine], p["scores"].numpy()[mine])]
                    theirs = [shapely.box(*box) for box in truths[labels == c]]
                    scores.setdefault(c, []).extend(s for _, s in detections)
                    matched.setdefault(c, []).extend(chips.match(detections, theirs, 0.5))
                    positives[c] = positives.get(c, 0) + len(theirs)
        aps = [chips.average_precision(scores[c], matched[c], positives[c]) for c in positives if positives[c]]
        return float(np.mean(aps)) if aps else 0.0

    def pixel_metrics(self, model, batches):
        torch = self.torch
        k = self.classes + 1
        matrix = np.zeros((k, k), np.int64)
        for batch in batches:
            if self.task == "ChangeDetection":
                logits = model(torch.stack([b["image"] for b in batch]), torch.stack([b["image2"] for b in batch]))
            else:
                logits = model(torch.stack([b["image"] for b in batch]))
            predicted = logits.argmax(1).numpy().ravel()
            truth = torch.stack([b["mask"] for b in batch]).numpy().ravel()
            matrix += np.bincount(truth * k + predicted, minlength=k * k).reshape(k, k)
        return _matrix_metrics(matrix)

    def class_metrics(self, model, batches):
        torch = self.torch
        k = self.classes
        matrix = np.zeros((k, k), np.int64)
        for batch in batches:
            predicted = model(torch.stack([b["image"] for b in batch])).argmax(1).numpy()
            for b, p in zip(batch, predicted):
                matrix[b["label"] - 1, p] += 1
        return _matrix_metrics(matrix)


def _matrix_metrics(matrix):
    """Accuracy, and the classes' mean precision, recall, F1 (= Dice) and IoU, from a matrix of truth rows."""
    tp = np.diag(matrix).astype(float)
    predicted, actual = matrix.sum(axis=0), matrix.sum(axis=1)
    present = actual > 0
    precision = np.where(predicted > 0, tp / np.maximum(predicted, 1), 0.0)
    recall = np.where(actual > 0, tp / np.maximum(actual, 1), 0.0)
    f1 = np.where(precision + recall > 0, 2 * precision * recall / np.maximum(precision + recall, 1e-12), 0.0)
    union = predicted + actual - tp
    iou = np.where(union > 0, tp / np.maximum(union, 1), 0.0)
    mean = (lambda v: float(v[present].mean()) if present.any() else 0.0)
    return {"accuracy": float(tp.sum() / max(matrix.sum(), 1)), "precision": mean(precision), "recall": mean(recall),
            "f1": mean(f1), "dice": mean(f1), "miou": mean(iou)}


def _train_without_statistics(model):
    """Train mode, so that a detector answers its losses, with its batch norm layers kept in eval mode."""
    import torch
    model.train()
    for module in model.modules():
        if isinstance(module, torch.nn.modules.batchnorm._BatchNorm):
            module.eval()


def _crop(item, top, left, size, torch):
    out = dict(item)
    for key in ("image", "image2"):
        if out.get(key) is not None:
            out[key] = out[key][:, top:top + size, left:left + size]
    if out.get("mask") is not None:
        out["mask"] = out["mask"][top:top + size, left:left + size]
    if out.get("masks") is not None:
        out["masks"] = out["masks"][:, top:top + size, left:left + size]
    if out.get("boxes") is not None and len(out["boxes"]):
        b = out["boxes"].numpy().copy()
        b[:, [0, 2]] = np.clip(b[:, [0, 2]] - left, 0, size)
        b[:, [1, 3]] = np.clip(b[:, [1, 3]] - top, 0, size)
        keep = (b[:, 2] - b[:, 0] >= 1) & (b[:, 3] - b[:, 1] >= 1)
        out["boxes"] = torch.from_numpy(b[keep])
        out["labels"] = out["labels"][torch.from_numpy(keep)]
        if out.get("masks") is not None:
            out["masks"] = out["masks"][torch.from_numpy(keep)]
    return out


# ---- Train Deep Learning Model


def _check_backbone(architecture, backbone, tool, given):
    from . import _dlnets as nets
    why = nets.backbone_reason(backbone)
    if why:
        _refuse(tool, "backbone_model", backbone, why)
    if architecture in ("PIX2PIX", "CYCLEGAN"):
        if given:
            _refuse(tool, "backbone_model", backbone, f"{architecture}'s generator and discriminator are the paper's own "
                                                       "networks, with no backbone to choose")
        return None
    if architecture == "SSD":
        if backbone != "VGG16":
            _refuse(tool, "backbone_model", backbone, "torchvision's SSD is SSD300 on VGG-16, the paper's base network; "
                                                      "pass VGG16")
        return backbone
    allowed = nets.CLASSIFIERS if architecture == "FEATURE_CLASSIFIER" else nets.RESNETS
    if backbone not in allowed:
        _refuse(tool, "backbone_model", backbone, f"{architecture} is built on ResNet backbones in grapy (RESNET18 to "
                                                  "RESNET152, RESNEXT50, WIDE_RESNET50), which give its feature pyramid "
                                                  "or encoder")
    return backbone


_TRAINING_ARGUMENTS = {"class_balancing", "focal_loss", "ignore_classes"}


def train(in_folders, out_folder, max_epochs, architecture, batch_size, arguments, learning_rate, backbone,
          pretrained_model, validation_percentage, early_stopping, freeze_model, augmentation, augmentation_parameters,
          chip_size, resize_to, weight_init_scheme, monitor, tool, backbone_given=True, seconds=None):
    """Trains one model and writes its .emd, .pth and .dlpk; answers (emd path, history)."""
    from . import _dlnets as nets
    torch = dl.framework(tool)
    dl.threads(tool)
    dl.seed(tool=tool)
    args = arguments_of(arguments)
    for name in args:
        if name not in _TRAINING_ARGUMENTS:
            _refuse(tool, "arguments", name, "grapy's training takes class_balancing, focal_loss and ignore_classes "
                                             "(pixel models); the other arguments of the Deep learning arguments page "
                                             "configure arcgis.learn's own networks")
    if args.keys() & _TRAINING_ARGUMENTS and TASK[architecture] not in ("ImageClassification", "ChangeDetection"):
        _refuse(tool, "arguments", ", ".join(sorted(args)), "class_balancing, focal_loss and ignore_classes apply to "
                                                             "pixel classification and change detection")
    data = Data(in_folders, architecture, tool)
    backbone = _check_backbone(architecture, backbone, tool, backbone_given)
    chip = (data.tile[0], data.tile[1])
    if _is_given(chip_size):
        if int(chip_size) > min(chip):
            raise ValueError(f"{tool}: chip_size {chip_size} is larger than the training chips ({min(chip)}), and the "
                             "page says it must be less.")
        chip = (int(chip_size), int(chip_size))
    if _is_given(resize_to):
        chip = (int(resize_to), int(resize_to))
    classes = len(data.classes)
    outputs = {"ObjectDetection": classes, "ImageClassification": classes + 1, "ChangeDetection": classes + 1,
               "ObjectClassification": classes}.get(TASK[architecture])
    model = nets.build(architecture, backbone, data.bands, outputs, chip,
                       bands_out=getattr(data, "target_bands", None), scheme=weight_init_scheme)
    pretrained = None
    if _is_given(pretrained_model):
        pretrained = dl.read_definition(pretrained_model, tool)
        if pretrained.grapy.get("architecture") != architecture:
            raise ValueError(f"{tool}: the pretrained model is a {pretrained.grapy.get('architecture')}, not a "
                             f"{architecture}.")
        _load_compatible(model, pretrained.path_of(), tool, data.bands, weight_init_scheme)
        if freeze_model == "FREEZE_MODEL" and hasattr(model, "backbone"):
            dl.freeze(model.backbone, tool)
    trainer = Trainer(data, architecture, model, batch_size, augmentation, resize_to, chip_size, tool, args)
    rng = np.random.default_rng(dl.seed(tool=tool))
    tensors = [trainer.tensor(i) for i in data.items]
    count = len(tensors)
    order = rng.permutation(count)
    held = int(round(count * float(validation_percentage) / 100.0)) if count > 1 else 0
    held = min(max(held, 1 if count > 1 and float(validation_percentage) > 0 else 0), count - 1)
    valid = [tensors[i] for i in order[:held]] or tensors
    training = [tensors[i] for i in order[held:]]
    if architecture == "CYCLEGAN":
        domain_b = [trainer.scale_target(torch.from_numpy(b)) for b in data.domain_b]
        for k, t in enumerate(training + ([] if valid is tensors else valid)):
            t["image2"] = domain_b[k % len(domain_b)]

    def epoch_batches(epoch):
        local = np.random.default_rng(rng.integers(2 ** 31) + epoch)
        items = [augmentation(t, local, torch) for t in training]
        return trainer.batches(items, True, local)

    valid_batches = trainer.batches(valid, False, rng)
    rate = learning_rate
    if not _is_given(rate):
        rate = dl.find_learning_rate(model, epoch_batches(0), trainer.step, tool,
                                     seconds=None if seconds is None else seconds / 4)
        dl.AddMessage(f"{tool}: the learning rate found by the range test is {rate:.3g}.")
    key, larger = MONITORS[monitor]
    if key == "average_precision" and TASK[architecture] != "ObjectDetection":
        raise ValueError(f"{tool}: monitor AVERAGE_PRECISION is a detection metric; {architecture} is not a detector.")
    if key not in ("valid_loss", "average_precision") and (TASK[architecture] not in (
            "ImageClassification", "ChangeDetection", "ObjectClassification") or data.format == "MultiLabeled_Tiles"):
        raise ValueError(f"{tool}: monitor {monitor} is not computed for {architecture}; use VALID_LOSS.")
    history = dl.fit(model, epoch_batches, valid_batches, trainer.step, trainer.evaluate, max_epochs, float(rate),
                     monitor=key, larger_is_better=larger, early_stopping=early_stopping, tool=tool, seconds=seconds)
    folder = chips._path(out_folder)
    name = os.path.basename(os.path.normpath(folder))
    os.makedirs(folder, exist_ok=True)
    dl.save_weights(model, os.path.join(folder, f"{name}.pth"), tool)
    extra = {"history": history, "metadata_format": data.format, "chip_size": list(chip)}
    if data.target_range is not None:
        extra.update({"input_range": list(data.range), "target_range": list(data.target_range),
                      "target_bands": data.target_bands})
    if trainer.resize_to:
        extra["resize_to"] = trainer.resize_to
    emd = dl.write_definition(folder, name, TASK[architecture], architecture, backbone, data.bands, data.classes, chip,
                              data.range, data.mean, data.std, _inference_defaults(architecture, chip, batch_size),
                              extra=extra)
    dl.write_package(emd)
    return emd, history


def _inference_defaults(architecture, chip, batch_size):
    """The model arguments written into the definition, which inference takes when none is given (INFERRED, ADR-248
    §5.3): padding a quarter of the chip for pixel models and an eighth for detectors; threshold 0.5 and nms_overlap
    0.1 (the page's own defaults); predict_background true."""
    size = int(max(chip))
    base = {"batch_size": int(batch_size)}
    task = TASK[architecture]
    if task == "ObjectDetection":
        base.update({"padding": size // 8, "threshold": 0.5, "nms_overlap": 0.1, "exclude_pad_detections": True})
    elif task in ("ImageClassification", "ChangeDetection", "PixelTranslation"):
        base.update({"padding": size // 4})
        if task == "ImageClassification":
            base["predict_background"] = True
    if architecture == "CYCLEGAN":
        base.update({"direction": "AtoB", "tile_size": size})
    return base


def _load_compatible(model, path, tool, bands, scheme):
    """A pretrained model's weights, its first convolution adapted when the band count differs (weight_init_scheme)."""
    torch = dl.framework(tool)
    state = torch.load(path, map_location="cpu", weights_only=True)
    own = model.state_dict()
    for key, value in list(state.items()):
        if key in own and own[key].shape != value.shape:
            if value.dim() == 4 and own[key].shape[1] == bands and value.shape[0] == own[key].shape[0]:
                conv = torch.nn.Conv2d(value.shape[1], value.shape[0], value.shape[2:], bias=False)
                with torch.no_grad():
                    conv.weight.copy_(value)
                state[key] = dl.adapt_first_convolution(conv, bands, scheme).weight.detach()
            else:
                del state[key]
    model.load_state_dict(state, strict=False)


TRAIN_METHOD = ("the published architecture its model type names, from random weights, with Adam under a one-cycle "
                "schedule", "see grapy._dlnets for each network's paper; Kingma and Ba 2015; Smith and Topin 2019")


def TrainDeepLearningModel(in_folder, out_folder, max_epochs=20, model_type=None, batch_size=2, arguments=None,
                           learning_rate=None, backbone_model=None, pretrained_model=None, validation_percentage=10,
                           stop_training="STOP_TRAINING", freeze="FREEZE_MODEL", augmentation="DEFAULT",
                           augmentation_parameters=None, chip_size=None, resize_to=None, weight_init_scheme="RANDOM",
                           monitor="VALID_LOSS", tensorboard="DISABLE_TENSORBOARD"):
    """EQUIVALENT: trains the network the model type names (grapy._dlnets) on training data grapy's Export Training Data
    For Deep Learning wrote, and writes ``<out_folder>/<name>.emd``, ``<name>.pth`` and ``<name>.dlpk``, ``<name>``
    being the folder's name.

    Built: max_epochs; model_type (FASTERRCNN, RETINANET, MASKRCNN, SSD, UNET, DEEPLAB, PSPNET, FEATURE_CLASSIFIER,
    CHANGEDETECTOR, PIX2PIX, CYCLEGAN; with none, the metadata format's usual type); batch_size; learning_rate (with
    none, Smith's range test); backbone_model (torchvision's ResNet family, and VGG, DenseNet and MobileNetV2 for the
    classifier); pretrained_model (a grapy .emd or .dlpk of the same type, fine-tuned); validation_percentage;
    stop_training (early stopping after 5 epochs without improvement); freeze (the backbone of a pretrained model);
    augmentation and augmentation_parameters (see ``Augmentation``); chip_size; resize_to; weight_init_scheme; monitor.

    Refused by name: model types whose network is unpublished, Esri's, a foundation model's, or neither in torchvision
    nor written here; backbones without published random-init builders; tensorboard; augmentation FILE; monitor
    CORPUS_BLEU and MULTI_LABEL_FBETA. INFERRED (ADR-248 §5.3): the batch size default 2; FREEZE_MODEL freezes nothing
    without a pretrained_model, since grapy's backbones start from random weights; one chip of ten (at least one) is
    held out to validate."""
    from ._result import Result
    tool = TOOL_TRAIN
    folders = _folders(in_folder)
    fmt = chips.read_training(folders[0], tool)[0]["metadata_format"]
    architecture = str(model_type).upper() if _is_given(model_type) else DEFAULT_TYPE.get(fmt)
    if architecture in REFUSED_TYPES:
        _refuse(tool, "model_type", model_type, REFUSED_TYPES[architecture])
    if architecture not in TASK or architecture == "FCN" or architecture == "FCOS":
        raise ValueError(f"{tool}: '{model_type}' is not a model type of Train Deep Learning Model.")
    if str(tensorboard).upper() not in ("DISABLE_TENSORBOARD", "FALSE", "#", "", "NONE"):
        _refuse(tool, "tensorboard", tensorboard, "TensorBoard's writer needs the tensorboard package, which is not "
                                                  "among grapy's libraries; the training history is in the .emd")
    kind = _word(augmentation, "DEFAULT", ("DEFAULT", "NONE", "CUSTOM", "FILE"), tool, "augmentation")
    if kind == "FILE":
        _refuse(tool, "augmentation", "FILE", "transforms.json is a list of fastai transforms, arcgis.learn's own format")
    watch = _word(monitor, "VALID_LOSS", tuple(MONITORS) + ("CORPUS_BLEU", "MULTI_LABEL_FBETA"), tool, "monitor")
    if watch == "CORPUS_BLEU":
        _refuse(tool, "monitor", watch, "it scores the captions of an image captioner, a model type grapy refuses")
    if watch == "MULTI_LABEL_FBETA":
        _refuse(tool, "monitor", watch, "the page does not give its beta")
    early = _word(stop_training, "STOP_TRAINING", ("STOP_TRAINING", "CONTINUE_TRAINING"), tool,
                  "stop_training") == "STOP_TRAINING"
    frozen = _word(freeze, "FREEZE_MODEL", ("FREEZE_MODEL", "UNFREEZE_MODEL"), tool, "freeze")
    scheme = _word(weight_init_scheme, "RANDOM", ("RANDOM", "RED_BAND", "ALL_RANDOM"), tool, "weight_init_scheme")
    given = _is_given(backbone_model)
    backbone = str(backbone_model).upper() if given else "RESNET34"
    if architecture == "SSD" and not given:
        backbone = "VGG16"
    emd, _ = train(folders, out_folder, int(max_epochs or 20), architecture, batch_size or 2, arguments, learning_rate,
                   backbone, pretrained_model, validation_percentage if _is_given(validation_percentage) else 10, early,
                   frozen, Augmentation(kind, augmentation_parameters, tool), augmentation_parameters, chip_size,
                   resize_to, scheme, watch, tool, backbone_given=given)
    return Result(emd)


def _folders(value):
    if isinstance(value, (list, tuple)):
        return [chips._path(v) for v in value]
    return [chips._path(v.strip().strip("'\"")) for v in str(value).split(";") if v.strip()]


# ---- Train Using AutoDL


AUTODL_NETWORKS = {"FasterRCNN": "FASTERRCNN", "RetinaNet": "RETINANET", "SingleShotDetector": "SSD", "FCOS": "FCOS",
                   "MaskRCNN": "MASKRCNN", "DeepLab": "DEEPLAB", "UnetClassifier": "UNET", "PSPNetClassifier": "PSPNET",
                   "FCN": "FCN"}
AUTODL_REFUSED = {
    "YOLOv3": REFUSED_TYPES["YOLOV3"], "DETReg": REFUSED_TYPES["DETREG"], "RTDetrV2": REFUSED_TYPES["RTDETRV2"],
    "SamLoRA": REFUSED_TYPES["SAMLORA"], "DeepLabV3Plus": _NOT_IN_TORCHVISION.format("DeepLabv3+, Chen et al. 2018"),
    "HRNet": _NOT_IN_TORCHVISION.format("HRNet, Wang et al. 2020"),
    "Mask2Former": _NOT_IN_TORCHVISION.format("Mask2Former, Cheng et al. 2022"),
}
for _name in ("ATSS", "CARAFE", "CascadeRCNN", "CascadeRPN", "DCN", "DynamicRCNN", "EmpiricalAttention", "FoveaBox",
              "FSAF", "GHM", "LibraRCNN", "PaFPN", "Res2Net", "SABL", "VFNet"):
    AUTODL_REFUSED[_name] = REFUSED_TYPES["MMDETECTION"]
for _name in ("ANN", "APCNet", "CCNet", "CGNet", "DMNet", "DNLNet", "FastSCNN", "GCNet", "MobileNetV2", "NonLocalNet",
              "PSANet", "SemFPN", "UperNet"):
    AUTODL_REFUSED[_name] = REFUSED_TYPES["MMSEGMENTATION"]
AUTODL_METHOD = ("a search over grapy's own networks, then learning rates and backbones for the best two",
                 "ADR-248 §5.5; Bergstra and Bengio 2012, JMLR 13 for the random search")


def TrainUsingAutoDL(in_data, out_model, pretrained_model=None, total_time_limit=2.0, autodl_mode="BASIC", networks=None,
                     save_evaluated_models="SAVE_BEST_MODEL"):
    """EQUIVALENT: AutoDL's search, over grapy's networks. BASIC trains each network asked (or every network grapy holds
    for the training data's format: FasterRCNN, RetinaNet, SingleShotDetector, FCOS for rectangles; MaskRCNN for masks;
    UnetClassifier, DeepLab, PSPNetClassifier, FCN for classified tiles) on its default backbone, in an equal share of
    ``total_time_limit`` hours; ADVANCED spends half the time so, then trains the best two again with ResNet-18, -34 and
    -50 backbones and learning rates drawn log-uniformly from 1e-4 to 1e-2 (Bergstra and Bengio 2012) for the other
    half. The best is the lowest validation loss, as the page leaves the measure open (INFERRED); it is written to
    ``out_model`` as a .dlpk, and with SAVE_ALL_MODELS every evaluated model beside it.

    Each network trains for at most 20 epochs with early stopping, or until its share of the time is spent (INFERRED);
    networks AutoDL names but grapy does not build are refused by name when asked for."""
    from ._result import Result
    tool = "TrainUsingAutoDL"
    folders = _folders(in_data)
    fmt = chips.read_training(folders[0], tool)[0]["metadata_format"]
    if fmt not in ("Classified_Tiles", "PASCAL_VOC_rectangles", "KITTI_rectangles"):
        raise ValueError(f"{tool}: the training data must be Classified_Tiles, PASCAL_VOC_rectangles or KITTI_rectangles, "
                         f"as the page says; it is {fmt}.")
    mode = _word(autodl_mode, "BASIC", ("BASIC", "ADVANCED"), tool, "autodl_mode")
    save_all = _word(save_evaluated_models, "SAVE_BEST_MODEL", ("SAVE_ALL_MODELS", "SAVE_BEST_MODEL"), tool,
                     "save_evaluated_models") == "SAVE_ALL_MODELS"
    if _is_given(networks):
        asked = [n.strip() for n in (networks if isinstance(networks, (list, tuple)) else str(networks).split(";"))
                 if str(n).strip()]
    else:
        asked = [n for n, a in AUTODL_NETWORKS.items() if fmt in FORMATS_OF[a]]
    chosen = []
    for name in asked:
        found = next((k for k in list(AUTODL_NETWORKS) + list(AUTODL_REFUSED) if k.lower() == name.lower()), None)
        if found in AUTODL_REFUSED:
            _refuse(tool, "networks", name, AUTODL_REFUSED[found])
        if found is None:
            raise ValueError(f"{tool}: '{name}' is not one of AutoDL's networks.")
        if fmt in FORMATS_OF[AUTODL_NETWORKS[found]]:
            chosen.append(found)
        else:
            AddWarning(f"{tool}: {found} does not train on {fmt} and is left out.")
    if not chosen:
        raise ValueError(f"{tool}: none of the networks asked trains on {fmt}.")
    import time
    budget = float(total_time_limit if _is_given(total_time_limit) else 2.0) * 3600.0
    first_half = budget / 2 if mode == "ADVANCED" else budget
    out_path = chips._path(out_model)
    if not out_path.lower().endswith(".dlpk"):
        out_path += ".dlpk"
    work = os.path.join(os.path.dirname(out_path), os.path.splitext(os.path.basename(out_path))[0] + "_autodl")
    results = []

    def run(network, backbone, rate, seconds, label):
        architecture = AUTODL_NETWORKS[network]
        epochs = 20
        start = time.time()
        emd, history = train(folders, os.path.join(work, label), epochs, architecture, 2, None, rate,
                             "VGG16" if architecture == "SSD" else backbone, pretrained_model, 10, True, "FREEZE_MODEL",
                             Augmentation("DEFAULT", None, tool), None, None, None, "RANDOM", "VALID_LOSS", tool,
                             backbone_given=False, seconds=seconds)
        loss = min((h["valid_loss"] for h in history), default=math.inf)
        dl.AddMessage(f"{tool}: {label}: validation loss {loss:.4f} in {time.time() - start:.1f} s.")
        results.append((loss, label, emd))

    share = first_half / len(chosen)
    for network in chosen:
        run(network, "RESNET34", None, share, network)
    if mode == "ADVANCED":
        rng = np.random.default_rng(dl.seed(tool=tool))
        best_two = [label for _, label, _ in sorted(results)[:2]]
        trials = [(n, b, float(10 ** rng.uniform(-4, -2))) for n in best_two for b in ("RESNET18", "RESNET34", "RESNET50")
                  if AUTODL_NETWORKS[n] != "SSD"]
        for network, backbone, rate in trials:
            run(network, backbone, rate, budget / 2 / max(1, len(trials)), f"{network}_{backbone}_{rate:.0e}")
    loss, label, emd = sorted(results)[0]
    dl.AddMessage(f"{tool}: the best model is {label}, validation loss {loss:.4f}.")
    dl.write_package(emd, out_path)
    if save_all:
        for _, other, path in results:
            dl.write_package(path, os.path.join(os.path.dirname(out_path), f"{other}.dlpk"))
    return Result(out_path)


# ---- inference


class Model:
    """A trained model read back: its definition, network and normalisation, ready to run on arrays."""

    def __init__(self, in_model_definition, tool, kinds=None):
        from . import _dlnets as nets
        self.torch = dl.framework(tool)
        dl.threads(tool)
        self.definition = dl.read_definition(in_model_definition, tool, kinds)
        g = self.definition.grapy
        self.architecture, self.backbone = g["architecture"], g.get("backbone")
        self.bands, self.values = int(g["bands"]), [int(v) for v in g["classes"]]
        self.names = {int(c["Value"]): c["Name"] for c in self.definition.get("Classes", [])}
        self.chip = tuple(g.get("chip_size") or (self.definition["ImageWidth"], self.definition["ImageHeight"]))
        self.task = self.definition["ModelType"]
        outputs = {"ObjectDetection": len(self.values), "ImageClassification": len(self.values) + 1,
                   "ChangeDetection": len(self.values) + 1, "ObjectClassification": len(self.values)}.get(self.task)
        self.model = nets.build(self.architecture, self.backbone, self.bands, outputs, self.chip,
                                bands_out=g.get("target_bands"))
        dl.load_weights(self.model, self.definition.path_of(), tool)
        self.model.eval()
        self.mean = np.asarray(g["mean"], np.float32).reshape(-1, 1, 1)
        self.std = np.asarray(g["std"], np.float32).reshape(-1, 1, 1)
        self.defaults = dict(g.get("arguments") or {})
        self.g = g
        self.tool = tool

    def arguments(self, given, allowed):
        args = dict(self.defaults)
        for name, value in arguments_of(given).items():
            if name not in allowed:
                _refuse(self.tool, "arguments", name, f"the model arguments grapy runs with are {', '.join(allowed)}")
            args[name] = value
        return args

    def check_bands(self, raster):
        if len(raster._arrays) != self.bands:
            raise ValueError(f"{self.tool}: the model was trained on {self.bands} bands and the raster has "
                             f"{len(raster._arrays)}.")

    def normalise(self, array):
        if self.task == "PixelTranslation":
            low, high = self.g["input_range"]
            return 2 * (array - low) / max(high - low, 1e-12) - 1
        return (array - self.mean) / self.std


def _batch_size(value):
    """The page's rule: a batch size that is not a square is rounded down to the nearest square (6 becomes 4)."""
    n = max(1, int(float(value)))
    return int(math.isqrt(n)) ** 2


def tiles(rows, columns, size, padding):
    """Tile origins (row, column) covering a raster with tiles of ``size`` overlapping by ``padding`` on each side: each
    tile keeps its centre, ``size - 2·padding`` a side, and the first starts ``padding`` before the raster."""
    step = max(1, size - 2 * padding)
    out = []
    for r in range(-padding, max(rows - padding, 1), step):
        for c in range(-padding, max(columns - padding, 1), step):
            out.append((r, c))
    return out


def _window(arrays, r, c, size):
    """A ``size`` window of (bands, rows, columns) at (r, c), zeros outside."""
    bands, rows, columns = arrays.shape
    out = np.zeros((bands, size, size), np.float32)
    r0, c0 = max(r, 0), max(c, 0)
    r1, c1 = min(r + size, rows), min(c + size, columns)
    if r1 > r0 and c1 > c0:
        out[:, r0 - r:r1 - r, c0 - c:c1 - c] = arrays[:, r0:r1, c0:c1]
    return out


def _stack(raster):
    return np.stack([np.asarray(a, np.float32) for a in raster._arrays])


def _dihedral(x, k, flip, torch, inverse=False):
    if not inverse:
        x = torch.flip(x, [-1]) if flip else x
        return torch.rot90(x, k, [-2, -1])
    x = torch.rot90(x, -k, [-2, -1])
    return torch.flip(x, [-1]) if flip else x


def predict_pixels(model, arrays, padding, batch, tta=False, second=None, background=True, out_bands=None):
    """Per-pixel output of a pixel, change or translation model over a whole raster, tile by tile: class indexes for
    classification (argmax of the softmax, averaged over the eight dihedral turns with test time augmentation), or the
    generated bands for translation."""
    torch = model.torch
    size = int(model.chip[0])
    if padding * 2 >= size:
        raise ValueError(f"{model.tool}: padding {padding} must be less than half the tile size {size}, as the page says.")
    _, rows, columns = arrays.shape
    translating = model.task == "PixelTranslation"
    channels = out_bands if translating else None
    out = np.zeros(((channels or 1), rows, columns), np.float32 if translating else np.int32)
    positions = tiles(rows, columns, size, padding)
    for k in range(0, len(positions), batch):
        group = positions[k:k + batch]
        x = torch.from_numpy(np.stack([model.normalise(_window(arrays, r, c, size)) for r, c in group]))
        y = torch.from_numpy(np.stack([model.normalise(_window(second, r, c, size)) for r, c in group])) \
            if second is not None else None
        with torch.no_grad():
            if translating:
                direction = str(model.g.get("direction_used") or "AtoB")
                result = model.model(x, direction) if model.architecture == "CYCLEGAN" else model.model(x)
                low, high = model.g["target_range"]
                result = ((result + 1) / 2 * (high - low) + low).numpy()
            else:
                turns = [(t, f) for f in (False, True) for t in range(4)] if tta else [(0, False)]
                probability = 0
                for t, f in turns:
                    xi = _dihedral(x, t, f, torch)
                    logits = model.model(xi, _dihedral(y, t, f, torch)) if y is not None else model.model(xi)
                    probability = probability + _dihedral(torch.softmax(logits, 1), t, f, torch, inverse=True)
                if not background:
                    probability[:, 0] = -1
                result = probability.argmax(1, keepdim=True).numpy()
        for (r, c), tile in zip(group, result):
            r0, c0 = max(r + padding, 0), max(c + padding, 0)
            r1, c1 = min(r + size - padding, rows), min(c + size - padding, columns)
            if r1 > r0 and c1 > c0:
                out[:, r0:r1, c0:c1] = tile[:, r0 - r:r1 - r, c0 - c:c1 - c]
    return out


def detect(model, arrays, padding, batch, threshold, nms_overlap, exclude_pad):
    """Detections over a whole raster, tile by tile: [(class value, score, shape in pixel coordinates)], those in a
    tile's padding dropped with ``exclude_pad`` (a detection whose box centre lies there, INFERRED), and the tiles'
    detections merged by non-maximum suppression of each class at ``nms_overlap``. A Mask R-CNN detection is its mask's
    outline (a mask at 0.5), any other its box."""
    torch = model.torch
    size = int(model.chip[0])
    _, rows, columns = arrays.shape
    found = []
    # The network's own score cut-off (torchvision's 0.05) is the threshold argument when that is lower, so that a
    # threshold the user gives is the one applied.
    for owner in (model.model, getattr(model.model, "roi_heads", None)):
        if owner is not None and hasattr(owner, "score_thresh"):
            owner.score_thresh = min(float(owner.score_thresh), float(threshold))
    positions = tiles(rows, columns, size, padding)
    for k in range(0, len(positions), batch):
        group = positions[k:k + batch]
        x = [torch.from_numpy(model.normalise(_window(arrays, r, c, size))) for r, c in group]
        with torch.no_grad():
            predictions = model.model(x)
        for (r, c), p in zip(group, predictions):
            masks = p.get("masks")
            for n, (box, label, score) in enumerate(zip(p["boxes"].numpy(), p["labels"].numpy(), p["scores"].numpy())):
                if score < threshold or label < 1 or label > len(model.values):
                    continue
                cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
                if exclude_pad and padding and not (padding <= cx < size - padding and padding <= cy < size - padding):
                    continue
                if masks is not None:
                    shape = _mask_outline(masks[n, 0].numpy() >= 0.5) or shapely.box(*box)
                else:
                    shape = shapely.box(*box)
                shape = shapely.affinity.translate(shape, c, r).intersection(shapely.box(0, 0, columns, rows))
                if not shape.is_empty:
                    found.append((model.values[label - 1], float(score), shape))
    if not found:
        return []
    kept = chips.greedy_nms([s for _, _, s in found], [p for _, p, _ in found], nms_overlap, [v for v, _, _ in found])
    return [found[i] for i in kept]


def _mask_outline(mask):
    from rasterio.features import shapes
    if not mask.any():
        return None
    parts = [shapely.geometry.shape(g) for g, v in shapes(mask.astype(np.uint8), mask=mask) if v == 1]
    return shapely.union_all(parts) if parts else None


import shapely.affinity  # noqa: E402 — used by detect


def _to_map(shape, grid):
    """A shape in pixel coordinates (column, row from the corner) on a grid, in map coordinates."""
    return shapely.affinity.affine_transform(shape, [grid.width, 0, 0, -grid.height, grid.left, grid.top])


def _raster_of(array, mask, grid, integer=True):
    from ._raster import Raster
    if array.shape[0] == 1:
        return Raster._of(array[0].astype(np.int32 if integer else np.float64), mask.copy(), grid)
    return Raster._of_bands([a.astype(np.float64) for a in array], [mask.copy() for _ in array], grid)


def _nodata(raster):
    return np.logical_or.reduce([np.asarray(m) for m in raster._masks])


_PIXEL_ARGUMENTS = ("padding", "batch_size", "predict_background", "test_time_augmentation")


def _pixelspace(tool, use_pixelspace):
    if _word(use_pixelspace, "NO_PIXELSPACE", ("PIXELSPACE", "NO_PIXELSPACE"), tool, "use_pixelspace") == "PIXELSPACE":
        _refuse(tool, "use_pixelspace", "PIXELSPACE", "its output is a raster, and the page does not say how a raster "
                                                      "classified in an image's pixel space is carried back to map space; "
                                                      "Detect Objects Using Deep Learning takes PIXELSPACE")


def _attachments(tool, value, parameter):
    if _is_given(value) and str(value).upper() not in ("NO_OVERWRITE", "FALSE"):
        _refuse(tool, parameter, value, "it applies to a feature class of image attachments, and grapy reads images "
                                        "from rasters, folders and mosaic datasets")


def _attached_input(tool, in_raster):
    if chips._is_features(in_raster):
        from . import _mosaic
        if not _mosaic.exists(in_raster):
            _refuse(tool, "in_raster", in_raster, "a feature class of image attachments is classified into "
                                                  "attachments whose file format and name the page does not give")


def ClassifyPixelsUsingDeepLearning(in_raster, in_model_definition, arguments=None,
                                    processing_mode="PROCESS_AS_MOSAICKED_IMAGE", out_classified_folder=None,
                                    out_featureclass=None, overwrite_attachments="NO_OVERWRITE",
                                    use_pixelspace="NO_PIXELSPACE"):
    """Each pixel classified by a pixel classification model grapy trained (UNET, DEEPLAB, PSPNET), tile by tile with
    ``padding`` (see the module's docstring). Answers the classified Raster — class values, 0 the background — or, with
    PROCESS_ITEMS_SEPARATELY or a folder of images, writes one ``<name>.tif`` per image to ``out_classified_folder``
    and answers the folder. Model arguments: padding, batch_size (rounded down to a square, as the page says),
    predict_background, test_time_augmentation (the softmax averaged over the eight dihedral turns, INFERRED)."""
    tool = "ClassifyPixelsUsingDeepLearning"
    _pixelspace(tool, use_pixelspace)
    _attachments(tool, overwrite_attachments, "overwrite_attachments")
    if _is_given(out_featureclass):
        _refuse(tool, "out_featureclass", out_featureclass, "it receives the results for a feature class of image "
                                                            "attachments, which grapy does not read")
    _attached_input(tool, in_raster)
    model = Model(in_model_definition, tool, ("ImageClassification",))
    args = model.arguments(arguments, _PIXEL_ARGUMENTS)
    sources = chips.rasters(in_raster, processing_mode, tool)
    outputs = []
    for name, raster in sources:
        model.check_bands(raster)
        classes = predict_pixels(model, _stack(raster), int(args.get("padding", 0)),
                                 _batch_size(args.get("batch_size", 4)), _boolean(args.get("test_time_augmentation", False)),
                                 background=_boolean(args.get("predict_background", True)))[0]
        values = np.concatenate([[0], model.values]).astype(np.int32)[classes]
        outputs.append((name, _raster_of(values[None], _nodata(raster), raster._grid)))
    return _pixel_outputs(tool, outputs, sources, in_raster, out_classified_folder)


def _pixel_outputs(tool, outputs, sources, in_raster, out_folder):
    from ._result import Result
    if len(outputs) == 1 and not (isinstance(in_raster, (str, os.PathLike)) and os.path.isdir(chips._path(in_raster))):
        return outputs[0][1]
    if not _is_given(out_folder):
        raise ValueError(f"{tool}: an output folder is needed when images are processed one by one, as the page says.")
    folder = chips._path(out_folder)
    os.makedirs(folder, exist_ok=True)
    for name, raster in outputs:
        raster.save(os.path.join(folder, f"{name}.tif"))
    return Result(folder)


def DetectChangeUsingDeepLearning(from_raster, to_raster, out_classified_raster, in_model_definition, arguments=None):
    """The change between two rasters, by a change detection model grapy trained (CHANGEDETECTOR): each pixel's change
    class, 0 none, tile by tile with ``padding``. Model arguments: padding, batch_size. Both rasters are on one grid."""
    from ._raster import _open_bands
    from ._result import Result
    tool = "DetectChangeUsingDeepLearning"
    model = Model(in_model_definition, tool, ("ChangeDetection",))
    args = model.arguments(arguments, ("padding", "batch_size"))
    before, after = _open_bands(from_raster, tool), _open_bands(to_raster, tool)
    model.check_bands(before)
    model.check_bands(after)
    if not before._grid.same(after._grid):
        raise ValueError(f"{tool}: the two rasters are on different grids; resample one onto the other first.")
    classes = predict_pixels(model, _stack(before), int(args.get("padding", 0)), _batch_size(args.get("batch_size", 4)),
                             second=_stack(after))[0]
    values = np.concatenate([[0], model.values]).astype(np.int32)[classes]
    out = _raster_of(values[None], _nodata(before) | _nodata(after), before._grid)
    out.save(out_classified_raster)
    return Result(out.catalogPath)


def TranslatePixelsUsingDeepLearning(in_raster, in_model_definition, arguments=None,
                                     processing_mode="PROCESS_AS_MOSAICKED_IMAGE", out_translated_folder=None,
                                     out_featureclass=None, overwrite_attachments="NO_OVERWRITE",
                                     use_pixelspace="NO_PIXELSPACE"):
    """An image translated by a model grapy trained (PIX2PIX, or CYCLEGAN in ``direction`` AtoB or BtoA), tile by tile
    with ``padding``, its values scaled back to the range of the training data's target images. Model arguments:
    padding, batch_size, direction and tile_size (CycleGAN's, which must be the model's chip size)."""
    tool = "TranslatePixelsUsingDeepLearning"
    _pixelspace(tool, use_pixelspace)
    _attachments(tool, overwrite_attachments, "overwrite_attachments")
    if _is_given(out_featureclass):
        _refuse(tool, "out_featureclass", out_featureclass, "it receives the results for a feature class of image "
                                                            "attachments, which grapy does not read")
    _attached_input(tool, in_raster)
    model = Model(in_model_definition, tool, ("PixelTranslation",))
    args = model.arguments(arguments, ("padding", "batch_size", "direction", "tile_size"))
    direction = str(args.get("direction", "AtoB"))
    if direction not in ("AtoB", "BtoA"):
        raise ValueError(f"{tool}: direction is AtoB or BtoA.")
    if direction == "BtoA" and model.architecture != "CYCLEGAN":
        raise ValueError(f"{tool}: direction applies to CycleGAN models, as the page says.")
    if int(float(args.get("tile_size", model.chip[0]))) != int(model.chip[0]):
        _refuse(tool, "arguments", f"tile_size {args['tile_size']}", "grapy's generators run on the chip size they were "
                                                                     "trained on")
    model.g["direction_used"] = direction
    if direction == "BtoA":
        low, high = model.g["target_range"]
        model.g = dict(model.g, input_range=[low, high], target_range=model.g["input_range"])
    out_bands = model.bands if direction == "BtoA" else int(model.g.get("target_bands") or model.bands)
    sources = chips.rasters(in_raster, processing_mode, tool)
    outputs = []
    for name, raster in sources:
        expected = int(model.g.get("target_bands") or model.bands) if direction == "BtoA" else model.bands
        if len(raster._arrays) != expected:
            raise ValueError(f"{tool}: the model takes {expected} bands and the raster has {len(raster._arrays)}.")
        result = predict_pixels(model, _stack(raster), int(args.get("padding", 0)), _batch_size(args.get("batch_size", 4)),
                                out_bands=out_bands)
        outputs.append((name, _raster_of(result, _nodata(raster), raster._grid, integer=False)))
    return _pixel_outputs(tool, outputs, sources, in_raster, out_translated_folder)


_DETECT_ARGUMENTS = ("padding", "batch_size", "threshold", "nms_overlap", "exclude_pad_detections", "tta_scales",
                     "test_time_augmentation", "tile_size", "merge_policy", "output_classified_raster")


def DetectObjectsUsingDeepLearning(in_raster, out_detected_objects, in_model_definition, arguments=None, run_nms="NO_NMS",
                                   confidence_score_field=None, class_value_field=None, max_overlap_ratio=0,
                                   processing_mode="PROCESS_AS_MOSAICKED_IMAGE", use_pixelspace="NO_PIXELSPACE",
                                   in_objects_of_interest=None):
    """Objects found by a detector grapy trained (FASTERRCNN, RETINANET, MASKRCNN, SSD), as polygons — boxes, or the
    masks' outlines for Mask R-CNN — with fields ``Class`` (the class's name), ``Classvalue`` and the confidence
    (``confidence_score_field``, else ``Confidence``), appended when the output exists, as the page says. Tiles overlap
    by ``padding`` and their detections are merged as the module's docstring says; with ``NMS`` the output then goes
    through Non Maximum Suppression at ``max_overlap_ratio``. ``in_objects_of_interest`` keeps the classes it names.

    With ``PIXELSPACE`` the input is a mosaic dataset of Frame Camera items (ADR-234), each detected in its own image:
    the ``iShape`` field holds the detection in the image's pixel space and ``ImageID`` the item (the encoding is
    grapy's, INFERRED, ADR-248 §5.6), and the shape is the detection carried to the ground at the item's average
    height — Transform Image Shapes carries it again over a DEM.

    Model arguments: padding, batch_size, threshold, nms_overlap, exclude_pad_detections. Refused by name:
    test_time_augmentation and tta_scales (the page does not say how detections of turned or scaled images merge),
    and MaskRCNN's tile_size and merge_policy and MaX-DeepLab's output_classified_raster (model types grapy refuses or
    runs at its chip size)."""
    from . import _gpkg, da
    from ._result import Result
    from .analysis import _out
    tool = "DetectObjectsUsingDeepLearning"
    model = Model(in_model_definition, tool, ("ObjectDetection",))
    args = model.arguments(arguments, _DETECT_ARGUMENTS)
    for name in ("test_time_augmentation", "tta_scales"):
        if name in arguments_of(arguments) and str(arguments_of(arguments)[name]).strip().lower() not in ("false", "1", "0", "1.0"):
            _refuse(tool, "arguments", name, "the page does not say how the detections of turned or scaled images are "
                                             "merged")
    for name in ("tile_size", "merge_policy", "output_classified_raster"):
        if name in arguments_of(arguments):
            _refuse(tool, "arguments", name, "it is an argument of MaskRCNN's test time augmentation or of MaX-DeepLab, "
                                             "and grapy runs a model at its chip size")
    nms = _word(run_nms, "NO_NMS", ("NMS", "NO_NMS"), tool, "run_nms") == "NMS"
    pixelspace = _word(use_pixelspace, "NO_PIXELSPACE", ("PIXELSPACE", "NO_PIXELSPACE"), tool, "use_pixelspace") == "PIXELSPACE"
    score_field = str(confidence_score_field) if _is_given(confidence_score_field) else "Confidence"
    wanted = None
    if _is_given(in_objects_of_interest):
        items = in_objects_of_interest if isinstance(in_objects_of_interest, (list, tuple)) else \
            str(in_objects_of_interest).replace(",", ";").split(";")
        wanted = {str(w).strip().lower() for w in items if str(w).strip()}
    padding, batch = int(args.get("padding", 0)), _batch_size(args.get("batch_size", 4))
    threshold, overlap = float(args.get("threshold", 0.5)), float(args.get("nms_overlap", 0.1))
    exclude = _boolean(args.get("exclude_pad_detections", True))
    rows = []
    if pixelspace:
        srid, rows = _detect_in_pixel_space(tool, model, in_raster, padding, batch, threshold, overlap, exclude)
    else:
        sources = chips.rasters(in_raster, processing_mode, tool)
        srid = sources[0][1]._grid.srid
        for _, raster in sources:
            model.check_bands(raster)
            for value, score, shape in detect(model, _stack(raster), padding, batch, threshold, overlap, exclude):
                rows.append((value, score, _to_map(shape, raster._grid), None, None))
    if wanted is not None:
        rows = [r for r in rows if str(model.names.get(r[0], r[0])).lower() in wanted or str(r[0]) in wanted]
    if nms and rows:
        # Non Maximum Suppression's two passes over this run's detections (INFERRED: not over features already in an
        # output appended to).
        shapes, scores = [r[2] for r in rows], [r[1] for r in rows]
        first = chips.greedy_nms(shapes, scores, max_overlap_ratio or 0, [r[0] for r in rows])
        second = chips.greedy_nms([shapes[i] for i in first], [scores[i] for i in first], max_overlap_ratio or 0)
        rows = [rows[first[k]] for k in second]
    out = _out(out_detected_objects)
    fields = [("Class", "String"), ("Classvalue", "Integer"), (score_field, "Double")]
    extra_class = _is_given(class_value_field) and str(class_value_field).lower() not in ("class", "classvalue")
    if extra_class:
        fields.append((str(class_value_field), "Integer"))
    if pixelspace:
        fields += [("iShape", "String"), ("ImageID", "Integer")]
    if not _has_table(out):
        _gpkg.create(out, "Polygon", srid, [_gpkg.Column(n, k) for n, k in fields])
    with da.InsertCursor(out, [n for n, _ in fields] + ["SHAPE@"]) as cursor:
        for value, score, shape, ishape, image in rows:
            row = [str(model.names.get(value, value)), int(value), float(score)]
            if extra_class:
                row.append(int(value))
            if pixelspace:
                row += [ishape, image]
            cursor.insertRow(tuple(row) + (shape,))
    return Result(out)


def _has_table(path):
    from . import _gpkg
    try:
        connection, _ = _gpkg.table_of(path)
        connection.close()
        return True
    except (ValueError, FileNotFoundError):
        return False


def _detect_in_pixel_space(tool, model, in_raster, padding, batch, threshold, overlap, exclude):
    from . import _mosaic, _ortho
    if not (isinstance(in_raster, (str, os.PathLike)) and _mosaic.exists(in_raster)):
        raise ValueError(f"{tool}: PIXELSPACE detects in each image of a mosaic dataset of Frame Camera items (ADR-234).")
    srid = _mosaic_srid(in_raster)
    connection, name, rec, found, items = _ortho._frame_items(in_raster, tool)
    try:
        rows = []
        for oid, frame in found.items():
            import warnings
            import rasterio
            with warnings.catch_warnings():   # a photograph has no georeference
                warnings.simplefilter("ignore")
                with rasterio.open(items[oid]["Raster"]) as source:
                    arrays = source.read().astype(np.float32)
            if arrays.shape[0] != model.bands:
                raise ValueError(f"{tool}: the model takes {model.bands} bands and item {oid} has {arrays.shape[0]}.")
            for value, score, shape in detect(model, arrays, padding, batch, threshold, overlap, exclude):
                rows.append((value, score, image_to_ground(shape, frame, None), shape.wkt, int(oid)))
    finally:
        connection.close()
    return srid, rows


def image_to_ground(shape, frame, dem, iterations=5):
    """A shape in an image's pixel space (column, row from its corner) on the ground through the frame's model: at the
    model's average height, or with a DEM, at the DEM's height where the ray meets it, found by iterating height and
    position (INFERRED: five iterations from the average height, bilinear heights)."""

    def move(x, y, z=None):
        cols, rows = np.asarray(x, float), np.asarray(y, float)
        heights = np.full(cols.shape, frame.average_z)
        X, Y = frame.to_ground(cols, rows, heights)
        if dem is not None:
            for _ in range(iterations):
                heights = dem(X, Y, heights)
                X, Y = frame.to_ground(cols, rows, heights)
        return X, Y

    def moved(coordinates):
        X, Y = move(coordinates[:, 0], coordinates[:, 1])
        return np.column_stack([X, Y])

    return shapely.transform(shape, moved)


_CLASSIFY_OBJECT_ARGUMENTS = ("batch_size", "test_time_augmentation", "score_threshold", "threshold")


def ClassifyObjectsUsingDeepLearning(in_raster, out_feature_class, in_model_definition, in_features=None,
                                     class_label_field="ClassLabel", processing_mode="PROCESS_AS_MOSAICKED_IMAGE",
                                     model_arguments=None, caption_field=None):
    """Each object labelled by an object classification model grapy trained (FEATURE_CLASSIFIER): the image chip around
    each feature of ``in_features`` — its bounding box, made square about its centre and resampled to the model's chip
    size (INFERRED, ADR-248 §5.3) — or, with no features, each image as one object, its extent the shape. The output
    holds the features (or extents) with ``class_label_field`` (``ClassLabel``) and ``Confidence``, the softmax
    probability (INFERRED: the page names only the label field); appended when it exists.

    Model arguments: batch_size; test_time_augmentation (the softmax over the eight dihedral turns); threshold or
    score_threshold (a label below it is left null, INFERRED). ``caption_field`` is refused: it belongs to Image
    Captioner models, which grapy refuses."""
    from . import _gpkg, da
    from ._result import Result
    from .analysis import _out, _read, _schema
    tool = "ClassifyObjectsUsingDeepLearning"
    if _is_given(caption_field):
        _refuse(tool, "caption_field", caption_field, "it holds an Image Captioner model's captions, a model type grapy "
                                                      "refuses")
    _attached_input(tool, in_raster)
    model = Model(in_model_definition, tool, ("ObjectClassification",))
    args = model.arguments(model_arguments, _CLASSIFY_OBJECT_ARGUMENTS)
    torch = model.torch
    tta = _boolean(args.get("test_time_augmentation", False))
    threshold = float(args.get("threshold", args.get("score_threshold", 0)) or 0)
    label_field = str(class_label_field) if _is_given(class_label_field) else "ClassLabel"
    sources = chips.rasters(in_raster, processing_mode, tool)
    size = int(model.chip[0])
    jobs = []   # (array, shape, values, srid)
    columns = []
    if _is_given(in_features):
        raster = sources[0][1]
        model.check_bands(raster)
        columns = _schema(in_features)[0]
        for _, values, shape in _read(in_features, raster._grid.srid):
            x0, y0, x1, y1 = shape.bounds
            side = max(x1 - x0, y1 - y0, raster._grid.width)
            chip = chips.Chip((x0 + x1) / 2, (y0 + y1) / 2, size, size, side / size, side / size)
            arrays, mask = chip.sample(raster)
            jobs.append((np.stack([np.where(mask, 0, a) for a in arrays]).astype(np.float32), shape, values,
                         raster._grid.srid))
    else:
        for _, raster in sources:
            model.check_bands(raster)
            g = raster._grid
            side = max(g.right - g.left, g.top - g.bottom)
            chip = chips.Chip((g.left + g.right) / 2, (g.top + g.bottom) / 2, size, size, side / size, side / size)
            arrays, mask = chip.sample(raster)
            jobs.append((np.stack([np.where(mask, 0, a) for a in arrays]).astype(np.float32),
                         shapely.box(g.left, g.bottom, g.right, g.top), None, g.srid))
    results = []
    batch = _batch_size(args.get("batch_size", 4))
    for k in range(0, len(jobs), batch):
        group = jobs[k:k + batch]
        x = torch.from_numpy(np.stack([model.normalise(j[0]) for j in group]))
        with torch.no_grad():
            turns = [(t, f) for f in (False, True) for t in range(4)] if tta else [(0, False)]
            probability = sum(torch.softmax(model.model(_dihedral(x, t, f, torch)), 1) for t, f in turns) / len(turns)
        for p in probability.numpy():
            best = int(np.argmax(p))
            results.append((model.values[best], float(p[best])))
    out = _out(out_feature_class)
    kept = [(c.name, c.type) for c in columns if c.name.lower() not in (label_field.lower(), "confidence")]
    fields = kept + [(label_field, "String"), ("Confidence", "Double")]
    srid = jobs[0][3] if jobs else 0
    if not _has_table(out):
        shape_type = _schema(in_features)[1] if _is_given(in_features) else "Polygon"
        _gpkg.create(out, shape_type, srid, [_gpkg.Column(n, t) for n, t in fields])
    order = [c.name for c in columns]
    with da.InsertCursor(out, [n for n, _ in fields] + ["SHAPE@"]) as cursor:
        for (array, shape, values, _), (value, p) in zip(jobs, results):
            row = [values[order.index(n)] for n, _ in kept] if values is not None else []
            label = model.names.get(value, str(value)) if p >= threshold else None
            cursor.insertRow(tuple(row) + (label, p, shape))
    return Result(out)


# ---- Deep Learning Model To Ecd, and Classify Raster on its output


def DeepLearningModelToEcd(in_deep_learning_model, in_classification_info_json, out_classifier_definition):
    """A classifier definition (.ecd) for Classify Raster and Classify from a pixel classification model and the class
    information JSON whose keys the page's example shows (``Classifier``, ``NumberRasterBands``, ``MiniBatchSize``,
    ``Classes`` of ``Value``, ``Name``, ``Color``). The model is one grapy trained — its .emd, .dlpk, or the .pth beside
    its .emd; the .ecd is grapy's own (see grapy._classify), its classifier ``DeepLearning``, holding the model's
    path and the classes' names and colours from the JSON, as the page says the tool adds them.

    The page's model is *"the binary model file generated by a deep learning package such as Microsoft CNTK"*: a
    ``Classifier`` other than PyTorch or grapy (CNTK among them) is refused by name, as grapy carries no other framework.
    INFERRED (ADR-248 §5.3): the JSON's classes rename the model's by value; a value the model does not have is an error;
    the background the model predicts is class 0, ``Background``."""
    from ._classify import _write_ecd
    tool = "DeepLearningModelToEcd"
    path = chips._path(in_deep_learning_model)
    if path.lower().endswith(".pth"):
        candidates = [os.path.splitext(path)[0] + ".emd"]
        path = next((c for c in candidates if os.path.isfile(c)), path)
        if path.lower().endswith(".pth"):
            raise ValueError(f"{tool}: '{in_deep_learning_model}' has no .emd beside it to say what network it holds.")
    with open(chips._path(in_classification_info_json), encoding="utf-8-sig") as handle:
        info = json.load(handle)
    framework = str(info.get("Classifier") or "PyTorch")
    if framework.lower() not in ("pytorch", "grapy"):
        _refuse(tool, "in_classification_info_json", f"Classifier {framework}", f"grapy runs PyTorch models only, and "
                                                                               f"carries no {framework}")
    model = Model(path, tool, ("ImageClassification",))
    if _is_given(info.get("NumberRasterBands")) and int(info["NumberRasterBands"]) != model.bands:
        raise ValueError(f"{tool}: the JSON says {info['NumberRasterBands']} bands and the model takes {model.bands}.")
    names = {int(c["Value"]): c for c in info.get("Classes", [])}
    for value in names:
        if value not in model.values:
            raise ValueError(f"{tool}: the JSON's class {value} is not one of the model's ({model.values}).")
    classes = [{"value": 0, "name": "Background", "count": 0, "color": [0, 0, 0]}] + [
        {"value": int(v), "name": str(names.get(v, {}).get("Name", model.names.get(v, v))).strip(), "count": 0,
         "color": names.get(v, {}).get("Color")} for v in sorted(model.values)]
    source = model.definition.source
    return _write_ecd(out_classifier_definition, "DeepLearning", model.bands, 0, classes,
                      model=os.path.abspath(source), batch_size=int(info.get("MiniBatchSize") or 4))


def classify_with_ecd(data, mask, document, tool):
    """Classify Raster with a DeepLearning .ecd: the model's classes over the stacked bands (rows, columns, bands)."""
    model = Model(document["model"], tool, ("ImageClassification",))
    arrays = np.moveaxis(np.where(mask[..., None], 0, data), -1, 0).astype(np.float32)
    padding = int(model.defaults.get("padding", 0))
    classes = predict_pixels(model, arrays, padding, _batch_size(document.get("batch_size", 4)))[0]
    return np.concatenate([[0], model.values]).astype(np.int32)[classes]


# ---- Extract Features Using AI Models


_WORKFLOWS = ("Line Regularization", "Parcel Regularization", "Polygon Regularization", "Polygon Segmentation", "None")
_REGULARIZE = {"Right Angles": "RIGHT_ANGLES", "Right Angles and Diagonals": "RIGHT_ANGLES_AND_DIAGONALS",
               "Any Angles": "ANY_ANGLE", "Circle": "CIRCLE"}
EXTRACT_METHOD = ("grapy's detectors and pixel classifiers, then Regularize Building Footprint",
                  "ADR-248 §5.6; RegularizeBuildingFootprint's own sources")


def ExtractFeaturesUsingAIModels(in_raster, mode="Infer and Postprocess", out_location=None, out_prefix=None,
                                 area_of_interest=None, pretrained_models=None, additional_models=None,
                                 confidence_threshold=None, save_intermediate_output=False, test_time_augmentation=False,
                                 buffer_distance="15 Meters", extend_length="25 Meters",
                                 smoothing_tolerance="30 Meters", dangle_length="5 Meters", in_road_features=None,
                                 road_buffer_width=None, regularize_parcels=True, post_processing_workflow=None,
                                 out_features=None, parcel_tolerance="3 Meters", regularization_method="Right Angles",
                                 poly_tolerance="1 Meter", prompt="None", in_features=None, out_summary=None):
    """EQUIVALENT: features extracted with models grapy trained, each given with its post-processing workflow in
    ``additional_models`` (a value table of model path and workflow), and written to ``out_location`` as
    ``<out_prefix>_<n>`` — or ``out_features`` for a single model; ``out_summary`` lists each output and its path in
    fields ``Output`` and ``Path`` (INFERRED, ADR-248 §5.6).

    A detector's objects, or a pixel classifier's non-background regions as polygons, are the model's output
    (``save_intermediate_output`` keeps it as ``<out_prefix>_<n>_raw``); workflow ``Polygon Regularization`` then runs
    Regularize Building Footprint with ``regularization_method`` and ``poly_tolerance``, and ``None`` keeps the output
    as it is. ``Only Postprocess`` regularizes ``in_features`` (or the polygons of the binary ``in_raster``'s 1 cells).
    ``area_of_interest`` keeps what intersects it; ``confidence_threshold`` is the detectors' threshold.

    Refused by name: ``pretrained_models`` (Esri's models downloaded from ArcGIS Living Atlas, which a user tool has no
    network to reach and whose weights are Esri's); the Line and Parcel Regularization workflows (the page lists their
    parameters — buffer, extend, PAEK smoothing, dangle, road buffer, parcel tolerance — without the order or the
    operations that join them); Polygon Segmentation (it prompts a segmentation model the page does not name with each
    object's centroid or box)."""
    from . import _gpkg, da
    from ._result import Result
    from .analysis import _read
    tool = "ExtractFeaturesUsingAIModels"
    if _is_given(pretrained_models):
        _refuse(tool, "pretrained_models", pretrained_models, "they are Esri's trained models, downloaded from ArcGIS "
                                                              "Living Atlas, which a user tool has no network to reach "
                                                              "and which Esri does not publish")
    run_mode = _word(mode, "Infer and Postprocess", ("Infer and Postprocess", "Only Postprocess"), tool, "mode")
    method = _REGULARIZE.get(str(regularization_method or "Right Angles").strip())
    if method is None:
        raise ValueError(f"{tool}: regularization_method is one of {', '.join(_REGULARIZE)}.")
    location = chips._path(out_location) if _is_given(out_location) else env.workspace
    prefix = str(out_prefix or "extracted")
    aoi = None
    if _is_given(area_of_interest):
        aoi = shapely.union_all([s for _, _, s in _read(area_of_interest)])
    jobs = []
    if run_mode == "Only Postprocess":
        workflow = _workflow(tool, post_processing_workflow or "Polygon Regularization")
        jobs.append((None, workflow))
    else:
        if not _is_given(additional_models):
            raise ValueError(f"{tool}: give a model in additional_models (pretrained_models are Esri's, refused).")
        listed = additional_models if isinstance(additional_models, (list, tuple)) else \
            [p.strip().split(None, 1) if " " in p.strip() and not os.path.exists(p.strip()) else [p.strip()]
             for p in str(additional_models).split(";") if p.strip()]
        for entry in listed:
            entry = [entry] if isinstance(entry, str) else list(entry)
            jobs.append((entry[0], _workflow(tool, entry[1] if len(entry) > 1 and _is_given(entry[1]) else "None")))
    outputs = []
    for n, (model_path, workflow) in enumerate(jobs, 1):
        name = os.path.join(_gpkg_of(location), f"{prefix}_{n}") if not (len(jobs) == 1 and _is_given(out_features)) \
            else chips._path(out_features)
        raw = f"{name}_raw"
        if model_path is None:
            if _is_given(in_features):
                source = in_features
            else:
                source = _polygons_of(_binary(in_raster, tool), raw, tool)
        else:
            definition = dl.read_definition(model_path, tool)
            if definition["ModelType"] == "ObjectDetection":
                arguments = {}
                if _is_given(confidence_threshold):
                    arguments["threshold"] = float(confidence_threshold)
                DetectObjectsUsingDeepLearning(in_raster, raw, definition, arguments or None)
            elif definition["ModelType"] == "ImageClassification":
                if workflow == "Polygon Regularization":
                    raise ValueError(f"{tool}: Polygon Regularization is only compatible with object detection models, "
                                     "as the page says.")
                arguments = {"test_time_augmentation": True} if _boolean(test_time_augmentation) else None
                classified = ClassifyPixelsUsingDeepLearning(in_raster, definition, arguments)
                _polygons_of(classified, raw, tool, keep_zero=False)
            else:
                raise ValueError(f"{tool}: the model is of type {definition['ModelType']}; Extract Features takes "
                                 "detection and pixel classification models.")
            source = raw
        if aoi is not None:
            _keep_within(source, aoi)
        if workflow == "Polygon Regularization":
            from ._surfaces3d import RegularizeBuildingFootprint
            RegularizeBuildingFootprint(source, name, method, _linear(poly_tolerance))
        else:
            from . import management
            management.CopyFeatures(source, name)
        if source == raw and not _boolean(save_intermediate_output):
            from . import management
            management.Delete(raw)
        outputs.append((os.path.basename(name), name))
    if _is_given(out_summary):
        summary = chips._path(out_summary)
        from .analysis import _out
        summary = _out(summary)
        _gpkg.create(summary, "Geometry", 0, [_gpkg.Column("Output", "String"), _gpkg.Column("Path", "String")])
        with da.InsertCursor(summary, ["Output", "Path", "SHAPE@"]) as cursor:
            for label, path in outputs:
                cursor.insertRow((label, path, None))
    return Result(*[p for _, p in outputs])


def _workflow(tool, value):
    text = str(value).strip()
    found = next((w for w in _WORKFLOWS if w.lower() == text.lower() or w.replace(" ", "_").upper() == text.upper()), None)
    if found is None:
        raise ValueError(f"{tool}: the post-processing workflow is one of {', '.join(_WORKFLOWS)}.")
    if found in ("Line Regularization", "Parcel Regularization"):
        _refuse(tool, "post_processing_workflow", found, "the page lists its parameters — buffer, extend, PAEK smoothing, "
                                                         "dangle, road buffer, parcel tolerance — but not the order or "
                                                         "the operations that join them")
    if found == "Polygon Segmentation":
        _refuse(tool, "post_processing_workflow", found, "it prompts a segmentation model the page does not name with "
                                                         "each object's centroid or bounding box")
    return found


def _gpkg_of(location):
    return location if location.lower().endswith(".gpkg") else os.path.join(location, "extracted.gpkg")


def _linear(value):
    from .analysis import _distance
    return _distance(value, 0) if _is_given(value) else 1.0


def _binary(in_raster, tool):
    from ._raster import _open_bands
    raster = _open_bands(in_raster, tool)
    if len(raster._arrays) != 1:
        raise ValueError(f"{tool}: Only Postprocess takes a single-band binary raster, as the page says.")
    return raster


def _polygons_of(raster, out, tool, keep_zero=False):
    """The raster's regions of each non-zero value as polygons, with ``Classvalue`` (by Raster To Polygon's regions)."""
    from rasterio.features import shapes
    from . import _gpkg, da
    from .analysis import _out
    out = _out(out)
    array = np.asarray(raster._array).astype(np.int32)
    mask = ~raster._mask & ((array != 0) | keep_zero)
    _gpkg.create(out, "Polygon", raster._grid.srid, [_gpkg.Column("Classvalue", "Integer")])
    with da.InsertCursor(out, ["Classvalue", "SHAPE@"]) as cursor:
        for geometry, value in shapes(array, mask=mask, transform=raster._grid.transform()):
            cursor.insertRow((int(value), shapely.geometry.shape(geometry)))
    return out


def _keep_within(path, area):
    from . import da
    with da.UpdateCursor(path, ["SHAPE@"]) as rows:
        for (shape,) in rows:
            if shape is None or not shape._shape.intersects(area):
                rows.deleteRow()


# ---- Transform Image Shapes


def TransformImageShapes(in_shape_table, out_shape_table, in_orientations, in_dem=None):
    """Features in image space carried to map space: each feature's ``iShape`` (grapy's encoding: well-known text in
    the image's pixel coordinates, column and row from its corner) and ``ImageID`` (the Frame Camera item of the mosaic
    dataset ``in_orientations``, ADR-234), as Detect Objects Using Deep Learning writes them with PIXELSPACE (INFERRED,
    ADR-248 §5.6), through that item's collinearity model: at the item's average height, or where its rays meet
    ``in_dem``. The output is the input's features with the new shapes, in the mosaic dataset's coordinate system."""
    from . import _gpkg, _ortho, da
    from ._raster import _open_bands
    from ._result import Result
    from .analysis import _out, _schema
    tool = "TransformImageShapes"
    columns, kind, _, _ = _schema(in_shape_table)
    names = {c.name.lower(): c.name for c in columns}
    if "ishape" not in names or "imageid" not in names:
        raise ValueError(f"{tool}: the input needs the iShape and ImageID fields Detect Objects Using Deep Learning "
                         "writes in pixel space.")
    connection, mosaic, rec, frames, _ = _ortho._frame_items(in_orientations, tool)
    connection.close()
    dem = None
    if _is_given(in_dem):
        raster = _open_bands(in_dem, tool)
        heights = np.asarray(raster._arrays[0], float)

        def height(X, Y, z):
            g = raster._grid
            sampled = _ortho.sample(heights, (np.asarray(X) - g.left) / g.width, (g.top - np.asarray(Y)) / g.height)
            return np.where(np.isfinite(sampled), sampled, z)
        dem = height
    srid = _mosaic_srid(in_orientations)
    out = _out(out_shape_table)
    _gpkg.create(out, kind if kind != "Geometry" else "Polygon", srid, columns)
    order = [c.name for c in columns]
    with da.SearchCursor(in_shape_table, order) as rows, da.InsertCursor(out, order + ["SHAPE@"]) as cursor:
        for values in rows:
            text, image = values[order.index(names["ishape"])], values[order.index(names["imageid"])]
            frame = frames.get(int(image)) if image is not None else None
            if not text or frame is None:
                cursor.insertRow(tuple(values) + (None,))
                continue
            cursor.insertRow(tuple(values) + (image_to_ground(shapely.from_wkt(text), frame, dem),))
    return Result(out)


def _mosaic_srid(value):
    from . import _mosaic
    connection, name, rec, _ = _mosaic.open_mosaic(value, "TransformImageShapes")
    try:
        row = connection.execute("select srs_id from gpkg_contents where table_name = ?", (name,)).fetchone()
        return int(row[0]) if row and row[0] else 0
    finally:
        connection.close()
