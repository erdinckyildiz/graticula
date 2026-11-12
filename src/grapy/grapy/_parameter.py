"""arcpy's Parameter and Field — what getParameterInfo returns and Describe lists — ADR-223 §5.1."""
from __future__ import annotations


class Filter:
    """A parameter's filter: a ValueList with an empty list until it is set, as ArcGIS Pro 3.7 answers a new
    parameter's (measured 2026-11-08)."""

    def __init__(self):
        self.type = "ValueList"
        self.list = []


# The data types of a parameter, by the keyword a Python toolbox gives and the name arcpy answers as ``datatype``
# (ArcGIS Pro 3.7 answers "Linear Unit" for GPLinearUnit and "String" for GPString, measured 2026-11-08). The names
# are those of the page "Defining parameter data types in a Python toolbox". A keyword not listed is answered as given.
DATATYPE_NAMES = {
    "GPString": "String", "GPLong": "Long", "GPDouble": "Double", "GPBoolean": "Boolean", "GPDate": "Date",
    "GPLinearUnit": "Linear Unit", "GPArealUnit": "Areal Unit", "GPFeatureLayer": "Feature Layer",
    "GPRasterLayer": "Raster Layer", "GPTableView": "Table View", "GPLayer": "Layer", "GPValueTable": "Value Table",
    "GPExtent": "Extent", "GPSpatialReference": "Spatial Reference", "GPCoordinateSystem": "Coordinate System",
    "GPSQLExpression": "SQL Expression", "GPVariant": "Variant", "GPFeatureRecordSetLayer": "Feature Set",
    "GPRecordSet": "Record Set", "GPPoint": "Point", "GPCellSize": "Cell Size", "GPEnvelope": "Envelope",
    "GPFieldMapping": "Field Mappings", "GPMultiValue": "Multiple Value", "GPType": "Data Type",
    "GPSAGeoData": "Raster Dataset Layer", "GPRasterDataLayer": "Raster Data Layer", "GPSACellSize": "Cell Size XY",
    "GPEncryptedString": "Encrypted String", "GPGroupLayer": "Group Layer", "GPMap": "Map", "GPLayout": "Layout",
    "GPInternetTiledLayer": "Internet Tiled Layer", "GPMosaicLayer": "Mosaic Layer", "GPTimeUnit": "Time Unit",
    "GPStringHidden": "String Hidden", "GPCalculatorExpression": "Calculator Expression",
    "GPSpatialReferenceType": "Spatial Reference", "GPDataFile": "Data File", "GPLasDatasetLayer": "LAS Dataset Layer",
    "GPNetworkDatasetLayer": "Network Dataset Layer", "GPTinLayer": "TIN Layer", "GPTerrainLayer": "Terrain Layer",
    "GPRasterCalculatorExpression": "Raster Calculator Expression", "GPRasterBuilder": "Raster Builder",
    "DEFeatureClass": "Feature Class", "DETable": "Table", "DEFolder": "Folder", "DEWorkspace": "Workspace",
    "DEFile": "File", "DERasterDataset": "Raster Dataset", "DEFeatureDataset": "Feature Dataset",
    "DEDatasetType": "Dataset", "DEType": "Data Element", "DELayer": "Layer File", "DEMosaicDataset": "Mosaic Dataset",
    "DETextfile": "Text File", "DERasterCatalog": "Raster Catalog", "DERelationshipClass": "Relationship Class",
    "DEShapefile": "Shapefile", "DEPrjFile": "Prj File", "DETin": "TIN", "DELasDataset": "LAS Dataset",
    "DENetworkDataset": "Network Dataset", "DETopology": "Topology", "DEToolbox": "Toolbox", "DEMapDocument": "ArcMap Document",
    "DEServerConnection": "Server Connection", "DECadDrawingDataset": "CAD Drawing Dataset", "Field": "Field",
    "GPSAGeoDataDomain": "Geodataset", "AnalysisCellSize": "Analysis Cell Size",
}


