"""Reads PoE2 .datc64 tables with data/schema/schema.min.json (the columns valid for PoE2, validFor & 2).

    python scripts/re/poe2/dat.py <Table> [row | Id] [...]     prints rows as JSON
    import dat; dat.table("Characters")                         -> list of dicts

Files come from the Steam bundles through scripts/re/poe2/steamdump and are cached in Tools/re/out/poe2, like
monster_varieties.py. Foreign rows read as the row index (None when null), arrays as lists, strings as text.
"""
import json
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
BUNDLES = os.environ.get("POE2_BUNDLES", r"G:\SteamLibrary\steamapps\common\Path of Exile 2\Bundles2")
CACHE = os.path.join(REPO, "Tools", "re", "out", "poe2")
SCHEMA = os.path.join(REPO, "data", "schema", "schema.min.json")
NULL = 0xfefefefefefefefe

SIZES = {"bool": 1, "i16": 2, "u16": 2, "i32": 4, "u32": 4, "f32": 4, "i64": 8, "u64": 8, "string": 8, "row": 8,
         "foreignrow": 16, "enumrow": 4, "interval": 8}
SCALARS = {"bool": "<?", "i16": "<h", "u16": "<H", "i32": "<i", "u32": "<I", "f32": "<f", "i64": "<q", "u64": "<Q",
           "enumrow": "<i"}


def cached(path):
    return os.path.join(CACHE, path.replace("/", "_"))


def fetch(paths):
    todo = [p for p in paths if not os.path.exists(cached(p))]
    if not todo:
        return
    os.makedirs(CACHE, exist_ok=True)
    # Batches: Windows refuses a command line over 32 k characters (the equipment's .it files are 2000+ paths of ~70
    # characters); each batch is one steamdump run, which loads the bundle index again.
    for i in range(0, len(todo), 300):
        cmd = ["dotnet", "run", "--project", os.path.join(HERE, "steamdump"), "-c", "Release", "--", BUNDLES, CACHE] + todo[i:i + 300]
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode:
            raise SystemExit(f"steamdump failed ({r.returncode}): {r.stdout[-2000:]}{r.stderr[-2000:]}")


def columns(name):
    schema = json.load(open(SCHEMA, encoding="utf-8"))
    for t in schema["tables"]:
        if t["name"] == name and t.get("validFor", 3) & 2:
            return t["columns"]
    raise SystemExit(f"{name}: no PoE2 table in {SCHEMA}")


def width(col):
    return 16 if col.get("array") else SIZES[col["type"]]


def table(name, folder="Data/Balance"):
    path = f"{folder}/{name}.datc64"
    fetch([path])
    if not os.path.exists(cached(path)):
        raise SystemExit(f"{path}: not in the bundles")
    d = open(cached(path), "rb").read()
    n = struct.unpack_from("<I", d, 0)[0]
    var = d.index(b"\xbb" * 8)
    row_size = (var - 4) // max(n, 1)
    cols = columns(name)
    known = sum(width(c) for c in cols)
    if known > row_size:
        raise SystemExit(f"{name}: schema has {known} bytes, rows are {row_size}")

    def text(off):
        p = var + off
        e = p
        while d[e:e + 2] != b"\0\0":
            e += 2
        return d[p:e].decode("utf-16le")

    def one(t, at):
        if t in SCALARS:
            return struct.unpack_from(SCALARS[t], d, at)[0]
        if t == "string":
            return text(struct.unpack_from("<Q", d, at)[0])
        if t in ("row", "foreignrow"):
            r = struct.unpack_from("<Q", d, at)[0]
            return None if r == NULL else r
        if t == "interval":
            return list(struct.unpack_from("<ii", d, at))
        raise SystemExit(f"{name}: column type {t}")

    rows = []
    for i in range(n):
        base, at, row = 4 + i * row_size, 0, {"_row": i}
        for k, c in enumerate(cols):
            key = c.get("name") or f"_{k}"
            if c.get("array"):
                count, off = struct.unpack_from("<QQ", d, base + at)
                if c["type"] == "array":  # element type unknown: the count only
                    row[key] = {"count": count}
                else:
                    step = SIZES[c["type"]]
                    row[key] = [one(c["type"], var + off + j * step) for j in range(count)]
            else:
                row[key] = one(c["type"], base + at)
            at += width(c)
        row["_size"] = row_size
        rows.append(row)
    return rows


def main():
    rows = table(sys.argv[1])
    keys = sys.argv[2:]
    for r in rows:
        if not keys or str(r["_row"]) in keys or r.get("Id") in keys:
            print(json.dumps(r, ensure_ascii=False))


if __name__ == "__main__":
    main()
