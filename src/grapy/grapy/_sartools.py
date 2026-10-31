"""The synthetic aperture radar tools of arcpy.ia, over grapy's SAR datasets (``_sar``) — ADR-237.

Each is built to its reference page on doc.esri.com (read 2026-10-31) and to the public specification of the data it
reads: ESA's for Sentinel-1. What a page leaves open is labelled INFERRED in the tool's docstring and listed in
ADR-237 §5; what grapy cannot do is refused by name. Where a page names a computation without specifying it (a speckle
filter, the coherence estimator, an unwrapping method), the tool is an equivalent (ADR-224 §5.6): it names its method
and source, and ``arcpy.ia`` marks it so.
"""
from __future__ import annotations

import math
import os

import numpy as np

from . import _sar
from ._result import Result
from ._runtime import AddMessage, ExecuteError, NotBuiltError, env
from ._sar import C, polarizations, word
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

# (method, source) of each equivalent, read by arcpy.ia when it marks the tool
DEBURST = ("bursts joined at the middle of each overlap, invalid samples blanked",
           "ESA Sentinel-1 Product Specification S1-RS-MDA-52-7441, burst layout")
DESPECKLE = ("Lee (1980), Refined Lee (Lee 1981), Enhanced Lee and Gamma-MAP (Lopes et al. 1990), Frost (1982) and "
             "Kuan (1985) local-statistics filters",
             "Lee, IEEE PAMI 2(2) 1980; Lee, CGIP 17 1981; Lopes, Touzi and Nezry, IEEE TGRS 28(6) 1990; Frost et al., "
             "IEEE PAMI 4(2) 1982; Kuan et al., IEEE PAMI 7(2) 1985")
COHERENCE = ("boxcar sample coherence |Σ s1 s2*| / sqrt(Σ|s1|² Σ|s2|²)",
             "Touzi et al., IEEE TGRS 37(1) 1999; Hanssen, Radar Interferometry, 2001, §4.4")
INTERFEROGRAM = ("s1 s2* with the phase of the reference geometry, 4π(R2 − R1)/λ on the ellipsoid or DEM, removed",
                 "Hanssen, Radar Interferometry, 2001, §2.4")
GOLDSTEIN = ("Goldstein-Werner adaptive spectral filter on overlapping patches",
             "Goldstein and Werner, Geophys. Res. Lett. 25(21) 1998")
UNWRAP = ("weighted least-squares unwrapping by preconditioned conjugate gradient, DCT preconditioner",
          "Ghiglia and Romero, J. Opt. Soc. Am. A 11(1) 1994; Ghiglia and Pritt, Two-Dimensional Phase Unwrapping, 1998")
INDICES = ("RVI 8σHV/(σHH+σVV+2σHV) quad and 4σx/(σco+σx) dual; RFDI (σco−σx)/(σco+σx); CSI (σHH−σVV)/(σHH+σVV)",
           "Kim and van Zyl, IEEE TGRS 47(8) 2009; Mitchard et al., Biogeosciences 9 2012; Pope et al., RSE 48 1994")
OCEAN_OBJECTS = ("two-parameter CFAR over a background ring, then connected components measured by their minimum "
                 "rotated rectangle", "Crisp, The State-of-the-Art in Ship Detection in SAR Imagery, DSTO-RR-0272, 2004")
DARK_AREAS = ("adaptive threshold below the local mean, then connected components",
              "Solberg et al., IEEE TGRS 37(4) 1999")
WINDS = ("wind direction from the image's streaks by the local gradient method and wind speed by inverting CMOD5.N",
         "Koch, IEEE TGRS 42(4) 2004; Hersbach, CMOD5.N, ECMWF Tech. Memo. 554, 2008")
GTC = ("range-Doppler terrain correction onto a WGS 84 grid, bilinear resampling",
       "Small and Schubert, Guide to ASAR Geocoding, ESA RSL-ASAR-GC-AD, 2008")
FLATTENING = ("area-based radiometric terrain flattening: DEM facets projected into radar geometry",
              "Small, IEEE TGRS 49(8) 2011")
COREGISTRATION = ("geometric coregistration through orbits and a DEM, bilinear resampling of the secondary",
                  "Sansosti et al., IEEE TGRS 44(8) 2006")
RTC = ("the RTC chain of grapy's own SAR tools: thermal noise, beta nought, deburst, multilook, Small (2011) flattening,"
       " Refined Lee, range-Doppler terrain correction", "ADR-237")
CALIBRATION_FUNCTION = ("ApplyRadiometricCalibration's calibration on every band", "ESA-EOPG-CSCOP-TN-0002")


def _tool(name):
    return f"arcpy.ia.{name}"


def _say(text):
    AddMessage(text)


def _write(out, bands, meta, sar=None):
    path = _sar.write(out, bands, meta, sar.transform if sar is not None and not sar.radar else None,
                      sar.srid if sar is not None and not sar.radar else 0)
    return path


def _write_blocks(out, names, dtype, shape, meta, blocks, sar=None):
    """A dataset written block by block (D-328); a map-geometry input keeps its grid."""
    keep = sar is not None and not sar.radar
    return _sar.write_blocks(out, names, dtype, shape, meta, blocks, sar.transform if keep else None,
                             sar.srid if keep else 0)


def _keep_pols(meta, names):
    if meta.get("pols"):
        meta["pols"] = {k: v for k, v in meta["pols"].items() if k in names}
    return meta


# ---- calibration and thermal noise (ESA-EOPG-CSCOP-TN-0002, MPC-0392)


_CALIBRATIONS = {"BETA_NOUGHT": "beta0", "SIGMA_NOUGHT": "sigma0", "GAMMA_NOUGHT": "gamma0"}


def _calibration_lut(sar, pol, key, tool, lines=None):
    """The calibration LUT at every pixel of the given original ``lines`` (all of the dataset's when none are
    given); each row depends only on its own line, so a block of rows is answered exactly as the whole would be."""
    cal = (sar.meta.get("pols") or {}).get(pol, {}).get("calibration")
    if not cal:
        raise ExecuteError(f"{tool}: band {pol} carries no calibration look-up table; only Sentinel-1 products, "
                           "which ship theirs, are calibrated.")
    return _sar.lut(cal, key, sar.meta["lines"] if lines is None else lines, sar.meta["pixels"])


def ApplyRadiometricCalibration(in_radar_data, out_radar_data, polarization_bands=None, calibration_type="BETA_NOUGHT"):
    """Calibrates Sentinel-1 data with the product's own look-up table, as ESA defines it: a detected value becomes
    |DN|² / A² and a complex one DN / A, A being the sigma nought, beta nought or gamma LUT interpolated bilinearly
    (ESA, *Radiometric Calibration of S-1 Level-1 Products*, ESA-EOPG-CSCOP-TN-0002). Complex in, complex out, as
    documented; amplitude or intensity in, intensity out. Products of IPF 2.34 or earlier are refused, as documented.
    Data whose thermal noise was removed is calibrated by the same LUT."""
    tool = _tool("ApplyRadiometricCalibration")
    kind = word(calibration_type, "BETA_NOUGHT")
    if kind not in _CALIBRATIONS:
        raise ExecuteError(f"{tool}: calibration_type '{calibration_type}' is not one of {', '.join(_CALIBRATIONS)}.")
    sar = _sar.read(in_radar_data, tool)
    if sar.meta.get("calibration", "none") != "none":
        raise ExecuteError(f"{tool}: the radar data is already calibrated ({sar.meta['calibration']}).")
    version = sar.meta.get("ipf_version")
    if version is not None and version <= 2.34:
        raise ExecuteError(f"{tool}: the product was made by IPF {version}; its calibration table may be wrong, and "
                           "the tool does not calibrate IPF 2.34 or earlier, as documented.")
    _sar.needs_linear(sar, tool)
    values = sar.meta.get("values")
    if values not in ("complex", "amplitude", "intensity"):
        raise ExecuteError(f"{tool}: '{values}' data cannot be calibrated.")
    names = polarizations(sar, polarization_bands, tool)
    key = _CALIBRATIONS[kind]
    for pol in names:
        _calibration_lut(sar, pol, key, tool, [])   # every band has its table before anything is written
    lines = np.asarray(sar.meta["lines"], float)
    floor = sar.meta.get("noise_removed")

    def blocks():   # by rows (D-328): each row's LUT depends only on its line
        for r0, r1 in _sar.row_blocks(sar.shape):
            out = {}
            for pol in names:
                a = _calibration_lut(sar, pol, key, tool, lines[r0:r1])
                band = sar.bands.window(pol, r0, r1)
                if values == "complex":
                    out[pol] = (band / a).astype(np.complex64)
                elif values == "amplitude":
                    out[pol] = (band.astype(np.float64) ** 2 / a ** 2).astype(np.float32)
                else:
                    if floor:   # the noise floor, calibrated as Pro 3.7 calibrates it (NOISE_FLOOR)
                        band = np.where(band == np.float32(NOISE_FLOOR), np.float32(NOISE_FLOOR * FLOOR_CALIBRATED),
                                        band)
                    out[pol] = (band / a ** 2).astype(np.float32)
            yield r0, 0, out
    meta = _keep_pols(sar.copy_meta(calibration=key, values="complex" if values == "complex" else "intensity"), names)
    return Result(_write_blocks(out_radar_data, names, "complex64" if values == "complex" else "float32", sar.shape,
                                meta, blocks(), sar))


NOISE_FLOOR = 1e-5   # what Pro 3.7 writes where the noise exceeds the power (RemoveThermalNoise)
#: Pro 3.7 calibrates a floored pixel as 2**19 x 1e-5 / A² (measured 2026-10-31 on a real IW GRD: every floored pixel of
#: six windows, 36 of them, to seven digits). Why is not published; grapy answers as Pro does.
FLOOR_CALIBRATED = 2 ** 19


def RemoveThermalNoise(in_radar_data, out_radar_data, polarization_bands=None):
    """Subtracts the product's thermal noise power: |DN|² − η, η the range noise LUT times, from IPF 2.9 on, the
    azimuth noise LUT, both interpolated as ESA's note prescribes (*Thermal Denoising of Products Generated by the S-1
    IPF*, MPC-0392). On calibrated data the noise is calibrated by the same LUT first. The output is intensity.
    A power that comes out at or below zero is set to 1e-5, as ArcGIS Pro 3.7 writes it (measured 2026-10-31 on
    S1D_IW_GRDH_1SDV_20261031T054608, ADR-237 condition 3: every such pixel of six 512 x 512 windows); grapy wrote 0,
    which a later conversion to decibels turns into minus infinity."""
    tool = _tool("RemoveThermalNoise")
    sar = _sar.read(in_radar_data, tool)
    if sar.meta.get("noise_removed"):
        raise ExecuteError(f"{tool}: thermal noise has already been removed from the radar data.")
    _sar.needs_linear(sar, tool)
    values = sar.meta.get("values")
    if values not in ("complex", "amplitude", "intensity"):
        raise ExecuteError(f"{tool}: '{values}' data has no thermal noise to remove.")
    names = polarizations(sar, polarization_bands, tool)
    calibrated = sar.meta.get("calibration", "none")
    tables = {}
    for pol in names:
        vectors = (sar.meta.get("pols") or {}).get(pol, {}).get("noise")
        if not vectors or not vectors.get("range"):
            raise ExecuteError(f"{tool}: band {pol} carries no noise look-up table; only Sentinel-1 products, which "
                               "ship theirs, are corrected.")
        if calibrated != "none":
            _calibration_lut(sar, pol, calibrated, tool, [])
        tables[pol] = vectors
    lines, pixels = np.asarray(sar.meta["lines"], float), np.asarray(sar.meta["pixels"], float)

    def blocks():   # by rows (D-328): the noise of a row depends only on its line
        for r0, r1 in _sar.row_blocks(sar.shape):
            out = {}
            for pol in names:
                eta = _sar.noise(tables[pol], lines[r0:r1], pixels)
                if calibrated != "none":
                    eta = eta / _calibration_lut(sar, pol, calibrated, tool, lines[r0:r1]) ** 2
                band = sar.bands.window(pol, r0, r1)
                power = (np.abs(band).astype(np.float64) ** 2 if values in ("complex", "amplitude")
                         else band.astype(np.float64))
                out[pol] = np.maximum(power - eta, NOISE_FLOOR).astype(np.float32)
            yield r0, 0, out
    meta = _keep_pols(sar.copy_meta(noise_removed=True, values="intensity"), names)
    return Result(_write_blocks(out_radar_data, names, "float32", sar.shape, meta, blocks(), sar))


# ---- deburst


