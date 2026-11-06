# ADR-282 — 3D models are converted to glb by Assimp, in a process of its own

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07. **That a converter is built is the owner's decision** ([ADR-281](ADR-281-owner-decisions-on-what-3d-left-partly-built.md) §5.1, *glb + converter*). The library and how it is run are this ADR's. |
| **Supersedes** | — |
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

1. **The port and the adapter**, with the limits, and a test per format offered. *(Open.)*
2. **Real files**: an FBX from a modelling tool, an IFC from a BIM tool and a USD, each converted or refused with its
   reason. *(Open.)*
3. **The image and the licence register** carry `assimp-utils`. *(Open.)*
4. **Behind the mesh layer's `uploadAssets`** (ADR-280), a non-glb asset type is converted there. *(Open, waits on the
   mesh layer.)*
