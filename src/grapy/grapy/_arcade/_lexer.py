"""Arcade's tokens, read from the language guide's structure pages (ADR-235): numbers in decimal, exponential, binary,
octal and hexadecimal notation; text in single or double quotes; template literals in backticks with ``${…}``
placeholders; identifiers of Latin letters, digits, ``_`` and ``$``; ``//`` and ``/* */`` comments.
"""
from __future__ import annotations

from ._errors import ArcadeError

KEYWORDS = {"var", "if", "else", "for", "in", "of", "while", "function", "return", "break", "continue", "true", "false",
            "null", "import", "export"}

# Longest first, so that ``>>>`` is not read as ``>>`` and ``>``.
OPERATORS = (">>>", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "?.",
             "+", "-", "*", "/", "%", "<", ">", "=", "!", "&", "|", "^", "~", "(", ")", "{", "}", "[", "]", ";", ",",
             ".", ":")

_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "b": "\b", "f": "\f", "v": "\v", "0": "\0", "\\": "\\", "'": "'",
            '"': '"', "`": "`", "$": "$", "/": "/"}


class Token:
    __slots__ = ("kind", "value", "line")

    def __init__(self, kind, value, line):
        self.kind, self.value, self.line = kind, value, line

    def __repr__(self):
        return f"Token({self.kind}, {self.value!r}, {self.line})"


def _escape(source, at, line):
    """(text, next position) of the escape sequence whose backslash is at ``at``."""
    if at + 1 >= len(source):
        raise ArcadeError("A text value ends inside an escape sequence.", line)
    char = source[at + 1]
    if char == "u":
        if at + 2 < len(source) and source[at + 2] == "{":
            end = source.find("}", at + 3)
            if end < 0:
                raise ArcadeError("A \\u{…} escape is not closed.", line)
            return chr(int(source[at + 3:end], 16)), end + 1
        digits = source[at + 2:at + 6]
        if len(digits) != 4 or any(c not in "0123456789abcdefABCDEF" for c in digits):
            raise ArcadeError("A \\u escape takes four hexadecimal digits.", line)
        return chr(int(digits, 16)), at + 6
    if char == "\n":
        return "", at + 2
    return _ESCAPES.get(char, char), at + 2


def _template_end(source, at, line):
    """The position of the ``}`` closing a placeholder whose ``${`` ends just before ``at``: braces, text values and
    nested template literals inside it are skipped."""
    depth = 1
    while at < len(source):
        char = source[at]
        if char in "'\"":
            at = _string(source, at, line)[1]
            continue
        if char == "`":
            at = _template(source, at, line)[1]
            continue
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return at
        at += 1
    raise ArcadeError("A ${ placeholder in a template literal is not closed.", line)


def _string(source, at, line):
    quote = source[at]
    out = []
    at += 1
    while True:
        if at >= len(source) or source[at] == "\n":
            raise ArcadeError("A text value is not closed.", line)
        char = source[at]
        if char == quote:
            return "".join(out), at + 1
        if char == "\\":
            text, at = _escape(source, at, line)
            out.append(text)
            continue
        out.append(char)
        at += 1


def _template(source, at, line):
    """(parts, next position): each part is text, or ("expr", source text, line) for a placeholder."""
    parts, out = [], []
    at += 1
    while True:
        if at >= len(source):
            raise ArcadeError("A template literal is not closed.", line)
        char = source[at]
        if char == "`":
            if out:
                parts.append("".join(out))
            return parts, at + 1
        if char == "\\":
            text, at = _escape(source, at, line)
            out.append(text)
            continue
        if char == "$" and at + 1 < len(source) and source[at + 1] == "{":
            if out:
                parts.append("".join(out))
                out = []
            end = _template_end(source, at + 2, line)
            parts.append(("expr", source[at + 2:end], line + source.count("\n", 0, at) - source.count("\n", 0, at)))
            at = end + 1
            continue
        out.append(char)
        at += 1


def _number(source, at, line):
    start = at
    if source[at] == "0" and at + 1 < len(source) and source[at + 1] in "xXbBoO":
        base = {"x": 16, "b": 2, "o": 8}[source[at + 1].lower()]
        at += 2
        begin = at
        while at < len(source) and (source[at].isalnum() or source[at] == "_"):
            at += 1
        try:
            return float(int(source[begin:at], base)), at
        except ValueError:
            raise ArcadeError(f"'{source[start:at]}' is not a number.", line) from None
    while at < len(source) and source[at].isdigit():
        at += 1
    if at < len(source) and source[at] == "." and (at + 1 >= len(source) or source[at + 1] != "."):
        at += 1
        while at < len(source) and source[at].isdigit():
            at += 1
    if at < len(source) and source[at] in "eE":
        look = at + 1
        if look < len(source) and source[look] in "+-":
            look += 1
        if look < len(source) and source[look].isdigit():
            at = look
            while at < len(source) and source[at].isdigit():
                at += 1
    return float(source[start:at]), at


def tokens(source):
    out = []
    at, line = 0, 1
    length = len(source)
    while at < length:
        char = source[at]
        if char == "\n":
            line += 1
            at += 1
            continue
        if char in " \t\r\f\v﻿ ":
            at += 1
            continue
        if source.startswith("//", at):
            end = source.find("\n", at)
            at = length if end < 0 else end
            continue
        if source.startswith("/*", at):
            end = source.find("*/", at + 2)
            if end < 0:
                raise ArcadeError("A /* comment is not closed.", line)
            line += source.count("\n", at, end)
            at = end + 2
            continue
        if char.isdigit() or (char == "." and at + 1 < length and source[at + 1].isdigit()):
            value, at = _number(source, at, line)
            out.append(Token("num", value, line))
            continue
        if char in "'\"":
            value, at = _string(source, at, line)
            out.append(Token("str", value, line))
            continue
        if char == "`":
            begin = at
            value, at = _template(source, at, line)
            out.append(Token("tmpl", value, line))
            line += source.count("\n", begin, at)
            continue
        if char.isascii() and (char.isalpha() or char in "_$"):
            begin = at
            while at < length and source[at].isascii() and (source[at].isalnum() or source[at] in "_$"):
                at += 1
            word = source[begin:at]
            out.append(Token("kw" if word.lower() in KEYWORDS else "name", word.lower() if word.lower() in KEYWORDS
                             else word, line))
            continue
        for operator in OPERATORS:
            if source.startswith(operator, at):
                out.append(Token("op", operator, line))
                at += len(operator)
                break
        else:
            raise ArcadeError(f"Unexpected character '{char}'.", line)
    out.append(Token("eof", None, line))
    return out
