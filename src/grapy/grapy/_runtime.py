"""What a running tool shares with grapy: its messages, its parameters and its environment — ADR-223 §5.2.

Messages go to a list the runner writes out as the job's messages; ``AddError`` also raises nothing by itself, as in
arcpy, where a tool says its error and then raises ``ExecuteError`` or returns.
"""
from __future__ import annotations

import os

def _is_given(value):
    """Whether an optional parameter was given: not None, and not an empty or ``#`` string. Tested by type and
    identity, never with ``in (None, \"\", \"#\")``: that compares with ``==``, which on a Raster is map algebra
    and answers a (truthy) Raster, so a Raster passed as an optional argument read as not given."""
    return value is not None and not (isinstance(value, str) and value.strip() in ("", "#"))


def _is_set(value, *unset):
    """Whether an environment setting or keyword parameter holds something other than None, an empty or ``#`` string,
    or one of the ``unset`` keywords (``MAXOF``, ``MINOF`` …), compared without case. By type, as ``_is_given``: a
    Raster in ``env.mask``, ``env.snapRaster`` or ``env.cellSize`` is set."""
    if not _is_given(value):
        return False
    return not (isinstance(value, str) and value.strip().upper() in {w.upper() for w in unset})


MESSAGES: list[dict] = []
PARAMETERS: list = []


def _say(kind: str, text) -> None:
    MESSAGES.append({"type": kind, "description": str(text)})


def AddMessage(message):
    _say("esriJobMessageTypeInformative", message)


def AddWarning(message):
    _say("esriJobMessageTypeWarning", message)


def AddError(message):
    _say("esriJobMessageTypeError", message)


def AddIDMessage(message_type, message_ID, add_argument1=None, add_argument2=None):
    kind = {"ERROR": "esriJobMessageTypeError", "WARNING": "esriJobMessageTypeWarning"}.get(
        str(message_type).upper(), "esriJobMessageTypeInformative")
    _say(kind, " ".join(str(x) for x in (message_ID, add_argument1, add_argument2) if x is not None))


def GetMessages(severity=0):
    wanted = {0: None, 1: ("esriJobMessageTypeWarning", "esriJobMessageTypeError"), 2: ("esriJobMessageTypeError",)}[severity]
    return "\n".join(m["description"] for m in MESSAGES if wanted is None or m["type"] in wanted)


def GetMessageCount():
    return len(MESSAGES)


def SetProgressor(*_, **__):
    """Progress is not drawn on a server; accepted so a tool written for Pro runs unchanged."""


def SetProgressorLabel(*_):
    pass


def SetProgressorPosition(*_):
    pass


def ResetProgressor():
    pass


def GetParameterAsText(index):
    value = PARAMETERS[index].valueAsText if index < len(PARAMETERS) else ""
    return value or ""


def GetParameter(index):
    return PARAMETERS[index].value if index < len(PARAMETERS) else None


def GetParameterCount(tool_name=None):
    return len(PARAMETERS)


def SetParameter(index, value):
    PARAMETERS[index].value = value


def SetParameterAsText(index, text):
    PARAMETERS[index].value = text


class _Environment:
    """arcpy.env: the settings a tool reads. The workspace is the run's directory; nothing else is reachable.

    ``env["workspace"]`` reads and writes the same settings as ``env.workspace``, as arcpy's env does.
    INFERRED: a setting's default — what ClearEnvironment and ResetEnvironments return it to — is grapy's, not
    arcpy's: the workspace and scratch workspace default to the run's directory, where arcpy's default to none, because
    a user tool on this server has no other place to write (ADR-223 §5.5).
    """

    _NAMES = ("workspace", "scratchWorkspace", "overwriteOutput", "outputCoordinateSystem", "extent", "cellSize", "mask",
              "snapRaster", "addOutputsToMap", "randomGenerator")

    def __init__(self):
        self._reset()

    @staticmethod
    def _default(name):
        return {"workspace": os.getcwd(), "scratchWorkspace": os.getcwd(), "overwriteOutput": True,
                "addOutputsToMap": False}.get(name)

    def _reset(self, name=None):
        for each in ([name] if name else self._NAMES):
            setattr(self, each, self._default(each))

    def _name(self, name):
        for each in (*self._NAMES, "scratchFolder", "scratchGDB"):
            if each.lower() == str(name).lower():
                return each
        raise AttributeError(f"'{name}' is not a geoprocessing environment grapy holds.")

    def __getitem__(self, name):
        return getattr(self, self._name(name))

    def __setitem__(self, name, value):
        setattr(self, self._name(name), value)

    def keys(self):
        return list(self._NAMES) + ["scratchFolder", "scratchGDB"]

    @property
    def scratchFolder(self):
        return self.scratchWorkspace

    @property
    def scratchGDB(self):
        return os.path.join(self.scratchWorkspace, "scratch.gpkg")


env = _Environment()


