"""ADR-241 condition 1: the same path distance, distance accumulation and path runs under ArcGIS Pro's arcpy and under
grapy. A tool, not a test: it needs ArcGIS Pro and never runs in CI.

    <grapy python>  tools/pro-compare/adr241-pathdist-inputs.py <worktree> <inputs>
    <Pro python>    tools/pro-compare/adr241-pathdist.py pro   <inputs> <pro outputs>
    <grapy python>  tools/pro-compare/adr241-pathdist.py grapy <inputs> <grapy outputs> <worktree>
    <grapy python>  tools/pro-compare/compare-rasters.py <worktree> <pro outputs> <grapy outputs>

Every output is a GeoTIFF named after its case. Measured 2026-10-31 with Pro 3.7: see ADR-241 §11.
"""
import os
import sys
import traceback

side, src, out = sys.argv[1], sys.argv[2], sys.argv[3]
if side == "grapy":
    sys.path.insert(0, os.path.join(sys.argv[4], "src", "grapy"))
    import grapy as arcpy
else:
    import arcpy
    arcpy.CheckOutExtension("Spatial")
sa = arcpy.sa
os.makedirs(out, exist_ok=True)
arcpy.env.overwriteOutput = True
arcpy.env.workspace = out


def i(name):
    return arcpy.Raster(os.path.join(src, name + ".tif"))


def o(name):
    return os.path.join(out, name + ".tif")


cases = {
    "pd_surf_1x3": lambda: sa.PathDistance(i("src_1x3"), in_surface_raster=i("rise_1x3")),
    "pd_vflin_1x3": lambda: sa.PathDistance(i("src_1x3"), in_vertical_raster=i("rise_1x3"),
                                            vertical_factor=sa.VfLinear()),
    "pd_vflin_to_1x3": lambda: sa.PathDistance(i("src_1x3"), in_vertical_raster=i("rise_1x3"),
                                               vertical_factor=sa.VfLinear(), source_direction="TO_SOURCE"),
    "pd_vfdefault_1x3": lambda: sa.PathDistance(i("src_1x3"), in_vertical_raster=i("rise_1x3")),
    "pd_hf_1x3": lambda: sa.PathDistance(i("src_1x3_mid"), in_horizontal_raster=i("wind_east_1x3"),
                                         horizontal_factor=sa.HfLinear()),
    "pd_cost_3x3": lambda: sa.PathDistance(i("src_3x3"), i("cost_3x3")),
    "pd_diag_3x3": lambda: sa.PathDistance(i("src_3x3"), in_surface_raster=i("diag_surface_3x3")),
    "pd_diagcost_3x3": lambda: sa.PathDistance(i("src_3x3"), i("cost_3x3"), in_surface_raster=i("diag_surface_3x3")),
    "pd_vrma_bin_1x3": lambda: sa.PathDistance(i("src_1x3"), in_vertical_raster=i("rise1_1x3"),
                                               vertical_factor=sa.VfBinary(1, -5.72, 5.72)),
    "pd_vrma_lin_1x3": lambda: sa.PathDistance(i("src_1x3"), in_vertical_raster=i("rise1_1x3"),
                                               vertical_factor=sa.VfLinear(1, -90, 90, 0.1)),
    "pd_full_21": lambda: sa.PathDistance(i("src_21"), i("cost_21"), i("surface_21"), i("wind_21"), sa.HfLinear(),
                                          i("surface_21"), sa.VfLinear(), out_backlink_raster=o("pd_full_21_back")),
    "pd_vfonly_21": lambda: sa.PathDistance(i("src_21"), in_vertical_raster=i("surface_21"),
                                            vertical_factor=sa.VfSymLinear(1, -90, 90, 0.02)),
    "pd_hike_21": lambda: sa.PathDistance(i("src_21"), in_vertical_raster=i("surface_21"),
                                          vertical_factor=sa.VfHikingTime()),
    "pd_hfonly_21": lambda: sa.PathDistance(i("src_21"), in_horizontal_raster=i("wind_21"),
                                            horizontal_factor=sa.HfInverseLinear()),
    "da_5x5": lambda: sa.DistanceAccumulation(i("src_5x5")),
    "da_41": lambda: sa.DistanceAccumulation(i("src_41")),
    "da_vflin_1x3": lambda: sa.DistanceAccumulation(i("src_1x3"), in_vertical_raster=i("rise2_1x3"),
                                                    vertical_factor=sa.VfLinear()),
    "da_cost_21": lambda: sa.DistanceAccumulation(i("src_21"), in_cost_raster=i("cost_21")),
    "da_surf_21": lambda: sa.DistanceAccumulation(i("src_21"), in_surface_raster=i("surface_21")),
    "da_vf_21": lambda: sa.DistanceAccumulation(i("src_21"), in_vertical_raster=i("surface_21"),
                                                vertical_factor=sa.VfSymLinear(1, -90, 90, 0.02)),
    "da_hf_21": lambda: sa.DistanceAccumulation(i("src_21"), in_horizontal_raster=i("wind_21"),
                                                horizontal_factor=sa.HfInverseLinear()),
    "da_full_21": lambda: sa.DistanceAccumulation(i("src_21"), None, i("surface_21"), i("cost_21"), i("surface_21"),
                                                  sa.VfLinear(), i("wind_21"), sa.HfLinear(),
                                                  out_back_direction_raster=o("da_full_21_back")),
    "dp_cost1": lambda: sa.DistanceAccumulation(i("src_41"), in_cost_raster=i("ones_41")),
    "dp_flat": lambda: sa.DistanceAccumulation(i("src_41"), in_surface_raster=i("flat_41")),
    "dp_wall": lambda: sa.DistanceAccumulation(i("src_41"), in_barrier_data=i("wall_41")),
    "dp_wall_cost1": lambda: sa.DistanceAccumulation(i("src_41"), in_barrier_data=i("wall_41"), in_cost_raster=i("ones_41")),
    "dp_vfflat": lambda: sa.DistanceAccumulation(i("src_41"), in_vertical_raster=i("flat_41"), vertical_factor=sa.VfLinear()),
    "dp_vframp": lambda: sa.DistanceAccumulation(i("src_41"), in_vertical_raster=i("ramp_41"), vertical_factor=sa.VfLinear()),
    "dp_surframp": lambda: sa.DistanceAccumulation(i("src_41"), in_surface_raster=i("ramp_41")),
    "dp_hfnorth": lambda: sa.DistanceAccumulation(i("src_41"), in_horizontal_raster=i("north_41"), horizontal_factor=sa.HfLinear()),
    "dp_hfnorth_bin": lambda: sa.DistanceAccumulation(i("src_41"), in_horizontal_raster=i("north_41"), horizontal_factor=sa.HfForward()),
    "dp_mult": lambda: sa.DistanceAccumulation(i("src_41"), source_cost_multiplier=2),
    "dq_plain": lambda: sa.DistanceAccumulation(i("src_41"), out_back_direction_raster=o("dq_plain_back"), out_source_direction_raster=o("dq_plain_sdir")),
    "dq_init": lambda: sa.DistanceAccumulation(i("src_41"), source_initial_accumulation=5),
    "dq_max": lambda: sa.DistanceAccumulation(i("src_41"), source_maximum_accumulation=150),
    "dq_mult1": lambda: sa.DistanceAccumulation(i("src_41"), source_cost_multiplier=1),
    "dq_tosrc": lambda: sa.DistanceAccumulation(i("src_41"), source_direction="TO_SOURCE"),
    "dq_alloc": lambda: sa.DistanceAllocation(i("src_21")),
    "dq_cost1_back": lambda: sa.DistanceAccumulation(i("src_41"), in_cost_raster=i("ones_41"), out_back_direction_raster=o("dq_cost1_backdir")),
}

