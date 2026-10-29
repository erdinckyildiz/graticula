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
