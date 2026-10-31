"""ADR-231 condition 5 (D-338): Pro 3.7's and grapy's trace answers compared, pairs of JSON files the trace tools wrote.

    python adr231-compare.py <pro.json> <grapy.json> [<pro.json> <grapy.json> ...]

Prints each trace whose elements, error or function results differ, and the count that agree. An error is compared by
its first line. A tool, not a test.
"""
import json, sys
def norm(v):
    if isinstance(v, dict):
        e = v.get('elements', v.get('error'))
        if isinstance(e, list) and e and str(e[0]).startswith('ERROR'):
            e = e[0]
        return (e, v.get('functions'))
    return (v, None)
tot = same = 0
for pf, gf in zip(sys.argv[1::2], sys.argv[2::2]):
    p, g = json.load(open(pf)), json.load(open(gf))
    for k in p:
        if k == 'side' or k not in g:
            continue
        tot += 1
        if norm(p[k]) == norm(g[k]):
            same += 1
        else:
            print('DIFF', pf, k)
print(same, 'of', tot)
