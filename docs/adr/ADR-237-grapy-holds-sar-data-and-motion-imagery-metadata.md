# ADR-237 — grapy holds SAR data and motion imagery metadata

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the Sentinel-1 calibration and thermal noise removal, the unit conversions and the range-Doppler geometry, which follow ESA's published definitions and are tested against independent solutions · `MEDIUM` for the equivalents (speckle filters, coherence, interferogram, Goldstein, PCG unwrapping, terrain correction and flattening, CFAR, CMOD5.N winds), which are the literature's methods and not Pro's · `LOW` for agreement with Pro on any number, since no real scene has been run through both |
| **Decided** | 2026-10-31, by owner decision: the SAR, motion video and ocean names of `arcpy.ia` may no longer be refused as *"needs a data model this server does not hold yet"*; each is built, an equivalent, or refused with its own reason. The SAR dataset form below and the choices §5.8 lists are INFERRED and listed for confirmation in [Q-205](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision `arcpy.ia` refused thirty names of its Synthetic Aperture Radar toolset, SAR Analysis toolset
and motion imagery tools with one sentence, *"it needs a data model this server does not hold yet (ADR-224 group 2)"*.
That is a schedule, not a reason, and ADR-224's rule forbids it. A user tool that calibrates a Sentinel-1 scene, makes
an interferogram or reads a drone's metadata table could not run on this server.

What the public pages define, and what they do not (doc.esri.com, which pro.arcgis.com's pages redirect to, read
2026-10-31; no Esri implementation was read, CLAUDE.md §5):

- **The SAR tools are documented by parameters and purpose.** The calibration types (beta, sigma, gamma nought), the
  conversions of Convert SAR Units, the speckle filters by name with their parameters, Deburst's and Multilook's
  purpose, Unwrap Phase's *least squares Preconditioned Conjugate Gradient* by name, the RTC chain's order. **No page
  gives a formula** for a filter, an index, coherence, phase removal or terrain flattening.
- **The data is ESA's, and ESA publishes it.** The Sentinel-1 SAFE layout, the annotation, calibration and noise XML,
  the calibration equations (*Radiometric Calibration of S-1 Level-1 Products*, ESA-EOPG-CSCOP-TN-0002), thermal
  denoising (MPC-0392) and the orbit file format are public. Esri's CRF and its SAR metadata are not.
- **Three names have no reference page.** `S1RadiometricCalibration`, `S1ThermalNoiseRemoval` and `RadarCalibration`
  answer 404 under `arcpy/image-analyst/`; only *Radar Calibration function* has a raster function page.
- **The motion imagery tools** read and write video containers (.ts, .mp4, .avi …) and the MISB ST 0601 KLV stream in
  them, except Convert Video Metadata, which works on a .csv. grapy's environment has no demuxer, muxer or decoder
  (numpy, scipy, shapely, pyproj, rasterio, measured with `pip list` on 2026-10-31).

## 2. Alternatives considered

### Alternative A — grapy's own SAR dataset, ESA's definitions for Sentinel-1, the literature where the pages are silent (chosen)

**For.** Every SAR tool's input and output becomes a file grapy can read back with its metadata; Sentinel-1, the one
mission whose products are free and fully documented, is read from its SAFE directly; each undocumented computation is
a cited method, counted as an equivalent (ADR-224 §5.6). No new wheel.
**Against.** A grapy SAR dataset does not open in Pro, and Pro's numbers will differ wherever an equivalent stands.

### Alternative B — write and read Esri's CRF with its SAR metadata

**For.** Interchange with Pro. **Against.** The CRF and its SAR metadata are not published; reproducing them would be
reverse engineering (CLAUDE.md §5).

### Alternative C — adopt a SAR toolbox (ESA SNAP through snappy, ISCE, SNAPHU for unwrapping)

**For.** Mature, widely compared. **Against.** SNAP is GPL and a Java runtime, SNAPHU is taken as GPL, and nothing
linked here is copyleft (DEPENDENCY-LICENSES.md); ISCE is heavy. And Tier 1 (§4) is ours: the computations are server
domain, not a Tier 2 library behind a port.

### Alternative D — PyAV or OpenCV for the video tools

**For.** Would demux KLV and decode frames. **Against.** Neither is in the image, both carry FFmpeg, whose builds are
LGPL or GPL by configuration and whose codecs carry patent questions; the brief for this work was to refuse rather than
vendor one, and the tools' value on a server without a video player is small.

## 3. Counterarguments to the preferred option

- A full Sentinel-1 IW SLC swath is about 1.3 GB per polarization. grapy reads a band whole, so the chain runs into
  ADR-223's memory limits on real scenes long before it runs out of correctness. Nothing has been measured (§7).
- Interferometry on Sentinel-1 IW, the mode almost everyone uses, does not run end to end: ApplyCoregistration refuses
  TOPS data, because resampling a burst needs deramping and enhanced spectral diversity. The interferogram, coherence,
  filter and unwrapping tools work on any coregistered pair, and on stripmap data the chain is whole.
- An equivalent under Pro's name will be compared with Pro and found to differ. ADR-224 §5.6 accepts this and says so
  in every run.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Calibration and noise removal are ESA's equations | σ0 = DN²/A² at a hand-interpolated LUT; (DN² − η)/A² with η the range LUT times the azimuth LUT | `test_sar.py` SafeAndCalibration |
| The range-Doppler geometry is right | A point on the equator is at zero Doppler at the crossing, at the range of the cosine rule (hand-worked); geocoding agrees with an independent `fsolve` solution to 1 cm, back-geocoding to 0.001 cell | `test_sar.py` Geometry |
| Deburst joins bursts as specified | three bursts with a 3-line overlap read back line by line | DeburstAndMultilook |
| Terrain correction places cells right | each output cell's value is the radar column and row an independent zero-Doppler solve gives, to 0.01 cell | Terrain |
| Flattening reduces to the textbook on flat terrain | γ0 = β0·tan θ and σ0 = β0·sin θ, median within 3 % | Terrain |
| The interferogram's reference phase is removed | a flat-earth pair of exp(−i4πR/λ) samples with a 170 m baseline leaves a constant 0.05 rad (0.2 mm of orbit interpolation) and no fringes | Interferometry |
| Unwrapping recovers phase | a ramp of several cycles to 1e-3 rad | Interferometry |
| CMOD5.N is transcribed right | Table 1's coefficients reproduce Figures 4–6 of ECMWF Tech. Memo. 554: the speed shift 0.32–0.79 m/s, B1 and B2 differences and ranges | Ocean |
| The ocean tools detect | a 30 × 100 m ship measured to 4 m; a 300 × 400 m slick found and a 20 × 20 m one left; streaks at 30° and a 10 m/s CMOD wind recovered within 5° and 1 m/s | Ocean |

## 5. Decision

### 5.1 The SAR dataset (INFERRED)

A SAR dataset is a GeoTIFF whose bands are named by polarization, complex64 for complex data and float32 otherwise,
with NoData as NaN, beside `<path>.sar.json` (`format: grapy-sar/1`): mission, mode, product, what the values are
(`complex`, `amplitude`, `intensity`, `phase`, `unwrapped_phase`, `displacement`, `coherence` …), the scale (`linear`
or `db`), the calibration (`none`, `beta0`, `sigma0`, `gamma0`), whether noise was removed, the looks, the radar
frequency and spacings, the orbit state vectors, the geolocation tie points, the bursts, each polarization's
calibration and noise LUTs, and for every row and column the original line and pixel it came from — so a LUT is
looked up correctly after Deburst or Multilook. In radar geometry the GeoTIFF has no reference; after terrain
correction it is a WGS 84 grid. A `.crf` name is written as a GeoTIFF under that name.

### 5.2 Sentinel-1 is read from its SAFE

From the folder, `manifest.safe`, or `<SAFE>/<swath>`; one swath per dataset, as many polarizations as it holds. The
IPF version is read from the manifest, so calibration refuses IPF 2.34 or earlier, as documented.

### 5.3 Built to the pages and ESA

ApplyRadiometricCalibration, RemoveThermalNoise, Multilook, ConvertSARUnits, GenerateMaskedSARRaster,
GenerateMultitemporalCoherence, ApplyOrbitCorrection and ConvertVideoMetadata. Apply Complex Data Filter's page
contradicts itself — its usage note uses the inner window where coherence is low, its parameters' explanations where
it is high; grapy follows the parameters.

### 5.4 Built as equivalents (ADR-224 §5.6)

Deburst, Despeckle (Lee 1980, Refined Lee 1981, Enhanced Lee and Gamma-MAP Lopes 1990, Frost 1982, Kuan 1985),
ComputeCoherence, GenerateInterferogram, ApplyComplexDataFilter (Goldstein–Werner 1998), UnwrapPhase (Ghiglia–Romero
1994 PCG), ComputeSARIndices, DetectBrightOceanObjects (two-parameter CFAR), DetectDarkOceanAreas (Solberg 1999),
ExtractOceanWinds (Koch 2004 directions, CMOD5.N speeds), ApplyGeometricTerrainCorrection (range-Doppler, Small and
Schubert 2008), ApplyRadiometricTerrainFlattening (Small 2011), ApplyCoregistration (geometric, Sansosti 2006),
GenerateRadiometricTerrainCorrectedData (the chain of the above) and RadarCalibration. Each names its method and
source in its docstring and warns once per run.

### 5.5 Refused, each with its own reason

- **DownloadOrbitFile** — downloads from the Copernicus Data Space Ecosystem; a fenced tool has no network (ADR-223
  §5.3). ApplyOrbitCorrection applies a file the tool is given. The RTC tool's `username`, `password` and
  `cloud_storage_connection` are refused by name for the same reason.
- **S1RadiometricCalibration, S1ThermalNoiseRemoval** — no reference page could be read, so their parameters are
  unknown; ApplyRadiometricCalibration, RadarCalibration and RemoveThermalNoise do the work.
- Parameters refused by name: `geoid` GEOID wherever a DEM is given (grapy carries no EGM96 grid, as ADR-234 refuses
  it); ApplyRadiometricTerrainFlattening's two geometric distortion outputs (the page does not define the
  foreshortening ratio or the class boundaries); ApplyCoregistration on TOPS (IW, EW) SLC.

