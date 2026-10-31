"""Toolboxes in a session — ImportToolbox, AddToolbox, RemoveToolbox, ListToolboxes, ListTools, Usage, Command,
GetParameterInfo and GetParameterValue (ADR-247 §5.3).

A session holds two kinds of toolbox. **grapy's own** are its tool modules, each under the alias arcpy gives it —
``analysis``, ``management``, ``sa`` … — and the label the tool reference gives it (``Analysis Tools``); their tools are
the public functions each module builds. **Imported** ones are Python toolboxes (``.pyt``), which ImportToolbox loads
as grapy's runner loads a published toolbox (ADR-223): the file's ``Toolbox`` class names the tools, and each tool's
``getParameterInfo`` its parameters. A tool is named, as the pages say, ``<tool>_<alias>`` or ``<alias>.<tool>``.

What stays refused, each by its own reason:

- a ``.tbx`` or ``.atbx`` toolbox — Esri's binary ``.tbx`` and the ``.atbx`` archive's JSON content are not published
  (Esri describes ``.atbx`` only as *"a zip archive containing json and other text files"*);
- a geoprocessing service or an ``.ags`` connection (the semicolon-delimited forms) — a fenced tool has no network
  (ADR-223 §5.3);
- GetParameterInfo of one of grapy's own tools — the documented answer is Parameter objects, each with its data type
  and direction, and those are Esri's catalogue of its system tools; grapy's functions carry names and defaults, not
  data types.
"""
from __future__ import annotations

import fnmatch
import importlib
import importlib.machinery
import importlib.util
import inspect
import os
import shlex
import types

from ._runtime import ExecuteError, NotBuiltError

# The toolboxes grapy builds tools of: alias -> the label the tool reference gives the toolbox. INFERRED: these are
# the toolboxes listed, in this order; arcpy.da, arcpy.nax and arcpy.graticula hold classes and functions, not tools.
SYSTEM = {
    "analysis": "Analysis Tools", "cartography": "Cartography Tools", "conversion": "Conversion Tools",
    "management": "Data Management Tools", "edit": "Editing Tools", "ddd": "3D Analyst Tools",
    "sa": "Spatial Analyst Tools", "ia": "Image Analyst Tools", "ga": "Geostatistical Analyst Tools",
    "stats": "Spatial Statistics Tools", "na": "Network Analyst Tools", "nd": "Network Diagram Tools",
    "un": "Utility Network Tools",
}

# Imported toolboxes, by the absolute path of their file: {"alias", "label", "path", "tools": {name: class}}.
_SESSION: dict = {}


def _module(alias):
    return importlib.import_module("grapy." + alias)


def _system_tools(alias):
    """{name: function} of the tools one of grapy's modules builds: its public functions whose names begin with a
    capital, as arcpy's tool names do."""
    module = _module(alias)
    found = {}
    for name in dir(module):
        if name.startswith("_") or not name[:1].isupper():
            continue
        value = getattr(module, name, None)
        if inspect.isfunction(value) and getattr(value, "__module__", "").startswith("grapy"):
            found[name] = value
    return found


def _remote(text):
    return ";" in text or text.lower().startswith(("http://", "https://")) or text.lower().endswith(".ags")


def _resolve(tool_name):
    """("system", alias, name, function) or ("pyt", record, name, class) of a tool named ``<tool>_<alias>``,
    ``<alias>.<tool>`` or ``<tool>``; not case sensitive. INFERRED: a bare name is looked for in imported toolboxes
    first, then in grapy's in SYSTEM's order."""
    text = str(tool_name).strip()
    tool, alias = text, None
    if "." in text:
        alias, tool = text.split(".", 1)
    elif "_" in text:
        head, tail = text.rsplit("_", 1)
        known = {r["alias"].lower() for r in _SESSION.values()} | set(SYSTEM)
        if tail.lower() in known:
            tool, alias = head, tail
    for record in _SESSION.values():
        if alias is None or record["alias"].lower() == alias.lower():
            for name, cls in record["tools"].items():
                if name.lower() == tool.lower():
                    return "pyt", record, name, cls
    for each in SYSTEM:
        if alias is None or each == alias.lower():
            for name, function in _system_tools(each).items():
                if name.lower() == tool.lower():
                    return "system", each, name, function
    raise ValueError(f"'{tool_name}' is not a tool of a toolbox in this session (ListTools names them).")


