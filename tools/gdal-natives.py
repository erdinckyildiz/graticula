"""GDAL for the import reader, without Poppler: a pinned set of conda-forge packages — ADR-254.

Owner decision 2026-11-01 on D-380: *"PDF'siz GDAL'a geç"* — move to GDAL builds without the
PDF driver and Poppler, so no copyleft library ships. MaxRev's packages link Poppler into
`libgdal` and into every SWIG wrapper, so leaving the file out stops GDAL loading at all; the
replacement is conda-forge's `libgdal-core` — GDAL with the PDF driver split into a plugin this
lock never names — and conda-forge's `gdal-csharp`, whose wrappers need nothing but `libgdal`.

Two commands:

    python tools/gdal-natives.py lock [--repodata DIR]
        Resolves the closure for linux-64, linux-aarch64 and win-64 from conda-forge's
        current_repodata.json and writes deploy/gdal/gdal-natives.lock.json: every package by
        name, version, build, URL, SHA-256 and licence. Run by a person, when GDAL moves; the
        lock is committed and is the bill of materials.

    python tools/gdal-natives.py fetch RID OUT
        RID is linux-x64, linux-arm64 or win-x64. Downloads the lock's packages for it (cached in
        $GRATICULA_GDAL_CACHE or ~/.cache/graticula-gdal), checks every SHA-256, and writes OUT:
          lib/ (Linux) or bin/ (Windows) — the four wrappers and exactly the shared libraries they
              need, transitively, each under the name the loader asks for; nothing else;
          lib/gdalplugins/ or bin/gdalplugins/ — the plugins the lock names, and their closure;
          share/gdal, share/proj — GDAL's and PROJ's data;
          csharp/ — the binding's C# sources (GDAL's SWIG output, from the win-64 package, which
              is the one that carries them), checked entry point by entry point against the
              wrappers of RID;
          licenses/ — every shipped package's licence files and PACKAGES.txt, because a notice
              has to travel with every copy and an image is a copy.
        It refuses — and the build fails — if a package's licence is GPL without a linking
        exception, if any file is Poppler, PDFium, PoDoFo or librttopo, or if a binary needs a
        newer libstdc++ than the server image's Ubuntu 24.04 carries.

Standard library only, so the Docker stage that runs it needs nothing installed. A .conda file is
a zip of zstd tarballs: Python 3.14's `compression.zstd` reads them, and before 3.14 the `zstd`
program or bsdtar (Windows' own `tar.exe`) does.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
LOCK = REPO / "deploy" / "gdal" / "gdal-natives.lock.json"
CHANNEL = "https://conda.anaconda.org/conda-forge"

# What the reader needs. The core carries GeoTIFF/COG, GeoPackage, OpenFileGDB, shapefile, KML and
# LIBKML, CSV, XLSX, GeoJSON, FlatGeobuf, ERDAS Imagine and the ASCII grids; the four plugins are
# ADR-157's JPEG 2000, NetCDF and HDF. Never libgdal-pdf, and never the `libgdal` metapackage that
# pulls it in.
ROOTS = [
    "libgdal-core 3.13.3",
    "gdal-csharp 2.1.6",
    "libgdal-netcdf 3.13.3",
    "libgdal-hdf4 3.13.3",
    "libgdal-hdf5 3.13.3",
    "libgdal-jp2openjpeg 3.13.3",
]

# Dependencies that are declared and not wanted. `libgdal` is gdal-csharp's (it would bring every
# plugin, PDF's among them); `dotnet` is gdal-csharp's too, and the server image has its own; `ucrt`
# is part of Windows; `sqlite` is the sqlite3 command-line program, which proj and libspatialite
# declare for their own tools, and which brings readline (GPL-3.0) — the library is `libsqlite`.
# Their own dependencies are not followed.
SKIP = {"libgdal", "dotnet", "ucrt", "libgdal-pdf", "poppler", "poppler-data", "python", "sqlite"}

SUBDIRS = {"linux-x64": "linux-64", "linux-arm64": "linux-aarch64", "win-x64": "win-64"}

# The libraries the system provides and that are therefore never copied. libstdc++ and libgcc_s
# are the server image's own because the .NET runtime has already loaded them by the time GDAL is,
# so a second copy would never be used; the ceiling below is what makes that safe.
SYSTEM = re.compile(
    r"^(libc|libm|libdl|libpthread|librt|libutil|libresolv|libstdc\+\+|libgcc_s|ld-linux[-\w.]*)\.so"
    r"|^(kernel32|user32|advapi32|ws2_32|shell32|ole32|oleaut32|crypt32|bcrypt|secur32|normaliz|wldap32"
    r"|shlwapi|version|psapi|dbghelp|ntdll|iphlpapi|rpcrt4|gdi32|comdlg32|winmm|userenv|mswsock|ncrypt"
    r"|setupapi|cfgmgr32|dnsapi|winhttp|wininet|msvcrt|ucrtbase|odbc32)\.dll$"
    r"|^api-ms-win-.*\.dll$",
    re.IGNORECASE,
)

# Ubuntu 24.04's libstdc++ (GCC 14) and glibc. The server and CI both run on it.
CEILING = {"GLIBCXX": (3, 4, 33), "GLIBC": (2, 39)}

FORBIDDEN_FILES = re.compile(r"poppler|pdfium|podofo|rttopo", re.IGNORECASE)


# --------------------------------------------------------------------------------------------
# conda's version order and match specs — the subset conda-forge's dependency strings use.

def _segments(version: str):
    out = []
    for part in re.split(r"[._-]", version.lower().replace("+", ".")):
        for token in re.findall(r"\d+|[a-z]+", part) or ["0"]:
            if token.isdigit():
                out.append((1, int(token), ""))
            elif token == "dev":
                out.append((-1, 0, token))
            elif token == "post":
                out.append((2, 0, token))
            else:
                out.append((0, 0, token))
    return out


def vcmp(a: str, b: str) -> int:
    x, y = _segments(a), _segments(b)
    n = max(len(x), len(y))
    x += [(1, 0, "")] * (n - len(x))
    y += [(1, 0, "")] * (n - len(y))
    return (x > y) - (x < y)


def _one(constraint: str, version: str) -> bool:
    constraint = constraint.strip()
    if constraint in ("", "*"):
        return True
    m = re.match(r"^(>=|<=|==|!=|>|<|~=)?(.*)$", constraint)
    op, ref = m.group(1), m.group(2)
    if ref.endswith(".*") or (op is None and ref.endswith("*")):
        prefix = ref.rstrip("*").rstrip(".")
        same = version == prefix or version.startswith(prefix + ".")
        return (not same) if op == "!=" else same
    c = vcmp(version, ref)
    return {">=": c >= 0, "<=": c <= 0, ">": c > 0, "<": c < 0, "!=": c != 0}.get(op, c == 0)


def version_matches(spec: str, version: str) -> bool:
    return any(all(_one(part, version) for part in alt.split(",")) for alt in spec.split("|"))


def parse_spec(spec: str):
    parts = spec.split()
    name = parts[0]
    version = parts[1] if len(parts) > 1 else "*"
    build = parts[2] if len(parts) > 2 else "*"
    return name, version, build


def spec_matches(spec: str, record: dict) -> bool:
    name, version, build = parse_spec(spec)
    if record["name"] != name or not version_matches(version, record["version"]):
        return False
    pattern = "^" + re.escape(build).replace(r"\*", ".*") + "$"
    return re.match(pattern, record["build"]) is not None


# --------------------------------------------------------------------------------------------
# lock

def load_repodata(directory: Path, subdir: str) -> dict:
    directory.mkdir(parents=True, exist_ok=True)
    records: dict[str, list[dict]] = {}
    for sd in (subdir, "noarch"):
        path = directory / f"{sd}.json"
        if not path.exists():
            print(f"fetching {sd} repodata", file=sys.stderr)
            urllib.request.urlretrieve(f"{CHANNEL}/{sd}/current_repodata.json", path)
        data = json.loads(path.read_text(encoding="utf-8"))
        for key in ("packages", "packages.conda"):
            for filename, record in data.get(key, {}).items():
                record = dict(record, filename=filename, subdir=sd)
                records.setdefault(record["name"], []).append(record)
    return records


def licence_allowed(licence: str) -> bool:
    """GPL is refused unless it is the LGPL or carries GCC's runtime-library exception."""
    text = licence or ""
    if "exception" in text.lower():
        return True
    return re.search(r"(?<![L])GPL", text) is None


