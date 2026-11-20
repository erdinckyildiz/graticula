# ADR-283 — The owner's two answers: IFC and USD are converted, and a 3D object layer has one writer per site

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07, **by owner decision**: *"Karar verdim. İkisini de uygula"* ("I have decided. Carry out both"), in answer to the fifth night's morning report, which put two questions, each with a recommendation (§2). The owner chose against both recommendations. How each answer is carried out is this ADR's. |
| **Supersedes** | — It amends [ADR-282](ADR-282-3d-models-are-converted-by-assimp-in-its-own-process.md) §5, which refuses IFC, DWG and USD, and closes [D-390](../architecture-debt.md). |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-282](ADR-282-3d-models-are-converted-by-assimp-in-its-own-process.md) converts OBJ, COLLADA and FBX to glb with
Assimp, and refuses IFC, DWG and USD by name. [ADR-280](ADR-280-3d-object-layers-hold-meshes.md) §5.4 keeps a 3D
object layer's features as one resource of its item. That resource is read, changed and written back under a lock that
holds only within one process, which is [D-390](../architecture-debt.md).

## 2. Alternatives considered

The two questions, as the morning report put them. **(R)** marks the recommendation.

1. **Converting IFC, DWG and USD.** This needs libraries other than Assimp: IfcOpenShell (LGPL) for IFC, and OpenUSD for
   USD. The report said DWG has no suitable path. The options were:
   - wait until a customer asks (R);
   - do it now.

   **Answer: do it now.**
2. **One writer per site for a 3D object layer (D-390).** The options were:
   - add a site-wide lock at the first multi-node installation (R);
   - do it now.

   **Answer: do it now.**

## 3. Counterarguments to the preferred option

- **Converters for formats nobody has yet asked for** add two third-party native programs to the serving image. That
  means about 45 MB for IfcConvert on each architecture, two more licences to carry, and two more parsers of hostile
  input. ADR-282 §5's child-process limits answer the last point: memory, time, and a crash that ends one conversion
  and not the server. The licences are answered in §5.2.
- **A conditional write is retried, not queued.** When many nodes edit one layer at once, the retries could run out
  before an edit lands. One node never retries against itself, because its own lock comes first. The limit is eight
  attempts with a growing random pause. Past that, the answer is *503, nothing changed*, never a lost edit.

## 4. Evidence

- **The lost edit is real.** `ASiteOfTwoNodesTests.Two_nodes_editing_one_3D_object_layer_at_once_lose_no_edit` sends
  fifteen adds to each of two nodes of one site at the same moment. With the write made unconditional again (the
  behaviour before this ADR), the layer kept **24 of 30**. With the compare-and-replace it keeps **30 of 30**.
  Measured 2026-11-07 against the scenes fixture's PostgreSQL.
- **The store's race is closed by PostgreSQL itself.** The condition `content = @expected` sits in the `update`'s own
  `where`. Under read committed, an update that waited on another writer's row lock evaluates its condition again
  against the row that writer left.
  `AResourceIsReplacedOnlyWhileItHoldsWhatWasReadTests.A_writer_waiting_on_another_finds_the_row_it_left_and_writes_nothing`
  holds that lock open in a transaction, confirms the second writer waits, commits, and gets `Changed`.
