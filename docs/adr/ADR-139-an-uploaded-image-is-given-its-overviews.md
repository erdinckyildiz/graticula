# ADR-139 — An uploaded image is given its overviews

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — measured before and after on the same image, and the full resolution is byte-for-byte the file's |
| **Decided** | 2026-10-02 by owner decision (*"sıra ile devam et"*, taking the remaining imagery items in order; the first, a tile cache, was measured and found to be the wrong remedy, and this is the one the measurement pointed at) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-123](ADR-123-imagery-comes-into-studio.md) (condition 2: what an upload does with the file) |

> **Amended 2026-10-02 by [ADR-140](ADR-140-several-images-are-one-mosaic.md).** Each image of a mosaic is given its overviews; the mosaic reads them level for level.

> **Amended 2026-10-03 by [ADR-148](ADR-148-download-capability-and-pyramids-for-every-image.md).** Images uploaded before this and images registered in place are given overviews too, in the background.

---

## 1. Context

The next imagery item was a tile cache. Before building it, its premise was measured: a 6000 × 6000 float32 elevation
model uploaded as a striped GeoTIFF without overviews — the shape an export from most tools takes — on the local
fixture.

| | z0–z4 tile | z6 | z8 | z10 | `exportImage`, whole image |
|---|---|---|---|---|---|
| no overviews | 2.7–4.3 s, every time | 1.2–1.7 s | 0.4–0.5 s | 0.04–0.06 s | 2.9–3.8 s |

Every zoomed-out picture read every pixel, because there was no smaller level to read. A tile cache answers the second
request for a tile; it does nothing for the first, and nothing for `exportImage`, which is how Studio's Map Viewer and
the JS SDK's `ImageryLayer` draw an image service and which is not cached. The cost was the missing pyramid.

## 2. Alternatives considered

### Alternative A — Overviews beside the image, as GDAL's `.ovr` (chosen)

An upload whose image has no overviews and is larger than one 256-pixel tile is given them: a TIFF beside it,
`<file>.ovr`, holding the image halved until it fits a tile, tiled 256 and deflated. That is GDAL's external overview,
and what ArcGIS's Build Pyramids writes for a TIFF, so the reader reads either — a file registered in place with
pyramids built by ArcGIS or GDAL now uses them too. Overviews inside a file still win. An `.ovr` whose bands or sample
size are not the image's is not used.

- **Floating-point values are averaged**, no-data and NaN left out; **integer values are taken from one pixel**,
  because an integer band may be classes and the average of two classes is neither.
- **The image itself is not rewritten.** The full resolution served is the uploaded file, byte for byte.
- **Built during the upload**, before the service is published, so it is never served half-built. A failure is logged
  (event 1090) and the image is published without them; it is servable either way.
- **Deleted with the image.**

### Alternative B — Rewrite the upload as a cloud-optimised GeoTIFF

**For:** one file. **Against:** the full resolution is rewritten, which is the one part of the file whose exactness a
user relies on, and it costs a full write of the largest level for no reading gain over the external file.

### Alternative C — The tile cache that was next in line

**Against:** §1. It may still be wanted for a service whose rendering is expensive at every level; that is a separate
decision with its own measurement.

## 3. Counterarguments to the preferred option

- *The upload takes longer*: 2.6–2.8 s for this 144 MB model in a Release build (about 4 s at deflate's default level;
  level 1 was chosen), roughly linear in size, so a minute or more for a multi-gigabyte image. Studio's upload dialog
  says what is happening once the last byte is sent, with a bar that shows work rather than a full one.
- *A proxy in front may cut a long upload off.* nginx's default `proxy_read_timeout` is 60 s. The showcase is behind
  Caddy, whose `reverse_proxy` sets no response timeout and streams the request body (read 2026-10-02); a deployment
  behind nginx sets `proxy_read_timeout`, `client_max_body_size` and `proxy_request_buffering off` for uploads.
- *Images uploaded before this have none.* No image service is on the showcase; an operator can re-upload. Building
  them for files already published, and for files registered in place, is not done here.
- *Overviews add a third or so to the disk an image takes* (40 MB beside 144 MB here).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Without overviews, the model's tiles and whole-image export cost seconds at every zoomed-out level, every time | §1's table | local fixture, 2026-10-02 |
| With them: z0–z6 tiles 0.03–0.05 s; whole-image `exportImage` 0.5 s; z8–z10 unchanged | the same requests against the same file uploaded again | local fixture, 2026-10-02 |
| The full resolution is the file's: a TIFF export at full resolution is byte-identical with and without overviews, and `identify` answers the same value | `cmp` and `identify` on both services | local fixture, 2026-10-02 |
| Building them: 2.6–2.8 s for 36 million float values; reading the whole image alone is 0.6 s | a timing run in Release | local run, 2026-10-02 |
| Averaging leaves no-data out; integers take one pixel; every type is written as itself; an image with overviews, or one tile's size, is left alone; another image's `.ovr` is not used | `TiffPyramidBuilderTests` | this repository |
| An upload is given overviews, draws whole from them, and answers its own value close up; deleting it removes them | `ImageryDisplayTests` | this repository |
| The upload dialog's wait: every way out (Stop, ✕, Escape) stops it, where the ✕ and Escape stopped nothing and the page jumped to the service minutes later; focus on Stop; Back hidden and the fields locked; a clock while the image is prepared; a stop after the last byte checks whether it was already published | ux review, then `ImportFormTests` | this repository |
| An upload stopped part way leaves nothing on disk — it left the whole image, in no catalogue entry; the ArcGIS append upload and the import scratch directory left theirs too | ux review measured the leak; fixed in all three | this repository |

## 5. Decision

An image uploaded without overviews, larger than one tile, is given them beside it as `<file>.ovr`, built during the
upload and deleted with it; the reader uses a file's own overviews, or else a matching `.ovr` beside it.

## 6. Consequences

**Positive.** A large image is as quick to see whole as zoomed in, in the Map Viewer and in any ArcGIS client; and a
registered file with pyramids built elsewhere uses them.

**Negative.** Longer uploads, and a third more disk, as §3 says.

**State.** A file beside each uploaded image; no schema change.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The imagery items are taken in order, each measured first | Stated by the owner, 2026-10-02 |

## 8. Dependencies

**Depends on:** ADR-043, ADR-123.

**Depended on by:** —

## 9. Revisit triggers

- An upload measured spending most of its time building overviews, or one large enough that the request times out.
- Owners asking for overviews on files registered in place, or on images uploaded before this.
