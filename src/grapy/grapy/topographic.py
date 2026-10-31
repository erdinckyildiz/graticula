"""arcpy.topographic — topographic production, by their documented names and parameters (ADR-251).

The tools work on the GeoPackage grapy holds: feature classes, feature datasets (management.CreateFeatureDataset),
subtypes and defaults (ADR-228), domains, topology (ADR-225) and relationship classes. What the toolbox keeps that a
GeoPackage has no place for is held as documented tables of its own name (the six cross-reference tables) or as
grapy's own documented file (a topology definition, ADR-251 §5.8 (h)). The machinery is in the private modules
``_topo_prod`` (editing tools), ``_topo_prod_metrics``, ``_topo_prod_relief`` (MEF, magnetism, contours, spot heights,
bands), ``_topo_prod_shape`` (centrelines, narrow polygons, hydrology thinning, sheet diagrams), ``_topo_prod_schema``
(workbooks, cross-reference, shapefiles, extraction, topology files, GeoNames) and ``_topo_prod_wmm`` (the World
Magnetic Model, with NOAA's coefficient files in ``_topo_prod_data``).

Where a page describes a result without the method, the tool is an *equivalent* (ADR-224 §5.6) naming the published
method it uses; the rest are built to their pages. Twenty names are refused, each for its own reason in ``_REFUSED``.
"""
from __future__ import annotations

from . import _topo_prod as _tp
from . import _topo_prod_metrics as _metrics
from . import _topo_prod_relief as _relief
from . import _topo_prod_schema as _schema
from . import _topo_prod_shape as _shape
from ._runtime import NotBuiltError, equivalent

_MAP = "is not built in grapy: it changes an ArcGIS Pro map's layers"
_LAYOUT = "is not built in grapy: its product is an ArcGIS Pro layout"
_PORTAL = ("is not built in grapy: it talks to an ArcGIS Enterprise portal's Workflow Manager and Topographic "
           "Production services, and a grapy tool has no network and no such server (ADR-223 §5.3)")
_RULE_WORKBOOK = ("is not built in grapy: the theme-to-production mapping lives in Esri's generalization rule workbook "
                  "from the product files, whose sheet and column layout is not published")
_TRD = ("is not built in grapy: the metadata follows the MGCP/MUVD Technical Reference Documents, which are "
        "distributed to the programme's members and not published, and the page gives no field-to-element mapping")

# Each name not built is refused with a reason of its own (ADR-251); never a schedule.
_REFUSED: dict[str, str] = {
    "ApplyBuildingOffsets": f"{_MAP} — it aligns, moves and hides building marker symbols by an offset rule file whose "
                            "XML schema is not published",
    "ApplyElementValues": f"{_LAYOUT}: the numbers and grid references it writes come from a layout's Guide To Numbered "
                          "Features or Index To Streets element as Pro renders it",
    "ApplyMasksFromRules": f"{_MAP} — its whole effect is the symbol-layer masking of the map's layers",
    "ApplyVisualSpecificationToMap": f"{_MAP} — it turns Production Mapping visual specification tables, whose schema "
                                     "is not published, into Pro renderers and .stylx symbols",
    "CalculateBridgeOffsets": "is not built in grapy: each offset is the rendered width of the overpassing layer's "
                              "ArcGIS Pro symbol at the reference scale, and grapy holds no symbol to measure",
    "MakeMasksFromRules": "is not built in grapy: a mask is drawn where rendered symbol parts of given CMYK or spot "
                          "colours meet, which needs ArcGIS Pro's drawing of a map",
    "PopulateMapSheetInfo": f"{_LAYOUT}: the text it changes is the graphic text elements of a Pro layout",
    "GenerateProductLayout": f"{_LAYOUT} (PAGX, APRX, PPKX) built from Esri's product-file JSON definitions and "
                             "layout templates",
    "DownloadJobFiles": _PORTAL,
    "UploadJobFiles": _PORTAL,
    "UpdateTopographicProductionServiceDatabase": "is not built in grapy: it maintains the tables of ArcGIS Enterprise's "
                                                  "Topographic Production server object extension, whose schema is not "
                                                  "published, in an enterprise geodatabase",
    "MakeGridsAndGraticulesLayer": "is not built in grapy: every line, interval and label comes from Esri's grids and "
                                   "graticules XML definition, whose format is not published, and its product is a "
                                   "group layer of a map",
    "ExportGeneralizedData": _RULE_WORKBOOK,
    "ImportGeneralizationData": _RULE_WORKBOOK,
    "ExportMetadata": _TRD,
    "ImportMetadata": _TRD,
    "UnzipCellAndImport": "is not built in grapy: the cell package's layout and its metadata import are defined by the "
                          "MGCP/MUVD Technical Reference Documents, which are not published",
    "GAIT": "is not built in grapy: it launches NGA's GAIT Windows program under Cygwin, the checks being that "
            "program's, and a fenced grapy tool runs no external program (ADR-223 §5.3)",
}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = _REFUSED.get(name)
    message = f"arcpy.topographic.{name} {reason}." if reason else f"arcpy.topographic.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing


