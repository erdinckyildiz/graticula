# Dependency Licences

Required by §55.

**Outbound licence: Graticula Community License 1.1** — owner decision 2026-10-13,
[ADR-180](docs/adr/ADR-180-the-outbound-licence-is-the-community-licence.md), replacing the Elastic
License 2.0 of [ADR-047](docs/adr/ADR-047-the-outbound-licence-is-elastic-2.md)
(2026-08-25), which replaced the Apache-2.0 taken on 2026-08-13 under Q-73. See
`LICENSE` and `NOTICE`. **The compatibility answer below carries over unchanged:** the
new licence forbids modification where ELv2 forbade hosting, and both are restrictions a
copyleft dependency would forbid and no permissive one does. The column still says
*ELv2* because that is what it was measured against; the measurement is the same for
either.

**What changed for this file, and what did not.** The outbound licence decides what
*we* promise; it does not touch what our dependencies require, so every attribution
and bill-of-materials obligation below is unchanged. What it does change is the
compatibility question: ELv2 adds a restriction, and a copyleft dependency linked into
the artefact would forbid adding one. **Measured 2026-08-25: nothing linked here is
copyleft** — every referenced package is MIT, BSD-2, BSD-3, Apache-2.0 or the
PostgreSQL Licence, and GDAL's fourteen-component bill below is permissive throughout.
GEOS (LGPL) and PostGIS (GPL-2.0-or-later) are a separate process and are not linked.
Every inbound dependency must be compatible with redistribution under Apache-2.0.
**This makes one class of dependency newly disqualifying: GPL and AGPL components
cannot be linked into anything we ship**, because we cannot sublicense them under
Apache-2.0. LGPL remains usable via the Tier 2 port layer. The note below records
the earlier *inbound* posture, which predates the outbound choice and is now
constrained by it.

**Project licensing posture:** ~~open source; copyleft (GPL/AGPL) is acceptable
to the project owner. There is therefore no *exclusion* pressure on dependencies.
This register exists to track **obligations**, not to filter candidates.~~

**Corrected 2026-08-15 by the §66 licensing gate.** That paragraph describes the
*inbound* posture from before Q-73, and Q-73's Apache-2.0 outbound choice
reverses its conclusion: GPL and AGPL components cannot be linked into anything
shipped, so this register **does** filter candidates as well as tracking
obligations. Left struck rather than deleted, because the earlier position is why
some research notes weigh licences the way they do.

---

---

## §66 Licensing review gate — RUN 2026-08-15, and it passes

**Every dependency verified from the package itself**, by reading the `<license>`
expression in the shipped `.nuspec` rather than from documentation, a website, or
memory. That distinction is the whole point of the gate: a licence somebody
wrote down is a claim, and a licence in the artefact being redistributed is the
fact.

| Package | Version | Licence | Verified from | Compatible with ELv2 outbound |
|---|---|---|---|---|
| Npgsql | 9.0.2 | **PostgreSQL** (BSD-style, permissive) | nuspec `<license type="expression">` | **Yes.** Permissive, attribution only |
| Konscious.Security.Cryptography.Argon2 | 1.3.1 | **MIT** | nuspec | **Yes** |
| xunit | 2.9.2 | **Apache-2.0** | nuspec | **Yes**, and identical to outbound |
| xunit.runner.visualstudio | 2.8.2 | **Apache-2.0** | nuspec | **Yes** |
| Microsoft.NET.Test.Sdk | 17.12.0 | **MIT** | nuspec | **Yes** |
| Microsoft.Extensions.TimeProvider.Testing | 9.0.0 | **MIT** | nuspec | **Yes** |
| Microsoft.AspNetCore.TestHost | 9.0.0 | **MIT** | nuspec `<license type="expression">` | **Yes** — added 2026-09-13, ADR-068, test-only, in-process pipeline tests for response compression |
| **SkiaSharp** | 3.119.2 | **MIT** | nuspec `<license type="expression">MIT</license>` | **Yes** |
| **SkiaSharp.NativeAssets.Linux** | 3.119.2 | **MIT** | same package family, same expression | **Yes** |
| **SkiaSharp.NativeAssets.Linux.NoDependencies** | 3.119.2 | **MIT** | same package family, same expression | **Yes** |
| **BitMiracle.LibTiff.NET** | 2.4.660 | **BSD-3-Clause**, with **IJG JPEG** and **libtiff** BSD-style notices bundled | the project's own licence page, cited from its nuspec `licenseUrl` | **Yes**, and the three notices go in `NOTICE` |
| **DejaVu Sans** (font, redistributed) | 2.37 | **Bitstream Vera** (permissive) + public-domain DejaVu changes | the licence text shipped in [tools/fonts/LICENSE-DejaVu.txt](tools/fonts/LICENSE-DejaVu.txt) | **Yes**, with one obligation that is not the usual one — see below. **Redistributed twice since 2026-08-25**: as the SDF glyphs under `glyphs/` (ADR-027) and now embedded in `Graticula.Render.Skia` as the drawn face ([D-161](docs/architecture-debt.md)). One file, one licence, one obligation — the renderer was given the font the tile face already used rather than a second family |
| **Noto Sans**, **Noto Sans Arabic**, **Hebrew**, **Armenian**, **Georgian**, **Devanagari**, **Bengali**, **Tamil**, **Thai** (fonts, redistributed) | 2.015 · 2.013 · 3.001 · 2.008 · 2.005 · 2.007 · 3.011 · 2.004 · 2.002, each read from the file's own `name` table | **OFL-1.1** (SIL Open Font License) | each file's `name` table (IDs 0, 13 and 14: *"This Font Software is licensed under the SIL Open Font License, Version 1.1"*), and the text shipped in [tools/fonts/OFL.txt](tools/fonts/OFL.txt); files pinned by SHA-256 in [tools/fonts/stack.json](tools/fonts/stack.json), fetched from `notofonts/notofonts.github.io` at commit `3ec599d8` | **Yes** — added 2026-09-29, [ADR-100](docs/adr/ADR-100-labels-in-more-scripts.md). Redistributed like DejaVu, twice: as glyph ranges under `glyphs/` and embedded in `Graticula.Render.Skia`. See *The fonts are not like the other rows* |
| **Noto Sans CJK SC** (font, redistributed **only by an image built with `GLYPHS_CJK=1`**) | 2.004 | **OFL-1.1** | the file's `name` table (ID 13) and the `LICENSE` at the `Sans2.004` tag of `notofonts/noto-cjk`; pinned by SHA-256 in [tools/fonts/stack.json](tools/fonts/stack.json), **not checked in** | **Yes** — [ADR-100](docs/adr/ADR-100-labels-in-more-scripts.md) §4. The default image does not carry it; one that opts in carries the `.otf` in `/app/fonts` beside `OFL.txt` |
| **SkiaSharp.HarfBuzz** | 3.119.2 | **MIT** | nuspec `<license type="expression">MIT</license>` | **Yes** — added 2026-09-29, [ADR-100](docs/adr/ADR-100-labels-in-more-scripts.md): the raster face's shaper |
| **HarfBuzzSharp** and **HarfBuzzSharp.NativeAssets.Linux** | 8.3.1.3 | **MIT** (the binding); HarfBuzz itself is under its *"Old MIT"* notice | nuspec `<license type="expression">MIT</license>` | **Yes**. The Linux library needs only libc, libm and libpthread (its ELF `NEEDED` entries, read 2026-09-29), so the air-gapped image needs no system package for it |
| **OGC and W3C XML Schemas** (test data, **not shipped**) | as fetched 2026-10-13 | **OGC Software License 1.0** (`schemas.opengis.net`, `INFERRED` — see ADR-176 condition 4) and **W3C Software and Document License 2023** (`xlink.xsd`, `xml.xsd`) | each file's own header, and OGC's and W3C's licence pages, both texts copied in full into [tests/Graticula.Host.Tests/Schemas/NOTICE.md](tests/Graticula.Host.Tests/Schemas/NOTICE.md) | **Yes** — added 2026-10-13, [ADR-176](docs/adr/ADR-176-ogc-api-styles.md) condition 4. Permissive with notice: the headers are kept unmodified and both notices sit beside the files. Test-only: copied to the test output, read by `OgcStylesTests`, in no image |

