# A comparison with ArcGIS Pro 3.7 (ADR-243 condition 2, D-337, 2026-10-31). A tool, not a test: not run in CI.
# Needs scikit-learn (its LIBSVM) and rasterio in a scratch venv, the training arrays written by extract() below with grapy,
# and Pro's maps from adr243-classify-pro.py / adr243-classify-svm-params-pro.py.
# Usage: <venv python> adr243-classify-svm-grid.py grid <image|image2|image3>   then   ... agree
"""Where Pro's SVM C and gamma lie on a cross-validated grid, and how an RBF machine at a chosen cell agrees with Pro's map."""
import sys
if sys.argv[1] == "grid":
    import numpy as np, sys
    from sklearn.svm import SVC
    from sklearn.model_selection import StratifiedKFold, cross_val_score
    name, lo_hi = sys.argv[2], sys.argv[3] if len(sys.argv)>3 else None
    d=np.load(f"{name}.npz"); x,y=d["x"],d["y"]
    if name=="image3":
        mins=np.array([11,11,11,11.]); maxs=np.array([651.1000366210938,699.2000122070312,717.7000122070312,765.7999877929688])
        z=(x-mins)/(maxs-mins)
    else: z=x/255.0
    log2c=np.arange(-5,16,1.0); log2g=np.arange(-15,6,0.5)
    res=np.zeros((len(log2c),len(log2g)))
    seeds=(0,1,2)
    for i,lc in enumerate(log2c):
        for j,lg in enumerate(log2g):
            s=0
            for seed in seeds:
                s+=cross_val_score(SVC(C=2**lc,gamma=2**lg,kernel="rbf",tol=1e-3),z,y,cv=StratifiedKFold(5,shuffle=True,random_state=seed)).mean()
            res[i,j]=s/len(seeds)
    np.save(f"cv_{name}.npy",res)
    k=np.unravel_index(np.argmax(res),res.shape); print(name,"best log2c",log2c[k[0]],"log2g",log2g[k[1]],res[k])
    order=np.argsort(-res.ravel())[:15]
    for o in order:
        a,b=np.unravel_index(o,res.shape); print("  ",log2c[a],log2g[b],round(res[a,b]*940,1))
else:
    import numpy as np, rasterio, sys
    from sklearn.svm import SVC
    d="D:/temp/procmp-c"
    def scaled(name, x):
        if name=="image3":
            mins=np.array([11,11,11,11.]); maxs=np.array([651.1000366210938,699.2000122070312,717.7000122070312,765.7999877929688]); return (x-mins)/(maxs-mins)
        return x/255.0
    pro_file={"image":"svm.tif","image2":"svm_image2.tif","image3":"svm_image3.tif"}
    for name, pairs in (("image",[(1,5),(0,5),(-1,5),(3,3)]),("image3",[(-1,5),(1,5),(0,4)]),("image2",[(11,-1.5),(5,1),(9,-1)])):
        t=np.load(f"{name}.npz"); z=scaled(name,t["x"]); y=t["y"]
        img=rasterio.open(f"{d}/{name}.tif").read().reshape(4,-1).T.astype(float); zi=scaled(name,img)
        pro=rasterio.open(f"{d}/pro_cls/{pro_file[name]}").read(1).ravel()+1
        for lc,lg in pairs:
            m=SVC(C=2**lc,gamma=2**lg).fit(z,y); p=m.predict(zi)
            print(name, lc, lg, "agree with Pro %.4f"%(p==pro).mean())

# extract(): with grapy's python -- see extract.py in the scratch folder:
# import sys, os, numpy as np
# sys.path.insert(0, "D:/temp/wt-grapy-b/src/grapy")
# from grapy import _classify as C
# d = "D:/temp/procmp-c"
# for name in ("image", "image2", "image3"):
#     data, mask, grid, extra = C._stack(os.path.join(d, name + ".tif"), None, "x")
#     labels, names = C._training(os.path.join(d, "training.gpkg") + "/training", grid, "x")
#     x, y = C._samples(data, mask, labels, 0)
#     np.savez(f"D:/temp/procmp-f/svm/{name}.npz", x=x, y=y)
#     print(name, x.shape, np.bincount(y.astype(int)), x.min(0), x.max(0))
