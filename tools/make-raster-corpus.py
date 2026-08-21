"""Build the COG corpus the raster reader tests read.

Run with the GDAL bindings that live inside ArcGIS Pro's Python, which is the only
GDAL on the development machine:

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" \
        tools/make-raster-corpus.py

Writes into tests/Graticula.Raster.Tiff.Tests/corpus. The files are checked in, so
this only needs running when a case is added or the corpus is lost.

GDAL is an independent implementation of the same published specification, and that
is the point: a reader verified only against files it was written alongside proves
nothing about a file anybody else produced. This is the same discipline
make-shapefile-corpus.py applies, for the same reason.

Nothing here runs in the serving process. A-016 keeps GDAL out of it, and
ADR-043 §3.5 keeps it out of the reader too -- this is a build tool.
"""
import os
import struct

from osgeo import gdal, osr

gdal.UseExceptions()

OUT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "tests", "Graticula.Raster.Tiff.Tests", "corpus")
os.makedirs(OUT, exist_ok=True)

# A tile smaller than GDAL's 512 default, so a 256-wide raster is genuinely several
# tiles across and the reader has to walk more than one of them to answer anything.
TILE = 128


def ramp(width, height, bands, dtype, seed=0):
    """Deterministic pixels: a diagonal ramp per band, so a wrong tile is visible.

    A constant image cannot tell a correct reader from one that returns the first
    tile for every request. A ramp can: every pixel's value is a function of where
    it is, so a transposition, an off-by-one row or a repeated tile all change the
    number the test reads back.
    """
    peak = 255 if dtype == gdal.GDT_Byte else 65535 if dtype == gdal.GDT_UInt16 else 1.0
    out = []
    for b in range(bands):
        plane = bytearray() if dtype != gdal.GDT_Float32 else []
        for y in range(height):
            for x in range(width):
                t = (x + y * 2 + b * 37 + seed) % 251 / 251.0
                v = t * peak
                if dtype == gdal.GDT_Byte:
                    plane.append(int(v) & 0xFF)
                elif dtype == gdal.GDT_UInt16:
                    plane.extend(struct.pack("<H", int(v) & 0xFFFF))
                else:
                    plane.append(float(v))
        out.append(plane)
    return out


def write(name, width, height, bands, dtype, epsg, compress,
          origin=(30.0, 41.0), pixel=(0.01, -0.01), nodata=None, overviews=True):
    """One corpus file, plus a sidecar of what it should read back as."""
    path = os.path.join(OUT, name)

    memory = gdal.GetDriverByName("MEM").Create("", width, height, bands, dtype)
    memory.SetGeoTransform([origin[0], pixel[0], 0.0, origin[1], 0.0, pixel[1]])

    reference = osr.SpatialReference()
    reference.ImportFromEPSG(epsg)
    memory.SetProjection(reference.ExportToWkt())

    planes = ramp(width, height, bands, dtype)

    for b in range(bands):
        band = memory.GetRasterBand(b + 1)

        # <b>Before the pixels, not after, and this was a real bug.</b> The MEM driver
        # treats SetNoDataValue as an instruction to initialise the buffer to that
        # value, so setting it after WriteRaster wipes everything just written. The
        # corpus's no-data file was silently all zeros for its first generation, GDAL
        # agreed it was all zeros, and the reader test passed because it only asserted
        # that *some* pixel was absent -- which is trivially true of an empty image.
        # Caught by looking at the rendered PNG, which was one flat colour.
        if nodata is not None:
            band.SetNoDataValue(nodata)

        if dtype == gdal.GDT_Float32:
            band.WriteRaster(0, 0, width, height,
                             struct.pack(f"<{len(planes[b])}f", *planes[b]),
                             buf_type=dtype)
        else:
            band.WriteRaster(0, 0, width, height, bytes(planes[b]), buf_type=dtype)

    if overviews:
        memory.BuildOverviews("AVERAGE", [2, 4])

    options = [
        f"BLOCKSIZE={TILE}",
        f"COMPRESS={compress}",
        "OVERVIEWS=" + ("AUTO" if overviews else "NONE"),
    ]

    gdal.GetDriverByName("COG").CreateCopy(path, memory, options=options)
    memory = None

    check = gdal.Open(path)
    band = check.GetRasterBand(1)
    print(f"  {name:38s} {check.RasterXSize}x{check.RasterYSize} "
          f"bands={check.RasterCount} overviews={band.GetOverviewCount()} "
          f"block={band.GetBlockSize()} {compress}")
    check = None


print(f"writing to {OUT}")

# <b>The compressions a COG in the wild actually uses.</b> DEFLATE is the default the
# COG driver picks, LZW is what most older pipelines emit, and NONE is the case where
# the reader must not assume there is a codec at all.
write("gray-byte-deflate.tif", 256, 192, 1, gdal.GDT_Byte, 4326, "DEFLATE")
write("gray-byte-lzw.tif", 256, 192, 1, gdal.GDT_Byte, 4326, "LZW")
write("gray-byte-none.tif", 256, 192, 1, gdal.GDT_Byte, 4326, "NONE")

# Three bands, because an RGB COG is the common case and the interleaving is a place
# a reader can be wrong without being obviously wrong.
write("rgb-byte-deflate.tif", 256, 192, 3, gdal.GDT_Byte, 4326, "DEFLATE")

# Web Mercator, so the reader is not only ever tested against degrees.
write("rgb-byte-3857.tif", 256, 256, 3, gdal.GDT_Byte, 3857, "DEFLATE",
      origin=(3_300_000.0, 5_000_000.0), pixel=(100.0, -100.0))

# Sixteen-bit and float, which is most scientific imagery and is where a reader that
# assumed bytes stops working.
write("gray-uint16-deflate.tif", 256, 192, 1, gdal.GDT_UInt16, 4326, "DEFLATE")
write("gray-float32-deflate.tif", 256, 192, 1, gdal.GDT_Float32, 4326, "DEFLATE")

# A no-data value, which is a rendering decision rather than a reading one and has to
# survive the read to be available to make.
write("gray-byte-nodata.tif", 256, 192, 1, gdal.GDT_Byte, 4326, "DEFLATE", nodata=0)

# No overviews at all: a legal TIFF and a COG only by courtesy, and the case where a
# reader that assumes a pyramid exists returns nothing.
write("gray-byte-no-overviews.tif", 256, 192, 1, gdal.GDT_Byte, 4326, "DEFLATE",
      overviews=False)

print("done")
