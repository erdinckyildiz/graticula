"""Disposable prototype (ADR-288): write a mobile geodatabase from a feature service's REST description and rows,
with plain SQLite, generating every per-layer statement and registry row. Only format constants come from a captured
file: the item-type and relationship-type tables and the system tables' CREATE statements (the production writer
carries those as its own constants).

Usage: writer_proto.py <service.json> <out.geodatabase> [--registered REPLICA_NAME SERVICE_URL OWNER]
service.json: {"service": {...REST...}, "layers": [{"doc": {...REST layer...}, "features": [...REST f=json...],
               "attachments": [{"parentGlobalId":..., "globalId":..., "name":..., "contentType":..., "data_hex":...}]}]}
"""
import sqlite3, sys, os, json, uuid, math, re, datetime
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import stgeom as g

CONST = r"D:\temp\offline-capture\prod\cap1.perLayer.geodatabase"
FOLD = lambda a, b: (a.casefold() > b.casefold()) - (a.casefold() < b.casefold())
NS = "xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:xs='http://www.w3.org/2001/XMLSchema' xmlns:typens='http://www.esri.com/schemas/ArcGIS/10.8'"
FAR = "julianday ('9999-12-31 23:59:59')"
SYSTEM = ["GDB_Version", "st_aux_spatial_reference_systems", "st_geometry_columns", "GDB_Layers", "GDB_Metadata",
          "GDB_TableRegistry", "GDB_Locators", "GDB_ColumnRegistry", "GDB_TablesModified", "GDB_RowidGenerators",
          "GDB_Tables_Last_Modified", "GDB_Items", "GDB_ItemRelationships", "GDB_ItemTypes", "GDB_ItemRelationshipTypes",
          "GDB_ReplicaLog", "GDB_EditingTemplates", "GDB_EditingTemplateRelationships", "GDB_TriggerWork", "GDB_ServiceItems"]