def Deburst(in_radar_data, out_radar_data, polarization_bands=None):
    """EQUIVALENT: bursts joined at the middle of each overlap (ESA's product specification gives the burst layout —
    the bursts stacked in the image, their azimuth times, valid lines and samples — but not where one burst gives way to
    the next). Each output line is the burst line nearest its azimuth time; samples outside a line's valid range are
    NoData. The output's azimuth time runs evenly at the line interval."""
    tool = _tool("Deburst")
    sar = _sar.read(in_radar_data, tool)
    bursts = sar.meta.get("bursts")
    if not bursts:
        raise ExecuteError(f"{tool}: the radar data has no bursts; Deburst is for Sentinel-1 IW and EW SLC data.")
    names = polarizations(sar, polarization_bands, tool)
    lpb = bursts["lines_per_burst"]
    dt = sar.meta["line_interval"]
    spans = []
    for k, b in enumerate(bursts["list"]):
        valid = [j for j, f in enumerate(b["first_valid"]) if f >= 0]
        if not valid:
            continue
        spans.append((k, b["time"], valid[0], valid[-1]))
    if not spans:
        raise ExecuteError(f"{tool}: no burst has a valid line.")
    start = spans[0][1] + spans[0][2] * dt
    end = spans[-1][1] + spans[-1][3] * dt
    count = int(round((end - start) / dt)) + 1
    times = start + np.arange(count) * dt
    # the burst each time falls in: before the middle of an overlap the earlier burst, after it the later
    owner = np.zeros(count, int)
    for i, (k, t0, first, last) in enumerate(spans):
        lo = t0 + first * dt
        if i > 0:
            pk, pt0, pfirst, plast = spans[i - 1]
            lo = (pt0 + plast * dt + lo) / 2
        owner[times >= lo - dt / 2] = i
    source, keep_first, keep_last = np.zeros(count, int), np.zeros(count, int), np.zeros(count, int)
    for n in range(count):
        k, t0, first, last = spans[owner[n]]
        j = int(np.clip(round((times[n] - t0) / dt), first, last))
        source[n] = k * lpb + j
        keep_first[n] = bursts["list"][k]["first_valid"][j]
        keep_last[n] = bursts["list"][k]["last_valid"][j]
    columns = np.arange(sar.shape[1])
    blank = (columns[None, :] < keep_first[:, None]) | (columns[None, :] > keep_last[:, None])
    out = {}
    for pol in names:
        band = sar.bands[pol][source]
        nodata = (np.nan + 1j * np.nan) if np.iscomplexobj(band) else np.nan
        out[pol] = np.where(blank, nodata, band).astype(band.dtype)
    meta = _keep_pols(sar.copy_meta(first_line_time=float(times[0]), line_interval=dt,
                                    lines=[int(sar.meta["lines"][s]) for s in source]), names)
    meta.pop("bursts", None)
    return Result(_write(out_radar_data, out, meta, sar))


# ---- multilook


def square_looks(meta, along_range=None):
    """Range and azimuth looks for an approximately square ground cell: the smaller ground spacing is averaged up to
    the larger, rounded (INFERRED: to the nearest whole look, at least 1)."""
    rg, az = _sar.ground_spacing(meta)
    if along_range is not None:
        return int(along_range), max(1, int(round(along_range * rg / az)))
    if az >= rg:
        return max(1, int(round(az / rg))), 1
    return 1, max(1, int(round(rg / az)))


def _block_mean(array, rl, al):
    n, m = array.shape
    n2, m2 = n // al, m // rl
    a = array[: n2 * al, : m2 * rl].reshape(n2, al, m2, rl)
    with np.errstate(invalid="ignore"):
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", RuntimeWarning)
            return np.nanmean(a, axis=(1, 3))


def Multilook(in_radar_data, out_radar_data, polarization_bands=None, range_looks=None, azimuth_looks=None):
    """Averages intensity over blocks of range × azimuth looks. Complex and amplitude data are detected (|z|², A²)
    first, so the output is intensity — the incoherent multilook. A look left empty takes the fewest looks that make
    the cell about square, as documented (INFERRED: rounded to the nearest whole look). INFERRED: a partial block at
    the far edges is dropped, and NoData cells are left out of a block's mean."""
    tool = _tool("Multilook")
    sar = _sar.read(in_radar_data, tool)
    if sar.meta.get("bursts"):
        raise ExecuteError(f"{tool}: the radar data is still in bursts; run Deburst first.")
    _sar.needs_linear(sar, tool)
    values = sar.meta.get("values")
    if values not in ("complex", "amplitude", "intensity"):
        raise ExecuteError(f"{tool}: '{values}' data is not multilooked.")
    names = polarizations(sar, polarization_bands, tool)
    rl, al = square_looks(sar.meta)
    if _is_given(range_looks):
        rl = int(range_looks)
    if _is_given(azimuth_looks):
        al = int(azimuth_looks)
    if rl < 1 or al < 1:
        raise ExecuteError(f"{tool}: looks must be whole numbers of at least 1.")
    n, m = sar.shape
    rows, columns = n // al, m // rl

    def blocks():   # whole looks of rows at a time (D-328); a block's mean is the same in any block
        for r0, r1 in _sar.row_blocks(sar.shape, al):
            if (r1 - r0) // al == 0:
                continue
            out = {}
            for pol in names:
                band = sar.bands.window(pol, r0, r1)
                power = (np.abs(band).astype(np.float64) ** 2 if values in ("complex", "amplitude")
                         else band.astype(np.float64))
                out[pol] = _block_mean(power, rl, al).astype(np.float32)
            yield r0 // al, 0, out
    lines = np.asarray(sar.meta["lines"], float)[: rows * al].reshape(rows, al).mean(1)
    pixels = np.asarray(sar.meta["pixels"], float)[: columns * rl].reshape(columns, rl).mean(1)
    looks = sar.meta.get("looks", [1, 1])
    meta = sar.copy_meta(values="intensity", lines=lines.tolist(), pixels=pixels.tolist(),
                         looks=[looks[0] * rl, looks[1] * al])
    if sar.radar and meta.get("first_line_time") is not None:
        meta["first_line_time"] = sar.meta["first_line_time"] + (al - 1) / 2 * sar.meta["line_interval"]
        meta["line_interval"] = sar.meta["line_interval"] * al
    _say(f"Multilook: {rl} range looks and {al} azimuth looks.")
    return Result(_write_blocks(out_radar_data, names, "float32", (rows, columns), _keep_pols(meta, names), blocks(),
                                sar))


# ---- units


def ConvertSARUnits(in_radar_data, out_radar_data, conversion_type="LINEAR_TO_DB"):
    """Converts every band by the documented formulas: dB is 10·log10 of intensity (INFERRED: 20·log10 of amplitude,
    the same power in decibels); amplitude and intensity by squaring and square root; complex to intensity by
    re² + im²; unwrapped phase to displacement by −λφ/4π, λ = c / radar frequency (INFERRED: the sign, positive
    towards the sensor). A value of 0 or less has no decibel value and becomes NoData."""
    tool = _tool("ConvertSARUnits")
    kind = word(conversion_type, "LINEAR_TO_DB")
    sar = _sar.read(in_radar_data, tool)
    values, scale = sar.meta.get("values"), sar.meta.get("scale", "linear")
    meta = sar.copy_meta()

    def need(ok, what):
        if not ok:
            raise ExecuteError(f"{tool}: {kind} needs {what}; the radar data is {scale} {values}.")

    if kind == "LINEAR_TO_DB":
        need(scale == "linear" and values in ("amplitude", "intensity", "coherence"), "linear amplitude or intensity")
        meta["scale"] = "db"

        def convert(b):
            with np.errstate(divide="ignore", invalid="ignore"):
                return np.where(b > 0, (20 if values == "amplitude" else 10) * np.log10(np.where(b > 0, b, 1)), np.nan)
    elif kind == "DB_TO_LINEAR":
        need(scale == "db", "data in decibels")
        meta["scale"] = "linear"

        def convert(b):
            return 10 ** (b / (20 if values == "amplitude" else 10))
    elif kind == "AMPLITUDE_TO_INTENSITY":
        need(scale == "linear" and values == "amplitude", "linear amplitude")
        meta["values"] = "intensity"

        def convert(b):
            return b ** 2
    elif kind == "INTENSITY_TO_AMPLITUDE":
        need(scale == "linear" and values == "intensity", "linear intensity")
        meta["values"] = "amplitude"

        def convert(b):
            return np.sqrt(np.maximum(b, 0))
    elif kind == "COMPLEX_TO_INTENSITY":
        need(values == "complex", "complex data")
        meta["values"] = "intensity"

        def convert(b):
            return b.real ** 2 + b.imag ** 2
    elif kind == "PHASE_TO_DISPLACEMENT":
        need(values == "unwrapped_phase", "an unwrapped interferogram phase")
        wavelength = C / sar.meta["radar_frequency"]
        meta["values"] = "displacement"

        def convert(b):
            return -wavelength * b / (4 * math.pi)
    else:
        raise ExecuteError(f"{tool}: conversion_type '{conversion_type}' is not a documented conversion.")
    names = sar.names

    def blocks():   # cell by cell, so by rows (D-328)
        for r0, r1 in _sar.row_blocks(sar.shape):
            out = {}
            for pol in names:
                band = sar.bands.window(pol, r0, r1)
                b = band.astype(np.complex128 if np.iscomplexobj(band) else np.float64)
                out[pol] = np.asarray(convert(b)).astype(np.float32)
            yield r0, 0, out
    return Result(_write_blocks(out_radar_data, names, "float32", sar.shape, meta, blocks(), sar))


# ---- speckle filters


_SIZES = {"3X3": 3, "5X5": 5, "7X7": 7, "9X9": 9, "11X11": 11}


def _local(values, size):
    from scipy import ndimage
    mean = ndimage.uniform_filter(values, size, mode="reflect")
    square = ndimage.uniform_filter(values * values, size, mode="reflect")
    return mean, np.maximum(square - mean * mean, 0.0)


def lee(z, size, model="MULTIPLICATIVE_NOISE", noise_variance=0.25, add_mean=0.0, mult_mean=1.0, looks=1):
    """Lee's (1980) minimum mean square error filter for z = x·u + w: u of mean ū and variance 1/L (the speckle of L
    looks), w of mean w̄ and variance σw². x̄ = (z̄ − w̄)/ū, var x = (var z − σw² − x̄²σu²)/(σu² + ū²), and
    x̂ = x̄ + k(z − ūx̄ − w̄) with k = ū var x / var z."""
    mean, var = _local(z, size)
    mu_u, var_u, mu_w, var_w = 1.0, 0.0, 0.0, 0.0
    if model in ("MULTIPLICATIVE_NOISE", "ADDITIVE_AND_MULTIPLICATIVE_NOISE"):
        mu_u, var_u = float(mult_mean), 1.0 / float(looks)
    if model in ("ADDITIVE_NOISE", "ADDITIVE_AND_MULTIPLICATIVE_NOISE"):
        mu_w, var_w = float(add_mean), float(noise_variance)
    xbar = (mean - mu_w) / mu_u
    var_x = np.maximum((var - var_w - xbar ** 2 * var_u) / (var_u + mu_u ** 2), 0.0)
    with np.errstate(divide="ignore", invalid="ignore"):
        k = np.where(var > 0, mu_u * var_x / var, 0.0)
    return xbar + k * (z - mu_u * xbar - mu_w)


def kuan(z, size, looks):
    """Kuan et al. (1985): W = (1 − Cu²/Ci²)/(1 + Cu²), x̂ = z̄ + W(z − z̄), Cu² = 1/L."""
    mean, var = _local(z, size)
    cu2 = 1.0 / looks
    with np.errstate(divide="ignore", invalid="ignore"):
        ci2 = np.where(mean > 0, var / mean ** 2, 0.0)
        w = np.where(ci2 > 0, (1 - cu2 / ci2) / (1 + cu2), 0.0)
    return mean + np.clip(w, 0, 1) * (z - mean)


def enhanced_lee(z, size, looks, damp):
    """Lopes, Touzi and Nezry (1990): the mean where Ci ≤ Cu, the pixel where Ci ≥ Cmax = √2·Cu, and between them
    W = exp(−K(Ci − Cu)/(Cmax − Ci)), x̂ = z̄W + z(1 − W)."""
    mean, var = _local(z, size)
    cu = 1.0 / math.sqrt(looks)
    cmax = math.sqrt(2) * cu
    with np.errstate(divide="ignore", invalid="ignore"):
        ci = np.where(mean > 0, np.sqrt(var) / mean, 0.0)
        w = np.exp(-damp * (ci - cu) / (cmax - ci))
    middle = mean * w + z * (1 - w)
    return np.where(ci <= cu, mean, np.where(ci >= cmax, z, middle))


def gamma_map(z, size, looks):
    """Lopes, Touzi and Nezry (1990): the mean where Ci ≤ Cu, the pixel where Ci ≥ Cmax = √2·Cu, and between them the
    MAP estimate x̂ = (b·z̄ + √(z̄²b² + 4αLz̄z))/2α, α = (1 + Cu²)/(Ci² − Cu²), b = α − L − 1."""
    mean, var = _local(z, size)
    cu = 1.0 / math.sqrt(looks)
    cmax = math.sqrt(2) * cu
    with np.errstate(divide="ignore", invalid="ignore"):
        ci = np.where(mean > 0, np.sqrt(var) / mean, 0.0)
        alpha = (1 + cu ** 2) / (ci ** 2 - cu ** 2)
        b = alpha - looks - 1
        d = mean ** 2 * b ** 2 + 4 * alpha * looks * mean * z
        middle = (b * mean + np.sqrt(np.maximum(d, 0))) / (2 * alpha)
    return np.where(ci <= cu, mean, np.where(ci >= cmax, z, middle))


