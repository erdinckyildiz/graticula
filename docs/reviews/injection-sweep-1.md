# Injection sweep 1 — every path from caller text to an interpreter

**Run 2026-08-16.** Self-review, so §67's objection applies in full — and this
sweep exists *because* of that objection landing: the §66 security gate of
2026-08-15 tested the where-clause parser, found it sound, and missed
[D-41](../architecture-debt.md) one query parameter away, because a comment in the
code claimed the parameter beside it got the same treatment.

**The question this answers is not "is there an injection".** D-41 was found by
accident while writing an unrelated architecture test. One accidental find tells
you nothing about how many more there are, and an unknown base rate is the worst
state to be in. So this is a census: every place caller-controlled text reaches an
interpreter — SQL, a filesystem path, a process argument, an HTTP header, HTML —
traced to what constrains it.

## Method

Not "read the security-relevant files". D-41's whole shape was that the code's own
account of itself was wrong and reading it was how the account got believed. So
each site was traced the other way: from the interpreter backwards, asking of
every interpolated value *where did this come from and what makes it safe*, with
the code's comments treated as claims rather than answers.

Sites were enumerated mechanically rather than by judgement — every interpolated
string containing SQL keywords, every `StringBuilder` feeding a command, every
`CommandText`/`CreateCommand`, every `Path.Combine`, every `ProcessStartInfo`,
every header assignment, and every interpolation hole in the three HTML pages.

## Result

**Two findings, neither an injection.** Everything that reaches SQL is either a
bound parameter, an identifier matched against the database's own column list and
then quoted, or a value built by us from an enum or an integer.

### Findings

| # | What | Outcome |
|---|---|---|
| 1 | **A mandatory statement timeout was removable by an unrelated setting.** `LayerConnections` applied `statement_timeout` only when a registered connection string's `Options` was empty, so `Options=-c application_name=qgis` silently removed a control ADR-007 §4.8 makes mandatory. | [D-42](../architecture-debt.md), fixed and tested the same day |
| 2 | **Two independent hand-rolled SQL literal escapers.** `PostGisFeatureSource.Literal` and an inline `Replace("'", "''")` in `PostGisTileSource.BuildSql` do the same job in two places. Both are correct today and both depend on `standard_conforming_strings` being on. | Not fixed. It is the *shape* of D-41 — a second implementation nobody re-audits — and worth consolidating before a third appears. Recorded here rather than as debt, because the code is currently right |

### What was checked and found sound

| Surface | What constrains the caller's text |
|---|---|
| `where` (attribute filter) | `WhereClause` parses to an expression and re-emits our own SQL; identifiers matched against the layer's real columns, operators from a fixed table, every literal bound. No grammar rule exists for `;`, comments, subqueries, function calls or arithmetic, so none can arrive by an unforeseen escape |
| `outStatistics` | `statisticType` → enum; `onStatisticField` → matched against the layer's fields, and the *matched* name is what reaches SQL; `outStatisticFieldName` → refused unless letters, digits and underscores within 63 characters, because an alias is an identifier and cannot be bound |
| `groupByFieldsForStatistics`, `orderByFields`, `outFields` | Each name matched against the layer's field list, refused by name if absent |
| `havingClause` | Refused entirely — D-41 |
| `applyEdits` attributes | Keys matched ordinally against the field list read from the database; values bound as `@v{n}`; geometry as `st_geomfromwkb(@geom, srid)` with an integer SRID from the catalogue |
| Table and schema identifiers | `LayerDefinition.RequireIdentifier` restricts to ASCII alphanumerics within 63 bytes, and `Quote` doubles embedded quotes on top of that — belt and braces, deliberately |
| Import table names | Built character by character from ASCII alphanumerics only, truncated at 40, prefixed if it would not start with a letter, and suffixed with eight random hex characters. Safe by construction rather than by escaping |
| Hosted-table drops | Refused outside the single `hosted` schema ([data-model.md](../data-model.md) §2) |
| Tile SQL | Values bound; identifiers quoted; the MVT layer name is a string literal with quotes doubled; the SRIDs are integers formatted invariantly |
| Related records | Every identifier from the layer's own column list, quoted; ids bound as an array parameter; the limit is an `int` |
| Platform store (catalogue, identity, audit, sessions, migrations) | Fully parameterised. The only interpolations are `const` column and `from` lists |
| Uploaded archives | An entry whose name contains `/` or `\` is refused outright, and nothing is extracted to disk at all — everything is read into bounded memory. Zip slip is structurally impossible, not merely guarded |
| Attachment filenames | Never a path: only the last segment is kept, control characters replaced, and the header is written through `ContentDispositionHeaderValue.SetHttpFileName` rather than by concatenation. Responses carry `nosniff` and `default-src 'none'; sandbox` |
| Tile cache paths | A GUID, an eight-hex fingerprint and three integers. No caller text reaches a path, and `Purge` deletes a directory named by a GUID |
| Glyph paths | The font stack is matched against the directory listing; the range is rebuilt from parsed integers |
| Overlay worker launch | A computed executable path, `UseShellExecute = false`, and **no arguments at all** |
| REST directory, query page, geometry page | Text escaped with `HtmlEncode`, URLs with `EscapeDataString` segment by segment. The unescaped interpolations are all developer-authored constants, several of which carry deliberate markup |
| Registered connection strings | Parsed by `NpgsqlConnectionStringBuilder`, never concatenated, and gated behind `Privilege.ContentRegisterDataStore` |

## What this sweep does not establish

- **It is still a self-review.** It read the same code its author wrote, and
  §67's objection is not answered by being more careful. An independent pass is
  the §66 correctness and simplicity gates, which are open and waiting on
  somebody other than the author.
- **No fuzzing was run.** Every conclusion here is from reading a path, not from
  driving values through it. The where-clause grammar has twelve asserted refused
  techniques in the conformance suite; nothing else on this list has an equivalent.
- **Second-order sinks were not examined.** Values that reach a log line, an audit
  row or a metric label were out of scope. Log injection is a real class and this
  says nothing about it.
- **The two escapers in finding 2 were judged correct by reading them.** That is
  precisely the method D-41 discredited. Consolidating them to one implementation
  would remove the need to trust that judgement twice.