def ListEnvironments(wild_card=None):
    """The names of the environment settings grapy holds, filtered by a case-insensitive wildcard."""
    import fnmatch
    return [n for n in env.keys() if wild_card in (None, "", "*") or fnmatch.fnmatch(n.lower(), str(wild_card).lower())]


def ClearEnvironment(environment_name):
    """One environment setting back to its default (see ``_Environment`` for grapy's defaults)."""
    name = env._name(environment_name)
    if name in ("scratchFolder", "scratchGDB"):
        raise ValueError(f"'{name}' is read-only; it follows scratchWorkspace.")
    env._reset(name)


def ResetEnvironments():
    """Every environment setting back to its default."""
    env._reset()


def GetSystemEnvironment(environment):
    """A system environment variable's value; for TEMP, the first of TEMP, TMP and MW_TMPDIR that is set, as
    documented. INFERRED: an unset variable answers None. The fence gives a user tool a clean environment
    (ADR-223 §5.3), so the server's own variables are never seen."""
    names = ("TEMP", "TMP", "MW_TMPDIR") if str(environment).upper() == "TEMP" else (str(environment),)
    for name in names:
        if os.environ.get(name) is not None:
            return os.environ[name]
    return None


class EnvManager:
    """Environment settings set for a ``with`` block or a decorated function, and put back afterwards."""

    def __init__(self, **kwargs):
        self._wanted = {env._name(k): v for k, v in kwargs.items()}
        self._before = {}

    def __enter__(self):
        self._before = {k: getattr(env, k) for k in self._wanted}
        for k, v in self._wanted.items():
            setattr(env, k, v)
        return self

    def __exit__(self, *_):
        self.reset()
        return False

    def reset(self):
        for k, v in self._before.items():
            setattr(env, k, v)

    def __call__(self, function):
        import functools

        @functools.wraps(function)
        def wrapped(*args, **kwargs):
            with EnvManager(**self._wanted):
                return function(*args, **kwargs)
        return wrapped


# ---- the message stack, read back

_SEVERITY = {"esriJobMessageTypeInformative": 0, "esriJobMessageTypeWarning": 1, "esriJobMessageTypeError": 2}
_SEVERITY_LEVEL = [2]


def _message(index):
    return MESSAGES[int(index)]


def GetMessage(index):
    return _message(index)["description"]


def GetSeverity(index):
    """0 for a message, 1 for a warning, 2 for an error."""
    return _SEVERITY.get(_message(index)["type"], 0)


def GetMaxSeverity():
    return max((_SEVERITY.get(m["type"], 0) for m in MESSAGES), default=0)


def GetReturnCode(index):
    """0 for a message or warning. INFERRED: an error answers -2147467259, the code GetAllMessages documents for an
    error that has no ID — grapy's messages carry none."""
    return -2147467259 if GetSeverity(index) == 2 else 0


def GetAllMessages():
    """``[type, return code, text]`` for each message: type 0 informative, 50 warning, 100 error, as documented."""
    kinds = {0: 0, 1: 50, 2: 100}
    return [[kinds[GetSeverity(i)], GetReturnCode(i), GetMessage(i)] for i in range(len(MESSAGES))]


def AddReturnMessage(index):
    """The message at ``index`` added again as the script's own, its severity kept."""
    message = _message(index)
    _say(message["type"], message["description"])


def SetSeverityLevel(severity_level):
    """Only 2, the default — a tool raises on an error — is built. Levels 0 (never raise) and 1 (raise on a warning)
    would change how every tool ends, which grapy's tools do not yet consult; they are refused."""
    level = int(severity_level)
    if level != 2:
        raise NotBuiltError(f"arcpy.SetSeverityLevel: severity_level {level} is not built in grapy; only 2, the default, is.")
    _SEVERITY_LEVEL[0] = level


def GetSeverityLevel():
    return _SEVERITY_LEVEL[0]


def GetArgumentCount():
    """The number of parameters passed to the tool."""
    return len(PARAMETERS)


class ExecuteError(Exception):
    """What arcpy raises when a tool fails, and what a tool raises to fail."""


class ExecuteWarning(Warning):
    pass


class NotBuiltError(NotImplementedError, AttributeError):
    """A name arcpy documents that grapy has not built — an AttributeError too, so ``hasattr`` answers False."""


_SAID_EQUIVALENT: set = set()


def equivalent(method, source):
    """Marks a tool built as an equivalent where Esri's documentation does not define the answer (ADR-224 §5.6): it is
    counted apart by tools/grapy_refusals.py, and its run warns once that its numbers may differ from ArcGIS Pro's."""
    import functools

    def mark(tool):
        @functools.wraps(tool)
        def run(*args, **kwargs):
            if tool.__qualname__ not in _SAID_EQUIVALENT:
                _SAID_EQUIVALENT.add(tool.__qualname__)
                AddWarning(f"{tool.__name__} is an equivalent, not ArcGIS Pro's own computation: {method} ({source}). "
                           "Its numbers may differ from Pro's.")
            return tool(*args, **kwargs)

        run.__grapy_equivalent__ = f"{method} ({source})"
        return run

    return mark