# ---- built to their pages --------------------------------------------------------------------------------------------

def ApplyFeatureLevelMetadata(in_features, in_metadata_table, metadata_favorite):
    """The values of the FeatureLevelMetadata row named ``metadata_favorite`` written to every same-named field of the
    features (the selected ones of a layer with a selection); its MetadataTokens fill USER, UTC_DATE and LOCAL_DATE
    fields. A field whose type differs between table and features stops the tool before anything changes, as
    documented. INFERRED (ADR-251 §5.8 (b)): a null in the favourite is written as null; USER is the operating system
    login."""
    return _tp.apply_feature_level_metadata(in_features, in_metadata_table, metadata_favorite)


def CalculateDefaultValues(in_datasets):
    """Null attribute values replaced by the data model's default values (AssignDefaultToField, ADR-228); a value
    already there is left alone. INFERRED: a subtype's default wins over the field's own."""
    return _tp.calculate_default_values(in_datasets)


def CalculateMEF(target_mef_features, in_terrain, obstruction_features, mef_field, max_vo_field, max_terrain_field,
                 specification, terrain_elev_field=None, vo_allowance="150 Feet", vo_accuracy="20 Meters",
                 terrain_accuracy="20 Meters"):
    """Maximum elevation figures by the four published rules (JOG MIL-J-89100, ONC MIL-O-89102, TPC MIL-T-89101,
    STANAG 3591 ed. 6): the obstruction branch and the terrain branch, rounded up to the next 100 feet. The pages'
    rules are followed where their own worked examples disagree with them. INFERRED (§5.8 (c)): terrain is in metres;
    the fields hold feet; ONC, TPC and STANAG take the larger branch; a raster's maximum is that of the cells whose
    centres fall in the cell."""
    return _relief.calculate_mef(target_mef_features, in_terrain, obstruction_features, mef_field, max_vo_field,
                                 max_terrain_field, specification, terrain_elev_field, vo_allowance, vo_accuracy,
                                 terrain_accuracy)


def CreateCrossReferenceGeodatabase(source_workspace, target_database, out_database, mapping_file=None):
    """A cross-reference database (a GeoPackage of the six documented tables) mapping a source workspace's classes and
    fields to a target's: by the same names, or by a mapping workbook's Dataset, Field and Value Mapping sheets, with
    the documented warnings."""
    return _schema.create_cross_reference_geodatabase(source_workspace, target_database, out_database, mapping_file)


def EliminatePolygon(in_features, surrounding_features, min_area=None):
    """Each candidate polygon (the selection, or all; smaller than ``min_area`` when given) merged into the surrounding
    feature it shares the longest boundary with, and every candidate deleted, merged or not, as documented. INFERRED:
    ties go to the earlier surrounding layer and then the lower object ID; the absorbing feature keeps its attributes."""
    return _tp.eliminate_polygon(in_features, surrounding_features, min_area)


def ExtractDataByFeature(in_datasets, target_gdb, reuse_schema="DO_NOT_REUSE", filter_feature=None,
                         filter_type="INTERSECTS", checkout_replica="DO_NOT_CHECKOUT_REPLICA", replica_name=None,
                         expand_feature_classes_and_tables="DO_NOT_ADD", get_related_data="DO_NOT_GET_RELATED",
                         excluded_rel_classes=None, where_clause=None):
    """Datasets copied into an existing workspace under their own names: all rows, the schema only, or the rows the
    one selected filter polygon intersects, contains or clips; topology participants come along, relationship
    destinations by ``expand_feature_classes_and_tables``, related rows forward by ``get_related_data``. Checkout
    replicas are refused by name."""
    return _schema.extract_data_by_feature(in_datasets, target_gdb, reuse_schema, filter_feature, filter_type,
                                           checkout_replica, replica_name, expand_feature_classes_and_tables,
                                           get_related_data, excluded_rel_classes, where_clause)


