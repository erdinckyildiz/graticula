# ADR-282 — 3D models are converted to glb by Assimp, in a process of its own

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07. **That a converter is built is the owner's decision** ([ADR-281](ADR-281-owner-decisions-on-what-3d-left-partly-built.md) §5.1, *glb + converter*). The library and how it is run are this ADR's. |
| **Supersedes** | — |
| **Amended by** | [ADR-283](ADR-283-owner-decisions-on-conversion-and-one-writer-per-site.md) §5.2, by owner decision: IFC and USD are to be converted too; DWG stays refused. |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A 3D object layer stores glb ([ADR-280](ADR-280-3d-object-layers-hold-meshes.md)). Scene Viewer's editor uploads
OBJ, FBX, COLLADA, IFC, DWG, USD and glTF, and ArcGIS converts them on the server. Converting a 3D format is geometry
I/O: [build-vs-adopt](../build-vs-adopt-policy.md) **Tier 2**, an established library behind our own port. These
formats have long histories of parser faults, so a malformed or hostile file must not be able to take the server down.

## 2. Alternatives considered

### Alternative A — Assimp's command-line tool, run as a child process per file (chosen)

The Open Asset Import Library imports COLLADA, FBX, OBJ, IFC-STEP, glTF, PLY, STL, 3DS and, in later releases, USD. It
exports glTF 2.0 binary (`assimp export in.x out.glb -fglb2`). Its licence is **BSD-3-Clause** (its `LICENSE` file,
read 2026-11-07). Run as a separate process, it is **not linked** to anything of ours. A crash, a hang or a runaway
allocation ends that process, under a time and memory limit, and the server answers that the file could not be
converted.

### Alternative B — AssimpNet, linked into the server

**Rejected.** It is the same library inside the server's process, where a fault in a parser is a fault in the server.

### Alternative C — one library per format (IfcOpenShell for IFC, OpenUSD for USD, an FBX SDK)

**Not now.** IfcOpenShell is LGPL-3.0. The Autodesk FBX SDK is proprietary and not redistributable here. OpenUSD is
large. Assimp covers most of the list with one permissive library. A format it reads badly is a reason to add one of
these behind the same port.

### Alternative D — write our own readers

**Rejected**: this is Tier 2 work, and each format is a project of its own.

## 3. Counterarguments to the preferred option

- **Assimp's FBX import is experimental**, and Debian's 5.2.5 could not read back the binary FBX it had itself just
  written (§4). Real FBX files from 3ds Max or Revit have not been tried.
- **DWG is not read** by Assimp at all. It is refused, with the reason, rather than converted.
- **IFC import is IFC 2x3 STEP and partial.** A BIM model may lose parts; what is lost has to be said to the uploader.
- **A process per file costs a fork and a start** (milliseconds, against an upload of seconds).

## 4. Evidence

A prototype on 2026-11-07, with Debian bookworm's `assimp-utils` 5.2.5 (arm64) on the build host:

- An OBJ with a material, written by hand (a 10 × 20 × 4 m box in red), was converted to glb. A COLLADA and an ASCII
  FBX written from it by Assimp were also converted. All three glbs were the same 36-index box, with its extent and its
  red base colour.
- **Our reader had to change.** Assimp writes `FB_ngon_encoding` on every primitive, and `GltfTriangles` skipped any
  primitive with an extension. It now refuses only extensions that store the geometry another way (Draco, meshopt,
  quantization, splats). `SceneModelTests` covers both kinds.
- A binary FBX failed (*block offset is out of range*), as did a file of garbage named `.fbx`. Each failure exited
  with code 10 and a message, and nothing else was affected.

## 5. Decision

1. **A port**, `IModelConverter`: bytes and a format code (`3D_obj`, `3D_fbx`, `3D_dae`, `3D_ifc`, `3D_usd`, …) in;
   glb bytes or a refusal with its reason out. Nothing of Assimp's appears in its signature.
2. **The adapter runs `assimp export`** in a scratch directory per file, with a time limit and a memory limit, and
   reads the glb back with `GltfTriangles` before accepting it. An OBJ whose material file or textures come separately
   is uploaded as a zip, as Scene Viewer asks for, and unpacked into that directory with the same limits on entries and expanded size the server's other zip uploads have.
3. **The formats offered** are the ones measured to convert. Each other is refused with what to do instead (export to
   glb or OBJ).
4. **The server's image carries `assimp-utils`** (Debian's package, BSD-3), recorded in
   [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md).

## 6. Consequences

- **State.** None. Conversion is a request's work in a scratch directory, removed afterwards.
- One more native binary in the image, and its security updates are Debian's.

## 7. Assumptions this decision rests on

- Assimp's glTF 2.0 exporter writes plain float positions and indices. Measured for three files.

