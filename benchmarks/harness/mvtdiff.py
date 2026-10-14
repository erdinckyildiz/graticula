"""Compare Q-68's two tiles feature by feature: ids, geometry command integers, tag pairs, and where the bytes go."""
import glob
import os
import sys
from collections import Counter


def varint(b, i):
    shift = result = 0
    while True:
        c = b[i]; i += 1
        result |= (c & 0x7F) << shift
        if not c & 0x80:
            return result, i
        shift += 7


def fields(b):
    i = 0
    while i < len(b):
        key, i = varint(b, i)
        f, w = key >> 3, key & 7
        if w == 0:
            v, i = varint(b, i)
        elif w == 2:
            n, i = varint(b, i)
            v = b[i:i + n]; i += n
        elif w == 1:
            v = b[i:i + 8]; i += 8
        elif w == 5:
            v = b[i:i + 4]; i += 4
        else:
            raise ValueError(w)
        yield f, w, v


def packed(b):
    out, i = [], 0
    while i < len(b):
        v, i = varint(b, i)
        out.append(v)
    return out


def tile(path):
    feats, sizes = {}, Counter()
    for f, w, layer in fields(open(path, "rb").read()):
        if f != 3:
            continue
        for lf, lw, lv in fields(layer):
            name = {1: "name", 2: "features", 3: "keys", 4: "values", 5: "extent", 15: "version"}.get(lf, lf)
            sizes[name] += len(lv) if isinstance(lv, (bytes, bytearray)) else 1
            if lf == 2:
                fid, geom, tags, kind = None, [], [], None
                for ff, fw, fv in fields(lv):
                    if ff == 1: fid = fv
                    elif ff == 2: tags = packed(fv)
                    elif ff == 3: kind = fv
                    elif ff == 4: geom = packed(fv)
                feats.setdefault(fid, []).append((kind, len(geom), len(tags), len(lv)))
    return feats, sizes


folder = sys.argv[1]
tot = Counter()
for a in sorted(glob.glob(os.path.join(folder, "a-*.pbf"))):
    b = a.replace(os.sep + "a-", os.sep + "b-")
    fa, sa = tile(a)
    fb, sb = tile(b)
    only_a = set(fa) - set(fb)
    only_b = set(fb) - set(fa)
    both = set(fa) & set(fb)
    tot["only_a"] += len(only_a); tot["only_b"] += len(only_b); tot["both"] += len(both)
    tot["geom_a"] += sum(g for k in both for (_, g, _, _) in fa[k]); tot["geom_b"] += sum(g for k in both for (_, g, _, _) in fb[k])
    tot["bytes_only_a"] += sum(s for k in only_a for (*_, s) in fa[k]); tot["bytes_only_b"] += sum(s for k in only_b for (*_, s) in fb[k])
    tot["dup_a"] += sum(len(v) - 1 for v in fa.values()); tot["dup_b"] += sum(len(v) - 1 for v in fb.values())
    for k, v in sa.items(): tot["A " + str(k)] += v
    for k, v in sb.items(): tot["B " + str(k)] += v
for k in sorted(tot):
    print(f"{k:>22} {tot[k]:>12,}")
