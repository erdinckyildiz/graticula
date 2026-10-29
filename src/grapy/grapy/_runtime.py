"""What a running tool shares with grapy: its messages, its parameters and its environment — ADR-223 §5.2.

Messages go to a list the runner writes out as the job's messages; ``AddError`` also raises nothing by itself, as in
arcpy, where a tool says its error and then raises ``ExecuteError`` or returns.
"""
from __future__ import annotations

import os

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
    """arcpy.env: the settings a tool reads. The workspace is the run's directory; nothing else is reachable."""

    def __init__(self):
        self.workspace = os.getcwd()
        self.scratchWorkspace = os.getcwd()
        self.overwriteOutput = True
        self.outputCoordinateSystem = None
        self.extent = None
        self.cellSize = None
        self.mask = None
        self.snapRaster = None
        self.addOutputsToMap = False

    @property
    def scratchFolder(self):
        return self.scratchWorkspace

    @property
    def scratchGDB(self):
        return os.path.join(self.scratchWorkspace, "scratch.gpkg")


env = _Environment()


class ExecuteError(Exception):
    """What arcpy raises when a tool fails, and what a tool raises to fail."""


class ExecuteWarning(Warning):
    pass


class NotBuiltError(NotImplementedError, AttributeError):
    """A name arcpy documents that grapy has not built — an AttributeError too, so ``hasattr`` answers False."""
