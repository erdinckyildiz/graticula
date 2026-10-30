"""Network diagrams of a utility network, held in the GeoPackage a tool works on — ADR-232.

A network diagram is a schematic view of some of a utility network's features: diagram junctions, diagram edges and
diagram containers, each linked to the network element or feature it represents, or aggregating several that a rule
reduced, collapsed or removed. ArcGIS publishes what the diagram tools do — the Network Diagram toolbox pages, and the
ArcGIS REST API's Query Diagram Content response for what a diagram holds — but not how Esri stores a diagram, so the
storage is grapy's own (INFERRED, ADR-232 §5.9 (a), Q-200):

- ``grapy_nd_template``: a diagram template — its properties and its ordered steps, each a rule, a layout or a
  diagram feature selection, with the parameters its Add tool was given;
- ``grapy_nd_diagram``: a diagram — its template, owner, access right, tags, dates, whether it is stored, whether it
  is a subnetwork system diagram, the input it was made from and the signature of the topology it was made against;
- ``grapy_nd_feature``: what a diagram feature represents (its network elements) and aggregates, and its flags;
- three feature classes beside the network, ``<network>_DiagramJunctions`` (points), ``<network>_DiagramEdges``
  (lines) and ``<network>_DiagramContainers`` (polygons), holding every diagram's features with DIAGRAMNAME,
  DIAGRAMFEATUREID, ASSOCSOURCEID, ASSOCGLOBALID and CONTAINERID (FROMID and TOID on edges), so a cursor or a layer
  reads a diagram's geometry as it reads any class.

A diagram is built from the network topology ``_utility.build`` makes from the record, never from the features as they
are now, as the pages say diagrams reflect the topology "the last time it was enabled or validated". Each junction
feature (all its terminals) is one diagram junction; each edge element — a line between two connection points — one
diagram edge, from the junction at its start to the junction at its end; a system junction one diagram junction; a
network feature with no element (a structure boundary, an assembly) one diagram junction that becomes a container when
it has content in the diagram (INFERRED, §5.9 (b)).
"""
from __future__ import annotations

import datetime
import getpass
import hashlib
import json
import math
from collections import defaultdict

import shapely
from shapely.geometry import LineString, Point, box

from . import _gpkg, _utility
from ._runtime import ExecuteError
from ._topology import register

EXTENSION = "grapy_network_diagram"
DEFINITION = "Graticula ADR-232: grapy's network diagrams"
TABLES = ("grapy_nd_template", "grapy_nd_diagram", "grapy_nd_feature")
CLASSES = (("DiagramJunctions", "Point"), ("DiagramEdges", "Polyline"), ("DiagramContainers", "Polygon"))

# INFERRED (ADR-232 §5.9 (e)): the "estimate of the average junction size" proportional units are relative to is one
# unit of the diagram's coordinate system, so a proportional value is read as that many units.
JUNCTION_SIZE = 1.0
# INFERRED (§5.9 (b)): the container margin when a template sets none.
DEFAULT_MARGIN = 0.5
# INFERRED (§5.9 (c)): how many times a Start Iteration … Stop Iteration sequence may run while it still changes the
# diagram; the pages say it loops while reductions make new candidates, and give no bound.
MAX_ITERATIONS = 100
SYSTEM_JUNCTIONS = "SystemJunctions"
# The source id a connectivity association or structural attachment drawn as a diagram edge is associated with
# (INFERRED, §5.9 (d)); ExportDiagramContent writes it in sourceMapping as "Associations".
ASSOCIATION_SOURCE = -1

TEMPLATE_DEFAULTS = {"description": "", "is_default": False, "keep_vertices": False, "container_margin": DEFAULT_MARGIN,
                     "storage": True, "extension": False, "keep_reflexive": True, "remove_linear_containers": False,
                     "reflexive_radius": 1.0, "persist_aggregations": True, "revision": 0, "steps": []}


