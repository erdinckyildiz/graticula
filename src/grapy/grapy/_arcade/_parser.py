"""Arcade's grammar as a recursive-descent parser (ADR-235), written from the language guide: statements (``var``,
``function``, ``if``/``else``, ``for`` in its three forms, ``while``, ``return``, ``break``, ``continue``, blocks),
and expressions with the operators the guide lists.

The guide lists the operators but gives no precedence table. INFERRED (ADR-235 §5.8): JavaScript's precedence and
associativity, which Arcade's syntax follows: assignment (right to left) < ``||`` < ``&&`` < ``|`` < ``^`` < ``&`` <
equality < relational < shifts < additive < multiplicative < unary < postfix < member access and calls.

Nodes are tuples whose first item names them. Statements carry their line last, so an error can name it.
"""
from __future__ import annotations

from ._errors import ArcadeError
from ._lexer import tokens

_BINARY = {"||": 1, "&&": 2, "|": 3, "^": 4, "&": 5, "==": 6, "!=": 6, "<": 7, ">": 7, "<=": 7, ">=": 7,
           "<<": 8, ">>": 8, ">>>": 8, "+": 9, "-": 9, "*": 10, "/": 10, "%": 10}
_ASSIGN = {"=", "+=", "-=", "*=", "/=", "%="}


