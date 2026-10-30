"""Arcade's evaluation (ADR-235), written from the language guide: variables are declared with ``var`` and named
without regard to case; a variable declared outside a function is global, one declared in a function is local, and
blocks have no scope of their own; a script and a function answer their ``return`` or else the value of the last
statement executed. Conditions and logical operands are Booleans, never cast; the arithmetic, ``+`` and comparison
operators cast as the *Type casting* page tabulates.
"""
from __future__ import annotations

import math

from ._errors import ArcadeError
from ._values import (Date, DateOnly, Dictionary, Feature, Function, Geometry, Time, compare, equal, is_number,
                      require_bool, to_number, to_text, type_of)

_MAX_DEPTH = 200


class _Return(Exception):
    def __init__(self, value):
        self.value = value


class _Break(Exception):
    pass


class _Continue(Exception):
    pass


class _Frame:
    __slots__ = ("names", "last")

    def __init__(self):
        self.names = {}
        self.last = None


def _int32(value):
    number = to_number(value)
    if math.isnan(number) or math.isinf(number):
        return 0
    number = int(number) & 0xFFFFFFFF
    return number - 0x100000000 if number >= 0x80000000 else number


def arithmetic(op, a, b):
    if op == "+":
        if isinstance(a, str) or isinstance(b, str):
            return to_text(a) + to_text(b)
        return to_number(a) + to_number(b)
    x, y = to_number(a), to_number(b)
    if op == "-":
        return x - y
    if op == "*":
        return x * y
    if op == "/":
        if y == 0:
            if x == 0 or math.isnan(x):
                return math.nan
            return math.copysign(math.inf, x) * math.copysign(1.0, y)
        return x / y
    if op == "%":
        if y == 0 or math.isinf(x) or math.isnan(x) or math.isnan(y):
            return math.nan
        return math.fmod(x, y)
    if op == "&":
        return float(_int32(a) & _int32(b))
    if op == "|":
        return float(_int32(a) | _int32(b))
    if op == "^":
        return float(_int32(a) ^ _int32(b))
    if op == "<<":
        return float(_int32(_int32(a) << (_int32(b) & 31)))
    if op == ">>":
        return float(_int32(a) >> (_int32(b) & 31))
    if op == ">>>":
        return float((_int32(a) & 0xFFFFFFFF) >> (_int32(b) & 31))
    raise ArcadeError(f"Unknown operator '{op}'.")


