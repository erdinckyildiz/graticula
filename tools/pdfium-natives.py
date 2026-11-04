"""PDFium for the import reader's PDF thumbnails: one pinned library per platform — ADR-268.

Owner decision 2026-11-04 on D-321, *"Evet, PDFium ekle"*: a PDF a member uploads is given its
first page as its picture when its owner gave it none. ADR-254 had left the decision open in one
sentence — *"it is PDFium (BSD-3-Clause/Apache-2.0) or nothing, and it is a separate decision"* — and
this is that decision's payload. It is not GDAL's PDF driver: GDAL stays without one, and
tools/gdal-natives.py still refuses a file named PDFium inside GDAL's closure.

    python tools/pdfium-natives.py fetch RID OUT
        RID is linux-x64, linux-arm64 or win-x64. Downloads the archive deploy/pdfium/
        pdfium-natives.lock.json names for it (cached in $GRATICULA_PDFIUM_CACHE or
        ~/.cache/graticula-pdfium), checks its SHA-256, and writes OUT:
          libpdfium.so (Linux) or pdfium.dll (Windows) — the one library, nothing else from the
              archive's lib/ or bin/;
          licenses/ — PDFium's LICENSE and every third party's notice from the archive, because a
              notice has to travel with every copy and an image is a copy;
          VERSION — the build's version, read by the reader's ping.
        It refuses — and the build fails — if the checksum differs, if an archive member would be
        written outside OUT, or if a Linux library asks for anything beyond libc, libm, libpthread,
        libdl and libgcc_s, which every glibc image has.

Standard library only, so the Docker stage that runs it needs nothing installed.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import re
import shutil
import sys
import tarfile
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
LOCK = REPO / "deploy" / "pdfium" / "pdfium-natives.lock.json"

# What a glibc system provides beneath any program; anything else would have to be shipped too.
SYSTEM = {"libc.so.6", "libm.so.6", "libpthread.so.0", "libdl.so.2", "libgcc_s.so.1", "ld-linux-x86-64.so.2",
          "ld-linux-aarch64.so.1"}


def cache_dir() -> Path:
    given = os.environ.get("GRATICULA_PDFIUM_CACHE")
    return Path(given) if given else Path.home() / ".cache" / "graticula-pdfium"


def download(entry: dict) -> bytes:
    target = cache_dir() / entry["sha256"][:16] / entry["url"].rsplit("/", 1)[-1]
    if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == entry["sha256"]:
        return target.read_bytes()

    with urllib.request.urlopen(entry["url"], timeout=120) as response:
        data = response.read()

    actual = hashlib.sha256(data).hexdigest()
    if actual != entry["sha256"]:
        raise SystemExit(f"{entry['url']}: SHA-256 is {actual}, the lock says {entry['sha256']}. Refusing it.")

    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    return data


def needed(library: bytes) -> set[str]:
    """The DT_NEEDED names of an ELF library, read from its dynamic string table by name rather than parsed: the
    names are the only thing this check needs, and they are NUL-terminated `lib….so…` strings."""
    if library[:4] != b"\x7fELF":
        return set()
    return {m.decode() for m in re.findall(rb"\x00(lib[A-Za-z0-9_+.-]+\.so(?:\.[0-9]+)*)\x00", library)} - {"libpdfium.so"}


def fetch(rid: str, out: Path) -> None:
    lock = json.loads(LOCK.read_text(encoding="utf-8"))
    entry = lock["platforms"].get(rid)
    if entry is None:
        raise SystemExit(f"{rid}: no PDFium in {LOCK.relative_to(REPO)}. Platforms: {', '.join(lock['platforms'])}.")

    data = download(entry)
    if out.exists():
        shutil.rmtree(out)
    (out / "licenses").mkdir(parents=True)

    library_name = entry["library"].rsplit("/", 1)[-1]
    found = False

    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        for member in archive.getmembers():
            name = member.name.lstrip("./")
            if not member.isfile():
                continue
            if name.startswith("/") or ".." in Path(name).parts:
                raise SystemExit(f"{entry['url']}: member '{member.name}' would be written outside {out}. Refusing it.")

            if name == entry["library"]:
                content = archive.extractfile(member).read()
                extra = needed(content) - SYSTEM
                if extra:
                    raise SystemExit(f"{library_name} for {rid} needs {', '.join(sorted(extra))}, which a server image "
                                     "does not carry. Refusing it.")
                (out / library_name).write_bytes(content)
                found = True
            elif name == "LICENSE":
                (out / "licenses" / "PDFIUM-LICENSE").write_bytes(archive.extractfile(member).read())
            elif name.startswith("licenses/"):
                (out / "licenses" / Path(name).name).write_bytes(archive.extractfile(member).read())
            elif name == "VERSION":
                (out / "VERSION").write_bytes(archive.extractfile(member).read())

    if not found:
        raise SystemExit(f"{entry['url']} has no {entry['library']}. Refusing it.")

    (out / "licenses" / "SOURCE.txt").write_text(
        f"PDFium {lock['release']} from {lock['source']}\n{entry['url']}\nsha256 {entry['sha256']}\n", encoding="utf-8")
    (out / ".complete").write_text(entry["sha256"], encoding="utf-8")
    print(f"PDFium {lock['release']} for {rid} in {out}")


def main(argv: list[str]) -> None:
    if len(argv) == 4 and argv[1] == "fetch":
        fetch(argv[2], Path(argv[3]))
        return
    raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv)