def GenerateExcelFromGeodatabase(in_geodatabase, out_excel_file):
    """The geodatabase workbook of a GeoPackage, its seven documented worksheets written as an .xlsx (ECMA-376)."""
    return _schema.generate_excel_from_geodatabase(in_geodatabase, out_excel_file)


def GenerateGeodatabaseFromExcel(in_excel_file, out_geodatabase):
    """A GeoPackage's schema made from a geodatabase workbook (.xlsx): spatial references, feature datasets, classes,
    fields, domains, subtypes and defaults, rows whose Create* column is false skipped."""
    return _schema.generate_geodatabase_from_excel(in_excel_file, out_geodatabase)


def GeodatabaseToShape(in_features, output_folder, coded_value_domain_export_mode="VALUES",
                       conversion_method="DEFENSE_BY_SUBTYPE", create_empties="NO_CREATE_EMPTIES"):
    """Shapefiles (ESRI Shapefile Technical Description, 1998) named by class, subtype or MGCP/MUVD feature code, with
    coded values as codes, descriptions or both."""
    return _schema.geodatabase_to_shape(in_features, output_folder, coded_value_domain_export_mode, conversion_method,
                                        create_empties)


def LoadData(in_cross_reference, in_sources, in_target, in_dataset_map_defs=None,
             row_level_errors="ROW_LEVEL_ERROR_LOGGING"):
    """Rows appended from source workspaces to a target by a cross-reference database's mappings — dataset where
    clause, subtype, field mappings with their conditions, and value mappings — with the documented warnings."""
    return _schema.load_data(in_cross_reference, in_sources, in_target, in_dataset_map_defs, row_level_errors)


def MergeLinesByPseudoNode(input_features, merge_fields=None, aggregate_operations=None, merge_feature_rule="FIRST"):
    """Lines meeting at a pseudo node (an end shared by exactly two lines) merged when their merge fields agree,
    aggregates computed over the merged features. INFERRED (§5.8 (d)): FIRST is the default rule; the kept feature's
    direction is kept."""
    return _tp.merge_lines_by_pseudo_node(input_features, merge_fields, aggregate_operations, merge_feature_rule)


def RemoveCutbackVertices(in_features, minimum_angle, removal_method="SEQUENTIAL",
                          skip_coincident_vertices="REMOVE_COINCIDENT"):
    """Vertices whose angle with their two neighbours is under ``minimum_angle`` removed, one at a time or all at once;
    a polygon ring is read round its closing vertex."""
    return _tp.remove_cutback_vertices(in_features, minimum_angle, removal_method, skip_coincident_vertices)


def RepairSelfIntersection(in_features, repair_type="DELETE", max_length=None, repair_at_end_point="IGNORE_ENDS"):
    """Loops where a line crosses itself (or a ring of a polygon part does) deleted or split off, only those shorter
    than ``max_length`` when given, and only where the z of both segments agrees; touching is not crossing, and an end
    snapped back onto the line is repaired with REPAIR_ENDS, as documented."""
    return _tp.repair_self_intersection(in_features, repair_type, max_length, repair_at_end_point)


def SplitFeatures(cutting_features, target_features, use_target_z="DONT_USE_TARGET_Z"):
    """Target lines and polygons split where the selected cutting features cross them, a vertex added to the cutting
    features at each crossing (its z the target's with USE_TARGET_Z). INFERRED: every piece keeps the attributes."""
    return _tp.split_features(cutting_features, target_features, use_target_z)


def ExportTopology(topology, location, file_name):
    """A topology's definition — its classes and ranks, rules and cluster tolerance — written as grapy's own XML
    (ADR-251 §5.8 (h)), which ImportTopology reads. Esri's own layout is not published, so the file is not the one Pro
    writes."""
    return _schema.export_topology(topology, location, file_name)


