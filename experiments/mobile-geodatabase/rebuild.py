"""Disposable feasibility probe: rebuild a mobile geodatabase with plain SQLite (no Esri extension).
Schema statements are replayed from a source file; the two virtual tables whose modules only Esri has are written into
sqlite_master directly; triggers go in last so no Esri function is needed while rows are loaded; every geometry is
re-encoded through stgeom.py. Usage: rebuild.py <source> <target>"""
import sqlite3, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import stgeom as g

src, dst = sys.argv[1], sys.argv[2]
if os.path.exists(dst): os.remove(dst)
s = sqlite3.connect(f"file:{src}?mode=ro", uri=True)
fold = lambda a, b: (a.casefold() > b.casefold()) - (a.casefold() < b.casefold())
s.create_collation("ICUFOLDCASE", fold)
d = sqlite3.connect(dst)
d.create_collation("ICUFOLDCASE", fold)
d.execute(f"pragma page_size={s.execute('pragma page_size').fetchone()[0]}")
d.execute(f"pragma application_id={s.execute('pragma application_id').fetchone()[0]}")

entries = s.execute("select type, name, tbl_name, sql from sqlite_master where sql is not null order by rowid").fetchall()
esri_modules = ("USING VSRS", "USING VTSPINDEX")
virtual_special, triggers, indexes, views = [], [], [], []
for typ, name, tbl, sql in entries:
    up = sql.upper()
    if typ == "table" and any(m in up for m in esri_modules):
        virtual_special.append((name, tbl, sql)); continue
    if typ == "trigger": triggers.append(sql); continue
    if typ == "index": indexes.append(sql); continue
    if typ == "view": views.append(sql); continue
    if typ == "table":
        # shadow tables of an RTREE are made by the CREATE VIRTUAL TABLE ... USING RTREE itself
        if any(name.startswith(r[1]) and name != r[1] and name[len(r[1]):] in ("_node", "_parent", "_rowid") for r in [(0, n) for (t2, n, _, q) in entries if t2 == "table" and "USING RTREE" in (q or "").upper()]):
            continue
        if name == "sqlite_sequence" or name.startswith("sqlite_stat"): continue
        d.execute(sql)

# rows
geomcols = {t: c for t, c in s.execute("select f_table_name, f_geometry_column from st_geometry_columns")}
names = [n for (n,) in d.execute("select name from sqlite_master where type='table'")]
for n in names:
    if n.startswith("st_spindex__") and not n.endswith(("_node", "_parent", "_rowid")):
        rows = s.execute(f'select * from "{n}"').fetchall()
        d.executemany(f'insert into "{n}" values ({",".join("?" * 5)})', rows)
        continue
    if n.startswith("st_spindex__") or n.startswith("sqlite_"): continue
    cur = s.execute(f'select * from "{n}"'); cols = [x[0] for x in cur.description]
    rows = cur.fetchall()
    gc = geomcols.get(n)
    if gc:
        k = cols.index(gc) if gc in cols else [c.lower() for c in cols].index(gc.lower())
        new = []
        for r in rows:
            r = list(r)
            if r[k] is not None:
                srid, kind, hz, hm, raw = g.decode(r[k])
                parts = g.regroup(kind, raw)
                pt = lambda q: tuple([q[0], q[1]] + ([q[2]] if hz else []) + ([q[3]] if hm else []))
                P = [[pt(q) for q in parts[0]]] if kind in ("point", "multipoint") else ([[pt(q) for q in p] for p in parts] if kind == "polyline" else [[[pt(q) for q in ring] for ring in p] for p in parts])
                r[k] = g.encode(kind, P, srid, g.Grid(0, 0, 1, 0, 1, 0, 1), hz, hm)
            new.append(r)
        rows = new
    if rows:
        d.executemany(f'insert into "{n}" values ({",".join("?" * len(cols))})', rows)
seq = s.execute("select name, seq from sqlite_sequence").fetchall()
if seq:
    d.executemany("insert into sqlite_sequence(name, seq) values (?, ?)", seq)
for sql in indexes: d.execute(sql)
for sql in views: d.execute(sql)
for sql in triggers: d.execute(sql)
d.commit()
# Esri-module virtual tables: a CREATE VIRTUAL TABLE cannot run here, so the schema row is written directly.
d.execute("pragma writable_schema=ON")
for name, tbl, sql in virtual_special:
    d.execute("insert into sqlite_master(type, name, tbl_name, rootpage, sql) values ('table', ?, ?, 0, ?)", (name, tbl, sql))
d.commit()
d.execute("pragma writable_schema=OFF")
d.close()
print("rebuilt", dst, os.path.getsize(dst))
