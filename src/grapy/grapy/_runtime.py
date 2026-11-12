"""What a running tool shares with grapy: its messages, its parameters and its environment — ADR-223 §5.2.

Messages go to a list the runner writes out as the job's messages, and to the tool that said them, whose messages
GetMessages answers once it ends (ADR-289 §5.3, below); ``AddError`` also raises nothing by itself, as in arcpy, where a
tool says its error and then raises ``ExecuteError`` or returns.
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


def wildcard(name, wild_card):
    """Whether ``name`` matches a List function's ``wild_card``, without regard to case. Only ``*`` is a wildcard:
    ArcGIS Pro 3.7 finds nothing for "[dz]*" or "de?.tif" where "d*" finds dem.tif (measured 2026-11-08), so brackets
    and question marks are ordinary characters."""
    import re
    if wild_card in (None, "", "*"):
        return True
    pattern = ".*".join(re.escape(part) for part in str(wild_card).split("*"))
    return re.fullmatch(pattern, str(name), re.IGNORECASE | re.DOTALL) is not None


# ---- messages: the run's list, and each tool's own (ADR-223 §5.2, ADR-289 §5.3)
#
# grapy keeps two records. ``MESSAGES`` is the run's: everything AddMessage, AddWarning, AddError, AddIDMessage and
# AddReturnMessage say while the run lasts, which the runner writes out as the server job's messages (ADR-223) and which
# ADR-289 leaves as it was. The other is arcpy's own model, measured in ArcGIS Pro 3.7 (2026-11-12): a tool run from a
# script keeps its own messages — "Start Time: …", what it said, and "Succeeded at … (Elapsed Time: …)", or when it
# fails its errors, "Failed to execute (<tool>)." and "Failed at …" — and GetMessages and its family answer the
# messages of the last tool that ended, never the script's own. AddMessage outside a tool prints its text and joins
# nothing. Inside a script tool they answer the last tool *it* ran, and nothing before it has run one; the tools it runs
# do not add to its messages (AddReturnMessage copies one across). Each tool runs through one wrapper, ``tool``.

_PRO_KIND = {"esriJobMessageTypeInformative": 0, "esriJobMessageTypeWarning": 50, "esriJobMessageTypeError": 100}
_JOB_TYPE = {50: "esriJobMessageTypeWarning", 100: "esriJobMessageTypeError"}  # any other kind is informative
_GENERIC = -2147467259   # the return code of an error that has no ID, as GetAllMessages documents and Pro answers


class _Frame:
    """One tool run: the messages it has said, and the messages of the last tool it ran — what GetMessages answers
    while it runs. The script itself is the frame at the bottom of the stack, with no messages of its own."""

    __slots__ = ("name", "messages", "last")

    def __init__(self, name=None):
        self.name, self.messages, self.last = name, [], []


_STACK = [_Frame()]


def _from_grapy(depth):
    """Whether the code ``depth`` calls above the caller is grapy's own — a tool calling another, or a tool saying its
    message — rather than a script's or a toolbox's. The runner's and _toolboxes' ``messages`` objects speak for the
    toolbox that was handed them."""
    import sys
    name = sys._getframe(depth + 1).f_globals.get("__name__", "")
    return name.startswith("grapy.") and name not in ("grapy.runner", "grapy._toolboxes")


def _code(text, kind, said_by_grapy):
    """A message's return code. ArcGIS Pro 3.7 (measured 2026-11-12) answers 1 for a script's AddError and 0 for its
    AddMessage and AddWarning; a tool's own message carries its ID — 732 for "ERROR 000732: …" — and an error with
    none answers -2147467259. grapy's tools write Esri's IDs into their texts, so the ID is read back from there."""
    if not said_by_grapy:
        return 1 if kind == 100 else 0
    import re
    found = re.match(r"(?:ERROR|WARNING) (\d{6}):", text)
    if found:
        return int(found.group(1))
    return _GENERIC if kind == 100 else 0


def _print(kind, text):
    """What arcpy prints for a message a script says — in ArcGIS Pro 3.7, outside a tool and inside a script tool run
    from a script alike, with ``WARNING: `` or ``ERROR: `` before a warning or an error (measured 2026-11-12)."""
    import sys
    prefix = {50: "WARNING: ", 100: "ERROR: "}.get(kind, "")
    try:
        sys.stdout.write(f"{prefix}{text}\n")
    except (AttributeError, OSError, ValueError):  # no stdout, or a closed one: the message is still kept
        pass


def _say(kind: str, text, said_by_grapy=False, code=None) -> None:
    text = str(text)
    MESSAGES.append({"type": kind, "description": text})
    pro = _PRO_KIND[kind]
    if len(_STACK) > 1:
        _STACK[-1].messages.append({"kind": pro, "code": _code(text, pro, said_by_grapy) if code is None else code,
                                    "text": text})
    if not said_by_grapy:
        _print(pro, text)


def AddMessage(message):
    _say("esriJobMessageTypeInformative", message, _from_grapy(1))


def AddWarning(message):
    _say("esriJobMessageTypeWarning", message, _from_grapy(1))


def AddError(message):
    _say("esriJobMessageTypeError", message, _from_grapy(1))


def AddIDMessage(message_type, message_ID, add_argument1=None, add_argument2=None):
    """A message by its ID. Its return code is the ID, as ArcGIS Pro 3.7's (measured 2026-11-12); its text is the ID
    and the arguments, because Esri's message catalogue — Pro's text is "WARNING 000012: x already exists" — is not
    published as data."""
    word = str(message_type).upper()
    kind = {"ERROR": "esriJobMessageTypeError", "WARNING": "esriJobMessageTypeWarning"}.get(
        word, "esriJobMessageTypeInformative")
    _say(kind, " ".join(str(x) for x in (message_ID, add_argument1, add_argument2) if x is not None),
         _from_grapy(1), code=int(message_ID))


def _view():
    """The messages GetMessages and its family read: the last tool's that ended where the caller is."""
    return _STACK[-1].last


