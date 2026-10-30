"""The Exif tags of a photo that GeoTaggedPhotosToPoints and MatchPhotosToRowsByTime read — ADR-228.

Read with the standard library from the public Exif standard (CIPA DC-008, *Exchangeable image file format for digital
still cameras*) and the TIFF 6.0 structure it is written in: a JPEG's APP1 segment that begins ``Exif\\0\\0``, or a TIFF
file's own header. Only what the two tools use is read: ``DateTimeOriginal`` (0x9003) in the Exif IFD, and in the GPS IFD
the latitude, longitude, altitude and image direction with their references. HEIF photos (``.heic``) are not read: their
Exif sits in an ISO base media file box, which grapy does not parse.
"""
from __future__ import annotations

import datetime
import struct

_SIZES = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 4, 10: 8}


class Unreadable(ValueError):
    """A photo whose Exif metadata is missing or malformed."""


def _tiff(data):
    """The TIFF block of a JPEG or TIFF file's bytes."""
    if data[:2] in (b"II", b"MM"):
        return data
    if data[:2] != b"\xff\xd8":
        raise Unreadable("not a JPEG or TIFF file")
    at = 2
    while at + 4 <= len(data):
        if data[at] != 0xFF:
            raise Unreadable("malformed JPEG markers")
        marker = data[at + 1]
        if marker in (0xD9, 0xDA):  # end of image, start of scan: no Exif before the image data
            break
        length = struct.unpack(">H", data[at + 2: at + 4])[0]
        body = data[at + 4: at + 2 + length]
        if marker == 0xE1 and body[:6] == b"Exif\0\0":
            return body[6:]
        at += 2 + length
    raise Unreadable("no Exif segment")


def _ifd(block, offset, order):
    """{tag: value} of one IFD; RATIONALs as floats, ASCII as text, several values as a tuple."""
    if offset + 2 > len(block):
        raise Unreadable("an IFD lies outside the file")
    count = struct.unpack(order + "H", block[offset: offset + 2])[0]
    tags = {}
    for i in range(count):
        entry = block[offset + 2 + 12 * i: offset + 14 + 12 * i]
        if len(entry) < 12:
            raise Unreadable("a truncated IFD")
        tag, kind, n = struct.unpack(order + "HHI", entry[:8])
        size = _SIZES.get(kind)
        if size is None:
            continue
        raw = entry[8:12] if size * n <= 4 else block[struct.unpack(order + "I", entry[8:12])[0]:][: size * n]
        if len(raw) < size * n:
            raise Unreadable("a tag's value lies outside the file")
        if kind == 2:
            value = raw.split(b"\0", 1)[0].decode("ascii", "replace")
        elif kind in (5, 10):
            parts = struct.unpack(order + ("I" if kind == 5 else "i") * (2 * n), raw[: 8 * n])
            value = tuple(parts[j] / parts[j + 1] if parts[j + 1] else None for j in range(0, 2 * n, 2))
        else:
            letter = {1: "B", 3: "H", 4: "I", 7: "B", 9: "i"}[kind]
            value = struct.unpack(order + letter * n, raw[: size * n])
        if isinstance(value, tuple) and len(value) == 1:
            value = value[0]
        tags[tag] = value
    return tags


def read(path):
    """(time taken, longitude, latitude, altitude, direction) of a photo, each None when it is not recorded."""
    with open(path, "rb") as opened:
        data = opened.read()
    block = _tiff(data)
    if block[:2] not in (b"II", b"MM"):
        raise Unreadable("malformed TIFF header")
    order = "<" if block[:2] == b"II" else ">"
    if struct.unpack(order + "H", block[2:4])[0] != 42:
        raise Unreadable("malformed TIFF header")
    first = _ifd(block, struct.unpack(order + "I", block[4:8])[0], order)
    taken = None
    if 0x8769 in first:
        exif = _ifd(block, first[0x8769], order)
        taken = _time(exif.get(0x9003))
    longitude = latitude = altitude = direction = None
    if 0x8825 in first:
        gps = _ifd(block, first[0x8825], order)
        latitude = _degrees(gps.get(2), gps.get(1), "S")
        longitude = _degrees(gps.get(4), gps.get(3), "W")
        if isinstance(gps.get(6), float):
            altitude = -gps[6] if gps.get(5) == 1 else gps[6]
        if isinstance(gps.get(17), float):
            direction = gps[17]
    return taken, longitude, latitude, altitude, direction


def _degrees(value, reference, negative):
    if not isinstance(value, tuple) or len(value) != 3 or any(v is None for v in value):
        return None
    degrees = value[0] + value[1] / 60 + value[2] / 3600
    return -degrees if str(reference or "").upper() == negative else degrees


def _time(text):
    """Exif's ``YYYY:MM:DD HH:MM:SS``; None when blank or malformed."""
    try:
        return datetime.datetime.strptime(str(text).strip(), "%Y:%m:%d %H:%M:%S") if text else None
    except ValueError:
        return None
