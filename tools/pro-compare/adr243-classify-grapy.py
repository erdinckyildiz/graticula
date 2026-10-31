# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr243-classify-grapy.py <scratch> <worktree>
"""ADR-243 condition 2, grapy side: the same training and classification as classify_pro.py."""
import os, sys
sys.path.insert(0, sys.argv[2] + "/src/grapy")
import grapy as arcpy
from grapy.ia import *
d = sys.argv[1]; o = os.path.join(d, "grapy_cls"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
img = os.path.join(d, "image.tif"); tr = os.path.join(d, "training.gpkg") + "/training"
TrainMaximumLikelihoodClassifier(img, tr, os.path.join(o, "ml.ecd"))
ClassifyRaster(img, os.path.join(o, "ml.ecd")).save(os.path.join(o, "ml.tif"))
TrainSupportVectorMachineClassifier(img, tr, os.path.join(o, "svm.ecd"), None, 0)
ClassifyRaster(img, os.path.join(o, "svm.ecd")).save(os.path.join(o, "svm.tif"))
print("done")