## 8. Dependencies

ADR-254 (no GPL linked), ADR-280, ADR-281 §5.1, build-vs-adopt Tier 2.

## 9. Conditions

1. **The port and the adapter**, with the limits, and a test per format offered. *(Partly discharged 2026-11-07:
   `IModelConverter` (Core) and `AssimpModelConverter` (Host) give 90 seconds, 2 GB of address space and a scratch
   directory per file. A zip is unpacked flat, with no path from it followed, within 512 MB. The glb is read back by
   `GltfTriangles` before it is accepted. When the tool is absent, nothing is offered. Tested end to end for OBJ, and
   for an unreadable FBX refused with its reason
   (`SceneLayerSourceTests.A_3d_object_layer_converts_an_obj_model_and_refuses_one_it_cannot_read`). COLLADA and FBX
   are converted by the same path and measured only in the prototype (§4); a test each is owed.)* *(Discharged the same
   night: the test, renamed `…converts_obj_collada_and_fbx_models_and_refuses_one_it_cannot_read`, converts the same box
   as COLLADA and as ASCII FBX from `tests/Graticula.Console.Tests/corpus/`, each kept under its own hash.)*
2. **Real files**: an FBX from a modelling tool, an IFC from a BIM tool and a USD, each converted or refused with its
   reason. *(Open.)* **PARTLY DISCHARGED 2026-11-20: each converted or refused, on Windows; the same run on the image's
   own tools is written and has not run yet.** `tools/real-models.py` fetches five public models at pinned commits,
   checks each by SHA-256, converts it by `ModelConverters`' own command line, and reads the glb back (triangles and
   the box it fills). Nothing of them is committed. Measured on this machine:

   | model | made by | licence | result |
   |---|---|---|---|
   | `maxPbrMaterial_metalRough.fbx` (Assimp's test models) | 3ds Max 2021, FBX SDK 2020 (ASCII FBX 7.7) | BSD-3-Clause | 12 triangles, 17.8 x 16.8 x 22.0, 4 ms |
   | `spider.fbx` (Assimp's test models) | Blender 2.76 (binary FBX) | BSD-3-Clause | 1,368 triangles, 15,059 x 7,974 x 19,338 in the file's own units, 8 ms |
   | `HHS Office/construction.ifc` (opensourceBIM/IFC-files) | Revit Architecture 2010 (IFC2x3) | CC BY-ND 4.0 | 63,624 triangles, 65.9 x 11.1 x 53.9 m, 10-13 s |
   | `CarbonFrameBike.usdz` (usd-wg/assets) | a production model for AR Quick Look | CC BY-SA 4.0 | 135,648 triangles, 1.93 x 1.22 x 0.61 m, 2-3 s |
   | `McUsd.usdz` (usd-wg/assets) | Mineways | CC BY-NC-SA 4.0 | **refused**: *"The file is not a USD stage this converter reads: Failed to parse USDA"* (tinyusdz requires `doubleSided` to be `uniform`) |

   - **With which tools.** IFC by IfcConvert 0.9.0post1's `win64` build without its CGAL kernels, and USD by `usd2glb`
     built here from the pinned tinyusdz commit: the shipped versions, on another platform. **FBX by Assimp 6.0.6's
     Windows library** through its C API (`aiImportFile`, then `aiExportScene` as `glb2`), because the Windows release
     ships no command-line tool. **That is not the image's Assimp**, which is Ubuntu's packaged `assimp-utils`, and
     §4 saw Debian's 5.2.5 fail on a binary FBX. So the FBX rows say what Assimp can do, not yet what the image does.
   - **What the server said.** The refused USDZ showed that the server's reason would have been a tinyusdz source path,
     not usd2glb's sentence: `ModelConverters.Reason` now takes the converter's own sentence first
     (`AModelConverterSaysWhyTests`, three tests).
   - **The spider's size** is in its file's units, scaled by nothing. A model in centimetres would be placed a hundred
     times too large. Whether to apply FBX's unit scale is for a real upload to show, not this run.
   - **Owed**: CI's new step *real 3D models through the converters* runs the same script on the image's tools (amd64)
     on the next push. The FBX rows accept either answer there, converted or refused with a reason, and print which;
     its output is to be written here, and then this condition is discharged.
3. **The image and the licence register** carry `assimp-utils`. *(Discharged 2026-11-07: `deploy/server.Dockerfile`,
   the CI job with a server, and [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) §Assimp.)*
4. **Behind the mesh layer's `uploadAssets`** (ADR-280), a non-glb asset type is converted there. *(Discharged
   2026-11-07: converted, and kept under the hash of what was uploaded, which is the hash the client's asset map names.
   The layer's `editFormats` lists what the converter takes.)*