def resolve(records: dict, roots: list[str]) -> dict[str, dict]:
    """Depth-first with backtracking: a package's builds are tried newest first, and a choice that
    later meets a constraint it cannot satisfy is undone. Small closures only, which this is."""
    sys.setrecursionlimit(20000)
    steps = [0]

    def order(r):
        # Newest first; between two builds of one version, the one that is not GPL — conda-forge
        # builds libspatialite and libarchive both ways, and only the lgpl_ one leaves librttopo out.
        return (licence_allowed(r.get("license", "")), not r["build"].startswith("gpl_"),
                _segments(r["version"]), r.get("build_number", 0), r.get("timestamp", 0))

    def consistent(pick, chosen, specs, rest) -> bool:
        for dep in pick.get("depends", []):
            n = parse_spec(dep)[0]
            if n in SKIP or n.startswith("__"):
                continue
            if n in chosen:
                if not spec_matches(dep, chosen[n]):
                    return False
                continue
            together = specs.get(n, []) + [s for s in rest if parse_spec(s)[0] == n] + [dep]
            if not any(all(spec_matches(s, r) for s in together) for r in records.get(n, [])):
                return False
        return True

    def solve(chosen: dict, specs: dict, queue: tuple):
        steps[0] += 1
        if steps[0] > 200000:
            raise SystemExit("the resolver did not settle")
        i = 0
        while i < len(queue):
            spec = queue[i]
            name = parse_spec(spec)[0]
            if name in SKIP or name.startswith("__"):
                i += 1
                continue
            if name in chosen:
                if not spec_matches(spec, chosen[name]):
                    return None
                i += 1
                continue
            wanted = specs.get(name, []) + [spec]
            rest = queue[i + 1:]
            # Every later spec for this name constrains the choice too.
            wanted += [s for s in rest if parse_spec(s)[0] == name]
            candidates = sorted((r for r in records.get(name, []) if all(spec_matches(s, r) for s in wanted)),
                                key=order, reverse=True)
            for pick in candidates:
                # Forward check: a build whose dependencies contradict what is already chosen, or leave
                # nothing that satisfies a name's constraints so far, is not tried. Without this the
                # search meets hdf5's two variants dozens of choices later and backtracks through all.
                if not consistent(pick, chosen, specs, rest):
                    continue
                found = solve({**chosen, name: pick}, {**specs, name: wanted},
                              rest + tuple(pick.get("depends", [])))
                if found is not None:
                    return found
            return None
        return chosen

    result = solve({}, {}, tuple(roots))
    if result is None:
        raise SystemExit(f"no consistent set satisfies {roots}")
    return result


