"""Runs one tool of a Python toolbox, fenced, in its run's directory — ADR-223 §5.3, §5.5 and §5.7.

``python -I -m grapy.runner <run directory>``. The directory holds ``run.json`` — what to do — and the toolbox's source;
the runner writes ``result.json`` and nothing outside the directory. Two modes:

- ``describe``: the toolbox's label and its tools with their parameters, read from ``getParameterInfo`` — what the
  server lists as the GPServer's tasks when the toolbox is published;
- ``run``: one tool's ``execute`` with the values sent, its messages, and each output parameter's value.

The fence goes up before the toolbox is imported, so nothing in it runs unfenced — its module-level code included. On
anything but Linux the runner refuses, unless the server passed ``GRAPY_UNFENCED=1`` because an operator chose the
unsafe development setting (ADR-223 §5.9b).
"""
from __future__ import annotations

import importlib.machinery
import importlib.util
import json
import os
import sys
import traceback


# The raster types the server's task resource lists as GPRasterDataLayer (ToolboxEndpoints.DataTypeOf).
_RASTER_TYPES = {"GPRasterLayer", "GPRasterDataLayer", "DERasterDataset", "Raster Layer", "Raster Dataset",
                 "GPRasterLayerOrDataset"}


def _write(directory: str, document: dict) -> None:
    with open(os.path.join(directory, "result.json"), "w", encoding="utf-8") as out:
        json.dump(document, out, default=str)


class _Messages:
    """The ``messages`` object an ``execute`` is handed, writing where arcpy's messages go."""

    def __init__(self, runtime):
        self._runtime = runtime

    def addMessage(self, message):
        self._runtime.AddMessage(message)

    def addWarningMessage(self, message):
        self._runtime.AddWarning(message)

    def addErrorMessage(self, message):
        self._runtime.AddError(message)

    def addIDMessage(self, message_type, message_ID, add_argument1=None, add_argument2=None):
        self._runtime.AddIDMessage(message_type, message_ID, add_argument1, add_argument2)

    def addGPMessages(self):
        pass


