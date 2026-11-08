"""arcpy.analysis against ArcGIS Pro 3.7."""
CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


@case("analysis.Buffer")
def _(ap, I, O):
    return ap.analysis.Buffer(I["points"], O("out"), "25 Meters")


@case("analysis.Clip")
def _(ap, I, O):
    return ap.analysis.Clip(I["polys"], I["clip"], O("out"))


@case("analysis.Select")
def _(ap, I, O):
    return ap.analysis.Select(I["points"], O("out"), "val > 50")