def lock(repodata_dir: Path) -> None:
    out = {"generated_by": "tools/gdal-natives.py lock", "roots": ROOTS, "skip": sorted(SKIP), "platforms": {}}
    for rid, subdir in SUBDIRS.items():
        records = load_repodata(repodata_dir, subdir)
        chosen = resolve(records, ROOTS)
        refused = [f"{r['name']} {r['version']} ({r.get('license')})" for r in chosen.values()
                   if not licence_allowed(r.get("license", ""))]
        if refused:
            raise SystemExit(f"{rid}: copyleft package(s) in the closure: {', '.join(refused)}")
        out["platforms"][rid] = [
            {
                "name": r["name"],
                "version": r["version"],
                "build": r["build"],
                "license": r.get("license", ""),
                "url": f"{CHANNEL}/{r['subdir']}/{r['filename']}",
                "sha256": r["sha256"],
            }
            for r in sorted(chosen.values(), key=lambda r: r["name"])
        ]
        print(f"{rid}: {len(chosen)} packages", file=sys.stderr)
    # The C# sources travel only in the win-64 build of gdal-csharp; every platform compiles them.
    out["csharp_sources"] = next(p for p in out["platforms"]["win-x64"] if p["name"] == "gdal-csharp")
    LOCK.parent.mkdir(parents=True, exist_ok=True)
    LOCK.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {LOCK.relative_to(REPO)}", file=sys.stderr)


