# The Pro sweep

Every name grapy builds, run with the same inputs under ArcGIS Pro 3.7's arcpy and under grapy, and the outputs
compared. A tool, not a test: one side needs ArcGIS Pro, so nothing here runs in CI. Owner decision 2026-11-08:
compare all of them, module by module.

```sh
PRO="C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe"
GRAPY=D:/temp/grapy-env/Scripts/python.exe
"$PRO"   harness.py pro   analysis <scratch>/pro/analysis
"$GRAPY" harness.py grapy analysis <scratch>/grapy/analysis
"$GRAPY" compare.py <scratch>/pro/analysis <scratch>/grapy/analysis --json <scratch>/analysis.json
```

- `fixtures.py` — the shared inputs (UTM 35N points, lines, polygons, a table, a DEM and a zone raster).
- `cases/<module>.py` — `CASES`, one `run(ap, I, O)` per arcpy name; inputs a module needs beyond the shared ones are
  built in that module with `ap`, so both sides build them the same way.
- `harness.py` — runs a module's cases on one side and dumps each output to JSON (+ `.npy` for cells).
- `compare.py` — the verdict per case: MATCH, SCHEMA, DIFF, REFUSED, GRAPY-ERROR, PRO-ERROR, BOTH-ERROR.
- `results/<module>.md` — each name's verdict and what was done about it: fixed (with the test that pins Pro's
  answer), or why the difference stands.

Not compared on this machine: `arcpy.locref` and `arcpy.topographic` (their extensions are not licensed here).
