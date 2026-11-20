# ADR-304 — grapy's tool outputs carry z and m as ArcGIS Pro's do

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-20, in the overnight run the owner asked for (*"first finish the things you can do yourself"*), to repay [D-412](../architecture-debt.md). Derived from measurement under [ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md)'s aim that Pro scripts run on grapy; no owner choice was needed, and the one reading that is not measured is marked INFERRED in §5. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

grapy's general tools wrote 2D outputs from 3D inputs ([D-412](../architecture-debt.md)): about 250 calls of
`_gpkg.create` passed no `has_z`/`has_m`, so a 3D input lost its z, silently, in every tool but CopyFeatures. The Pro
sweep found it late because every module before `ddd` used 2D fixtures. Several tools also crashed on an input with
m (shapely refuses a four-ordinate line).

## 2. Alternatives considered

### Alternative A — pass the input's z and m at each call

**Argument for.** Each tool says what it writes; nothing implicit.

**Argument against.** About 250 calls, each to be found, changed and tested; every new tool must remember it, which
is exactly the omission that made D-412.

### Alternative B — the default follows the running tool's inputs (chosen)

**Argument for.** Pro's own rule is a framework rule, not each tool's: the *output has Z* and *output has M*
environments default to *Same As Input*. One place (`_gpkg.create`, reading the inputs of the call `_runtime.run_tool`
is running) reaches every tool, the new ones included; a tool that writes 2D whatever its input (Buffer) says so.

**Argument against.** Implicit: what an output carries depends on the call it is made in. A grapy function that is
not a tool, or is called outside one, writes 2D unless it says otherwise.

## 3. Counterarguments to the preferred option

- A tool may have an input parameter holding a feature class that is not the shape source (a barrier, a mask). Under
  Pro's rule that still counts: measured, Clip of a 2D polygon by a PolygonZM writes PolygonZM.
- Restoring z and m vertex by vertex costs time on a 3D input: each vertex is looked up on its inputs' segments. A 2D
  input pays nothing.

## 4. Evidence

`tools/pro-compare/sweep/cases/zm.py` and [results/zm.md](../../tools/pro-compare/sweep/results/zm.md): 81 cases of 50
tools on PolylineZM, PolygonZM and PointZM inputs under ArcGIS Pro 3.7's arcpy and under grapy, 2026-11-20. Before:
MATCH 9, DIFF 65, GRAPY-ERROR 6. After: MATCH 78; the one DIFF and one SCHEMA are 2D differences recorded there.

## 5. Decision

1. `_gpkg.create` called without `has_z`/`has_m` follows `env.outputZFlag` and `env.outputMFlag` (new, default
   `Same As Input`): under *Same As Input*, an output carries z (m) when any feature class among the running tool's
   input parameters does (`_zm.from_tool`; a parameter named `out…` is an output). A table never does.
2. Buffer, PairwiseBuffer, GraphicBuffer and MultipleRingBuffer write 2D, as Pro's do.
3. A tool that cuts its inputs restores each output vertex's z and m from them (`_zm.restore`): a vertex's own values,
   or those interpolated along the input segment it lies on, the piece's own features first, then any other 3D input
   it lies on; a vertex on none has z 0 and m NaN. A shape a tool makes rather than cuts has z 0 and m NaN.
4. INFERRED: a vertex lies on an input's segment within a ten-billionth of the largest coordinate (under half a
   millimetre at UTM northings). Pro's XY tolerance is a millimetre by default; the figure was chosen below it, not
   measured.

## 6. Consequences

- D-412 is repaid. Every grapy tool that writes features from features carries z and m when its inputs do.
- **State.** None in the catalogue or at runtime. Outputs in a user's GeoPackage are now 3D where their inputs were.
- A new tool that writes 2D from 3D on purpose must say `False, False`.

## 7. Assumptions this decision rests on

- The 50 tools measured stand for the rest: Pro's *Same As Input* is applied by its framework, so a tool not measured
  behaves the same unless, like Buffer, it is 2D by nature.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md), [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md),
[ADR-289](ADR-289-owner-decisions-on-the-pro-sweep.md) (its per-tool frame is what `from_tool` reads).

## 9. Revisit triggers

- A tool found to write 2D in Pro from a 3D input, other than the four buffers: it gets `False, False`.
- A user's 3D data whose cut vertices are found more than the tolerance off their segment.

## 10. Dissent

None recorded.
