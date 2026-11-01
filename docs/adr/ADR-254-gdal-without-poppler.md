# ADR-254 — The import reader's GDAL is conda-forge's `libgdal-core`, without the PDF driver and without Poppler

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-01, by owner decision on [D-380](../architecture-debt.md): *"PDF'siz GDAL'a geç"* — move to GDAL builds without the PDF driver and Poppler, so that no copyleft library ships, and make the documents true again. The choice of *which* build is this ADR's, made by measurement below. |
| **Supersedes** | the package half of [ADR-037](ADR-037-job-workers-come-in-two-kinds.md) §5a (`MaxRev.Gdal.Core` and its runtime packages). The child process, and the reason for it, are unchanged. |
| **Superseded by** | — |

---

## 1. Context

D-380 measured on 2026-11-01 that the GDAL the import reader loads — `MaxRev.Gdal.Core` 3.13.1.534 with
`MaxRev.Gdal.WindowsRuntime.Minimal` and `MaxRev.Gdal.LinuxRuntime.Minimal` — builds its PDF driver on **Poppler,
GPL-2.0-or-later**, and that [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) had said PDFium. The product's
outbound licence ([ADR-180](ADR-180-the-outbound-licence-is-the-community-licence.md)) is source-available and forbids
modification; a GPL library in the distributed image is the conflict [ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md)
§4 had measured the project free of. The owner decided: a GDAL without the PDF driver.

**What the inventory found** (2026-11-01, read-only, in the published `ghcr.io/erdinckyildiz/graticula:1.0.491`, arm64):

| Where GDAL comes from in the server image | Version | Poppler? |
|---|---|---|
| `/app/importer/runtimes/{linux-x64,linux-arm64,win-x64}` — MaxRev's payload, all three platforms in every image | 3.13.1 | **yes**: `libpoppler.so.151` (both Linux), `poppler.dll` (Windows), 155 / 159 / 108 MB; `/app/importer` is 429 MB |
| `/opt/grapy/.../pyogrio.libs/libgdal-8aa8b828.so.38` (grapy's wheel) | 3.12.4 | **no** — no Poppler file, no `poppler` string in the library; its `PDF` driver is GDAL's write-only one |
| `/opt/grapy/.../rasterio.libs/libgdal-c14cf0f0.so.38` (grapy's wheel) | 3.12.2 | **no** — the same |
| `ghcr.io/erdinckyildiz/graticula-datastore` (PostGIS) | Debian's 3.6.2 | **yes**: `libpoppler.so.126`, through Debian's `libgdal32`, which PostGIS raster loads — [D-383](../architecture-debt.md) |

And one fact that rules out the cheap fix: MaxRev's **SWIG wrappers themselves** (`libgdal_wrap.so` and the other
three) name `libpoppler.so.151` as a needed library, not only `libgdal`. Deleting the file stops GDAL loading at all,
as D-380 said; keeping MaxRev's wrappers and replacing only `libgdal` would keep Poppler as a needed library.

## 2. Alternatives considered

### Alternative A — build GDAL from source in the Dockerfile, `-DGDAL_USE_POPPLER=OFF`

**Argument for.** Complete control of the driver set and of every library linked; the build is the bill of materials.

**Argument against.** The release builds amd64 and arm64 with QEMU (`release.yml`): a GDAL compile under emulation is
hours, and its dependencies (PROJ, GEOS, libtiff, netCDF, HDF4/5, OpenJPEG, libkml, xerces) would each have to be built
or taken from noble's `-dev` packages, whose `libgdal` is the Debian one that links Poppler. The C# wrappers would have
to be built too (SWIG, against the same headers), and Windows development would need its own build with MSVC. This
machine cannot run Docker, so none of it could be verified before CI.

### Alternative B — another NuGet package

**Argument for.** Smallest change: swap one package for another.

**Argument against.** None measured fits. `MaxRev.Gdal.*` has no build without Poppler (its "Minimal" is the one that
links it). `GDAL`/`GDAL.Native` (3.12.1, Windows only) and `ThinkGeo.Gdal.*` were not adopted: Windows-only, or a
vendor's build whose driver set is not published as a bill of materials.

### Alternative C — conda-forge `libgdal-core` and `gdal-csharp`, laid out by a pinned lock (chosen)

conda-forge splits GDAL into **`libgdal-core`** and one package per plugin driver. The PDF driver is the plugin
`libgdal-pdf` — the only package that depends on `poppler` — and `libgdal-core` does not depend on it. **`gdal-csharp`**
is GDAL's own C# binding: its four wrappers need nothing but `libgdal.so.39` and the C/C++ runtime (read from their
ELF and PE headers), and its win-64 build carries the SWIG-generated C# sources.

**Argument for.** Prebuilt for linux-64, linux-aarch64 and win-64 from one recipe, so the server image, CI and the
Windows development machine run the same GDAL. Nothing is compiled: the Docker stage only downloads, checks hashes and
copies, on the build platform, for both architectures. Every package declares its licence in its metadata, so the lock
can refuse a GPL one mechanically. The plugins the product needs (JPEG 2000, NetCDF, HDF4, HDF5 — ADR-157) are packages
of their own, and leaving PDF out is leaving one package out.