> **AMENDED 2026-08-21. Four packages were shipping and not in this table.**
> The gate ran on 2026-08-15 and passed on six packages; SkiaSharp and its two
> native-asset packages arrived on 2026-08-20 with
> [ADR-041](docs/adr/ADR-041-the-map-renderer.md) and nothing brought them here, so the
> row in [architecture-completeness.md](docs/architecture-completeness.md) said `PASS`
> about a dependency set that had grown by three. **All four are verified now from
> their own nuspecs** — the same standard the original six were held to — and all four
> are MIT or BSD-3, so the result does not change. What changed is that it is checked
> rather than assumed.
>
> **This is [D-126](docs/architecture-debt.md) again**, in the register a licensing
> gate reads: a decision shipped and the document that describes its consequences was
> not told. The repayable form there is a check, and a dependency added without a row
> here is exactly the kind of thing a check would catch.

**Result: pass.** No GPL or AGPL component is linked into anything shipped,
which is the one class the Apache-2.0 outbound choice (Q-73) made disqualifying.
Four of the six are test-only and never ship at all.

### What the gate found beyond the table

**The register described a project that no longer exists.** Its own preamble
still said *"copyleft (GPL/AGPL) is acceptable to the project owner. There is
therefore no exclusion pressure on dependencies… This register exists to track
obligations, not to filter candidates."* Q-73 chose Apache-2.0 outbound on
2026-08-13, which makes GPL and AGPL disqualifying — so the register's stated
purpose was the opposite of its actual one. Corrected below.

**Two Tier 2 candidates were avoided for reasons that were never licence
reasons, and it is worth saying so.** NetTopologySuite appears in
`/benchmarks` and in no shipped assembly, because [ADR-021](docs/adr/ADR-021-tile-encoding.md)
retired the in-process encoder — a measurement outcome, not a licensing one. A
.NET projection library was rejected by [ADR-022](docs/adr/ADR-022-geometry-server.md)
§4 because two coordinate engines disagree by metres, also not a licensing
reason. **The shipped dependency list is six packages, and the two largest
candidates were declined on evidence.**

**What this gate did not check:** transitive dependencies. The six above are
direct; Npgsql and the test packages pull others, and `dotnet list package
--include-transitive` would enumerate them. That is the honest limit of a gate
run by reading six nuspecs, and it is recorded rather than implied — see
[D-06](docs/architecture-debt.md), which is narrowed rather than closed.

---

### The font is not like the other rows

**Added 2026-08-15 with [ADR-027](docs/adr/ADR-027-glyphs-and-sprites.md).** The
vector tile service ships signed-distance-field glyphs so that a style can draw a
label at all, and they are generated from **DejaVu Sans**. Two things make this
row different from every other one in the table:

- **The artefact is redistributed, not linked.** The `.ttf` is in the repository
  ([tools/fonts/DejaVuSans.ttf](tools/fonts/DejaVuSans.ttf)) and its glyph
  outlines are baked into the `.pbf` ranges the server serves. That is
  distribution of the Font Software, which is exactly what the licence governs.
- **The notice must travel with it.** Bitstream Vera requires the copyright and
  permission notice to be included in all copies, so
  [tools/fonts/LICENSE-DejaVu.txt](tools/fonts/LICENSE-DejaVu.txt) sits beside
  the font rather than only being referenced here.

One condition of that licence is worth stating because it constrains a future
change: **modified versions may not keep the Bitstream or Vera names.** If
anybody ever subsets or edits this font, the derived font must be renamed — and
the font stack name the server serves it under is user-visible, so that rename
would reach the API.

The generated ranges are derived from the outlines and are covered by the same
permission. They are not a separate licence question, and they are not a
different obligation.

### The Noto fonts, since 2026-09-29