- **IfcConvert ships prebuilt for both architectures.** IfcOpenShell's release `ifcconvert-0.9.0post1`, published
  2026-10-15, has `linux64` and `linuxarm64` zips:
  - `ifcconvert-0.9.0post1-linux64.zip`: SHA-256 `7360c7dac5b69ec722b4bf100036c2ab2f96b74fa89ee99d33944e6cbf3ba9d6`;
  - `ifcconvert-0.9.0post1-linuxarm64.zip`: SHA-256 `0f0be070f3cdcaee74059bea5e5301a33cca30b19a73608924b86c0b4b76db3f`.

  Each zip holds `IfcConvert` and its shared libraries. Among them are the geometry kernel plugins
  `ifcopenshell_geometry_kernel_cgal.so` and `…_cgalsimple.so`, which are CGAL code. CGAL's 3D packages are
  **GPL-3.0-or-later** (CGAL's licence page). The default kernel is Open CASCADE (LGPL-2.1 with an exception). These
  facts were read from the zips' file lists. **IfcConvert has not been run here.** Running a downloaded third-party
  binary was refused by this session's permission settings, so nothing in this ADR measures its output.
- **Assimp already lists `.ifc` and `.ifczip`** (`assimp listext` on the installed 5.2). Its IFC importer reads IFC2x3
  STEP only. It is not measured here either.
- **USD has no prebuilt command-line converter to glb** under a licence we can carry, as far as this session found.
  OpenUSD (Apache-2.0 modified) is a C++ build with TBB that takes most of an hour. tinyusdz (Apache-2.0,
  `lighttransport/tinyusdz`, release `v0.9.1`) has no dependencies. Its `examples/usd_to_gltf` reads USDA, USDC and
  USDZ meshes into glTF. Its README calls it *for illustration*, with materials and textures not supported. Building it
  was refused by the same permission setting. **Not measured.**

## 5. Decision

### 5.1 One writer per site — built

- **The store.** `IItemResourceStore` gains `ReplaceIfAsync(item, path, expected, …)`. It replaces a resource only
  while it still holds the bytes the caller read, and answers `Changed` when another writer came first.
  `ItemResourceWrite.Changed` is new.
- **The edits.** A 3D object layer's `applyEdits` reads its state with the bytes it came from, applies the edit, and
  writes back through `ReplaceIfAsync`. On `Changed` it reads again and applies the same edit to what the other node
  left. After eight attempts it answers 503 with nothing changed. The in-process lock stays, so one node's own requests
  never retry against each other.
- **No new connection, lock or setting.** This uses the store every node already has, as `SiteSingletons` does
  (ADR-012 §2 A). An advisory lock held for the edit would also work. It would hold a pooled connection across the
  read-change-write, and it would serialise the readers of other layers' resources for nothing.

### 5.2 IFC and USD conversion — decided, not yet built

- **IFC is converted by IfcOpenShell's `IfcConvert` (LGPL-3.0-or-later), version 0.9.0post1, pinned by SHA-256 for both
  architectures.** It runs as Assimp does under ADR-282: a child process per file, `ulimit -v`, a time limit, and
  `GltfTriangles` checking what comes out.
  - **It ships without the two CGAL kernel plugins**, so that nothing GPL lands in the serving image beside our code.
    This is the rule [ADR-254](ADR-254-gdal-without-poppler.md) set for GDAL, and the plugins are files that can be
    left out.
  - `IfcConvert` must be measured to convert with the Open CASCADE kernel alone. That is condition 2.
- **USD is converted by a tool built from tinyusdz (Apache-2.0) at a pinned tag, in an image build stage.** OpenUSD is
  not used. **INFERRED**, for confirmation: the owner's message named neither library. The report offered *OpenUSD*.
  tinyusdz is chosen because OpenUSD's build costs most of an hour on every image and CI run, for a converter that
  needs only meshes and transforms.
- **DWG stays refused.**
  - LibreDWG is **GPL-3.0**, which ADR-254 keeps out of the serving image.
  - The Open Design Alliance's converter is proprietary and needs a membership.
  - **INFERRED**, for confirmation: *ikisini de* ("both") is read as both *questions*, and the first question's
    answer as converting what has a carriable library. A DWG converter would need either an owner licence decision
    (GPL beside our code) or a paid licence.

## 6. Consequences

- **State.** None new. The edit writes the same resource, now on condition.
- **Operations.** Two nodes editing one layer each wait a few milliseconds when they collide. One node behaves as
  before. When §5.2 is built, the serving image grows by IfcConvert's libraries (about 150 MB unpacked per
  architecture, less the CGAL plugins), and `DEPENDENCY-LICENSES.md` gains IfcOpenShell, Open CASCADE and tinyusdz.

## 7. Assumptions this decision rests on

- PostgreSQL's read-committed re-check of an update's condition after a row-lock wait. This is documented behaviour,
  and the test above measures it.

## 8. Dependencies

- [ADR-280](ADR-280-3d-object-layers-hold-meshes.md), [ADR-282](ADR-282-3d-models-are-converted-by-assimp-in-its-own-process.md),
  [ADR-254](ADR-254-gdal-without-poppler.md), ADR-012 §2 A.

## 9. Conditions

1. **The compare-and-replace, tested between two real nodes.**
   **DISCHARGED 2026-11-07**: `ReplaceIfAsync` exists, and the 3D object layer writes through it. The store's three
   tests and the two-node test pass. The two-node test fails, 24 of 30, with the condition taken out.
