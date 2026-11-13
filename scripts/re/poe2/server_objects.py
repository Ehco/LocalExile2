"""Writes data/poe2/server-objects.json: the PoE2 server objects other than exits and monsters that the area captures
place (NPCs, the waypoint, the stash, ...), with the components the client deserializes for them and the quest flags
that drive their shared states (PORTING-POE2.md, wave 27).

    python scripts/re/poe2/server_objects.py ["G:/SteamLibrary/steamapps/common/Path of Exile 2/Bundles2"]

- The types: every type in data/walkability/*.objects.json (scripts/walkmaps/poe2_walkmap.py) under Metadata/NPC/ or
  Metadata/MiscellaneousObjects/ whose components include InteractionAction (NPCs, the waypoint, the stash, the well),
  except the exits (AreaTransition*, written by AreaTransitionObjectPoe2). The others are the client's own: live, an
  unknown type (DogTrader_Entrance, which extends "nothing") crashed the client on its 0x2f7 (type lookup 0x141693840
  found nothing), and the townspeople extend Doodad.
- The components: ot_components.py's merge of the .ot chain (Positioned first, then the sections parent-first; client
  blocks are not deserialized), as for the monsters (monster_varieties.py).
- The StateMachine's shared states with their .ot defaults, and PoE2's per_player_quest_flag_state_<state>_<value> =
  "Flag" rules: the server sets the state to the value when the player has the flag (Renly's "visible" goes to 0 with
  IronCountKilled, the injured Hooded One's to 1 with TreeOfSoulsMentorReleased).
- The NPC's dialogue options, one per NPCTalk row of the NPCs row with the type's Id: index (what C→S 0xa1 sends),
  category (NPCTalkCategory Id: BuyItems opens the buy window), requires / excludes (the QuestFlags Ids of columns _3 /
  _4, PoE1's QuestFlag1 / QuestFlag2: the client offers the option when the player has all of the first and none of the
  second), start (QuestFlags1, set when the option is picked), finish (QuestFlag2, set after its talk; NEVER_SET is
  none) and the row's Script (SetQuestFlag(...) sets more flags; triggerer.FinishNPCChat() ends the dialogue at once).

Files come from the Steam bundles through scripts/re/poe2/steamdump and are cached in Tools/re/out/poe2.
"""
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)
BUNDLES = sys.argv[1] if len(sys.argv) > 1 else None
sys.argv = sys.argv[:1] + ([BUNDLES] if BUNDLES else [])
import dat  # noqa: E402
import ot_components  # noqa: E402
import monster_varieties  # noqa: E402  (fetch and load of .ot chains from the Steam bundles)

OUT = os.path.join(REPO, "data", "poe2", "server-objects.json")
PREFIXES = ("Metadata/NPC/", "Metadata/MiscellaneousObjects/")
# Written elsewhere: the exits and their arrival markers.
SKIP = ("Metadata/MiscellaneousObjects/AreaTransition",)


def main():
    placed = sorted({o["type"] for f in glob.glob(os.path.join(REPO, "data", "walkability", "*.objects.json"))
                     for o in json.load(open(f, encoding="utf-8"))
                     if o["type"].startswith(PREFIXES) and not o["type"].startswith(SKIP)})
    files = monster_varieties.load(placed)
    npc_rows = {r["Id"]: i for i, r in enumerate(dat.table("NPCs"))}
    categories = [r["Id"] for r in dat.table("NPCTalkCategory")]
    flag_ids = [r["Id"] for r in dat.table("QuestFlags")]

    def flags(v):
        return [flag_ids[i] for i in (v if isinstance(v, list) else [v] if v is not None else [])]

    talk = {}
    for r in dat.table("NPCTalk"):
        if r["NPCKey"] is not None and r["Category"] is not None:
            finish = flag_ids[r["QuestFlag2"]] if r["QuestFlag2"] is not None else None
            talk.setdefault(r["NPCKey"], []).append({
                "index": r["_1"] or 0, "category": categories[r["Category"]], "requires": flags(r["_3"]),
                "excludes": flags(r["_4"]), "start": flags(r["QuestFlags1"]),
                "finish": None if finish == "NEVER_SET" else finish, "script": r["Script"] or ""})
    rows = []
    for t in placed:
        m = ot_components.merge(t, files, {})
        if m["missing"]:
            print(f"{t}: .ot missing for {', '.join(m['missing'])}", file=sys.stderr)
            continue
        if "InteractionAction" not in m["components"][:m["read"]]:
            continue
        if m["shared_timers"]:
            print(f"{t}: shared timers {m['shared_timers']} not written", file=sys.stderr)
            continue
        rows.append({"id": t, "components": m["components"][:m["read"]], "model": m["animated_object"],
                     "sharedStates": m["shared_states"], "questFlagStates": m["quest_flag_states"],
                     "talk": talk.get(npc_rows.get(t), [])})
        print(f"{t}: {', '.join(rows[-1]['components'])}", file=sys.stderr)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("[\n" + ",\n".join(" " + json.dumps(r, separators=(",", ":")) for r in rows) + "\n]\n")
    print(f"{len(rows)} types -> {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
