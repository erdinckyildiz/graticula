"""grapy's deep learning foundation — ADR-248. The framework, the device, the model definition (.emd) and package
(.dlpk), checkpoints, and the training loop that every deep learning tool shares: the imagery tools of ``_dltools`` and
the point cloud tools of ``arcpy.ddd``.

**The framework is PyTorch** (with torchvision), CPU builds in the server image (owner decision 2026-10-31, *"Tam aç"*).
It is imported when a tool first needs it, never by ``import grapy``: a Python without torch runs every other tool, and a
deep learning tool there says which package is missing (``FrameworkMissing``).

The Esri model definition
=========================

An ``.emd`` is a JSON file whose keys Esri documents in its public ``raster-deep-learning`` repository
(``docs/writing_model_definition.md``): ``Framework``, ``ModelConfiguration``, ``ModelFile``, ``InferenceFunction``,
``ModelType``, ``ImageHeight``, ``ImageWidth``, ``ExtractBands``, ``DataRange``, ``BatchSize``, ``ModelPadding``,
``ModelName`` and ``Version``; ``Classes`` (``Value``, ``Name``, ``Color``) appears in its examples. A ``.dlpk`` is a zip
holding exactly one ``.emd`` and the files it names (Pro's error 260458 refuses a package with more than one).

grapy writes and reads these keys for the models it trains (INFERRED, ADR-248 §5.2, Q-218):

- ``Framework`` ``"PyTorch"``, ``ModelConfiguration`` ``"grapy"``, no ``InferenceFunction``;
- ``ModelType`` ``"ObjectDetection"``, ``"ImageClassification"`` (pixels, as the repository uses the name),
  ``"ObjectClassification"``, ``"ChangeDetection"``, ``"PixelTranslation"`` — or a point cloud type the ``ddd`` tools
  define;
- ``ModelFile`` the weights, a PyTorch ``state_dict`` saved with ``torch.save`` beside the ``.emd``;
- ``ImageHeight``/``ImageWidth`` the chip size, ``ExtractBands`` the band indexes from 0, ``DataRange`` the training
  data's minimum and maximum, ``Classes`` the classes;
- a ``"grapy"`` object: ``{"version": 1, "architecture", "backbone", "bands", "classes": [values in the model's order],
  "mean", "std", "arguments"}`` — what grapy needs to rebuild the network and normalise its input.

A definition whose ``Framework`` is not PyTorch, whose ``ModelConfiguration`` is not ``"grapy"``, or that names an
``InferenceFunction`` is refused by name. The last is how ArcGIS Pro's own models run: the function is a Python raster
function that imports ``arcgis.learn``, Esri's proprietary package, which grapy neither has nor reproduces.
"""
from __future__ import annotations

import copy
import json
import math
import os
import shutil
import tempfile
import time
import zipfile

import numpy as np

from ._runtime import AddMessage, NotBuiltError, _is_given, env

GRAPY_CONFIGURATION = "grapy"
DEFINITION_VERSION = 1


class FrameworkMissing(ImportError):
    """A deep learning tool ran on a Python without PyTorch."""


def framework(tool="grapy"):
    """``torch``, imported on first use; ``FrameworkMissing`` naming the packages when this Python lacks them."""
    try:
        import torch
    except ImportError as error:
        raise FrameworkMissing(f"{tool} needs PyTorch: the packages 'torch' and 'torchvision' are not installed in this "
                               "Python (pip install torch torchvision). The server image carries them (ADR-248).") from error
    return torch


def vision(tool="grapy"):
    framework(tool)
    try:
        import torchvision
    except ImportError as error:
        raise FrameworkMissing(f"{tool} needs torchvision, which is not installed in this Python (pip install "
                               "torchvision). The server image carries it (ADR-248).") from error
    return torchvision


def device(tool="grapy"):
    """The device a model runs on: CUDA's first GPU when this Python's torch has one, else the CPU (INFERRED: arcpy's
    ``processorType`` and ``gpuId`` environments are not held; the server image's torch is a CPU build)."""
    torch = framework(tool)
    return torch.device("cuda:0" if torch.cuda.is_available() else "cpu")


def seed(value=None, tool="grapy"):
    """Seeds torch, numpy and Python from ``env.randomGenerator`` (0 when unset), so that a run on the CPU repeats."""
    import random
    from .management import _seed
    torch = framework(tool)
    number = int(_seed() if value is None else value) % (2 ** 31)
    torch.manual_seed(number)
    random.seed(number)
    np.random.seed(number)
    return number


def threads(tool="grapy"):
    """Bounds torch's CPU threads to what the run may use: the fence counts each thread as a task (ADR-223)."""
    torch = framework(tool)
    want = max(1, min(4, os.cpu_count() or 1))
    if torch.get_num_threads() > want:
        torch.set_num_threads(want)