def _match(names, wild_card):
    if wild_card is None or str(wild_card).strip() in ("", "*"):
        return list(names)
    pattern = str(wild_card).lower()
    return [n for n in names if fnmatch.fnmatchcase(n.lower(), pattern)]


# ---- importing ------------------------------------------------------------------------------------------------------

def _load(path):
    loader = importlib.machinery.SourceFileLoader("grapy_toolbox_" + str(abs(hash(path))), path)
    spec = importlib.util.spec_from_file_location(loader.name, path, loader=loader)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not hasattr(module, "Toolbox"):
        raise ValueError(f"'{path}' has no class Toolbox, which a Python toolbox names its tools in.")
    return module


class _Messages:
    """The ``messages`` an ``execute`` is handed, writing where arcpy's messages go (as grapy's runner does)."""

    def addMessage(self, message):
        from ._runtime import AddMessage
        AddMessage(message)

    def addWarningMessage(self, message):
        from ._runtime import AddWarning
        AddWarning(message)

    def addErrorMessage(self, message):
        from ._runtime import AddError
        AddError(message)

    def addIDMessage(self, message_type, message_ID, add_argument1=None, add_argument2=None):
        from ._runtime import AddIDMessage
        AddIDMessage(message_type, message_ID, add_argument1, add_argument2)

    def addGPMessages(self):
        pass


def _runs(alias, name, cls):
    """The function that runs a Python toolbox's tool: values in parameter order or by name, ``#`` or None for a
    parameter's own default; validation as a tool dialog runs it (updateParameters, updateMessages); then execute. Its
    Result holds the output parameters' values. A parameter message after validation, an error message from execute,
    or an exception fails the tool with ExecuteError."""

    def run(*args, **kwargs):
        from . import _runtime
        from ._result import Result
        tool = cls()
        if hasattr(tool, "isLicensed") and not tool.isLicensed():
            raise ExecuteError(f"ERROR 000824: the tool {name}_{alias} is not licensed.")
        parameters = list(tool.getParameterInfo() or [])
        if len(args) > len(parameters):
            raise TypeError(f"{name}_{alias} takes {len(parameters)} parameters; {len(args)} were given.")
        by_name = {p.name: p for p in parameters}
        for parameter, value in zip(parameters, args):
            if value is not None and str(value) != "#":
                parameter.value = value
        for key, value in kwargs.items():
            if key not in by_name:
                raise TypeError(f"{name}_{alias} has no parameter '{key}'.")
            if value is not None and str(value) != "#":
                by_name[key].value = value
        held = list(_runtime.PARAMETERS)
        _runtime.PARAMETERS[:] = parameters
        errors = sum(1 for m in _runtime.MESSAGES if m["type"] == "esriJobMessageTypeError")
        try:
            if hasattr(tool, "updateParameters"):
                tool.updateParameters(parameters)
            if hasattr(tool, "updateMessages"):
                tool.updateMessages(parameters)
            refused = [f"{p.name}: {p.message}" for p in parameters if getattr(p, "message", "")]
            if refused:
                raise ExecuteError("; ".join(refused))
            try:
                tool.execute(parameters, _Messages())
            except ExecuteError:
                raise
            except Exception as failure:  # noqa: BLE001 — a tool's own failure is the tool failing, as in arcpy
                _runtime.AddError(f"{type(failure).__name__}: {failure}")
                raise ExecuteError(f"{type(failure).__name__}: {failure}") from failure
            if sum(1 for m in _runtime.MESSAGES if m["type"] == "esriJobMessageTypeError") > errors:
                raise ExecuteError(_runtime.GetMessages(2))
        finally:
            _runtime.PARAMETERS[:] = held
        return Result(*[p.valueAsText for p in parameters if p.direction == "Output"])

    run.__name__ = run.__qualname__ = name
    run.__doc__ = getattr(cls, "description", None) or getattr(cls, "label", None) or name
    return run