# --------------------------------------------------------------------------------------------
# fetch

def cache_dir() -> Path:
    given = os.environ.get("GRATICULA_GDAL_CACHE")
    return Path(given) if given else Path.home() / ".cache" / "graticula-gdal"


def download(entry: dict) -> Path:
    target = cache_dir() / entry["url"].rsplit("/", 2)[-2] / entry["url"].rsplit("/", 1)[-1]
    if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == entry["sha256"]:
        return target
    target.parent.mkdir(parents=True, exist_ok=True)
    last = None
    for _ in range(3):
        try:
            with urllib.request.urlopen(entry["url"], timeout=120) as response:
                data = response.read()
            break
        except OSError as failed:  # a CDN hiccup is retried; a wrong hash is not
            last = failed
    else:
        raise SystemExit(f"could not download {entry['url']}: {last}")
    digest = hashlib.sha256(data).hexdigest()
    if digest != entry["sha256"]:
        raise SystemExit(f"{entry['url']}: SHA-256 {digest}, the lock says {entry['sha256']}")
    tmp = target.with_suffix(".part")
    tmp.write_bytes(data)
    tmp.replace(target)
    return target


def unzstd(data: bytes) -> bytes:
    try:
        from compression import zstd  # Python 3.14+
        return zstd.decompress(data)
    except ImportError:
        pass
    if shutil.which("zstd"):
        return subprocess.run(["zstd", "-dc"], input=data, capture_output=True, check=True).stdout
    tar = shutil.which("bsdtar") or (r"C:\Windows\System32\tar.exe" if os.name == "nt" else None)
    if tar:
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "in.tar.zst"
            src.write_bytes(data)
            return subprocess.run([tar, "-cf", "-", f"@{src}"], capture_output=True, check=True).stdout
    raise SystemExit("cannot read zstd: needs Python 3.14, the zstd program, or bsdtar")


def members(conda: Path) -> dict[str, tuple[str, bytes | str]]:
    """Every member of a .conda as path -> ('file', bytes) or ('link', target)."""
    out: dict[str, tuple[str, bytes | str]] = {}
    with zipfile.ZipFile(conda) as z:
        for name in z.namelist():
            if not name.endswith(".tar.zst"):
                continue
            with tarfile.open(fileobj=io.BytesIO(unzstd(z.read(name)))) as t:
                for m in t.getmembers():
                    if m.issym():
                        out[m.name] = ("link", m.linkname)
                    elif m.isfile():
                        out[m.name] = ("file", t.extractfile(m).read())
    return out


