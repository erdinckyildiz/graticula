/*
  Studio's Arcade — ADR-313, 2026-11-22, by owner decision ("Tasarım + kendi düzenleyicide çalıştır").

  A clean-room interpreter of Esri's Arcade language, written from the public language guide and function reference
  (developers.arcgis.com/arcade), with grapy's interpreter (ADR-235, src/grapy/grapy/_arcade) as a map of the same
  pages. It runs a web map form's expressions in the browser — in the form designer, to test them, and in Studio's
  editing card, to apply them — and in Node, where tests/studio-js runs it headless.

  <b>What it will not do.</b> It has no network access and never runs text as JavaScript: there is no eval and no
  Function here. A FeatureSet cannot be fetched, so $layer, $featureSet, $datastore, $map and every FeatureSet
  function are refused by name, as are the geometry operations (D-476). A documented function that is not built
  raises an error that names it and says why; an undocumented name is "not a function".

  <b>Values.</b> Number — a JS number; Text — a string; Boolean; null; Array — a JS array; and the classes below for
  Dictionary, Feature, Geometry, Date, DateOnly, Time and Function.

  Everything is inside one function and leaves one global, GraticulaArcade, because Studio's classic scripts share
  one namespace.
*/
(function (root) {
  "use strict";

  // ---------------------------------------------------------------------------------------------------- errors

  class ArcadeError extends Error {
    constructor(message, line = null) {
      super(line ? `${message} (line ${line})` : message);
      this.name = "ArcadeError";
      this.arcadeMessage = message;
      this.line = line;
    }
  }

  /** A documented function that is not built here, or not for these arguments. */
  class NotBuiltFunction extends ArcadeError {
    constructor(message, line = null) {
      super(message, line);
      this.name = "NotBuiltFunction";
    }
  }

  // ---------------------------------------------------------------------------------------------------- tokens

  const KEYWORDS = new Set(["var", "if", "else", "for", "in", "of", "while", "function", "return", "break", "continue",
    "true", "false", "null", "import", "export"]);

  // Longest first, so that ">>>" is not read as ">>" and ">".
  const OPERATORS = [">>>", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=",
    "?.", "+", "-", "*", "/", "%", "<", ">", "=", "!", "&", "|", "^", "~", "(", ")", "{", "}", "[", "]", ";", ",", ".",
    ":"];

  const ESCAPES = { n: "\n", t: "\t", r: "\r", b: "\b", f: "\f", v: "\v", 0: "\0", "\\": "\\", "'": "'", '"': '"',
    "`": "`", $: "$", "/": "/" };

  const isDigit = c => c >= "0" && c <= "9";
  const isIdentStart = c => (c >= "a" && c <= "z") || (c >= "A" && c <= "Z") || c === "_" || c === "$";
  const isIdent = c => isIdentStart(c) || isDigit(c);

  function escapeAt(source, at, line) {
    if (at + 1 >= source.length) throw new ArcadeError("A text value ends inside an escape sequence.", line);
    const char = source[at + 1];
    if (char === "u") {
      if (source[at + 2] === "{") {
        const end = source.indexOf("}", at + 3);
        if (end < 0) throw new ArcadeError("A \\u{…} escape is not closed.", line);
        return [String.fromCodePoint(parseInt(source.slice(at + 3, end), 16)), end + 1];
      }
      const digits = source.slice(at + 2, at + 6);
      if (!/^[0-9a-fA-F]{4}$/.test(digits)) throw new ArcadeError("A \\u escape takes four hexadecimal digits.", line);
      return [String.fromCharCode(parseInt(digits, 16)), at + 6];
    }
    if (char === "\n") return ["", at + 2];
    return [ESCAPES[char] !== undefined ? ESCAPES[char] : char, at + 2];
  }

  function stringAt(source, at, line) {
    const quote = source[at];
    let out = "";
    at++;
    for (;;) {
      if (at >= source.length || source[at] === "\n") throw new ArcadeError("A text value is not closed.", line);
      const char = source[at];
      if (char === quote) return [out, at + 1];
      if (char === "\\") {
        const [text, next] = escapeAt(source, at, line);
        out += text;
        at = next;
        continue;
      }
      out += char;
      at++;
    }
  }

  function templateEnd(source, at, line) {
    let depth = 1;
    while (at < source.length) {
      const char = source[at];
      if (char === "'" || char === '"') { at = stringAt(source, at, line)[1]; continue; }
      if (char === "`") { at = templateAt(source, at, line)[1]; continue; }
      if (char === "{") depth++;
      else if (char === "}" && --depth === 0) return at;
      at++;
    }
    throw new ArcadeError("A ${ placeholder in a template literal is not closed.", line);
  }

  /** [parts, next]: each part is text, or { expr: source } for a placeholder. */
  function templateAt(source, at, line) {
    const parts = [];
    let out = "";
    at++;
    for (;;) {
      if (at >= source.length) throw new ArcadeError("A template literal is not closed.", line);
      const char = source[at];
      if (char === "`") {
        if (out) parts.push(out);
        return [parts, at + 1];
      }
      if (char === "\\") {
        const [text, next] = escapeAt(source, at, line);
        out += text;
        at = next;
        continue;
      }
      if (char === "$" && source[at + 1] === "{") {
        if (out) { parts.push(out); out = ""; }
        const end = templateEnd(source, at + 2, line);
        parts.push({ expr: source.slice(at + 2, end) });
        at = end + 1;
        continue;
      }
      out += char;
      at++;
    }
  }

  function numberAt(source, at, line) {
    const start = at;
    if (source[at] === "0" && /[xXbBoO]/.test(source[at + 1] || "")) {
      const base = { x: 16, b: 2, o: 8 }[source[at + 1].toLowerCase()];
      at += 2;
      const begin = at;
      while (at < source.length && (isIdent(source[at]))) at++;
      const digits = source.slice(begin, at);
      const valid = { 16: /^[0-9a-fA-F]+$/, 2: /^[01]+$/, 8: /^[0-7]+$/ }[base];
      if (!valid.test(digits)) throw new ArcadeError(`'${source.slice(start, at)}' is not a number.`, line);
      return [parseInt(digits, base), at];
    }
    while (at < source.length && isDigit(source[at])) at++;
    if (source[at] === "." && source[at + 1] !== ".") {
      at++;
      while (at < source.length && isDigit(source[at])) at++;
    }
    if (source[at] === "e" || source[at] === "E") {
      let look = at + 1;
      if (source[look] === "+" || source[look] === "-") look++;
      if (isDigit(source[look] || "")) {
        at = look;
        while (at < source.length && isDigit(source[at])) at++;
      }
    }
    return [Number(source.slice(start, at)), at];
  }

  function tokens(source) {
    const out = [];
    let at = 0;
    let line = 1;
    const length = source.length;
    while (at < length) {
      const char = source[at];
      if (char === "\n") { line++; at++; continue; }
      if (" \t\r\f\v﻿ ".includes(char)) { at++; continue; }
      if (source.startsWith("//", at)) {
        const end = source.indexOf("\n", at);
        at = end < 0 ? length : end;
        continue;
      }
      if (source.startsWith("/*", at)) {
        const end = source.indexOf("*/", at + 2);
        if (end < 0) throw new ArcadeError("A /* comment is not closed.", line);
        line += (source.slice(at, end).match(/\n/g) || []).length;
        at = end + 2;
        continue;
      }
      if (isDigit(char) || (char === "." && isDigit(source[at + 1] || ""))) {
        const [value, next] = numberAt(source, at, line);
        out.push({ kind: "num", value, line });
        at = next;
        continue;
      }
      if (char === "'" || char === '"') {
        const [value, next] = stringAt(source, at, line);
        out.push({ kind: "str", value, line });
        at = next;
        continue;
      }
      if (char === "`") {
        const [value, next] = templateAt(source, at, line);
        out.push({ kind: "tmpl", value, line });
        line += (source.slice(at, next).match(/\n/g) || []).length;
        at = next;
        continue;
      }
      if (isIdentStart(char)) {
        const begin = at;
        while (at < length && isIdent(source[at])) at++;
        const word = source.slice(begin, at);
        const lower = word.toLowerCase();
        out.push(KEYWORDS.has(lower) ? { kind: "kw", value: lower, line } : { kind: "name", value: word, line });
        continue;
      }
      const operator = OPERATORS.find(op => source.startsWith(op, at));
      if (!operator) throw new ArcadeError(`Unexpected character '${char}'.`, line);
      out.push({ kind: "op", value: operator, line });
      at += operator.length;
    }
    out.push({ kind: "eof", value: null, line });
    return out;
  }

  // ---------------------------------------------------------------------------------------------------- grammar
  //
  // The guide lists the operators but no precedence. INFERRED (ADR-235 §5.8 b, ADR-313 §7 i): JavaScript's, which
  // Arcade's syntax follows: assignment < || < && < | < ^ < & < equality < relational < shifts < additive <
  // multiplicative < unary < postfix < member access and calls.

  const BINARY = { "||": 1, "&&": 2, "|": 3, "^": 4, "&": 5, "==": 6, "!=": 6, "<": 7, ">": 7, "<=": 7, ">=": 7,
    "<<": 8, ">>": 8, ">>>": 8, "+": 9, "-": 9, "*": 10, "/": 10, "%": 10 };
  const ASSIGN = new Set(["=", "+=", "-=", "*=", "/=", "%="]);
  const ASSIGNABLE = new Set(["name", "member", "index"]);

  class Parser {
    constructor(source) {
      this.tokens = tokens(source);
      this.at = 0;
    }

    get token() { return this.tokens[this.at]; }
    peek(ahead = 1) { return this.tokens[Math.min(this.at + ahead, this.tokens.length - 1)]; }
    isOp(value) { return this.token.kind === "op" && this.token.value === value; }
    isKw(value) { return this.token.kind === "kw" && this.token.value === value; }
    advance() { return this.tokens[this.at++]; }

    expectOp(value) {
      if (!this.isOp(value)) this.fail(`'${value}' was expected`);
      return this.advance();
    }

    fail(what) {
      const found = this.token;
      const shown = found.kind === "eof" ? "the end of the expression" : `'${found.value}'`;
      throw new ArcadeError(`${what}, but ${shown} was found.`, found.line);
    }

    name() {
      if (this.token.kind !== "name") this.fail("A name was expected");
      return this.advance().value;
    }

    program() {
      const body = [];
      while (this.token.kind !== "eof") body.push(this.statement());
      return { t: "program", body };
    }

    endStatement() { if (this.isOp(";")) this.advance(); }

    statement() {
      const token = this.token;
      const line = token.line;
      if (this.isOp(";")) { this.advance(); return { t: "empty", line }; }
      if (this.isOp("{")) return this.block();
      if (token.kind === "kw") {
        switch (token.value) {
          case "var": {
            this.advance();
            const declared = this.declarations();
            this.endStatement();
            return { t: "var", declared, line };
          }
          case "function": return this.functionDeclaration();
          case "if": {
            this.advance();
            this.expectOp("(");
            const test = this.expression();
            this.expectOp(")");
            const then = this.statement();
            let otherwise = null;
            if (this.isKw("else")) { this.advance(); otherwise = this.statement(); }
            return { t: "if", test, then, otherwise, line };
          }
          case "for": return this.forStatement();
          case "while": {
            this.advance();
            this.expectOp("(");
            const test = this.expression();
            this.expectOp(")");
            return { t: "while", test, body: this.statement(), line };
          }
          case "return": {
            this.advance();
            let value = null;
            if (!(this.isOp(";") || this.isOp("}") || this.token.kind === "eof")) value = this.expression();
            this.endStatement();
            return { t: "return", value, line };
          }
          case "break":
          case "continue":
            this.advance();
            this.endStatement();
            return { t: token.value, line };
          case "import":
          case "export":
            throw new ArcadeError(`'${token.value}' (Arcade modules) is not built in Studio's form engine: a form's `
              + "expression has no module files beside it.", line);
          default:
        }
      }
      const value = this.expression();
      this.endStatement();
      return { t: "expr", value, line };
    }

    block() {
      const line = this.expectOp("{").line;
      const body = [];
      while (!this.isOp("}")) {
        if (this.token.kind === "eof") this.fail("'}' was expected");
        body.push(this.statement());
      }
      this.advance();
      return { t: "block", body, line };
    }

    declarations() {
      const declared = [];
      for (;;) {
        const name = this.name();
        let value = null;
        if (this.isOp("=")) { this.advance(); value = this.assignment(); }
        declared.push([name, value]);
        if (!this.isOp(",")) return declared;
        this.advance();
      }
    }

    functionDeclaration() {
      const line = this.advance().line;
      const name = this.name();
      this.expectOp("(");
      const params = [];
      while (!this.isOp(")")) {
        params.push(this.name());
        if (!this.isOp(",")) break;
        this.advance();
      }
      this.expectOp(")");
      if (!this.isOp("{")) this.fail("'{' was expected");
      return { t: "function", name, params, body: this.block(), line };
    }

    forStatement() {
      const line = this.advance().line;
      this.expectOp("(");
      const start = this.at;
      let declare = false;
      if (this.isKw("var")) { this.advance(); declare = true; }
      if (this.token.kind === "name" && this.peek().kind === "kw" && (this.peek().value === "in" || this.peek().value === "of")) {
        const name = this.advance().value;
        const of = this.advance().value === "of";
        const iterable = this.expression();
        this.expectOp(")");
        return { t: "forin", declare, name, iterable, body: this.statement(), of, line };
      }
      this.at = start;
      let init = null;
      if (this.isKw("var")) {
        this.advance();
        init = { t: "var", declared: this.declarations(), line };
      } else if (!this.isOp(";")) {
        init = { t: "expr", value: this.expression(), line };
      }
      this.expectOp(";");
      const test = this.isOp(";") ? null : this.expression();
      this.expectOp(";");
      const update = this.isOp(")") ? null : this.expression();
      this.expectOp(")");
      return { t: "for", init, test, update, body: this.statement(), line };
    }

    expression() { return this.assignment(); }

    assignment() {
      const target = this.binary(1);
      if (this.token.kind === "op" && ASSIGN.has(this.token.value)) {
        const token = this.advance();
        if (!ASSIGNABLE.has(target.t)) {
          throw new ArcadeError("Only a variable, a dictionary or array member, or a feature's attribute can be "
            + "assigned to.", token.line);
        }
        return { t: "assign", op: token.value, target, value: this.assignment(), line: token.line };
      }
      return target;
    }

    binary(level) {
      if (level > 10) return this.unary();
      let left = this.binary(level + 1);
      while (this.token.kind === "op" && BINARY[this.token.value] === level) {
        const token = this.advance();
        const right = this.binary(level + 1);
        const t = token.value === "&&" ? "and" : token.value === "||" ? "or" : "bin";
        left = { t, op: token.value, left, right, line: token.line };
      }
      return left;
    }

    unary() {
      const token = this.token;
      if (token.kind === "op" && ["!", "-", "+", "~"].includes(token.value)) {
        this.advance();
        return { t: "unary", op: token.value, value: this.unary(), line: token.line };
      }
      if (token.kind === "op" && (token.value === "++" || token.value === "--")) {
        this.advance();
        const target = this.unary();
        if (!ASSIGNABLE.has(target.t)) throw new ArcadeError(`'${token.value}' needs a variable.`, token.line);
        return { t: "update", op: token.value, prefix: true, target, line: token.line };
      }
      return this.postfix();
    }

    postfix() {
      const node = this.call();
      if (this.token.kind === "op" && (this.token.value === "++" || this.token.value === "--")) {
        const token = this.advance();
        if (!ASSIGNABLE.has(node.t)) throw new ArcadeError(`'${token.value}' needs a variable.`, token.line);
        return { t: "update", op: token.value, prefix: false, target: node, line: token.line };
      }
      return node;
    }

    memberName() {
      if (this.token.kind !== "name" && this.token.kind !== "kw") this.fail("A member name was expected");
      return this.advance().value;
    }

    call() {
      let node = this.primary();
      for (;;) {
        const token = this.token;
        if (token.kind !== "op") return node;
        if (token.value === ".") {
          this.advance();
          node = { t: "member", object: node, name: this.memberName(), safe: false, line: token.line };
        } else if (token.value === "?.") {
          this.advance();
          if (this.isOp("[")) {
            this.advance();
            const index = this.expression();
            this.expectOp("]");
            node = { t: "index", object: node, index, safe: true, line: token.line };
          } else {
            node = { t: "member", object: node, name: this.memberName(), safe: true, line: token.line };
          }
        } else if (token.value === "[") {
          this.advance();
          const index = this.expression();
          this.expectOp("]");
          node = { t: "index", object: node, index, safe: false, line: token.line };
        } else if (token.value === "(") {
          this.advance();
          const args = [];
          while (!this.isOp(")")) {
            args.push(this.assignment());
            if (!this.isOp(",")) break;
            this.advance();
          }
          this.expectOp(")");
          node = { t: "call", callee: node, args, line: token.line };
        } else {
          return node;
        }
      }
    }

    primary() {
      const token = this.token;
      if (token.kind === "num") { this.advance(); return { t: "lit", value: token.value }; }
      if (token.kind === "str") { this.advance(); return { t: "lit", value: token.value }; }
      if (token.kind === "tmpl") {
        this.advance();
        const parts = token.value.map(part => {
          if (typeof part === "string") return part;
          const inner = new Parser(part.expr);
          if (inner.token.kind === "eof") throw new ArcadeError("A ${} placeholder in a template literal is empty.", token.line);
          const expression = inner.expression();
          if (inner.token.kind !== "eof") inner.fail("The end of the placeholder was expected");
          return expression;
        });
        return { t: "tmpl", parts, line: token.line };
      }
      if (token.kind === "kw") {
        if (token.value === "true" || token.value === "false") { this.advance(); return { t: "lit", value: token.value === "true" }; }
        if (token.value === "null") { this.advance(); return { t: "lit", value: null }; }
        this.fail("An expression was expected");
      }
      if (token.kind === "name") { this.advance(); return { t: "name", name: token.value, line: token.line }; }
      if (token.kind === "op") {
        if (token.value === "(") {
          this.advance();
          const inner = this.expression();
          this.expectOp(")");
          return inner;
        }
        if (token.value === "[") {
          this.advance();
          const items = [];
          while (!this.isOp("]")) {
            items.push(this.assignment());
            if (!this.isOp(",")) break;
            this.advance();
          }
          this.expectOp("]");
          return { t: "array", items, line: token.line };
        }
        if (token.value === "{") return this.dictionary();
      }
      this.fail("An expression was expected");
      return null;
    }

    dictionary() {
      const line = this.advance().line;
      const entries = [];
      while (!this.isOp("}")) {
        const keyToken = this.token;
        let key;
        if (keyToken.kind === "name" || keyToken.kind === "kw") {
          key = this.advance().value;
          if (!this.isOp(":")) {
            if (keyToken.kind !== "name") this.fail("':' was expected");
            entries.push([key, { t: "name", name: key, line: keyToken.line }]); // {a, b}: auto-keys (since 1.19)
            if (!this.isOp(",")) break;
            this.advance();
            continue;
          }
        } else if (keyToken.kind === "str") {
          key = this.advance().value;
        } else if (keyToken.kind === "num") {
          key = numberText(this.advance().value);
        } else {
          this.fail("A dictionary key was expected");
        }
        this.expectOp(":");
        entries.push([key, this.assignment()]);
        if (!this.isOp(",")) break;
        this.advance();
      }
      this.expectOp("}");
      return { t: "dict", entries, line };
    }
  }

  function parse(source) {
    if (typeof source !== "string") throw new ArcadeError("An Arcade expression is text.");
    return new Parser(source).program();
  }

  // ---------------------------------------------------------------------------------------------------- types

  /** A Dictionary: keys are case-insensitive, as the Types page says; each keeps the case it was first given. */
  class ArcDict {
    constructor(entries = null, immutable = false) {
      this.map = new Map();
      this.immutable = false;
      if (entries) for (const [k, v] of (entries instanceof Map ? entries : Array.isArray(entries) ? entries : Object.entries(entries))) this.set(k, v);
      this.immutable = immutable;
    }
    get(key) { const found = this.map.get(String(key).toLowerCase()); return found === undefined ? null : found[1]; }
    has(key) { return this.map.has(String(key).toLowerCase()); }
    set(key, value) {
      if (this.immutable) throw new ArcadeError("This dictionary cannot be changed: it is immutable.");
      const lower = String(key).toLowerCase();
      const kept = this.map.get(lower);
      this.map.set(lower, [kept ? kept[0] : String(key), value === undefined ? null : value]);
    }
    erase(key) {
      if (this.immutable) throw new ArcadeError("This dictionary cannot be changed: it is immutable.");
      if (!this.has(key)) throw new ArcadeError(`The dictionary has no key '${key}'.`);
      this.map.delete(String(key).toLowerCase());
    }
    keys() { return [...this.map.values()].map(e => e[0]); }
    items() { return [...this.map.values()]; }
    get size() { return this.map.size; }
  }

  /** A geometry as ArcGIS JSON. Studio's engine reads its kind and properties; it builds no operation on it (D-476). */
  class ArcGeometry {
    constructor(json) {
      this.json = json || {};
      const j = this.json;
      this.kind = j.rings ? "Polygon" : j.paths ? "Polyline" : j.points ? "Multipoint"
        : (j.xmin !== undefined && j.xmax !== undefined) ? "Extent" : "Point";
    }
  }

  /**
   * A Feature: attributes and an optional geometry. A profile's $feature is immutable. `schema` is the layer's
   * description (its fields with their domains), so Schema, Domain, DomainName and DomainCode can answer.
   */
  class ArcFeature {
    constructor(attributes, geometry = null, immutable = false, schema = null) {
      this.attributes = attributes instanceof ArcDict ? attributes : new ArcDict(attributes || {});
      this.geometry = geometry;
      this.immutable = immutable;
      this.schema = schema;
    }
  }

  /** A Date: an instant (epoch milliseconds) and the time zone it is read in. */
  class ArcDate {
    constructor(ms, zone) { this.ms = ms; this.zone = zone; }
  }

  /** A DateOnly: a calendar day; `month` is 1–12 here (Arcade's functions number months from 0). */
  class ArcDateOnly {
    constructor(year, month, day) { this.year = year; this.month = month; this.day = day; }
    get epochUtc() { return Date.UTC(this.year, this.month - 1, this.day); }
  }

  /** A Time: milliseconds since midnight. */
  class ArcTime {
    constructor(ms) { this.ms = Math.trunc(ms); }
  }

  /** A function value: a user function (`node`) or a built-in (`call`). */
  class ArcFunction {
    constructor(name, node = null, call = null) { this.name = name; this.node = node; this.call = call; }
  }

  /** A FeatureSet stand-in: never built here, it exists so TypeOf and errors can name it. */

  const isNumber = v => typeof v === "number";

  // ---------------------------------------------------------------------------------------------------- time zones
  //
  // INFERRED (ADR-313 §7 f): an expression executes in the browser's time zone, as Map Viewer's runs in the client.
  // A zone is { kind: "local" }, { kind: "utc" }, { kind: "fixed", minutes } or { kind: "iana", name }.

  const UTC = Object.freeze({ kind: "utc" });
  const LOCAL = Object.freeze({ kind: "local" });
  const OFFSET = /^([+-])(\d{1,2})(?::?(\d{2}))?$/;
  const formatters = new Map();

  function zoneOf(name, execution) {
    if (name && typeof name === "object" && name.kind) return name;
    const text = toText(name).trim();
    const lower = text.toLowerCase();
    if (["utc", "z", "gmt", "unknown"].includes(lower)) return UTC;
    if (lower === "" || lower === "system" || lower === "local") return execution || LOCAL;
    const match = OFFSET.exec(text);
    if (match) {
      const minutes = Number(match[2]) * 60 + Number(match[3] || 0);
      return { kind: "fixed", minutes: match[1] === "-" ? -minutes : minutes };
    }
    try {
      new Intl.DateTimeFormat("en-US", { timeZone: text });
      return { kind: "iana", name: text };
    } catch {
      throw new ArcadeError(`The time zone '${text}' is not known here; UTC and offsets such as +07:00 are.`);
    }
  }

  function ianaFormatter(name) {
    let found = formatters.get(name);
    if (!found) {
      found = new Intl.DateTimeFormat("en-US", { timeZone: name, hourCycle: "h23", year: "numeric", month: "numeric",
        day: "numeric", hour: "numeric", minute: "numeric", second: "numeric" });
      formatters.set(name, found);
    }
    return found;
  }

  /** The zone's offset from UTC, in minutes, at an instant. */
  function offsetAt(zone, ms) {
    if (zone.kind === "utc") return 0;
    if (zone.kind === "fixed") return zone.minutes;
    if (zone.kind === "local") return -new Date(ms).getTimezoneOffset();
    const parts = {};
    for (const p of ianaFormatter(zone.name).formatToParts(new Date(ms))) parts[p.type] = p.value;
    const asUtc = Date.UTC(Number(parts.year), Number(parts.month) - 1, Number(parts.day), Number(parts.hour) % 24,
      Number(parts.minute), Number(parts.second));
    return Math.round((asUtc - (ms - (((ms % 1000) + 1000) % 1000))) / 60000);
  }

  /** An instant's calendar parts in a zone. */
  function partsOf(date) {
    const offset = offsetAt(date.zone, date.ms);
    const d = new Date(date.ms + offset * 60000);
    return { year: d.getUTCFullYear(), month: d.getUTCMonth(), day: d.getUTCDate(), hour: d.getUTCHours(),
      minute: d.getUTCMinutes(), second: d.getUTCSeconds(), millisecond: d.getUTCMilliseconds(),
      weekday: d.getUTCDay(), offset };
  }

  function utcMs(year, month, day = 1, hour = 0, minute = 0, second = 0, ms = 0) {
    const d = new Date(Date.UTC(2000, 0, 1));
    d.setUTCFullYear(year, month, day);
    d.setUTCHours(hour, minute, second, ms);
    return d.getTime();
  }

  /** The instant whose parts in a zone are these; out-of-range parts roll over as JavaScript's dates do. */
  function fromParts(zone, year, month, day = 1, hour = 0, minute = 0, second = 0, ms = 0) {
    const wall = utcMs(year, month, day, hour, minute, second, ms);
    let guess = wall - offsetAt(zone, wall) * 60000;
    const again = offsetAt(zone, guess);
    guess = wall - again * 60000;
    return new ArcDate(guess, zone);
  }

  function pad(n, width = 2) { return String(Math.abs(n)).padStart(width, "0"); }

  function offsetText(minutes) {
    return `${minutes >= 0 ? "+" : "-"}${pad(Math.trunc(Math.abs(minutes) / 60))}:${pad(Math.abs(minutes) % 60)}`;
  }

  function dateIso(date) {
    const p = partsOf(date);
    return `${p.year}-${pad(p.month + 1)}-${pad(p.day)}T${pad(p.hour)}:${pad(p.minute)}:${pad(p.second)}`
      + (p.millisecond ? `.${pad(p.millisecond, 3)}` : "") + offsetText(p.offset);
  }

  function dateOnlyIso(d) { return `${pad(d.year, 4)}-${pad(d.month)}-${pad(d.day)}`; }

  function timeIso(t) {
    const seconds = Math.floor(t.ms / 1000);
    const ms = t.ms % 1000;
    return `${pad(Math.floor(seconds / 3600))}:${pad(Math.floor(seconds / 60) % 60)}:${pad(seconds % 60)}`
      + (ms ? `.${pad(ms, 3)}` : "");
  }

  function zoneName(zone) {
    if (zone.kind === "utc") return "UTC";
    if (zone.kind === "fixed") return offsetText(zone.minutes);
    if (zone.kind === "iana") return zone.name;
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone || "local"; } catch { return "local"; }
  }

  // ---------------------------------------------------------------------------------------------------- casting

  /** A Number as text: integral values without a decimal point, NaN and Infinity by name. */
  function numberText(value) {
    if (Number.isNaN(value)) return "NaN";
    if (value === Infinity) return "Infinity";
    if (value === -Infinity) return "-Infinity";
    if (Object.is(value, -0)) return "0";
    return String(value);
  }

  const DECIMAL = /^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$/;

  /** Implicit Number casting, as the Type casting page tabulates it. */
  function toNumber(value) {
    if (typeof value === "boolean") return value ? 1 : 0;
    if (isNumber(value)) return value;
    if (value === null || value === undefined) return 0;
    if (typeof value === "string") {
      if (value.trim() === "") return 0;
      return DECIMAL.test(value) ? Number(value) : NaN;
    }
    if (value instanceof ArcDate) return value.ms;
    return NaN;
  }

  /** Implicit Boolean casting: a Number other than 0 and NaN, and lower-case 'true', are true. */
  function toBool(value) {
    if (typeof value === "boolean") return value;
    if (isNumber(value)) return !(value === 0 || Number.isNaN(value));
    if (typeof value === "string") return value === "true";
    return false;
  }

  /** Implicit Text casting: null is empty text; arrays, dictionaries, features and geometries are JSON. */
  function toText(value) {
    if (value === null || value === undefined) return "";
    if (typeof value === "string") return value;
    if (typeof value === "boolean") return value ? "true" : "false";
    if (isNumber(value)) return numberText(value);
    if (value instanceof ArcDate) return dateIso(value);
    if (value instanceof ArcDateOnly) return dateOnlyIso(value);
    if (value instanceof ArcTime) return timeIso(value);
    if (Array.isArray(value) || value instanceof ArcDict || value instanceof ArcFeature || value instanceof ArcGeometry) {
      return JSON.stringify(toJson(value));
    }
    if (value instanceof ArcFunction) return `function ${value.name}`;
    return String(value);
  }

  /** The JSON-able form of a value: what Text, Console and JSON text write. */
  function toJson(value) {
    if (value === null || value === undefined || typeof value === "string" || typeof value === "boolean") return value === undefined ? null : value;
    if (isNumber(value)) return Number.isFinite(value) ? value : null;
    if (Array.isArray(value)) return value.map(toJson);
    if (value instanceof ArcDict) return Object.fromEntries(value.items().map(([k, v]) => [k, toJson(v)]));
    if (value instanceof ArcFeature) {
      return { geometry: value.geometry ? value.geometry.json : null,
        attributes: Object.fromEntries(value.attributes.items().map(([k, v]) => [k, toJson(v)])) };
    }
    if (value instanceof ArcGeometry) return value.json;
    if (value instanceof ArcDate || value instanceof ArcDateOnly || value instanceof ArcTime) return toText(value);
    return toText(value);
  }

  /** A parsed JSON value as Arcade's: objects become Dictionaries. */
  function fromJson(value) {
    if (Array.isArray(value)) return value.map(fromJson);
    if (value && typeof value === "object") return new ArcDict(Object.entries(value).map(([k, v]) => [k, fromJson(v)]));
    return value === undefined ? null : value;
  }

  /** TypeOf's names. */
  function typeOf(value) {
    if (value === null || value === undefined) return "Null";
    if (typeof value === "boolean") return "Boolean";
    if (isNumber(value)) return "Number";
    if (typeof value === "string") return "String";
    if (Array.isArray(value)) return "Array";
    if (value instanceof ArcDict) return "Dictionary";
    if (value instanceof ArcFeature) return "Feature";
    if (value instanceof ArcGeometry) return value.kind;
    if (value instanceof ArcDate) return "Date";
    if (value instanceof ArcDateOnly) return "DateOnly";
    if (value instanceof ArcTime) return "Time";
    if (value instanceof ArcFunction) return "Function";
    return "Unrecognized Type";
  }

  const isObject = v => Array.isArray(v) || v instanceof ArcDict || v instanceof ArcFeature || v instanceof ArcGeometry
    || v instanceof ArcFunction;

  /** `==`: no coercion. Text, dates, numbers and booleans compare by value; objects by identity. */
  function equal(a, b) {
    if (a === null || a === undefined || b === null || b === undefined) return (a ?? null) === null && (b ?? null) === null;
    if (typeof a === "boolean" || typeof b === "boolean") return a === b;
    if (isNumber(a) && isNumber(b)) return a === b;
    if (typeof a === "string" && typeof b === "string") return a === b;
    if (a instanceof ArcDate && b instanceof ArcDate) return a.ms === b.ms;
    if (a instanceof ArcDateOnly && b instanceof ArcDateOnly) return a.epochUtc === b.epochUtc;
    if (a instanceof ArcTime && b instanceof ArcTime) return a.ms === b.ms;
    return a === b;
  }

  function scalar(value) {
    if (value instanceof ArcDate) return value.ms;
    if (value instanceof ArcDateOnly) return value.epochUtc;
    if (value instanceof ArcTime) return value.ms;
    return value;
  }

  function kindOf(value) {
    if (typeof value === "boolean") return "bool";
    if (isNumber(value)) return "number";
    if (typeof value === "string") return "text";
    if (value === null || value === undefined) return "null";
    return value.constructor ? value.constructor.name : "object";
  }

  /** `<`, `>`, `<=`, `>=`: objects answer false; two values of different types are both cast to numbers. */
  function compare(op, a, b) {
    if (isObject(a) || isObject(b)) return false;
    if ((a instanceof ArcTime && (b instanceof ArcDate || b instanceof ArcDateOnly))
      || (b instanceof ArcTime && (a instanceof ArcDate || a instanceof ArcDateOnly))) {
      throw new ArcadeError("A Date or DateOnly cannot be compared with a Time.");
    }
    const same = kindOf(a) === kindOf(b);
    let x;
    let y;
    if (same && typeof a === "string") { x = a; y = b; }
    else if (same && (a instanceof ArcDate || a instanceof ArcDateOnly || a instanceof ArcTime)) { x = scalar(a); y = scalar(b); }
    else { x = toNumber(a); y = toNumber(b); }
    if (op === "<") return x < y;
    if (op === ">") return x > y;
    if (op === "<=") return x <= y;
    return x >= y;
  }

  /** A condition or a logical operand: Boolean, with no casting, as the Type casting page says. */
  function requireBool(value, where) {
    if (typeof value !== "boolean") throw new ArcadeError(`${where} must be a Boolean; ${typeOf(value)} was given.`);
    return value;
  }

  function int32(value) {
    const number = toNumber(value);
    return Number.isFinite(number) ? number | 0 : 0;
  }

  function arithmetic(op, a, b) {
    if (op === "+") {
      if (typeof a === "string" || typeof b === "string") return toText(a) + toText(b);
      return toNumber(a) + toNumber(b);
    }
    const x = toNumber(a);
    const y = toNumber(b);
    switch (op) {
      case "-": return x - y;
      case "*": return x * y;
      case "/": return x / y;
      case "%": return x % y;
      case "&": return int32(a) & int32(b);
      case "|": return int32(a) | int32(b);
      case "^": return int32(a) ^ int32(b);
      case "<<": return int32(a) << (int32(b) & 31);
      case ">>": return int32(a) >> (int32(b) & 31);
      case ">>>": return int32(a) >>> (int32(b) & 31);
      default: throw new ArcadeError(`Unknown operator '${op}'.`);
    }
  }

  // ---------------------------------------------------------------------------------------------------- evaluation

  class Return { constructor(value) { this.value = value; } }
  const BREAK = { signal: "break" };
  const CONTINUE = { signal: "continue" };
  const MAX_DEPTH = 200;

  /**
   * An expression runs within a step budget, because a form evaluates as the reader types and a `while (true)` must
   * not hang the page. ADR-313 §5.1; the figure is ours (a form's expressions are short).
   */
  const MAX_STEPS = 1000000;

  class Frame {
    constructor() { this.names = new Map(); this.last = null; }
  }

  class Interpreter {
    constructor(profile, functions, options) {
      this.profile = new Map(Object.entries(profile || {}).map(([k, v]) => [k.toLowerCase(), v]));
      this.functions = functions;
      this.zone = options.zone;
      this.refusedVariables = options.refusedVariables || {};
      this.globals = new Frame();
      this.frames = [this.globals];
      this.line = null;
      this.console = [];
      this.steps = 0;
    }

    run(program) {
      for (const [name, value] of Object.entries(constants())) this.globals.names.set(name.toLowerCase(), value);
      try {
        this.block(program.body);
      } catch (e) {
        if (e instanceof Return) return e.value;
        if (e === BREAK || e === CONTINUE) throw new ArcadeError("'break' and 'continue' belong inside a loop.", this.line);
        if (e instanceof ArcadeError) {
          if (e.line === null && this.line !== null) {
            const Kind = e.constructor;
            throw new Kind(e.arcadeMessage, this.line);
          }
          throw e;
        }
        if (e instanceof RangeError) throw new ArcadeError("The expression recursed too deeply.", this.line);
        throw e;
      }
      return this.globals.last;
    }

    tick() {
      if (++this.steps > MAX_STEPS) throw new ArcadeError("The expression ran too long: it took more than a million steps.");
    }

    block(statements) { for (const s of statements) this.statement(s); }

    statement(node) {
      this.tick();
      if (node.line !== undefined) this.line = node.line;
      const frame = this.frames[this.frames.length - 1];
      switch (node.t) {
        case "expr": frame.last = this.evaluate(node.value); break;
        case "var":
          for (const [name, value] of node.declared) frame.names.set(name.toLowerCase(), value === null ? null : this.evaluate(value));
          break;
        case "block": this.block(node.body); break;
        case "if":
          if (requireBool(this.evaluate(node.test), "An if condition")) this.statement(node.then);
          else if (node.otherwise) this.statement(node.otherwise);
          break;
        case "for": this.forLoop(node); break;
        case "forin": this.forIn(node); break;
        case "while":
          while (requireBool(this.evaluate(node.test), "A while condition")) {
            try { this.statement(node.body); } catch (e) {
              if (e === BREAK) break;
              if (e === CONTINUE) continue;
              throw e;
            }
          }
          break;
        case "return": throw new Return(node.value === null ? null : this.evaluate(node.value));
        case "break": throw BREAK;
        case "continue": throw CONTINUE;
        case "function": frame.names.set(node.name.toLowerCase(), new ArcFunction(node.name, node)); break;
        case "empty": break;
        default: throw new ArcadeError(`Unknown statement '${node.t}'.`, this.line);
      }
    }

    forLoop(node) {
      if (node.init) this.statement(node.init);
      while (node.test === null || requireBool(this.evaluate(node.test), "A for condition")) {
        try { this.statement(node.body); } catch (e) {
          if (e === BREAK) break;
          if (e !== CONTINUE) throw e;
        }
        if (node.update) this.evaluate(node.update);
      }
    }

    forIn(node) {
      const collection = this.evaluate(node.iterable);
      let items;
      if (Array.isArray(collection) || typeof collection === "string") {
        items = node.of ? [...collection] : [...collection].map((_, i) => i);
        if (typeof collection === "string" && node.of) items = collection.split("");
      } else if (collection instanceof ArcDict || collection instanceof ArcFeature) {
        const pairs = collection instanceof ArcDict ? collection.items() : collection.attributes.items();
        items = node.of ? pairs.map(([k, v]) => new ArcDict([["key", k], ["value", v]])) : pairs.map(([k]) => k);
      } else if (collection === null) {
        items = [];
      } else {
        throw new ArcadeError(`A ${typeOf(collection)} cannot be iterated by for…${node.of ? "of" : "in"}.`, node.line);
      }
      if (node.declare) this.frames[this.frames.length - 1].names.set(node.name.toLowerCase(), null);
      for (const item of items) {
        this.assignName(node.name, item);
        try { this.statement(node.body); } catch (e) {
          if (e === BREAK) break;
          if (e === CONTINUE) continue;
          throw e;
        }
      }
    }

    lookup(name, line) {
      const lower = name.toLowerCase();
      if (lower.startsWith("$")) {
        if (this.profile.has(lower)) return this.profile.get(lower);
        const why = this.refusedVariables[lower];
        if (why) throw new NotBuiltFunction(`The profile variable '${name}' is not available in Studio's form engine: ${why}.`, line);
        throw new ArcadeError(`The profile variable '${name}' is not available here.`, line);
      }
      const frame = this.frames[this.frames.length - 1];
      if (frame.names.has(lower)) return frame.names.get(lower);
      if (this.globals.names.has(lower)) return this.globals.names.get(lower);
      if (this.functions.has(lower)) return this.functions.get(lower);
      throw new ArcadeError(`'${name}' is not declared.`, line);
    }

    assignName(name, value) {
      const lower = name.toLowerCase();
      if (lower.startsWith("$")) throw new ArcadeError(`The profile variable '${name}' cannot be assigned to.`, this.line);
      const frame = this.frames[this.frames.length - 1];
      if (frame.names.has(lower)) frame.names.set(lower, value);
      else if (this.globals.names.has(lower)) this.globals.names.set(lower, value);
      else throw new ArcadeError(`'${name}' is not declared; declare it with var.`, this.line);
    }

    evaluate(node) {
      this.tick();
      switch (node.t) {
        case "lit": return node.value;
        case "name": return this.lookup(node.name, node.line);
        case "tmpl": return node.parts.map(p => typeof p === "string" ? p : toText(this.evaluate(p))).join("");
        case "array": return node.items.map(i => this.evaluate(i));
        case "dict": {
          const out = new ArcDict();
          for (const [key, value] of node.entries) out.set(key, this.evaluate(value));
          return out;
        }
        case "member": return this.member(this.evaluate(node.object), node.name, node.safe, node.line);
        case "index": return this.index(this.evaluate(node.object), this.evaluate(node.index), node.safe, node.line);
        case "call": return this.call(node);
        case "unary": {
          const value = this.evaluate(node.value);
          if (node.op === "!") return !requireBool(value, "The operand of !");
          if (node.op === "-") return -toNumber(value);
          if (node.op === "+") return toNumber(value);
          return ~int32(value);
        }
        case "update": {
          const old = this.evaluate(node.target);
          const before = isNumber(old) ? old : NaN; // ++ and -- do not cast: anything else is NaN
          const after = before + (node.op === "++" ? 1 : -1);
          this.store(node.target, after);
          return node.prefix ? after : before;
        }
        case "and": return requireBool(this.evaluate(node.left), "An operand of &&") && requireBool(this.evaluate(node.right), "An operand of &&");
        case "or": return requireBool(this.evaluate(node.left), "An operand of ||") || requireBool(this.evaluate(node.right), "An operand of ||");
        case "bin": {
          const a = this.evaluate(node.left);
          const b = this.evaluate(node.right);
          if (node.op === "==") return equal(a, b);
          if (node.op === "!=") return !equal(a, b);
          if (["<", ">", "<=", ">="].includes(node.op)) return compare(node.op, a, b);
          return arithmetic(node.op, a, b);
        }
        case "assign": {
          let value = this.evaluate(node.value);
          if (node.op !== "=") value = arithmetic(node.op[0], this.evaluate(node.target), value);
          this.store(node.target, value);
          return value;
        }
        default: throw new ArcadeError(`Unknown expression '${node.t}'.`, this.line);
      }
    }

    store(target, value) {
      if (target.t === "name") { this.assignName(target.name, value); return; }
      const holder = this.evaluate(target.object);
      const key = target.t === "member" ? target.name : this.evaluate(target.index);
      if (Array.isArray(holder)) {
        if (target.t === "member" || !isNumber(key) || !Number.isInteger(key)) throw new ArcadeError("An array is indexed by a whole number.", this.line);
        let at = key < 0 ? key + holder.length : key;
        if (at === holder.length) holder.push(value);
        else if (at >= 0 && at < holder.length) holder[at] = value;
        else throw new ArcadeError(`Index ${key} is out of the array's bounds; an array cannot have missing elements.`, this.line);
      } else if (holder instanceof ArcDict) {
        holder.set(toText(key), value);
      } else if (holder instanceof ArcFeature) {
        if (holder.immutable) throw new ArcadeError("This feature cannot be changed: a profile's feature is immutable.", this.line);
        holder.attributes.set(toText(key), value);
      } else if (holder === null) {
        throw new ArcadeError("A member of null cannot be assigned to.", this.line);
      } else {
        throw new ArcadeError(`A ${typeOf(holder)} cannot be assigned to by member.`, this.line);
      }
    }

    member(holder, name, safe, line) {
      if (holder === null || holder === undefined) {
        if (safe) return null;
        throw new ArcadeError(`Cannot read '${name}' of null.`, line);
      }
      if (holder instanceof ArcDict) {
        if (holder.has(name)) return holder.get(name);
        if (safe) return null;
        throw new ArcadeError(`The dictionary has no key '${name}'.`, line);
      }
      if (holder instanceof ArcFeature) {
        if (holder.attributes.has(name)) return holder.attributes.get(name);
        if (safe) return null;
        throw new ArcadeError(`The feature has no field '${name}'.`, line);
      }
      if (holder instanceof ArcGeometry) {
        const found = Object.keys(holder.json).find(k => k.toLowerCase() === String(name).toLowerCase());
        if (found !== undefined) return fromJson(holder.json[found]);
        if (safe) return null;
        throw new ArcadeError(`A ${holder.kind} has no property '${name}'.`, line);
      }
      if (holder instanceof ArcDate || holder instanceof ArcDateOnly || holder instanceof ArcTime) {
        throw new ArcadeError(`A ${typeOf(holder)} has no members; use the date functions.`, line);
      }
      if (safe) return null;
      throw new ArcadeError(`A ${typeOf(holder)} has no member '${name}'.`, line);
    }

    index(holder, key, safe, line) {
      if (holder === null || holder === undefined) {
        if (safe) return null;
        throw new ArcadeError("Cannot index null.", line);
      }
      if (Array.isArray(holder) || typeof holder === "string") {
        if (!isNumber(key) || !Number.isInteger(key)) {
          throw new ArcadeError(`A${Array.isArray(holder) ? "n array" : " text value"} is indexed by a whole number.`, line);
        }
        const at = key < 0 ? key + holder.length : key;
        if (at < 0 || at >= holder.length) {
          if (safe) return null;
          throw new ArcadeError(`Index ${key} is out of bounds.`, line);
        }
        return holder[at];
      }
      return this.member(holder, toText(key), safe, line);
    }

    call(node) {
      let target = null;
      if (node.callee.t === "name") {
        // A variable named like a function (`var count = Count(x)`) does not hide it: a call looks for a function.
        const lower = node.callee.name.toLowerCase();
        for (const frame of [this.frames[this.frames.length - 1], this.globals]) {
          if (frame.names.get(lower) instanceof ArcFunction) { target = frame.names.get(lower); break; }
        }
        if (!target) target = this.functions.get(lower) || null;
        if (!target) {
          for (const frame of [this.frames[this.frames.length - 1], this.globals]) {
            if (frame.names.has(lower)) throw new ArcadeError(`'${node.callee.name}' is a ${typeOf(frame.names.get(lower))}, not a function.`, node.line);
          }
          notBuilt(node.callee.name, node.line);
        }
      } else {
        target = this.evaluate(node.callee);
      }
      const args = node.args.map(a => this.evaluate(a));
      if (!(target instanceof ArcFunction)) throw new ArcadeError(`A ${typeOf(target)} is not a function.`, node.line);
      this.line = node.line;
      return this.invoke(target, args);
    }

    invoke(fn, args) {
      if (fn.call) return fn.call(this, args);
      const { name, params, body } = fn.node;
      if (args.length !== params.length) {
        throw new ArcadeError(`The function '${name}' takes ${params.length} argument${params.length !== 1 ? "s" : ""}; `
          + `${args.length} ${args.length !== 1 ? "were" : "was"} given.`, this.line);
      }
      if (this.frames.length > MAX_DEPTH) throw new ArcadeError("The expression recursed too deeply.", this.line);
      const frame = new Frame();
      params.forEach((p, i) => frame.names.set(p.toLowerCase(), args[i]));
      this.frames.push(frame);
      const calledFrom = this.line;
      try {
        this.block(body.body);
      } catch (e) {
        if (e instanceof Return) return e.value;
        if (e === BREAK || e === CONTINUE) throw new ArcadeError("'break' and 'continue' belong inside a loop.", this.line);
        throw e;
      } finally {
        this.frames.pop();
        this.line = calledFrom;
      }
      return frame.last;
    }
  }

  // ---------------------------------------------------------------------------------------------------- the library

  const REGISTRY = new Map(); // lower-case name -> [{ accepts, fn }]
  const CANONICAL = new Map();

  function builtin(name, fn, accepts = null) {
    const lower = name.toLowerCase();
    const entries = REGISTRY.get(lower) || [];
    entries.push({ accepts, fn });
    entries.sort((a, b) => (a.accepts === null) - (b.accepts === null));
    REGISTRY.set(lower, entries);
    CANONICAL.set(lower, name);
  }

  function functionTable() {
    const table = new Map();
    for (const [lower, entries] of REGISTRY) {
      const name = CANONICAL.get(lower);
      table.set(lower, new ArcFunction(name, null, (interpreter, args) => {
        for (const { accepts, fn } of entries) if (!accepts || accepts(args)) return fn(interpreter, args);
        return notBuilt(name, interpreter.line, args);
      }));
    }
    return table;
  }

  function constants() {
    return {
      PI: Math.PI, Infinity, NaN,
      TextFormatting: new ArcDict({ NewLine: "\n", Tab: "\t", SingleQuote: "'", DoubleQuote: '"', BackwardSlash: "\\",
        ForwardSlash: "/", LeftParenthesis: "(", RightParenthesis: ")", LeftSquareBracket: "[", RightSquareBracket: "]",
        LeftCurlyBracket: "{", RightCurlyBracket: "}", LessThan: "<", GreaterThan: ">", NewParagraph: "\n\n" }, true),
    };
  }

  function arity(name, args, low, high = low) {
    if (args.length < low || (high >= 0 && args.length > high)) {
      const wanted = low === high ? `${low}` : high < 0 ? `at least ${low}` : `${low} to ${high}`;
      throw new ArcadeError(`${name} takes ${wanted} argument${wanted !== "1" ? "s" : ""}; ${args.length} `
        + `${args.length !== 1 ? "were" : "was"} given.`);
    }
  }

  function callFn(interpreter, fn, ...args) {
    if (!(fn instanceof ArcFunction)) throw new ArcadeError(`A function was expected; a ${typeOf(fn)} was given.`);
    return interpreter.invoke(fn, args);
  }

  const firstIs = test => args => args.length > 0 && test(args[0]);
  const numbersOf = args => (args.length === 1 && Array.isArray(args[0]) ? args[0] : args).map(toNumber);

  // ---- math

  function unary(name, fn) {
    builtin(name, (_, args) => { arity(name, args, 1); return fn(toNumber(args[0])); });
  }
  unary("Abs", Math.abs);
  unary("Acos", Math.acos);
  unary("Asin", Math.asin);
  unary("Atan", Math.atan);
  unary("Cos", Math.cos);
  unary("Sin", Math.sin);
  unary("Tan", Math.tan);
  unary("Exp", Math.exp);
  unary("Sqrt", Math.sqrt);
  builtin("Atan2", (_, args) => { arity("Atan2", args, 2); return Math.atan2(toNumber(args[0]), toNumber(args[1])); });
  builtin("Log", (_, args) => {
    arity("Log", args, 1, 2);
    const value = toNumber(args[0]);
    if (args.length === 1) return Math.log(value);
    const base = toNumber(args[1]);
    if (value <= 0 || base <= 0 || base === 1 || Number.isNaN(value) || Number.isNaN(base)) return NaN;
    return Math.log(value) / Math.log(base);
  });
  builtin("Pow", (_, args) => { arity("Pow", args, 2); return Math.pow(toNumber(args[0]), toNumber(args[1])); });
  builtin("Constrain", (_, args) => {
    arity("Constrain", args, 3);
    const [value, low, high] = args.map(toNumber);
    return Math.min(Math.max(value, low), high);
  });

  /** A value shifted by a power of ten through its decimal text, so 2316.2562 rounds by its digits, not binary noise. */
  function shift(value, places) {
    const [mantissa, exponent] = String(value).split("e");
    return Number(`${mantissa}e${Number(exponent || 0) + places}`);
  }

  function rounded(value, places, mode) {
    if (!Number.isFinite(value)) return value;
    const p = Math.trunc(places);
    // INFERRED (ADR-235 §5.8): a half rounds toward +infinity, as Round(2.5) = 3 and Round(-2.5) = -2.
    const fn = mode === "round" ? Math.round : mode === "ceil" ? Math.ceil : Math.floor;
    const out = shift(fn(shift(value, p)), -p);
    return Object.is(out, -0) ? 0 : out;
  }
  for (const [name, mode] of [["Round", "round"], ["Ceil", "ceil"], ["Floor", "floor"]]) {
    builtin(name, (_, args) => { arity(name, args, 1, 2); return rounded(toNumber(args[0]), args.length > 1 ? toNumber(args[1]) : 0, mode); });
  }
  builtin("Sign", (_, args) => {
    arity("Sign", args, 1);
    const value = typeof args[0] === "string" && !args[0].trim() ? NaN : toNumber(args[0]);
    if (Number.isNaN(value)) return NaN;
    if (value === 0) return value; // -0 stays -0, as documented
    return value > 0 ? 1 : -1;
  });
  builtin("Random", (_, args) => { arity("Random", args, 0); return Math.random(); });

  const mean = values => values.length ? values.reduce((a, b) => a + b, 0) / values.length : NaN;
  const variance = values => {
    if (!values.length) return NaN;
    const m = mean(values);
    return values.reduce((a, v) => a + (v - m) ** 2, 0) / values.length; // the population variance, as documented
  };
  builtin("Sum", (_, args) => numbersOf(args).reduce((a, b) => a + b, 0));
  builtin("Average", (_, args) => mean(numbersOf(args)));
  builtin("Mean", (_, args) => mean(numbersOf(args)));
  builtin("Max", (_, args) => { const v = numbersOf(args); return v.length ? Math.max(...v) : NaN; });
  builtin("Min", (_, args) => { const v = numbersOf(args); return v.length ? Math.min(...v) : NaN; });
  builtin("Variance", (_, args) => variance(numbersOf(args)));
  builtin("Stdev", (_, args) => { const v = numbersOf(args); return v.length ? Math.sqrt(variance(v)) : NaN; });
  builtin("Product", (_, args) => {
    const values = args.length === 1 && Array.isArray(args[0]) ? args[0] : args;
    let out = 1;
    for (const v of values) {
      if (!isNumber(v)) return NaN; // Product([[1,2],[3,1]]) and Product([{a: 10}]) are NaN, as documented
      out *= v;
    }
    return out;
  });
  builtin("Number", (_, args) => {
    arity("Number", args, 1, 2);
    if (args.length === 2 && args[1] !== null && args[1] !== "") {
      throw new NotBuiltFunction("The Arcade function Number is not built in Studio's form engine for a text pattern; "
        + "Number(value) is.");
    }
    const value = args[0];
    if (value instanceof ArcDate) return value.ms;
    if (value instanceof ArcDateOnly) return value.epochUtc;
    if (value instanceof ArcTime) return value.ms;
    return toNumber(value);
  });

  // ---- logical

  const empty = v => v === null || v === undefined || v === "";

  builtin("Boolean", (_, args) => { arity("Boolean", args, 1); return toBool(args[0]); });
  builtin("IIf", (_, args) => { arity("IIf", args, 3); return toBool(args[0]) ? args[1] : args[2]; });
  builtin("When", (_, args) => {
    if (args.length < 3 || args.length % 2 === 0) throw new ArcadeError("When takes pairs of a condition and a result, then a default value.");
    for (let at = 0; at < args.length - 1; at += 2) if (toBool(args[at])) return args[at + 1];
    return args[args.length - 1];
  });
  builtin("Decode", (_, args) => {
    if (args.length < 4 || args.length % 2 !== 0) throw new ArcadeError("Decode takes an expression, pairs of a value to compare and a result, then a default.");
    for (let at = 1; at < args.length - 1; at += 2) if (equal(args[0], args[at])) return args[at + 1];
    return args[args.length - 1];
  });
  builtin("IsEmpty", (_, args) => {
    arity("IsEmpty", args, 1, 2);
    if (empty(args[0])) return true;
    if (args.length === 2 && toBool(args[1])) {
      const v = args[0];
      if (Array.isArray(v)) return v.length === 0;
      if (v instanceof ArcDict) return v.size === 0;
      if (v instanceof ArcFeature) return v.geometry === null && v.attributes.size === 0;
    }
    return false;
  });
  builtin("IsNan", (_, args) => { arity("IsNan", args, 1); return Number.isNaN(toNumber(args[0])); });
  builtin("Equals", (_, args) => { arity("Equals", args, 2); return equal(args[0], args[1]); });
  builtin("TypeOf", (_, args) => { arity("TypeOf", args, 1); return typeOf(args[0]); });

  /** [found, value] one level into a container. */
  function step(container, key) {
    if (Array.isArray(container)) {
      if (!isNumber(key) || !Number.isInteger(key)) return [false, null];
      const at = key < 0 ? key + container.length : key;
      return at >= 0 && at < container.length ? [true, container[at]] : [false, null];
    }
    if (container instanceof ArcDict) return container.has(toText(key)) ? [true, container.get(key)] : [false, null];
    if (container instanceof ArcFeature) return container.attributes.has(toText(key)) ? [true, container.attributes.get(key)] : [false, null];
    if (container instanceof ArcGeometry) {
      const found = Object.keys(container.json).find(k => k.toLowerCase() === toText(key).toLowerCase());
      return found !== undefined ? [true, fromJson(container.json[found])] : [false, null];
    }
    return [false, null];
  }

  function path(container, keys) {
    let value = container;
    for (const key of Array.isArray(keys) ? keys : [keys]) {
      const [found, next] = step(value, key);
      if (!found) return [false, null];
      value = next;
    }
    return [true, value];
  }

  builtin("DefaultValue", (_, args) => {
    arity("DefaultValue", args, 2, 3);
    if (args.length === 2) return empty(args[0]) ? args[1] : args[0];
    const [container, keys, fallback] = args;
    if (!(Array.isArray(container) || container instanceof ArcDict || container instanceof ArcFeature || container instanceof ArcGeometry)) {
      throw new ArcadeError(`DefaultValue with a key takes an array, dictionary, feature or geometry; a ${typeOf(container)} was given.`);
    }
    const [found, value] = path(container, keys);
    return found && !empty(value) ? value : fallback;
  });
  builtin("HasValue", (_, args) => {
    arity("HasValue", args, 2);
    const [found, value] = path(args[0], args[1]);
    return found && value !== null && value !== undefined;
  });
  builtin("HasKey", (_, args) => {
    arity("HasKey", args, 2);
    const [container, key] = args;
    if (container instanceof ArcDict) return container.has(toText(key));
    if (container instanceof ArcFeature) return container.attributes.has(toText(key));
    if (container instanceof ArcGeometry) return step(container, key)[0];
    throw new ArcadeError(`HasKey takes a dictionary, feature or geometry; a ${typeOf(container)} was given.`);
  });

  // ---- text

  builtin("Count", (_, args) => {
    arity("Count", args, 1);
    const v = args[0];
    if (Array.isArray(v)) return v.length;
    if (v instanceof ArcDict) return v.size;
    if (v instanceof ArcFeature) return v.attributes.size;
    return toText(v).length;
  });
  builtin("Concatenate", (_, args) => {
    if (!args.length) return "";
    if (Array.isArray(args[0])) {
      arity("Concatenate", args, 1, 3);
      const separator = args.length > 1 ? toText(args[1]) : "";
      const pattern = args.length > 2 ? args[2] : null;
      return args[0].map(v => (pattern !== null && pattern !== "" ? formatText(v, pattern) : toText(v))).join(separator);
    }
    // INFERRED (ADR-235 §5.8): a first argument that is not an array joins every argument with no separator.
    return args.map(toText).join("");
  });
  builtin("Find", (_, args) => {
    arity("Find", args, 2, 3);
    const start = args.length > 2 ? Math.trunc(toNumber(args[2])) : 0;
    return toText(args[1]).indexOf(toText(args[0]), Math.max(start, 0));
  });
  builtin("Left", (_, args) => { arity("Left", args, 2); return toText(args[0]).slice(0, Math.max(Math.trunc(toNumber(args[1])), 0)); });
  builtin("Right", (_, args) => {
    arity("Right", args, 2);
    const count = Math.trunc(toNumber(args[1]));
    const text = toText(args[0]);
    return count > 0 ? text.slice(Math.max(text.length - count, 0)) : "";
  });
  builtin("Mid", (_, args) => {
    arity("Mid", args, 2, 3);
    const text = toText(args[0]);
    const start = Math.max(Math.trunc(toNumber(args[1])), 0);
    return args.length === 2 ? text.slice(start) : text.slice(start, start + Math.max(Math.trunc(toNumber(args[2])), 0));
  });
  builtin("Lower", (_, args) => { arity("Lower", args, 1); return toText(args[0]).toLowerCase(); });
  builtin("Upper", (_, args) => { arity("Upper", args, 1); return toText(args[0]).toUpperCase(); });
  builtin("Trim", (_, args) => { arity("Trim", args, 1); return toText(args[0]).replace(/^ +| +$/g, ""); });
  builtin("Proper", (_, args) => {
    // INFERRED (ADR-235 §5.8): letters after the first of a word are made lower case; a word begins after whitespace.
    arity("Proper", args, 1, 2);
    const text = toText(args[0]);
    const which = args.length > 1 ? toText(args[1]).toLowerCase() : "everyword";
    if (which !== "everyword" && which !== "firstword") throw new ArcadeError("Proper's second argument is 'everyword' or 'firstword'.");
    if (which === "firstword") return text.slice(0, 1).toUpperCase() + text.slice(1).toLowerCase();
    return text.replace(/(^|\s)(\S)(\S*)/g, (_m, a, b, c) => a + b.toUpperCase() + c.toLowerCase());
  });
  builtin("Replace", (_, args) => {
    arity("Replace", args, 3, 4);
    const every = args.length > 3 ? toBool(args[3]) : true;
    const [text, find, put] = args.slice(0, 3).map(toText);
    if (find === "") return text;
    return every ? text.split(find).join(put) : text.replace(find, () => put);
  });
  builtin("Split", (_, args) => {
    arity("Split", args, 2, 4);
    const text = toText(args[0]);
    const separator = toText(args[1]);
    const limit = args.length > 2 && args[2] !== null ? Math.trunc(toNumber(args[2])) : -1;
    const remove = args.length > 3 ? toBool(args[3]) : false;
    let parts = separator === "" ? [...text] : text.split(separator);
    if (remove) parts = parts.filter(p => p !== "");
    return limit >= 0 ? parts.slice(0, limit) : parts;
  });
  builtin("StandardizeFilename", (_, args) => {
    arity("StandardizeFilename", args, 1);
    let text = toText(args[0]);
    for (const [bad, good] of [["/", "-"], ["\\", "-"], ["|", "-"], ["*", "_"], ["<", "_"], [">", "_"], ["?", "_"], [":", ", "]]) {
      text = text.split(bad).join(good);
    }
    return text;
  });

  const GUID_FORMATS = ["digits", "digits-hyphen", "digits-hyphen-braces", "digits-hyphen-parentheses"];
  function guidText(hex, form) {
    const f = String(form).toLowerCase();
    if (!GUID_FORMATS.includes(f)) throw new ArcadeError(`'${form}' is not a GUID format: digits, digits-hyphen, digits-hyphen-braces or digits-hyphen-parentheses.`);
    const hyphen = `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    return f === "digits" ? hex : f === "digits-hyphen" ? hyphen : f === "digits-hyphen-braces" ? `{${hyphen}}` : `(${hyphen})`;
  }
  function randomHex() {
    const bytes = new Uint8Array(16);
    const crypto = root.crypto || (typeof globalThis !== "undefined" ? globalThis.crypto : null);
    if (crypto && crypto.getRandomValues) crypto.getRandomValues(bytes);
    else for (let i = 0; i < 16; i++) bytes[i] = Math.floor(Math.random() * 256);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    return [...bytes].map(b => b.toString(16).padStart(2, "0")).join("");
  }
  builtin("Guid", (_, args) => { arity("Guid", args, 0, 1); return guidText(randomHex(), args.length ? toText(args[0]) : "digits-hyphen-braces"); });
  builtin("StandardizeGuid", (_, args) => {
    arity("StandardizeGuid", args, 2);
    const hex = toText(args[0]).trim().replace(/^[{(]|[})]$/g, "").replace(/-/g, "").toLowerCase();
    if (!/^[0-9a-f]{32}$/.test(hex)) return "";
    return guidText(hex, toText(args[1]));
  });
  builtin("Text", (_, args) => { arity("Text", args, 1, 2); return formatText(args[0], args.length > 1 ? args[1] : null); });
  builtin("ToCharCode", (_, args) => {
    arity("ToCharCode", args, 1, 2);
    const text = toText(args[0]);
    const at = args.length > 1 ? Math.trunc(toNumber(args[1])) : 0;
    return at >= 0 && at < text.length ? text.charCodeAt(at) : NaN;
  });
  builtin("ToCodePoint", (_, args) => {
    arity("ToCodePoint", args, 1, 2);
    const text = toText(args[0]);
    const at = args.length > 1 ? Math.trunc(toNumber(args[1])) : 0;
    return at >= 0 && at < text.length ? text.codePointAt(at) : NaN;
  });
  builtin("FromCharCode", (_, args) => String.fromCharCode(...args.map(a => Math.trunc(toNumber(a)))));
  builtin("FromCodePoint", (_, args) => {
    try { return String.fromCodePoint(...args.map(a => Math.trunc(toNumber(a)))); } catch {
      throw new ArcadeError("FromCodePoint takes code points from 0 to 0x10FFFF.");
    }
  });
  builtin("ToHex", (_, args) => {
    arity("ToHex", args, 1);
    const value = toNumber(args[0]);
    if (!Number.isFinite(value)) return "NaN";
    const number = Math.trunc(value);
    return (number < 0 ? "-" : "") + Math.abs(number).toString(16).toUpperCase();
  });
  builtin("UrlEncode", (_, args) => {
    // A dictionary is written key=value pairs joined by &; INFERRED (ADR-235 §5.8): ordered as GetKeys orders them.
    arity("UrlEncode", args, 1);
    if (args[0] instanceof ArcDict) {
      return args[0].items().sort((a, b) => a[0].toLowerCase().localeCompare(b[0].toLowerCase()))
        .map(([k, v]) => `${encodeURIComponent(toText(k))}=${encodeURIComponent(toText(v))}`).join("&");
    }
    return encodeURIComponent(toText(args[0]));
  });

  // ---- arrays

  function arrayOf(name, value) {
    if (!Array.isArray(value)) throw new ArcadeError(`${name} takes an array; a ${typeOf(value)} was given.`);
    return value;
  }
  function copy(value, deep) {
    if (Array.isArray(value)) return value.map(v => (deep ? copy(v, true) : v));
    if (value instanceof ArcDict) return new ArcDict(value.items().map(([k, v]) => [k, deep ? copy(v, true) : v]));
    return value;
  }
  builtin("Array", (_, args) => {
    arity("Array", args, 1, 2);
    if (Array.isArray(args[0])) return copy(args[0], args.length > 1 && toBool(args[1]));
    const length = toNumber(args[0]);
    if (!Number.isInteger(length) || length < 0) throw new ArcadeError("Array's length is a whole number of zero or more.");
    return Array.from({ length }, () => (args.length > 1 ? args[1] : null));
  });
  builtin("Back", (_, args) => {
    arity("Back", args, 1);
    const values = arrayOf("Back", args[0]);
    if (!values.length) throw new ArcadeError("Back of an empty array.");
    return values[values.length - 1];
  });
  builtin("Front", (_, args) => {
    arity("Front", args, 1);
    const values = arrayOf("Front", args[0]);
    if (!values.length) throw new ArcadeError("Front of an empty array.");
    return values[0];
  });
  builtin("First", (_, args) => { arity("First", args, 1); return args[0].length ? args[0][0] : null; }, firstIs(Array.isArray));
  for (const name of ["All", "Any", "None"]) {
    builtin(name, (interpreter, args) => {
      arity(name, args, 2);
      const passed = args[0].map(v => toBool(callFn(interpreter, args[1], v)));
      if (name === "All") return passed.every(Boolean);
      if (name === "Any") return passed.some(Boolean);
      return !passed.some(Boolean);
    }, firstIs(Array.isArray));
  }
  builtin("Filter", (interpreter, args) => { arity("Filter", args, 2); return args[0].filter(v => toBool(callFn(interpreter, args[1], v))); }, firstIs(Array.isArray));
  builtin("Map", (interpreter, args) => { arity("Map", args, 2); return arrayOf("Map", args[0]).map(v => callFn(interpreter, args[1], v)); });
  builtin("Reduce", (interpreter, args) => {
    arity("Reduce", args, 2, 3);
    const values = [...arrayOf("Reduce", args[0])];
    let accumulated;
    if (args.length === 3) accumulated = args[2];
    else if (values.length) accumulated = values.shift();
    else return null;
    for (const v of values) accumulated = callFn(interpreter, args[1], accumulated, v);
    return accumulated;
  });
  function distinct(values) {
    const out = [];
    for (const v of values) if (!out.some(k => equal(v, k))) out.push(v);
    return out;
  }
  builtin("Distinct", (_, args) => distinct(args.length === 1 && Array.isArray(args[0]) ? args[0] : args));
  builtin("Erase", (_, args) => {
    arity("Erase", args, 2);
    const values = args[0];
    let at = Math.trunc(toNumber(args[1]));
    if (at < 0) at += values.length;
    if (at < 0 || at >= values.length) throw new ArcadeError("Erase: the index is out of the array's bounds.");
    values.splice(at, 1);
    return null;
  }, firstIs(Array.isArray));
  builtin("Insert", (_, args) => {
    arity("Insert", args, 3);
    const values = args[0];
    let at = Math.trunc(toNumber(args[1]));
    if (at < 0) at += values.length;
    if (at < 0 || at > values.length) throw new ArcadeError("Insert: the index is out of the array's bounds.");
    values.splice(at, 0, args[2]);
    return null;
  }, firstIs(Array.isArray));
  builtin("Includes", (_, args) => { arity("Includes", args, 2); return arrayOf("Includes", args[0]).some(v => equal(v, args[1])); });
  builtin("IndexOf", (_, args) => { arity("IndexOf", args, 2); return arrayOf("IndexOf", args[0]).findIndex(v => equal(v, args[1])); });
  builtin("Pop", (_, args) => {
    arity("Pop", args, 1);
    const values = arrayOf("Pop", args[0]);
    if (!values.length) throw new ArcadeError("Pop of an empty array.");
    return values.pop();
  });
  builtin("Push", (_, args) => { arity("Push", args, 2); const values = arrayOf("Push", args[0]); values.push(args[1]); return values.length; });
  builtin("Resize", (_, args) => {
    arity("Resize", args, 2, 3);
    const values = arrayOf("Resize", args[0]);
    const size = Math.trunc(toNumber(args[1]));
    if (size < 0) throw new ArcadeError("Resize: the size is zero or more.");
    if (values.length > size) values.length = size;
    while (values.length < size) values.push(args.length > 2 ? args[2] : null);
    return null;
  });
  builtin("Reverse", (_, args) => { arity("Reverse", args, 1); return arrayOf("Reverse", args[0]).reverse(); });
  builtin("Slice", (_, args) => {
    arity("Slice", args, 1, 3);
    const values = arrayOf("Slice", args[0]);
    return values.slice(args.length > 1 ? Math.trunc(toNumber(args[1])) : 0, args.length > 2 ? Math.trunc(toNumber(args[2])) : values.length);
  });
  builtin("Sort", (interpreter, args) => {
    arity("Sort", args, 1, 2);
    const values = arrayOf("Sort", args[0]);
    let ordered;
    if (args.length === 2 && args[1] !== null) {
      ordered = [...values].sort((a, b) => {
        const r = toNumber(callFn(interpreter, args[1], a, b));
        return r < 0 ? -1 : r > 0 ? 1 : 0;
      });
    } else if (values.some(v => v instanceof ArcDict || v instanceof ArcFeature)) {
      return values; // dictionaries without a comparator are not sorted, as documented
    } else {
      const present = values.filter(v => v !== null);
      const nulls = values.filter(v => v === null);
      if (present.every(isNumber)) ordered = present.sort((a, b) => a - b);
      else if (present.length && present.every(v => v instanceof ArcDate || v instanceof ArcDateOnly || v instanceof ArcTime)) ordered = present.sort((a, b) => scalar(a) - scalar(b));
      // INFERRED (ADR-235 §5.8): a mixed array is ordered by text, which gives the page's ['$', 1, 'A', 'a'].
      else ordered = present.sort((a, b) => { const x = toText(a); const y = toText(b); return x < y ? -1 : x > y ? 1 : 0; });
      ordered = ordered.concat(nulls);
    }
    values.splice(0, values.length, ...ordered);
    return values;
  });
  builtin("Splice", (_, args) => args.flatMap(v => (Array.isArray(v) ? v : [v])));
  builtin("Top", (_, args) => { arity("Top", args, 2); return args[0].slice(0, Math.max(Math.trunc(toNumber(args[1])), 0)); }, firstIs(Array.isArray));

  // ---- dictionaries

  builtin("Dictionary", (_, args) => {
    if (args.length === 1 && typeof args[0] === "string") {
      let parsed;
      try { parsed = JSON.parse(args[0]); } catch { throw new ArcadeError("Dictionary: the text is not JSON."); }
      if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new ArcadeError("Dictionary: the JSON text is not an object.");
      return fromJson(parsed);
    }
    if (args.length && args[0] instanceof ArcDict && args.length <= 2) return copy(args[0], args.length > 1 && toBool(args[1]));
    if (args.length === 1 && (args[0] instanceof ArcGeometry || args[0] instanceof ArcFeature)) return fromJson(toJson(args[0]));
    if (args.length % 2) throw new ArcadeError("Dictionary takes pairs of a name and a value.");
    const out = new ArcDict();
    for (let at = 0; at < args.length; at += 2) out.set(toText(args[at]), args[at + 1]);
    return out;
  });
  builtin("Erase", (_, args) => { arity("Erase", args, 2); args[0].erase(toText(args[1])); return null; }, firstIs(v => v instanceof ArcDict));
  builtin("Insert", (_, args) => { arity("Insert", args, 3); args[0].set(toText(args[1]), args[2]); return null; }, firstIs(v => v instanceof ArcDict));
  builtin("FromJSON", (_, args) => {
    arity("FromJSON", args, 1);
    try { return fromJson(JSON.parse(toText(args[0]))); } catch { throw new ArcadeError("FromJSON: the text is not JSON."); }
  });
  function pairsOf(name, value) {
    if (value instanceof ArcDict) return value.items();
    if (value instanceof ArcFeature) return value.attributes.items();
    throw new ArcadeError(`${name} takes a dictionary; a ${typeOf(value)} was given.`);
  }
  const byKey = (a, b) => { const x = a[0].toLowerCase(); const y = b[0].toLowerCase(); return x < y ? -1 : x > y ? 1 : 0; };
  builtin("GetKeys", (_, args) => { arity("GetKeys", args, 1); return pairsOf("GetKeys", args[0]).sort(byKey).map(p => p[0]); });
  builtin("GetValues", (_, args) => { arity("GetValues", args, 1); return pairsOf("GetValues", args[0]).sort(byKey).map(p => p[1]); });

  // ---- debugging

  builtin("Console", (interpreter, args) => { interpreter.console.push(args.map(toText).join(" ")); return null; });
  builtin("GetEnvironment", (interpreter, args) => {
    arity("GetEnvironment", args, 0);
    const zone = zoneName(interpreter.zone);
    return new ArcDict({ engine: "graticula-studio", engineVersion: "1", application: "Graticula Studio",
      locale: (root.navigator && root.navigator.language) || "en", timeZone: zone, userTimeZone: zone });
  });

  // ---- dates
  //
  // From the Date functions page: months are 0-based in Date, DateOnly and Month, 1-based in ISOMonth; DateAdd and
  // DateDiff count in milliseconds unless given units. Out-of-range parts roll over, as JavaScript's dates do.

  const UNITS = { millisecond: 1, milliseconds: 1, second: 1000, seconds: 1000, minute: 60000, minutes: 60000,
    hour: 3600000, hours: 3600000, day: 86400000, days: 86400000, week: 604800000, weeks: 604800000 };
  const CALENDAR = { month: 1, months: 1, year: 12, years: 12 };

  function parseIso(text, zone) {
    const t = text.trim();
    const m = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,3})\d*)?)?)?\s*(Z|[+-]\d{2}:?\d{2})?$/i.exec(t);
    if (!m) return null;
    const parts = [Number(m[1]), Number(m[2]) - 1, Number(m[3]), Number(m[4] || 0), Number(m[5] || 0), Number(m[6] || 0),
      Number((m[7] || "0").padEnd(3, "0"))];
    if (m[8]) {
      const fixed = m[8].toUpperCase() === "Z" ? UTC : zoneOf(m[8].length === 5 ? `${m[8].slice(0, 3)}:${m[8].slice(3)}` : m[8]);
      return new ArcDate(fromParts(fixed, ...parts).ms, zone);
    }
    return fromParts(zone, ...parts);
  }

  /** Implicit Date casting: a Date, a DateOnly at midnight, ISO text; anything else null. */
  function asDate(value, zone) {
    if (value instanceof ArcDate) return value;
    if (value instanceof ArcDateOnly) return fromParts(zone, value.year, value.month - 1, value.day);
    if (typeof value === "string") return parseIso(value, zone);
    return null;
  }

  /** The calendar parts a date function reads, or null. */
  function readParts(name, value, zone) {
    if (value === null || value === undefined) return null;
    if (value instanceof ArcDateOnly) {
      const d = new Date(value.epochUtc);
      return { year: value.year, month: value.month - 1, day: value.day, hour: 0, minute: 0, second: 0, millisecond: 0, weekday: d.getUTCDay() };
    }
    const date = asDate(value, zone);
    if (!date) throw new ArcadeError(`${name} takes a Date; a ${typeOf(value)} was given.`);
    return partsOf(date);
  }

  builtin("Date", (interpreter, args) => {
    const zone = interpreter.zone;
    if (!args.length) return new ArcDate(Date.now(), zone);
    const first = args[0];
    if (args.length === 1) {
      if (first === null) return new ArcDate(0, zone);
      if (first instanceof ArcDate) return new ArcDate(first.ms, first.zone);
      if (first instanceof ArcDateOnly) return asDate(first, zone);
      if (typeof first === "string") return parseIso(first, zone);
      if (isNumber(first)) return Number.isFinite(first) ? new ArcDate(first, zone) : null;
      throw new ArcadeError(`Date takes a number, text or a date; a ${typeOf(first)} was given.`);
    }
    if (first instanceof ArcDateOnly) {
      arity("Date", args, 1, 3);
      const z = args.length > 2 ? zoneOf(args[2], zone) : zone;
      const ms = args[1] instanceof ArcTime ? args[1].ms : 0;
      return fromParts(z, first.year, first.month - 1, first.day, 0, 0, 0, ms);
    }
    arity("Date", args, 3, 8);
    const parts = args.slice(0, 7).map(toNumber);
    if (parts.some(Number.isNaN)) return null;
    const z = args.length > 7 ? zoneOf(args[7], zone) : zone;
    return fromParts(z, ...parts.map(Math.trunc));
  });
  builtin("Now", (interpreter, args) => { arity("Now", args, 0); return new ArcDate(Date.now(), interpreter.zone); });
  builtin("Timestamp", (_, args) => { arity("Timestamp", args, 0); return new ArcDate(Date.now(), UTC); });
  builtin("Today", (interpreter, args) => {
    arity("Today", args, 0);
    const p = partsOf(new ArcDate(Date.now(), interpreter.zone));
    return fromParts(interpreter.zone, p.year, p.month, p.day);
  });
  builtin("ToLocal", (interpreter, args) => { arity("ToLocal", args, 1); const d = asDate(args[0], interpreter.zone); return d && new ArcDate(d.ms, interpreter.zone); });
  builtin("ToUTC", (interpreter, args) => { arity("ToUTC", args, 1); const d = asDate(args[0], interpreter.zone); return d && new ArcDate(d.ms, UTC); });
  builtin("ChangeTimeZone", (interpreter, args) => {
    arity("ChangeTimeZone", args, 2);
    const d = asDate(args[0], interpreter.zone);
    return d && new ArcDate(d.ms, zoneOf(args[1], interpreter.zone));
  });
  builtin("TimeZone", (interpreter, args) => { arity("TimeZone", args, 1); const d = asDate(args[0], interpreter.zone); return d && zoneName(d.zone); });
  builtin("TimeZoneOffset", (interpreter, args) => {
    arity("TimeZoneOffset", args, 1);
    const d = asDate(args[0], interpreter.zone);
    return d && offsetAt(d.zone, d.ms) * 60000;
  });

  function isoWeek(p) {
    // ISO 8601: the week holding the year's first Thursday is week 1, and a week belongs to the year of its Thursday.
    const date = new Date(Date.UTC(p.year, p.month, p.day));
    date.setUTCDate(date.getUTCDate() + 4 - (date.getUTCDay() || 7));
    const year = date.getUTCFullYear();
    const week = Math.ceil(((date - Date.UTC(year, 0, 1)) / 86400000 + 1) / 7);
    return { week, year };
  }

  function part(name, read, times = false) {
    builtin(name, (interpreter, args) => {
      arity(name, args, 1);
      if (times && args[0] instanceof ArcTime) {
        const ms = args[0].ms;
        return read({ hour: Math.floor(ms / 3600000), minute: Math.floor(ms / 60000) % 60, second: Math.floor(ms / 1000) % 60, millisecond: ms % 1000 });
      }
      const p = readParts(name, args[0], interpreter.zone);
      return p === null ? null : read(p);
    });
  }
  part("Year", p => p.year);
  part("Month", p => p.month);
  part("ISOMonth", p => p.month + 1);
  part("Day", p => p.day);
  part("Hour", p => p.hour, true);
  part("Minute", p => p.minute, true);
  part("Second", p => p.second, true);
  part("Millisecond", p => p.millisecond, true);
  part("Weekday", p => p.weekday);
  part("ISOWeekday", p => (p.weekday === 0 ? 7 : p.weekday));
  part("ISOWeek", p => isoWeek(p).week);
  part("ISOYear", p => isoWeek(p).year);
  builtin("Week", (interpreter, args) => {
    // The number of the given first days of the week on or before the date in its year: 0 before the first one.
    arity("Week", args, 1, 2);
    const p = readParts("Week", args[0], interpreter.zone);
    if (p === null) return null;
    const start = args.length > 1 ? Math.trunc(toNumber(args[1])) : 0;
    if (start < 0 || start > 6) throw new ArcadeError("Week's start day is 0 (Sunday) to 6 (Saturday).");
    const day = Math.round((Date.UTC(p.year, p.month, p.day) - Date.UTC(p.year, 0, 1)) / 86400000);
    const firstWeekday = new Date(Date.UTC(p.year, 0, 1)).getUTCDay();
    const toFirst = (((start - firstWeekday) % 7) + 7) % 7;
    return day < toFirst ? 0 : Math.floor((day - toFirst) / 7) + 1;
  });

  function unitOf(name, value) {
    const unit = toText(value === null || value === undefined ? "milliseconds" : value).trim().toLowerCase();
    if (!(unit in UNITS) && !(unit in CALENDAR)) throw new ArcadeError(`${name}: '${unit}' is not a unit: milliseconds, seconds, minutes, hours, days, months or years.`);
    return unit;
  }

  function addMonths(p, months) {
    const total = p.month + months;
    const year = p.year + Math.floor(total / 12);
    const month = ((total % 12) + 12) % 12;
    const last = new Date(Date.UTC(year, month + 1, 0)).getUTCDate();
    return { ...p, year, month, day: Math.min(p.day, last) };
  }

  builtin("DateAdd", (interpreter, args) => {
    arity("DateAdd", args, 2, 3);
    const unit = unitOf("DateAdd", args.length > 2 ? args[2] : null);
    const amount = toNumber(args[1]);
    const target = args[0];
    if (target instanceof ArcTime) {
      if (!["millisecond", "milliseconds", "second", "seconds", "minute", "minutes", "hour", "hours"].includes(unit)) {
        throw new ArcadeError("DateAdd on a Time takes milliseconds, seconds, minutes or hours.");
      }
      return new ArcTime((((target.ms + Math.trunc(amount * UNITS[unit])) % 86400000) + 86400000) % 86400000);
    }
    if (target instanceof ArcDateOnly) {
      const p = { year: target.year, month: target.month - 1, day: target.day };
      if (unit in CALENDAR) {
        const q = addMonths(p, Math.round(amount) * CALENDAR[unit]);
        return new ArcDateOnly(q.year, q.month + 1, q.day);
      }
      const days = Math.trunc((amount * UNITS[unit]) / 86400000); // less than a day adds nothing, as documented
      const d = new Date(Date.UTC(p.year, p.month, p.day + days));
      return new ArcDateOnly(d.getUTCFullYear(), d.getUTCMonth() + 1, d.getUTCDate());
    }
    const date = asDate(target, interpreter.zone);
    if (!date) return null;
    if (unit in CALENDAR) {
      const p = addMonths(partsOf(date), Math.round(amount) * CALENDAR[unit]);
      const out = fromParts(date.zone, p.year, p.month, p.day, p.hour, p.minute, p.second, p.millisecond);
      return out;
    }
    return new ArcDate(date.ms + amount * UNITS[unit], date.zone);
  });

  /** `a - b` in months: whole months, then the fraction of the next one. */
  function monthDifference(a, b) {
    const pa = partsOf(a);
    const pb = partsOf(b);
    let whole = (pa.year - pb.year) * 12 + (pa.month - pb.month);
    const at = n => { const q = addMonths(pb, n); return fromParts(b.zone, q.year, q.month, q.day, q.hour, q.minute, q.second, q.millisecond).ms; };
    let anchor = at(whole);
    if (anchor > a.ms) { whole--; anchor = at(whole); }
    const following = at(whole + 1);
    const span = following - anchor;
    return whole + (span ? (a.ms - anchor) / span : 0);
  }

  builtin("DateDiff", (interpreter, args) => {
    arity("DateDiff", args, 2, 4);
    const unit = unitOf("DateDiff", args.length > 2 ? args[2] : null);
    const [a, b] = args;
    if (a instanceof ArcTime && b instanceof ArcTime) {
      if (unit in CALENDAR) throw new ArcadeError("DateDiff between Times takes milliseconds, seconds, minutes or hours.");
      return (a.ms - b.ms) / UNITS[unit];
    }
    let x;
    let y;
    if (a instanceof ArcDateOnly && b instanceof ArcDateOnly) {
      x = new ArcDate(a.epochUtc, UTC);
      y = new ArcDate(b.epochUtc, UTC);
    } else {
      x = asDate(a, interpreter.zone);
      y = asDate(b, interpreter.zone);
      if (!x || !y) return null;
    }
    if (unit in CALENDAR) return monthDifference(x, y) / CALENDAR[unit];
    return (x.ms - y.ms) / UNITS[unit];
  });

  builtin("DateOnly", (interpreter, args) => {
    const zone = interpreter.zone;
    if (!args.length) { const p = partsOf(new ArcDate(Date.now(), zone)); return new ArcDateOnly(p.year, p.month + 1, p.day); }
    const first = args[0];
    if (args.length === 1) {
      if (first instanceof ArcDateOnly) return new ArcDateOnly(first.year, first.month, first.day);
      if (first instanceof ArcDate) { const p = partsOf(first); return new ArcDateOnly(p.year, p.month + 1, p.day); }
      if (isNumber(first)) { const d = new Date(first); return new ArcDateOnly(d.getUTCFullYear(), d.getUTCMonth() + 1, d.getUTCDate()); }
      if (typeof first === "string") {
        const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(first.trim());
        if (!m) return null;
        const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
        if (d.getUTCMonth() !== Number(m[2]) - 1) return null;
        return new ArcDateOnly(Number(m[1]), Number(m[2]), Number(m[3]));
      }
      if (first === null) return null;
      throw new ArcadeError(`DateOnly takes a number, text or a date; a ${typeOf(first)} was given.`);
    }
    if (args.length === 2) throw new NotBuiltFunction("The Arcade function DateOnly is not built in Studio's form engine for a text pattern.");
    arity("DateOnly", args, 3);
    const [year, month, day] = args.map(a => Math.trunc(toNumber(a)));
    const d = new Date(Date.UTC(2000, 0, 1));
    d.setUTCFullYear(year, month, day);
    // An out-of-range month or day answers null, as documented.
    if (d.getUTCFullYear() !== year || d.getUTCMonth() !== month || d.getUTCDate() !== day) return null;
    return new ArcDateOnly(year, month + 1, day);
  });

  const CLOCK = /^\s*(\d{1,2})(?::(\d{2}))?(?::(\d{2})(?:\.(\d{1,3}))?)?\s*([AaPp][Mm])?\s*$/;
  builtin("Time", (interpreter, args) => {
    if (!args.length) {
      const p = partsOf(new ArcDate(Date.now(), interpreter.zone));
      return new ArcTime(((p.hour * 60 + p.minute) * 60 + p.second) * 1000 + p.millisecond);
    }
    const first = args[0];
    if (args.length === 1) {
      if (first instanceof ArcTime) return new ArcTime(first.ms);
      if (first instanceof ArcDate) { const p = partsOf(first); return new ArcTime(((p.hour * 60 + p.minute) * 60 + p.second) * 1000 + p.millisecond); }
      if (isNumber(first)) return first >= 0 && first <= 86400000 ? new ArcTime(first) : null;
      if (typeof first === "string") {
        const m = CLOCK.exec(first);
        if (!m) return null;
        let hour = Number(m[1]);
        const minute = Number(m[2] || 0);
        const second = Number(m[3] || 0);
        const ms = Number((m[4] || "0").padEnd(3, "0"));
        const half = (m[5] || "").toUpperCase();
        if (half) {
          if (hour < 1 || hour > 12) return null;
          hour = (hour % 12) + (half === "PM" ? 12 : 0);
        }
        if (hour > 23 || minute > 59 || second > 59) return null;
        return new ArcTime(((hour * 60 + minute) * 60 + second) * 1000 + ms);
      }
      if (first === null) return null;
      throw new ArcadeError(`Time takes a number, text or a date; a ${typeOf(first)} was given.`);
    }
    if (args.length === 2 && typeof first === "string") throw new NotBuiltFunction("The Arcade function Time is not built in Studio's form engine for a text pattern.");
    arity("Time", args, 2, 4);
    const [hour, minute, second = 0, ms = 0] = args.map(a => Math.trunc(toNumber(a)));
    if (!(hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59 && second >= 0 && second <= 59 && ms >= 0 && ms <= 999)) return null;
    return new ArcTime(((hour * 60 + minute) * 60 + second) * 1000 + ms);
  });

  // ---- features: the ones a form uses without a FeatureSet

  builtin("Feature", (_, args) => {
    if (args.length === 1 && args[0] instanceof ArcFeature) {
      return new ArcFeature(copy(args[0].attributes, true), args[0].geometry, false, args[0].schema);
    }
    if (args.length === 1 && (typeof args[0] === "string" || args[0] instanceof ArcDict)) {
      let json = args[0];
      if (typeof json === "string") {
        try { json = fromJson(JSON.parse(json)); } catch { throw new ArcadeError("Feature: the text is not JSON."); }
      }
      const attributes = json.get("attributes");
      const geometry = json.get("geometry");
      return new ArcFeature(attributes instanceof ArcDict ? copy(attributes, true) : new ArcDict(),
        geometry instanceof ArcDict ? new ArcGeometry(toJson(geometry)) : null);
    }
    if (!args.length) throw new ArcadeError("Feature takes a geometry and attributes, a feature, or its JSON.");
    const geometry = args[0] instanceof ArcGeometry ? args[0] : args[0] instanceof ArcDict ? new ArcGeometry(toJson(args[0])) : null;
    if (args.length === 2 && args[1] instanceof ArcDict) return new ArcFeature(copy(args[1], true), geometry);
    if ((args.length - 1) % 2) throw new ArcadeError("Feature takes a geometry, then pairs of a field name and a value.");
    const attributes = new ArcDict();
    for (let at = 1; at < args.length; at += 2) attributes.set(toText(args[at]), args[at + 1]);
    return new ArcFeature(attributes, geometry);
  });
  builtin("Geometry", (_, args) => {
    arity("Geometry", args, 1);
    const v = args[0];
    if (v instanceof ArcFeature) return v.geometry;
    if (v instanceof ArcGeometry) return v;
    if (v instanceof ArcDict) return new ArcGeometry(toJson(v));
    if (v === null) return null;
    throw new ArcadeError(`Geometry takes a feature or a geometry's dictionary; a ${typeOf(v)} was given.`);
  });
  builtin("Expects", (_, args) => { arity("Expects", args, 1, -1); return null; });

  function fieldOf(feature, name, fn) {
    if (!(feature instanceof ArcFeature)) throw new ArcadeError(`${fn} takes a feature; a ${typeOf(feature)} was given.`);
    const fields = (feature.schema && feature.schema.fields) || [];
    const found = fields.find(f => String(f.name).toLowerCase() === toText(name).toLowerCase());
    if (!found) {
      if (!feature.schema) throw new ArcadeError(`${fn}: this feature carries no layer description to read '${toText(name)}' from.`);
      throw new ArcadeError(`${fn}: the layer has no field '${toText(name)}'.`);
    }
    return found;
  }
  function domainDict(domain) {
    if (!domain) return null;
    return fromJson(domain);
  }
  builtin("Schema", (_, args) => {
    arity("Schema", args, 1);
    const f = args[0];
    if (!(f instanceof ArcFeature) || !f.schema) throw new ArcadeError("Schema: this feature carries no layer description.");
    return fromJson({ fields: f.schema.fields || [], objectIdField: f.schema.objectIdField || null,
      globalIdField: f.schema.globalIdField || "", geometryType: f.schema.geometryType || "" });
  });
  builtin("Domain", (_, args) => { arity("Domain", args, 2, 3); return domainDict(fieldOf(args[0], args[1], "Domain").domain); });
  builtin("DomainName", (_, args) => {
    arity("DomainName", args, 2, 4);
    const field = fieldOf(args[0], args[1], "DomainName");
    const code = args.length > 2 ? args[2] : args[0].attributes.get(field.name);
    const coded = field.domain && field.domain.type === "codedValue" ? field.domain.codedValues || [] : null;
    if (!coded) return code === null || code === undefined ? null : code; // no coded domain: the value itself
    const found = coded.find(c => equal(c.code, code) || String(c.code) === toText(code));
    return found ? found.name : null;
  });
  builtin("DomainCode", (_, args) => {
    arity("DomainCode", args, 2, 4);
    const field = fieldOf(args[0], args[1], "DomainCode");
    const name = args.length > 2 ? args[2] : null;
    const coded = field.domain && field.domain.type === "codedValue" ? field.domain.codedValues || [] : null;
    if (!coded) return null;
    if (name === null) return args[0].attributes.get(field.name);
    const found = coded.find(c => c.name === toText(name));
    return found ? found.code : null;
  });

  // ---------------------------------------------------------------------------------------------------- Text(value, format)
  //
  // Numbers: 0 a digit kept, # a digit dropped when it would be a leading or trailing zero, ',' grouping by thousands,
  // '.' the decimal point, '%' multiplying by 100, ';' separating the positive pattern from the negative one.
  // INFERRED (ADR-235 §5.8): a pattern without grouping keeps only as many integer digits as it has placeholders, and a
  // half is rounded away from zero. Dates: the tokens the Text page lists; anything else is written as it is.

  function formatText(value, pattern) {
    if (pattern === null || pattern === undefined || pattern === "") return toText(value);
    const p = toText(pattern);
    if (value instanceof ArcDate || value instanceof ArcDateOnly || value instanceof ArcTime) return formatDate(value, p);
    if (isNumber(value)) return formatNumber(value, p);
    return toText(value);
  }

  function formatNumber(value, pattern) {
    if (Number.isNaN(value)) return "NaN";
    if (!Number.isFinite(value)) return value > 0 ? "Infinity" : "-Infinity";
    const sections = pattern.split(";");
    if (value < 0 && sections.length > 1) return formatOne(Math.abs(value), sections[1]);
    const out = formatOne(Math.abs(value), sections[0]);
    return value < 0 ? `-${out}` : out;
  }

  function formatOne(value, pattern) {
    const places = [...pattern].map((c, i) => ("0#.,".includes(c) ? i : -1)).filter(i => i >= 0);
    if (!places.length) return pattern;
    const first = places[0];
    const last = places[places.length - 1];
    const prefix = pattern.slice(0, first);
    const core = pattern.slice(first, last + 1);
    const suffix = pattern.slice(last + 1);
    if ((prefix + suffix).includes("%")) value = shift(value, 2);
    const [integerPart, fractionPart = ""] = core.split(".");
    const grouping = integerPart.includes(",");
    const integerDigits = integerPart.replace(/,/g, "");
    const minimumFraction = (fractionPart.match(/0/g) || []).length;
    const maximumFraction = (fractionPart.match(/[0#]/g) || []).length;
    const exact = shift(Math.round(shift(value, maximumFraction)), -maximumFraction);
    let [whole, fraction = ""] = exact.toFixed(maximumFraction).split(".");
    while (fraction.length > minimumFraction && fraction.endsWith("0")) fraction = fraction.slice(0, -1);
    const minimumInteger = (integerDigits.match(/0/g) || []).length;
    if (whole === "0" && minimumInteger === 0) whole = "";
    whole = whole.padStart(minimumInteger, "0");
    if (!grouping && integerDigits && !integerDigits.includes("#") && whole.length > integerDigits.length) {
      whole = whole.slice(-integerDigits.length);
    }
    if (grouping && whole && /[1-9]/.test(whole)) whole = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
    return prefix + whole + (fraction ? `.${fraction}` : "") + suffix;
  }

  const DATE_TOKENS = /\[[^\]]*\]|dddd|ddd|DDDD|DDD|DD|D|d|MMMM|MMM|MM|M|YYYY|YY|Y|hh|h|HH|H|mm|m|ss|s|SSS|A|a|ZZZZZ|ZZZZ|ZZZ|ZZ|Z/g;
  const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
  const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
    "November", "December"];

  function formatDate(value, pattern) {
    let p;
    let zone = null;
    if (value instanceof ArcDate) { p = partsOf(value); zone = value.zone; }
    else if (value instanceof ArcDateOnly) {
      p = { year: value.year, month: value.month - 1, day: value.day, hour: 0, minute: 0, second: 0, millisecond: 0,
        weekday: new Date(value.epochUtc).getUTCDay() };
    } else {
      const ms = value.ms;
      p = { year: 1970, month: 0, day: 1, hour: Math.floor(ms / 3600000), minute: Math.floor(ms / 60000) % 60,
        second: Math.floor(ms / 1000) % 60, millisecond: ms % 1000, weekday: 4 };
    }
    const hour12 = p.hour % 12 || 12;
    const yearDay = Math.round((Date.UTC(p.year, p.month, p.day) - Date.UTC(p.year, 0, 1)) / 86400000) + 1;
    return pattern.replace(DATE_TOKENS, token => {
      if (token.startsWith("[")) return token.slice(1, -1);
      switch (token) {
        case "D": return String(p.day);
        case "DD": return pad(p.day);
        case "DDD": case "DDDD": return String(yearDay);
        case "d": return String(p.weekday + 1); // 1–7, Sunday first, as the page lists it
        case "ddd": return DAYS[p.weekday].slice(0, 3);
        case "dddd": return DAYS[p.weekday];
        case "M": return String(p.month + 1);
        case "MM": return pad(p.month + 1);
        case "MMM": return MONTHS[p.month].slice(0, 3);
        case "MMMM": return MONTHS[p.month];
        case "Y": case "YYYY": return String(p.year);
        case "YY": return pad(p.year % 100);
        case "h": return String(hour12);
        case "hh": return pad(hour12);
        case "H": return String(p.hour);
        case "HH": return pad(p.hour);
        case "m": return String(p.minute);
        case "mm": return pad(p.minute);
        case "s": return String(p.second);
        case "ss": return pad(p.second);
        case "SSS": return pad(p.millisecond, 3);
        case "A": return p.hour < 12 ? "AM" : "PM";
        case "a": return p.hour < 12 ? "am" : "pm";
        default: {
          if (!zone) return "";
          const minutes = p.offset;
          const sign = minutes >= 0 ? "+" : "-";
          const h = Math.trunc(Math.abs(minutes) / 60);
          const m = Math.abs(minutes) % 60;
          if (token === "Z") return `${sign}${h}${m ? `:${pad(m)}` : ""}`;
          if (token === "ZZ") return `${sign}${pad(h)}:${pad(m)}`;
          if (token === "ZZZ") return `${sign}${pad(h)}${pad(m)}`;
          return zoneName(zone);
        }
      }
    });
  }

  // ---------------------------------------------------------------------------------------------------- the catalogue
  //
  // Every function of the function reference, by category, read from developers.arcgis.com/arcade/function-reference
  // (the same catalogue grapy's ADR-235 counts). Each is built here, or refused with its reason.

  const DOCUMENTED = {
    AI: ["TranslateText"],
    Array: ["All", "Any", "Array", "Back", "Count", "DefaultValue", "Distinct", "Erase", "Filter", "First", "Front",
      "HasValue", "Includes", "IndexOf", "Insert", "Map", "None", "Pop", "Push", "Reduce", "Resize", "Reverse", "Slice",
      "Sort", "Splice", "Top"],
    Date: ["ChangeTimeZone", "Date", "DateAdd", "DateDiff", "DateOnly", "Day", "Hour", "ISOMonth", "ISOWeek",
      "ISOWeekday", "ISOYear", "Millisecond", "Minute", "Month", "Now", "Second", "Time", "Timestamp", "TimeZone",
      "TimeZoneOffset", "Today", "ToLocal", "ToUTC", "Week", "Weekday", "Year"],
    Debugging: ["Console", "GetEnvironment"],
    Dictionary: ["Count", "DefaultValue", "Dictionary", "Erase", "FromJSON", "GetKeys", "GetValues", "HasKey",
      "HasValue", "Insert"],
    Enterprise: ["NextSequenceValue"],
    Feature: ["DefaultValue", "Domain", "DomainCode", "DomainName", "Expects", "Feature", "FeatureInFilter",
      "GdbVersion", "HasKey", "HasValue", "Schema", "SubtypeCode", "SubtypeName", "Subtypes", "TimeReceived"],
    FeatureSet: ["Area", "AreaGeodetic", "Attachments", "Average", "Contains", "Count", "Crosses", "Distinct", "Domain",
      "DomainCode", "DomainName", "EnvelopeIntersects", "Expects", "FeatureSet", "FeatureSetByAssociation",
      "FeatureSetById", "FeatureSetByName", "FeatureSetByRelationshipClass", "FeatureSetByRelationshipName", "Filter",
      "FilterBySubtypeCode", "First", "GdbVersion", "GetFeatureSet", "GetFeatureSetInfo", "GetUser", "GroupBy",
      "Intersects", "Length", "Length3D", "LengthGeodetic", "Max", "Mean", "Min", "OrderBy", "Overlaps", "Schema",
      "Stdev", "Subtypes", "Sum", "Top", "Touches", "Variance", "Within"],
    Geometry: ["Angle", "Area", "AreaGeodetic", "Bearing", "Buffer", "BufferGeodetic", "Centroid", "Clip", "Contains",
      "ConvertDirection", "ConvexHull", "Crosses", "Cut", "DefaultValue", "Densify", "DensifyGeodetic", "Difference",
      "Disjoint", "Distance", "DistanceGeodetic", "DistanceToCoordinate", "EnvelopeIntersects", "Equals", "Extent",
      "Generalize", "Geometry", "HasValue", "Intersection", "Intersects", "IsSelfIntersecting", "IsSimple", "Length",
      "Length3D", "LengthGeodetic", "MeasureToCoordinate", "MultiPartToSinglePart", "Multipoint", "NearestCoordinate",
      "NearestVertex", "Offset", "Overlaps", "Point", "PointToCoordinate", "Polygon", "Polyline", "Relate",
      "RingIsClockwise", "Rotate", "SetGeometry", "Simplify", "SymmetricDifference", "Touches", "Union", "Within"],
    "Knowledge graph": ["KnowledgeGraphByPortalItem", "QueryGraph"],
    Logical: ["Boolean", "Decode", "DefaultValue", "Equals", "IIf", "IsEmpty", "IsNan", "TypeOf", "When"],
    Math: ["Abs", "Acos", "Asin", "Atan", "Atan2", "Average", "Ceil", "Constrain", "Cos", "Exp", "Floor", "Hash", "Log",
      "Max", "Mean", "Min", "Number", "Pow", "Product", "Random", "Round", "Sign", "Sin", "Sqrt", "Stdev", "Sum", "Tan",
      "Variance"],
    Pixel: ["Count", "DefaultValue", "GetKeys", "GetValues", "HasKey", "HasValue"],
    Portal: ["FeatureSetByPortalItem", "GetUser", "Portal"],
    Text: ["Concatenate", "Count", "Find", "FromCharCode", "FromCodePoint", "Guid", "Left", "Lower", "Mid", "Proper",
      "Replace", "Right", "Split", "StandardizeFilename", "StandardizeGuid", "Text", "ToCharCode", "ToCodePoint",
      "ToHex", "Trim", "Upper", "UrlEncode"],
    Track: ["TrackAccelerationAt", "TrackAccelerationWindow", "TrackCurrentAcceleration", "TrackCurrentDistance",
      "TrackCurrentSpeed", "TrackCurrentTime", "TrackDistanceAt", "TrackDistanceWindow", "TrackDuration",
      "TrackFieldWindow", "TrackGeometryWindow", "TrackIndex", "TrackSpeedAt", "TrackSpeedWindow", "TrackStartTime",
      "TrackWindow"],
    Voxel: ["Count", "DefaultValue", "GetKeys", "GetValues", "HasKey", "HasValue"],
  };

  const NO_NETWORK = "Studio's form engine runs in the page with no network access, so it cannot read a FeatureSet or "
    + "the portal (ADR-313, D-476)";
  const NO_GEOMETRY = "geometry operations are not built in Studio's form engine (ADR-313, D-476); Field Maps runs them";

  const REFUSED = {
    TranslateText: `it calls an AI translation service, and ${NO_NETWORK}`,
    KnowledgeGraphByPortalItem: NO_NETWORK,
    QueryGraph: NO_NETWORK,
    NextSequenceValue: "database sequences belong to attribute rules in a geodatabase, not to a form",
    Hash: "its hash algorithm is not documented, and a hash that differs from ArcGIS's answers nothing a form can compare",
    GdbVersion: "a form edits the default version of a feature service; the version is not passed to the form",
    TimeReceived: "it belongs to stream layers and real-time profiles, not to a form",
    SubtypeCode: "a layer's subtypes are not passed to Studio's form engine",
    SubtypeName: "a layer's subtypes are not passed to Studio's form engine",
    Subtypes: "a layer's subtypes are not passed to Studio's form engine",
    FeatureInFilter: "it evaluates a SQL where clause, which Studio's form engine does not parse",
  };
  for (const name of ["Attachments", "FeatureSet", "FeatureSetByAssociation", "FeatureSetById", "FeatureSetByName",
    "FeatureSetByRelationshipClass", "FeatureSetByRelationshipName", "FilterBySubtypeCode", "GetFeatureSet",
    "GetFeatureSetInfo", "GetUser", "GroupBy", "OrderBy", "FeatureSetByPortalItem", "Portal"]) {
    REFUSED[name] = NO_NETWORK;
  }
  for (const name of DOCUMENTED.Geometry) if (!REFUSED[name] && !["DefaultValue", "Equals", "Geometry", "HasValue"].includes(name)) REFUSED[name] = NO_GEOMETRY;
  for (const name of ["Area", "AreaGeodetic", "Length", "Length3D", "LengthGeodetic"]) REFUSED[name] = NO_GEOMETRY;
  for (const name of DOCUMENTED.Track) REFUSED[name] = "track functions run only in GeoAnalytics tools on track-aware data";

  const REFUSED_LOWER = new Map(Object.entries(REFUSED).map(([k, v]) => [k.toLowerCase(), [k, v]]));
  const DOCUMENTED_LOWER = new Map(Object.values(DOCUMENTED).flat().map(n => [n.toLowerCase(), n]));

  /** Raises the error a function that is not built answers — or, for an overload not built, that one's. */
  function notBuilt(name, line = null, args = null) {
    const lower = String(name).toLowerCase();
    const refused = REFUSED_LOWER.get(lower);
    if (refused && !REGISTRY.has(lower)) {
      throw new NotBuiltFunction(`The Arcade function ${refused[0]} is not built in Studio's form engine: ${refused[1]}.`, line);
    }
    if (REGISTRY.has(lower)) {
      const shown = (args || []).map(typeOf).join(", ");
      const why = refused ? `: ${refused[1]}` : "";
      throw new NotBuiltFunction(`The Arcade function ${CANONICAL.get(lower)} is not built in Studio's form engine for (${shown})${why}.`, line);
    }
    if (DOCUMENTED_LOWER.has(lower)) throw new NotBuiltFunction(`The Arcade function ${DOCUMENTED_LOWER.get(lower)} has no implementation in Studio's form engine.`, line);
    throw new ArcadeError(`'${name}' is not a function.`, line);
  }

  /** 'built', 'refused' or 'unknown' for a function name. */
  function status(name) {
    const lower = String(name).toLowerCase();
    if (REGISTRY.has(lower)) return "built";
    if (REFUSED_LOWER.has(lower)) return "refused";
    return "unknown";
  }

  /** {documented name: 'built' | 'refused' | 'unknown'} — what ADR-313 §5.1 counts. */
  function coverage() {
    const out = {};
    for (const name of [...new Set(DOCUMENTED_LOWER.values())].sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()))) {
      out[name] = status(name);
    }
    return out;
  }

  // ---------------------------------------------------------------------------------------------------- entry

  const FUNCTIONS = functionTable();

  const FORM_REFUSED_VARIABLES = {
    $layer: NO_NETWORK, $featureset: NO_NETWORK, $datastore: NO_NETWORK, $map: NO_NETWORK,
  };

  /** A compiled expression: parse once, run many times. Throws ArcadeError for a syntax error. */
  function compile(source) {
    return parse(source);
  }

  /**
   * Runs a program (or source text) with a profile, and answers { value, console }. `options.zone` is the execution
   * time zone: "local" (the default), "UTC", an offset or an IANA name.
   */
  function run(program, profile = {}, options = {}) {
    const tree = typeof program === "string" ? parse(program) : program;
    const zone = zoneOf(options.zone || "local", LOCAL);
    const interpreter = new Interpreter(profile, FUNCTIONS, { zone, refusedVariables: options.refusedVariables || FORM_REFUSED_VARIABLES });
    const value = interpreter.run(tree);
    return { value: value === undefined ? null : value, console: interpreter.console };
  }

  function evaluate(source, profile = {}, options = {}) {
    return run(source, profile, options).value;
  }

  const api = {
    ArcadeError, NotBuiltFunction, ArcDict, ArcFeature, ArcGeometry, ArcDate, ArcDateOnly, ArcTime, ArcFunction,
    parse, compile, run, evaluate, coverage, status, DOCUMENTED, REFUSED,
    toText, toNumber, toBool, toJson, fromJson, typeOf, equal, zoneOf, partsOf, fromParts, UTC, LOCAL,
  };

  if (typeof module === "object" && module.exports) module.exports = api;
  else root.GraticulaArcade = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
