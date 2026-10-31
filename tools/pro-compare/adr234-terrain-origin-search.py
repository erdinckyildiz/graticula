"""Where Pro's window size windows start (ADR-234 condition 4, D-339): for each level of a terrain_origin-pro.py run, the
corner (modulo the window) from which per-window selection reproduces Pro's points best. Run from the work folder:
adr234-terrain-origin-search.py <tag> "4;8;16"."""
import csv, sys, numpy as np
def load(p):
    with open(p) as f: return np.array([[float(r["x"]),float(r["y"]),float(r["z"])] for r in csv.DictReader(f)])
tag=sys.argv[1]; levels=[float(v) for v in sys.argv[2].split(";")]
full=load(f"pro_origin/{tag}/0.csv")
print(tag,"extent",full[:,0].min(),full[:,1].min(),full[:,0].max(),full[:,1].max())
def match(kept,res,ox,oy):
    ix=np.floor((full[:,0]-ox)/res); iy=np.floor((full[:,1]-oy)/res)
    key=ix*100000+iy
    order=np.lexsort((full[:,2],key)); k=key[order]; first=np.r_[True,k[1:]!=k[:-1]]
    s1={(round(a,2),round(b,2)) for a,b,_ in full[order][first]}; s2={(round(a,2),round(b,2)) for a,b,_ in kept}
    return len(s1&s2), len(s1), len(s2)
for res in levels:
    kept=load(f"pro_origin/{tag}/{res:g}.csv")
    best=max(((match(kept,res,ox,oy),ox,oy) for ox in np.arange(0,res,0.1) for oy in np.arange(0,res,0.1)),key=lambda t:t[0][0]-abs(t[0][1]-t[0][2]))
    print(res,len(kept),best)