2. **IfcConvert, measured: our own IFC file converts to glb with the CGAL plugins left out, on both architectures.**
   - The binary is fetched by a script that checks it against the pinned hashes and refuses the CGAL plugins.
   - The converter and the formats list take `.ifc`.
   - A test converts a wall from `corpus/` and checks its triangles.
   - The Dockerfile, CI and the dev image carry it.
   - `DEPENDENCY-LICENSES.md` names it.

   Open: blocked on permission to run the downloaded binary in this session (§4).
   **PARTLY DISCHARGED 2026-11-20: built the same night as this ADR, measured on amd64 and on Windows x64, not on
   arm64.** The sentence above was left behind when a later session built every part listed (the propagation shape of
   D-130): `tools/ifcconvert-fetch.sh` (pinned hashes, the CGAL kernels and GMP/MPFR left out), `.ifc` in
   `ModelConverters`, the image (`deploy/server.Dockerfile`, both architectures, which is also what dev runs), CI, and
   [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) §IfcConvert and tinyusdz. What was measured, and where:
   - **amd64, Linux, every CI push**: the conformance job's step *the IFC and USD converters* lays IfcConvert out with
     the fetch script and converts `corpus/wall.ifc` to a non-empty glb; it passed on c4bc8b60 (run 44180733795,
     2026-11-17). Through the server, `SceneLayerSourceTests.A_3d_object_layer_converts_obj_collada_and_fbx_models_and_refuses_one_it_cannot_read`
     converts the wall by `uploadAssets`, and the server's `GltfTriangles` reads its triangles before keeping it; it
     passed in run 42379263052 on 911bad52 (2026-11-07), when the console suite still ran in CI (D-398).
   - **Windows x64, on this machine, 2026-11-20**: the same release's `win64` zip (SHA-256
     `963265186fec13ad2ba56edea614f32b715774ca1a5777fc8b84e5e5a35767ff`), with its two CGAL kernels deleted. Our wall:
     12 triangles, 4.00 x 3.00 x 0.20 m (its 4 x 0.2 x 3 m, Y up), 110 ms warm. A real Revit 2010 office model (IFC2x3,
     opensourceBIM's *HHS Office*, CC BY-ND 4.0): `construction.ifc`, 3.9 MB, became 63,624 triangles, 65.9 x 11.1 x
     53.9 m, in 10-13 s; `architect.ifc`, 13 MB, 423,466 triangles in 31 s at a peak of 156 MB. Both inside ADR-282's
     90 s and 2 GB.
   - **arm64: not measured.** The image's arm64 stage lays out the `linuxarm64` zip by the same script, and nothing runs
     it. Owed: one conversion of `corpus/wall.ifc` in the arm64 image (the VPS builds it), with the result written here.
3. **A tinyusdz-built converter, measured: our own USDA and USDZ convert to glb.** The same parts as condition 2.
   Open: blocked on permission to build third-party source in this session (§4).
   **DISCHARGED 2026-11-20**: built the same night as this ADR by a later session (`tools/usd2glb/`, built against
   tinyusdz at the commit `tools/usd2glb/TINYUSDZ` pins, checked after the clone; `.usda`, `.usdc` and `.usdz` in
   `ModelConverters`; the image's `usd2glb` stage for both architectures; CI; the licence register), and measured:
   - **amd64, Linux**: CI's step converts `corpus/box.usda` on every push (passed on c4bc8b60, run 44180733795); the
     console test named under condition 2 converted `box.usda` and `box.usdz` through the server in run 42379263052.
   - **Windows x64, 2026-11-20**: built from the same pinned commit with Zig 0.17.1's clang 22 (`TINYUSDZ_NO_WERROR`,
     since clang 22 turns tinyusdz's `-Weverything` into errors). Our box, as USDA and as USDZ: 12 triangles, x 1 to
     3 m, 0 to 3 m up, z -1 to 0 m, which is what `corpus/README.md` says a conversion that honours the stage's
     centimetres, its Z up and its transform gives. 101 ms warm.
   - A real USDZ (ADR-282 condition 2): the USD Working Group's *Carbon Frame Bike* (27 MB, CC BY-SA 4.0) became
     135,648 triangles, 1.93 x 1.22 x 0.61 m, in 2-3 s; their *McUsd* (a Mineways export) is refused by tinyusdz, which
     requires `doubleSided` to be `uniform`, with the converter's own sentence as the reason.
4. **The two INFERRED readings in §5.2 confirmed by the owner:** tinyusdz rather than OpenUSD, and DWG left refused.