def frost(z, size, damp):
    """Frost et al. (1982): weights exp(−K·Ci²·|d|) over the window, d the distance to the centre in cells and Ci² the
    local variance over the squared local mean."""
    mean, var = _local(z, size)
    with np.errstate(divide="ignore", invalid="ignore"):
        ci2 = np.where(mean > 0, var / mean ** 2, 0.0)
    half = size // 2
    padded = np.pad(z, half, mode="reflect")
    total, weights = np.zeros_like(z), np.zeros_like(z)
    n, m = z.shape
    for dr in range(-half, half + 1):
        for dc in range(-half, half + 1):
            w = np.exp(-damp * ci2 * math.hypot(dr, dc))
            total += w * padded[half + dr: half + dr + n, half + dc: half + dc + m]
            weights += w
    return total / weights


def _refined_masks():
    """Lee's eight edge-aligned 7×7 windows: halves for the vertical and horizontal edges, triangles (diagonal
    included) for the diagonal ones, each of 28 cells."""
    r, c = np.mgrid[0:7, 0:7]
    return {"left": c <= 3, "right": c >= 3, "top": r <= 3, "bottom": r >= 3,
            "topleft": r + c <= 6, "bottomright": r + c >= 6, "topright": c >= r, "bottomleft": c <= r}


def refined_lee(z, looks):
    """Lee's refined filter (1981): the 7×7 window's nine 3×3 means find the edge direction of greatest gradient,
    the half of the window on the centre's side of that edge is chosen, and the MMSE filter runs on its statistics
    with Cu² = 1/L."""
    from scipy import ndimage
    m3 = ndimage.uniform_filter(z, 3, mode="reflect")
    pad = np.pad(m3, 2, mode="reflect")
    n, m = z.shape

    def at(i, j):  # the 3×3 mean centred (2i, 2j) cells from each pixel, i, j in −1..1
        return pad[2 + 2 * i: 2 + 2 * i + n, 2 + 2 * j: 2 + 2 * j + m]

    g = {(i, j): at(i, j) for i in (-1, 0, 1) for j in (-1, 0, 1)}
    left = g[-1, -1] + g[0, -1] + g[1, -1]
    right = g[-1, 1] + g[0, 1] + g[1, 1]
    top = g[-1, -1] + g[-1, 0] + g[-1, 1]
    bottom = g[1, -1] + g[1, 0] + g[1, 1]
    tl = g[-1, 0] + g[-1, -1] + g[0, -1]
    br = g[0, 1] + g[1, 1] + g[1, 0]
    tr = g[-1, 0] + g[-1, 1] + g[0, 1]
    bl = g[0, -1] + g[1, -1] + g[1, 0]
    gradients = np.stack([np.abs(left - right), np.abs(top - bottom), np.abs(tl - br), np.abs(tr - bl)])
    direction = np.argmax(gradients, axis=0)
    centre = g[0, 0]
    sides = [("left", "right", left, right), ("top", "bottom", top, bottom), ("topleft", "bottomright", tl, br),
             ("topright", "bottomleft", tr, bl)]
    masks = _refined_masks()
    stats = {}
    for name, mask in masks.items():
        k = mask.astype(float) / mask.sum()
        mean = ndimage.correlate(z, k, mode="reflect")
        square = ndimage.correlate(z * z, k, mode="reflect")
        stats[name] = (mean, np.maximum(square - mean * mean, 0.0))
    mean = np.zeros_like(z)
    var = np.zeros_like(z)
    for d, (a, b, sa, sb) in enumerate(sides):
        here = direction == d
        first = np.abs(centre - sa / 3) <= np.abs(centre - sb / 3)
        for chosen, sel in ((a, here & first), (b, here & ~first)):
            mean[sel] = stats[chosen][0][sel]
            var[sel] = stats[chosen][1][sel]
    cu2 = 1.0 / looks
    var_x = np.maximum((var - mean ** 2 * cu2) / (1 + cu2), 0.0)
    with np.errstate(divide="ignore", invalid="ignore"):
        k = np.where(var > 0, var_x / var, 0.0)
    return mean + k * (z - mean)


def Despeckle(in_radar_data, out_radar_data, polarization_bands=None, filter_type="REFINED_LEE", filter_size="3x3",
              noise_model="MULTIPLICATIVE_NOISE", noise_variance=0.25, add_noise_mean=0, mult_noise_mean=1,
              number_of_looks=1, damp_factor=1):
    """EQUIVALENT: the published filters the page names, each by its paper (see DESPECKLE). The filters run on
    intensity; complex data is detected first (INFERRED). NoData cells stay NoData and are filled with the window's
    mean while their neighbours are filtered (INFERRED). REFINED_LEE uses its own 7×7 window, as the page says
    filter_size does not apply to it."""
    tool = _tool("Despeckle")
    kind = word(filter_type, "REFINED_LEE")
    if kind not in ("LEE", "ENHANCED_LEE", "REFINED_LEE", "FROST", "KUAN", "GAMMA_MAP"):
        raise ExecuteError(f"{tool}: filter_type '{filter_type}' is not a documented filter.")
    size = _SIZES.get(word(filter_size, "3X3").replace(" ", ""))
    if size is None:
        raise ExecuteError(f"{tool}: filter_size '{filter_size}' is not one of 3x3, 5x5, 7x7, 9x9, 11x11.")
    model = word(noise_model, "MULTIPLICATIVE_NOISE")
    if model not in ("MULTIPLICATIVE_NOISE", "ADDITIVE_NOISE", "ADDITIVE_AND_MULTIPLICATIVE_NOISE"):
        raise ExecuteError(f"{tool}: noise_model '{noise_model}' is not a documented noise model.")
    looks = float(number_of_looks if _is_given(number_of_looks) else 1)
    damp = float(damp_factor if _is_given(damp_factor) else 1)
    if looks <= 0:
        raise ExecuteError(f"{tool}: number_of_looks must be positive.")
    sar = _sar.read(in_radar_data, tool)
    _sar.needs_linear(sar, tool)
    names = polarizations(sar, polarization_bands, tool)
    n = sar.shape[0]

    def blocks():   # rows with a halo of DESPECKLE_HALO on each side, which every filter's reach fits in (D-328)
        for r0, r1 in _sar.row_blocks(sar.shape):
            h0, h1 = max(0, r0 - DESPECKLE_HALO), min(n, r1 + DESPECKLE_HALO)
            yield r0, 0, {pol: _despeckle(sar.bands.window(pol, h0, h1))[r0 - h0: r1 - h0] for pol in names}

    def _despeckle(band):
        z = _sar.intensity(band).astype(np.float64)
        blank = np.isnan(z)
        if blank.any():
            from scipy import ndimage
            filled = np.where(blank, 0.0, z)
            weight = ndimage.uniform_filter((~blank).astype(float), 7)
            mean = ndimage.uniform_filter(filled, 7) / np.where(weight > 0, weight, 1)
            z = np.where(blank, mean, z)
        if kind == "LEE":
            r = lee(z, size, model, float(noise_variance if _is_given(noise_variance) else 0.25),
                    float(add_noise_mean or 0), float(mult_noise_mean if _is_given(mult_noise_mean) else 1),
                    looks)
        elif kind == "REFINED_LEE":
            r = refined_lee(z, looks)
        elif kind == "ENHANCED_LEE":
            r = enhanced_lee(z, size, looks, damp)
        elif kind == "KUAN":
            r = kuan(z, size, looks)
        elif kind == "GAMMA_MAP":
            r = gamma_map(z, size, looks)
        else:
            r = frost(z, size, damp)
        return np.where(blank, np.nan, r).astype(np.float32)
    values = sar.meta.get("values")
    meta = sar.copy_meta(values="intensity" if values == "complex" else values)
    return Result(_write_blocks(out_radar_data, names, "float32", sar.shape, _keep_pols(meta, names), blocks(), sar))


#: Rows each Despeckle block reads beyond its own on either side: the NoData fill's 7 x 7 window (3), then Refined
#: Lee's 3 x 3 means sampled 2 cells off and its 7 x 7 masks (3 + 2 + 3), or the largest documented window (5) —
#: at most 11, held at 16. Inside it every filter answers as on the whole band, to the rounding of scipy's running sums.
DESPECKLE_HALO = 16


# ---- interferometry


def _pair(reference, secondary, tool, polarization_bands):
    ref = _sar.read(reference, tool)
    sec = _sar.read(secondary, tool)
    pol = polarizations(ref, polarization_bands, tool)
    if ref.meta.get("values") != "complex" or sec.meta.get("values") != "complex":
        raise ExecuteError(f"{tool}: both inputs must be complex radar data.")
    for p in pol:
        if p not in sec.bands:
            raise ExecuteError(f"{tool}: the secondary radar data has no band {p}.")
        if sec.bands[p].shape != ref.bands[p].shape:
            raise ExecuteError(f"{tool}: the inputs are not coregistered — their grids differ "
                               f"({ref.bands[p].shape} and {sec.bands[p].shape}); run ApplyCoregistration first.")
    return ref, sec, pol


def ComputeCoherence(in_reference_radar_data, in_secondary_radar_data, out_radar_data, polarization_bands=None,
                     range_window_size=10, azimuth_window_size=None):
    """EQUIVALENT: the boxcar sample coherence over a window of range × azimuth cells (see COHERENCE). The azimuth
    window defaults to the fewest cells that make the window about square on the ground, as documented — 3 for a
    range window of 10 on Sentinel-1 IW SLC."""
    from scipy import ndimage
    tool = _tool("ComputeCoherence")
    ref, sec, pols = _pair(in_reference_radar_data, in_secondary_radar_data, tool, polarization_bands)
    rw = int(range_window_size if _is_given(range_window_size) else 10)
    aw = int(azimuth_window_size) if _is_given(azimuth_window_size) else square_looks(ref.meta, rw)[1]
    out = {}
    for p in pols:
        s1 = np.nan_to_num(ref.bands[p].astype(np.complex128))
        s2 = np.nan_to_num(sec.bands[p].astype(np.complex128))
        cross = s1 * np.conj(s2)
        num = ndimage.uniform_filter(cross.real, (aw, rw)) + 1j * ndimage.uniform_filter(cross.imag, (aw, rw))
        p1 = ndimage.uniform_filter(np.abs(s1) ** 2, (aw, rw))
        p2 = ndimage.uniform_filter(np.abs(s2) ** 2, (aw, rw))
        with np.errstate(divide="ignore", invalid="ignore"):
            out[p] = np.clip(np.where(p1 * p2 > 0, np.abs(num) / np.sqrt(p1 * p2), np.nan), 0, 1).astype(np.float32)
    meta = ref.copy_meta(values="coherence")
    _say(f"ComputeCoherence: a window of {rw} range × {aw} azimuth cells.")
    return Result(_write(out_radar_data, out, _keep_pols(meta, pols), ref))


def _lattice(shape, step=16):
    n, m = shape
    rs = np.unique(np.r_[np.arange(0, n, step), n - 1]).astype(float)
    cs = np.unique(np.r_[np.arange(0, m, step), m - 1]).astype(float)
    return rs, cs


def _expand(rs, cs, values, shape):
    """A field computed on a lattice, interpolated to every cell: cubically where the lattice has four points or more
    each way, bilinearly otherwise."""
    from scipy.interpolate import RegularGridInterpolator
    n, m = shape
    if len(rs) < 2 or len(cs) < 2:
        return np.full(shape, float(np.mean(values)))
    R, Cc = np.meshgrid(np.arange(n, dtype=float), np.arange(m, dtype=float), indexing="ij")
    method = "cubic" if len(rs) >= 4 and len(cs) >= 4 else "linear"
    return RegularGridInterpolator((rs, cs), values, method=method)(np.stack([R.ravel(), Cc.ravel()], -1)).reshape(n, m)


def _reference_phase(ref, sec, dem, shape):
    """The phase 4π(R2 − R1)/λ of each reference cell's ground point — on the ellipsoid without a DEM — between the
    reference sensor and the secondary's, on a lattice and interpolated."""
    rs, cs = _lattice(shape, 8)
    rr, cc = np.meshgrid(rs, cs, indexing="ij")
    h = _sar.heights_for(ref.meta, rr.ravel(), cc.ravel(), dem) if dem is not None else np.zeros(rr.size)
    p = _sar.geocode(ref.meta, rr.ravel(), cc.ravel(), h)
    _, r1, _ = _sar.zero_doppler(ref.meta, p)
    _, r2, _ = _sar.zero_doppler(sec.meta, p)
    wavelength = C / ref.meta["radar_frequency"]
    phase = (-4 * math.pi / wavelength * (r1 - r2)).reshape(rr.shape)
    return _expand(rs, cs, phase, shape)


