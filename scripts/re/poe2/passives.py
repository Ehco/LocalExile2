"""Writes data/poe2/passives.json: the PoE2 passive skill tree's nodes with their stats (PORTING-POE2.md, wave 28).

    python scripts/re/poe2/passives.py ["G:/SteamLibrary/steamapps/common/Path of Exile 2/Bundles2"]

One entry per PassiveSkills row with a PassiveSkillGraphId (the id the client allocates with C->S 0x3c and the server
lists in S->C 0x3a): the graph id, the row's Id and Name, its stats as [Stats Id, client key (Stats row + 1), value]
(StatNValue goes with the N-th entry of Stats), SkillPointsGranted, and whether it is a keystone, a notable, an
attribute node (the +5 attribute of the player's choice) or a jewel socket. The server adds the stats of the allocated
nodes to the player's (Poe2CharacterStats) and sends them in its Stats component.

Also writes data/poe2/gold-respec-prices.json from GoldRespecPrices.datc64: [[level, gold], ...], what a refund of one
node costs at each level (the client refuses the refund click when the character's gold is short).

Files come from the Steam bundles through scripts/re/poe2/steamdump and are cached in Tools/re/out/poe2 (dat.py).
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
sys.path.insert(0, HERE)
import dat  # noqa: E402

OUT = os.path.join(REPO, "data", "poe2", "passives.json")
PRICES_OUT = os.path.join(REPO, "data", "poe2", "gold-respec-prices.json")


def main():
    stats = [r["Id"] for r in dat.table("Stats")]
    rows = []
    for r in dat.table("PassiveSkills"):
        if r["PassiveSkillGraphId"] is None or r["IsJustIcon"]:
            continue
        values = [r.get(f"Stat{i}Value") or 0 for i in range(1, 8)]
        entry = {"graphId": r["PassiveSkillGraphId"], "id": r["Id"], "name": r["Name"] or "",
                 "stats": [[stats[s], s + 1, values[i] if i < len(values) else 0] for i, s in enumerate(r["Stats"] or [])],
                 "points": r["SkillPointsGranted"] or 0}
        for key, flag in (("keystone", "IsKeystone"), ("notable", "IsNotable"), ("attribute", "IsAttribute"),
                          ("jewelSocket", "IsJewelSocket"), ("ascendancy", "IsAscendancyStartingNode")):
            if r.get(flag):
                entry[key] = True
        if r["Ascendancy"] is not None:
            entry["ascendancyRow"] = r["Ascendancy"]
        rows.append(entry)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("[\n" + ",\n".join(" " + json.dumps(e, separators=(",", ":"), ensure_ascii=False) for e in rows) + "\n]\n")
    print(f"{len(rows)} nodes -> {OUT}", file=sys.stderr)
    prices = sorted((r["Level"], r["Cost"]) for r in dat.table("GoldRespecPrices"))
    with open(PRICES_OUT, "w", encoding="utf-8") as f:
        f.write("[" + ",".join(f"[{level},{cost}]" for level, cost in prices) + "]\n")
    print(f"{len(prices)} respec prices -> {PRICES_OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