def gid(): return "{" + str(uuid.uuid4()).upper() + "}"
def esc(s): return (s or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("'", "&apos;").replace('"', "&quot;")
def julian(ms): return ms / 86400000.0 + 2440587.5
def now_julian(): return julian(datetime.datetime.now(datetime.timezone.utc).timestamp() * 1000)

# ---- field types: REST type -> (declared sqlite type, check pattern, xml FieldType, sde_type) ----
def coldef(f, archived):
    n, t, nullable = f["name"], f["type"], f.get("nullable", True)
    def chk(cond, typ):
        return f"check({cond})" if not nullable else f"check((typeof({n}) = '{typ}' or typeof({n}) = 'null'){cond[len(f'typeof({n}) = {chr(39)}{typ}{chr(39)}'):]})"
    if t == "esriFieldTypeOID":
        return f"{n} int32 check(typeof({n}) = 'integer' and {n} >= -2147483648 and {n} <= 2147483647) not null" if archived else f"{n} integer primary key not null"
    if t in ("esriFieldTypeGlobalID",):
        return f"{n} uuidtext collate nocase check(typeof({n}) = 'text' and length({n}) = 38) not null"
    if t == "esriFieldTypeGUID":
        base = f"typeof({n}) = 'text' and length({n}) = 38"
        return f"{n} uuidtext collate nocase " + (f"check({base}) not null" if not nullable else f"check((typeof({n}) = 'text' or typeof({n}) = 'null') and length({n}) = 38)")
    if t == "esriFieldTypeString":
        L = f.get("length") or 255
        return f"{n} text({L}) " + (f"check(typeof({n}) = 'text' and not length({n}) > {L}) not null" if not nullable else f"check((typeof({n}) = 'text' or typeof({n}) = 'null') and not length({n}) > {L})")
    ints = {"esriFieldTypeSmallInteger": ("int16", 32768), "esriFieldTypeInteger": ("int32", 2147483648)}
    if t in ints:
        d, b = ints[t]
        r = f"{n} >= -{b} and {n} <= {b - 1}"
        return f"{n} {d} " + (f"check(typeof({n}) = 'integer' and {r}) not null" if not nullable else f"check((typeof({n}) = 'integer' or typeof({n}) = 'null') and {r})")
    if t in ("esriFieldTypeDouble", "esriFieldTypeSingle"):
        d = "float64" if t == "esriFieldTypeDouble" else "float32"
        return f"{n} {d} " + (f"check(typeof({n}) = 'real') not null" if not nullable else f"check(typeof({n}) = 'real' or typeof({n}) = 'null')")
    if t == "esriFieldTypeDate":
        return f"{n} realdate " + (f"check(typeof({n}) = 'real' and {n} >= 0.0) not null" if not nullable else f"check((typeof({n}) = 'real' or typeof({n}) = 'null') and {n} >= 0.0)")
    if t == "esriFieldTypeBlob":
        return f"{n} blob check(typeof({n}) = 'blob' or typeof({n}) = 'null')"
    raise ValueError(f"field type {t} not handled")

def shapedef(n):
    return f"{n} geometryblob check((typeof({n}) = 'blob' and length({n}) >= 18 and cast(hex(substr({n},1,1)) as integer) = 64) or typeof({n}) = 'null')"

ARCH_COLS = (", gdb_archive_oid integer primary key not null, gdb_from_date realdate check(typeof(gdb_from_date) = 'real' and gdb_from_date >= 0.0) not null default (gdb_transaction_time ()), "
             f"gdb_to_date realdate check(typeof(gdb_to_date) = 'real' and gdb_to_date >= 0.0) not null default ({FAR})")

def archive_objects(table, oid, cols):
    c = ",".join(cols); newc = ",".join("NEW." + x for x in cols)
    cur = f"gdb_to_date BETWEEN ({FAR} - 0.000000001) AND ({FAR} + 0.000000001)"
    out = []
    for v, where in (("_evw", cur), ("_evw2", "gdb_from_date <=  (gdb_undo_start_moment()) AND gdb_to_date > (gdb_undo_start_moment())")):
        out.append(f"CREATE VIEW {table}{v} AS SELECT {c} FROM {table} WHERE {where}")
    for v in ("_evw", "_evw2"):
        ins_vals = ",".join([f"IFNULL (NEW.{oid},Next_RowID (NULL,'{table}'))"] + ["NEW." + x for x in cols[1:]])
        out.append(f"CREATE TRIGGER {table}{v}_insert INSTEAD OF INSERT ON {table}{v} BEGIN INSERT INTO {table} ({c},gdb_from_date,gdb_to_date) VALUES ({ins_vals},gdb_transaction_time (),{FAR}); END")
    for v in ("_evw", "_evw2"):
        out.append(f"CREATE TRIGGER {table}{v}_delete INSTEAD OF DELETE ON {table}{v} BEGIN DELETE FROM {table} WHERE {oid} = OLD.{oid} AND gdb_from_date BETWEEN (gdb_transaction_time () - 0.000000001) AND (gdb_transaction_time () + 0.000000001); DELETE FROM {table} WHERE {oid} = OLD.{oid} AND gdb_to_date BETWEEN (gdb_transaction_time () - 0.000000001) AND (gdb_transaction_time () + 0.000000001); UPDATE {table} SET gdb_to_date = gdb_transaction_time () WHERE {oid} = OLD.{oid} AND {cur}; END")
    for v in ("_evw", "_evw2"):
        out.append(f"CREATE TRIGGER {table}{v}_update INSTEAD OF UPDATE ON {table}{v} BEGIN UPDATE OR IGNORE {table} SET gdb_to_date = gdb_transaction_time () WHERE {oid} = OLD.{oid} AND {cur};DELETE FROM {table} WHERE {oid} = OLD.{oid} AND {cur};DELETE FROM GDB_TriggerWork;INSERT INTO GDB_TriggerWork SELECT IFNULL ((SELECT MAX (gdb_to_date) FROM {table} WHERE {oid} = OLD.{oid} AND gdb_to_date < {FAR}),gdb_transaction_time ());DELETE FROM {table} WHERE {oid} = OLD.{oid} AND gdb_from_date = (SELECT gdb_to_date FROM GDB_TriggerWork);INSERT INTO {table} ({c},gdb_from_date,gdb_to_date) VALUES ({newc},(SELECT gdb_to_date FROM GDB_TriggerWork),{FAR}); END")
    return out

def spindex_objects(table, col):
    idx = f"st_spindex__{table}_{col}"
    return idx, [
        f"CREATE VIRTUAL TABLE [{idx}] USING RTREE (pkid,minx,maxx,miny,maxy)",
    ], [
        f"CREATE TRIGGER [st_insert_trigger_{table}_{col}] AFTER INSERT ON [{table}] FOR EACH ROW BEGIN SELECT InsertIndexEntry ('{idx}',NEW.{col},NEW._ROWID_,2); END",
        f"CREATE TRIGGER [st_delete_trigger_{table}_{col}] AFTER DELETE ON [{table}] FOR EACH ROW BEGIN DELETE FROM {idx} WHERE pkid = OLD._ROWID_; END",
        f"CREATE TRIGGER [st_update_trigger_{table}_{col}] AFTER UPDATE OF {col} ON [{table}] WHEN OLD._ROWID_ = NEW._ROWID_ BEGIN SELECT UpdateIndexEntry ('{idx}',NEW.{col},NEW._ROWID_,2); END",
        f"CREATE TRIGGER [st_update1_trigger_{table}_{col}] AFTER UPDATE ON [{table}] WHEN OLD._ROWID_ != NEW._ROWID_ BEGIN DELETE FROM [{idx}] WHERE pkid = OLD._ROWID_; SELECT UpdateIndexEntry ('{idx}',NEW.{col},NEW._ROWID_,2); END",
    ]

GTYPES = {"esriGeometryPoint": ("point", 1, 1), "esriGeometryMultipoint": ("multipoint", 4, 2), "esriGeometryPolyline": ("polyline", 5, 3), "esriGeometryPolygon": ("polygon", 6, 4)}

def eflags(gt, z, m):
    low = {"esriGeometryPoint": 0x83, "esriGeometryMultipoint": 0x83, "esriGeometryPolyline": 0x8D, "esriGeometryPolygon": 0x91}[gt]
    return 0x4400000 | (0 if gt == "esriGeometryPoint" else 0x40000) | (0x10000 if z else 0) | (0x80000 if m else 0) | low

def sr_xml(wkid, wkt, grid, tag="SpatialReference"):
    geographic = wkid in (4326,)
    kind = "GeographicCoordinateSystem" if geographic else "ProjectedCoordinateSystem"
    latest = 3857 if wkid == 102100 else wkid
    wk = 102100 if wkid in (3857, 102100) else wkid
    return (f"<{tag} xsi:type='typens:{kind}'><WKT>{esc(wkt)}</WKT><XOrigin>{grid.fx:g}</XOrigin><YOrigin>{grid.fy:g}</YOrigin><XYScale>{grid.u:g}</XYScale>"
            f"<ZOrigin>{grid.fz:g}</ZOrigin><ZScale>{grid.zu:g}</ZScale><MOrigin>{grid.fm:g}</MOrigin><MScale>{grid.mu:g}</MScale>"
            f"<XYTolerance>{0.000000008983152841195215 if geographic else 0.001}</XYTolerance><ZTolerance>0.001</ZTolerance><MTolerance>0.001</MTolerance><HighPrecision>true</HighPrecision>"
            f"<WKID>{wk}</WKID><LatestWKID>{latest}</LatestWKID></{tag}>")

WKT_3857 = 'PROJCS["WGS_1984_Web_Mercator_Auxiliary_Sphere",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Mercator_Auxiliary_Sphere"],PARAMETER["False_Easting",0.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",0.0],PARAMETER["Standard_Parallel_1",0.0],PARAMETER["Auxiliary_Sphere_Type",0.0],UNIT["Meter",1.0],AUTHORITY["EPSG",3857]]'
WKT_4326 = 'GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433],AUTHORITY["EPSG",4326]]'

def field_xml(f, domain_names):
    t = f["type"]
    parts = [f"<Name>{esc(f['name'])}</Name>"]
    if f.get("alias") and t not in ("esriFieldTypeGeometry",) and f.get("_alias", True): parts.append(f"<AliasName>{esc(f['alias'])}</AliasName>")
    dn = domain_names.get(f["name"])
    if dn: parts.append(f"<DomainName>{esc(dn)}</DomainName>")
    parts.append(f"<FieldType>{t}</FieldType>")
    parts.append(f"<IsNullable>{'true' if f.get('nullable', True) and t not in ('esriFieldTypeOID','esriFieldTypeGlobalID') else 'false'}</IsNullable>")
    if t in ("esriFieldTypeOID", "esriFieldTypeGlobalID"): parts.append("<Required>true</Required><Editable>false</Editable>")
    if t == "esriFieldTypeGeometry": parts.append("<Required>true</Required>")
    return "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'>" + "".join(parts) + "</GPFieldInfoEx>"

def common_tail(archived, hasglobal, gidname):
    return (f"<CLSID>{{CLSID}}</CLSID><EXTCLSID></EXTCLSID><RelationshipClassNames xsi:type='typens:Names'></RelationshipClassNames><AliasName></AliasName><ModelName></ModelName>"
            f"<HasGlobalID>{'true' if hasglobal else 'false'}</HasGlobalID><GlobalIDFieldName>{gidname}</GlobalIDFieldName><RasterFieldName></RasterFieldName>"
            "<ExtensionProperties xsi:type='typens:PropertySet'><PropertyArray xsi:type='typens:ArrayOfPropertySetProperty'></PropertyArray></ExtensionProperties>"
            "<ControllerMemberships xsi:type='typens:ArrayOfControllerMembership'></ControllerMemberships><EditorTrackingEnabled>false</EditorTrackingEnabled>"
            "<CreatorFieldName></CreatorFieldName><CreatedAtFieldName></CreatedAtFieldName><EditorFieldName></EditorFieldName><EditedAtFieldName></EditedAtFieldName><IsTimeInUTC>true</IsTimeInUTC>")

def mercator_to_wgs84(x, y):
    lon = x / 6378137.0 * 180 / math.pi
    lat = (2 * math.atan(math.exp(y / 6378137.0)) - math.pi / 2) * 180 / math.pi
    return lon, lat

def main():
    spec = json.load(open(sys.argv[1], encoding="utf-8"))
    out = sys.argv[2]
    registered = "--registered" in sys.argv
    if registered:
        i = sys.argv.index("--registered"); replica_name, service_url, owner = sys.argv[i + 1:i + 4]
    if os.path.exists(out): os.remove(out)
    k = sqlite3.connect(f"file:{CONST}?mode=ro", uri=True); k.create_collation("ICUFOLDCASE", FOLD)
    d = sqlite3.connect(out); d.create_collation("ICUFOLDCASE", FOLD)
    d.execute("pragma page_size=4096"); d.execute("pragma application_id=1165193833")
    sysddl = {n: s for n, s in k.execute("select name, sql from sqlite_master where sql is not null")}
    # system tables, indexes (those on system tables), GDB_Items spatial index
    for n in SYSTEM: d.execute(sysddl[n])
    for n, s in k.execute("select name, sql from sqlite_master where type='index' and sql is not null"):
        tbl = k.execute("select tbl_name from sqlite_master where name=?", (n,)).fetchone()[0]
        if tbl in SYSTEM: d.execute(s)
    d.execute("CREATE VIRTUAL TABLE [st_spindex__GDB_Items_Shape] USING RTREE (pkid,minx,maxx,miny,maxy)")
    for n in ("ItemTypes", "ItemRelationshipTypes"):
        rows = k.execute(f"select * from GDB_{n}").fetchall()
        d.executemany(f"insert into GDB_{n} values ({','.join('?' * len(rows[0]))})", rows)
    d.execute("insert into GDB_Version values (12, 1, 0, 'Graticula', 109504, 93001)")
    d.execute("insert into st_aux_spatial_reference_systems values (300000, 'ESRI', 300000, 'UNKNOWN', -450359962737.05, -450359962737.05, 10000.0, -100000.0, 10000.0, -100000.0, 10000.0, 1e-06, 0.001, 0.001, 1)")
    T = {n: u for n, u in k.execute("select Name, UUID from GDB_ItemTypes")}
    R = {n: u for n, u in k.execute("select Name, UUID from GDB_ItemRelationshipTypes")}
    now_epoch = int(datetime.datetime.now().timestamp())
    reg_id = [0]
    def register(table, rowid_col, flags):
        reg_id[0] += 1
        d.execute("insert into GDB_TableRegistry values (?,?,?,?,?,?,?,?,?)", (reg_id[0], table, rowid_col, None, flags, 0, now_epoch, 1, None))
        return reg_id[0]
    for t, rc, fl in (("GDB_Tables_Last_Modified", None, 0), ("GDB_Items", "ObjectID", 4210695), ("GDB_ItemRelationships", "ObjectID", 4194307),
                      ("GDB_ItemTypes", "ObjectID", 3), ("GDB_ItemRelationshipTypes", "ObjectID", 3), ("GDB_ReplicaLog", "ID", 3),
                      ("GDB_EditingTemplates", "ObjectID", 3), ("GDB_EditingTemplateRelationships", "ObjectID", 3)):
        register(t, rc, fl)
    for t in ("GDB_ITEMS", "GDB_ITEMRELATIONSHIPS", "GDB_ITEMTYPES", "GDB_ITEMRELATIONSHIPTYPES", "GDB_REPLICALOG"):
        d.execute("insert into GDB_Tables_Last_Modified values (?, 0)", (t,))
    items = []  # (uuid, type, name, phys, path, url, props, ds1, ds2, di1, di2, definition, shape)
    rels = []   # (type, origin, dest)
    def item(typ, name, path, defn=None, url="", props=1, ds1=None, ds2=None, di1=None, di2=None, shape=None, phys=None, u=None):
        u = u or gid()
        items.append((u, T[typ], name, phys if phys is not None else name.upper(), path, url, props, ds1, ds2, di1, di2, defn, shape))
        return u
    root = item("Folder", "", "\\", url=None, phys="")
    item("Workspace", "Workspace", "", f"<DEWorkspace xsi:type='typens:DEWorkspace' {NS}><CatalogPath>\\</CatalogPath><Name></Name><ChildrenExpanded>false</ChildrenExpanded><WorkspaceType>esriRemoteDatabaseWorkspace</WorkspaceType><WorkspaceFactoryProgID></WorkspaceFactoryProgID><ConnectionString></ConnectionString><ConnectionInfo xsi:nil='true'/><Domains xsi:type='typens:ArrayOfDomain'></Domains><MajorVersion>3</MajorVersion><MinorVersion>0</MinorVersion><BugfixVersion>0</BugfixVersion><Realm></Realm><User>{esc(owner if registered else '')}</User><MaxAttributeRuleID>0</MaxAttributeRuleID></DEWorkspace>", props=0, ds1=1, phys="WORKSPACE")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S")
    item("Historical Marker", "DEFAULT", "", f"<GPHistoricalMarker xsi:type='typens:GPHistoricalMarker' {NS}><Name>DEFAULT</Name><TimeStamp xsi:type='xs:dateTime'>{stamp}</TimeStamp></GPHistoricalMarker>")
    domains = {}  # name -> uuid
    svcname = spec["service"].get("_name", "service")
    dsid = [len(items)]
    sync_datasets = []
    gc_id = [1]
    d.execute("insert into st_geometry_columns (gcid, f_table_name, f_geometry_column, geometry_type, coord_dimension, srid, spatial_index_type) values (1, 'GDB_Items', 'Shape', 6, 2, 4326, 'RTREEXY')")
    layer_seq = [1]
    d.execute("insert into GDB_Layers values (1, NULL, 'GDB_Items', 'Shape', 71565457, 0, 0, 0, 0, 0, NULL, NULL, NULL, NULL, ?, 1, 4326, 0)", (now_epoch,))
    post = []      # statements after data
    gen = int(datetime.datetime.now().timestamp() * 1000)
    for L in spec["layers"]:
        doc = L["doc"]; lname = doc["name"]; table = f"{svcname}_{lname}"
        fields = doc["fields"]; oid = doc["objectIdField"]; gidf = doc.get("globalIdField")
        gt = doc.get("geometryType"); is_fc = bool(gt)
        z, m = bool(doc.get("hasZ")), bool(doc.get("hasM"))
        wkid = (doc.get("extent", {}).get("spatialReference", {}) or {}).get("latestWkid") or (doc.get("extent", {}).get("spatialReference", {}) or {}).get("wkid") or 3857
        geographic = wkid == 4326
        grid = g.Grid(-400, -400, 1e9) if geographic else g.Grid(-20037700, -30241100, 10000)
        wkt = WKT_4326 if geographic else WKT_3857
        # domains
        dn = {}
        for f in fields:
            dom = f.get("domain")
            if not dom: continue
            name = f"{svcname}_{dom['name']}"
            dn[f["name"]] = name
            if name in domains: continue
            ft = f["type"]
            if dom["type"] == "codedValue":
                xs = {"esriFieldTypeSmallInteger": "xs:short", "esriFieldTypeInteger": "xs:int", "esriFieldTypeString": "xs:string", "esriFieldTypeDouble": "xs:double"}[ft]
                cv = "".join(f"<CodedValue xsi:type='typens:CodedValue'><Name>{esc(c['name'])}</Name><Code xsi:type='{xs}'>{esc(str(c['code']))}</Code></CodedValue>" for c in dom["codedValues"])
                domains[name] = item("Coded Value Domain", name, "", f"<GPCodedValueDomain2 xsi:type='typens:GPCodedValueDomain2' {NS}><DomainName>{esc(name)}</DomainName><FieldType>{ft}</FieldType><MergePolicy>esriMPTDefaultValue</MergePolicy><SplitPolicy>esriSPTDefaultValue</SplitPolicy><Description></Description><Owner></Owner><CodedValues xsi:type='typens:ArrayOfCodedValue'>{cv}</CodedValues></GPCodedValueDomain2>")
            else:
                lo, hi = dom["range"]
                domains[name] = item("Range Domain", name, "", f"<GPRangeDomain2 xsi:type='typens:GPRangeDomain2' {NS}><DomainName>{esc(name)}</DomainName><FieldType>{ft}</FieldType><MergePolicy>esriMPTDefaultValue</MergePolicy><SplitPolicy>esriSPTDefaultValue</SplitPolicy><Description></Description><Owner></Owner><MaxValue xsi:type='xs:double'>{hi:g}</MaxValue><MinValue xsi:type='xs:double'>{lo:g}</MinValue></GPRangeDomain2>")
        # table DDL
        shape = "shape"
        cols_sql = [coldef(f, registered) for f in fields if f["type"] != "esriFieldTypeGeometry"]
        if is_fc: cols_sql.append(shapedef(shape))
        ddl = f"CREATE TABLE {table} (" + ", ".join(cols_sql) + (ARCH_COLS if registered else "") + ")"
        d.execute(ddl)
        colnames = [f["name"] for f in fields if f["type"] != "esriFieldTypeGeometry"] + ([shape] if is_fc else [])
        rid = register(table, oid, (0x4007 if is_fc else 0x3) | (0x40000 if registered else 0))
        if gidf:
            post.append(f"CREATE UNIQUE INDEX [UUID_{rid}] ON {table} ({gidf} ASC{',gdb_to_date ASC' if registered else ''})")
        if registered:
            post += [f"CREATE UNIQUE INDEX [r{rid}_gdb_xpk] ON {table} ({oid} ASC,gdb_to_date ASC)", f"CREATE INDEX [gdb_ct1_{rid}] ON {table} (gdb_from_date ASC,gdb_to_date ASC)", f"CREATE UNIQUE INDEX [gdb_ct4_{rid}] ON {table} ({oid} ASC,gdb_from_date ASC)"]
            post += archive_objects(table, oid, colnames)
        # rows
        xs_, ys_, zs_ = [], [], []
        maxoid = 0
        rows = []
        for feat in L["features"]:
            a = feat["attributes"]; vals = []
            for f in fields:
                if f["type"] == "esriFieldTypeGeometry": continue
                v = a.get(f["name"])
                if f["type"] == "esriFieldTypeDate" and v is not None: v = julian(v)
                vals.append(v)
            maxoid = max(maxoid, a[oid])
            if is_fc:
                geo = feat.get("geometry"); blob = None
                if geo:
                    kind = GTYPES[gt][0]
                    if kind == "point":
                        pt = (geo["x"], geo["y"]) + ((geo.get("z", 0),) if z else ()) + ((geo.get("m", 0),) if m else ())
                        P = [[pt]]; xs_.append(geo["x"]); ys_.append(geo["y"]); zs_ += [geo.get("z")] if z else []
                    elif kind == "polyline":
                        P = [[tuple(p) for p in path] for path in geo["paths"]]
                        for path in geo["paths"]:
                            for p in path: xs_.append(p[0]); ys_.append(p[1])
                    elif kind == "polygon":
                        # ArcGIS JSON rings: clockwise = outer, counter-clockwise = hole; stored OGC way (outer CCW)
                        def area(r): return sum(r[i][0] * r[i + 1][1] - r[i + 1][0] * r[i][1] for i in range(len(r) - 1)) / 2
                        P = []
                        for r in geo["rings"]:
                            for p in r: xs_.append(p[0]); ys_.append(p[1])
                            rr = [tuple(p) for p in r]
                            if area(rr) < 0: P.append([list(reversed(rr))])      # outer, CW in JSON -> CCW
                            else: P[-1].append(list(reversed(rr)))               # hole, CCW in JSON -> CW
                    blob = g.encode(kind, P, wkid if wkid != 102100 else 3857, grid, z, m)
                vals.append(blob)
            rows.append(vals)
        if rows:
            colsql = ",".join(colnames) + (",gdb_from_date,gdb_to_date" if registered else "")
            ph = ",".join("?" * len(colnames)) + (",?,?" if registered else "")
            base_from = 2440587.500011574  # the epoch plus one second, as the server's file has it
            far = 5373484.499988426
            d.executemany(f"insert into {table} ({colsql}) values ({ph})", [r + ([base_from, far] if registered else []) for r in rows])
        d.execute("insert into GDB_RowidGenerators values (?, ?, -1, ?)", (rid, maxoid + 1 if rows else 1, maxoid if rows else 1))
        defn_fields = "".join(field_xml(f, dn) for f in fields if f["type"] != "esriFieldTypeGeometry") + (field_xml({"name": shape, "type": "esriFieldTypeGeometry"}, {}) if is_fc else "")
        dsid_now = len(items) + 1
        if is_fc:
            kind, ctype, esritype = GTYPES[gt]
            gc_id[0] += 1
            d.execute("insert into st_geometry_columns (gcid, f_table_name, f_geometry_column, geometry_type, coord_dimension, srid, spatial_index_type) values (?,?,?,?,?,?, 'RTREEXY')",
                      (gc_id[0], table, shape, ctype + (1000 if z else 0) + (2000 if m else 0), 2 + z + m, 3857 if wkid == 102100 else wkid))
            idx, vt, trg = spindex_objects(table, shape)
            for s in vt: d.execute(s)
            # fill the R*Tree directly (the device's triggers keep it afterwards)
            for (rowid, blob) in d.execute(f"select _rowid_, {shape} from {table}").fetchall():
                if blob is None: continue
                _, k2, hz, hm, raw = g.decode(blob)
                pts = [r for r in raw if r is not None]
                xs = [grid.ux(r[0]) for r in pts]; ys = [grid.uy(r[1]) for r in pts]
                d.execute(f"insert into {idx} values (?,?,?,?,?)", (rowid, min(xs), max(xs), min(ys), max(ys)))
            post += trg
            layer_seq[0] += 1
            de = doc.get("extent") or {}
            ext = (min(xs_), min(ys_), max(xs_), max(ys_)) if xs_ else (de.get("xmin", 0), de.get("ymin", 0), de.get("xmax", 0), de.get("ymax", 0))
            d.execute("insert into GDB_Layers values (?, NULL, ?, ?, ?, 0, ?, ?, ?, ?, ?, NULL, ?, NULL, ?, 1, ?, 0)",
                      (layer_seq[0], table, shape, eflags(gt, z, m), *ext, min(zs_) if zs_ else None, max(zs_) if zs_ else None, now_epoch, 3857 if wkid == 102100 else wkid))
            extent_xml = ""
            if xs_:
                extent_xml = (f"<Extent xsi:type='typens:EnvelopeN'><XMin>{ext[0]:g}</XMin><YMin>{ext[1]:g}</YMin><XMax>{ext[2]:g}</XMax><YMax>{ext[3]:g}</YMax>"
                              + (f"<ZMin>{min(zs_):g}</ZMin><ZMax>{max(zs_):g}</ZMax>" if zs_ else "") + sr_xml(wkid, wkt, grid) + "</Extent>")
            defn = (f"<DEFeatureClassInfo xsi:type='typens:DEFeatureClassInfo' {NS}><CatalogPath>\\main.{table}</CatalogPath><Name>main.{table}</Name><ChildrenExpanded>false</ChildrenExpanded><DatasetType>esriDTFeatureClass</DatasetType><DSID>{dsid_now}</DSID><Versioned>false</Versioned><CanVersion>false</CanVersion><ConfigurationKeyword></ConfigurationKeyword><RequiredGeodatabaseClientVersion>10.2</RequiredGeodatabaseClientVersion><HasOID>true</HasOID><OIDFieldName>{oid}</OIDFieldName>"
                    f"<GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'>{defn_fields}</GPFieldInfoExs>" + common_tail(registered, bool(gidf), gidf or "").replace("{CLSID}", "{52353152-891A-11D0-BEC6-00805F7C4268}")
                    + f"<FeatureType>esriFTSimple</FeatureType><ShapeType>{gt}</ShapeType><ShapeFieldName>{shape}</ShapeFieldName><HasM>{'true' if m else 'false'}</HasM><HasZ>{'true' if z else 'false'}</HasZ><HasSpatialIndex>false</HasSpatialIndex><AreaFieldName></AreaFieldName><LengthFieldName></LengthFieldName>"
                    + extent_xml + sr_xml(wkid, wkt, grid) + f"<ChangeTracked>{'true' if registered else 'false'}</ChangeTracked><FieldFilteringEnabled>false</FieldFilteringEnabled><FilteredFieldNames xsi:type='typens:Names'></FilteredFieldNames><ReplicaTracked>false</ReplicaTracked></DEFeatureClassInfo>")
            ishape = None
            if xs_:
                lo = mercator_to_wgs84(ext[0], ext[1]) if not geographic else (ext[0], ext[1])
                hi = mercator_to_wgs84(ext[2], ext[3]) if not geographic else (ext[2], ext[3])
                ishape = g.encode("polygon", [[[(lo[0], lo[1]), (hi[0], lo[1]), (hi[0], hi[1]), (lo[0], hi[1]), (lo[0], lo[1])]]], 4326, g.Grid(-400, -400, 1e9))
            fc_uuid = item("Feature Class", f"main.{table}", f"\\main.{table}", defn, ds1=1, ds2=esritype, di1=shape, shape=ishape)
        else:
            defn = (f"<DETableInfo xsi:type='typens:DETableInfo' {NS}><CatalogPath>\\main.{table}</CatalogPath><Name>main.{table}</Name><ChildrenExpanded>false</ChildrenExpanded><DatasetType>esriDTTable</DatasetType><DSID>{dsid_now}</DSID><Versioned>false</Versioned><CanVersion>false</CanVersion><ConfigurationKeyword></ConfigurationKeyword><RequiredGeodatabaseClientVersion>10.2</RequiredGeodatabaseClientVersion><HasOID>true</HasOID><OIDFieldName>{oid}</OIDFieldName>"
                    f"<GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'>{defn_fields}</GPFieldInfoExs>" + common_tail(registered, bool(gidf), gidf or "").replace("{CLSID}", "{7A566981-C114-11D2-8A28-006097AFF44E}")
                    + f"<ChangeTracked>{'true' if registered else 'false'}</ChangeTracked><FieldFilteringEnabled>false</FieldFilteringEnabled><FilteredFieldNames xsi:type='typens:Names'></FilteredFieldNames><ReplicaTracked>false</ReplicaTracked></DETableInfo>")
            fc_uuid = item("Table", f"main.{table}", f"\\main.{table}", defn)
        rels.append(("DatasetInFolder", root, fc_uuid))
        for dname in sorted(set(dn.values())): rels.append(("DomainInDataset", fc_uuid, domains[dname]))
        sync_datasets.append((dsid_now, f"main.{table}", "esriDTFeatureClass" if is_fc else "esriDTTable", doc["id"], lname, is_fc, bool(doc.get("hasAttachments")), False, table))
        # attachments
        if doc.get("hasAttachments"):
            at = f"{table}__ATTACH"
            afields = [{"name": "attachmentid", "type": "esriFieldTypeOID", "nullable": False}, {"name": "rel_globalid", "type": "esriFieldTypeGUID", "nullable": False},
                       {"name": "content_type", "type": "esriFieldTypeString", "length": 150, "nullable": False}, {"name": "att_name", "type": "esriFieldTypeString", "length": 250, "nullable": False},
                       {"name": "data_size", "type": "esriFieldTypeInteger", "nullable": False}, {"name": "data", "type": "esriFieldTypeBlob"}, {"name": "globalid", "type": "esriFieldTypeGlobalID", "nullable": False}]
            d.execute(f"CREATE TABLE [{at}] (" + ", ".join(coldef(f, registered) for f in afields) + (ARCH_COLS if registered else "") + ")")
            arid = register(at, "attachmentid", 0x3 | (0x40000 if registered else 0))
            acols = [f["name"] for f in afields]
            arows = []
            for n_, a in enumerate(L.get("attachments", []), 1):
                data = bytes.fromhex(a["data_hex"])
                arows.append([n_, a["parentGlobalId"], a["contentType"], a["name"], len(data), data, a["globalId"]])
            if arows:
                d.executemany(f"insert into [{at}] ({','.join(acols)}{',gdb_from_date,gdb_to_date' if registered else ''}) values ({','.join('?' * len(acols))}{',?,?' if registered else ''})",
                              [r + ([2440587.500011574, 5373484.499988426] if registered else []) for r in arows])
            d.execute("insert into GDB_RowidGenerators values (?, ?, -1, ?)", (arid, len(arows) + 1, max(len(arows), 1)))
            post.append(f"CREATE UNIQUE INDEX [UUID_{arid}] ON {at} (globalid ASC{',gdb_to_date ASC' if registered else ''})")
            post.append(f"CREATE INDEX [I{arid}rel_globalid] ON {at} (rel_globalid ASC)")
            if registered:
                post += [f"CREATE UNIQUE INDEX [r{arid}_gdb_xpk] ON {at} (attachmentid ASC,gdb_to_date ASC)", f"CREATE INDEX [gdb_ct1_{arid}] ON {at} (gdb_from_date ASC,gdb_to_date ASC)", f"CREATE UNIQUE INDEX [gdb_ct4_{arid}] ON {at} (attachmentid ASC,gdb_from_date ASC)"]
                post += archive_objects(at, "attachmentid", acols)
            adsid = len(items) + 1
            afx = "".join(field_xml(dict(f, alias=None), {}) for f in afields if f["name"] != "data")
            adefn = (f"<DETableInfo xsi:type='typens:DETableInfo' {NS}><CatalogPath>\\main.{at}</CatalogPath><Name>main.{at}</Name><ChildrenExpanded>false</ChildrenExpanded><DatasetType>esriDTTable</DatasetType><DSID>{adsid}</DSID><Versioned>false</Versioned><CanVersion>false</CanVersion><ConfigurationKeyword></ConfigurationKeyword><RequiredGeodatabaseClientVersion>10.2</RequiredGeodatabaseClientVersion><HasOID>true</HasOID><OIDFieldName>attachmentid</OIDFieldName>"
                     f"<GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'>{afx}</GPFieldInfoExs>" + common_tail(registered, True, "globalid").replace("{CLSID}", "{7A566981-C114-11D2-8A28-006097AFF44E}")
                     + f"<ChangeTracked>{'true' if registered else 'false'}</ChangeTracked><FieldFilteringEnabled>false</FieldFilteringEnabled><FilteredFieldNames xsi:type='typens:Names'></FilteredFieldNames><ReplicaTracked>false</ReplicaTracked></DETableInfo>")
            at_uuid = item("Table", f"main.{at}", f"\\main.{at}", adefn)
            rels.append(("DatasetInFolder", root, at_uuid))
            sync_datasets.append((adsid, f"main.{at}", "esriDTTable", doc["id"], None, False, False, True, at))
            rel_name = f"{table}__ATTACHREL"
            rdefn = (f"<DERelationshipClassInfo xsi:type='typens:DERelationshipClassInfo' {NS}><CatalogPath>\\main.{rel_name}</CatalogPath><Name>main.{rel_name}</Name><ChildrenExpanded>false</ChildrenExpanded><DatasetType>esriDTRelationshipClass</DatasetType><DSID>{len(items) + 1}</DSID><Versioned>false</Versioned><CanVersion>true</CanVersion><ConfigurationKeyword></ConfigurationKeyword><RequiredGeodatabaseClientVersion>10.0</RequiredGeodatabaseClientVersion><HasOID>false</HasOID><OIDFieldName></OIDFieldName><GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'></GPFieldInfoExs>"
                     + common_tail(registered, False, "").replace("{CLSID}", "")
                     + f"<Cardinality>esriRelCardinalityOneToMany</Cardinality><Notification>esriRelNotificationNone</Notification><IsAttributed>false</IsAttributed><IsComposite>true</IsComposite><OriginClassNames xsi:type='typens:Names'><Name>main.{table}</Name></OriginClassNames><DestinationClassNames xsi:type='typens:Names'><Name>main.{at}</Name></DestinationClassNames><KeyType>esriRelKeyTypeSingle</KeyType><ClassKey>esriRelClassKeyUndefined</ClassKey><ForwardPathLabel>attachment</ForwardPathLabel><BackwardPathLabel>object</BackwardPathLabel><IsReflexive>false</IsReflexive>"
                     f"<OriginClassKeys xsi:type='typens:ArrayOfRelationshipClassKey'><RelationshipClassKey xsi:type='typens:RelationshipClassKey'><ObjectKeyName>{gidf}</ObjectKeyName><ClassKeyName></ClassKeyName><KeyRole>esriRelKeyRoleOriginPrimary</KeyRole></RelationshipClassKey><RelationshipClassKey xsi:type='typens:RelationshipClassKey'><ObjectKeyName>REL_GLOBALID</ObjectKeyName><ClassKeyName></ClassKeyName><KeyRole>esriRelKeyRoleOriginForeign</KeyRole></RelationshipClassKey></OriginClassKeys><RelationshipRules xsi:type='typens:ArrayOfRelationshipRule'></RelationshipRules><IsAttachmentRelationship>true</IsAttachmentRelationship><ChangeTracked>false</ChangeTracked><ReplicaTracked>false</ReplicaTracked></DERelationshipClassInfo>")
            rel_uuid = item("Relationship Class", f"main.{rel_name}", f"\\main.{rel_name}", rdefn, ds1=2, ds2=0)
            rels += [("DatasetInFolder", root, rel_uuid), ("DatasetsRelatedThrough", fc_uuid, rel_uuid), ("DatasetsRelatedThrough", at_uuid, rel_uuid)]
    # service items: the service and each layer's REST JSON
    svc_doc = dict(spec["service"]); svc_doc.pop("_name", None)
    d.execute("insert into GDB_ServiceItems (ObjectID, ItemType, ItemInfo, ItemId, DatasetName, AdvancedDrawingInfo) values (1, 0, ?, -1, ?, NULL)", (json.dumps(svc_doc), svcname))
    for n_, L in enumerate(spec["layers"], 2):
        d.execute("insert into GDB_ServiceItems (ObjectID, ItemType, ItemInfo, ItemId, DatasetName, AdvancedDrawingInfo) values (?, 1, ?, ?, ?, NULL)", (n_, json.dumps(L["doc"]), L["doc"]["id"], f"{svcname}_{L['doc']['name']}"))
    sreg = register("GDB_ServiceItems", "ObjectID", 3)
    d.execute("insert into GDB_RowidGenerators values (?, ?, -1, 1)", (sreg, len(spec["layers"]) + 2))
    if registered:
        rid_uuid = "{" + str(uuid.uuid4()).upper() + "}"
        ds_xml = []
        for (dsid_, dname, dtype, lid, lname, is_fc, has_att, is_att, table) in sync_datasets:
            x = (f"<DatasetID>{dsid_}</DatasetID><DatasetName>{dname}</DatasetName><DatasetType>{dtype}</DatasetType><LayerID>{lid}</LayerID>" + (f"<LayerName>{esc(lname)}</LayerName>" if lname else "")
                 + f"<Direction>esriSyncDirectionBidirectional</Direction><ReplicaServerGen xsi:type='xs:long'>{gen}</ReplicaServerGen><ReplicaClientDownloadGen xsi:type='xs:long'>1000</ReplicaClientDownloadGen><ReplicaClientUploadGen xsi:type='xs:long'>1000</ReplicaClientUploadGen><ReplicaClientAcknowledgeUploadGen xsi:type='xs:long'>1000</ReplicaClientAcknowledgeUploadGen>"
                 + (f"<UseGeometry>true</UseGeometry><IncludeRelated>true</IncludeRelated><QueryOption>esriRowsTypeFilter</QueryOption>" if is_fc else (f"<UseGeometry>false</UseGeometry><IncludeRelated>false</IncludeRelated><QueryOption>esriRowsTypeNone</QueryOption>" if is_att else "<UseGeometry>false</UseGeometry><IncludeRelated>true</IncludeRelated><QueryOption>esriRowsTypeNone</QueryOption>"))
                 + ("<HasAttachments>true</HasAttachments>" if has_att else "") + ("<IsAttachment>true</IsAttachment>" if is_att else ""))
            ds_xml.append((x, dname, table))
        q = spec.get("queryGeometry", {"xmin": 3000000, "ymin": 4500000, "xmax": 3300000, "ymax": 5000000})
        srx = sr_xml(3857, WKT_3857, g.Grid(-20037700, -30241100, 10000))
        rdef = (f"<GPSyncReplica xsi:type='typens:GPSyncReplica' {NS}><ReplicaName>{esc(replica_name)}</ReplicaName><ID>1</ID><ReplicaID>{rid_uuid[1:-1]}</ReplicaID><ServiceName>{esc(service_url)}</ServiceName><Owner>{esc(owner)}</Owner><Role>esriReplicaRoleChild</Role><SyncModel>esriSyncModelPerLayer</SyncModel><Direction>esriSyncDirectionBidirectional</Direction>"
                f"<CreationDate>{stamp}.000</CreationDate><LastSyncDate>1970-01-01T00:00:01</LastSyncDate><ReturnsAttachments>true</ReturnsAttachments><SpatialRelation>esriSpatialRelIntersects</SpatialRelation>"
                f"<QueryGeometry xsi:type='typens:EnvelopeN'><XMin>{q['xmin']:g}</XMin><YMin>{q['ymin']:g}</YMin><XMax>{q['xmax']:g}</XMax><YMax>{q['ymax']:g}</YMax>{srx}</QueryGeometry>"
                "<GPSyncDatasets xsi:type='typens:ArrayOfGPSyncDataset'>" + "".join(f"<GPSyncDataset xsi:type='typens:GPSyncDataset'>{x}</GPSyncDataset>" for x, _, _ in ds_xml) + "</GPSyncDatasets>"
                + srx + "<AttachmentsSyncDirection>esriAttachmentsSyncDirectionBidirectional</AttachmentsSyncDirection><TargetType>esriReplicaTargetTypeClient</TargetType><SyncDataOptions>0</SyncDataOptions></GPSyncReplica>")
        rep = item("Sync Replica", replica_name, replica_name, rdef, di1=service_url, di2=owner, u=rid_uuid)
        for x, dname, table in ds_xml:
            su = item("Sync Dataset", table, f"{replica_name}\\{table}", f"<GPSyncDataset xsi:type='typens:GPSyncDataset' {NS}>{x}</GPSyncDataset>", phys=dname.upper())
            target = [it[0] for it in items if it[2] == dname][0]
            rels += [("SyncDatasetInSyncReplica", rep, su), ("DatasetOfSyncDataset", su, target)]
    # items and relationships
    for n_, it in enumerate(items, 1):
        u, typ, name, phys, path, url, props, ds1, ds2, di1, di2, defn, shape = it
        d.execute("insert into GDB_Items (ObjectID, UUID, Type, Name, PhysicalName, Path, Url, Properties, Defaults, DatasetSubtype1, DatasetSubtype2, DatasetInfo1, DatasetInfo2, Definition, Documentation, ItemInfo, ContingentValues, Shape) values (?,?,?,?,?,?,?,?,NULL,?,?,?,?,?,NULL,NULL,NULL,?)",
                  (n_, u, typ, name, phys, path, url, props, ds1, ds2, di1, di2, defn, shape))
        if shape:
            _, _, _, _, raw = g.decode(shape); pts = [r for r in raw if r]
            G = g.Grid(-400, -400, 1e9)
            d.execute("insert into st_spindex__GDB_Items_Shape values (?,?,?,?,?)", (n_, min(G.ux(r[0]) for r in pts), max(G.ux(r[0]) for r in pts), min(G.uy(r[1]) for r in pts), max(G.uy(r[1]) for r in pts)))
    bb = d.execute("select min(minx), min(miny), max(maxx), max(maxy) from st_spindex__GDB_Items_Shape").fetchone()
    if bb[0] is not None: d.execute("update GDB_Layers set minx=?, miny=?, maxx=?, maxy=? where layer_id=1", bb)
    for n_, (typ, o, de) in enumerate(rels, 1):
        d.execute("insert into GDB_ItemRelationships (ObjectID, UUID, Type, OriginID, DestID, Properties, Attributes) values (?,?,?,?,?,1,NULL)", (n_, gid(), R[typ], o, de))
    d.execute("insert into GDB_RowidGenerators values (2, ?, -1, 1)", (len(items) + 1,))
    d.execute("insert into GDB_RowidGenerators values (3, ?, -1, 1)", (len(rels) + 1,))
    d.execute("insert into GDB_RowidGenerators values (4, ?, -1, 1)", (k.execute("select count(*) + 1 from GDB_ItemTypes").fetchone()[0],))
    d.execute("insert into GDB_RowidGenerators values (5, ?, -1, 1)", (k.execute("select count(*) + 1 from GDB_ItemRelationshipTypes").fetchone()[0],))
    for r_ in (6, 7, 8): d.execute("insert into GDB_RowidGenerators values (?, 1, -1, 1)", (r_,))
    d.execute("insert into GDB_TablesModified values ('GDB_Layers', ?)", (now_epoch,))
    d.execute("insert into GDB_TablesModified values ('GDB_TableRegistry', ?)", (now_epoch,))
    # GDB_ColumnRegistry: generated from the tables
    sysrows = k.execute("select * from GDB_ColumnRegistry where table_name like 'GDB\\_%' escape '\\'").fetchall()
    d.executemany("insert into GDB_ColumnRegistry values (?,?,?,?,?,?,?,?,?,?)", sysrows)
    SDE = {"esriFieldTypeSmallInteger": 1, "esriFieldTypeInteger": 2, "esriFieldTypeSingle": 3, "esriFieldTypeDouble": 4, "esriFieldTypeString": 5,
           "esriFieldTypeBlob": 6, "esriFieldTypeDate": 7, "esriFieldTypeGUID": 12, "esriFieldTypeGlobalID": 12, "esriFieldTypeOID": 2}
    layer_ids = {t: lid for lid, t in d.execute("select layer_id, table_name from GDB_Layers")}
    def colreg(table, fields, shape_col=None):
        for f in fields:
            t = f["type"]
            if t == "esriFieldTypeGeometry": continue
            size = 10 if t == "esriFieldTypeOID" else (38 if t in ("esriFieldTypeGUID", "esriFieldTypeGlobalID") else (f.get("length") or 255 if t == "esriFieldTypeString" else (None if t == "esriFieldTypeBlob" else 0)))
            flags = 3 if t == "esriFieldTypeOID" else (256 if t == "esriFieldTypeGlobalID" else (4 if f.get("nullable", True) else 0))
            d.execute("insert into GDB_ColumnRegistry values (?,?,?,?,?,NULL,?,0,NULL,NULL)", (table, f["name"], SDE[t], size, 0 if t in ("esriFieldTypeDouble", "esriFieldTypeSingle") else None, flags))
        if shape_col:
            d.execute("insert into GDB_ColumnRegistry values (?,?,8,NULL,NULL,NULL,32772,0,?,NULL)", (table, shape_col, layer_ids[table]))
        if registered:
            for n_, desc, st in (("gdb_from_date", "Archiving from date.", 7), ("gdb_to_date", "Archiving to date.", 7), ("gdb_archive_oid", "Archiving record unique id.", 2)):
                d.execute("insert into GDB_ColumnRegistry values (?,?,?,0,NULL,?,536870912,0,NULL,NULL)", (table, n_, st, desc))
    for L in spec["layers"]:
        doc = L["doc"]; table = f"{svcname}_{doc['name']}"
        colreg(table, doc["fields"], "shape" if doc.get("geometryType") else None)
        if doc.get("hasAttachments"):
            colreg(f"{table}__ATTACH", [{"name": "attachmentid", "type": "esriFieldTypeOID"}, {"name": "rel_globalid", "type": "esriFieldTypeGUID", "nullable": False},
                                        {"name": "content_type", "type": "esriFieldTypeString", "length": 150, "nullable": False}, {"name": "att_name", "type": "esriFieldTypeString", "length": 250, "nullable": False},
                                        {"name": "data_size", "type": "esriFieldTypeInteger", "nullable": False}, {"name": "data", "type": "esriFieldTypeBlob"}, {"name": "globalid", "type": "esriFieldTypeGlobalID"}])
    for s in post: d.execute(s)
    for n in ("st_insert_trigger_GDB_Items_Shape", "st_delete_trigger_GDB_Items_Shape", "st_update_trigger_GDB_Items_Shape", "st_update1_trigger_GDB_Items_Shape"):
        d.execute(sysddl[n])
    d.commit()
    d.execute("pragma writable_schema=ON")
    for n in ("st_spatial_reference_systems", "st_vtspindex_interface"):
        d.execute("insert into sqlite_master(type, name, tbl_name, rootpage, sql) values ('table', ?, ?, 0, ?)", (n, n, sysddl[n]))
    d.commit(); d.execute("pragma writable_schema=OFF"); d.close()
    print("wrote", out, os.path.getsize(out), "registered" if registered else "snapshot")

main()