def ImportToolbox(input_file, module_name=None):
    """A Python toolbox (``.pyt``) imported: its tools become ``arcpy.<tool>_<alias>`` and ``arcpy.<alias>.<tool>``,
    and the module returned lists them in ``__all__``, as documented. The alias is ``module_name`` when given, else the
    toolbox's own; one with neither is an error, as the page says the name is required then. INFERRED: an alias that
    names one of grapy's own modules is an error, since it would hide that module."""
    import grapy
    text = str(input_file).strip()
    if _remote(text):
        raise NotBuiltError("arcpy.ImportToolbox: a geoprocessing service or an ArcGIS Server connection (.ags) is "
                            "reached over the network, and a fenced user tool has none (ADR-223 §5.3).")
    extension = os.path.splitext(text)[1].lower()
    if extension in (".tbx", ".atbx"):
        raise NotBuiltError(f"arcpy.ImportToolbox: a {extension} toolbox is Esri's own format, which is not published "
                            "(the .tbx is binary; the .atbx is a zip whose JSON content has no public specification); "
                            "a Python toolbox (.pyt) is imported.")
    if extension not in (".pyt", ".py"):
        raise ValueError(f"'{text}' is not a toolbox: a Python toolbox is a .pyt file.")
    path = os.path.abspath(text)
    if not os.path.isfile(path):
        raise ValueError(f"'{text}' does not exist.")
    module = _load(path)
    toolbox = module.Toolbox()
    alias = str(module_name).strip() if module_name not in (None, "", "#") else str(getattr(toolbox, "alias", "") or "")
    if not alias:
        raise ValueError(f"'{os.path.basename(path)}' has no alias; give module_name, which sets one for the session.")
    if not alias.isidentifier():
        raise ValueError(f"'{alias}' is not an alias: it is a Python name, letters, digits and underscores.")
    if alias.lower() in SYSTEM or alias in ("da", "nax", "graticula", "runner", "fence", "env"):
        raise ValueError(f"'{alias}' is the alias of one of grapy's own modules; give module_name another.")
    classes = {cls.__name__: cls for cls in getattr(toolbox, "tools", [])}
    imported = types.ModuleType(alias, getattr(toolbox, "description", "") or None)
    imported.__all__ = sorted(classes)
    imported.__file__ = path
    for name, cls in classes.items():
        function = _runs(alias, name, cls)
        setattr(imported, name, function)
        setattr(grapy, f"{name}_{alias}", function)
    setattr(grapy, alias, imported)
    label = str(getattr(toolbox, "label", "") or os.path.splitext(os.path.basename(path))[0])
    _SESSION[os.path.normcase(path)] = {"alias": alias, "label": label, "path": path, "tools": classes}
    return imported


def AddToolbox(input_file, module_name=None):
    """The same as ImportToolbox, as its page says."""
    return ImportToolbox(input_file, module_name)


def RemoveToolbox(toolbox):
    """An imported toolbox, named by its path or its alias, removed from the session: ListToolboxes and ListTools no
    longer name it. As documented, its tools are removed *"only from the underlying geoprocessor object"* — the
    ``arcpy.<alias>`` names ImportToolbox made stay. INFERRED: a toolbox not in the session is an error, and grapy's
    own toolboxes cannot be removed."""
    text = str(toolbox).strip()
    if _remote(text):
        raise NotBuiltError("arcpy.RemoveToolbox: a server toolbox is reached over the network, and a fenced user tool "
                            "has none (ADR-223 §5.3).")
    key = os.path.normcase(os.path.abspath(text))
    if key not in _SESSION:
        key = next((k for k, r in _SESSION.items() if r["alias"].lower() == text.lower()), None)
    if key is None:
        raise ValueError(f"'{text}' is not a toolbox imported in this session.")
    del _SESSION[key]


# ---- listing --------------------------------------------------------------------------------------------------------

def ListToolboxes(wild_card=None):
    """The toolboxes of the session, each as ``"<toolbox name>(<alias>)"`` — the form the page *List tools, toolboxes
    and environment settings* gives — grapy's own and then those imported, filtered by a case-insensitive wildcard."""
    names = [f"{label}({alias})" for alias, label in SYSTEM.items()]
    names += [f"{r['label']}({r['alias']})" for r in _SESSION.values()]
    return _match(names, wild_card)


def ListTools(wild_card=None):
    """The tools of the session's toolboxes, each as ``<tool>_<alias>``, filtered by a case-insensitive wildcard
    (``"*_analysis"``, as the page's example)."""
    names = []
    for alias in SYSTEM:
        names += sorted(f"{name}_{alias}" for name in _system_tools(alias))
    for record in _SESSION.values():
        names += sorted(f"{name}_{record['alias']}" for name in record["tools"])
    return _match(names, wild_card)


