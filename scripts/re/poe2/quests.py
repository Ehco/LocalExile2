"""Writes data/poe2/quests.json: what the PoE2 server needs to keep and send a character's quest state
(PORTING-POE2.md, wave 27).

    python scripts/re/poe2/quests.py ["G:/SteamLibrary/steamapps/common/Path of Exile 2/Bundles2"]

- "flags": the QuestFlags Ids in row order. The client's quest flag set (the Player component's, sent again with 0x2f9)
  holds row indices; the server stores the Ids.
- "waypoints": the areas of the World panel's waypoint bitset (S->C 0x5a, 22 bytes), in bit order: the WorldAreas rows
  with HasWaypoint whose column _45 (the row's +0x1b4) is 1 (a town), 2 (an area) or 31, at most 175, as the client's
  0x141f68930 lists them. Each with its Id, HASH16, act and whether it is a town.
- "areas": per WorldAreas Id, "entered": the Ids of its FlagOnEntered flags (Visited-G1_2 ...), which the server sets when
  the character enters the area; the NPCs' gossip options need them.

Files come from the Steam bundles through scripts/re/poe2/steamdump and are cached in Tools/re/out/poe2 (dat.py).
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
sys.path.insert(0, HERE)
import dat  # noqa: E402

OUT = os.path.join(REPO, "data", "poe2", "quests.json")
# WorldAreas +0x1b4 (column _45 of the 0.5.5e schema) values the bitset takes, and its size (0x141f68930: 0xaf entries).
WAYPOINT_KINDS = (1, 2, 31)
MAX_WAYPOINTS = 175


def main():
    flags = [r["Id"] for r in dat.table("QuestFlags")]
    areas = dat.table("WorldAreas")
    waypoints = [{"id": r["Id"], "hash": r["HASH16"], "act": r["Act"], "town": bool(r["IsTown"])}
                 for r in areas if r["HasWaypoint"] and r["_45"] in WAYPOINT_KINDS][:MAX_WAYPOINTS]
    entered = {r["Id"]: {"entered": [flags[f] for f in r["FlagOnEntered"]]} for r in areas if r["FlagOnEntered"]}
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write('{\n "flags": ' + json.dumps(flags, separators=(",", ":")) + ',\n "waypoints": [\n'
                + ",\n".join("  " + json.dumps(w, separators=(",", ":")) for w in waypoints) + '\n ],\n "areas": {\n'
                + ",\n".join(f"  {json.dumps(k)}:{json.dumps(v, separators=(',', ':'))}" for k, v in entered.items())
                + "\n }\n}\n")
    print(f"{len(flags)} flags, {len(waypoints)} waypoint areas, {len(entered)} areas with entry flags -> {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