class Parameter:
    """A tool parameter, as a Python toolbox declares it."""

    def __init__(self, name=None, displayName=None, direction="Input", datatype="GPString",
                 parameterType="Required", enabled=True, category=None, symbology=None, multiValue=False):
        self.name = name
        self.displayName = displayName or name
        self.direction = direction
        self._datatype = datatype
        self.parameterType = parameterType
        self.enabled = enabled
        self.category = category
        self.symbology = symbology
        self.multiValue = multiValue
        self.parameterDependencies = []
        self.columns = []
        self.filter = Filter()
        self.value = None
        self.message = ""
        self.altered = False
        self.hasBeenValidated = False

    @property
    def datatype(self):
        """The data type's name, as arcpy answers it ("Linear Unit" for "GPLinearUnit"); the keyword given is kept in
        ``_datatype`` for grapy's runner, and a list of keywords answers a list of names."""
        kind = self._datatype
        if isinstance(kind, (list, tuple)):
            return [DATATYPE_NAMES.get(k, k) for k in kind]
        return DATATYPE_NAMES.get(kind, kind)

    @datatype.setter
    def datatype(self, value):
        self._datatype = value

    @property
    def valueAsText(self):
        if self.value is None:
            return None
        if isinstance(self.value, (list, tuple)):
            return ";".join(str(v) for v in self.value)
        return str(self.value)

    @valueAsText.setter
    def valueAsText(self, text):
        self.value = text

    def setErrorMessage(self, message):
        self.message = message

    def setWarningMessage(self, message):
        self.message = message

    def clearMessage(self):
        self.message = ""

    def hasError(self):
        return bool(self.message)

    def describe(self) -> dict:
        """The parameter as this server's task resource lists it."""
        return {"name": self.name, "displayName": self.displayName, "direction": self.direction,
                "datatype": _datatype(self._datatype), "parameterType": self.parameterType, "multiValue": self.multiValue,
                "choiceList": list(self.filter.list) if self.filter.type == "ValueList" else None,
                "defaultValue": self.value if isinstance(self.value, (str, int, float, bool)) else None}


def _datatype(datatype):
    name = datatype if isinstance(datatype, str) else (datatype[0] if datatype else "GPString")
    return name


# A field's length by type, as ArcGIS Pro 3.7's ListFields answers on a file geodatabase (measured 2026-10-31, ADR-240):
# the bytes it stores, 255 for a text field given none, 0 for geometry and blobs.
_LENGTHS = {"OID": 4, "SmallInteger": 2, "Integer": 4, "BigInteger": 8, "Single": 4, "Double": 8, "String": 255,
            "Date": 8, "DateOnly": 8, "TimeOnly": 8, "TimestampOffset": 10, "Guid": 38, "GlobalID": 38}


class Field:
    """A field as Describe and ListFields answer it."""

    def __init__(self, name="", type="", nullable=True, length=None, aliasName=None):
        # A blank Field() — arcpy.Field() in a script — has no type and length 0, as ArcGIS Pro 3.7 answers (measured
        # 2026-11-08); the fields Describe and ListFields answer are always made with a type. Its scale stays 0: Pro
        # answered -1 in one run and 6881395 in the next, an unset value, which is not copied.
        self.name = name
        self.aliasName = aliasName or name
        self.baseName = name
        self.type = type
        self.isNullable = nullable
        self.length = length or _LENGTHS.get(type, 0)
        self.editable = type not in ("OID", "Geometry")
        self.required = type in ("OID", "Geometry")
        self.precision = 0
        self.scale = 0
        self.domain = ""
        self.defaultValue = None

    def __repr__(self):
        return f"<Field {self.name} {self.type}>"


class Index:
    """An attribute index of a table, as ListIndexes answers it: its name, fields, and whether it is unique and
    ascending. INFERRED: SQLite indexes are ascending unless a column is declared DESC; grapy reads the declaration."""

    def __init__(self, name="", fields=None, isUnique=False, isAscending=True):
        # fields are Field objects, as ArcGIS Pro 3.7 answers an index's (measured 2026-11-08); names given are
        # made Fields.
        self.name = name
        self.fields = [f if isinstance(f, Field) else Field(str(f)) for f in (fields or [])]
        self.isUnique = bool(isUnique)
        self.isAscending = bool(isAscending)

    def __repr__(self):
        return f"<Index {self.name}>"


def _tokens(text):
    """A value table row's cells: split on spaces, quotes keeping a cell together. Backslashes are characters, not
    escapes: Pro reads a Windows path in a row (``<gdb path> Shape.Z Mass_Points``) as given (measured in ArcGIS Pro
    3.7, 2026-11-08, by the ddd sweep: CreateTin's in_features), where shlex's POSIX mode dropped every backslash."""
    import shlex
    lexer = shlex.shlex(str(text), posix=True)
    lexer.whitespace_split, lexer.escape = True, ""
    return list(lexer)


def _quoted(value):
    text = "" if value is None else str(value)
    return f"'{text}'" if (" " in text or text == "") else text


