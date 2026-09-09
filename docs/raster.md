# Raster

**Status:** STUB — not written, except §1, which exists because
[ADR-009](adr/ADR-009-raster-engine.md) condition 3 asks for one limitation to be
written where a reader would meet it rather than only inside the decision that
created it.
**Required by:** §35

---

GDAL integration, COG and range requests, STAC, overviews, mosaics,
multidimensional raster, raster functions, dynamic imagery, reprojection, and
object storage access.

Expands [ADR-009](adr/ADR-009-raster-engine.md).

---

## 1. What this server can read, and the one thing it cannot

**It reads GeoTIFF, and it reads it without GDAL.** Imagery is registered *in place*
and never copied ([ADR-043](adr/ADR-043-imageserver-and-the-raster-face.md) §3.3):
registration reads the header and stores a reference, and every request reads the file
where it lies. `TiffCoverageReader` is our own reader, so nothing in the serving
artefact depends on GDAL ([ADR-009](adr/ADR-009-raster-engine.md) §2.2).

**A cloud-optimised GeoTIFF is faster, not required.** This is worth stating plainly
because the opposite was written down for a while and is easy to infer from *"other
formats are converted at registration"*. Measured 2026-09-09 on a running server: a
plain GeoTIFF with **no overviews and no tiling** registered normally and
`exportImage` drew it. What a COG buys is that a request for one corner reads that
corner, instead of reading enough of the file to find it. On a large image that is the
difference between a map that draws and one that does not; on a small one it is
nothing.

**What it cannot read is a raster that is not a GeoTIFF.** JPEG 2000, ECW, MrSID, HDF,
NetCDF and the rest need GDAL to convert them into something this server reads, and
**conversion needs somewhere to write**. That is the limitation:

> **A read-only registered source holding a non-GeoTIFF raster cannot be published.**
> There is nowhere to put the converted copy, and the original cannot be served as it
> is. Either the imagery is converted to GeoTIFF before this server sees it, or it is
> registered from a location this server may write to.

**You find this out at registration, not at draw time.** `POST /admin/coverages`
refuses such a file with **400** and says why:

```text
'…/thing.jp2' is not a TIFF this server can read. A coverage is registered as a
GeoTIFF, and a cloud-optimised one is the arrangement it reads fastest.
```

A limitation that surfaces when somebody first opens the map is a support call; one
that surfaces when they register the file is a decision they can still make
differently, which is the whole reason this section exists.

**Not the same as *no imagery without GDAL*.** GDAL is needed to *ingest* a non-GeoTIFF.
A deployment whose imagery already arrives as GeoTIFF never loads GDAL at all — see
[ADR-009](adr/ADR-009-raster-engine.md) §2.1, which is where the distinction is argued.