def _syntax_of(kind, alias, name, target):
    """The tool's parameters as its syntax lists them: optional ones in braces, as the tool reference writes them."""
    if kind == "pyt":
        parameters = [p for p in (target().getParameterInfo() or []) if p.parameterType != "Derived"]
        return [("{" + p.name + "}") if p.parameterType == "Optional" else p.name for p in parameters]
    names = []
    for parameter in inspect.signature(target).parameters.values():
        if parameter.kind in (parameter.VAR_POSITIONAL, parameter.VAR_KEYWORD) or parameter.name.startswith("_"):
            continue
        names.append(("{" + parameter.name + "}") if parameter.default is not parameter.empty else parameter.name)
    return names


def Usage(tool_name):
    """The tool's syntax, ``<tool>_<alias>(required, {optional}, …)``. INFERRED: the form is the tool reference's
    syntax notation, which the page does not show; for grapy's own tools the parameters are those its function names,
    which can be fewer than arcpy's where grapy accepts a trailing parameter without naming it."""
    kind, where, name, target = _resolve(tool_name)
    alias = where["alias"] if kind == "pyt" else where
    return f"{name}_{alias}({', '.join(_syntax_of(kind, alias, name, target))})"


# ---- parameters -----------------------------------------------------------------------------------------------------

def GetParameterInfo(tool_name=None):
    """The tool's Parameter objects. With no name — in a script tool's or a Python toolbox's validation, or its
    execute — the parameters of the tool that is running, as the page allows; with a Python toolbox's tool, a fresh
    list from its ``getParameterInfo``. One of grapy's own tools is refused: see the module's note."""
    from . import _runtime
    if tool_name in (None, "", "#"):
        return list(_runtime.PARAMETERS)
    kind, where, name, target = _resolve(tool_name)
    if kind == "pyt":
        return list(target().getParameterInfo() or [])
    raise NotBuiltError(f"arcpy.GetParameterInfo('{tool_name}'): the Parameter objects of a system tool carry each "
                        "parameter's data type and direction, which are Esri's catalogue of its tools; grapy's "
                        f"{where}.{name} carries its parameters' names and defaults (arcpy.Usage, arcpy."
                        "GetParameterValue).")


def GetParameterValue(tool_name, index):
    """The default value of a tool's parameter, as a string; ``index`` its position or its name. A Python toolbox's
    is the value its ``getParameterInfo`` sets; one of grapy's own tools' is its function's default. INFERRED: a
    parameter with no default answers an empty string."""
    kind, where, name, target = _resolve(tool_name)
    if kind == "pyt":
        parameters = list(target().getParameterInfo() or [])
        values = [(p.name, p.value) for p in parameters]
    else:
        values = [(p.name, None if p.default is p.empty else p.default)
                  for p in inspect.signature(target).parameters.values()
                  if p.kind not in (p.VAR_POSITIONAL, p.VAR_KEYWORD) and not p.name.startswith("_")]
    if isinstance(index, str) and not index.lstrip("-").isdigit():
        found = [v for n, v in values if n.lower() == index.lower()]
        if not found:
            raise ValueError(f"{name} has no parameter '{index}'.")
        value = found[0]
    else:
        value = values[int(index)][1]
    if value is None:
        return ""
    if isinstance(value, (list, tuple)):
        return ";".join(str(v) for v in value)
    return str(value)


# ---- the command line -----------------------------------------------------------------------------------------------

def Command(command_line):
    """A tool run from one string in the legacy Command Line syntax — the tool's name, then its parameters separated
    by spaces, a parameter with a space in it quoted, ``#`` for a default — answering its messages separated by
    newlines, or only its error messages when it fails, as documented. INFERRED: a failure is answered, not raised —
    the page says the string holds the error messages."""
    from . import _runtime
    tokens = [t[1:-1] if len(t) >= 2 and t[0] == t[-1] and t[0] in "\"'" else t
              for t in shlex.split(str(command_line), posix=False)]
    if not tokens:
        raise ValueError("The command line names no tool.")
    kind, where, name, target = _resolve(tokens[0])
    function = (lambda *a: _runs(where["alias"], name, target)(*a)) if kind == "pyt" else target
    arguments = [None if t == "#" else t for t in tokens[1:]]
    start = len(_runtime.MESSAGES)
    try:
        function(*arguments)
    except Exception as failure:  # noqa: BLE001 — the page answers a failure as its error messages
        said = [m["description"] for m in _runtime.MESSAGES[start:] if m["type"] == "esriJobMessageTypeError"]
        text = f"{type(failure).__name__}: {failure}"
        if not any(text.endswith(s) or s.endswith(str(failure)) for s in said):
            said.append(text)
        return "\n".join(said)
    return "\n".join(m["description"] for m in _runtime.MESSAGES[start:])