**[ADR-100](docs/adr/ADR-100-labels-in-more-scripts.md) added nine Noto families to the
stack, and Noto Sans CJK to an image that asks for it.** Everything said above about
DejaVu holds for them in the SIL Open Font License's own terms:

- **Redistributed, not linked**: the `.ttf` files are in
  [tools/fonts](tools/fonts), their outlines are in the glyph ranges, and they are embedded
  in `Graticula.Render.Skia`.
- **The licence travels with them.** OFL-1.1 requires the copyright notice and the licence
  with every copy of the Font Software. [tools/fonts/OFL.txt](tools/fonts/OFL.txt) carries
  both — each file's copyright line as its own `name` table states it, then the licence —
  and the serving image carries it at `/app/fonts/OFL.txt`.
- **The fonts may not be sold by themselves.** They are not: they ship inside a server, which
  OFL §1 permits.
- **A modified font may not keep a Reserved Font Name.** None of these files declares one;
  nothing here modifies them, and the served stack keeps the name `DejaVu Sans Regular`,
  which is a stack name and not a font's.

## grapy's Python wheels — the image's `/opt/grapy`, added 2026-10-29

[ADR-223](docs/adr/ADR-223-user-tools-run-sandboxed-on-grapy.md) §5.6 and
[ADR-224](docs/adr/ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) put a curated wheel set in the server
image for users' toolboxes. **None of them is linked into anything .NET.** They are installed into a virtual
environment that only the tool runner's child process loads. This section was missing until 2026-10-29: the first
seven wheels went into the image with ADR-223, and no row was written for them. Licences were read from each project's
PyPI metadata (`license_expression`, or the classifier where that is all there is) on 2026-10-29.