class Interpreter:
    """Runs one program. ``profile`` holds the profile variables (``$feature`` …) by lower-case name; ``functions`` the
    built-in function library by lower-case name."""

    def __init__(self, profile=None, functions=None):
        self.profile = {k.lower(): v for k, v in (profile or {}).items()}
        self.functions = functions or {}
        self.globals = _Frame()
        self.frames = [self.globals]
        self.line = None
        self.console = []

    # -- entry
    def run(self, program):
        from ._library import constants
        for name, value in constants().items():
            self.globals.names[name.lower()] = value
        try:
            self.block(program[1])
        except _Return as done:
            return done.value
        except (_Break, _Continue):
            raise ArcadeError("'break' and 'continue' belong inside a loop.", self.line) from None
        except ArcadeError as error:
            if error.line is None and self.line is not None:
                raise type(error)(error.message, self.line) from None
            raise
        except RecursionError:
            raise ArcadeError("The expression recursed too deeply.", self.line) from None
        return self.globals.last

    # -- statements
    def block(self, statements):
        for statement in statements:
            self.statement(statement)

    def statement(self, node):
        kind = node[0]
        self.line = node[-1] if kind not in ("program",) else self.line
        frame = self.frames[-1]
        if kind == "expr":
            frame.last = self.evaluate(node[1])
        elif kind == "var":
            for name, value in node[1]:
                frame.names[name.lower()] = None if value is None else self.evaluate(value)
        elif kind == "block":
            self.block(node[1])
        elif kind == "if":
            if require_bool(self.evaluate(node[1]), "An if condition"):
                self.statement(node[2])
            elif node[3] is not None:
                self.statement(node[3])
        elif kind == "for":
            self.for_loop(node)
        elif kind == "forin":
            self.for_in(node)
        elif kind == "while":
            while require_bool(self.evaluate(node[1]), "A while condition"):
                try:
                    self.statement(node[2])
                except _Break:
                    break
                except _Continue:
                    continue
        elif kind == "return":
            raise _Return(None if node[1] is None else self.evaluate(node[1]))
        elif kind == "break":
            raise _Break()
        elif kind == "continue":
            raise _Continue()
        elif kind == "function":
            frame.names[node[1].lower()] = Function(node[1], node=node)
        elif kind == "empty":
            pass
        else:
            raise ArcadeError(f"Unknown statement '{kind}'.", self.line)

    def for_loop(self, node):
        _, init, condition, update, body, _ = node
        if init is not None:
            self.statement(init)
        while condition is None or require_bool(self.evaluate(condition), "A for condition"):
            try:
                self.statement(body)
            except _Break:
                break
            except _Continue:
                pass
            if update is not None:
                self.evaluate(update)

    def for_in(self, node):
        _, declare, name, iterable, body, of, line = node
        from ._data import FeatureSet
        collection = self.evaluate(iterable)
        if isinstance(collection, list):
            items = list(collection) if of else [float(i) for i in range(len(collection))]
        elif isinstance(collection, str):
            items = list(collection) if of else [float(i) for i in range(len(collection))]
        elif isinstance(collection, (Dictionary, Feature, Geometry)):
            pairs = self.pairs(collection)
            items = [Dictionary({"key": k, "value": v}) for k, v in pairs] if of else [k for k, _ in pairs]
        elif isinstance(collection, FeatureSet):
            items = collection.features()
        elif collection is None:
            items = []
        else:
            raise ArcadeError(f"A {type_of(collection)} cannot be iterated by for…{'of' if of else 'in'}.", line)
        if declare:
            self.frames[-1].names[name.lower()] = None
        for item in items:
            self.assign_name(name, item)
            try:
                self.statement(body)
            except _Break:
                break
            except _Continue:
                continue

    def pairs(self, value):
        if isinstance(value, Dictionary):
            return value.items()
        if isinstance(value, Feature):
            return value.attributes.items()
        from ._geometry import properties
        return list(properties(value).items())

    # -- names
    def lookup(self, name, line):
        lower = name.lower()
        if lower.startswith("$"):
            if lower in self.profile:
                return self.profile[lower]
            raise ArcadeError(f"The profile variable '{name}' is not available here.", line)
        frame = self.frames[-1]
        if lower in frame.names:
            return frame.names[lower]
        if lower in self.globals.names:
            return self.globals.names[lower]
        if lower in self.functions:
            return self.functions[lower]
        raise ArcadeError(f"'{name}' is not declared.", line)

    def assign_name(self, name, value):
        lower = name.lower()
        if lower.startswith("$"):
            raise ArcadeError(f"The profile variable '{name}' cannot be assigned to.", self.line)
        frame = self.frames[-1]
        if lower in frame.names:
            frame.names[lower] = value
        elif lower in self.globals.names:
            self.globals.names[lower] = value
        else:
            raise ArcadeError(f"'{name}' is not declared; declare it with var.", self.line)

    # -- expressions
    def evaluate(self, node):
        kind = node[0]
        if kind == "num":
            return node[1]
        if kind == "str":
            return node[1]
        if kind == "bool":
            return node[1]
        if kind == "null":
            return None
        if kind == "name":
            return self.lookup(node[1], node[2])
        if kind == "tmpl":
            return "".join(part if isinstance(part, str) else to_text(self.evaluate(part)) for part in node[1])
        if kind == "array":
            return [self.evaluate(item) for item in node[1]]
        if kind == "dict":
            out = Dictionary()
            for key, value in node[1]:
                out.set(key, self.evaluate(value))
            return out
        if kind == "member":
            return self.member(self.evaluate(node[1]), node[2], node[3], node[4])
        if kind == "index":
            return self.index(self.evaluate(node[1]), self.evaluate(node[2]), node[3], node[4])
        if kind == "call":
            return self.call(node)
        if kind == "unary":
            return self.unary(node)
        if kind == "update":
            return self.update(node)
        if kind == "and":
            left = require_bool(self.evaluate(node[2]), "An operand of &&")
            return left and require_bool(self.evaluate(node[3]), "An operand of &&")
        if kind == "or":
            left = require_bool(self.evaluate(node[2]), "An operand of ||")
            return left or require_bool(self.evaluate(node[3]), "An operand of ||")
        if kind == "bin":
            op, a, b = node[1], self.evaluate(node[2]), self.evaluate(node[3])
            if op == "==":
                return equal(a, b)
            if op == "!=":
                return not equal(a, b)
            if op in ("<", ">", "<=", ">="):
                return compare(op, a, b)
            return arithmetic(op, a, b)
        if kind == "assign":
            return self.assign(node)
        raise ArcadeError(f"Unknown expression '{kind}'.", self.line)

    def unary(self, node):
        op, value = node[1], self.evaluate(node[2])
        if op == "!":
            return not require_bool(value, "The operand of !")
        if op == "-":
            return -to_number(value)
        if op == "+":
            return to_number(value)
        return float(~_int32(value))

    def update(self, node):
        op, prefix, target = node[1], node[2], node[3]
        old = self.evaluate(target)
        before = float(old) if is_number(old) else math.nan  # ++ and -- do not cast: anything else is NaN
        after = before + (1 if op == "++" else -1)
        self.store(target, after)
        return after if prefix else before

    def assign(self, node):
        op, target, value_node = node[1], node[2], node[3]
        value = self.evaluate(value_node)
        if op != "=":
            value = arithmetic(op[0], self.evaluate(target), value)
        self.store(target, value)
        return value

    def store(self, target, value):
        kind = target[0]
        if kind == "name":
            self.assign_name(target[1], value)
            return
        holder = self.evaluate(target[1])
        key = target[2] if kind == "member" else self.evaluate(target[2])
        if isinstance(holder, list):
            if kind == "member" or not is_number(key) or key != int(key):
                raise ArcadeError("An array is indexed by a whole number.", self.line)
            index = int(key)
            if index < 0:
                index += len(holder)
            if index == len(holder):
                holder.append(value)
            elif 0 <= index < len(holder):
                holder[index] = value
            else:
                raise ArcadeError(f"Index {int(key)} is out of the array's bounds; an array cannot have missing "
                                  "elements.", self.line)
        elif isinstance(holder, Dictionary):
            holder.set(to_text(key), value)
        elif isinstance(holder, Feature):
            if holder.immutable:
                raise ArcadeError("This feature cannot be changed: a profile's feature is immutable.", self.line)
            holder.attributes.set(to_text(key), value)
        elif holder is None:
            raise ArcadeError("A member of null cannot be assigned to.", self.line)
        else:
            raise ArcadeError(f"A {type_of(holder)} cannot be assigned to by member.", self.line)

    def member(self, holder, name, safe, line):
        if holder is None or (safe and holder == ""):
            if safe:
                return None
            raise ArcadeError(f"Cannot read '{name}' of null.", line)
        if isinstance(holder, Dictionary):
            if not holder.has(name):
                if safe:
                    return None
                raise ArcadeError(f"The dictionary has no key '{name}'.", line)
            return holder.get(name)
        if isinstance(holder, Feature):
            if not holder.attributes.has(name):
                if safe:
                    return None
                raise ArcadeError(f"The feature has no field '{name}'.", line)
            return holder.attributes.get(name)
        if isinstance(holder, Geometry):
            from ._geometry import properties
            found = properties(holder)
            for key, value in found.items():
                if key.lower() == name.lower():
                    return value
            if safe:
                return None
            raise ArcadeError(f"A {holder.kind} has no property '{name}'.", line)
        if isinstance(holder, (Date, DateOnly, Time)):
            raise ArcadeError(f"A {type_of(holder)} has no members; use the date functions.", line)
        if safe:
            return None
        raise ArcadeError(f"A {type_of(holder)} has no member '{name}'.", line)

    def index(self, holder, key, safe, line):
        if holder is None:
            if safe:
                return None
            raise ArcadeError("Cannot index null.", line)
        if isinstance(holder, (list, str)):
            if not is_number(key) or key != int(key):
                raise ArcadeError(f"A{'n array' if isinstance(holder, list) else ' text value'} is indexed by a "
                                  "whole number.", line)
            at = int(key)
            if at < 0:
                at += len(holder)
            if not 0 <= at < len(holder):
                if safe:
                    return None
                raise ArcadeError(f"Index {int(key)} is out of bounds.", line)
            return holder[at]
        return self.member(holder, to_text(key), safe, line)

    # -- calls
    def call(self, node):
        _, callee, arg_nodes, line = node
        if callee[0] == "name":
            # A variable named like a function (``var count = Count(x)``) does not hide it: a call looks for a function.
            lower = callee[1].lower()
            target = None
            for frame in (self.frames[-1], self.globals):
                if isinstance(frame.names.get(lower), Function):
                    target = frame.names[lower]
                    break
            if target is None:
                target = self.functions.get(lower)
            if target is None:
                for frame in (self.frames[-1], self.globals):
                    if lower in frame.names:
                        raise ArcadeError(f"'{callee[1]}' is a {type_of(frame.names[lower])}, not a function.", line)
                from ._library import not_built
                not_built(callee[1], line)
        else:
            target = self.evaluate(callee)
        args = [self.evaluate(a) for a in arg_nodes]
        if not isinstance(target, Function):
            raise ArcadeError(f"A {type_of(target)} is not a function.", line)
        self.line = line
        return self.invoke(target, args)

    def invoke(self, function, args):
        if function.call is not None:
            return function.call(self, args)
        _, name, params, body, line = function.node
        if len(args) != len(params):
            raise ArcadeError(f"The function '{name}' takes {len(params)} argument{'s' if len(params) != 1 else ''}; "
                              f"{len(args)} {'were' if len(args) != 1 else 'was'} given.", self.line)
        if len(self.frames) > _MAX_DEPTH:
            raise ArcadeError("The expression recursed too deeply.", self.line)
        frame = _Frame()
        for param, value in zip(params, args):
            frame.names[param.lower()] = value
        self.frames.append(frame)
        called_from = self.line
        try:
            self.block(body[1])
        except _Return as done:
            return done.value
        except (_Break, _Continue):
            raise ArcadeError("'break' and 'continue' belong inside a loop.", self.line) from None
        finally:
            self.frames.pop()
            self.line = called_from
        return frame.last