# ---- the model definition and package


class Definition(dict):
    """A model definition read from an ``.emd``, a JSON string or a ``.dlpk``; ``folder`` is where its files are."""

    folder = None
    source = None

    @property
    def grapy(self):
        return self.get("grapy") or {}

    def path_of(self, key="ModelFile"):
        name = self.get(key)
        if not name:
            return None
        return name if os.path.isabs(name) else os.path.join(self.folder or "", name)


def _package_folder():
    base = env.scratchWorkspace or env.workspace or tempfile.gettempdir()
    if ".gpkg" in str(base).lower():
        base = os.path.dirname(str(base))
    return tempfile.mkdtemp(prefix="dlpk_", dir=base if os.path.isdir(str(base)) else None)


def read_definition(value, tool, kinds=None):
    """The Definition of ``in_model_definition``: an ``.emd`` path, a ``.dlpk`` path or the JSON itself. Refuses by name
    a definition grapy cannot run — see the module's docstring; ``kinds`` limits the ModelType the tool takes."""
    if isinstance(value, Definition):
        definition = value
    else:
        text = os.fspath(value) if isinstance(value, os.PathLike) else str(value)
        if text.lstrip().startswith("{"):
            definition, folder, source = Definition(json.loads(text)), env.workspace, "JSON"
        else:
            path = text if os.path.isabs(text) else os.path.join(env.workspace or "", text)
            if not os.path.isfile(path):
                raise ValueError(f"{tool}: the model definition '{value}' does not exist.")
            if zipfile.is_zipfile(path):
                folder = _package_folder()
                with zipfile.ZipFile(path) as package:
                    for member in package.namelist():
                        target = os.path.realpath(os.path.join(folder, member))
                        if not target.startswith(os.path.realpath(folder)):
                            raise ValueError(f"{tool}: '{value}' holds a path outside the package ({member}).")
                    package.extractall(folder)
                found = [os.path.join(r, f) for r, _, fs in os.walk(folder) for f in fs if f.lower().endswith(".emd")]
                if len(found) != 1:
                    raise ValueError(f"{tool}: a deep learning package holds exactly one .emd file; '{value}' holds "
                                     f"{len(found)}.")
                path = found[0]
            with open(path, encoding="utf-8-sig") as handle:
                definition = Definition(json.load(handle))
            folder, source = os.path.dirname(path), path
        definition.folder, definition.source = folder, source
    framework_name = str(definition.get("Framework") or "")
    if _is_given(definition.get("InferenceFunction")):
        raise NotBuiltError(
            f"{tool}: the model definition names the inference function '{definition.get('InferenceFunction')}', a "
            "Python raster function that ArcGIS runs with Esri's arcgis.learn — the proprietary arcgis package — and "
            "grapy neither has arcgis.learn nor reproduces it; grapy runs the models its own Train Deep Learning Model "
            "writes (ADR-248 §5.2).")
    if framework_name.lower() != "pytorch":
        raise NotBuiltError(f"{tool}: the model definition's Framework is '{framework_name or 'not given'}', and grapy "
                            "runs PyTorch models only (ADR-248 §5.2).")
    configuration = definition.get("ModelConfiguration")
    if isinstance(configuration, dict):
        configuration = configuration.get("Name")
    if configuration != GRAPY_CONFIGURATION or not definition.grapy:
        raise NotBuiltError(f"{tool}: the model definition's ModelConfiguration is '{configuration}', not an "
                            "architecture grapy builds: grapy reads the definitions its own training tools write, whose "
                            "configuration is 'grapy' (ADR-248 §5.2).")
    if kinds and definition.get("ModelType") not in kinds:
        raise ValueError(f"{tool}: the model is of type '{definition.get('ModelType')}', and {tool} takes "
                         f"{' or '.join(kinds)}.")
    return definition


def write_definition(folder, name, model_type, architecture, backbone, bands, classes, chip, data_range, mean, std,
                     arguments=None, weights=None, extra=None):
    """Writes ``<folder>/<name>.emd`` (and returns its path) for a model whose weights are ``<name>.pth``.
    ``classes`` are dicts of value, name and an optional colour."""
    os.makedirs(folder, exist_ok=True)
    document = {
        "Framework": "PyTorch", "ModelConfiguration": GRAPY_CONFIGURATION, "ModelType": model_type,
        "ModelFile": weights or f"{name}.pth", "ImageHeight": int(chip[1]), "ImageWidth": int(chip[0]),
        "ExtractBands": list(range(int(bands))), "DataRange": [float(data_range[0]), float(data_range[1])],
        "ModelPadding": 0, "BatchSize": int((arguments or {}).get("batch_size", 4)), "ModelName": name,
        "Version": time.strftime("%Y.%m"),
        "Classes": [{"Value": int(c["value"]), "Name": str(c["name"]),
                     "Color": list(c.get("color") or _colour(k))} for k, c in enumerate(classes)],
        "grapy": {"version": DEFINITION_VERSION, "architecture": architecture, "backbone": backbone, "bands": int(bands),
                  "classes": [int(c["value"]) for c in classes], "mean": [float(m) for m in mean],
                  "std": [float(s) for s in std], "arguments": dict(arguments or {})},
    }
    if extra:
        document["grapy"].update(extra)
    path = os.path.join(folder, f"{name}.emd")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=1)
    return path