def ImportTopology(in_feature_dataset, topology_definition_file):
    """A topology recreated in a feature dataset from a definition ExportTopology wrote."""
    return _schema.import_topology(in_feature_dataset, topology_definition_file)


# ---- equivalents -----------------------------------------------------------------------------------------------------

_WMM = ("the World Magnetic Model WMM2015v2, WMM2020 or WMM2025 of the date's epoch, degree 12 spherical harmonics "
        "with secular variation")
_WMM_SOURCE = "Chulliat et al., The US/UK World Magnetic Model for 2025-2030: Technical Report, NOAA NCEI and BGS, 2025"


@equivalent(_WMM, _WMM_SOURCE)
def CalculateMagneticComponents(in_features, altitude="0 Meters", date=None, magnetic_component=None, wmm_file=None):
    """EQUIVALENT: the geomagnetic components at each point by the World Magnetic Model (NOAA's bundled coefficient
    files; a NOAA-format .COF given as ``wmm_file``; Esri's WMM.xml refused by name), GRID_VARIATION being the
    declination less the Lambert Conformal Conic grid convergence. INFERRED (§5.8 (e)): degrees and nanoteslas; the
    altitude is taken as the height above the WGS 84 ellipsoid the model uses."""
    return _relief.calculate_magnetic_components(in_features, altitude, date, magnetic_component, wmm_file)


@equivalent("the minimum-area bounding rectangle for length, width and orientation, Horn's (1981) aspect for downhill "
            "rotation, and MGRS per NGA.STND.0037", "Freeman & Shapira 1975; Horn 1981; NGA.STND.0037 (2014)")
def CalculateMetrics(in_features, in_metric_types, in_length_attributes=None, in_width_attributes=None,
                     in_area_attributes=None, in_angle_attributes=None, in_elevation_attributes=None, in_precision=None,
                     mgrs_attributes=None, mgrs_precision=None, elevation_raster=None,
                     only_populate_null_values="NO_ONLYPOPNULLVALS"):
    """EQUIVALENT: lengths, widths, areas, orientations, elevations and MGRS references written to the fields present.
    INFERRED (§5.8 (f)): metres and square metres; a polygon's length and width are its minimum bounding rectangle's
    sides and its orientation the long side's azimuth; ``in_precision`` is decimal places. The Image City Map
    precision is refused by name."""
    return _metrics.calculate_metrics(in_features, in_metric_types, in_length_attributes, in_width_attributes,
                                      in_area_attributes, in_angle_attributes, in_elevation_attributes, in_precision,
                                      mgrs_attributes, mgrs_precision, elevation_raster, only_populate_null_values)


@equivalent("enclosed gaps as holes of the inputs' union; unenclosed gaps by morphological closing", "Serra 1982")
def FillGaps(input_features, max_gap_area, fill_option="FILL_BY_LENGTH", fill_unenclosed_gaps="SKIP_UNENCLOSED_GAPS",
             max_gap_distance=None):
    """EQUIVALENT: gaps no larger than ``max_gap_area`` given to the neighbour with the longest shared boundary or of
    the earliest input; unenclosed gaps (FILL_ALL) found by closing the union by ``max_gap_distance`` and filled
    first, as documented."""
    return _tp.fill_gaps(input_features, max_gap_area, fill_option, fill_unenclosed_gaps, max_gap_distance)


@equivalent("sheets of the area-of-interest layer meeting the area of interest buffered by buffer_size",
            "ADR-251 §5.8 (i)")
def GenerateAdjoiningSheetsFeatures(in_feature_dataset, area_of_interest, land_features=None, scale="1:50000",
                                    clip_aoi_to_sheets="CLIP_AOI", buffer_size=None):
    """EQUIVALENT: ASG_SHEETS_A, ASG_SHEETS_L and ASG_AOI, and the coast classes where land does not cover the area;
    scale None only, the specification scales refused by name."""
    return _shape.generate_adjoining_sheets_features(in_feature_dataset, area_of_interest, land_features, scale,
                                                     clip_aoi_to_sheets, buffer_size)


@equivalent("area-share thresholds snapped to the contour interval, polygonised and smoothed by Douglas-Peucker and "
            "Chaikin's corner cutting", "TDS TM specification §3.3.6.3 as cited; Chaikin 1974")