failed = 0
for name, run in cases.items():
    try:
        run().save(o(name))
        print("ok  ", name)
    except Exception as e:  # noqa: BLE001
        failed += 1
        print("FAIL", name, type(e).__name__, str(e).splitlines()[0] if str(e) else "")

# Least-cost paths over the full runs.
try:
    sa.CostPath(i("dst_21"), arcpy.Raster(o("pd_full_21")), arcpy.Raster(o("pd_full_21_back"))).save(o("cp_full_21"))
    print("ok   cp_full_21")
except Exception as e:  # noqa: BLE001
    print("FAIL cp_full_21", e)
try:
    sa.OptimalPathAsRaster(i("dst_21"), arcpy.Raster(o("da_full_21")),
                           arcpy.Raster(o("da_full_21_back"))).save(o("op_full_21"))
    print("ok   op_full_21")
except Exception as e:  # noqa: BLE001
    traceback.print_exc()
    print("FAIL op_full_21", e)

# The optimal path over a cost-only accumulation, where the two sides' accumulations agree.
sa.DistanceAccumulation(i("src_21"), in_cost_raster=i("cost_21"), out_back_direction_raster=o("oc_back"),
    out_source_location_raster=o("oc_loc")).save(o("oc_acc"))
sa.OptimalPathAsRaster(i("dst_21"), arcpy.Raster(o("oc_acc")), arcpy.Raster(o("oc_back"))).save(o("oc_path"))
sa.OptimalPathAsRaster(i("dst_21"), arcpy.Raster(o("oc_acc")), arcpy.Raster(o("oc_back")), path_type="EACH_CELL").save(o("oc_path_cell"))
sa.CostPath(i("dst_21"), arcpy.Raster(o("oc_acc")), arcpy.Raster(o("oc_back"))).save(o("oc_costpath_bd"))
line = os.path.join(out, "oc_line.shp" if side == "pro" else "oc_line.gpkg")
sa.OptimalPathAsLine(i("dst_21"), arcpy.Raster(o("oc_acc")), arcpy.Raster(o("oc_back")), line)
with arcpy.da.SearchCursor(line, ["SHAPE@", "DestID", "PathCost"]) as rows:
    flds = rows.fields
    for row in rows:
        g = row[0]
        pts = [(round(p.X, 3), round(p.Y, 3)) for p in g.getPart(0)]
        print(dict(zip(flds[1:], row[1:])) , "length", round(g.length, 3), "n", len(pts), pts)