def GetMessages(severity=0):
    """The last tool's messages: with 0, all of them, one to a line; with 1 only its warnings and with 2 only its
    errors, each followed by a newline — ArcGIS Pro 3.7 answers "a warning\\n" for 1 and "" for 1 when the tool wrote
    errors and no warnings (measured 2026-11-12)."""
    view = _view()
    level = int(severity)
    if level == 0:
        return "\n".join(m["text"] for m in view)
    wanted = {1: 50, 2: 100}[level]
    return "".join(m["text"] + "\n" for m in view if m["kind"] == wanted)


def GetMessageCount():
    return len(_view())


def SetProgressor(*_, **__):
    """Progress is not drawn on a server; accepted so a tool written for Pro runs unchanged."""


def SetProgressorLabel(*_):
    pass


def SetProgressorPosition(*_):
    pass


def ResetProgressor():
    pass


# Outside a tool run — a script run from a command line — the parameters are the script's arguments, as ArcGIS Pro 3.7
# reads them: GetParameterAsText(0) is sys.argv[1], GetArgumentCount the number of arguments, and SetParameter and
# SetParameterAsText do nothing (measured 2026-11-08).


def _arguments():
    import sys
    return [str(a) for a in sys.argv[1:]]


def GetParameterAsText(index):
    if not PARAMETERS:
        arguments = _arguments()
        return arguments[index] if index < len(arguments) else ""
    value = PARAMETERS[index].valueAsText if index < len(PARAMETERS) else ""
    return value or ""


def GetParameter(index):
    if not PARAMETERS:
        arguments = _arguments()
        return arguments[index] if index < len(arguments) else None
    return PARAMETERS[index].value if index < len(PARAMETERS) else None


def GetParameterCount(tool_name=None):
    """The number of parameters of the tool named — a Python toolbox's tool's, or grapy's own tool's function's —
    or, with no name, of the tool running. ArcGIS Pro 3.7 answers 8 for Buffer_analysis and 4 for Clip (measured
    2026-11-08). INFERRED: one of grapy's own tools counts its function's named parameters, which follow arcpy's."""
    if tool_name not in (None, "", "#"):
        from ._toolboxes import _resolve, _syntax_of
        kind, where, name, target = _resolve(tool_name)
        if kind == "pyt":
            return len(list(target().getParameterInfo() or []))
        return len(_syntax_of(kind, where, name, target))
    return len(PARAMETERS)


def SetParameter(index, value):
    if not PARAMETERS:
        return
    PARAMETERS[index].value = value


def SetParameterAsText(index, text):
    if not PARAMETERS:
        return
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
    return [n for n in env.keys() if wildcard(n, wild_card)]


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


# ---- the last tool's messages, read back, and the wrapper every tool runs through

_SEVERITY = {50: 1, 100: 2}
_SEVERITY_LEVEL = [2]


def _message(index, what):
    """The last tool's message at ``index``, or None when the last tool has none. ArcGIS Pro 3.7 answers "" or 0 for
    any index before a tool has run, and raises RuntimeError("Object: Error in getting <what>") for an index past the
    last message or below 0 once one has (measured 2026-11-12)."""
    view = _view()
    index = int(index)
    if not view:
        return None
    if not 0 <= index < len(view):
        raise RuntimeError(f"Object: Error in getting {what}")
    return view[index]