def GenerateElevationBands(in_raster, in_aoi, out_feature_class, contour_interval=20, min_area=None,
                           smooth_tolerance=None, in_hydro_features=None, number_of_bands=None):
    """EQUIVALENT: elevation band polygons with Low_Value, High_Value and TintBand."""
    return _relief.generate_elevation_bands(in_raster, in_aoi, out_feature_class, contour_interval, min_area,
                                            smooth_tolerance, in_hydro_features, number_of_bands)


@equivalent("the faces of the contours and the area of interest, each given the range of its bounding contours",
            "ADR-251 §5.8 (j)")
def GenerateElevationBandsFromFeatures(contour_features, elevation_field, area_of_interest, out_feature_class,
                                       exclusion_features=None, product="Custom", band_interval=None, band_values=None):
    """EQUIVALENT: elevation bands from contours; JOG-A by the page's sample values, Custom by interval or values; ONC
    and TPC refused by name."""
    return _relief.generate_elevation_bands_from_features(contour_features, elevation_field, area_of_interest,
                                                          out_feature_class, exclusion_features, product, band_interval,
                                                          band_values)


@equivalent("GenerateElevationBands, ThinHydrologyLines, IdentifyNarrowPolygons and spot thinning by distance, composed",
            "ADR-251 §5.8 (k)")
def GenerateElevationGuideFeatures(in_feature_dataset, area_of_interest, in_rasters, hydro_exclusion_features=None,
                                   spot_height_features=None, hydro_line_features=None, hydro_area_features=None,
                                   contour_interval=20, bands_minarea=None, smooth_tolerance=None, number_of_bands=None,
                                   height_field=None, search_distance="0 Meters", hydroline_minlength=None,
                                   hydroline_minspacing=None, hydroarea_minlength=None, hydroarea_minwidth=None,
                                   min_island_area=None):
    """EQUIVALENT: the EGB_* classes of an elevation guide box."""
    return _shape.generate_elevation_guide_features(in_feature_dataset, area_of_interest, in_rasters,
                                                    hydro_exclusion_features, spot_height_features, hydro_line_features,
                                                    hydro_area_features, contour_interval, bands_minarea,
                                                    smooth_tolerance, number_of_bands, height_field, search_distance,
                                                    hydroline_minlength, hydroline_minspacing, hydroarea_minlength,
                                                    hydroarea_minwidth, min_island_area)


@equivalent(_WMM + ", contoured over the extent by the contouring of arcpy.sa.Contour", _WMM_SOURCE)
def GenerateIsogonicLines(extent_features, altitude="0 Meters", date=None, interval=1, base=0,
                          target_isogonic_features=None, magnetic_field=None, subtype=None,
                          annual_drift="NO_ANNUAL_DRIFT", annual_drift_field=None):
    """EQUIVALENT: lines of equal declination at ``base`` + k·``interval``, appended to the target. INFERRED: the
    drift is the model's yearly change of declination at each line's midpoint."""
    return _relief.generate_isogonic_lines(extent_features, altitude, date, interval, base, target_isogonic_features,
                                           magnetic_field, subtype, annual_drift, annual_drift_field)


@equivalent("a five-by-five-sheet diagram centred on the area of interest's sheet", "ADR-251 §5.8 (i)")
def GenerateLocationDiagramFeatures(in_feature_dataset, area_of_interest, sheet_id_field, wac_features, onc_features,
                                    land_features, specification="JOG", tpc_features=None, smooth_tolerance=None,
                                    hydro_line_features=None, hydro_area_features=None, hydroline_minlength=None,
                                    hydroline_minspacing=None, hydroarea_minlength=None, hydroarea_minwidth=None,
                                    min_island_area=None):
    """EQUIVALENT: LD_AOI, JOG_AOI, JOG_SHEETS_A/L, JOG_WAC, JOG_ONC and JOG_COAST_A/L; ``tpc_features`` refused by
    name."""
    return _shape.generate_location_diagram_features(in_feature_dataset, area_of_interest, sheet_id_field,
                                                     wac_features, onc_features, land_features, specification,
                                                     tpc_features, smooth_tolerance, hydro_line_features,
                                                     hydro_area_features, hydroline_minlength, hydroline_minspacing,
                                                     hydroarea_minlength, hydroarea_minwidth, min_island_area)