def ensure(connection):
    connection.execute("""create table if not exists grapy_nd_template (network text not null, name text not null,
        properties text not null, primary key (network, name))""")
    connection.execute("""create table if not exists grapy_nd_diagram (network text not null, name text not null,
        template text not null, owner text, access text not null, tags text, stored integer not null,
        system integer not null default 0, created text not null, updated text not null, updated_by text,
        template_revision integer not null, signature text not null, inputs text not null, primary key (network, name))""")
    connection.execute("""create table if not exists grapy_nd_feature (network text not null, diagram text not null,
        id integer not null, data text not null, primary key (network, diagram, id))""")
    register(connection, TABLES, EXTENSION, DEFINITION)


def now():
    return datetime.datetime.now().replace(microsecond=0).isoformat(sep=" ")


def user():
    try:
        return getpass.getuser()
    except Exception:  # noqa: BLE001 — no login name in a container is no owner, not a failure
        return ""


def srid_of(network):
    return int(network.q("select srs_id from grapy_feature_datasets where name = ?", network.dataset).fetchone()[0])


def class_path(network, suffix):
    return f"{network.dataset}/{network.name}_{suffix}"


def ensure_classes(path):
    """The three diagram feature classes beside the network, made when they are not there. Made outside an open
    ``Network``, whose connection would hold the file."""
    file, dataset, name = _utility.locate(path)
    connection = _gpkg.connect(file)
    try:
        missing = [(s, k) for s, k in CLASSES if connection.execute(
            "select 1 from gpkg_contents where table_name = ?", (f"{dataset}/{name}_{s}",)).fetchone() is None]
        srid = connection.execute("select srs_id from grapy_feature_datasets where name = ?", (dataset,)).fetchone()
        ensure(connection)
        connection.commit()
    finally:
        connection.close()
    for suffix, shape in missing:
        columns = [_gpkg.Column(n, t) for n, t in (
            ("DIAGRAMNAME", "String"), ("DIAGRAMFEATUREID", "Integer"), ("ASSOCSOURCEID", "Integer"),
            ("ASSOCGLOBALID", "String"), ("CONTAINERID", "Integer"), ("ISROOT", "SmallInteger"),
            ("ISREDUCTION", "SmallInteger"), ("ROTATION", "Double"))]
        if suffix == "DiagramEdges":
            columns += [_gpkg.Column("FROMID", "Integer"), _gpkg.Column("TOID", "Integer")]
        _gpkg.create(f"{file}/{dataset}/{name}_{suffix}", shape, int(srid[0]) if srid else 0, columns)


# ---- templates -----------------------------------------------------------------------------------------------------

def template(network, name, must=True):
    row = network.q("select name, properties from grapy_nd_template where network = ? and lower(name) = lower(?)",
                    network.name, str(name)).fetchone()
    if row is None:
        if must:
            raise ExecuteError(f"ERROR 000732: '{name}' is not a diagram template of '{network.name}'.")
        return None
    found = dict(TEMPLATE_DEFAULTS)
    found.update(json.loads(row[1]))
    found["name"] = row[0]
    return found


def templates(network):
    return [r[0] for r in network.q("select name from grapy_nd_template where network = ? order by rowid", network.name)]


def save_template(network, found, revise=True):
    data = {k: v for k, v in found.items() if k != "name"}
    if revise:
        data["revision"] = int(data.get("revision", 0)) + 1
    network.q("insert or replace into grapy_nd_template values (?, ?, ?)", network.name, found["name"], json.dumps(data))
    found["revision"] = data["revision"]


def add_step(network, template_name, step):
    """A rule, layout or selection appended to a template's steps; its diagrams become inconsistent."""
    found = template(network, template_name)
    found["steps"] = list(found["steps"]) + [step]
    save_template(network, found)
    return found["name"]


# ---- the diagram model ---------------------------------------------------------------------------------------------