def elf(data: bytes):
    """(needed, verneed, dynamic exports) of an ELF shared object, or None."""
    if data[:4] != b"\x7fELF":
        return None
    is64 = data[4] == 2
    e = "<" if data[5] == 1 else ">"
    shoff = struct.unpack_from(e + ("Q" if is64 else "I"), data, 0x28 if is64 else 0x20)[0]
    shentsize, shnum = struct.unpack_from(e + "HH", data, 0x3A if is64 else 0x2E)
    secs = []
    for i in range(shnum):
        fmt = e + ("IIQQQQIIQQ" if is64 else "IIIIIIIIII")
        _, typ, _, _, off, size, link, _, _, entsize = struct.unpack_from(fmt, data, shoff + i * shentsize)
        secs.append((typ, off, size, link, entsize))

    def string(table, at):
        start = secs[table][1] + at
        return data[start:data.index(b"\0", start)].decode()

    needed, verneed, exports = [], set(), set()
    for typ, off, size, link, entsize in secs:
        if typ == 6:  # DYNAMIC
            step = 16 if is64 else 8
            for p in range(off, off + size, step):
                tag, val = struct.unpack_from(e + ("qQ" if is64 else "iI"), data, p)
                if tag == 0:
                    break
                if tag == 1:
                    needed.append(string(link, val))
        elif typ == 0x6FFFFFFE:  # GNU verneed
            p = off
            while True:
                _, cnt, _, aux, nxt = struct.unpack_from(e + "HHIII", data, p)
                a = p + aux
                for _ in range(cnt):
                    _, _, _, name, anext = struct.unpack_from(e + "IHHII", data, a)
                    verneed.add(string(link, name))
                    if anext == 0:
                        break
                    a += anext
                if nxt == 0:
                    break
                p += nxt
        elif typ == 11:  # DYNSYM
            step = entsize or (24 if is64 else 16)
            for p in range(off, off + size, step):
                if is64:
                    name, info, _, shndx, _, _ = struct.unpack_from(e + "IBBHQQ", data, p)
                else:
                    name, _, _, info, _, shndx = struct.unpack_from(e + "IIIBBH", data, p)
                if name and shndx != 0:
                    exports.add(string(link, name))
    return needed, verneed, exports


def pe(data: bytes):
    """(imports, exports) of a PE DLL, or None."""
    if data[:2] != b"MZ":
        return None
    p = struct.unpack_from("<I", data, 0x3C)[0]
    if data[p:p + 4] != b"PE\0\0":
        return None
    nsec, = struct.unpack_from("<H", data, p + 6)
    optsize, = struct.unpack_from("<H", data, p + 20)
    opt = p + 24
    dd = opt + (112 if struct.unpack_from("<H", data, opt)[0] == 0x20B else 96)
    exp_rva = struct.unpack_from("<I", data, dd)[0]
    imp_rva = struct.unpack_from("<I", data, dd + 8)[0]
    secs = [struct.unpack_from("<IIII", data, opt + optsize + i * 40 + 8) for i in range(nsec)]

    def off(rva):
        for vsize, va, rsize, raw in secs:
            if va <= rva < va + max(vsize, rsize):
                return rva - va + raw
        return None

    def cstr(rva):
        o = off(rva)
        return data[o:data.index(b"\0", o)].decode()

    imports, exports = [], set()
    q = off(imp_rva) if imp_rva else None
    while q is not None:
        name = struct.unpack_from("<I", data, q + 12)[0]
        if name == 0:
            break
        imports.append(cstr(name))
        q += 20
    if exp_rva:
        q = off(exp_rva)
        count, = struct.unpack_from("<I", data, q + 24)
        names, = struct.unpack_from("<I", data, q + 32)
        n = off(names)
        for i in range(count):
            exports.add(cstr(struct.unpack_from("<I", data, n + 4 * i)[0]))
    return imports, exports


def version_tuple(tag: str, prefix: str):
    return tuple(int(x) for x in tag[len(prefix) + 1:].split("."))


