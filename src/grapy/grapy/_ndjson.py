"""ExportDiagramContent's JSON — ADR-232 §5.6.

The Export Diagram Content page points to the ArcGIS REST API's Query Diagram Content response for the file's layout:
``diagram``, ``spatialRef``, ``sourceMapping``, ``diagramInfo`` (template, tag, creator, dates, counts and extents),
and ``containers``, ``junctions`` and ``edges``, each with ``id``, ``containerID``, ``assocSourceID``,
``assocGlobalID``, ``userType`` and, on request, ``geometry``, ``attributes`` and ``aggregations`` (``sourceID``,
``globalID``, ``assetGroup``, ``assetType``); an edge adds ``fromID`` and ``toID``. INFERRED (§5.9 (h)): ``userType``
is the network class's name (``SystemJunctions`` for a system junction, ``Associations`` for an association edge,
``Reduction`` for a reduction feature); attribute keys are the field's alias where it has one, else its name.
"""
from __future__ import annotations

from . import _diagram, _gpkg
from ._unjson import esri


def document(network, d, props, geoms, attrs, aggs, names):
    srid = _diagram.srid_of(network)
    sources = network.sources()
    doc = {"diagram": d.name, "spatialRef": {"wkid": srid, "latestWkid": srid},
           "sourceMapping": {**{c.rsplit("/", 1)[-1]: s for s, (c, _, _) in sources.items()},
                             _diagram.SYSTEM_JUNCTIONS: 0, "Associations": _diagram.ASSOCIATION_SOURCE}}
    features = sorted(d.features.values(), key=lambda f: f.id)
    cache = {}

    def user_type(f):
        if f.reduction:
            return "Reduction"
        if f.association:
            return "Associations"
        if f.source == 0:
            return _diagram.SYSTEM_JUNCTIONS
        return sources[int(f.source)][0].rsplit("/", 1)[-1] if f.source in sources else None

    def row_of(source, globalid):
        class_name = sources[int(source)][0]
        if class_name not in cache:
            from . import _field_domains, _field_titles
            columns = [c.name for c in _gpkg.describe(network.connection, class_name)[0]]
            cache[class_name] = (columns, _field_titles(network.connection, class_name),
                                 _field_domains(network.connection, class_name))
        columns, titles, domains = cache[class_name]
        oid = network.oid(class_name)
        found = network.q(f'select "{oid}", {", ".join(chr(34) + c + chr(34) for c in columns)} from "{class_name}" '
                          'where upper(GLOBALID) = upper(?)', str(globalid)).fetchone()
        if found is None:
            return None
        values = dict(zip([oid] + columns, found))
        return class_name, values, titles, domains

    def coded(class_name, field, value, values, domains):
        if not names or value is None:
            return value
        low = field.lower()
        if low == "assetgroup":
            return network.asset_names(class_name, value, values.get("ASSETTYPE") or 0)[0]
        if low == "assettype":
            return network.asset_names(class_name, values.get("ASSETGROUP") or 0, value)[1]
        domain = domains.get(low)
        if domain:
            hit = network.q("select description from gpkg_data_column_constraints where constraint_name = ? and "
                            "constraint_type = 'enum' and value = ?", domain, str(value)).fetchone()
            return hit[0] if hit else value
        return value

    def attributes(f):
        if not f.key or f.association or f.reduction or f.source not in sources:
            return None
        found = row_of(f.source, f.globalid)
        if found is None:
            return None
        class_name, values, titles, domains = found
        return {titles.get(k.lower(), k): coded(class_name, k, v, values, domains) for k, v in values.items()
                if not isinstance(v, bytes)}

    def aggregations(f):
        out = []
        for source, globalid in f.aggregated:
            entry = {"sourceID": source, "globalID": globalid}
            if source in sources:
                found = row_of(source, globalid)
                if found is not None:
                    class_name, values, _, domains = found
                    entry["assetGroup"] = coded(class_name, "ASSETGROUP", values.get("ASSETGROUP"), values, domains)
                    entry["assetType"] = coded(class_name, "ASSETTYPE", values.get("ASSETTYPE"), values, domains)
            out.append(entry)
        return out

    def entry(f):
        out = {"id": f.id, "containerID": f.container, "assocSourceID": f.source,
               "assocGlobalID": f.globalid if f.source not in (None, 0) else None, "userType": user_type(f)}
        if f.kind == "E":
            out["fromID"], out["toID"] = f.from_id, f.to_id
        if geoms:
            out["geometry"] = esri(f.shape)
        if attrs:
            out["attributes"] = attributes(f)
        if aggs:
            out["aggregations"] = aggregations(f)
        return out

    if props:
        shapes = [f.shape for f in features if f.shape is not None]
        bounds = None
        if shapes:
            import shapely
            b = shapely.GeometryCollection(shapes).bounds
            bounds = {"xmin": b[0], "ymin": b[1], "xmax": b[2], "ymax": b[3]}
        network_shapes = []
        for f in features:
            if f.key and f.source in sources:
                found = network.q(f'select geom from "{sources[int(f.source)][0]}" where upper(GLOBALID) = upper(?)',
                                  str(f.globalid)).fetchone() if sources[int(f.source)][2] not in (
                    "JunctionObject", "EdgeObject") else None
                shape = _gpkg.decode(found[0]) if found and found[0] else None
                if shape is not None and not shape.is_empty:
                    network_shapes.append(shape)
        net = None
        if network_shapes:
            import shapely
            b = shapely.GeometryCollection(network_shapes).bounds
            net = {"xmin": b[0], "ymin": b[1], "xmax": b[2], "ymax": b[3]}
        doc["diagramInfo"] = {
            "template": d.template, "tag": d.tags, "creator": d.owner, "creationDate": d.created,
            "lastUpdateBy": d.updated_by, "lastUpdateDate": d.updated,
            "junctionCount": sum(1 for f in features if f.kind == "J"),
            "edgeCount": sum(1 for f in features if f.kind == "E"),
            "containerCount": sum(1 for f in features if f.kind == "C"),
            "aggregationCount": sum(len(f.aggregated) for f in features),
            "diagramExtent": bounds, "networkExtent": net}
    doc["containers"] = [entry(f) for f in features if f.kind == "C"]
    doc["junctions"] = [entry(f) for f in features if f.kind == "J"]
    doc["edges"] = [entry(f) for f in features if f.kind == "E"]
    return doc