def _load(path: str):
    # A loader named outright: `.pyt`, the extension a Python toolbox has in ArcGIS, is not one Python recognises.
    loader = importlib.machinery.SourceFileLoader("toolbox", path)
    spec = importlib.util.spec_from_file_location("toolbox", path, loader=loader)
    if spec is None or spec.loader is None:
        raise ImportError(f"'{os.path.basename(path)}' is not a Python file a toolbox can be read from.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not hasattr(module, "Toolbox"):
        raise ImportError("The file has no class Toolbox, which a Python toolbox names its tools in.")
    return module


def _tools(module):
    toolbox = module.Toolbox()
    return toolbox, {cls.__name__: cls for cls in getattr(toolbox, "tools", [])}


def main(directory: str) -> int:
    directory = os.path.abspath(directory)
    with open(os.path.join(directory, "run.json"), encoding="utf-8") as read:
        run = json.load(read)

    import grapy
    from grapy import _runtime

    fenced = None
    try:
        if os.environ.get("GRAPY_UNFENCED") != "1":
            # Imported here: the fence is Linux's, and an unfenced development run on another system never loads it.
            from grapy import fence

            limits = run.get("limits", {})
            fenced = fence.up(directory, int(limits.get("memory", 2 << 30)), int(limits.get("cpu", 600)),
                              int(limits.get("file", 1 << 30)), int(limits.get("processes", 1024)))
    except (ImportError, RuntimeError) as refused:  # FenceError is a RuntimeError; no fence module is an ImportError
        _write(directory, {"ok": False, "fence": False, "error": f"The run was not started: {refused}."})
        return 3

    os.chdir(directory)
    grapy.env.workspace = directory
    grapy.env.scratchWorkspace = directory

    try:
        module = _load(os.path.join(directory, run["toolbox"]))
        toolbox, tools = _tools(module)

        if run["mode"] == "describe":
            described = []
            for name, cls in tools.items():
                tool = cls()
                parameters = tool.getParameterInfo() or []
                described.append({
                    "name": name,
                    "label": getattr(tool, "label", name),
                    "description": getattr(tool, "description", ""),
                    "canRunInBackground": getattr(tool, "canRunInBackground", True),
                    "parameters": [p.describe() for p in parameters],
                })
            _write(directory, {"ok": True, "fence": fenced, "toolbox": {
                "label": getattr(toolbox, "label", ""), "alias": getattr(toolbox, "alias", ""),
                "description": getattr(toolbox, "description", "")}, "tools": described})
            return 0

        cls = tools.get(run["tool"])
        if cls is None:
            raise LookupError(f"The toolbox has no tool '{run['tool']}'.")
        tool = cls()
        parameters = tool.getParameterInfo() or []
        sent = dict(run.get("parameters", {}))

        # Layers the server wrote as GeoJSON become the GeoPackage feature classes a tool reads (ADR-223 §5.5).
        from grapy import _io
        for name, written in run.get("inputs", {}).items():
            if written.endswith(".raster.json"):
                # An image service's band, written raw by the server, becomes the GeoTIFF a tool opens (ADR-224).
                tif = written[: -len(".raster.json")] + ".tif"
                _io.raster_from_raw(os.path.join(directory, written), os.path.join(directory, tif))
                sent[name] = tif
                continue
            feature_class = os.path.splitext(written)[0] + ".gpkg"
            _io.geojson_to_gpkg(os.path.join(directory, written), os.path.join(directory, feature_class))
            sent[name] = feature_class
        for parameter in parameters:
            if parameter.direction == "Input" and parameter.name in sent:
                parameter.value = sent[parameter.name]
        _runtime.PARAMETERS[:] = parameters

        if hasattr(tool, "isLicensed") and not tool.isLicensed():
            raise PermissionError("The tool says it is not licensed to run.")
        if hasattr(tool, "updateParameters"):
            tool.updateParameters(parameters)
        if hasattr(tool, "updateMessages"):
            tool.updateMessages(parameters)
        refused = [f"{p.name}: {p.message}" for p in parameters if getattr(p, "message", "")]
        if refused:
            raise ValueError("; ".join(refused))

        tool.execute(parameters, _Messages(_runtime))
        for parameter in parameters:
            # An output set to a Raster object is its file, saved first when it is only in memory, as arcpy reads it.
            if parameter.direction == "Output" and hasattr(parameter.value, "catalogPath") and hasattr(parameter.value, "save"):
                if getattr(parameter.value, "isTemporary", False):
                    parameter.value.save(os.path.join(directory, f"{parameter.name}_saved.tif"))
                parameter.value = parameter.value.catalogPath
        outputs = {p.name: p.valueAsText for p in parameters if p.direction == "Output"}

        # A feature class output goes back as GeoJSON in WGS 84, which the server imports as a hosted layer; a raster as
        # a GeoTIFF, which it publishes as an image service (D-324). The server finds each at outputs/<parameter>, by
        # its own name for it, and reads no path from here.
        layers, rasters = {}, {}
        datatypes = {p.name: p.describe()["datatype"] for p in parameters if p.direction == "Output"}
        os.makedirs(os.path.join(directory, "outputs"), exist_ok=True)
        for name, value in outputs.items():
            if value and ".gpkg" in value.lower():
                written = os.path.join("outputs", f"{name}.geojson")
                layers[name] = {"geojson": written, "count": _io.gpkg_to_geojson(os.path.join(directory, value),
                                                                                 os.path.join(directory, written))}
            elif value and datatypes.get(name) in _RASTER_TYPES:
                written = os.path.join("outputs", f"{name}.tif")
                _io.raster_to_geotiff(os.path.join(directory, value), os.path.join(directory, written))
                rasters[name] = {"tiff": written}
        failed = any(m["type"] == "esriJobMessageTypeError" for m in _runtime.MESSAGES)
        _write(directory, {"ok": not failed, "fence": fenced, "messages": _runtime.MESSAGES, "outputs": outputs, "layers": layers,
                           "rasters": rasters, "datatypes": datatypes})
        return 0 if not failed else 1
    except BaseException as failure:  # The tool's own failure, said whole — the process ends here anyway.
        _runtime.MESSAGES.append({"type": "esriJobMessageTypeError", "description": f"{type(failure).__name__}: {failure}"})
        _write(directory, {"ok": False, "fence": fenced, "messages": _runtime.MESSAGES,
                           "error": f"{type(failure).__name__}: {failure}",
                           "trace": traceback.format_exc(limit=8)[-4000:]})
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
