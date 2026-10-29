"""arcpy's Parameter and Field — what getParameterInfo returns and Describe lists — ADR-223 §5.1."""
from __future__ import annotations


class Filter:
    def __init__(self):
        self.type = None
        self.list = []


class Parameter:
    """A tool parameter, as a Python toolbox declares it."""

    def __init__(self, name=None, displayName=None, direction="Input", datatype="GPString",
                 parameterType="Required", enabled=True, category=None, symbology=None, multiValue=False):
        self.name = name
        self.displayName = displayName or name
        self.direction = direction
        self.datatype = datatype
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
                "datatype": _datatype(self.datatype), "parameterType": self.parameterType, "multiValue": self.multiValue,
                "choiceList": list(self.filter.list) if self.filter.type == "ValueList" else None,
                "defaultValue": self.value if isinstance(self.value, (str, int, float, bool)) else None}


def _datatype(datatype):
    name = datatype if isinstance(datatype, str) else (datatype[0] if datatype else "GPString")
    return name


class Field:
    """A field as Describe and ListFields answer it."""

    def __init__(self, name="", type="String", nullable=True, length=None, aliasName=None):
        self.name = name
        self.aliasName = aliasName or name
        self.baseName = name
        self.type = type
        self.isNullable = nullable
        self.length = length or (255 if type == "String" else 0)
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
        self.name = name
        self.fields = list(fields or [])
        self.isUnique = bool(isUnique)
        self.isAscending = bool(isAscending)

    def __repr__(self):
        return f"<Index {self.name}>"


def _tokens(text):
    import shlex
    return shlex.split(str(text), posix=True)


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
        """The row's values as a space-delimited string."""
        return " ".join(_quoted(v) for v in self._rows[int(row)])

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
