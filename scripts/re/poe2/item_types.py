"""Writes data/poe2/item-types.json: the PoE2 item types the server drops (PORTING-POE2.md, wave 25), with what the item
data needs.

    python scripts/re/poe2/item_types.py

- The types: every BaseItemTypes row under Metadata/Items/Currency/ with a name players see (not "[DNT]").
- hash: the row's HASH32, the item data's first u32 (the reader 0x1421ecfc0 looks the type up by it).
- visualIdentity: the ItemVisualIdentity row's HASH16, the Base component's first u16 (0x1425a0e50).
- components: the .it chain merged as ot_components.merge_items does for PoE1 (parent-first, nothing first, "client"
  blocks not read): the reader calls each component's vtable +0x28 in this order. Currency is Base, Usable, Stack.
- maxStack: CurrencyItems StackSize (1 without a row).

Files come from the Steam bundles through scripts/re/poe2/steamdump and are cached in Tools/re/out/poe2.
"""
import json
import os
import sys

import dat

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import ot_components  # noqa: E402  (parse and merge of .ot/.it files)

OUT = os.path.join(dat.REPO, "data", "poe2", "item-types.json")
PREFIX = "Metadata/Items/Currency/"


def load(types):
    """Parsed .it files of the types and their extends chains (types without a file are left out)."""
    files, frontier = {}, sorted(set(types))
    while frontier:
        dat.fetch([p + ".it" for p in frontier])
        nxt = []
        for p in frontier:
            f = dat.cached(p + ".it")
            if not os.path.exists(f):
                continue
            b = open(f, "rb").read()
            files[p] = ot_components.parse(b.decode("utf-16") if b[:2] in (b"\xff\xfe", b"\xfe\xff") else b.decode("utf-8"))
            nxt += [e for e in files[p]["extends"] if e != "nothing" and e not in files]
        frontier = sorted(set(nxt))
    return files


def main():
    bases = dat.table("BaseItemTypes")
    rows = [r for r in bases if r["Id"].startswith(PREFIX) and r["Name"] and not r["Name"].startswith("[DNT]")]
    visuals = dat.table("ItemVisualIdentity")
    stacks = {r["BaseItemType"]: r["StackSize"] for r in dat.table("CurrencyItems") if r["BaseItemType"] is not None}
    files = load([r["Id"] for r in rows] + [r["InheritsFrom"] for r in rows if r["InheritsFrom"]])
    out = []
    for r in rows:
        own = r["Id"] if r["Id"] in files else r["InheritsFrom"]
        if own not in files:
            print(f"{r['Id']}: no .it in its chain", file=sys.stderr)
            continue
        m = ot_components.merge(own, files, {}, first=())
        if m["missing"]:
            print(f"{r['Id']}: .it missing for {', '.join(m['missing'])}", file=sys.stderr)
            continue
        visual = r["ItemVisualIdentity"]
        out.append({"id": r["Id"], "row": r["_row"], "name": r["Name"], "hash": r["HASH32"], "width": r["Width"],
                    "height": r["Height"], "dropLevel": r["DropLevel"],
                    "visualIdentity": visuals[visual]["HASH16"] if visual is not None else 0,
                    "components": m["components"][:m["read"]], "maxStack": stacks.get(r["_row"], 1)})
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("[\n" + ",\n".join(" " + json.dumps(r, separators=(",", ":")) for r in out) + "\n]\n")
    print(f"{len(out)} item types -> {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