| Package | Version read | Licence | Compatible with the outbound licence |
|---|---|---|---|
| numpy | 2.5.4 | BSD-3-Clause AND 0BSD AND MIT AND Zlib AND CC0-1.0 | **Yes**, all permissive |
| pandas | 3.1.0 | BSD-3-Clause | **Yes** |
| shapely | 2.2.0 | BSD-3-Clause; its wheel bundles **GEOS**, LGPL-2.1, as a shared library | **Yes**: LGPL, dynamically loaded, unmodified |
| pyproj | 3.8.0 | MIT; its wheel bundles **PROJ**, MIT | **Yes** |
| pyarrow | 26.0.0 | Apache-2.0 | **Yes** |
| pyogrio | 0.13.0 | MIT; its wheel bundles **GDAL**, MIT-style (see *GDAL* below for the components) | **Yes** |
| geopandas | 1.2.0 | BSD-3-Clause | **Yes** |
| rasterio | 1.5.2 | BSD-3-Clause; its wheel bundles GDAL as pyogrio's does | **Yes** — added with `grapy.sa` (ADR-224) |
| scipy | 1.18.1 | BSD-3-Clause (the classifier says *BSD License*) | **Yes** — added with `grapy.sa` (ADR-224) |
| laspy | 2.7.0 | BSD-2-Clause (`license_expression`); pure Python over numpy, no bundled native code. Its LAZ backends (lazrs, laszip) are optional extras and are **not** installed | **Yes** — added with grapy's LAS datasets (ADR-229) |
| xarray | 2026.9.0 | Apache-2.0 (`license_expression`); pure Python over numpy and pandas, which the image already has, and packaging (Apache-2.0 OR BSD-2-Clause) | **Yes** — added with grapy's multidimensional rasters, for RasterToXarray and XarrayToRaster (ADR-236) |
| manifold3d | 3.5.4 | Apache-2.0 (the classifier, and the wheel's `licenses/LICENSE`). One compiled extension, no Python dependencies but numpy; the Manifold library builds Clipper2 in, which is BSL-1.0. Wheels for CPython 3.9–3.14 on Windows (x64, arm64), Linux (manylinux_2_28 x86_64 and aarch64) and macOS | **Yes** — added with grapy's multipatch features (ADR-238): the booleans on closed solids |

| torch | 2.15.0+cpu | `Apache-2.0 AND Apache-2.0 WITH LLVM-exception AND BSD-2-Clause AND BSD-3-Clause AND BSL-1.0 AND ISC AND MIT AND Zlib` (`license_expression`); PyTorch's own `LICENSE` is BSD-3-Clause, and its `licenses/third_party` folder holds the bundled libraries' — sleef, XNNPACK, fbgemm, oneDNN (ideep), protobuf, pybind11, the LLVM OpenMP runtime and others — every one permissive (the two mentions of GPLv2 are the LLVM exception's compatibility clause). Read from the Windows CPU wheel on 2026-10-31, and from the two Linux CPU wheels the image installs (`manylinux_2_28` x86_64 and aarch64, cp312) the same day: the two have byte-identical `LICENSE` files, the same 142 files under `licenses/third_party` and the same `License-Expression`, which the Windows wheel's matches. **The Linux wheels also carry shared libraries that no licence file in the wheel covers** — see *The Linux wheels' bundled libraries* below (ADR-248 condition 3) | **Yes** — added with grapy's deep learning (ADR-248), CPU builds from `download.pytorch.org/whl/cpu` |
| torchvision | 0.29.2+cpu | BSD-3-Clause (the wheel's `LICENSE`; the metadata says only *BSD*). **Its pretrained weights are not code under this licence and are not installed**: torchvision's README says they *"may have their own licenses or terms and conditions derived from the dataset used for training"* (the SWAG models are CC-BY-NC 4.0), and grapy builds every network with `weights=None` (ADR-248 §5.4) | **Yes** — added with ADR-248 |
| pillow, sympy, mpmath, networkx, jinja2, MarkupSafe, filelock, fsspec, typing_extensions | as torch requires them | MIT-CMU (pillow), BSD-3-Clause (sympy, networkx, MarkupSafe, fsspec, mpmath's *BSD License*, jinja2's *BSD License*), MIT (filelock), PSF-2.0 (typing_extensions) — torch's and torchvision's own dependencies, read from their metadata on 2026-10-31 | **Yes**, all permissive |

### The Linux wheels' bundled libraries — read 2026-10-31

ADR-248 condition 3. The wheels were downloaded from `download.pytorch.org/whl/cpu` into an empty folder
(`torch-2.15.0+cpu-cp312-cp312-manylinux_2_28_{x86_64,aarch64}.whl`,
`torchvision-0.29.2+cpu-py3-none-manylinux_2_28_{x86_64,aarch64}.whl`), unzipped, and every shared library outside
PyTorch's own read for its version and origin from the strings it carries; `libtorch_cpu.so`'s needed-library names were
read the same way. The wheels' `licenses/` folders name none of the libraries in this table, so each licence is the
upstream project's.

| Library | Wheel, platform | Read from the binary | Licence | What it obliges |
|---|---|---|---|---|
| `libgomp.so.1` (GNU OpenMP runtime) | torch, both | GCC 8.5.0, AlmaLinux 8 build (`libgomp.so.1.0.0-8.5.0-29.el8_10.alma.1`); `libtorch_cpu.so` needs it on both platforms | GPL-3.0-or-later WITH GCC-exception-3.1 | The GCC Runtime Library Exception lets PyTorch, and grapy above it, link the library under any licence; the library itself is GPLv3, so **the image redistributes a GPLv3 binary** and owes its licence text and an offer of its source, as it already does for the Debian packages in the same image. Nothing of Graticula's becomes GPL |
| `libgfortran.so.5` | torch, both | GCC 8.5.0, AlmaLinux 8 (`...-29.el8_10` on x86_64, `...-30.el8_10` on aarch64); OpenBLAS's Fortran runtime. On x86_64 it needs `libquadmath.so.0`, which the wheel does not carry, and nothing loads it there — no OpenBLAS is bundled on x86_64 | GPL-3.0-or-later WITH GCC-exception-3.1 | As `libgomp` |
| `libopenblas.so.0` | torch, aarch64 | `OpenBLAS 0.3.34 DYNAMIC_ARCH NO_AFFINITY USE_OPENMP` | BSD-3-Clause | Notice |
| `libarm_compute.so`, `libarm_compute_graph.so` (Arm Compute Library) | torch, aarch64 | `arm_compute_version=v53.2.0`, built for armv8a with NEON | MIT | Notice |
| **Intel oneMKL**, linked statically into `libtorch_cpu.so` | torch, x86_64 | `mkl_blas_*` and `MKL_Get_Version_String` symbols and `Intel oneMKL` messages in `libtorch_cpu.so`; the version is assembled at run time and not in the binary as text | **Intel Simplified Software License** — what Intel publishes `mkl-static` under on PyPI (2024.2.0 and 2026.2.0 read; classifier *Other/Proprietary License*) | **Not an open-source licence, and the one row here that is not.** It permits redistribution in binary form with its notice and forbids reverse engineering and modification; it does not touch Graticula's own licence. The image redistributes it inside the amd64 image only |
| `libpng16.so.16` | torchvision, both | libpng 1.6.56 (x86_64), 1.6.59 (aarch64) | libpng License v2 (PNG Reference Library License) | Notice |
| `libjpeg.so.8` (libjpeg-turbo) | torchvision, both | libjpeg-turbo 3.1.3 (x86_64), 3.2.1 (aarch64) | IJG License, BSD-3-Clause and Zlib | Notice; the IJG licence asks documentation to say the software is based in part on the Independent JPEG Group's work |
| `libwebp.so.7`, `libsharpyuv.so.0` | torchvision, both | libwebp's decoder (`webp_dec.c`) and SharpYUV | BSD-3-Clause (with Google's additional patent grant) | Notice |
| `libz.so.1` | torchvision, both | zlib 1.3.2 | Zlib | None for binaries |
| `ld-linux-aarch64.so.1` (the glibc dynamic loader) | torchvision, aarch64 | `Copyright (C) 2020 Free Software Foundation`; auditwheel's copy | LGPL-2.1-or-later | As `libgomp`: licence text and source offer for the binary; LGPL leaves what loads it unaffected |

**What this changes.** Nothing that blocks the image: no library here makes Graticula or grapy copyleft, and the
Community License is not in tension with any of them. Two things are now known that were not: the image carries GPLv3
and LGPL runtime binaries from these wheels (as it already carried them from Debian), and the amd64 image carries Intel
oneMKL under a proprietary redistribution licence. The amd64 image's notice set must therefore include the Intel
Simplified Software License alongside the GCC exception and the LGPL — which is a notice to ship, not a decision to
revisit.

**The image installs the newest of each when it is built**, so the version column says what was read, not what was
pinned. A wheel whose licence changes would change here only if somebody looked. Pinning is the open part of this row.

## Transitive dependencies — enumerated 2026-08-24

**[D-06](docs/architecture-debt.md)'s missing step, which was one command.** The §66
licensing gate verified the direct set on 2026-08-15 and left the graph beneath it
unexamined; the row named `dotnet list package --include-transitive` as the step nobody had
run. It has now been run over the whole solution, and every package's licence read from its
own `.nuspec` in the local NuGet cache — the same method the direct set was verified by.

**Thirty-three packages, direct and transitive together, and the answer is the one that
matters: no copyleft anywhere.**

| Licence | Packages |
|---|---|
| MIT | `DuckDB.NET.Data.Full` · `DuckDB.NET.Bindings.Full` (2026-09-13, ADR-066) · `MaxRev.Gdal.Core` and its four runtime packages · `SkiaSharp` and its four native-asset packages · `SkiaSharp.HarfBuzz`, `HarfBuzzSharp` and its native-asset packages (2026-09-29, ADR-100) · `Newtonsoft.Json` · `Konscious.Security.Cryptography.Argon2` · `Konscious.Security.Cryptography.Blake2` · `Microsoft.Extensions.DependencyInjection.Abstractions` · `Microsoft.Extensions.Logging.Abstractions` · `System.Memory` · `System.Reflection.Metadata` · `Microsoft.CodeCoverage` · `Microsoft.Extensions.TimeProvider.Testing` · `Microsoft.NET.Test.Sdk` · `Microsoft.TestPlatform.ObjectModel` · `Microsoft.TestPlatform.TestHost` · `Microsoft.AspNetCore.TestHost` (2026-09-13, ADR-068) · `Microsoft.IdentityModel.Protocols.OpenIdConnect` and `Microsoft.IdentityModel.JsonWebTokens` with what they bring — `Microsoft.IdentityModel.Protocols`, `.Tokens`, `.Logging`, `.Abstractions` and `System.IdentityModel.Tokens.Jwt`, all 8.23.0, nuspec `<license type="expression">MIT` (2026-09-24, ADR-088; they parse and validate, and open no connection of their own — `OidcClient` fetches) · `System.DirectoryServices.Protocols` 9.0.9, nuspec `<license type="expression">MIT` (2026-09-24, ADR-089; on Linux it loads OpenLDAP's `libldap` from the image — `libldap2`, OpenLDAP Public License, a permissive BSD-style licence, installed from the distribution and not linked into anything we ship) · what `ITfoxtec.Identity.Saml2` brings — `System.Security.Cryptography.Xml` and `System.Security.Cryptography.Pkcs` 9.0.21 (from 9.0.20 on 2026-10-14, for four .NET security advisories), `Microsoft.IdentityModel.Tokens.Saml` 8.22.0, `System.ServiceModel.Primitives` 8.1.2, `Microsoft.Extensions.Http` 9.0.8, nuspec `<license type="expression">MIT` (2026-09-24, ADR-090; `System.Security.Cryptography.Xml` is also referenced directly by the conformance suite, whose SAML provider signs with it) |
| BSD-3-Clause | `NetTopologySuite` · `ITfoxtec.Identity.Saml2` 4.21.0, nuspec `<license type="expression">BSD-3-Clause` (2026-09-24, ADR-090; it checks SAML signatures and opens no connection of its own — `SamlMetadata` fetches) |
| PostgreSQL Licence (BSD-style) | `Npgsql` |
| Apache-2.0 | `Apache.Arrow` · `Apache.Arrow.Scalars` (2026-09-13, with DuckDB.NET) · the `xunit` family — `xunit`, `xunit.assert`, `xunit.core`, `xunit.analyzers`, `xunit.abstractions`, `xunit.extensibility.core`, `xunit.extensibility.execution`, `xunit.runner.visualstudio` |
| BSD-style, stated by URL rather than SPDX | `BitMiracle.LibTiff.NET` |

**Nothing in the graph is GPL, LGPL or AGPL**, so the Apache-2.0 outbound licence is lawful
to grant over what is shipped, which is the question D-06 was opened on. PostGIS remains
GPL-2.0-or-later and remains a separate process that is not linked, which is recorded above
and unchanged.

**A method note, because it cost a wrong answer once.** The first pass read each package's
nuspec from `ls ~/.nuget/packages/<id>/*/ | head -1`, which is the **oldest cached version**
rather than the resolved one — and it reported the two `Microsoft.Extensions.*Abstractions`
packages under the old .NET Library EULA, from cached 1.1.0 folders that nothing in this
solution references. The resolved versions are 8.0.2 and both are MIT. The lesson is the one
this repository keeps relearning: *which* artefact was measured is part of the measurement.

**Half the list is test-only** — the xunit family, the TestPlatform packages,
`Microsoft.CodeCoverage`, `Microsoft.Extensions.TimeProvider.Testing` — and is not linked
into anything distributed. It is enumerated anyway, because a licence question answered only
for what ships has to be re-answered the first time somebody asks what is in the repository.

---

## GDAL — what this build carries, enumerated 2026-08-24

**[D-88](docs/architecture-debt.md) part 2, and the reason it exists is a sentence nobody
should write.** *"GDAL is MIT"* is true of GDAL's own code and false of the artefact: its
`LICENSE.TXT` is a bill of materials naming **fourteen** components, and which of them apply
is decided by which drivers were built in. So the drivers were asked rather than assumed —
`Graticula.Import.Reader` gained a `drivers` operation, because the native payload arrives
with `MaxRev.Gdal.Core` and changes when that package does; a list typed into this document
would be right until the next upgrade.

**Measured: GDAL 3.13.1, 83 vector drivers and 213 raster drivers.**

| Component in `LICENSE.TXT` | Licence | In this build? |
|---|---|---|
| GDAL/OGR general | MIT-style | yes, always |
| `port/cpl_float.cpp` (Industrial Light & Magic) | BSD-3-Clause | yes, core |
| `frmts/hdf4/hdf-eos/*` | BSD-style | **yes** — HDF4 is present |
| `frmts/pcraster/libcsf` (Utrecht University) | BSD-style | **yes** — PCIDSK is present |
| `frmts/grib/degrib/*` | public domain | **yes** — GRIB is present |
| `port/cpl_minizip*` | Info-ZIP | yes, core |
| `ogr/ogrsf_frmts/dxf/intronurbs.cpp` | BSD-style | **yes** — DXF is present |
| `alg/thinplatespline.cpp` and `alg/libqhull` | Qhull | yes, core algorithms |
| `frmts/pdf/pdfdataset.cpp` (PDFium) | BSD-3-Clause | **yes** — PDF is present |
| `frmts/mrf/*` — **Copyright 2014-2015 Esri** | **Apache-2.0** | **yes** — MRF is present, and `LICENSE.TXT` says this section applies *"when MRF driver included in build"* |
| `cmake/modules/3.*` | BSD-3-Clause | build-time only |
| `ogr/ogrsf_frmts/flatgeobuf` | BSD-2-Clause | **yes** — FlatGeobuf is present |
| `flatgeobuf/flatbuffers` | **Apache-2.0** | **yes**, with FlatGeobuf |

**ADR-157 (2026-10-03) puts four of these drivers to work for imagery**, and each carries a
library of its own beside GDAL's `LICENSE.TXT`, in the same native payload — all permissive:

| Driver used by ADR-157 | Library | Licence |
|---|---|---|
| JP2OpenJPEG | OpenJPEG | BSD-2-Clause |
| netCDF | netCDF-C (UCAR/Unidata) | BSD-3-Clause-style |
| HDF5, HDF5Image | HDF5 (The HDF Group) | BSD-3-Clause-style |
| HDF4, HDF4Image | HDF4 (The HDF Group) | BSD-style |
| HFA (ERDAS Imagine), AAIGrid | GDAL itself | MIT-style |

MrSID and ECW are **not** in this build — their SDKs' licences forbid it — and ADR-157 refuses
them by name.

**So the honest sentence is not *GDAL is MIT*.** It is: an MIT-style core, with BSD-2 and
BSD-3 components, a public-domain one, Info-ZIP, Qhull, and **two Apache-2.0 components, one
of them Esri's** — all of them permissive, none of them copyleft, and all compatible with
redistribution under ~~Apache-2.0~~ **the Elastic License 2.0 — corrected 2026-09-09.** This
paragraph was written on 2026-08-24, one day before
[ADR-047](docs/adr/ADR-047-the-outbound-licence-is-elastic-2.md) made the outbound licence
ELv2, and it named the licence this project no longer uses — in the file whose own header
says ELv2, five lines from the top. **The finding survives the change and that is the point
of having measured it**: permissive inbound components impose no copyleft obligation on any
outbound licence, so what was true for Apache-2.0 is true for ELv2. What was wrong is the
sentence, not the analysis. The conclusion is the same as the short sentence would have
given; the difference is that it is now checkable, and the Esri attribution is visible rather
than buried under a summary.

**Nothing here is linked into this server.** ADR-037's reversal put GDAL in a child process
(`Graticula.Import.Reader`), which is what [D-88](docs/architecture-debt.md) part 3 was about,
and `NativeDependencyTests` holds the confinement — one project per confined dependency, both
directions, and no path from the host through a project reference. The obligations are
attribution obligations on the artefact that ships it.

---

## ⚠ Verification status

Every entry below is marked `UNVERIFIED`. The licences are recorded from general
knowledge and **have not been checked against the actual source distributions**.

No licence claim in this file may be relied upon in an ADR until it is verified
against the upstream `LICENSE`/`COPYING` file of the exact version we intend to
use, and marked `VERIFIED` with a date. Licences change across versions, and
bundled components frequently differ from the parent project.

## Candidate dependencies

| Component | Role | Tier | Licence (claimed) | Status | Obligation notes |
|---|---|---|---|---|---|
| **Npgsql** | PostgreSQL driver | 2 | PostgreSQL Licence (BSD-style) | `UNVERIFIED` | **Actually referenced today**, by `Graticula.Platform.Postgres` and `Graticula.Providers.PostGis` only; an architecture test fails the build if it reaches Tier 1. Permissive and unproblematic under Apache-2.0 outbound. |
| **Konscious.Security.Cryptography.Argon2** | Password hashing (ADR-015 §5) | 2 | MIT | `UNVERIFIED` | **Actually referenced today**, behind `IPasswordHasher`. Managed, no native payload. **The row that deserves scrutiny disproportionate to its size**: it is a small community package holding the single most security-critical primitive in the server, and .NET ships no Argon2id of its own. The port exists so replacing it — libsodium via NSec, or a future BCL implementation — touches one file. |
| PostgreSQL | Baseline database | external | PostgreSQL Licence | `UNVERIFIED` | Separate process; not linked. |
| PostGIS | Spatial extension | external | GPL-2.0-or-later | `UNVERIFIED` | Separate process; not linked. Distribution posture differs from linking. |
| **OpenLayers 10.3.1** | The console's layer viewer (`wwwroot/ol.js`, `ol.css`) | **vendored code, 858 KB + 6 KB** | **BSD-2-Clause** | `VERIFIED 2026-08-16` — read from the published package metadata for the exact version fetched, not from the project's website; the minified build carries no header comment | **The only third-party code shipped inside this product.** Vendored rather than loaded from a CDN, which is the whole reason it was chosen over Esri's SDK (ADR-020 §4b): no runtime third-party request, so the console's policy needs no foreign origin, an air-gapped deployment works, and nobody outside learns the server exists. Pinned — a floating major version in a committed file is a silent upgrade nobody reviewed. Esri's SDK remains CDN-loaded on the compatibility-probe page only, and is not shipped. |
| **qrcode-generator 2.0.4** (Kazuhiko Arase) | The second factor's setup key drawn as a QR code, at the console's sign-in and on My profile (`wwwroot/qrcode.js`) | **vendored code, 57 KB, unminified** | **MIT** | `VERIFIED 2026-10-19` — read from the npm registry's metadata for the exact version, the tarball checked against its published sha512, and the licence in the file's own header | **Adopted by owner decision 2026-10-19** (*"QR için de kütüphane"*), after the `ux-designer` agent found that typing a 32-character key into a phone is the common enrolment path, since most enrolment happens at a desk. Vendored and loaded only when a key is shown, as OpenLayers is: `script-src 'self'`, no runtime third-party request, works air-gapped. Unminified so it can be read; it makes no network request and evaluates nothing. Not Tier 1 by any reading of the policy — it draws a picture of a string. The full MIT text is in `NOTICE`, which until this day did not carry OpenLayers' BSD-2 notice either; both are there now. |
| **Natural Earth 1:110m** | The console's fallback map ground (`wwwroot/ground-*.geojson`) — countries, lakes, rivers, cities, and a generated graticule | **vendored data, 87 KB** | **Public domain** | `VERIFIED 2026-08-16` — Natural Earth states all its raster and vector data is in the public domain, no permission needed, attribution appreciated rather than required | **Shipped data rather than code, and now the fallback rather than the ground.** Vendored because a map with no ground cannot tell *being in the wrong place* from *having no data*, and public domain is what makes shipping possible where a tile service is not. **It is drawn only when no rendered basemap and no imported ground are in use** (ADR-020 §4c) — on a first run, in other words. Properties stripped, coordinates rounded; attribution is carried in each layer's `copyright` and shown on the map. **196 KB total.** |
| **OpenStreetMap data** | Imported by the operator into the datastore and served as our own vector tiles (ADR-020 §4d) | **not shipped** | ODbL 1.0 | `N/A — we distribute none of it` | Recorded here to be explicit that it is *not* a dependency of this product. The operator downloads and imports; this server cuts tiles from what is in their datastore. So the ODbL obligations — attribution and share-alike on a derived database — belong to them, and nothing changes for our own outbound licence. Distinct from OpenStreetMap's **tile service**, which has a usage policy and is a separate decision in ADR-020 §4c. |
| SQL Server driver | Data provider | 2 | `Microsoft.Data.SqlClient` is MIT | `UNVERIFIED` | The straightforward one. No longer needed for the platform store (Q-51). |
| **MySQL driver** | Data provider ([ADR-208](docs/adr/ADR-208-a-registered-mysql-database-is-served-like-postgis.md)) | 2 | **MIT** for `MySqlConnector` 2.4.0, read from the package's own `mysqlconnector.nuspec` (`<license type="expression">MIT</license>`), 2026-10-20, and again for **2.6.1** on 2026-10-29, the upgrade [GHSA-473q-m89c-ghf8](https://github.com/advisories/GHSA-473q-m89c-ghf8) asked for (same expression, copyright now *2016–2026*, same four Microsoft dependencies); its dependencies are Microsoft's `Microsoft.Extensions.*` and `System.*` packages (MIT) | **VERIFIED 2026-10-20 — may be shipped** | **Chosen deliberately, as this row asked**: Oracle's official `MySql.Data` is GPLv2 with a FOSS exception and is not used. MIT asks for the copyright notice and the licence text with every copy; the package carries the licence only as an expression, so `deploy/LICENSE-MySqlConnector.txt` holds the text with the copyright its nuspec states (*Copyright 2016–2026 Bradley Grainger*), and `deploy/server.Dockerfile` copies it beside the driver and fails the build without either. MariaDB is not served (ADR-208 §5). |
| **SQL Server driver** | Data provider ([ADR-210](docs/adr/ADR-210-a-registered-sql-server-database-is-served-to-read.md)) | 2 | **MIT** for `Microsoft.Data.SqlClient` 7.1.1, read from the package's own `microsoft.data.sqlclient.nuspec` (`<license type="expression">MIT</license>`, *© Microsoft Corporation*), 2026-10-21; it brings `Microsoft.Data.SqlClient.SNI.runtime` (Windows only), `Microsoft.IdentityModel.*` and `Microsoft.Bcl.Cryptography`, all MIT | **VERIFIED 2026-10-21 — may be shipped** | The package carries the licence only as an expression, so `deploy/LICENSE-Microsoft.Data.SqlClient.txt` holds the text with the copyright its nuspec states, and `deploy/server.Dockerfile` copies it beside the driver and fails the build without either, as for MySqlConnector. |
| **DuckDB** | File-format query engine — GeoParquet layers ([ADR-066](docs/adr/ADR-066-geoparquet-layers-read-by-duckdb.md)) | 2 | MIT | `VERIFIED 2026-09-13` for the binding and the engine's own licence; `UNVERIFIED` for the components compiled into `libduckdb` | **Actually referenced since 2026-09-13**, by `Graticula.Providers.DuckDb` and by nothing else — `NativeDependencyTests` fails the build otherwise. `DuckDB.NET.Data.Full` and `DuckDB.NET.Bindings.Full` 1.5.5 carry `<license type="expression">MIT</license>` in their nuspecs, and the package ships two texts, `LICENSE.md` (DuckDB.NET, Giorgi Dalakishvili) and `LICENSE-DuckDB.txt` (Stichting DuckDB Foundation), both MIT — **the image copies both beside the library**, because MIT's obligation is the notice travelling with every copy. `Apache.Arrow` and `Apache.Arrow.Scalars` 23.0.0 arrive with it, `Apache-2.0` in their nuspecs. **What is not verified** is the set of third-party libraries DuckDB compiles in — ICU, Thrift, zstd, snappy and others — which, like GDAL's, is a bill of materials rather than one licence; DuckDB's upstream notice is where it is read, and nobody has read it against this version. **The spatial extension, which bundles GDAL and GEOS, is never installed or loaded** — measured, and refused by the locked configuration — so Q-87's concern does not reach this process. **Two extensions since [ADR-067](docs/adr/ADR-067-duckdb-sources-beyond-a-local-folder.md), and they are not alike.** `httpfs` is part of DuckDB itself, MIT under the same notice; the image fetches it at build for the restored version and both Linux architectures, and it is loaded by path with DuckDB's signature check on. `motherduck` is **proprietary** — MotherDuck's terms grant a customer the right to *download and install* it, non-transferably, and say nothing about redistribution — so **the image does not carry it**; ADR-067 §5.4 has an operator's server download it when MotherDuck is enabled — built 2026-09-13: `Graticula:MotherDuck=true` downloads it from `extensions.duckdb.org` into `<StatePath>/duckdb-extensions` on first use — and that reading of the terms is `INFERRED`, not legal advice. |
| Oracle client / driver | Data provider ([ADR-205](docs/adr/ADR-205-a-registered-oracle-database-is-served-like-postgis.md)) | 2 | **Oracle Free Distribution, Hosting, and Use Terms and Conditions** (v1.0, 2022-06-28) for `Oracle.ManagedDataAccess.Core` — the managed driver, no native client — read from the package's own `LICENSE.txt`, 23.26.301, 2026-10-19; its bundled third-party notices (`info.txt`: MIT Kerberos, OpenVision, FundsXpress and others) are MIT/BSD-style, attribution only; its dependencies are Microsoft's `System.*` packages (MIT) | **VERIFIED 2026-10-19 — may be shipped in the server image** | **Rewritten 2026-10-19 from what was read; the row before was written against an Apache-2.0 outbound licence two licences ago.** The terms grant use, hosting for third parties and **redistribution of the unmodified driver**, on five conditions: a copy of the licence goes with every distribution; no fee is charged for the driver itself (a product that adds substantial value may be sold — ours is not, which is simpler still); Oracle's notices stay; export-control law is the user's to follow; no reverse engineering. **What we owe:** the image carries `LICENSE.txt` and `info.txt` beside the driver (NuGet does not copy them into a publish) — `deploy/server.Dockerfile` copies them as `LICENSE-Oracle.ManagedDataAccess.txt` and `NOTICE-Oracle.ManagedDataAccess.txt` since 2026-10-20 and fails the build without them — and this file names the export-control duty — ADR-205 condition 1's remaining half. The Graticula Community License governs Graticula and not this driver, and nothing in either asks the other to change. |
| SQLite | Embedded platform store | 2 | public domain | `UNVERIFIED` | |
| GEOS | Geometry topology | 2 | LGPL-2.1 | `UNVERIFIED` | LGPL: dynamic linking and replaceability obligations. Our port layer already satisfies the replaceability intent. |
| PROJ | Coordinate transformation | 2 | MIT-style | `UNVERIFIED` | Grid data files carry **separate** licences and must be checked individually. |
| GDAL | Raster and vector I/O | 2 | MIT-style (core) | `UNVERIFIED` | **Drivers vary.** Individual drivers and their upstream libraries carry their own licences, some copyleft, some patent-encumbered. A GDAL build is not one licence — it is a bill of materials. **Confirmed from the source 2026-08-18**, and the warning above is the project's own words matching upstream's: `LICENSE.TXT` opens by saying it *"attempts to include all licenses that apply within the GDAL/OGR source tree"* and then lists BSD, public-domain, **Apache-2.0 (Esri components)**, ISC, Info-ZIP and Qhull terms beside the MIT-style core. The Esri-contributed parts under Apache-2.0 are unproblematic for us — permissive, and §7 accepts copyleft anyway — but they are the reason *"GDAL is MIT"* must not be written as a finding. **`OpenFileGDB` specifically is in-tree and reverse-engineered**, and its documentation states it *"does not depend on a third-party library"* — so the File Geodatabase route needs no Esri SDK and carries no separate licence to chase. That is the driver [Q-108](docs/open-questions.md) is about; the SDK-backed `FileGDB` driver is the one to avoid. |
| JTS | Geometry (JVM) | 2 | EPL/EDL dual | `UNVERIFIED` | Only if ADR-001 selects the JVM. |
| NetTopologySuite | Geometry (.NET) | 2 | BSD-3-Clause | `UNVERIFIED` | **Actually referenced since 2026-08-15**, by `Graticula.Overlay.Worker` and by nothing else. Not merely a tier boundary: the worker is a separate *process*, so the library never loads in the server (Q-97, [ADR-022](docs/adr/ADR-022-geometry-server.md) §9). BSD-3-Clause is permissive and unproblematic under Apache-2.0 outbound; the row stays `UNVERIFIED` because nobody has read the shipped nuspec, and D-06's trigger is the first binary that bundles it. |
| `geo` / `geo-types` | Geometry (Rust) | 2 | MIT/Apache-2.0 | `UNVERIFIED` | Only if ADR-001 selects Rust. |
| Skia | Rasterisation | 2 | BSD-3-Clause | `UNVERIFIED` | Large native build; distribution size and toolchain cost matter as much as licence. |
| Cairo | Rasterisation | 2 | LGPL-2.1 / MPL dual | `UNVERIFIED` | |
| HarfBuzz | Text shaping | 2 | MIT-style | `UNVERIFIED` | Usually pulled in by the rasteriser. |
| FreeType | Font rasterisation | 2 | FTL / GPL-2.0 dual | `UNVERIFIED` | |
| MapLibre Native | Rendering | 2 | BSD-2-Clause | `UNVERIFIED` | Licensing is mixed across components; check per module. |

## Rejected on architectural grounds, not licence

Recorded so the reason is not later misremembered as a licensing problem.

| Component | Reason |
|---|---|
| MapServer | Tier 3 — finished server product. Adopting it means adopting its architecture ([build-vs-adopt-policy.md](docs/build-vs-adopt-policy.md)). |
| GeoServer | Tier 3, as above. |
| QGIS Server | Tier 3, as above. |

## Obligations to state explicitly

- **AGPL.** If any AGPL component is ever linked into the server, the network-use
  clause applies: users interacting with the running server over a network gain
  source rights. For a server product this is a materially different obligation
  from GPL. Nothing currently proposed is AGPL, but the project's own licence
  choice is still open and this is the clause that matters most for it.
- **LGPL.** Requires that the user be able to replace the LGPL component. Our
  Tier 2 port layer means we satisfy this by construction rather than by
  paperwork.
- **GDAL driver bill of materials.** Must be enumerated per build before any
  distribution. This is the single most likely place for an unpleasant surprise.
- **Data files, not just code.** PROJ transformation grids, EPSG database, font
  files and any bundled sample data each carry their own terms.
- **EPSG and Esri data carried in the product.** `AxisOrderRegister.cs` (ADR-060) and
  `TransformationRegister.cs` (ADR-160) are generated from PROJ's `proj.db` and carry
  facts from the EPSG Geodetic Parameter Dataset (IOGP) — codes, names, accuracies, areas
  of use — and, for ESRI codes, from Esri's projection engine data as PROJ packages it
  (`ESRI.VERSION` ArcMap 10.8.1 in the register used). The EPSG dataset's terms of use
  ask that the source be acknowledged and that data presented as EPSG's not be altered;
  the registers carry EPSG names and values unchanged and name their source in their
  header. **To verify** before wide distribution, with the Esri data's terms as PROJ
  states them.

## Process

1. Before an ADR adopts a dependency, its row here must be `VERIFIED`.
2. Before any distribution, the full transitive tree is enumerated and checked —
   not just the direct dependencies listed above.
3. Re-verified at every major version upgrade.

**This file is not legal advice.** Before commercial or wide public
distribution, the obligations here need review by someone qualified to give it.