@equivalent("innermost closed contours, the raster's highest (top) or lowest (depression) cells inside each",
            "ADR-251 §5.8 (l)")
def GenerateSpotHeights(in_contour_features, in_raster, target_spot_features, contour_height_field, spot_height_field,
                        spot_height_subtype, area_of_interest=None, scale="1:50,000", z_factor=None):
    """EQUIVALENT: a spot height at every cell of the extreme value inside each top and depression; at 1:250,000 or
    smaller scales the default z factor 3.28084 and rounding up to the next 5 feet."""
    return _relief.generate_spot_heights(in_contour_features, in_raster, target_spot_features, contour_height_field,
                                         spot_height_field, spot_height_subtype, area_of_interest, scale, z_factor)


@equivalent("the contouring of arcpy.sa.Contour, Gaussian raster smoothing and Douglas-Peucker/Chaikin line smoothing",
            "Esri, How contouring works; Chaikin 1974")
def GenerateTopographicContours(in_rasters, area_of_interest, contour_features, elevation_field, contour_subtype=None,
                                scale="1:50,000", resample_raster="NO_RESAMPLE_RASTER", contour_interval=None,
                                base_contour=0, z_factor=1, zero_contour="NO_ZERO_CONTOUR", code_field=None,
                                index_interval=None, index_code=1, intermediate_code=2, depression_code=5,
                                depression_intermediate_code=6, raster_smooth_tolerance=None, minimum_length=None,
                                contour_smooth_tolerance=None, supplemental_contours="NONE", half_auxiliary_code=3,
                                quarter_auxiliary_code=14, depression_auxiliary_code=22):
    """EQUIVALENT: coded contours appended to the target inside the one area of interest. INFERRED (§5.8 (m)): the
    interval each scale defaults to; the index interval five contour intervals; RESAMPLE_RASTER refused by name."""
    return _relief.generate_topographic_contours(
        in_rasters, area_of_interest, contour_features, elevation_field, contour_subtype, scale, resample_raster,
        contour_interval, base_contour, z_factor, zero_contour, code_field, index_interval, index_code,
        intermediate_code, depression_code, depression_intermediate_code, raster_smooth_tolerance, minimum_length,
        contour_smooth_tolerance, supplemental_contours, half_auxiliary_code, quarter_auxiliary_code,
        depression_auxiliary_code)


@equivalent("NGA GNS tab-delimited country files, one point per UFI and one row per UNI", "NGA Geographic Names Server")
def GeoNamesToGeodatabase(in_source, in_feature_class, in_allow_duplicates, in_table, match_fields=None):
    """EQUIVALENT: an NGA GeoNames file loaded into the GeoNames feature class and table, fields matched by name or by
    ``match_fields``."""
    return _schema.geonames_to_geodatabase(in_source, in_feature_class, in_allow_duplicates, in_table, match_fields)


@equivalent("closed contours whose inside lies below them, by the raster's mean", "ADR-251 §5.8 (m)")
def IdentifyContours(in_contour_features, in_rasters, contour_height_field, contour_code_field,
                     contour_index_interval=100, index_code=1, intermediate_code=2, depression_code=5,
                     depression_intermediate_code=6, depression_contours_count=None, area_of_interest=None, z_factor=1,
                     supplemental_contours="NONE", half_auxiliary_code=3, quarter_auxiliary_code=14,
                     depression_auxiliary_code=22, contour_interval=20, base_contour=0):
    """EQUIVALENT: each contour's code — index, intermediate, auxiliary and their depression forms."""
    return _relief.identify_contours(in_contour_features, in_rasters, contour_height_field, contour_code_field,
                                     contour_index_interval, index_code, intermediate_code, depression_code,
                                     depression_intermediate_code, depression_contours_count, area_of_interest,
                                     z_factor, supplemental_contours, half_auxiliary_code, quarter_auxiliary_code,
                                     depression_auxiliary_code, contour_interval, base_contour)


@equivalent("morphological opening by a disc of the minimum width; centrelines by the Voronoi medial axis",
            "Serra 1982; Blum 1967")
def IdentifyNarrowPolygons(in_features, out_feature_class, min_width, min_length, taper_length=None,
                           connecting_features=None):
    """EQUIVALENT: polygons split into narrow, wide, short and tapered pieces with split_type, split_length and FID."""
    return _shape.identify_narrow_polygons(in_features, out_feature_class, min_width, min_length, taper_length,
                                           connecting_features)


