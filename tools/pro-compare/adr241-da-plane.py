# A comparison with ArcGIS Pro 3.7 (ADR-241 condition 1, D-336, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Steps: make.py (grapy python) writes src.tif, plane.tif, plane2.tif; run.py pro|grapy <out>; cmp.py compares with the
# exact surface distance; proto.py is a metric-exact first-order fast marching for comparison. All four are below.
# ==== make.py
# import numpy as np, rasterio
# from rasterio.transform import from_origin
# n=41
# prof=dict(driver="GTiff",width=n,height=n,count=1,crs="EPSG:3857",transform=from_origin(0,n,1,1))
# src=np.full((n,n),-9999,np.int32); src[20,20]=1
# with rasterio.open("src.tif","w",dtype="int32",nodata=-9999,**prof) as w: w.write(src,1)
# x=np.tile(np.arange(n,dtype=np.float32),(n,1))
# for name,z in (("plane",0.5*x),("plane2",0.5*x+0.3*np.arange(n,dtype=np.float32)[:,None])):
#     with rasterio.open(f"{name}.tif","w",dtype="float32",**prof) as w: w.write(z.astype(np.float32),1)
# ==== run.py
# import os, sys
# side, out = sys.argv[1], sys.argv[2]
# if side == "grapy":
#     sys.path.insert(0, "D:/temp/wt-grapy-b/src/grapy"); import grapy as arcpy
# else:
#     import arcpy; arcpy.CheckOutExtension("Spatial")
# sa = arcpy.sa; os.makedirs(out, exist_ok=True); arcpy.env.overwriteOutput = True
# d = "D:/temp/procmp-f/da/"
# R = lambda n: arcpy.Raster(d + n + ".tif")
# cases = {
#   "surf_plane": lambda: sa.DistanceAccumulation(R("src"), in_surface_raster=R("plane")),
#   "surf_plane2": lambda: sa.DistanceAccumulation(R("src"), in_surface_raster=R("plane2")),
#   "plain": lambda: sa.DistanceAccumulation(R("src")),
#   "vf_lin_plane": lambda: sa.DistanceAccumulation(R("src"), in_vertical_raster=R("plane"), vertical_factor=sa.VfLinear(1, -90, 90, 1.0 / 90)),
#   "vf_bin_plane": lambda: sa.DistanceAccumulation(R("src"), in_vertical_raster=R("plane"), vertical_factor=sa.VfBinary(1, -30, 30)),
# }
# for k, f in cases.items():
#     try:
#         f().save(os.path.join(out, k + ".tif")); print(k, "ok")
#     except Exception as e:
#         print(k, "ERR", str(e)[:200])
# ==== cmp.py
# import numpy as np, rasterio
# n=41; r,c=np.indices((n,n)); dx=(c-20).astype(float); dy=(20-r).astype(float)
# ana={"plain":np.hypot(dx,dy),"surf_plane":np.sqrt(dx**2+dy**2+(0.5*dx)**2),"surf_plane2":np.sqrt(dx**2+dy**2+(0.5*dx-0.3*dy)**2)}
# for k in ("plain","surf_plane","surf_plane2","vf_lin_plane","vf_bin_plane"):
#     p=rasterio.open(f"pro/{k}.tif").read(1,masked=True).astype(float); g=rasterio.open(f"grapy/{k}.tif").read(1,masked=True).astype(float)
#     line=f"{k:14s} pro-grapy max {abs(p-g).max():.4f} mean {abs(p-g).mean():.4f}"
#     if k in ana:
#         a=ana[k]; line+=f" | pro-exact max {abs(p-a).max():.4f} rel {abs(p-a).max()/a.max():.4f}  grapy-exact max {abs(g-a).max():.4f}"
#     print(line)
#     if k.startswith("vf"):
#         print("   pro row20", np.round(p[20,15:26],3).tolist()); print("   gr  row20", np.round(g[20,15:26],3).tolist())
#         print("   pro col20", np.round(p[15:26,20],3).tolist()); print("   gr  col20", np.round(g[15:26,20],3).tolist())
#         print("   pro diag", [round(float(p[20-k2,20+k2]),3) for k2 in range(6)], "gr", [round(float(g[20-k2,20+k2]),3) for k2 in range(6)])
# ==== proto.py
# import numpy as np, rasterio, heapq, math
# n=41; h=1.0
# NB=((0,1),(1,1),(1,0),(1,-1),(0,-1),(-1,-1),(-1,0),(-1,1))
# def fmm(z, causal=True):
#     T=np.full((n,n),np.inf); done=np.zeros((n,n),bool); T[20,20]=0; heap=[(0,20,20)]
#     def est(r,k):
#         best=np.inf
#         for dr,dk in NB:
#             nr,nk=r+dr,k+dk
#             if 0<=nr<n and 0<=nk<n and done[nr,nk]:
#                 run=h*math.hypot(dr,dk); rise=z[r,k]-z[nr,nk]
#                 best=min(best,T[nr,nk]+math.sqrt(run*run+rise*rise))
#         for dk in (-1,1):
#             for dr in (-1,1):
#                 ar,ak,br,bk=r,k+dk,r+dr,k
#                 if not(0<=ak<n and 0<=br<n and done[ar,ak] and done[br,bk]): continue
#                 ta,tb=T[ar,ak],T[br,bk]
#                 # gradient of z in (east,north) from the two neighbours
#                 gx=-dk*(z[r,k]-z[ar,ak])/h; gy=dr*(z[r,k]-z[br,bk])/h
#                 M=np.array([[1+gx*gx,gx*gy],[gx*gy,1+gy*gy]]); A=np.linalg.inv(M)
#                 sx=-dk; sy=dr   # dT/dx = sx*(T-ta)/h (east) ; dT/dy(north) = sy*(T-tb)/h
#                 # A11 sx^2 (T-ta)^2 + 2 A12 sx sy (T-ta)(T-tb) + A22 (T-tb)^2 = h^2
#                 a11,a12,a22=A[0,0],A[0,1]*sx*sy,A[1,1]
#                 qa=a11+2*a12+a22; qb=-2*(a11*ta+a12*(ta+tb)+a22*tb); qc=a11*ta*ta+2*a12*ta*tb+a22*tb*tb-h*h
#                 disc=qb*qb-4*qa*qc
#                 if disc<0: continue
#                 t=(-qb+math.sqrt(disc))/(2*qa)
#                 if t<max(ta,tb): continue
#                 if causal:
#                     gT=np.array([sx*(t-ta)/h, sy*(t-tb)/h]); v=A@gT   # characteristic direction (motion)
#                     # motion must come from inside the quadrant: v components point away from the two neighbours
#                     if not (v[0]*sx>=-1e-12 and v[1]*sy>=-1e-12): continue
#                 best=min(best,t)
#         return best
#     while heap:
#         v,r,k=heapq.heappop(heap)
#         if done[r,k] or v>T[r,k]: continue
#         done[r,k]=True
#         for dr,dk in NB:
#             nr,nk=r+dr,k+dk
#             if 0<=nr<n and 0<=nk<n and not done[nr,nk]:
#                 e=est(nr,nk)
#                 if e<T[nr,nk]: T[nr,nk]=e; heapq.heappush(heap,(e,nr,nk))
#     return T
# r,c=np.indices((n,n)); dx=(c-20).astype(float); dy=(20-r).astype(float)
# for name,a in (("surf_plane",np.sqrt(dx**2+dy**2+(0.5*dx)**2)),("surf_plane2",np.sqrt(dx**2+dy**2+(0.5*dx-0.3*dy)**2))):
#     z=rasterio.open(f"{name.replace('surf_','')}.tif").read(1).astype(float)
#     p=rasterio.open(f"pro/{name}.tif").read(1).astype(float)
#     for causal in (True,False):
#         T=fmm(z,causal)
#         print(name,"causal",causal,"proto-pro max %.4f mean %.4f | proto-exact max %.4f | pro-exact max %.4f"%(abs(T-p).max(),abs(T-p).mean(),abs(T-a).max(),abs(p-a).max()))
