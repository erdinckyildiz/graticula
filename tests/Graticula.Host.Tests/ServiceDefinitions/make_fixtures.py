#!/usr/bin/env python3
"""Writes the small service definitions ServiceDefinitionArchiveTests opens (ADR-191 conditions 3, 4 and 5).

Run once with 7-Zip on the PATH or at its default Windows path; the .sd files it writes are checked in, so the
tests need neither 7-Zip nor Python. A service definition is a 7z archive; these are written by 7-Zip itself and
not by hand, and only the two refused ones are then edited: one entry's name in the (uncompressed) header is
overwritten with a name of the same length that climbs out (``../evl``) or is absolute (``C:/evl``), and the two
CRCs that cover the header are recomputed so the archive still opens. Nothing here is taken from a real Pro
archive: the layout (cd/<name>.gdb, serviceconfiguration.json, servicedescriptions/featureserver/featureserver.json,
esriinfo/iteminfo.xml, v/*.mapx) is what ADR-191 section 5 records Pro writing, and every byte is made up.

Usage: python make_fixtures.py   (from this folder)
"""

import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SEVEN = shutil.which("7z") or r"C:\Program Files\7-Zip\7z.exe"


def tree(root):
    files = {
        "serviceconfiguration.json": json.dumps({
            "folderName": "Pro",
            "service": {"serviceName": "Fixture_Service", "capabilities": "Query,Create",
                        "properties": {"maxRecordCount": 2000}},
        }),
        "servicedescriptions/featureserver/featureserver.json": json.dumps({
            "resources": [{"name": "layers", "resources": [
                {"contents": {"name": "Roads", "drawingInfo": {"renderer": {"type": "simple"}}}}]}],
        }),
        "esriinfo/iteminfo.xml": "<ESRI_ItemInformation><snippet>A fixture</snippet><tags>a,b</tags></ESRI_ItemInformation>",
        "cd/data.gdb/a00000001.gdbtable": "TABLE-ONE " * 50,
        "cd/data.gdb/a00000001.gdbtablx": "INDEX-ONE " * 20,
        "cd/data.gdb/gdb": "GDB",
        # Not read, by ADR-191 condition 4: a map document, a thumbnail, a file beside the geodatabase, and a file
        # whose content would break the JSON reader if anything opened it as configuration.
        "v/map.mapx": "{ not json at all",
        "esriinfo/thumbnail/thumbnail.png": "PNG?",
        "cd/readme.txt": "beside the geodatabase, not in it",
        "zz/evl": "the entry the refused fixtures rename",
    }
    for name, text in files.items():
        path = os.path.join(root, *name.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="") as out:
            out.write(text)


def seven(root, target):
    if os.path.exists(target):
        os.remove(target)
    subprocess.run([SEVEN, "a", "-t7z", "-mhc=off", "-mhe=off", "-m0=Copy", target, "."],
                   cwd=root, check=True, stdout=subprocess.DEVNULL)


def renamed(source, target, old, new):
    data = bytearray(open(source, "rb").read())
    was, now = old.encode("utf-16-le"), new.encode("utf-16-le")
    assert len(was) == len(now) and data.count(was) == 1, "the name must appear once in an uncompressed header"
    at = data.index(was)
    data[at:at + len(was)] = now
    offset, size = struct.unpack_from("<QQ", data, 12)
    header = bytes(data[32 + offset:32 + offset + size])
    struct.pack_into("<I", data, 28, zlib.crc32(header) & 0xFFFFFFFF)
    struct.pack_into("<I", data, 8, zlib.crc32(bytes(data[12:32])) & 0xFFFFFFFF)
    open(target, "wb").write(data)


def main():
    work = tempfile.mkdtemp()
    try:
        tree(work)
        valid = os.path.join(HERE, "valid.sd")
        seven(work, valid)
        renamed(valid, os.path.join(HERE, "climbs-out.sd"), "zz/evl", "../evl")
        renamed(valid, os.path.join(HERE, "absolute.sd"), "zz/evl", "C:/evl")
    finally:
        shutil.rmtree(work)
    return 0


if __name__ == "__main__":
    sys.exit(main())