### 5.6 The video tools

ExtractVideoFramesToImages, GenerateVideoMetadataStationary, VideoMetadataToFeatureClass and VideoMultiplexer are
refused: each reads or writes a video stream inside a container, and a user tool on this server has no demuxer, muxer
or decoder; grapy does not vendor one (Alternative D). ConvertVideoMetadata, which needs only the .csv, is built; its
fields carry their MISB ST 0601 tags.

### 5.7 Geometry

WGS 84, ECEF; the orbit by cubic Hermite interpolation of positions and velocities; a pixel's slant range time from
the tie points, linearly along each tie-point row and in azimuth time between rows; geocoding by Newton's method on
zero Doppler, the range sphere and the raised ellipsoid; back-geocoding by Newton on zero Doppler. The Earth is not
rotated during the flight time beyond what the state vectors (Earth-fixed, as Sentinel-1's are) already carry.

### 5.8 INFERRED choices (listed in Q-205) — **Confirmed 2026-10-31 by owner decision in Q-205** (*"Hepsini onayla"*)

(a) the dataset form of §5.1 and the SAFE override `grapy-orbit.json` that ApplyOrbitCorrection writes beside a
product rather than modifying it; (b) dB as 10·log10 of intensity and 20·log10 of amplitude, displacement −λφ/4π
positive towards the sensor, negative denoised power set to 0 *(Pro 3.7 measured 2026-10-31, condition 3: **changed
to Pro's** — 1e-5, calibrated as 2¹⁹ × 1e-5 / A²)*; (c) the default looks rounded to the nearest whole look,
partial blocks dropped, complex data detected before Multilook and Despeckle; (d) Deburst's split at the middle of
each overlap; (e) the interferogram's sign convention exp(−i4πR/λ), its reference phase every 8 cells interpolated
cubically, the coregistration lattice every 16 cells; (f) Goldstein's coherence threshold 0.3 over the outer window,
patches overlapping by half with a triangular taper, a 3 × 3 spectral smoothing; (g) Unwrap Phase's 5 × 5 effective
coherence window and its constant chosen to agree with the wrapped phase; (h) RVI, RFDI and CSI's formulas;
(i) CFAR at 5 standard deviations with a guard window of the longest object and a ring as wide again, objects
measured by their minimum rotated rectangle, the fields Length, Width, PixelCount, MaxValue, MeanValue; dark areas
below half the 101 × 101 mean; water at DEM ≤ 0; land grown and water shrunk by the tolerance; (j) winds in blocks of
about 3 km, the 180° ambiguity resolved towards a wind from the northern half, a right-looking sensor, Thompson's
polarization ratio α = 0.6 for HH, U and V as the components of the velocity the wind blows with; (k) terrain
correction's cell the finer ground spacing, heights without a DEM interpolated over the tie points; flattening's DEM
resampled to half the ground spacing, facets shared bilinearly, self-shadow only, the DEM required in EPSG:4326 as
documented; (l) the RTC chain skipping Multilook for ground range products and keeping levels as `<level>.tif`;
(m) RadarCalibration's parameter names `raster` and `calibration_type`; (n) ConvertVideoMetadata's unit keywords,
copied columns, header names and mapping file layout.

## 6. Consequences

- **State.** None in the catalogue or the server: a SAR dataset is files in the run's directory.

**Positive.** A user tool can calibrate, denoise, deburst, multilook, filter, terrain-correct and flatten Sentinel-1
data, make coherence, interferograms and unwrapped phase, find ships, slicks and winds, and convert motion imagery
metadata, on this server, by arcpy's names. No new wheel.

**Negative.** Nothing interchanges with Pro; TOPS interferometry stops at coregistration; the video tools stay
refused; a real scene's size against ADR-223's limits is unmeasured ([D-328](../architecture-debt.md)).

**Names.** `tools/grapy-refusals.json` before → after: `arcpy.ia` built 156 → 179 (equivalents 4 → 19), refused
191 → 168; all modules built 1171 → 1194 (equivalents 125 → 140), refused 1003 → 980, unexamined 0 → 0. Seven reasons
in `arcpy.ia` reworded from the batch sentence to their own.

**Ports created.** None: rasterio reads and writes the GeoTIFFs as it already does for grapy's rasters.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A user tool's SAR scene fits in its run's memory; a full Sentinel-1 swath probably does not | Unverified (D-328) |
| — | Sentinel-1 state vectors are Earth-fixed, so no Earth rotation term is needed | Holds for ESA's annotation and POD files |
| — | The CMOD5 functional form transcribed from Hersbach et al. (2007) is right | Tested against TM-554's published figures |

## 8. Dependencies

**Depends on**: [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6,
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence, no network, the limits); clean room
(CLAUDE.md §5): only documented behaviour and public standards are reproduced.

**Depended on by**: —

## 9. Revisit triggers

- A real Sentinel-1 product run through the chain (condition 3) disagrees with SNAP or Pro beyond the equivalents'
  expected differences.
- A permissively licensed demuxer enters the image: VideoMetadataToFeatureClass becomes buildable for .ts.
- The S1RadiometricCalibration or S1ThermalNoiseRemoval page becomes readable.
- An EGM96 grid enters the image: GEOID becomes buildable here and in ADR-234.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The SAR tools are built and answer hand-worked or independently solved values.** **DISCHARGED 2026-10-31**:
   `test_sar.py`, forty-five tests on a synthetic Sentinel-1 SAFE and a synthetic orbit (§4); `test_video.py`, four.
2. **Every name of the thirty is built, an equivalent, or refused with its own reason**, none with the batch sentence.
   **DISCHARGED 2026-10-31**: `tools/grapy_refusals.py --check` reads none unexamined, and the refused names' reasons
   are tested.
3. **A real Sentinel-1 IW GRD product and a stripmap SLC pair are run through the chain** and compared with SNAP's or
   Pro's output, and their memory measured against ADR-223's limits.
   **PARTLY DISCHARGED 2026-10-31 — the GRD half.** The product is S1D_IW_GRDH_1SDV_20261031T054608 (IPF 4.03,
   26,325 × 21,331), downloaded with no account from Microsoft Planetary Computer's `sentinel-1-grd` collection (an
   anonymous SAS token; the SAFE's files are there under shortened names and were put back under the manifest's), and
   run through RemoveThermalNoise, ApplyRadiometricCalibration (SIGMA_NOUGHT) and ApplyGeometricTerrainCorrection without
   a DEM, VV, in ArcGIS Pro 3.7 (`tools/pro-compare/adr237-s1grd-*.py`). **Radiometry (a)**: grapy's noise and
   calibration look-up tables on six 512 × 512 windows across the swath agree with Pro's to 6e-8 relative (float32's
   precision) and sigma nought to under 0.0001 dB. **Changed to Pro's (b)**: where the noise exceeds the power Pro
   writes 1e-5, not 0, and calibrates that pixel as 2¹⁹ × 1e-5 / A² (all 36 floored pixels of the six windows, to seven
   digits). **A grapy bug, fixed**: grapy grouped the tie points by azimuth time, and a real GRD's 21 points on one line
   differ by 0.55 ms across the swath, so no row formed and back-geocoding failed; they are grouped by line now, the time
   read along each row. **Geometry (d)**: on 131,072 cells of Pro's terrain-corrected output in eight windows, grapy's
   range-Doppler position falls on the same azimuth line as Pro's (correlation peak at 0 lines in all eight) and from
   +0.5 to −6 range pixels away (0.80–0.97 correlation at the peak); moving grapy's tie-point heights up 30 m aligns two
   windows and not the others, so Pro places its cells on heights other than the annotation's tie points, by a rule its
   output does not show — [D-328](../architecture-debt.md). **Memory**: grapy's RemoveThermalNoise on the whole scene
   peaked at **22,009 MB** of committed memory (it reads both polarizations whole and works in float64), eleven times
   ADR-223's 2 GB, and its terrain correction refuses the scene by its 50-million-cell limit (the grid would be about
   29,000 × 21,000) — D-328. **Not run, the SLC half**: no stripmap SLC pair can be had without an account — Copernicus
   Data Space and ASF both require a login, the AWS registry's Sentinel-1 buckets are requester-pays (an AWS account),
   and Planetary Computer holds GRD and RTC only — so it waits for a product the owner provides.
4. **The owner confirms or changes §5.8** ([Q-205](../open-questions.md)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-205 (*"Hepsini onayla"*).