@equivalent("the Voronoi medial axis of the densified boundary, pruned to its main path", "Blum 1967")
def PolygonToCenterline(in_features, out_feature_class, connecting_features=None):
    """EQUIVALENT: centrelines split at junctions, with FID, FID_<input> and FID_<connecting class>."""
    return _shape.polygon_to_centerline(in_features, out_feature_class, connecting_features)


@equivalent("the page's rules for dangles, overshoots and declination angles", "ADR-251 §5.8 (n)")
def RemoveSmallLines(in_features, minimum_length, maximum_angle=None, in_intersecting_features=None,
                     recursive="NON_RECURSIVE", split_input_lines="SPLIT"):
    """EQUIVALENT: dangling lines shorter than the minimum removed, overshoots trimmed, the smallest-declination line
    within ``maximum_angle`` kept unless its combined length is still short."""
    return _tp.remove_small_lines(in_features, minimum_length, maximum_angle, in_intersecting_features, recursive,
                                  split_input_lines)


@equivalent("DEM heights at the line ends; networks oriented toward their lowest outlets by breadth-first search",
            "ADR-251 §5.8 (o)")
def SetLineDirection(in_line_features, digital_elevation_model, line_direction="DOWNHILL_FLOW",
                     reverse_direction="KEEP_DIRECTION", vertical_tolerance="0 Meters"):
    """EQUIVALENT: lines set to run downhill, or with the flow of their network, split at peaks with
    DOWNHILL_SPLIT_FLOW."""
    return _relief.set_line_direction(in_line_features, digital_elevation_model, line_direction, reverse_direction,
                                      vertical_tolerance)


@equivalent("stroke-based selection by good continuation", "Thomson & Richardson 1999")
def ThinHydrologyLines(in_features, invisibility_field, min_length, min_spacing=None, hierarchy_field=None,
                       intersecting_features=None, unsplit_lines="UNSPLIT_LINES", use_angles="NO_USE_ANGLES"):
    """EQUIVALENT: 0 (visible) or 1 (hidden) written to the invisibility field."""
    return _shape.thin_hydrology_lines(in_features, invisibility_field, min_length, min_spacing, hierarchy_field,
                                       intersecting_features, unsplit_lines, use_angles)


@equivalent("greedy selection by priority, each kept spot clearing its search distance", "ADR-251 §5.8 (p)")
def ThinSpotHeights(in_features, area_of_interest, elevation_field, invisibility_field, high_low_spots=None,
                    search_distance="1300 Meters", max_spots=None, input_contours=None, contour_code_field=None,
                    depression_code_value=None):
    """EQUIVALENT: 0 (visible) or 1 (hidden) written to the invisibility field of the spot heights in the area."""
    return _relief.thin_spot_heights(in_features, area_of_interest, elevation_field, invisibility_field,
                                     high_low_spots, search_distance, max_spots, input_contours, contour_code_field,
                                     depression_code_value)


@equivalent("the GeoNames feature's name of its stored name ID, else its approved name of lowest rank",
            "ADR-251 §5.8 (q)")
def UpdateGeoNames(in_features, in_geonames_features, in_geonames_table, named_feature_id_field, name_id_field,
                   name_field):
    """EQUIVALENT: each feature's name ID and name refreshed from the GeoNames data by its named feature ID."""
    return _schema.update_geonames(in_features, in_geonames_features, in_geonames_table, named_feature_id_field,
                                   name_id_field, name_field)


@equivalent("the documented codes tested in the order 4, 3, 2, 1", "ADR-251 §5.8 (r)")
def ValidateSpotHeights(in_contour_features, contour_height_field, contour_interval, in_rasters, in_spot_features,
                        spot_height_field, area_of_interest=None, z_factor=1, use_rasters="USE_RASTERS"):
    """EQUIVALENT: IS_VALID 0 valid, 1 not the highest point of its top, 2 a missing contour, 3 on the wrong side of
    its contour, 4 in no closed contour."""
    return _relief.validate_spot_heights(in_contour_features, contour_height_field, contour_interval, in_rasters,
                                         in_spot_features, spot_height_field, area_of_interest, z_factor, use_rasters)