class Feature:
    """A diagram feature. ``kind`` is J (junction), E (edge) or C (a container drawn as a polygon round its content);
    ``key`` the network feature (source, global id) it represents, ``span`` an edge element's (start, end) along its
    line, ``xy`` a system junction's location; ``aggregated`` the network features it stands for besides its own."""

    def __init__(self, id_, kind, source=None, globalid=None, span=None, xy=None):
        self.id, self.kind, self.source, self.globalid, self.span, self.xy = id_, kind, source, globalid, span, xy
        self.from_id = self.to_id = self.container = None
        self.shape = None
        self.elements = set()       # graph element ids, during a build
        self.aggregated = []        # [[source, globalid]]
        self.flags = {}             # capabilities: collapse, reduce, polygon, ignore_up, ignore_down
        self.root = False
        self.reduction = False
        self.rotation = 0.0
        self.association = None     # an association's global id when the edge draws one
        self.starting = None        # terminals when a Set Starting Point rule chose it ([] for any)

    @property
    def key(self):
        return None if self.source is None or self.globalid is None else (int(self.source), str(self.globalid))

    def identity(self):
        """What a feature is across rebuilds: kept features keep their positions (OverwriteDiagram, UpdateDiagram)."""
        return (self.kind if self.kind != "C" else "J", self.source, self.globalid,
                tuple(round(s, 9) for s in self.span) if self.span else None,
                tuple(round(c, 6) for c in self.xy) if self.xy else None, self.association,
                tuple(sorted(tuple(a) for a in self.aggregated)) if self.reduction else None)

    def data(self):
        return {"kind": self.kind, "source": self.source, "globalid": self.globalid, "span": self.span, "xy": self.xy,
                "from": self.from_id, "to": self.to_id, "container": self.container, "aggregated": self.aggregated,
                "flags": self.flags, "root": self.root, "reduction": self.reduction, "rotation": self.rotation,
                "association": self.association, "shape": self.shape.wkt if self.shape is not None else None}

    @classmethod
    def from_data(cls, id_, data):
        f = cls(id_, data["kind"], data["source"], data["globalid"], tuple(data["span"]) if data["span"] else None,
                tuple(data["xy"]) if data["xy"] else None)
        f.from_id, f.to_id, f.container = data["from"], data["to"], data["container"]
        f.aggregated, f.flags, f.root = data["aggregated"], data["flags"], data["root"]
        f.reduction, f.rotation, f.association = data["reduction"], data["rotation"], data.get("association")
        f.shape = shapely.from_wkt(data["shape"]) if data.get("shape") else None
        return f


class Diagram:
    def __init__(self, name, template_name):
        self.name, self.template = name, template_name
        self.features: dict[int, Feature] = {}
        self.next_id = 1
        self.owner, self.access, self.tags = user(), "PUBLIC", ""
        self.stored, self.system = False, False
        self.created = self.updated = now()
        self.updated_by = self.owner
        self.template_revision, self.signature = 0, ""
        self.inputs = {"features": [], "trace": None}

    def add(self, kind, **kw):
        f = Feature(self.next_id, kind, **kw)
        self.features[f.id] = f
        self.next_id += 1
        return f

    def junctions(self):
        return [f for f in self.features.values() if f.kind in ("J", "C")]

    def edges(self):
        return [f for f in self.features.values() if f.kind == "E"]

    def incident(self, fid):
        return [e for e in self.edges() if fid in (e.from_id, e.to_id)]

    def neighbours(self, fid):
        """The distinct other junctions an edge connects ``fid`` to: its valence, as the reduce rules count it."""
        out = set()
        for e in self.incident(fid):
            other = e.to_id if e.from_id == fid else e.from_id
            if other != fid:
                out.add(other)
        return out

    def contents(self, fid):
        return [f for f in self.features.values() if f.container == fid]

    def remove(self, fid):
        """A feature removed; its incident edges with it, and its content kept, no longer contained."""
        for e in self.incident(fid):
            self.features.pop(e.id, None)
        for f in self.contents(fid):
            f.container = None
        self.features.pop(fid, None)

    def aggregate(self, into, *others):
        for o in others:
            if o.key is not None and list(o.key) not in into.aggregated and o.key != into.key:
                into.aggregated.append(list(o.key))
            for a in o.aggregated:
                if a not in into.aggregated and tuple(a) != into.key:
                    into.aggregated.append(a)


