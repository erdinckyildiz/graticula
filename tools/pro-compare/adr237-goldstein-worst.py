"""Goldstein filtering's worst case on a full-size IW interferogram (D-357, 2026-11-01): a benchmark, not a test.

ApplyComplexDataFilter runs each of its two Goldstein filters only on the patches that reach a cell taking it, so its
cost depends on the scene; the worst is a scene whose coherent and incoherent cells interleave inside every patch.
This makes a 12,260 x 21,000 complex interferogram (the D-328 synthetic swath's size) and replaces the coherence the
tool computes by one that alternates column by column, so both filters reach every patch, then times the tool.

    <grapy python> adr237-goldstein-worst.py <worktree> <work folder> make [rows]
    <grapy python> adr237-goldstein-worst.py <worktree> <work folder> run [rows]     (prints CPU seconds)
    <grapy python> adr237-goldstein-worst.py <worktree> <work folder> same <a.tif> <b.tif>   (are two outputs equal?)

Run it with OPENBLAS_NUM_THREADS=1, as the fence runs a user tool. Measured 2026-11-01 on this machine: the code
before D-357's second cut 379 s, after it 225 s, the two outputs identical to the bit (peak commit 1,213 MB, through
D:/temp/realdata-c/memrun.py)."""
import os
import sys
import time

worktree, work, step = sys.argv[1], sys.argv[2], sys.argv[3]
sys.path.insert(0, os.path.join(worktree, "src", "grapy"))
sys.path.insert(0, os.path.join(worktree, "src", "grapy", "tests"))
import numpy as np  # noqa: E402

COLS = 21000

if step == "same":
    import rasterio
    from rasterio.windows import Window
    a, b = rasterio.open(sys.argv[4]), rasterio.open(sys.argv[5])
    same = a.shape == b.shape
    for r0 in range(0, a.height, 1000):
        w = Window(0, r0, a.width, min(1000, a.height - r0))
        same = same and np.array_equal(a.read(1, window=w), b.read(1, window=w), equal_nan=True)
    print("identical" if same else "different")
    sys.exit(0)

rows = int(sys.argv[4]) if len(sys.argv) > 4 else 12260
source = os.path.join(work, f"mix{rows}.tif")
import grapy as arcpy  # noqa: E402
from grapy import _sar, _sartools  # noqa: E402

if step == "make":
    import sar_fixture as F
    os.makedirs(work, exist_ok=True)
    rng = np.random.default_rng(3)
    writer = _sar.BlockWriter(source, ["VV"], "complex64", (rows, COLS), F.meta(rows=rows, cols=COLS))
    for r0, r1 in _sar.row_blocks((rows, COLS)):
        c, r = np.arange(COLS)[None, :], np.arange(r0, r1)[:, None]
        clean = 0.05 * c + 0.03 * r
        noisy = ((c // 40) + (r // 40)) % 2 == 1
        phase = np.where(noisy, rng.uniform(-np.pi, np.pi, (r1 - r0, COLS)), clean + rng.normal(0, 0.3, (r1 - r0, COLS)))
        writer.write(r0, 0, {"VV": (np.exp(1j * phase) * rng.gamma(2, 1, (r1 - r0, COLS))).astype(np.complex64)})
    writer.close()
    print("made", source)
else:
    measured = _sartools.effective_coherence

    def alternating(phase, size):
        e = measured(phase, size)          # still computed, so the run costs what the tool's does
        return np.where((np.arange(e.shape[1]) % 2 == 0)[None, :], 1.0, 0.0) + 0 * e

    _sartools.effective_coherence = alternating
    out = os.path.join(work, f"gold_{os.path.basename(os.path.normpath(worktree))}_{rows}.tif")
    started = time.process_time()
    arcpy.ia.ApplyComplexDataFilter(source, out)
    print(f"ApplyComplexDataFilter {rows} x {COLS}, both filters on every patch: {time.process_time() - started:.1f} s "
          f"of CPU -> {out}")