def _colour(k):
    """A distinct colour per class (INFERRED): golden-angle hues at full saturation."""
    import colorsys
    r, g, b = colorsys.hsv_to_rgb((k * 0.381966) % 1.0, 0.75, 0.95)
    return [int(r * 255), int(g * 255), int(b * 255)]


def write_package(emd_path, out_path=None):
    """A ``.dlpk`` beside the ``.emd`` (or at ``out_path``) holding it and the files it names."""
    with open(emd_path, encoding="utf-8") as handle:
        document = json.load(handle)
    folder = os.path.dirname(emd_path)
    out_path = out_path or os.path.splitext(emd_path)[0] + ".dlpk"
    # Stored, not deflated: weights barely compress, and deflating a ResNet's took seconds per package.
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_STORED) as package:
        package.write(emd_path, os.path.basename(emd_path))
        for key in ("ModelFile",):
            if document.get(key):
                package.write(os.path.join(folder, document[key]), document[key])
        for extra in document.get("grapy", {}).get("files", []):
            package.write(os.path.join(folder, extra), extra)
    return out_path


# ---- checkpoints


def save_weights(model, path, tool="grapy"):
    torch = framework(tool)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    torch.save({k: v.detach().cpu() for k, v in model.state_dict().items()}, path)
    return path


def load_weights(model, path, tool="grapy", strict=True):
    """Weights into a model, read with ``weights_only`` — a checkpoint is tensors, never code it would run."""
    torch = framework(tool)
    state = torch.load(path, map_location="cpu", weights_only=True)
    if isinstance(state, dict) and "state_dict" in state and isinstance(state["state_dict"], dict):
        state = state["state_dict"]
    try:
        model.load_state_dict(state, strict=strict)
    except RuntimeError as error:
        raise ValueError(f"{tool}: the weights in '{path}' do not fit the network the definition describes: "
                         f"{str(error).splitlines()[0]}") from error
    return model


# ---- training


ONE_CYCLE = ("Adam with a one-cycle learning rate", "Kingma and Ba 2015, ICLR; Smith and Topin 2019, Proc. SPIE 11006")
RANGE_TEST = ("the learning rate range test", "Smith 2017, WACV, §3.3")


def find_learning_rate(model, batches, step, tool="grapy", low=1e-6, high=1.0, count=None, seconds=None):
    """The learning rate range test (Smith 2017, §3.3): Adam on a copy of the model, the rate raised exponentially from
    ``low`` to ``high`` over the batches; answers a tenth of the rate at which the smoothed loss was least (INFERRED: the
    tenth, a common reading of the paper's *"the learning rate at which the loss decreases fastest"*). With ``seconds``
    the test stops when that much time has passed, with the best rate found so far."""
    torch = framework(tool)
    trial = copy.deepcopy(model)
    trial.train()
    parameters = [p for p in trial.parameters() if p.requires_grad]
    optimiser = torch.optim.Adam(parameters, lr=low)
    batches = list(batches)
    count = max(2, min(len(batches) * 3, count or 30))
    factor = (high / low) ** (1.0 / (count - 1))
    best, best_rate, smoothed = math.inf, 1e-3, None
    rate = low
    started = time.time()
    for k in range(count):
        if seconds is not None and k >= 2 and time.time() - started >= seconds:
            break
        for group in optimiser.param_groups:
            group["lr"] = rate
        loss = step(trial, batches[k % len(batches)])
        optimiser.zero_grad()
        loss.backward()
        optimiser.step()
        value = float(loss.detach())
        if not math.isfinite(value):
            break
        smoothed = value if smoothed is None else 0.8 * smoothed + 0.2 * value
        if smoothed < best:
            best, best_rate = smoothed, rate
        if smoothed > 4 * best:
            break
        rate *= factor
    return best_rate / 10.0


