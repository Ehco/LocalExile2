"""What a game update changed in the .datc64 tables the generators read (PORTING-POE2.md, wave 35).

    mv Tools/re/out/poe2 Tools/re/out/poe2-<old version>      # before the first run on the new data
    python scripts/re/poe2/table_changes.py Tools/re/out/poe2-<old version> [Table ...]

Fetches every table of the old cache again from the bundles and prints the tables whose bytes differ: rows and row
size before -> after. A changed row size means a schema change; check the columns the generators read. Then, for the
tables named (default: those whose row count changed), where the rows moved: rows are matched by Id (by their whole
decoded content when the table has no unique Id). Row numbers go on the wire (stat keys, skill rows, quest flags), so
"first difference at row N" says which of them are still good.
"""
import hashlib
import json
import os
import struct
import sys

import dat

NEW = dat.CACHE
PREFIX, SUFFIX = 'Data_Balance_', '.datc64'


def shape(path):
    d = open(path, 'rb').read()
    n = struct.unpack_from('<I', d, 0)[0]
    return n, (d.index(b'\xbb' * 8) - 4) // max(n, 1), hashlib.sha256(d).digest()


def keys(name, cache):
    dat.CACHE = cache
    try:
        rows = dat.table(name)
    finally:
        dat.CACHE = NEW
    key = next((c for c in ('Id', 'Name') if rows and isinstance(rows[0].get(c), str)), None)
    if key and len({r[key] for r in rows}) == len(rows):
        return key, [r[key] for r in rows]
    return 'content', [json.dumps({k: v for k, v in r.items() if k != '_row'}, sort_keys=True, default=str) for r in rows]


def row_moves(name, old_cache):
    try:
        by, old = keys(name, old_cache)
        _, new = keys(name, NEW)
    except (SystemExit, OverflowError, struct.error) as e:  # a table dat.py cannot decode (schema drift)
        print(f'== {name}: cannot decode ({e})')
        return
    first = next((i for i, (a, b) in enumerate(zip(old, new)) if a != b), min(len(old), len(new)))
    index, old_set = {k: i for i, k in enumerate(new)}, set(old)
    kept = sum(1 for i, k in enumerate(old) if index.get(k) == i)
    removed = sum(1 for k in old if k not in index)
    added = [(i, k) for i, k in enumerate(new) if k not in old_set]
    print(f'== {name} (by {by}): {len(old)} -> {len(new)} rows; first difference at row {first}; '
          f'{kept} keep their row, {len(old) - kept - removed} moved, {removed} removed, {len(added)} added')
    for i, k in added[:6]:
        print(f'   + row {i}' + (f': {k[:70]}' if by != 'content' else ''))
    if len(added) > 6:
        print(f'   + ... {len(added) - 6} more, rows {added[6][0]}..{added[-1][0]}')


def main():
    old_cache = os.path.abspath(sys.argv[1])
    names = sorted(f[len(PREFIX):-len(SUFFIX)] for f in os.listdir(old_cache) if f.startswith(PREFIX) and f.endswith(SUFFIX))
    dat.fetch([f'Data/Balance/{n}{SUFFIX}' for n in names])
    grown, same = [], 0
    for name in names:
        new_path = dat.cached(f'Data/Balance/{name}{SUFFIX}')
        if not os.path.exists(new_path):
            print(f'{name:<34} MISSING in the new bundles')
            continue
        old, new = shape(os.path.join(old_cache, PREFIX + name + SUFFIX)), shape(new_path)
        if old[2] == new[2]:
            same += 1
            continue
        what = 'ROW SIZE CHANGED' if old[1] != new[1] else 'rows changed' if old[0] != new[0] else 'values changed'
        print(f'{name:<34} rows {old[0]:>6} -> {new[0]:>6}   row size {old[1]:>4} -> {new[1]:>4}   {what}')
        if old[0] != new[0]:
            grown.append(name)
    print(f'identical: {same} of {len(names)}')
    for name in sys.argv[2:] or grown:
        row_moves(name, old_cache)


if __name__ == '__main__':
    main()