def GetMessage(index):
    """The text of the last tool's message at ``index`` (see ``_message`` for an index it does not hold). Pro answers
    every message, one to a line, for -1 (measured 2026-11-12), and grapy does too."""
    if int(index) == -1 and _view():
        return GetMessages(0)
    message = _message(index, "message")
    return message["text"] if message else ""


def GetSeverity(index):
    """0 for a message, 1 for a warning, 2 for an error."""
    message = _message(index, "severity")
    return _SEVERITY.get(message["kind"], 0) if message else 0


def GetMaxSeverity():
    return max((_SEVERITY.get(m["kind"], 0) for m in _view()), default=0)


def GetReturnCode(index):
    """The message's return code: 0 for a message or warning a script said and 1 for its error; a tool's own error its
    ID, or -2147467259 when it has none (measured in ArcGIS Pro 3.7, 2026-11-12; see ``_code``)."""
    message = _message(index, "return code")
    return message["code"] if message else 0


def GetAllMessages():
    """``[type, return code, text]`` for each of the last tool's messages: type 0 informative, 2 its start, 3 its end,
    50 a warning and 100 an error, as documented and as ArcGIS Pro 3.7 answers (measured 2026-11-12)."""
    return [[m["kind"], m["code"], m["text"]] for m in _view()]


def AddReturnMessage(index):
    """The last tool's message at ``index`` said again as the script's own, with its type and return code — Pro keeps
    the end line's type 3 (measured 2026-11-12). An index it does not hold raises RuntimeError, as Pro's does."""
    view, index = _view(), int(index)
    if not 0 <= index < len(view):
        raise RuntimeError("Object: Error in adding return message")
    message = view[index]
    MESSAGES.append({"type": _JOB_TYPE.get(message["kind"], "esriJobMessageTypeInformative"),
                     "description": message["text"]})
    if len(_STACK) > 1:
        _STACK[-1].messages.append(dict(message))
    _print(message["kind"], message["text"])


def SetSeverityLevel(severity_level):
    """How a tool ends when it warns or fails, which every tool consults through ``tool``: 2, the default, raises
    ExecuteError when a tool fails; 1 also raises ExecuteWarning, after the tool, when it warned; 0 raises neither and
    answers a Result with no outputs (ArcGIS Pro 3.7, measured 2026-11-12). A refusal grapy says (NotBuiltError) is
    raised at every level: it is not a tool failing. INFERRED: a level other than 0, 1 or 2 is a ValueError."""
    level = int(severity_level)
    if level not in (0, 1, 2):
        raise ValueError(f"arcpy.SetSeverityLevel: severity_level is 0, 1 or 2, not {severity_level}.")
    _SEVERITY_LEVEL[0] = level


def GetSeverityLevel():
    return _SEVERITY_LEVEL[0]


_DAYS = ("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")
_MONTHS = ("January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
           "November", "December")


def _when(seconds):
    """A time as a tool's start and end lines write it: "Thursday, November 12, 2026 9:26:54 AM" in ArcGIS Pro 3.7
    (measured 2026-11-12), the local time, in English whatever the locale."""
    import time
    t = time.localtime(seconds)
    return (f"{_DAYS[t.tm_wday]}, {_MONTHS[t.tm_mon - 1]} {t.tm_mday}, {t.tm_year} "
            f"{t.tm_hour % 12 or 12}:{t.tm_min:02d}:{t.tm_sec:02d} {'AM' if t.tm_hour < 12 else 'PM'}")


def _elapsed(seconds):
    """A run's length as its end line writes it. ArcGIS Pro 3.7 writes "0.05 seconds" and "12.50 seconds" under a
    minute, and whole minutes and seconds from one: "1 minutes 1 seconds" for 61.5 s, "2 minutes 5 seconds" for 125.2 s
    (measured 2026-11-12). INFERRED: from an hour, "<h> hours <m> minutes <s> seconds" — not measured."""
    if seconds < 60:
        return f"{seconds:.2f} seconds"
    whole = int(seconds)
    hours, rest = divmod(whole, 3600)
    minutes, secs = divmod(rest, 60)
    return (f"{hours} hours " if hours else "") + f"{minutes} minutes {secs} seconds"


def _inputs(function, args, kwargs):
    """A tool's input values as text, for Result.inputCount and getInput: its parameters but the ``out_`` ones, its
    outputs by arcpy's naming. ArcGIS Pro 3.7 answers 1 for GetCount and 7 for Buffer, whose eighth parameter is its
    output (measured 2026-11-12). INFERRED: a parameter not given is its default as text, "" for None."""
    import inspect
    try:
        signature = inspect.signature(function)
        bound = signature.bind_partial(*args, **kwargs)
    except (TypeError, ValueError):
        return [str(a) for a in args]
    bound.apply_defaults()
    found = []
    for name, parameter in signature.parameters.items():
        if parameter.kind in (parameter.VAR_POSITIONAL, parameter.VAR_KEYWORD) or name.startswith("out_"):
            continue
        value = bound.arguments.get(name)
        found.append("" if value is None else str(value))
    return found