def GenerateInterferogram(in_reference_radar_data, in_secondary_radar_data, out_radar_data, polarization_bands=None,
                          remove_topographic_phase="NONE", in_dem_raster=None, geoid="GEOID",
                          out_flattened_interferogram=None):
    """EQUIVALENT: s1·s2* with the reference geometry's phase removed (see INTERFEROGRAM). A complex sample is taken
    as exp(−i4πR/λ), so the phase removed is −4π(R1 − R2)/λ: R1 from the reference sensor and R2 from the secondary's
    to each reference cell's ground point, on the ellipsoid (flat earth) for NONE and on the DEM for TOPO (INFERRED:
    the sign convention, and the reference phase computed every 8 cells and interpolated cubically). The secondary keeps its
    own orbit, as ApplyCoregistration writes it."""
    tool = _tool("GenerateInterferogram")
    topo = word(remove_topographic_phase, "NONE") in ("TOPO", "TRUE")
    ref, sec, pols = _pair(in_reference_radar_data, in_secondary_radar_data, tool, polarization_bands)
    if len(pols) != 1:
        raise ExecuteError(f"{tool}: one polarization band is interfered at a time.")
    if not ref.radar:
        raise ExecuteError(f"{tool}: the inputs must be in radar geometry.")
    dem = None
    if topo:
        if not _is_given(in_dem_raster):
            raise ExecuteError(f"{tool}: remove_topographic_phase TOPO needs in_dem_raster.")
        dem = _sar.Dem(in_dem_raster, "GenerateInterferogram", geoid)
    p = pols[0]
    shape = ref.bands[p].shape
    raw = ref.bands[p].astype(np.complex128) * np.conj(sec.bands[p].astype(np.complex128))
    meta = ref.copy_meta(values="complex", interferogram=True, secondary_orbit=sec.meta.get("orbit"))
    flat = _reference_phase(ref, sec, None, shape)
    if topo:
        topo_phase = _reference_phase(ref, sec, dem, shape)
        out = raw * np.exp(-1j * topo_phase)
        if _is_given(out_flattened_interferogram):
            _write(out_flattened_interferogram, {p: (raw * np.exp(-1j * flat)).astype(np.complex64)},
                   _keep_pols(dict(meta, topographic_phase_removed=False), [p]), ref)
        meta["topographic_phase_removed"] = True
    else:
        out = raw * np.exp(-1j * flat)
        if _is_given(out_flattened_interferogram):
            _write(out_flattened_interferogram, {p: out.astype(np.complex64)}, _keep_pols(dict(meta), [p]), ref)
        meta["topographic_phase_removed"] = False
    path = _write(out_radar_data, {p: out.astype(np.complex64)}, _keep_pols(meta, [p]), ref)
    outputs = [path] + ([_sar.out_path(out_flattened_interferogram)] if _is_given(out_flattened_interferogram) else [])
    return Result(*outputs)


