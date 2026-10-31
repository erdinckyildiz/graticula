# A comparison with ArcGIS Pro 3.7 (ADR-243 condition 1, D-337, 2026-10-31). A tool, not a test: not run in CI.
# Usage: <grapy python> adr243-solar-horizon.py bil nnd ...   (run capture.py-style: needs cap.pkl from a grapy run and
# Pro's dur.tif from adr243-solar-pro.py; EDGE=0.5 stops rays at the outermost cells' centres)
"""Durations from horizons traced several ways (bilinear, nearest cell, nearest at its own distance, floor, a DDA walk),
against Pro's, over the whole 60 x 60 DEM."""
import sys, pickle, numpy as np, rasterio, math
sys.path.insert(0, "D:/temp/wt-grapy-b/src/grapy")
import grapy._insolation as I
from scipy.ndimage import map_coordinates
cap = pickle.load(open("cap.pkl", "rb"))
slope, aspect = cap["slope"], cap["aspect"]
pro = rasterio.open("D:/temp/procmp-c/pro_solar/dur.tif").read(1).astype(float).ravel()
z = rasterio.open("D:/temp/procmp-c/dem.tif").read(1).astype(float)
R, C = z.shape; cell = 30.0
rows, cols = np.divmod(np.arange(R*C), C); rows=rows.astype(float); cols=cols.astype(float)
base = z.ravel()
secs = cap["sectors"]; inner = np.zeros((R,C), bool); inner[1:-1,1:-1]=True; inner=inner.ravel()
EDGE=float(__import__('os').environ.get('EDGE','0'))
def trace(mode, nd=32):
    out=np.zeros((R*C, nd)); reach=int(math.hypot(R,C))+2
    for k in range(nd):
        a=math.radians(k*360/nd); dr,dc=-math.cos(a),math.sin(a); best=np.zeros(R*C)
        if mode.startswith("dda"):
            m=max(abs(dr),abs(dc)); dr,dc=dr/m,dc/m
        for s in range(1,reach+1):
            r,c=rows+dr*s, cols+dc*s
            e=EDGE
            inside=(r>-0.5+e)&(r<R-0.5-e)&(c>-0.5+e)&(c<C-0.5-e)
            if not inside.any(): break
            if mode in ("nn","nnd","dda_nn","fl","flc","trunc"):
                if mode=="fl": ri=np.floor(r); ci=np.floor(c)
                elif mode=="flc": ri=np.floor(r); ci=np.ceil(c)
                elif mode=="trunc": ri=np.trunc(r); ci=np.trunc(c)
                else: ri=np.rint(r); ci=np.rint(c)
                ri=np.clip(ri,0,R-1).astype(int); ci=np.clip(ci,0,C-1).astype(int); h=z[ri,ci]
                dist = np.hypot(ri-rows, ci-cols)*cell if mode in ("nnd","dda_nn","fl","flc","trunc") else s*cell
                dist=np.where(dist==0, s*cell, dist)
            else:
                h=map_coordinates(z,[np.clip(r,0,R-1),np.clip(c,0,C-1)],order=1,mode="nearest")
                dist=(s-0.5)*cell if mode=="half" else (np.hypot(dr,dc)*s*cell if mode.startswith("dda") else s*cell)
            ang=np.degrees(np.arctan2(h-base, dist)); best=np.maximum(best, np.where(inside, ang, 0))
        out[:,k]=best
    return out
def report(v):
    d=(v-pro).reshape(R,C)
    idx=np.argsort(-np.abs(d[1:-1,1:-1]).ravel())[:6]
    for k in idx:
        r,c=np.unravel_index(k,(R-2,C-2)); r+=1; c+=1
        print("    worst",(r,c),"d %.2f pro %.2f"%(d[r,c],pro[r*C+c]),"slope %.1f aspect %.0f"%(slope[r*C+c],aspect[r*C+c]))
def duration(H, front=False):
    v=np.zeros(R*C)
    for zen, az, dur, band, samples, day in secs:
        s=np.array(samples,float)
        ok=np.stack([(90-s[k,0])>I.horizon_at(H,s[k,1]) for k in range(len(s))],1)
        if front: ok&=I.cos_incidence(s[None,:,0],s[None,:,1],slope[:,None],aspect[:,None])>0
        v+=dur*ok.mean(1)
    return v
import os
EDGE=float(os.environ.get("EDGE","0"))
for mode in sys.argv[1:]:
    H=trace(mode)
    for front in (False, True):
        v=duration(H, front); d=(v-pro)[inner]
        report(v) if not front else None; print(f"{mode:5s} front={front} mean|d| {abs(d).mean():.3f} max {abs(d).max():.3f} bias {d.mean():+.3f} (23,31) {v[23*60+31]:.2f} (21,18) {v[21*60+18]:.2f}", flush=True)