def load(network, name, must=True):
    row = network.q("select name, template, owner, access, tags, stored, system, created, updated, updated_by, "
                    "template_revision, signature, inputs from grapy_nd_diagram where network = ? and lower(name) = "
                    "lower(?)", network.name, str(name)).fetchone()
    if row is None:
        if must:
            raise ExecuteError(f"ERROR 000732: '{name}' is not a network diagram of '{network.name}'.")
        return None
    d = Diagram(row[0], row[1])
    d.owner, d.access, d.tags, d.stored, d.system = row[2], row[3], row[4] or "", bool(row[5]), bool(row[6])
    d.created, d.updated, d.updated_by, d.template_revision, d.signature = row[7], row[8], row[9], row[10], row[11]
    d.inputs = json.loads(row[12])
    for fid, data in network.q("select id, data from grapy_nd_feature where network = ? and diagram = ? order by id",
                               network.name, d.name):
        d.features[int(fid)] = Feature.from_data(int(fid), json.loads(data))
    d.next_id = max(d.features, default=0) + 1
    return d


def names(network):
    return [r[0] for r in network.q("select name from grapy_nd_diagram where network = ? order by rowid", network.name)]


def delete(network, name):
    network.q("delete from grapy_nd_diagram where network = ? and name = ?", network.name, name)
    network.q("delete from grapy_nd_feature where network = ? and diagram = ?", network.name, name)
    for suffix, _ in CLASSES:
        network.q(f'delete from "{class_path(network, suffix)}" where DIAGRAMNAME = ?', name)


def save(network, d, rename_from=None):
    """A diagram written: its row, its features' links and flags, and its geometry in the three classes."""
    old = rename_from or d.name
    delete(network, old)
    network.q("insert into grapy_nd_diagram values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", network.name, d.name,
              d.template, d.owner, d.access, d.tags, int(d.stored), int(d.system), d.created, d.updated, d.updated_by,
              d.template_revision, d.signature, json.dumps(d.inputs))
    srid = srid_of(network)
    for f in sorted(d.features.values(), key=lambda f: f.id):
        network.q("insert into grapy_nd_feature values (?, ?, ?, ?)", network.name, d.name, f.id, json.dumps(f.data()))
        if f.shape is None:
            continue
        common = (d.name, f.id, f.source, f.globalid, f.container, int(f.root),
                  int(f.reduction), f.rotation)
        if f.kind == "E":
            network.q(f'insert into "{class_path(network, "DiagramEdges")}" (geom, DIAGRAMNAME, DIAGRAMFEATUREID, '
                      'ASSOCSOURCEID, ASSOCGLOBALID, CONTAINERID, ISROOT, ISREDUCTION, ROTATION, FROMID, TOID) values '
                      '(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)', _gpkg.encode(f.shape, srid), *common, f.from_id, f.to_id)
        else:
            suffix = "DiagramContainers" if f.shape.geom_type == "Polygon" else "DiagramJunctions"
            network.q(f'insert into "{class_path(network, suffix)}" (geom, DIAGRAMNAME, DIAGRAMFEATUREID, ASSOCSOURCEID, '
                      'ASSOCGLOBALID, CONTAINERID, ISROOT, ISREDUCTION, ROTATION) values (?, ?, ?, ?, ?, ?, ?, ?, ?)',
                      _gpkg.encode(f.shape, srid), *common)


def temporary_name(network):
    """INFERRED (§5.9 (g)): a temporary diagram is named ``Temporary Diagram``, then ``Temporary Diagram 2`` …"""
    taken = {n.lower() for n in names(network)}
    name, i = "Temporary Diagram", 1
    while name.lower() in taken:
        i += 1
        name = f"Temporary Diagram {i}"
    return name


def signature(network, d):
    """What the diagram was built against: the record of the network features it holds or aggregates, and the
    template's revision. A diagram is consistent while both are what they were (the "Consistency" the pages name)."""
    keys = set()
    for f in d.features.values():
        if f.key:
            keys.add(f.key)
        keys |= {tuple(a) for a in f.aggregated}
    rows = network.q("select globalid, signature from grapy_un_topology_feature where network = ?", network.name)
    parts = sorted((g, s) for g, s in rows if g and any(g.upper() == str(k[1]).upper() for k in keys))
    return hashlib.sha256(json.dumps(parts).encode("utf-8")).hexdigest()[:16]