def fit(model, train_batches, valid_batches, step, evaluate, epochs, learning_rate, monitor="valid_loss",
        larger_is_better=False, early_stopping=True, patience=5, tool="grapy", parameters=None, seconds=None,
        schedule="ONE_CYCLE", on_epoch=None):
    """The training loop every grapy deep learning tool shares: Adam (Kingma and Ba 2015) under a one-cycle learning
    rate (Smith and Topin 2019) peaking at ``learning_rate``, one pass over ``train_batches`` an epoch; after each epoch
    ``evaluate(model, valid_batches)`` answers a dict holding ``monitor``. The model ends with the weights of the best
    epoch. With ``early_stopping`` training stops when ``monitor`` has not improved for ``patience`` epochs (INFERRED: 5).

    With ``seconds``, training also stops after the epoch in which that much time has passed. ``schedule`` ``FIXED``
    keeps ``learning_rate`` throughout instead of the one-cycle schedule; ``on_epoch(epoch, model, scores)``, when given,
    is called after each epoch (the point cloud tools write their checkpoints from it, ADR-249).

    ``train_batches`` is a callable answering the epoch's batches (so that each epoch may shuffle and augment);
    ``step(model, batch)`` answers the loss to minimise. Answers the history, a list of dicts per epoch."""
    torch = framework(tool)
    threads(tool)
    parameters = [p for p in (parameters or model.parameters()) if p.requires_grad]
    optimiser = torch.optim.Adam(parameters, lr=learning_rate)
    first = train_batches(0)
    per_epoch = max(1, len(first))
    scheduler = (torch.optim.lr_scheduler.OneCycleLR(optimiser, max_lr=learning_rate, total_steps=per_epoch * int(epochs))
                if str(schedule).upper() != "FIXED" else torch.optim.lr_scheduler.LambdaLR(optimiser, lambda _: 1.0))
    history, best, best_state, waited = [], None, None, 0
    started = time.time()
    for epoch in range(int(epochs)):
        model.train()
        losses = []
        for k, batch in enumerate(first if epoch == 0 else train_batches(epoch)):
            if k >= per_epoch:
                break
            loss = step(model, batch)
            optimiser.zero_grad()
            loss.backward()
            optimiser.step()
            scheduler.step()
            losses.append(float(loss.detach()))
        model.eval()
        with torch.no_grad():
            scores = dict(evaluate(model, valid_batches))
        scores["train_loss"] = float(np.mean(losses)) if losses else float("nan")
        scores["epoch"] = epoch + 1
        history.append(scores)
        if on_epoch is not None:
            on_epoch(epoch + 1, model, scores)
        value = scores.get(monitor)
        AddMessage(f"{tool}: epoch {epoch + 1}: train loss {scores['train_loss']:.4f}, "
                   + ", ".join(f"{k} {v:.4f}" for k, v in scores.items() if k not in ("epoch", "train_loss")
                               and isinstance(v, float)))
        improved = value is not None and math.isfinite(value) and (
            best is None or (value > best if larger_is_better else value < best))
        if improved or best_state is None:
            if improved:
                best = value
            best_state = {k: v.detach().clone() for k, v in model.state_dict().items()}
            waited = 0
        else:
            waited += 1
            if early_stopping and waited >= patience:
                AddMessage(f"{tool}: {monitor} has not improved for {patience} epochs; training stops (early stopping).")
                break
        if seconds is not None and time.time() - started >= seconds:
            AddMessage(f"{tool}: the time allowed ({seconds:.0f} s) is spent after epoch {epoch + 1}.")
            break
    if best_state is not None:
        model.load_state_dict(best_state)
    model.eval()
    return history


def freeze(module, tool="grapy"):
    for parameter in module.parameters():
        parameter.requires_grad_(False)


def adapt_first_convolution(conv, bands, scheme="RANDOM", tool="grapy"):
    """A copy of a first convolution taking ``bands`` input channels, its weights by Train Deep Learning Model's
    weight_init_scheme: RANDOM keeps the weights of the first three channels and draws the others; RED_BAND copies the
    first channel's (the red band) to the others; ALL_RANDOM draws every channel's (Kaiming init, He et al. 2015)."""
    torch = framework(tool)
    nn = torch.nn
    if conv.in_channels == bands:
        return conv
    new = nn.Conv2d(bands, conv.out_channels, conv.kernel_size, conv.stride, conv.padding, conv.dilation, conv.groups,
                    conv.bias is not None)
    with torch.no_grad():
        nn.init.kaiming_normal_(new.weight, mode="fan_out", nonlinearity="relu")
        if scheme != "ALL_RANDOM":
            keep = min(bands, conv.in_channels)
            new.weight[:, :keep] = conv.weight[:, :keep]
            if scheme == "RED_BAND":
                for k in range(keep, bands):
                    new.weight[:, k] = conv.weight[:, 0]
        if conv.bias is not None:
            new.bias.copy_(conv.bias)
    return new


def copy_tree(source, target):
    os.makedirs(target, exist_ok=True)
    for name in os.listdir(source):
        shutil.copy2(os.path.join(source, name), os.path.join(target, name))