**Argument against.** See §3.

## 3. Counterarguments to the preferred option

- **conda-forge's closure is not automatically clean.** `libgdal-core` depends on `libspatialite`, whose default build
  links **librttopo, GPL-2.0-or-later**; and `proj` and `libspatialite` declare `sqlite`, the command-line program,
  which brings **readline, GPL-3.0**. A naive "install libgdal-core" ships both. The lock resolves this by choosing
  conda-forge's `lgpl_` builds of `libspatialite` and `libarchive` (which leave librttopo out) and by not following the
  `sqlite` program; the tool refuses the closure if any package's licence is GPL without a linking exception, so this
  cannot regress silently. It is still a dependency on a third party's packaging choices.
- **The C# binding is built on one platform and run on three.** The SWIG sources come from the win-64 package and are
  compiled into the reader for every platform. They are platform-independent C#, but nothing guarantees conda-forge's
  three builds ran the same SWIG — so the tool checks, for each platform, that every entry point the C# imports from a
  wrapper is exported by that platform's wrapper, and fails the build otherwise.
- **conda-forge targets an older glibc and its own libstdc++.** The .NET runtime has already loaded noble's libstdc++
  (GCC 14) before GDAL loads, so a library needing a newer one would fail at load. Measured: the closure's highest
  requirement is `GLIBCXX_3.4.30`; the tool fails the build above noble's `3.4.33`.
- **Parquet is lost.** The Arrow plugin (`libgdal-arrow-parquet`) brings libarrow 26 with Azure and Google Cloud storage
  clients, protobuf, abseil, ORC and a tracing library. The reader's `convert` operation — Q-74's GeoParquet copy —
  was its only user, and nothing had sent `convert` since ADR-038 moved features onto the pipe as NDJSON. It is
  retired rather than carried; ADR-037 condition 3's closed set shrinks by one.
- **The download happens at build time.** As MaxRev's did through NuGet restore; the lock pins every file by SHA-256,
  and a mismatch fails the build rather than using what arrived.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| MaxRev's wrappers need Poppler, not only its `libgdal` | `ldd libgdal_wrap.so` in the 1.0.491 image lists `libpoppler.so.151` | inventory run, 2026-11-01 |
| grapy's wheels carry no Poppler | no Poppler file in any `*.libs`; `grep -c poppler` on both `libgdal` copies is 0 | the same |
| `gdal-csharp`'s wrappers need only `libgdal` | ELF `NEEDED`: `libgdal.so.39`, `libstdc++`, `libgcc_s`, `libc`; PE imports: `gdal.dll` and the MSVC runtime | `tools/gdal-natives.py` and a header read |
| The closure is GPL-free | 72 packages per Linux platform, 58 on Windows; licences are MIT, BSD, Apache-2.0, Zlib, ISC, MPL-1.1, LGPL-2.1 (GEOS, libiconv, keyutils, libnsl) and GCC's runtime exception; none GPL-only | `deploy/gdal/gdal-natives.lock.json` |
| What ships | linux-x64: 76 libraries, 153.6 MB; linux-arm64: 76, 159.0 MB, from 57 packages, with each package's licence files and `PACKAGES.txt` | `tools/gdal-natives.py fetch` |
| It loads in the server image, on arm64 | the reader built for linux-arm64, run in the 1.0.491 image: `ping` (GDAL 3.13.3, OpenFileGDB), `fixture` (a geodatabase with a domain, a relationship class, a table and attachments), `layers`, `relationships`, `raster` on `first.jp2` (JP2OpenJPEG) and `steps.nc` (netCDF, three dated images); `ldd` on all 80 objects: nothing unresolved, no Poppler, no librttopo | probe run on the VPS, 2026-11-01 |
| No PDF driver at all | `drivers` lists neither `PDF` nor any PDF backend, on Windows and on arm64 | the same |
| Size | `/app/importer` 429 MB (three platforms) → about 170 MB (one platform: the reader with `gdal/linux-arm64`, 167 MB) | `du -sm`, both |
| Windows development | the reader built on Windows answers `ping` with GDAL 3.13.3; `GeodatabaseReaderTests` and the reader paths of `Graticula.Host.Tests` pass | test runs, 2026-11-01 |

## 5. Decision

The import reader's GDAL is conda-forge's **`libgdal-core` 3.13.3** with the plugins **`libgdal-jp2openjpeg`,
`libgdal-netcdf`, `libgdal-hdf4`, `libgdal-hdf5`**, and its binding is **`gdal-csharp` 2.1.6** — GDAL's own SWIG
output, compiled into `Graticula.Import.Reader`, and its four wrappers. **Never `libgdal-pdf`, never the `libgdal`
metapackage.** Every package is pinned by URL and SHA-256 in `deploy/gdal/gdal-natives.lock.json`, which
`tools/gdal-natives.py lock` writes from conda-forge's repodata and a person commits. `tools/gdal-natives.py fetch`
lays out exactly the libraries the wrappers and plugins need, transitively, in `gdal/<rid>/` beside the reader, with
GDAL's and PROJ's data and every package's licence files, and refuses a GPL package, a file named Poppler, PDFium,
PoDoFo or librttopo, a missing entry point, or a library above noble's libstdc++. The reader loads it by full path
(`GdalNative`) and points `GDAL_DRIVER_PATH` at its own `gdalplugins`, so a PDF plugin present elsewhere on a host is
never loaded. The server image carries one architecture's payload. A PDF uploaded as imagery is refused by name.