def consistent(network, d):
    found = template(network, d.template, must=False)
    if found is None:
        return False
    return d.template_revision == found["revision"] and d.signature == signature(network, d)


# ---- building ------------------------------------------------------------------------------------------------------

class Builder:
    """Builds a diagram's content from network elements, runs its template's rules, places it and runs the template's
    automatic layouts."""

    def __init__(self, network, graph=None):
        self.network = network
        self.graph = graph or _utility.build(network)
        self.values = _utility.Values(self.graph)
        self.sources = network.sources()
        self.by_globalid = {g: k for k, (g, _) in self.graph.features.items()}
        self.container_of, self.contents_of = {}, defaultdict(list)
        self.attached_to, self.attachments_of = defaultdict(list), defaultdict(list)
        for row in self.graph.associations:
            a = self.by_globalid.get(str(row["from_globalid"]).upper())
            b = self.by_globalid.get(str(row["to_globalid"]).upper())
            if a is None or b is None:
                continue
            if int(row["type"]) == 2:
                self.container_of[b] = a
                self.contents_of[a].append(b)
            elif int(row["type"]) == 3:
                self.attached_to[b].append(a)
                self.attachments_of[a].append(b)
        self._matches = {}

    # -- names and keys --

    def gkey(self, nkey):
        """The graph's (source, oid) of a network feature (source, global id)."""
        return self.by_globalid.get(str(nkey[1]).upper()) if nkey else None

    def nkey(self, gkey):
        return (gkey[0], self.graph.features[gkey][0])

    def source_ids(self, values):
        """Source ids of class names, paths or layers; ``SystemJunctions`` is source 0, the system junctions and
        system junction objects (the reduce and remove pages)."""
        from ._layers import find
        out = []
        for value in values:
            text = str(value).strip().strip("'\"")
            if text.lower() in ("systemjunctions", "system junctions", "systemjunctionobjects"):
                out.append(0)
                continue
            layer = find(text)
            class_name = self.network.table(layer.source if layer is not None else text)
            out.append(self.network.source_of(class_name))
        return out

    def matching(self, source, where):
        """The graph keys of a source's features a where clause selects."""
        cache = (source, where)
        if cache not in self._matches:
            class_name = self.sources[source][0]
            oid = self.network.oid(class_name)
            sql = f'select "{oid}" from "{class_name}"' + (f" where {where}" if where else "")
            try:
                found = {(source, int(r[0])) for r in self.network.q(sql)}
            except Exception as said:  # noqa: BLE001 — sqlite's message is the user's to read
                raise ExecuteError(f"ERROR 000358: the expression '{where}' is invalid: {said}.") from None
            self._matches[cache] = found
        return self._matches[cache]

    def asset(self, f):
        g = self.gkey(f.key)
        if g is None:
            return None
        data = self.graph.features[g][1]
        return self.sources[g[0]][0], data["group"], data["type"]

    def has_category(self, f, categories):
        found = self.asset(f)
        return found is not None and bool({c.lower() for c in categories} & self.values.categories.get(found, set()))

    def role(self, f):
        if f.source in (None, 0, ASSOCIATION_SOURCE):
            return "SystemJunction" if f.source == 0 else None
        return self.sources[int(f.source)][2]

    def is_junction_source(self, f):
        return f.kind in ("J", "C")

    def network_shape(self, f):
        g = self.gkey(f.key)
        if g is None:
            return None
        wkt = self.graph.features[g][1].get("wkt")
        return shapely.from_wkt(wkt) if wkt else None

    # -- content --

    def index(self, d):
        by_element, by_key = {}, defaultdict(list)
        for f in d.features.values():
            for i in f.elements:
                by_element[i] = f.id
            if f.key:
                by_key[f.key].append(f.id)
            if f.xy is not None:
                by_key[("xy",) + tuple(round(c, 6) for c in f.xy)].append(f.id)
        return by_element, by_key

    def bind(self, d):
        """The graph elements each feature of a loaded diagram represents now."""
        for f in d.features.values():
            f.elements = set()
            if f.xy is not None:
                for e in self.graph.elements:
                    if e.source == 0 and e.kind == "J" and e.shape is not None and \
                            abs(e.shape.x - f.xy[0]) <= _utility.TOLERANCE and abs(e.shape.y - f.xy[1]) <= _utility.TOLERANCE:
                        f.elements.add(e.id)
                continue
            g = self.gkey(f.key)
            if g is None or f.association:
                continue
            for e in self.graph.elements_of(g):
                if f.kind == "E" and e.kind == "E" and f.span and abs(e.start - f.span[0]) < 1e-9:
                    f.elements.add(e.id)
                elif f.kind in ("J", "C") and e.kind == "J":
                    f.elements.add(e.id)

    def junction_for(self, d, i, by_element, by_key):
        """The diagram junction of junction element ``i``: one per junction feature, all its terminals."""
        if i in by_element:
            return d.features[by_element[i]]
        e = self.graph.elements[i]
        if e.feature is None:
            xy = (e.shape.x, e.shape.y) if e.shape is not None else (0.0, 0.0)
            f = d.add("J", source=0, xy=xy)
            f.elements = {i}
        else:
            nkey = self.nkey(e.feature)
            if by_key.get(nkey):
                f = d.features[by_key[nkey][0]]
            else:
                f = d.add("J", source=nkey[0], globalid=nkey[1])
                by_key[nkey].append(f.id)
            f.elements |= {j.id for j in self.graph.elements_of(e.feature) if j.kind == "J"}
        for j in f.elements:
            by_element[j] = f.id
        return f

    def add_elements(self, d, ids):
        """Diagram features for graph elements: a junction feature per junction feature, an edge per edge element with
        the junctions at its two ends brought in (INFERRED, §5.9 (b): a diagram edge connects two diagram junctions)."""
        by_element, by_key = self.index(d)
        added = 0
        for i in sorted(ids):
            if self.graph.elements[i].kind == "J" and i not in by_element:
                self.junction_for(d, i, by_element, by_key)
                added += 1
        for i in sorted(ids):
            e = self.graph.elements[i]
            if e.kind != "E" or i in by_element:
                continue
            starts, ends = self.graph.ends.get(i, ([], []))
            if not starts or not ends:
                continue
            a = self.junction_for(d, starts[0], by_element, by_key)
            b = self.junction_for(d, ends[0], by_element, by_key)
            nkey = self.nkey(e.feature)
            f = d.add("E", source=nkey[0], globalid=nkey[1], span=(e.start, e.end))
            f.from_id, f.to_id, f.elements = a.id, b.id, {i}
            by_element[i] = f.id
            by_key[nkey].append(f.id)
            added += 1
        return added

    def add_features(self, d, gkeys):
        """Diagram features for whole network features; one with no element (a structure boundary, an assembly) is a
        diagram junction of its own."""
        ids = set()
        bare = []
        for g in sorted(gkeys):
            found = self.graph.by_feature.get(g)
            if found:
                ids |= set(found)
            elif g in self.graph.features:
                bare.append(g)
        added = self.add_elements(d, ids)
        _, by_key = self.index(d)
        for g in bare:
            nkey = self.nkey(g)
            if not by_key.get(nkey):
                d.add("J", source=nkey[0], globalid=nkey[1])
                added += 1
        return added

    def link_containers(self, d):
        """CONTAINERID: a feature whose container is in the diagram is its content."""
        _, by_key = self.index(d)
        for f in d.features.values():
            if f.reduction or not f.key:
                continue
            g = self.gkey(f.key)
            holder = self.container_of.get(g)
            if holder is not None:
                ids = by_key.get(self.nkey(holder))
                if ids and ids[0] != f.id:
                    f.container = ids[0]

    def initial(self, d, gkeys):
        """The input features with every container above them (INFERRED, §5.9 (b): the pages fail a diagram on dirty
        areas of "container network features related to" its input, so they are part of it)."""
        wanted = set(gkeys)
        for g in list(wanted):
            holder = self.container_of.get(g)
            while holder is not None and holder not in wanted:
                wanted.add(holder)
                holder = self.container_of.get(holder)
        self.add_features(d, wanted)
        self.link_containers(d)

    # -- the template --

    def run(self, d, found, inputs, trace_locations=None, keep=None):
        """Content from ``inputs`` (graph keys), the template's rules, geometry, and its automatic layouts. ``keep``:
        positions by identity of a diagram being rebuilt."""
        from . import _ndrules
        self.initial(d, inputs)
        _ndrules.run(self, d, found, trace_locations)
        if not found["keep_reflexive"]:
            for e in list(d.edges()):
                if e.association and e.from_id == e.to_id:
                    d.features.pop(e.id)
        if found["remove_linear_containers"]:
            for e in list(d.edges()):
                if d.contents(e.id):
                    d.remove(e.id)
        if not d.features:
            raise ExecuteError("ERROR 002096: no network element remains in the diagram: its input holds no network "
                               "feature, or its template's rules removed them all.")
        self.place(d, found)
        if keep:
            moved = False
            for f in d.features.values():
                if f.identity() in keep and f.kind in ("J",):
                    f.shape = keep[f.identity()]
                    moved = True
            if moved:
                reconnect(d)
                containers(d, found["container_margin"])
        else:
            from . import _ndlayout
            _ndlayout.template_layouts(self, d, found)
        d.template_revision = found["revision"]
        d.signature = signature(self.network, d)

    # -- geometry --

    def junction_position(self, d, f, placed):
        if f.xy is not None:
            return Point(f.xy)
        if f.reduction:
            points = [p for p in (self.position_of_key(tuple(k)) for k in f.aggregated) if p is not None]
            if points:
                return shapely.MultiPoint(points).centroid
        shape = self.network_shape(f)
        if shape is not None and not shape.is_empty:
            return shape if shape.geom_type == "Point" else shape.centroid
        g = self.gkey(f.key)
        for holder in ([self.container_of.get(g)] if g in self.container_of else []) + self.attached_to.get(g, []):
            p = self.position_of_key(self.nkey(holder))
            if p is not None:
                return p
        near = [placed[n] for n in d.neighbours(f.id) if n in placed]
        if near:
            return shapely.MultiPoint(near).centroid
        return Point(0.0, 0.0)

    def position_of_key(self, nkey):
        g = self.gkey(nkey)
        if g is None or not self.graph.features[g][1].get("wkt"):
            return None
        shape = shapely.from_wkt(self.graph.features[g][1]["wkt"])
        return shape if shape.geom_type == "Point" else shape.centroid

    def place(self, d, found, restore_edges=None):
        """Every feature at the geographic position of what it represents (Apply Geo Positions Layout): a junction at its
        feature's point, a reduction junction at the centre of what it aggregates, an object at its container's or
        structure's position or among its neighbours; an edge along its piece of line when vertices are kept, else
        straight; a container round its content."""
        keep_vertices = found["keep_vertices"] if restore_edges is None else restore_edges
        placed = {}
        for f in sorted(d.junctions(), key=lambda f: (bool(f.reduction), f.id)):
            placed[f.id] = self.junction_position(d, f, placed)
            f.shape = placed[f.id]
        for f in d.junctions():
            if f.shape.geom_type != "Point":
                f.shape = placed[f.id]
        for e in d.edges():
            a, b = d.features[e.from_id].shape, d.features[e.to_id].shape
            line = None
            if keep_vertices and not e.reduction and not e.association and e.span is not None:
                piece = self.edge_piece(e)
                if piece is not None and len(piece.coords) > 2:
                    line = LineString([a.coords[0]] + list(piece.coords)[1:-1] + [b.coords[0]])
            if e.association and e.from_id == e.to_id:
                line = loop(a, found["reflexive_radius"], sum(1 for x in d.edges() if x.association and
                                                                  x.from_id == e.from_id and x.id < e.id))
            e.shape = line or LineString([a.coords[0], b.coords[0]])
        containers(d, found["container_margin"])

    def edge_piece(self, e):
        g = self.gkey(e.key)
        if g is None:
            return None
        for el in self.graph.elements_of(g):
            if el.kind == "E" and e.span and abs(el.start - e.span[0]) < 1e-9 and el.shape is not None:
                return el.shape
        return None