class ValueTable:
    """arcpy.ValueTable: rows of values in a fixed number of columns. ``columns`` is a count or a list of data types;
    a row is added as a list or as a space-delimited string, values with spaces in quotes."""

    def __init__(self, columns=None):
        if isinstance(columns, (list, tuple)):
            self._types = [str(c) for c in columns]
        else:
            self._types = ["GPString"] * int(columns or 0)
        self._rows = []

    @property
    def columnCount(self):
        return len(self._types)

    @property
    def rowCount(self):
        return len(self._rows)

    def addColumns(self, number_of_columns):
        self._types += ["GPString"] * int(number_of_columns)
        for row in self._rows:
            row += [""] * int(number_of_columns)

    def setColumns(self, number_of_columns):
        self._types = ["GPString"] * int(number_of_columns)

    def _row(self, value):
        values = list(value) if isinstance(value, (list, tuple)) else _tokens(value)
        if self._types and len(values) > len(self._types):
            raise ValueError(f"ValueTable: a row of {len(values)} values for {len(self._types)} columns.")
        return values + [""] * (len(self._types) - len(values))

    def addRow(self, value):
        self._rows.append(self._row(value))

    def setRow(self, row, value):
        self._rows[int(row)] = self._row(value)

    def removeRow(self, row):
        del self._rows[int(row)]

    def getRow(self, row):
        """The row's values as a space-delimited string, each in single quotes: ArcGIS Pro 3.7 answers "'a' '1'" for the
        row "a 1" (measured 2026-11-08)."""
        return " ".join("'" + ("" if v is None else str(v)) + "'" for v in self._rows[int(row)])

    def getTrueRow(self, row):
        return [self.getTrueValue(row, c) for c in range(self.columnCount)]

    def getValue(self, row, column):
        value = self._rows[int(row)][int(column)]
        return "" if value is None else str(value)

    def getTrueValue(self, row, column):
        """The value as a Python object: integers, doubles and Booleans as int, float and bool; others as strings."""
        value = self._rows[int(row)][int(column)]
        kind = self._types[int(column)].upper() if int(column) < len(self._types) else "GPSTRING"
        if isinstance(value, str):
            if kind in ("GPLONG", "LONG", "GPINTEGER", "INTEGER"):
                return int(value)
            if kind in ("GPDOUBLE", "DOUBLE"):
                return float(value)
            if kind in ("GPBOOLEAN", "BOOLEAN"):
                return value.strip().lower() in ("true", "1")
        return value

    def setValue(self, row, column, value):
        self._rows[int(row)][int(column)] = value

    def exportToString(self):
        """Rows separated by semicolons, values by spaces, a value with spaces in single quotes."""
        return ";".join(" ".join(_quoted(v) for v in row) for row in self._rows)

    def loadFromString(self, string):
        """Rows separated by semicolons, values by spaces, as exportToString writes them. Every column becomes GPString."""
        rows = [_tokens(r) for r in _split_rows(string)]
        width = max((len(r) for r in rows), default=self.columnCount)
        self._types = ["GPString"] * max(width, self.columnCount)
        self._rows = [r + [""] * (len(self._types) - len(r)) for r in rows]

    def __str__(self):
        return self.exportToString()


def _split_rows(text):
    """A value table's text split at the semicolons that are not inside quotes."""
    rows, current, quote = [], "", None
    for character in str(text):
        if quote:
            current += character
            if character == quote:
                quote = None
        elif character in "'\"":
            quote = character
            current += character
        elif character == ";":
            rows.append(current)
            current = ""
        else:
            current += character
    if current.strip():
        rows.append(current)
    return [r for r in rows if r.strip()]


class FieldInfo:
    """arcpy.FieldInfo: for each field, a new name, whether it is visible, and its split rule — what Make Feature Layer
    and Make Table View take. INFERRED: exportToString writes ``name new_name VISIBLE|HIDDEN rule`` per field,
    fields separated by semicolons; the page does not show the format."""

    def __init__(self):
        self._fields = []

    @property
    def count(self):
        return len(self._fields)

    def addField(self, field_name, new_field_name, visible, split_rule):
        visible = str(visible).upper()
        if visible not in ("VISIBLE", "HIDDEN"):
            raise ValueError("FieldInfo.addField: visible is VISIBLE or HIDDEN.")
        rule = str(split_rule).upper()
        if rule not in ("NONE", "RATIO"):
            raise ValueError("FieldInfo.addField: split_rule is NONE or RATIO.")
        self._fields.append([str(field_name), str(new_field_name), visible, rule])

    def findFieldByName(self, field_name):
        return next((i for i, f in enumerate(self._fields) if f[0].lower() == str(field_name).lower()), -1)

    def findFieldByNewName(self, field_name):
        return next((i for i, f in enumerate(self._fields) if f[1].lower() == str(field_name).lower()), -1)

    def getFieldName(self, index):
        return self._fields[int(index)][0]

    def getNewName(self, index):
        return self._fields[int(index)][1]

    def getVisible(self, index):
        return self._fields[int(index)][2]

    def getSplitRule(self, index):
        return self._fields[int(index)][3]

    def setFieldName(self, index, field_name):
        self._fields[int(index)][0] = str(field_name)

    def setNewName(self, index, new_field_name):
        self._fields[int(index)][1] = str(new_field_name)

    def setVisible(self, index, visible):
        self._fields[int(index)][2] = str(visible).upper()

    def setSplitRule(self, index, rule):
        self._fields[int(index)][3] = str(rule).upper()

    def removeField(self, index):
        del self._fields[int(index)]

    def exportToString(self):
        return ";".join(" ".join(f) for f in self._fields)

    def loadFromString(self, string):
        self._fields = []
        for row in _split_rows(string):
            values = _tokens(row)
            if len(values) != 4:
                raise ValueError(f"FieldInfo.loadFromString: '{row}' is not 'name new_name visible rule'.")
            self.addField(*values)

    def __str__(self):
        return self.exportToString()