class Parser:
    def __init__(self, source):
        self.tokens = tokens(source)
        self.at = 0

    # -- tokens
    @property
    def token(self):
        return self.tokens[self.at]

    def peek(self, ahead=1):
        return self.tokens[min(self.at + ahead, len(self.tokens) - 1)]

    def is_op(self, value):
        return self.token.kind == "op" and self.token.value == value

    def is_kw(self, value):
        return self.token.kind == "kw" and self.token.value == value

    def advance(self):
        token = self.token
        self.at += 1
        return token

    def expect_op(self, value):
        if not self.is_op(value):
            self.fail(f"'{value}' was expected")
        return self.advance()

    def fail(self, what):
        found = self.token
        shown = "the end of the expression" if found.kind == "eof" else repr(found.value)
        raise ArcadeError(f"{what}, but {shown} was found.", found.line)

    def name(self):
        if self.token.kind != "name":
            self.fail("A name was expected")
        return self.advance().value

    # -- statements
    def program(self):
        body = []
        while self.token.kind != "eof":
            body.append(self.statement())
        return ("program", body)

    def end_statement(self):
        if self.is_op(";"):
            self.advance()

    def statement(self):
        token = self.token
        line = token.line
        if token.kind == "op" and token.value == ";":
            self.advance()
            return ("empty", line)
        if token.kind == "op" and token.value == "{":
            return self.block()
        if token.kind == "kw":
            word = token.value
            if word == "var":
                self.advance()
                declared = self.declarations()
                self.end_statement()
                return ("var", declared, line)
            if word == "function":
                return self.function()
            if word == "if":
                self.advance()
                self.expect_op("(")
                condition = self.expression()
                self.expect_op(")")
                then = self.statement()
                otherwise = None
                if self.is_kw("else"):
                    self.advance()
                    otherwise = self.statement()
                return ("if", condition, then, otherwise, line)
            if word == "for":
                return self.for_statement()
            if word == "while":
                self.advance()
                self.expect_op("(")
                condition = self.expression()
                self.expect_op(")")
                return ("while", condition, self.statement(), line)
            if word == "return":
                self.advance()
                value = None
                if not (self.is_op(";") or self.is_op("}") or self.token.kind == "eof"):
                    value = self.expression()
                self.end_statement()
                return ("return", value, line)
            if word in ("break", "continue"):
                self.advance()
                self.end_statement()
                return (word, line)
            if word in ("import", "export"):
                raise ArcadeError(f"'{word}' (Arcade modules) is not built in grapy: a user tool's rule runs one "
                                  "expression and has no module files beside it.", line)
        value = self.expression()
        self.end_statement()
        return ("expr", value, line)

    def block(self):
        line = self.expect_op("{").line
        body = []
        while not self.is_op("}"):
            if self.token.kind == "eof":
                self.fail("'}' was expected")
            body.append(self.statement())
        self.advance()
        return ("block", body, line)

    def declarations(self):
        declared = []
        while True:
            name = self.name()
            value = None
            if self.is_op("="):
                self.advance()
                value = self.assignment()
            declared.append((name, value))
            if not self.is_op(","):
                return declared
            self.advance()

    def function(self):
        line = self.advance().line
        name = self.name()
        self.expect_op("(")
        params = []
        while not self.is_op(")"):
            params.append(self.name())
            if not self.is_op(","):
                break
            self.advance()
        self.expect_op(")")
        if not self.is_op("{"):
            self.fail("'{' was expected")
        return ("function", name, params, self.block(), line)

    def for_statement(self):
        line = self.advance().line
        self.expect_op("(")
        # for (var x in …) / for (x in …) / for (var x of …) / for (x of …)
        start = self.at
        declare = False
        if self.is_kw("var"):
            self.advance()
            declare = True
        if self.token.kind == "name" and self.peek().kind == "kw" and self.peek().value in ("in", "of"):
            name = self.advance().value
            kind = self.advance().value
            iterable = self.expression()
            self.expect_op(")")
            return ("forin", declare, name, iterable, self.statement(), kind == "of", line)
        self.at = start
        init = None
        if self.is_kw("var"):
            self.advance()
            init = ("var", self.declarations(), line)
        elif not self.is_op(";"):
            init = ("expr", self.expression(), line)
        self.expect_op(";")
        condition = None if self.is_op(";") else self.expression()
        self.expect_op(";")
        update = None if self.is_op(")") else self.expression()
        self.expect_op(")")
        return ("for", init, condition, update, self.statement(), line)

    # -- expressions
    def expression(self):
        return self.assignment()

    def assignment(self):
        target = self.binary(1)
        if self.token.kind == "op" and self.token.value in _ASSIGN:
            token = self.advance()
            if target[0] not in ("name", "member", "index"):
                raise ArcadeError("Only a variable, a dictionary or array member, or a feature's attribute can be "
                                  "assigned to.", token.line)
            return ("assign", token.value, target, self.assignment(), token.line)
        return target

    def binary(self, level):
        if level > 10:
            return self.unary()
        left = self.binary(level + 1)
        while self.token.kind == "op" and _BINARY.get(self.token.value) == level:
            token = self.advance()
            right = self.binary(level + 1)
            kind = {"&&": "and", "||": "or"}.get(token.value, "bin")
            left = (kind, token.value, left, right, token.line)
        return left

    def unary(self):
        token = self.token
        if token.kind == "op" and token.value in ("!", "-", "+", "~"):
            self.advance()
            return ("unary", token.value, self.unary(), token.line)
        if token.kind == "op" and token.value in ("++", "--"):
            self.advance()
            target = self.unary()
            if target[0] not in ("name", "member", "index"):
                raise ArcadeError(f"'{token.value}' needs a variable.", token.line)
            return ("update", token.value, True, target, token.line)
        return self.postfix()

    def postfix(self):
        node = self.call()
        if self.token.kind == "op" and self.token.value in ("++", "--"):
            token = self.advance()
            if node[0] not in ("name", "member", "index"):
                raise ArcadeError(f"'{token.value}' needs a variable.", token.line)
            return ("update", token.value, False, node, token.line)
        return node

    def call(self):
        node = self.primary()
        while True:
            token = self.token
            if token.kind != "op":
                return node
            if token.value == ".":
                self.advance()
                if self.token.kind not in ("name", "kw"):
                    self.fail("A member name was expected")
                node = ("member", node, self.advance().value, False, token.line)
            elif token.value == "?.":
                self.advance()
                if self.is_op("["):
                    self.advance()
                    index = self.expression()
                    self.expect_op("]")
                    node = ("index", node, index, True, token.line)
                else:
                    if self.token.kind not in ("name", "kw"):
                        self.fail("A member name was expected")
                    node = ("member", node, self.advance().value, True, token.line)
            elif token.value == "[":
                self.advance()
                index = self.expression()
                self.expect_op("]")
                node = ("index", node, index, False, token.line)
            elif token.value == "(":
                self.advance()
                args = []
                while not self.is_op(")"):
                    args.append(self.assignment())
                    if not self.is_op(","):
                        break
                    self.advance()
                self.expect_op(")")
                node = ("call", node, args, token.line)
            else:
                return node

    def primary(self):
        token = self.token
        if token.kind == "num":
            self.advance()
            return ("num", token.value)
        if token.kind == "str":
            self.advance()
            return ("str", token.value)
        if token.kind == "tmpl":
            self.advance()
            parts = []
            for part in token.value:
                if isinstance(part, tuple):
                    inner = Parser(part[1])
                    if inner.token.kind == "eof":
                        raise ArcadeError("A ${} placeholder in a template literal is empty.", token.line)
                    expression = inner.expression()
                    if inner.token.kind != "eof":
                        inner.fail("The end of the placeholder was expected")
                    parts.append(expression)
                else:
                    parts.append(part)
            return ("tmpl", parts, token.line)
        if token.kind == "kw":
            if token.value in ("true", "false"):
                self.advance()
                return ("bool", token.value == "true")
            if token.value == "null":
                self.advance()
                return ("null",)
            self.fail("An expression was expected")
        if token.kind == "name":
            self.advance()
            return ("name", token.value, token.line)
        if token.kind == "op":
            if token.value == "(":
                self.advance()
                inner = self.expression()
                self.expect_op(")")
                return inner
            if token.value == "[":
                self.advance()
                items = []
                while not self.is_op("]"):
                    items.append(self.assignment())
                    if not self.is_op(","):
                        break
                    self.advance()
                self.expect_op("]")
                return ("array", items, token.line)
            if token.value == "{":
                return self.dictionary()
        self.fail("An expression was expected")

    def dictionary(self):
        line = self.advance().line
        entries = []
        while not self.is_op("}"):
            key_token = self.token
            if key_token.kind in ("name", "kw"):
                key = self.advance().value
                if not self.is_op(":"):
                    if key_token.kind != "name":
                        self.fail("':' was expected")
                    entries.append((key, ("name", key, key_token.line)))  # {a, b}: auto-keys (since 1.19)
                    if not self.is_op(","):
                        break
                    self.advance()
                    continue
            elif key_token.kind == "str":
                key = self.advance().value
            elif key_token.kind == "num":
                from ._values import number_text
                key = number_text(self.advance().value)
            else:
                self.fail("A dictionary key was expected")
            self.expect_op(":")
            entries.append((key, self.assignment()))
            if not self.is_op(","):
                break
            self.advance()
        self.expect_op("}")
        return ("dict", entries, line)


def parse(source):
    """The program a script is: ("program", [statements])."""
    if not isinstance(source, str):
        raise ArcadeError("An Arcade expression is text.")
    return Parser(source).program()