def loop(point, radius, level=0):
    """A reflexive edge: a circle through the junction, its radius growing by 0.5 per level (Alter Diagram Template's
    reflexive edge radius), drawn as sixteen segments (INFERRED, §5.9 (f))."""
    r = float(radius) + 0.5 * level
    cx, cy = point.x, point.y + r
    pts = [(cx + r * math.sin(2 * math.pi * k / 16), cy - r * math.cos(2 * math.pi * k / 16)) for k in range(16)]
    return LineString(pts + [pts[0]])


def reconnect(d):
    """Every edge's first and last vertex on its junctions, after junctions moved."""
    for e in d.edges():
        a, b = d.features[e.from_id].shape, d.features[e.to_id].shape
        if a is None or b is None:
            continue
        a, b = point_of(a), point_of(b)
        coords = list(e.shape.coords) if e.shape is not None else []
        if e.association and e.from_id == e.to_id and coords:
            dx, dy = a.x - coords[0][0], a.y - coords[0][1]
            e.shape = LineString([(x + dx, y + dy) for x, y in coords])
            continue
        middle = coords[1:-1] if len(coords) > 2 else []
        e.shape = LineString([a.coords[0]] + middle + [b.coords[0]])


def point_of(shape):
    if shape.geom_type == "Point":
        return shape
    return shape.centroid