def goldstein(z, alpha, patch):
    """Goldstein and Werner (1998): each patch's spectrum Z is weighted by |S(Z)|^α, S a 3×3 smoothing, normalised
    to its peak; patches overlap by half and are blended with a triangular taper."""
    from scipy import ndimage
    n, m = z.shape
    step = max(1, patch // 2)
    pad_n = (int(math.ceil(max(n - patch, 0) / step)) * step + patch) - n
    pad_m = (int(math.ceil(max(m - patch, 0) / step)) * step + patch) - m
    zp = np.pad(z, ((0, pad_n), (0, pad_m)), mode="reflect" if min(n, m) > 1 else "edge")
    out = np.zeros(zp.shape, complex)
    weight = np.zeros(zp.shape)
    taper1 = 1 - np.abs(np.arange(patch) - (patch - 1) / 2) / (patch / 2)
    taper = np.outer(taper1, taper1) + 1e-6
    for r in range(0, zp.shape[0] - patch + 1, step):
        for c in range(0, zp.shape[1] - patch + 1, step):
            block = zp[r: r + patch, c: c + patch]
            spectrum = np.fft.fft2(block)
            smooth = ndimage.uniform_filter(np.abs(spectrum), 3, mode="wrap")
            peak = smooth.max()
            h = (smooth / peak) ** alpha if peak > 0 else np.ones_like(smooth)
            out[r: r + patch, c: c + patch] += np.fft.ifft2(spectrum * h) * taper
            weight[r: r + patch, c: c + patch] += taper
    return (out / weight)[:n, :m]


def effective_coherence(phase, size):
    """|mean of exp(iφ)| over a window: the phase-derived coherence the pages call effective coherence."""
    from scipy import ndimage
    e = np.exp(1j * np.nan_to_num(phase))
    mean = ndimage.uniform_filter(e.real, size) + 1j * ndimage.uniform_filter(e.imag, size)
    return np.abs(mean)


def ApplyComplexDataFilter(in_radar_data, out_radar_data, polarization_bands=None, filter_type="GOLDSTEIN_PHASE_FILTER",
                           alpha=0.5, inner_window_size=8, outer_window_size=64):
    """EQUIVALENT: Goldstein-Werner filtering (see GOLDSTEIN) with patches of both window sizes; each cell takes the
    inner patch's result where the effective coherence over the outer window is high and the outer patch's where it
    is low, as the parameters' explanations say (the page's usage note says the reverse; ADR-237 §5.3). INFERRED:
    the coherence threshold is 0.3, the Unwrap Phase default. The amplitude is kept; only the phase is filtered."""
    tool = _tool("ApplyComplexDataFilter")
    if word(filter_type, "GOLDSTEIN_PHASE_FILTER") != "GOLDSTEIN_PHASE_FILTER":
        raise ExecuteError(f"{tool}: filter_type '{filter_type}' is not GOLDSTEIN_PHASE_FILTER.")
    a = float(alpha if _is_given(alpha) else 0.5)
    if not 0 <= a <= 1:
        raise ExecuteError(f"{tool}: alpha must be from 0 to 1.")
    inner = int(inner_window_size if _is_given(inner_window_size) else 8)
    outer = int(outer_window_size if _is_given(outer_window_size) else 64)
    if inner < 2 or outer < inner:
        raise ExecuteError(f"{tool}: the windows must be at least 2 cells, the outer no smaller than the inner.")
    sar = _sar.read(in_radar_data, tool)
    if sar.meta.get("values") != "complex":
        raise ExecuteError(f"{tool}: the input must be complex data.")
    names = polarizations(sar, polarization_bands, tool)
    out = {}
    for pol in names:
        z = sar.bands[pol].astype(np.complex128)
        blank = np.isnan(z)
        zz = np.where(blank, 0, z)
        unit = np.where(np.abs(zz) > 0, zz / np.where(np.abs(zz) > 0, np.abs(zz), 1), 0)
        if a == 0:
            filtered = unit
        else:
            small = goldstein(unit, a, inner)
            large = goldstein(unit, a, outer)
            coherent = effective_coherence(np.angle(unit), outer) >= 0.3
            filtered = np.where(coherent, small, large)
        result = np.abs(zz) * np.exp(1j * np.angle(filtered))
        out[pol] = np.where(blank, np.nan + 1j * np.nan, result).astype(np.complex64)
    return Result(_write(out_radar_data, out, _keep_pols(sar.copy_meta(), names), sar))


def _wrap(x):
    return (x + math.pi) % (2 * math.pi) - math.pi


def _dct_poisson(rho):
    """Solves the Neumann Poisson equation ∇²φ = ρ by the discrete cosine transform (Ghiglia and Romero 1994)."""
    from scipy.fft import dctn, idctn
    n, m = rho.shape
    d = dctn(rho, norm="ortho")
    i = np.arange(n)[:, None]
    j = np.arange(m)[None, :]
    denom = 2 * (np.cos(math.pi * i / n) + np.cos(math.pi * j / m) - 2)
    denom[0, 0] = 1.0
    phi = d / denom
    phi[0, 0] = 0.0
    return idctn(phi, norm="ortho")


def unwrap(phase, weights, iterations=200, tolerance=1e-8):
    """Weighted least-squares phase unwrapping by preconditioned conjugate gradient (Ghiglia and Romero 1994,
    algorithm PCG): the wrapped differences are the data, the weights each edge's product of its cells' weights."""
    w = weights.astype(float)
    dx = _wrap(np.diff(phase, axis=1))
    dy = _wrap(np.diff(phase, axis=0))
    wx = np.minimum(w[:, 1:], w[:, :-1]) ** 2
    wy = np.minimum(w[1:, :], w[:-1, :]) ** 2

    def divergence(gx, gy):
        out = np.zeros_like(phase)
        out[:, :-1] += gx
        out[:, 1:] -= gx
        out[:-1, :] += gy
        out[1:, :] -= gy
        return -out

    def apply(p):
        return divergence(wx * np.diff(p, axis=1), wy * np.diff(p, axis=0))

    rhs = divergence(wx * dx, wy * dy)
    x = np.zeros_like(phase)
    r = rhs - apply(x)
    z = _dct_poisson(r)
    p = z.copy()
    rz = np.sum(r * z)
    norm0 = np.sqrt(np.sum(rhs * rhs)) or 1.0
    for _ in range(iterations):
        ap = apply(p)
        denom = np.sum(p * ap)
        if denom == 0:
            break
        step = rz / denom
        x += step * p
        r -= step * ap
        if np.sqrt(np.sum(r * r)) / norm0 < tolerance:
            break
        z = _dct_poisson(r)
        rz_new = np.sum(r * z)
        p = z + (rz_new / rz) * p
        rz = rz_new
    return x


def UnwrapPhase(in_radar_data, out_radar_data, polarization_bands=None, unwrap_phase_method="LEAST_SQUARES_PCG",
                effective_coherence_threshold=0.3):
    """EQUIVALENT: weighted least-squares unwrapping by PCG (see UNWRAP). The effective coherence is |mean exp(iφ)|
    over 5 × 5 cells (INFERRED: the window); cells below the threshold get weight 0 and are NoData in the output.
    INFERRED: the solution, which least squares defines only up to a constant, is shifted to agree with the wrapped
    phase modulo 2π, then by the whole number of cycles that brings its mean closest to the wrapped phase's."""
    tool = _tool("UnwrapPhase")
    if word(unwrap_phase_method, "LEAST_SQUARES_PCG") != "LEAST_SQUARES_PCG":
        raise ExecuteError(f"{tool}: unwrap_phase_method '{unwrap_phase_method}' is not LEAST_SQUARES_PCG.")
    threshold = float(effective_coherence_threshold if _is_given(effective_coherence_threshold) else 0.3)
    sar = _sar.read(in_radar_data, tool)
    if sar.meta.get("values") != "complex":
        raise ExecuteError(f"{tool}: the input must be a complex wrapped interferogram.")
    names = polarizations(sar, polarization_bands, tool)
    out = {}
    for pol in names:
        z = sar.bands[pol].astype(np.complex128)
        blank = np.isnan(z) | (np.abs(np.nan_to_num(z)) == 0)
        phase = np.angle(np.nan_to_num(z))
        good = (effective_coherence(phase, 5) >= threshold) & ~blank
        u = unwrap(phase, good.astype(float))
        if good.any():
            u = u + np.angle(np.sum(np.exp(1j * (phase[good] - u[good]))))
            k = np.round(np.mean(u[good] - phase[good]) / (2 * math.pi))
            u = u - 2 * math.pi * k
        out[pol] = np.where(good, u, np.nan).astype(np.float32)
    meta = sar.copy_meta(values="unwrapped_phase")
    return Result(_write(out_radar_data, out, _keep_pols(meta, names), sar))


# ---- indices and composites


def ComputeSARIndices(in_radar_data, out_raster, index="RVI", polarization_bands="HH_HV"):
    """EQUIVALENT (see INDICES): RVI, RFDI and CSI by the formulas of their papers, the pages giving only their
    meaning. The polarization pair applies, as documented, to quad-polarized data and RVI or RFDI; dual-polarized
    data uses the pair it has."""
    tool = _tool("ComputeSARIndices")
    kind = word(index, "RVI")
    if kind not in ("RVI", "RFDI", "CSI"):
        raise ExecuteError(f"{tool}: index '{index}' is not RVI, RFDI or CSI.")
    sar = _sar.read(in_radar_data, tool)
    _sar.needs_linear(sar, tool)
    have = {n.upper(): _sar.intensity(b).astype(np.float64) for n, b in sar.bands.items()}
    quad = all(p in have for p in ("HH", "HV", "VH", "VV"))
    pair = word(polarization_bands, "HH_HV")
    if kind == "CSI":
        if not ("HH" in have and "VV" in have):
            raise ExecuteError(f"{tool}: CSI needs HH and VV bands.")
        r = (have["HH"] - have["VV"]) / (have["HH"] + have["VV"])
    else:
        if quad and kind == "RVI" and pair == "HH_HV_VH_VV":
            hv = (have["HV"] + have["VH"]) / 2
            r = 8 * hv / (have["HH"] + have["VV"] + 2 * hv)
        else:
            if quad:
                if pair not in ("HH_HV", "VV_VH"):
                    raise ExecuteError(f"{tool}: polarization_bands '{polarization_bands}' is not HH_HV or VV_VH for {kind}.")
                co, x = ("HH", "HV") if pair == "HH_HV" else ("VV", "VH")
            elif "HH" in have and "HV" in have:
                co, x = "HH", "HV"
            elif "VV" in have and "VH" in have:
                co, x = "VV", "VH"
            else:
                raise ExecuteError(f"{tool}: {kind} needs HH and HV, or VV and VH, bands.")
            if kind == "RVI":
                r = 4 * have[x] / (have[co] + have[x])
            else:
                r = (have[co] - have[x]) / (have[co] + have[x])
    meta = sar.copy_meta(values="index")
    meta.pop("pols", None)
    return Result(_write(out_raster, {kind: r.astype(np.float32)}, meta, sar))


def GenerateMultitemporalCoherence(in_radar_red, in_radar_green, in_radar_blue, out_radar_data, polarization_bands=None):
    """Stacks three terrain-corrected datasets into a red, green and blue composite, one polarization band of each
    (the first by default). INFERRED: the inputs must share one grid, as terrain correction of the same scene gives,
    and the bands are named Red, Green and Blue."""
    tool = _tool("GenerateMultitemporalCoherence")
    inputs = [_sar.read(v, tool) for v in (in_radar_red, in_radar_green, in_radar_blue)]
    first = inputs[0]
    if first.radar:
        raise ExecuteError(f"{tool}: the inputs must be terrain corrected; run ApplyGeometricTerrainCorrection first.")
    bands = {}
    for name, sar in zip(("Red", "Green", "Blue"), inputs):
        if sar.radar:
            raise ExecuteError(f"{tool}: the inputs must be terrain corrected; run ApplyGeometricTerrainCorrection first.")
        if sar.shape != first.shape or sar.srid != first.srid or not np.allclose(tuple(sar.transform)[:6],
                                                                                    tuple(first.transform)[:6]):
            raise ExecuteError(f"{tool}: the inputs are not on one grid.")
        pol = polarizations(sar, polarization_bands, tool)[0]
        bands[name] = _sar.intensity(sar.bands[pol]).astype(np.float32)
    meta = first.copy_meta(values="composite")
    meta.pop("pols", None)
    return Result(_write(out_radar_data, bands, meta, first))


# ---- masks, land and sea


def _features(value, tool):
    """The polygons of a feature class, in WGS 84."""
    from . import _gpkg, da
    connection, table = _gpkg.table_of(value)
    try:
        srid = _gpkg.describe(connection, table)[2]
    finally:
        connection.close()
    import shapely
    shapes = []
    with da.SearchCursor(value, ["SHAPE@"]) as rows:
        for (shape,) in rows:
            if shape is not None:
                shapes.append(shapely.force_2d(shape._shape))
    if not shapes:
        raise ExecuteError(f"{tool}: the mask features hold no shapes.")
    union = shapely.union_all(shapes)
    if srid not in (0, 4326):
        import pyproj
        to = pyproj.Transformer.from_crs(srid, 4326, always_xy=True)
        union = shapely.transform(union, lambda xy: np.column_stack(to.transform(xy[:, 0], xy[:, 1])))
    return union


def _buffer_m(shape, metres):
    """A WGS 84 shape buffered by metres, through an azimuthal equidistant projection centred on it."""
    if not metres:
        return shape
    import pyproj
    import shapely
    c = shape.centroid
    local = pyproj.CRS.from_proj4(f"+proj=aeqd +lat_0={c.y} +lon_0={c.x} +datum=WGS84 +units=m")
    there = pyproj.Transformer.from_crs(4326, local, always_xy=True)
    back = pyproj.Transformer.from_crs(local, 4326, always_xy=True)
    projected = shapely.transform(shape, lambda xy: np.column_stack(there.transform(xy[:, 0], xy[:, 1])))
    buffered = projected.buffer(metres)
    return shapely.transform(buffered, lambda xy: np.column_stack(back.transform(xy[:, 0], xy[:, 1])))


def _linear_text(value):
    """A Linear Unit parameter in metres: a number (metres) or '<number> <unit>'."""
    text = str(value).strip().split()
    number = float(text[0])
    unit = (text[1] if len(text) > 1 else "METERS").upper()
    factors = {"METERS": 1.0, "METER": 1.0, "KILOMETERS": 1000.0, "KILOMETER": 1000.0, "FEET": 0.3048,
               "FOOT": 0.3048, "MILES": 1609.344, "MILE": 1609.344, "NAUTICALMILES": 1852.0, "YARDS": 0.9144,
               "CENTIMETERS": 0.01, "DECIMETERS": 0.1, "MILLIMETERS": 0.001}
    if unit not in factors:
        raise ExecuteError(f"'{value}' is not a linear unit grapy reads.")
    return number * factors[unit]


def _areal_m2(value, default):
    if not _is_given(value):
        return default
    text = str(value).strip().split()
    number = float(text[0])
    unit = (text[1] if len(text) > 1 else "SQUAREMETERS").upper().replace("_", "")
    factors = {"SQUAREMETERS": 1.0, "SQUAREKILOMETERS": 1e6, "HECTARES": 1e4, "SQUAREFEET": 0.09290304,
               "ACRES": 4046.8564224, "SQUAREMILES": 2589988.110336}
    if unit not in factors:
        raise ExecuteError(f"'{value}' is not an areal unit grapy reads.")
    return number * factors[unit]


def _water(sar, tool, mask_features, feature_type, dem, tolerance):
    """Which cells are open water, from mask features (LAND or WATER) or, failing them, the DEM (INFERRED: water
    where the DEM is at or below 0). The land is grown by the tolerance (INFERRED: a WATER polygon is shrunk by it)."""
    import shapely
    if not _is_given(mask_features) and dem is None:
        return None
    lon, lat = _sar.cell_lonlat(sar, dem)
    if _is_given(mask_features):
        kind = word(feature_type, "")
        if kind not in ("LAND", "WATER"):
            raise ExecuteError(f"{tool}: feature_type must be LAND or WATER when mask features are given.")
        polygons = _features(mask_features, tool)
        if kind == "LAND":
            land = _buffer_m(polygons, tolerance)
            return ~shapely.contains_xy(land, lon, lat)
        water = _buffer_m(polygons, -tolerance) if tolerance else polygons
        return shapely.contains_xy(water, lon, lat)
    elevation = dem.at(lon, lat)
    water = np.nan_to_num(elevation, nan=0.0) <= 0
    if tolerance:
        from scipy import ndimage
        rg, az = _cell_metres(sar)
        grow = (max(1, int(round(tolerance / az))), max(1, int(round(tolerance / rg))))
        land = ndimage.binary_dilation(~water, structure=np.ones((2 * grow[0] + 1, 2 * grow[1] + 1), bool))
        water = ~land
    return water


def _cell_metres(sar):
    """(range or x, azimuth or y) size of one cell on the ground, in metres."""
    if sar.radar:
        return _sar.ground_spacing(sar.meta)
    t = sar.transform
    if sar.srid in (0, 4326):
        lat = t.f + t.e * sar.shape[0] / 2
        return abs(t.a) * 111320 * math.cos(math.radians(lat)), abs(t.e) * 110574
    return abs(t.a), abs(t.e)


def GenerateMaskedSARRaster(in_radar_data, out_radar_data, polarization_bands=None, in_mask_features=None,
                            mask_area="INSIDE", in_dem_raster=None, geoid="GEOID"):
    """Sets cells to NoData inside (or outside) the mask features, or — with no features — where the DEM says the
    ground is ocean (INFERRED: at or below 0). Radar-geometry data is placed on the ground by range-Doppler geocoding
    on the DEM, or on the tie points' heights where no DEM is given; terrain-corrected data by its own grid."""
    import shapely
    tool = _tool("GenerateMaskedSARRaster")
    sar = _sar.read(in_radar_data, tool)
    names = polarizations(sar, polarization_bands, tool)
    dem = _sar.Dem(in_dem_raster, "GenerateMaskedSARRaster", geoid) if _is_given(in_dem_raster) else None
    if not _is_given(in_mask_features) and dem is None:
        raise ExecuteError(f"{tool}: give mask features or a DEM.")
    lon, lat = _sar.cell_lonlat(sar, dem)
    if _is_given(in_mask_features):
        inside = shapely.contains_xy(_features(in_mask_features, tool), lon, lat)
        area = word(mask_area, "INSIDE")
        if area not in ("INSIDE", "OUTSIDE"):
            raise ExecuteError(f"{tool}: mask_area '{mask_area}' is not INSIDE or OUTSIDE.")
        blank = inside if area == "INSIDE" else ~inside
    else:
        blank = np.nan_to_num(dem.at(lon, lat), nan=0.0) <= 0
    out = {}
    for pol in names:
        band = sar.bands[pol]
        nodata = (np.nan + 1j * np.nan) if np.iscomplexobj(band) else np.nan
        out[pol] = np.where(blank, nodata, band).astype(band.dtype)
    return Result(_write(out_radar_data, out, _keep_pols(sar.copy_meta(), names), sar))


def _components(binary):
    from scipy import ndimage
    labels, count = ndimage.label(binary, structure=np.ones((3, 3), bool))
    return labels, count


def _cell_outline(sar, rows, columns, dem):
    """Each cell of a component as a lon/lat quadrilateral."""
    import shapely
    quads = []
    corners = np.array([[-0.5, -0.5], [-0.5, 0.5], [0.5, 0.5], [0.5, -0.5]])
    rr = (rows[:, None] + corners[None, :, 0]).ravel()
    cc = (columns[:, None] + corners[None, :, 1]).ravel()
    if sar.radar:
        h = _sar.heights_for(sar.meta, rr, cc, dem)
        lon, lat, _ = _sar.geodetic(_sar.geocode(sar.meta, rr, cc, h))
    else:
        t = sar.transform
        x = t.c + (cc + 0.5) * t.a + (rr + 0.5) * t.b
        y = t.f + (cc + 0.5) * t.d + (rr + 0.5) * t.e
        lon, lat = x, y
    lon, lat = lon.reshape(-1, 4), lat.reshape(-1, 4)
    for k in range(len(rows)):
        quads.append(shapely.Polygon(np.column_stack([lon[k], lat[k]])))
    return shapely.union_all(quads)


def _measure(shape, geographic):
    """Length and width in metres of a shape's minimum rotated rectangle, and that rectangle."""
    import shapely
    if geographic:
        import pyproj
        c = shape.centroid
        local = pyproj.CRS.from_proj4(f"+proj=aeqd +lat_0={c.y} +lon_0={c.x} +datum=WGS84 +units=m")
        to = pyproj.Transformer.from_crs(4326, local, always_xy=True)
        projected = shapely.transform(shape, lambda xy: np.column_stack(to.transform(xy[:, 0], xy[:, 1])))
    else:
        projected = shape
    box = shapely.minimum_rotated_rectangle(projected)
    xy = np.asarray(box.exterior.coords) if box.geom_type == "Polygon" else np.asarray(projected.envelope.exterior.coords)
    sides = sorted([float(np.hypot(*(xy[1] - xy[0]))), float(np.hypot(*(xy[2] - xy[1])))])
    return sides[1], sides[0], shapely.minimum_rotated_rectangle(shape)


def DetectBrightOceanObjects(in_radar_data, out_feature_class, out_type="BOUNDS", min_object_width="10 Meters",
                             max_object_width="100 Meters", min_object_length="50 Meters", max_object_length="500 Meters",
                             mask_features=None, feature_type=None, in_dem_raster=None, geoid="GEOID",
                             mask_tolerance="100 Meters"):
    """EQUIVALENT (see OCEAN_OBJECTS): a cell is bright where it exceeds the background ring's mean by 5 standard
    deviations (INFERRED: the factor, and a guard window of the longest object and a ring as wide again around it);
    bright cells joined by their 8 neighbours are objects, kept when their minimum rotated rectangle's length and
    width are within the limits. Objects are written in WGS 84 for radar-geometry data (geocoded on the DEM or the
    tie points) and in the data's reference otherwise; BOUNDS writes the minimum rotated rectangle and PERIMETER the
    outline of the object's cells. INFERRED fields: Length, Width (metres), PixelCount, MaxValue, MeanValue."""
    from scipy import ndimage
    from . import _gpkg
    tool = _tool("DetectBrightOceanObjects")
    kind = word(out_type, "BOUNDS")
    if kind not in ("BOUNDS", "PERIMETER"):
        raise ExecuteError(f"{tool}: out_type '{out_type}' is not BOUNDS or PERIMETER.")
    limits = [_linear_text(v) for v in (min_object_width or "10 Meters", max_object_width or "100 Meters",
                                         min_object_length or "50 Meters", max_object_length or "500 Meters")]
    if min(limits) <= 0:
        raise ExecuteError(f"{tool}: object widths and lengths must be positive.")
    tolerance = _linear_text(mask_tolerance) if _is_given(mask_tolerance) else 100.0
    if tolerance < 0:
        raise ExecuteError(f"{tool}: mask_tolerance cannot be negative.")
    sar = _sar.read(in_radar_data, tool)
    _sar.needs_linear(sar, tool)
    dem = _sar.Dem(in_dem_raster, "DetectBrightOceanObjects", geoid) if _is_given(in_dem_raster) else None
    z = _sar.intensity(sar.bands[sar.names[0]]).astype(np.float64)
    blank = np.isnan(z)
    water = _water(sar, tool, mask_features, feature_type, dem, tolerance)
    if water is not None:
        blank |= ~water
    rg, az = _cell_metres(sar)
    guard = (max(1, int(round(limits[3] / az))) | 1, max(1, int(round(limits[3] / rg))) | 1)
    outer = (guard[0] * 3, guard[1] * 3)
    filled = np.where(blank, 0.0, z)
    valid = (~blank).astype(float)

    def boxsum(a, size):
        return ndimage.uniform_filter(a, size, mode="constant") * size[0] * size[1]

    count = boxsum(valid, outer) - boxsum(valid, guard)
    total = boxsum(filled, outer) - boxsum(filled, guard)
    square = boxsum(filled ** 2, outer) - boxsum(filled ** 2, guard)
    with np.errstate(divide="ignore", invalid="ignore"):
        mean = total / count
        std = np.sqrt(np.maximum(square / count - mean ** 2, 0))
    bright = (z > mean + 5 * std) & ~blank & (count > 0)
    labels, n = _components(bright)
    rows_out = []
    geographic = sar.radar or sar.srid in (0, 4326)
    for k in range(1, n + 1):
        rr, cc = np.nonzero(labels == k)
        outline = _cell_outline(sar, rr.astype(float), cc.astype(float), dem)
        length, width, box = _measure(outline, geographic)
        if not (limits[0] <= width <= limits[1] and limits[2] <= length <= limits[3]):
            continue
        shape = box if kind == "BOUNDS" else outline
        values = z[rr, cc]
        rows_out.append((length, width, int(len(rr)), float(values.max()), float(values.mean()), shape))
    columns = [_gpkg.Column("Length", "Double"), _gpkg.Column("Width", "Double"), _gpkg.Column("PixelCount", "Integer"),
               _gpkg.Column("MaxValue", "Double"), _gpkg.Column("MeanValue", "Double")]
    from ._tintools import _write as write_features
    srid = 4326 if sar.radar else (sar.srid or 4326)
    path = write_features(out_feature_class, "Polygon", srid, columns, False, rows_out)
    _say(f"DetectBrightOceanObjects: {len(rows_out)} objects of {n} bright components.")
    return Result(path)


def DetectDarkOceanAreas(in_radar_data, out_raster, min_area="10000 SquareMeters", mask_features=None,
                         feature_type=None, in_dem_raster=None, geoid="GEOID", mask_tolerance="100 Meters"):
    """EQUIVALENT (see DARK_AREAS): after a 3 × 3 mean, a cell is dark where it is below half the mean of the 101 ×
    101 cells around it (INFERRED: −3 dB and the window); dark cells joined by their 8 neighbours are kept when they
    cover at least min_area on the ground. The output is 1 for a dark cell and 0 otherwise, NoData off the water,
    in the input's geometry."""
    from scipy import ndimage
    tool = _tool("DetectDarkOceanAreas")
    area = _areal_m2(min_area, 10000.0)
    if area < 0:
        raise ExecuteError(f"{tool}: min_area cannot be negative.")
    tolerance = _linear_text(mask_tolerance) if _is_given(mask_tolerance) else 100.0
    if tolerance < 0:
        raise ExecuteError(f"{tool}: mask_tolerance cannot be negative.")
    sar = _sar.read(in_radar_data, tool)
    _sar.needs_linear(sar, tool)
    dem = _sar.Dem(in_dem_raster, "DetectDarkOceanAreas", geoid) if _is_given(in_dem_raster) else None
    z = _sar.intensity(sar.bands[sar.names[0]]).astype(np.float64)
    blank = np.isnan(z)
    water = _water(sar, tool, mask_features, feature_type, dem, tolerance)
    if water is not None:
        blank |= ~water
    filled = np.where(blank, 0.0, z)
    valid = (~blank).astype(float)
    smooth = ndimage.uniform_filter(filled, 3) / np.maximum(ndimage.uniform_filter(valid, 3), 1e-12)
    background = ndimage.uniform_filter(filled, 101) / np.maximum(ndimage.uniform_filter(valid, 101), 1e-12)
    dark = (smooth < 0.5 * background) & ~blank
    labels, n = _components(dark)
    rg, az = _cell_metres(sar)
    sizes = ndimage.sum(dark, labels, index=np.arange(1, n + 1)) * rg * az if n else np.array([])
    keep = np.zeros(n + 1, bool)
    keep[1:] = sizes >= area
    result = keep[labels].astype(np.float32)
    result[blank] = np.nan
    meta = sar.copy_meta(values="mask")
    meta.pop("pols", None)
    return Result(_write(out_raster, {"Dark": result}, meta, sar))


# ---- ocean winds: CMOD5.N (Hersbach 2008, ECMWF Tech. Memo. 554, Table 1; form of Hersbach et al. 2007, JGR 112)


CMOD5 = (-0.688, -0.793, 0.338, -0.173, 0.0, 0.004, 0.111, 0.0162, 6.34, 2.57, -2.18, 0.4, -0.6, 0.045, 0.007, 0.33,
         0.012, 22.0, 1.95, 3.0, 8.39, -3.44, 1.36, 5.35, 1.99, 0.29, 3.80, 1.53)
CMOD5N = (-0.6878, -0.7957, 0.3380, -0.1728, 0.0, 0.0040, 0.1103, 0.0159, 6.7329, 2.7713, -2.2885, 0.4971, -0.7250,
          0.0450, 0.0066, 0.3222, 0.0120, 22.7, 2.0813, 3.0, 8.3659, -3.3428, 1.3236, 6.2437, 2.3893, 0.3249, 4.1590,
          1.6930)


def cmod_terms(v, theta, c=CMOD5N):
    """B0, B1 and B2 of the CMOD5 family at wind speed v (m/s) and incidence theta (degrees)."""
    v = np.asarray(v, float)
    x = (np.asarray(theta, float) - 40.0) / 25.0
    xx = x * x
    a0 = c[0] + c[1] * x + c[2] * xx + c[3] * x * xx
    a1 = c[4] + c[5] * x
    a2 = c[6] + c[7] * x
    gam = c[8] + c[9] * x + c[10] * xx
    s0 = c[11] + c[12] * x
    s = a2 * v
    a3 = 1.0 / (1.0 + np.exp(-np.maximum(s, s0)))
    with np.errstate(divide="ignore", invalid="ignore"):
        low = a3 * np.power(np.maximum(s, 0) / s0, s0 * (1.0 - a3))
    a3 = np.where(s < s0, low, a3)
    b0 = np.power(a3, gam) * np.power(10.0, a0 + a1 * v)
    b1 = c[14] * v * (0.5 + x - np.tanh(4.0 * (x + c[15] + c[16] * v)))
    b1 = (c[13] * (1.0 + x) - b1) / (np.exp(0.34 * (v - c[17])) + 1.0)
    v0 = c[20] + c[21] * x + c[22] * xx
    d1 = c[23] + c[24] * x + c[25] * xx
    d2 = c[26] + c[27] * x
    v2 = v / v0 + 1.0
    y0, pn = c[18], c[19]
    a = y0 - (y0 - 1.0) / pn
    b = 1.0 / (pn * (y0 - 1.0) ** (pn - 1.0))
    v2 = np.where(v2 < y0, a + b * np.power(np.maximum(v2 - 1.0, 0), pn), v2)
    b2 = (-d1 + d2 * v2) * np.exp(-v2)
    return b0, b1, b2


def cmod5n(v, phi, theta):
    """σ0 (linear) for wind speed v (m/s), relative direction phi (degrees, 0 upwind) and incidence theta."""
    b0, b1, b2 = cmod_terms(v, theta)
    phi = np.radians(np.asarray(phi, float))
    return b0 * np.power(np.maximum(1.0 + b1 * np.cos(phi) + b2 * np.cos(2 * phi), 0), 1.6)


def invert_cmod5n(sigma0, phi, theta, low=0.2, high=50.0, steps=40):
    """The wind speed that gives σ0 at phi and theta, by bisection (σ0 grows with speed over this range)."""
    lo = np.full(np.shape(sigma0), low)
    hi = np.full(np.shape(sigma0), high)
    for _ in range(steps):
        mid = (lo + hi) / 2
        above = cmod5n(mid, phi, theta) > sigma0
        hi = np.where(above, mid, hi)
        lo = np.where(above, lo, mid)
    return (lo + hi) / 2


def _bearings(lon, lat):
    """Bearing (degrees from north) of the +column and +row directions at every cell, from the cell coordinates."""
    dlon_c = np.gradient(lon, axis=1)
    dlat_c = np.gradient(lat, axis=1)
    dlon_r = np.gradient(lon, axis=0)
    dlat_r = np.gradient(lat, axis=0)
    k = np.cos(np.radians(lat))
    col = np.degrees(np.arctan2(dlon_c * k, dlat_c))
    row = np.degrees(np.arctan2(dlon_r * k, dlat_r))
    return col, row


def ExtractOceanWinds(in_radar_data, out_raster):
    """EQUIVALENT (see WINDS). Direction: wind streaks run along the wind, so in blocks of about 3 km (INFERRED) the
    dominant gradient orientation of the log backscatter, by the structure tensor, turned 90°, is the wind's axis
    (Koch 2004); INFERRED: the 180° ambiguity, which streaks cannot resolve, is taken as the wind coming from the
    northern half. Speed: σ0 VV is inverted through CMOD5.N at each cell's incidence angle (from the tie points) and
    the angle between the wind and the radar's look (INFERRED: right-looking, the look azimuth the flight heading plus
    90°); HH is converted to VV by Thompson's polarization ratio with α = 0.6 (Thompson et al., JGR 103 1998). The
    output's two bands are U (east) and V (north), the components of the velocity the wind blows with, in the input's
    radar geometry."""
    from scipy import ndimage
    tool = _tool("ExtractOceanWinds")
    sar = _sar.read(in_radar_data, tool)
    if not sar.radar:
        raise ExecuteError(f"{tool}: the input must be in radar geometry, before terrain correction.")
    if sar.meta.get("calibration") != "sigma0":
        raise ExecuteError(f"{tool}: the input must be calibrated to sigma nought; run ApplyRadiometricCalibration.")
    _sar.needs_linear(sar, tool)
    upper = {n.upper(): n for n in sar.names}
    pol = upper.get("VV") or upper.get("HH")
    if pol is None:
        raise ExecuteError(f"{tool}: the input needs a VV or HH band.")
    sigma = _sar.intensity(sar.bands[pol]).astype(np.float64)
    n, m = sigma.shape
    t = _sar.row_time(sar.meta, np.arange(n))
    theta = _sar.incidence(sar.meta, np.repeat(t, m), np.tile(np.arange(m, dtype=float), n)).reshape(n, m)
    if pol.upper() == "HH":
        tan2 = np.tan(np.radians(theta)) ** 2
        sigma = sigma * (1 + 2 * tan2) ** 2 / (1 + 0.6 * tan2) ** 2
    lon, lat = _sar.lonlat_grid(sar.meta, sigma.shape)
    col_bearing, row_bearing = _bearings(lon, lat)
    look = row_bearing + 90.0
    rg, az = _cell_metres(sar)
    block = (max(4, min(n, int(round(3000 / az)))), max(4, min(m, int(round(3000 / rg)))))
    logs = np.log(np.where(np.nan_to_num(sigma) > 0, np.nan_to_num(sigma), np.nan))
    logs = np.where(np.isnan(logs), np.nanmean(logs), logs)
    logs = ndimage.gaussian_filter(logs, 1.0)
    gr = ndimage.sobel(logs, axis=0) / az
    gc = ndimage.sobel(logs, axis=1) / rg
    # gradient vectors on the ground, east and north, through the column and row bearings
    cb, rb = np.radians(col_bearing), np.radians(row_bearing)
    ge = gc * np.sin(cb) + gr * np.sin(rb)
    gn = gc * np.cos(cb) + gr * np.cos(rb)
    jee = ndimage.uniform_filter(ge * ge, block)
    jnn = ndimage.uniform_filter(gn * gn, block)
    jen = ndimage.uniform_filter(ge * gn, block)
    gradient = 0.5 * np.arctan2(2 * jen, jee - jnn)  # angle from east of the dominant gradient
    axis = gradient + math.pi / 2  # the streaks, from east
    bearing = (90.0 - np.degrees(axis)) % 180.0  # the axis's bearing from north, 0..180
    frm = np.where(bearing <= 90.0, bearing, bearing + 180.0) % 360.0  # INFERRED: from the northern half
    phi = frm - look
    smooth = ndimage.uniform_filter(np.nan_to_num(sigma), 3)
    speed = invert_cmod5n(np.maximum(smooth, 1e-10), phi, theta)
    to = np.radians(frm + 180.0)
    u = (speed * np.sin(to)).astype(np.float32)
    v = (speed * np.cos(to)).astype(np.float32)
    blank = np.isnan(sigma)
    u[blank] = np.nan
    v[blank] = np.nan
    meta = sar.copy_meta(values="wind")
    meta.pop("pols", None)
    return Result(_write(out_raster, {"U": u, "V": v}, meta, sar))


# ---- terrain: correction and flattening


def _footprint(meta, shape, dem):
    n, m = shape
    edge = np.r_[np.linspace(0, m - 1, 9)]
    rows = np.r_[np.zeros(9), np.full(9, n - 1.0), np.linspace(0, n - 1, 9), np.linspace(0, n - 1, 9)]
    cols = np.r_[edge, edge, np.zeros(9), np.full(9, m - 1.0)]
    h = _sar.heights_for(meta, rows, cols, dem)
    lon, lat, _ = _sar.geodetic(_sar.geocode(meta, rows, cols, h))
    return lon.min(), lat.min(), lon.max(), lat.max()


def _tie_heights(meta):
    """A function of lon, lat answering the tie points' heights, linearly between them and nearest outside —
    the approximated DEM the page describes for a run without one."""
    from scipy.interpolate import LinearNDInterpolator, NearestNDInterpolator
    g = np.asarray(meta["grid"], float)
    pts = g[:, [5, 4]]
    linear = LinearNDInterpolator(pts, g[:, 6])
    nearest = NearestNDInterpolator(pts, g[:, 6])

    def at(lon, lat):
        h = linear(lon, lat)
        out = np.isnan(h)
        if out.any():
            h[out] = nearest(np.asarray(lon)[out], np.asarray(lat)[out])
        return h
    return at


def ApplyGeometricTerrainCorrection(in_radar_data, out_radar_data, polarization_bands=None, in_dem_raster=None,
                                    geoid="GEOID"):
    """EQUIVALENT (see GTC): every cell of a WGS 84 grid over the scene's footprint is placed on the DEM — or, without
    one, on heights interpolated from the metadata's tie points, as documented — found in the radar image by
    range-Doppler back-geocoding (solved exactly on a lattice and interpolated, ``_sar.backgeocode_grid``), and
    resampled bilinearly. The grid is worked in tiles, so a full scene runs within ADR-223's memory (D-328). INFERRED: the cell is the finer ground spacing of the
    input in degrees of latitude (and the same distance in longitude at the scene's middle latitude)."""
    tool = _tool("ApplyGeometricTerrainCorrection")
    sar = _sar.read(in_radar_data, tool)
    if not sar.radar:
        raise ExecuteError(f"{tool}: the radar data is already terrain corrected.")
    names = polarizations(sar, polarization_bands, tool)
    dem = _sar.Dem(in_dem_raster, "ApplyGeometricTerrainCorrection", geoid) if _is_given(in_dem_raster) else None
    west, south, east, north = _footprint(sar.meta, sar.shape, dem)
    spacing = min(_sar.ground_spacing(sar.meta))
    mid = math.radians((south + north) / 2)
    dlat = spacing / 110574.0
    dlon = spacing / (111320.0 * math.cos(mid))
    columns = int(math.ceil((east - west) / dlon)) + 1
    rows = int(math.ceil((north - south) / dlat)) + 1
    from rasterio.transform import from_origin
    transform = from_origin(west - dlon / 2, north + dlat / 2, dlon, dlat)
    tie = _tie_heights(sar.meta)
    complex_ = any(sar.bands.complex(p) for p in names)
    n, m = sar.shape
    heights = np.asarray(sar.meta["grid"], float)[:, 6]
    low, high = float(heights.min()), float(heights.max())
    if dem is not None and np.isfinite(dem.values).any():
        low, high = min(low, float(np.nanmin(dem.values))), max(high, float(np.nanmax(dem.values)))

    def _outside(lon_axis, lat_axis):
        """Whether a whole tile falls beyond one edge of the radar image at the lowest and the highest height any
        of its cells can have: nine by nine of its cells, edges included, are back-geocoded at both; a cell's place
        moves smoothly with height and across the tile, so a tile all past one edge by two cells has nothing to
        resample, and its heights are never looked up (the bounding box of a rotated swath is a fifth empty)."""
        lo, la = np.meshgrid(np.linspace(lon_axis[0], lon_axis[-1], 9), np.linspace(lat_axis[0], lat_axis[-1], 9))
        for level in (low, high):
            r, c, _ = _sar.backgeocode(sar.meta, _sar.ecef(lo.ravel(), la.ravel(), np.full(lo.size, level)))
            if not ((r < -2.5).all() or (r > n + 1.5).all() or (c < -2.5).all() or (c > m + 1.5).all()):
                return False
        return True

    def blocks():
        # Tiles of the output grid (D-328): each tile's cells are back-geocoded, and only the window of the radar
        # image their bilinear neighbours fall in is read, so the image is never held whole.
        side = max(1, int(math.sqrt(GTC_TILE_CELLS)))
        blank = np.nan + (1j * np.nan if complex_ else 0)
        for r0 in range(0, rows, side):
            lat_axis = north - np.arange(r0, min(rows, r0 + side)) * dlat
            # a row of tiles is gathered and written at once: the GeoTIFF is in strips of whole rows
            band = {p: np.full((len(lat_axis), columns), blank, np.complex64 if complex_ else np.float32)
                    for p in names}
            for c0 in range(0, columns, side):
                lon_axis = west + np.arange(c0, min(columns, c0 + side)) * dlon
                if _outside(lon_axis, lat_axis):
                    continue
                lon, lat = np.meshgrid(lon_axis, lat_axis)
                lon, lat = lon.ravel(), lat.ravel()
                h = tie(lon, lat)
                if dem is not None:
                    found = dem.at(lon, lat)
                    h = np.where(np.isnan(found), h, found)
                r, c = _sar.backgeocode_grid(sar.meta, lon_axis, lat_axis, h)
                (w0, w1), (v0, v1) = _sar.bilinear_reach(r, n), _sar.bilinear_reach(c, m)
                for p in names:
                    window = sar.bands.window(p, w0, w1, v0, v1)
                    band[p][:, c0: c0 + len(lon_axis)] = _sar.bilinear(window, r, c, offset=(w0, v0), shape=(n, m)
                                                                       ).reshape(len(lat_axis), len(lon_axis))
            yield r0, 0, band
    meta = sar.copy_meta(geometry="map")
    return Result(_sar.write_blocks(out_radar_data, names, "complex64" if complex_ else "float32", (rows, columns),
                                    _keep_pols(meta, names), blocks(), transform, 4326))


#: Cells of the output grid ApplyGeometricTerrainCorrection back-geocodes at a time, as square tiles (D-328).
GTC_TILE_CELLS = 512 * 512


def _enu(lon, lat):
    lo, la = np.radians(lon), np.radians(lat)
    east = np.stack([-np.sin(lo), np.cos(lo), np.zeros_like(lo)], -1)
    north = np.stack([-np.sin(la) * np.cos(lo), -np.sin(la) * np.sin(lo), np.cos(la)], -1)
    up = np.stack([np.cos(la) * np.cos(lo), np.cos(la) * np.sin(lo), np.sin(la)], -1)
    return east, north, up


def ApplyRadiometricTerrainFlattening(in_radar_data, out_radar_data, in_dem_raster, geoid="GEOID",
                                      polarization_bands=None, calibration_type="GAMMA_NOUGHT", out_scattering_area=None,
                                      out_geometric_distortion=None, out_geometric_distortion_mask=None):
    """EQUIVALENT (see FLATTENING): each DEM cell is a facet, placed in the radar image by back-geocoding; an
    illuminated facet adds its area projected perpendicular to the look direction (GAMMA_NOUGHT) or its own area on
    the terrain (SIGMA_NOUGHT) to the radar cell it falls in, and the output is β0·Aβ/ΣA, Aβ being the cell's slant
    range spacing times its azimuth spacing (Small 2011). INFERRED: the DEM is resampled bilinearly to at most half
    the radar ground spacing first; a facet's area is shared among the four cells around it by bilinear weights; a facet facing away from the sensor adds
    nothing (self-shadow, without shadow cast by other terrain); a cell no facet reaches is NoData.

    Refused: ``out_geometric_distortion`` and ``out_geometric_distortion_mask`` — the page names their bands and
    classes but not how the foreshortening ratio is computed or where lengthening ends and foreshortening begins."""
    tool = _tool("ApplyRadiometricTerrainFlattening")
    for name, value in (("out_geometric_distortion", out_geometric_distortion),
                        ("out_geometric_distortion_mask", out_geometric_distortion_mask)):
        if _is_given(value):
            raise NotBuiltError(f"{tool}: {name} is not built in grapy: the page does not define the foreshortening "
                                "ratio nor the boundaries between the distortion classes.")
    kind = word(calibration_type, "GAMMA_NOUGHT")
    if kind not in ("GAMMA_NOUGHT", "SIGMA_NOUGHT"):
        raise ExecuteError(f"{tool}: calibration_type '{calibration_type}' is not GAMMA_NOUGHT or SIGMA_NOUGHT.")
    if not _is_given(in_dem_raster):
        raise ExecuteError(f"{tool}: in_dem_raster is required.")
    sar = _sar.read(in_radar_data, tool)
    if not sar.radar:
        raise ExecuteError(f"{tool}: the input must be in radar geometry.")
    if sar.meta.get("calibration") != "beta0":
        raise ExecuteError(f"{tool}: the input must be calibrated to beta nought, as documented.")
    _sar.needs_linear(sar, tool)
    names = polarizations(sar, polarization_bands, tool)
    dem = _sar.Dem(in_dem_raster, "ApplyRadiometricTerrainFlattening", geoid)
    if dem.srid != 4326:
        raise ExecuteError(f"{tool}: the input DEM must be in WGS 1984 (EPSG:4326), as documented.")
    n, m = sar.shape
    west, south, east, north = _footprint(sar.meta, sar.shape, dem)
    g = dem.grid
    spacing = min(_sar.ground_spacing(sar.meta))
    cell_m = min(g.width * 111320 * math.cos(math.radians((south + north) / 2)), g.height * 110574)
    factor = int(min(8, max(1, math.ceil(cell_m / (spacing / 2)))))
    dlon, dlat = g.width / factor, g.height / factor
    pad_lon, pad_lat = 4 * g.width, 4 * g.height
    lons = np.arange(west - pad_lon, east + pad_lon, dlon)
    lats = np.arange(north + pad_lat, south - pad_lat, -dlat)
    area = np.zeros((n, m))
    chunk = max(1, 200_000 // max(1, len(lons)))
    for r0 in range(0, len(lats), chunk):
        la = lats[max(0, r0 - 1): r0 + chunk + 1]
        lon, lat = np.meshgrid(lons, la)
        h = dem.at(lon, lat)
        dx = np.radians(dlon) * _sar.WGS84_A * np.cos(np.radians(lat))
        dy = np.radians(dlat) * _sar.WGS84_A
        dhdx = np.gradient(h, axis=1) / dx
        dhdy = -np.gradient(h, axis=0) / dy
        keep = slice(1 if r0 > 0 else 0, None)
        lon, lat, h, dx, dhdx, dhdy = (a[keep][: min(chunk, len(lats) - r0)] for a in (lon, lat, h, dx, dhdx, dhdy))
        lon, lat, h, dx, dhdx, dhdy = (a.ravel() for a in (lon, lat, h, dx, dhdx, dhdy))
        ok = ~np.isnan(h) & ~np.isnan(dhdx) & ~np.isnan(dhdy)
        if not ok.any():
            continue
        lon, lat, h, dx, dhdx, dhdy = (a[ok] for a in (lon, lat, h, dx, dhdx, dhdy))
        e, nn, u = _enu(lon, lat)
        normal = -dhdx[:, None] * e - dhdy[:, None] * nn + u
        stretch = np.linalg.norm(normal, axis=1)
        normal /= stretch[:, None]
        facet = dx * dy * stretch
        p = _sar.ecef(lon, lat, h)
        rr, cc, s = _sar.backgeocode(sar.meta, p)
        look = p - s
        look /= np.linalg.norm(look, axis=1)[:, None]
        cos_local = -np.sum(look * normal, axis=1)
        lit = cos_local > 0
        contribution = facet * (cos_local if kind == "GAMMA_NOUGHT" else 1.0)
        r0, c0 = np.floor(rr).astype(int), np.floor(cc).astype(int)
        fr, fc = rr - r0, cc - c0
        for dr, dc, share in ((0, 0, (1 - fr) * (1 - fc)), (0, 1, (1 - fr) * fc), (1, 0, fr * (1 - fc)),
                              (1, 1, fr * fc)):
            ri, ci = r0 + dr, c0 + dc
            inside = lit & (ri >= 0) & (ri < n) & (ci >= 0) & (ci < m)
            np.add.at(area, (ri[inside], ci[inside]), (contribution * share)[inside])
    # the reference area of a radar cell: slant range spacing × azimuth spacing
    t = _sar.row_time(sar.meta, np.full(m, n / 2))
    tau = _sar.range_time(sar.meta, t, np.arange(m, dtype=float))
    slant = np.abs(np.gradient(tau)) * C / 2
    a_beta = slant[None, :] * (sar.meta["azimuth_pixel_spacing"] * sar.meta.get("looks", [1, 1])[1])
    out = {}
    with np.errstate(divide="ignore", invalid="ignore"):
        ratio = np.where(area > 0, a_beta / area, np.nan)
    for pol in names:
        out[pol] = (_sar.intensity(sar.bands[pol]) * ratio).astype(np.float32)
    meta = sar.copy_meta(calibration="gamma0" if kind == "GAMMA_NOUGHT" else "sigma0", terrain_flattened=True,
                         values="intensity")
    path = _write(out_radar_data, out, _keep_pols(meta, names), sar)
    outputs = [path]
    if _is_given(out_scattering_area):
        side = sar.copy_meta(values="scattering_area")
        side.pop("pols", None)
        outputs.append(_write(out_scattering_area, {"Area": np.where(area > 0, area, np.nan).astype(np.float32)}, side, sar))
    return Result(*outputs)


def ApplyCoregistration(in_reference_radar_data, in_secondary_radar_data, out_secondary_radar_data, in_dem_raster=None,
                        geoid="GEOID", polarization_bands=None):
    """EQUIVALENT (see COREGISTRATION): every 16th cell of the reference is placed on the ground (on the DEM, or the
    tie points' heights without one), found in the secondary by back-geocoding with the secondary's own orbit, and the
    offsets between are interpolated to every cell; the secondary is resampled there bilinearly. The output is on the
    reference's grid and keeps the secondary's orbit, which GenerateInterferogram reads.

    Refused: Sentinel-1 TOPS data (IW and EW SLC) — resampling a TOPS burst in azimuth needs its Doppler phase ramp
    removed first (ESA, *Definition of the TOPS SLC deramping function*, MPC-0221) and its residual shift found by
    enhanced spectral diversity, neither of which grapy builds."""
    tool = _tool("ApplyCoregistration")
    ref = _sar.read(in_reference_radar_data, tool)
    sec = _sar.read(in_secondary_radar_data, tool)
    for s in (ref, sec):
        if s.meta.get("values") != "complex":
            raise ExecuteError(f"{tool}: both inputs must be complex radar data.")
        if not s.radar:
            raise ExecuteError(f"{tool}: both inputs must be in radar geometry.")
        if s.meta.get("product") == "SLC" and str(s.meta.get("mode", "")).upper() in ("IW", "EW"):
            raise NotBuiltError(f"{tool}: Sentinel-1 TOPS data ({s.meta.get('mode')} SLC) is not built in grapy: "
                                "its bursts need deramping and enhanced spectral diversity before resampling.")
    names = polarizations(ref, polarization_bands, tool)
    for p in names:
        if p not in sec.bands:
            raise ExecuteError(f"{tool}: the secondary radar data has no band {p}.")
    dem = _sar.Dem(in_dem_raster, "ApplyCoregistration", geoid) if _is_given(in_dem_raster) else None
    shape = ref.shape
    rs, cs = _lattice(shape)
    rr, cc = np.meshgrid(rs, cs, indexing="ij")
    h = _sar.heights_for(ref.meta, rr.ravel(), cc.ravel(), dem)
    p = _sar.geocode(ref.meta, rr.ravel(), cc.ravel(), h)
    r2, c2, _ = _sar.backgeocode(sec.meta, p)
    rows = _expand(rs, cs, r2.reshape(rr.shape), shape)
    cols = _expand(rs, cs, c2.reshape(rr.shape), shape)
    out = {pol: _sar.bilinear(sec.bands[pol].astype(np.complex128), rows, cols).astype(np.complex64) for pol in names}
    meta = ref.copy_meta(orbit=sec.meta["orbit"], orbit_source=sec.meta.get("orbit_source"), coregistered=True,
                         radar_frequency=sec.meta["radar_frequency"])
    meta.pop("pols", None)
    shift = np.nanmean(np.hypot(rows - np.arange(shape[0])[:, None], cols - np.arange(shape[1])[None, :]))
    _say(f"ApplyCoregistration: mean offset {shift:.2f} cells.")
    return Result(_write(out_secondary_radar_data, out, meta, ref))


# ---- orbits


def read_eof(path, tool):
    """State vectors of an ESA orbit file (Earth Explorer ``.EOF``: ``Data_Block/List_of_OSVs/OSV`` with UTC, X, Y, Z,
    VX, VY, VZ, as Sentinel-1 POD products are published)."""
    import xml.etree.ElementTree as ET
    root = ET.parse(path).getroot()
    vectors = []
    for osv in root.iter():
        if osv.tag.split("}")[-1] != "OSV":
            continue
        values = {child.tag.split("}")[-1]: (child.text or "").strip() for child in osv}
        try:
            vectors.append([_sar.seconds(values["UTC"]), *(float(values[k]) for k in ("X", "Y", "Z", "VX", "VY", "VZ"))])
        except (KeyError, ValueError):
            raise ExecuteError(f"{tool}: '{path}' has an orbit state vector grapy cannot read.")
    if len(vectors) < 4:
        raise ExecuteError(f"{tool}: '{path}' holds fewer than four orbit state vectors; it is not an orbit file.")
    return sorted(vectors)


def _acquisition(meta):
    if meta.get("bursts"):
        times = [b["time"] for b in meta["bursts"]["list"]]
        return min(times), max(times) + meta["bursts"]["lines_per_burst"] * meta["line_interval"]
    t0 = meta["first_line_time"]
    return t0, t0 + len(meta["lines"]) * meta["line_interval"]


def ApplyOrbitCorrection(in_radar_data, in_orbit_file, folder=None):
    """Replaces the radar data's orbit with the state vectors of an orbit file that span the acquisition (INFERRED:
    with 100 s either side). An orbit file named without a folder is looked for in ``folder``, by default the .SAFE
    folder or the dataset's own (INFERRED). A grapy SAR dataset has its metadata file rewritten; a SAFE product gets
    ``grapy-orbit.json`` in its folder, which grapy reads with the product (INFERRED: the SAFE is not modified)."""
    tool = _tool("ApplyOrbitCorrection")
    sar = _sar.read(in_radar_data, tool)
    home = sar.path if sar.path and os.path.isdir(sar.path) else os.path.dirname(sar.path or "")
    path = os.fspath(in_orbit_file)
    if not os.path.exists(path):
        path = os.path.join(os.fspath(folder) if _is_given(folder) else home, os.path.basename(path))
    if not os.path.exists(path):
        raise ExecuteError(f"{tool}: the orbit file '{in_orbit_file}' was not found.")
    vectors = read_eof(path, tool)
    first, last = _acquisition(sar.meta)
    kept = [v for v in vectors if first - 100 <= v[0] <= last + 100]
    if len(kept) < 4 or vectors[0][0] > first or vectors[-1][0] < last:
        raise ExecuteError(f"{tool}: '{path}' does not cover the acquisition ({_sar.iso(first)} to {_sar.iso(last)}).")
    source = os.path.basename(path)
    if os.path.isdir(sar.path or ""):
        target = os.path.join(sar.path, "grapy-orbit.json")
        import json
        with open(target, "w", encoding="utf-8") as f:
            json.dump({"orbit": kept, "source": source}, f)
        _say(f"ApplyOrbitCorrection: {len(kept)} state vectors from {source} written to {target}.")
        return Result(sar.path)
    meta = sar.copy_meta(orbit=kept, orbit_source=source)
    import json
    meta["format"] = _sar.FORMAT
    meta["bands"] = sar.names
    with open(sar.path + _sar.SIDE, "w", encoding="utf-8") as f:
        json.dump(meta, f)
    _say(f"ApplyOrbitCorrection: {len(kept)} state vectors from {source}.")
    return Result(sar.path)


# ---- the RTC chain


_LEVELS = ("TNR", "CALB0", "DBST", "ML", "RTFG0", "DSPK", "GTC")


def GenerateRadiometricTerrainCorrectedData(in_radar_data, out_radar_data, in_dem_raster, geoid="GEOID",
                                            polarization_bands=None, output_units="LINEAR", processing_level=None,
                                            out_folder=None, username=None, password=None,
                                            cloud_storage_connection=None):
    """EQUIVALENT (see RTC): the documented chain run through grapy's own tools — RemoveThermalNoise,
    ApplyRadiometricCalibration to beta nought, Deburst (TOPS SLC only), Multilook (SLC only; INFERRED: ground range
    products are already multilooked), ApplyRadiometricTerrainFlattening to gamma nought, Despeckle (Refined Lee) and
    ApplyGeometricTerrainCorrection — then decibels when asked. The products named in ``processing_level`` are kept
    in ``out_folder`` as ``<level>.tif``.

    Refused: ``username``, ``password`` and ``cloud_storage_connection`` — they download an orbit file from the
    Copernicus Data Space Ecosystem, and a user tool on this server has no network (ADR-223 §5.3); without them no
    orbit is downloaded and the product's own orbit is used."""
    import tempfile
    tool = _tool("GenerateRadiometricTerrainCorrectedData")
    for name, value in (("username", username), ("password", password),
                        ("cloud_storage_connection", cloud_storage_connection)):
        if _is_given(value):
            raise NotBuiltError(f"{tool}: {name} is not built in grapy: it downloads an orbit file, and a user tool on "
                                "this server has no network (ADR-223 §5.3); run ApplyOrbitCorrection with a file.")
    units = word(output_units, "LINEAR")
    if units not in ("LINEAR", "DECIBEL"):
        raise ExecuteError(f"{tool}: output_units '{output_units}' is not LINEAR or DECIBEL.")
    if not _is_given(processing_level):
        levels = []
    else:
        levels = [str(x).strip().upper() for x in (processing_level if isinstance(processing_level, (list, tuple))
                                                   else str(processing_level).split(";")) if str(x).strip()]
    for level in levels:
        if level not in _LEVELS:
            raise ExecuteError(f"{tool}: processing_level '{level}' is not one of TNR, CalB0, DBST, ML, RTFG0, DSPK, GTC.")
    if levels and not _is_given(out_folder):
        raise ExecuteError(f"{tool}: out_folder is needed to keep processing levels.")
    work = tempfile.mkdtemp(prefix="rtc_", dir=env.scratchWorkspace or None)
    keep = os.fspath(out_folder) if _is_given(out_folder) else None
    if keep:
        os.makedirs(keep, exist_ok=True)

    def step(level):
        return os.path.join(keep if keep and level in levels else work, level + ".tif")

    sar = _sar.read(in_radar_data, tool)
    names = polarizations(sar, polarization_bands, tool)
    pols = ";".join(names)
    current = RemoveThermalNoise(in_radar_data, step("TNR"), pols)[0]
    current = ApplyRadiometricCalibration(current, step("CALB0"), pols, "BETA_NOUGHT")[0]
    if sar.meta.get("bursts"):
        current = Deburst(current, step("DBST"), pols)[0]
    if sar.meta.get("product") == "SLC":
        current = Multilook(current, step("ML"), pols)[0]
    current = ApplyRadiometricTerrainFlattening(current, step("RTFG0"), in_dem_raster, geoid, pols, "GAMMA_NOUGHT")[0]
    current = Despeckle(current, step("DSPK"), pols, "REFINED_LEE")[0]
    if units == "DECIBEL":
        current = ApplyGeometricTerrainCorrection(current, step("GTC"), pols, in_dem_raster, geoid)[0]
        current = ConvertSARUnits(current, out_radar_data, "LINEAR_TO_DB")[0]
    else:
        gtc = out_radar_data if "GTC" not in levels else step("GTC")
        current = ApplyGeometricTerrainCorrection(current, gtc, pols, in_dem_raster, geoid)[0]
        if gtc != out_radar_data:
            import shutil
            target = _sar.out_path(out_radar_data)
            shutil.copyfile(current, target)
            shutil.copyfile(current + _sar.SIDE, target + _sar.SIDE)
            current = target
    import shutil
    shutil.rmtree(work, ignore_errors=True)
    return Result(_sar.out_path(out_radar_data), keep or "")


# ---- the raster function


def RadarCalibration(raster, calibration_type="BETA_NOUGHT"):
    """EQUIVALENT — arcpy.ia's Radar Calibration raster function, whose arcpy page could not be read (404 on
    2026-10-31); its raster function page names the input raster and the calibration type, Beta nought by default.
    INFERRED: the parameters are named ``raster`` and ``calibration_type``, every band is calibrated, and the result is
    a Raster saved in the scratch workspace with its SAR metadata. NONE answers the input unchanged."""
    from ._raster import Raster
    kind = word(calibration_type, "BETA_NOUGHT")
    path = _sar._path_of(raster)
    if kind == "NONE":
        return Raster(path) if not _sar.is_safe(path) else _as_raster(_sar.read(path, _tool("RadarCalibration")))
    sar = _sar.read(path, _tool("RadarCalibration"))
    import tempfile
    out = os.path.join(tempfile.mkdtemp(prefix="radarcal_", dir=env.scratchWorkspace or None), "calibrated.tif")
    ApplyRadiometricCalibration(path, out, ";".join(sar.names), kind)
    result = Raster(out)
    result.isTemporary = True
    return result


def S1RadiometricCalibration(raster, calibration_type="BETA_NOUGHT"):
    """EQUIVALENT — arcpy.ia's Sentinel-1 Radiometric Calibration raster function, as RadarCalibration computes it. Its
    raster function page (help/analysis/raster-functions/sentinel-1-radiometric-calibration.html, read 2026-10-31) names
    the input raster and the calibration type — Beta nought (the default), Sigma nought, Gamma nought or None — and
    says a product of IPF 2.34 or earlier is not calibrated, its look-up table possibly being wrong, which
    ApplyRadiometricCalibration already refuses.
    INFERRED (Q-215): the parameters are named ``raster`` and ``calibration_type``, as RadarCalibration's are, no
    arcpy page for the function being published."""
    return RadarCalibration(raster, calibration_type)


def S1ThermalNoiseRemoval(raster):
    """EQUIVALENT — arcpy.ia's Sentinel-1 Thermal Noise Removal raster function, as RemoveThermalNoise computes it on
    every polarization band. Its raster function page (help/analysis/raster-functions/sentinel-1-thermal-noise-removal
    .html, read 2026-10-31) names only the input raster. INFERRED (Q-215): the parameter is named ``raster``, and the
    result is a Raster saved in the scratch workspace with its SAR metadata, as RadarCalibration's is."""
    import tempfile
    from ._raster import Raster
    path = _sar._path_of(raster)
    out = os.path.join(tempfile.mkdtemp(prefix="s1noise_", dir=env.scratchWorkspace or None), "denoised.tif")
    RemoveThermalNoise(path, out)
    result = Raster(out)
    result.isTemporary = True
    return result


def _as_raster(sar):
    import tempfile
    from ._raster import Raster
    out = os.path.join(tempfile.mkdtemp(prefix="radar_", dir=env.scratchWorkspace or None), "radar.tif")
    _sar.write(out, sar.bands, sar.meta)
    return Raster(out)