def fetch(rid: str, out: Path) -> None:
    if rid not in SUBDIRS:
        raise SystemExit(f"RID is one of {', '.join(SUBDIRS)}")
    locked = json.loads(LOCK.read_text(encoding="utf-8"))
    entries = locked["platforms"][rid]
    stamp_text = hashlib.sha256(LOCK.read_bytes() + Path(__file__).read_bytes()).hexdigest()
    stamp = out / ".complete"
    if stamp.exists() and stamp.read_text().strip() == stamp_text:
        return

    windows = rid.startswith("win")
    libdir = "bin" if windows else "lib"
    if out.exists():
        shutil.rmtree(out)

    files: dict[str, bytes] = {}     # basename -> bytes, from the platform's library directory
    links: dict[str, str] = {}       # basename -> basename
    plugins: dict[str, bytes] = {}
    data: dict[str, bytes] = {}      # share/... -> bytes
    owner: dict[str, str] = {}       # basename -> package
    licences: dict[str, dict[str, bytes]] = {}

    for entry in entries:
        for path, (kind, payload) in members(download(entry)).items():
            if path.startswith("info/licenses/") and kind == "file":
                licences.setdefault(entry["name"], {})[path[len("info/licenses/"):]] = payload
                continue
            rel = path[len("Library/"):] if windows and path.startswith("Library/") else path
            if windows and path.startswith("Library/") is False and not path.startswith("info/"):
                rel = path  # a few win-64 packages put files at the root; they are not libraries
            parts = rel.split("/")
            if parts[0] == libdir and len(parts) == 2:
                if kind == "link":
                    links[parts[1]] = payload.rsplit("/", 1)[-1]
                else:
                    files[parts[1]] = payload
                    owner[parts[1]] = entry["name"]
            elif rel.startswith(("lib/gdalplugins/", "bin/gdalplugins/")) and kind == "file" and len(parts) == 3:
                plugins[parts[2]] = payload
                owner[parts[2]] = entry["name"]
            elif rel.startswith(("share/gdal/", "share/proj/")) and kind == "file":
                data[rel] = payload

    def real(name: str) -> bytes | None:
        asked, seen = name, 0
        while name in links and seen < 10:
            name, seen = links[name], seen + 1
        if name in owner:
            owner[asked] = owner[name]  # the package that shipped the file the link names
        return files.get(name)

    def deps(blob: bytes):
        if windows:
            r = pe(blob)
            return (r[0], set()) if r else ([], set())
        r = elf(blob)
        return (r[0], r[1]) if r else ([], set())

    wrappers = (["gdal_wrap.dll", "ogr_wrap.dll", "osr_wrap.dll", "gdalconst_wrap.dll"] if windows else
                ["libgdal_wrap.so", "libogr_wrap.so", "libosr_wrap.so", "libgdalconst_wrap.so"])
    shipped: dict[str, bytes] = {}
    versions: set[str] = set()
    missing: set[str] = set()
    pending = [(w, real(w)) for w in wrappers] + [(f"gdalplugins/{p}", b) for p, b in plugins.items()
                                                 if p.lower().endswith((".so", ".dll"))]
    if windows:
        # The C++ runtime the conda builds were linked against travels with them, so a machine
        # with an older Visual C++ redistributable still loads them.
        pending += [(n, files[n]) for n in files if n.lower() in ("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll")]
    while pending:
        name, blob = pending.pop()
        if name in shipped:
            continue
        if blob is None:
            missing.add(name)
            continue
        shipped[name] = blob
        needed, verneed = deps(blob)
        versions |= verneed
        for n in needed:
            if SYSTEM.match(n):
                continue
            lookup = n if not windows else next((k for k in files if k.lower() == n.lower()), n)
            if lookup not in shipped:
                pending.append((lookup, real(lookup)))
    if missing:
        raise SystemExit(f"{rid}: needed and not in the lock: {', '.join(sorted(missing))}")

    bad = [n for n in shipped if FORBIDDEN_FILES.search(n)]
    if bad:
        raise SystemExit(f"{rid}: refused, copyleft library in the closure: {', '.join(bad)}")
    for tag, ceiling in CEILING.items():
        need = max((version_tuple(v, tag) for v in versions if re.fullmatch(tag + r"_[\d.]+", v)), default=None)
        if need and need > ceiling:
            raise SystemExit(f"{rid}: needs {tag} {need}, above Ubuntu 24.04's {ceiling}")

    # The binding's sources, and every entry point they call present in this platform's wrappers.
    csharp: dict[str, bytes] = {}
    src = locked["csharp_sources"]
    for path, (kind, payload) in members(download(src)).items():
        if kind == "file" and path.endswith("csharp-source.zip"):
            with zipfile.ZipFile(io.BytesIO(payload)) as z:
                for stored in z.namelist():
                    # The zip was written on Windows with backslashes; zipfile turns them into "/" only where
                    # os.sep is "\\", so a Linux build saw no file at all (CI run 41286846464).
                    n = stored.replace("\\", "/")
                    top = n.split("/")[0]
                    if top in ("gdal", "ogr", "osr", "const") and n.endswith(".cs") and "/obj/" not in n:
                        csharp[n] = z.read(stored)
    if not csharp:
        raise SystemExit("gdal-csharp's win-64 package carried no csharp-source.zip")
    exported: set[str] = set()
    for w in wrappers:
        r = pe(shipped[w]) if windows else elf(shipped[w])
        exported |= r[1] if windows else r[2]
    # SWIG 4.4 also emits a SWIGStringWithLengthHelper whose three imports name the module ("Gdal")
    # rather than its wrapper and which nothing calls; the wrappers do not export them. They are
    # left out of the check only while that stays true.
    called = set()
    for blob in csharp.values():
        called |= set(re.findall(rb'DllImport\("\w+_wrap", EntryPoint="([^"]+)"', blob))
        if re.search(rb"SWIGStringWithLengthHelper\.", blob):
            raise SystemExit("the binding now calls SWIGStringWithLengthHelper, whose entry points the wrappers lack")
    absent = sorted(c.decode() for c in called if c.decode() not in exported)
    if absent:
        raise SystemExit(f"{rid}: {len(absent)} entry points the C# calls are not in the wrappers, e.g. {absent[:5]}")

    for name, blob in shipped.items():
        target = out / libdir / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(blob)
    for rel, blob in data.items():
        if rel.startswith("share/gdal/") and rel.endswith((".py", ".cs", ".nupkg", ".zip")):
            continue
        target = out / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(blob)
    for rel, blob in csharp.items():
        target = out / "csharp" / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        # SWIG's output, compiled into a project that treats warnings as errors: it is not ours to
        # document or to tidy, so its warnings are off in its own files and nowhere else.
        target.write_bytes(b"#pragma warning disable\n" + blob)
    # Generated code: the analyzers and nullable warnings of this repository are not for it.
    (out / "csharp" / ".editorconfig").write_text(
        "[*.cs]\ngenerated_code = true\ndotnet_analyzer_diagnostic.severity = none\n", encoding="utf-8")

    unowned = [n for n in shipped if n.split("/")[-1] not in owner]
    if unowned:
        raise SystemExit(f"{rid}: no package recorded for {unowned}")
    carried = sorted({owner[n.split("/")[-1]] for n in shipped} | {"gdal-csharp"})
    lines = []
    for entry in entries:
        if entry["name"] in carried or entry["name"] in ("proj", "libgdal-core"):
            lines.append(f"{entry['name']} {entry['version']} {entry['build']}  {entry['license']}  {entry['url']}")
            for lic, blob in licences.get(entry["name"], {}).items():
                target = out / "licenses" / entry["name"] / lic
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(blob)
    (out / "licenses" / "PACKAGES.txt").write_text(
        "The conda-forge packages whose files this directory carries (tools/gdal-natives.py, ADR-254).\n\n"
        + "\n".join(lines) + "\n", encoding="utf-8", newline="\n")

    size = sum(len(b) for b in shipped.values())
    print(f"{rid}: {len(shipped)} libraries, {size / 1e6:.1f} MB, from {len(carried)} packages", file=sys.stderr)
    stamp.write_text(stamp_text + "\n")


def main(argv: list[str]) -> None:
    if len(argv) >= 1 and argv[0] == "lock":
        directory = Path(argv[2]) if len(argv) >= 3 and argv[1] == "--repodata" else Path(tempfile.gettempdir()) / "graticula-repodata"
        lock(directory)
    elif len(argv) == 3 and argv[0] == "fetch":
        fetch(argv[1], Path(argv[2]))
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