## 6. Consequences

**Positive.** No Poppler, and no GPL library, in what the server image ships for GDAL — checked by the tool and again
by the Dockerfile on the artefact. The server image loses the Windows payload it never ran and the other architecture's;
`/app/importer` falls from 429 MB to about 170 MB. One GDAL version (3.13.3) on Windows, CI and both images. The
licence notices now travel with the libraries (`gdal/<rid>/licenses/`), which MaxRev's payload did not carry.

**Negative.** GeoParquet output from the reader is gone (`convert`). A GeoPDF cannot be imported as imagery — it never
could through the host, which accepted only ADR-157's extensions; it is now refused by name. Building the reader needs
Python 3 and network access on first build (the Docker stage has both; the SDK image has neither and is given the
laid-out directory). Upgrading GDAL is now a `lock` run and a review of its diff, not a version bump.

**State.** None in the catalogue. At runtime the reader holds the loaded libraries and GDAL's configuration
(`GDAL_DATA`, `GDAL_DRIVER_PATH`, PROJ's search path), node-local, set once when the process starts.

**Ports created.** None new: GDAL stays behind the reader's JSON-lines protocol, in a child process, and
`NativeDependencyTests` now recognises it by `tools/gdal-natives.py` rather than by a package prefix.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | conda-forge keeps publishing `libgdal-core` without the PDF driver and `lgpl_` builds of libspatialite | measured 2026-11-01; the lock pins what was measured, and `lock` refuses a GPL closure when re-run |

## 8. Dependencies

**Depends on:** [ADR-037](ADR-037-job-workers-come-in-two-kinds.md) §5a (GDAL in a child process), ADR-157 (the imagery
formats), [ADR-180](ADR-180-the-outbound-licence-is-the-community-licence.md) (why copyleft matters).

**Depended on by:** ADR-106 and ADR-107 (export through the reader), ADR-213 (attachments and relationships), ADR-065
(domains), ADR-009 condition 1 (the `tiff` door, which does not use GDAL).

## 9. Revisit triggers

- A product feature needs a PDF reader (D-321's PDF thumbnail): it is PDFium (BSD-3-Clause/Apache-2.0) or nothing,
  and it is a separate decision.
- GeoParquet output is asked for again: it is the Arrow plugin and its closure, measured, or DuckDB, which the server
  already carries.
- conda-forge's `libgdal-core` gains a dependency on a GPL package: `lock` fails, and this ADR is reopened.

## 10. Conditions

1. **CI's image build verifies what this machine could not.** The release and quickstart jobs build the image for
   amd64 and arm64; the build fails unless `/app/importer/gdal/linux-{x64,arm64}/lib/libgdal.so.39` exist, the
   licences file is there, and no file named Poppler, PDFium or librttopo is anywhere under `/app`. Discharged when a
   green release run is recorded here with the image size beside 1.0.491's. **DISCHARGED 2026-11-01**: release run 41296322182 (v1.0.492) green on amd64 and arm64 with every check above; the arm64 image is 747 MB against 1.0.491's 829 MB; on the showcase, `find /app -iname '*poppler*'` finds nothing, `ldd` resolves every library of `libgdal.so.39`, and the reader answers `{"op":"ping"}` with GDAL 3.13.3.
2. **The conformance suite's imagery test passes against an image built from this**, not only against a Windows
   fixture: JPEG 2000, NetCDF, the ASCII grid, and the PDF and MrSID refusals. **DISCHARGED 2026-11-01**: a
   throwaway deployment on the showcase VPS (arm64) from the published `graticula:1.0.492` and
   `graticula-datastore:1.0.492` images, set up as the quickstart does (keygen, `migrate --apply` to schema 125,
   setup token, administrator), reached through an SSH tunnel; `ImageryFormatsConformanceTests` passed from a Windows
   test runner against it — the JPEG 2000 read back 12, the NetCDF's three steps became three dated images with
   `time` choosing between them, the ASCII grid was published as 4326 and read back 7, MrSID was refused naming the
   SDK and the PDF naming Poppler. The other imagery classes ran beside it (`ImageryUploadTests`,
   `ImageryDisplayTests`, `ImageryGroupSharingTests`, `MultidimensionalConformanceTests`): 16 of 17 passed. The one
   failure, `An_image_registered_without_overviews_is_given_them_beside_its_file`, registers a file the test writes to
   its own temporary folder, and its own comment states the precondition that the server is on the same machine; a
   server in a container on another host answered *This server cannot see* that path, which is the refusal it should
   give. The server's log held no unhandled error, and the probe's containers, network and volumes were removed.

## 11. Dissent

None recorded. The trade the owner accepted is stated in §6: no PDF import, and no GeoParquet from the reader.
