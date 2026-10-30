"""The errors an Arcade expression raises in grapy (ADR-235)."""
from __future__ import annotations


class ArcadeError(Exception):
    """A compilation or execution error of an Arcade expression, with the line it was raised on when known."""

    def __init__(self, message, line=None):
        self.message, self.line = message, line
        super().__init__(f"{message} (line {line})" if line else message)


class NotBuiltFunction(ArcadeError):
    """A documented Arcade function grapy does not build: the message names it and says why."""