def run_tool(name, function, args=(), kwargs=None):
    """Runs ``function`` as arcpy runs a tool: its own messages, with the start and end lines; the last tool's
    messages of whoever called it set to them when it ends; and the severity level consulted (SetSeverityLevel)."""
    import time
    from ._result import Result
    kwargs = kwargs or {}
    frame = _Frame(name)
    parent = _STACK[-1]
    started = time.time()
    frame.messages.append({"kind": 2, "code": 0, "text": f"Start Time: {_when(started)}"})
    _STACK.append(frame)
    try:
        answer = function(*args, **kwargs)
    except Exception as failure:
        _STACK.pop()
        said = [m["text"] for m in frame.messages if m["kind"] == 100]
        if not isinstance(failure, NotBuiltError):
            text = str(failure) if isinstance(failure, ExecuteError) else f"{type(failure).__name__}: {failure}"
            for line in text.splitlines():
                if line.strip() and line not in said:
                    frame.messages.append({"kind": 100, "code": _code(line, 100, True), "text": line})
        frame.messages.append({"kind": 100, "code": _GENERIC, "text": f"Failed to execute ({name})."})
        ended = time.time()
        frame.messages.append({"kind": 3, "code": 0,
                               "text": f"Failed at {_when(ended)} (Elapsed Time: {_elapsed(ended - started)})"})
        parent.last = frame.messages
        if isinstance(failure, ExecuteError):
            # Pro's ExecuteError says the tool's errors, each with a newline after it (measured 2026-11-12).
            failure.args = ("".join(m["text"] + "\n" for m in frame.messages if m["kind"] == 100),)
        if _SEVERITY_LEVEL[0] == 0 and not isinstance(failure, NotBuiltError):
            # Level 0: no exception, a Result with no outputs. Pro answers status 5 (failed) when the tool's parameters
            # were refused and 4 when a script tool's execute said an error (measured 2026-11-12); grapy's own tools
            # check as they run, so a failure of theirs is 5.
            result = Result()
            result.status = 4 if getattr(failure, "_grapy_in_execute", False) else 5
            result._messages = frame.messages
            return result
        raise
    except BaseException:
        _STACK.pop()
        raise
    _STACK.pop()
    ended = time.time()
    frame.messages.append({"kind": 3, "code": 0,
                           "text": f"Succeeded at {_when(ended)} (Elapsed Time: {_elapsed(ended - started)})"})
    parent.last = frame.messages
    if isinstance(answer, Result):
        answer._messages = frame.messages
        answer._inputs = _inputs(function, args, kwargs)
    if _SEVERITY_LEVEL[0] == 1:
        warnings = [m["text"] for m in frame.messages if m["kind"] == 50]
        if warnings:
            raise ExecuteWarning("".join(w + "\n" for w in warnings))
    return answer


_WRAPPED: dict = {}


def tool(function, name=None, always=False):
    """``function`` as a tool: called from a script or a toolbox it runs through ``run_tool``; called by grapy's own
    code — one tool using another — it runs as it is, inside the tool that called it, so its messages are that tool's.
    ``always`` runs it as a tool whoever calls it: a Python toolbox's tool, which grapy runs on a script's behalf."""
    import functools
    if getattr(function, "__grapy_tool__", False):
        return function
    if id(function) in _WRAPPED and not always and name is None:
        return _WRAPPED[id(function)]
    title = name or function.__name__

    @functools.wraps(function)
    def run(*args, **kwargs):
        if not always and _from_grapy(1):
            return function(*args, **kwargs)
        return run_tool(title, function, args, kwargs)

    run.__grapy_tool__ = True
    if not always and name is None:
        _WRAPPED[id(function)] = run
    return run


def GetArgumentCount():
    """The number of parameters passed to the tool; outside a tool run, the script's arguments."""
    return len(PARAMETERS) if PARAMETERS else len(_arguments())


class ExecuteError(Exception):
    """What arcpy raises when a tool fails, and what a tool raises to fail."""


class ExecuteWarning(RuntimeError):
    """What a tool raises at severity level 1 when it warned. A RuntimeError, as ArcGIS Pro 3.7's (measured 2026-11-12);
    grapy's was a Warning before it was ever raised."""


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
        # ADR-224 §5.6: an equivalent's docstring begins EQUIVALENT: and names its method and source.
        doc = (tool.__doc__ or "").lstrip()
        if not doc.upper().startswith("EQUIVALENT"):
            head = f"EQUIVALENT: {method} ({source})."
            run.__doc__ = head + "\n\n" + doc if doc else head
        return run

    return mark
