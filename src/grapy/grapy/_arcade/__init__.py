"""An Arcade interpreter for grapy's attribute rules — ADR-235.

Written clean-room from Esri's public Arcade documentation (developers.arcgis.com/arcade: the language guide, its
structure, operators, types and type casting pages, and the function reference), core first, as the owner asked on
2026-10-30. Esri's implementation was not read. Where the documentation leaves an answer open, the choice is labelled
INFERRED in the module that makes it and listed in ADR-235 §5.8.

    from grapy._arcade import evaluate
    evaluate("var x = 2; x * 21")                          # 42.0
    evaluate("$feature.name + '!'", {"$feature": feature})

``evaluate`` compiles the script (cached) and runs it with the profile variables given; ``ArcadeError`` is what a
compilation or execution error raises.
"""
from __future__ import annotations

import functools

from ._errors import ArcadeError, NotBuiltFunction
from ._interpreter import Interpreter
from ._library import coverage, functions
from ._parser import parse

__all__ = ["ArcadeError", "NotBuiltFunction", "compile_script", "evaluate", "coverage", "run"]


@functools.lru_cache(maxsize=256)
def compile_script(source):
    """The parsed program of a script: a compilation error raises here."""
    return parse(source)


@functools.lru_cache(maxsize=1)
def _built():
    return functions()


def run(source, profile=None):
    """(value, console lines) of a script run with the profile variables given."""
    program = compile_script(source)
    interpreter = Interpreter(profile, _built())
    return interpreter.run(program), interpreter.console


def evaluate(source, profile=None):
    """The value a script answers."""
    return run(source, profile)[0]