def containers(d, margin):
    """A container with visible content is the rectangle round its content, ``margin`` from it (the container margin
    of Alter Diagram Template); one with none is a point at its own position, or — with the Save Empty Container As
    Polygon If Unconnected capability and no connection — a square of the margin round it."""
    margin = float(margin)
    order = sorted(d.junctions(), key=lambda f: -_depth(d, f))
    for f in order:
        inside = [c for c in d.contents(f.id) if c.shape is not None]
        if f.kind == "E":
            continue
        if inside:
            bounds = shapely.GeometryCollection([c.shape for c in inside]).bounds
            f.shape = box(bounds[0] - margin, bounds[1] - margin, bounds[2] + margin, bounds[3] + margin)
            f.kind = "C"
        else:
            centre = point_of(f.shape) if f.shape is not None else Point(0, 0)
            if f.flags.get("polygon") and not d.neighbours(f.id):
                f.shape = box(centre.x - margin, centre.y - margin, centre.x + margin, centre.y + margin)
                f.kind = "C"
            else:
                f.shape = centre
                f.kind = "J"


def _depth(d, f):
    n, seen = 0, set()
    while f.container is not None and f.container in d.features and f.id not in seen:
        seen.add(f.id)
        f = d.features[f.container]
        n += 1
    return n


# ---- checks the pages make -----------------------------------------------------------------------------------------

def check_dirty(builder, gkeys, tool):
    """The tool fails on dirty areas of its network features or their containers (every diagram page)."""
    _utility.write_dirty_areas(builder.network)
    dirty = _utility.dirty_keys(builder.network)
    wanted = set(gkeys)
    for g in list(wanted):
        holder = builder.container_of.get(g)
        while holder is not None and holder not in wanted:
            wanted.add(holder)
            holder = builder.container_of.get(holder)
    hit = dirty & wanted
    if hit:
        raise ExecuteError(f"ERROR 002087: {tool}: {len(hit)} network feature(s) of the diagram carry dirty areas; "
                           "validate the network topology first.")


def features_of(d):
    """The graph-independent network features a diagram holds or aggregates, as (source, global id)."""
    out = set()
    for f in d.features.values():
        if f.key and not f.association:
            out.add(f.key)
        out |= {tuple(a) for a in f.aggregated}
    return out


def keys_of(builder, d):
    return {builder.gkey(k) for k in features_of(d)} - {None}

