"""Writes data/poe2/monster-varieties.json: the PoE2 monster varieties the area captures place, with the components the
client deserializes for them (PORTING-POE2.md, wave 20).

    python scripts/re/poe2/monster_varieties.py ["G:/SteamLibrary/steamapps/common/Path of Exile 2/Bundles2"]

- The varieties: every `Metadata/Monsters/...` type in data/walkability/*.objects.json (scripts/walkmaps/poe2_walkmap.py)
  that is an Id of Data/Balance/MonsterVarieties.datc64 (row size 1030: Id +0, BaseMonsterTypeIndex +72, both
  strings). The client registers "<Id>@<level>" (S->C 0x21) and builds the object from the base type's .ot.
- The components: ot_components.py's merge of the .ot chain (Positioned first, then the sections parent-first, a name
  keeping its first place; client blocks are not deserialized), checked live on CourtGuardZombie (14 components).
- The StateMachine's shared states: define_shared_state and PoE2's define_shared_checkpoint_state, in .ot order.
- The variety's combat columns (dat.py): Name, LifeMultiplier, DamageMultiplier, ExperienceMultiplier (percent),
  AttackSpeed (ms), Minimum/MaximumAttackDistance, MovementSpeed, ObjectSize (wave 22).

It also writes data/poe2/default-monster-stats.json: DefaultMonsterStats by level (row = level - 1), the base life,
damage, experience, accuracy, evasion and armour the variety's multipliers scale.

Files come from the Steam bundles through scripts/re/poe2/steamdump and are cached in Tools/re/out/poe2.
"""
import glob
import json
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
sys.path.insert(0, os.path.dirname(HERE))
import ot_components  # noqa: E402  (parse and merge of .ot files)
import dat  # noqa: E402  (.datc64 tables with the community schema)

BUNDLES = sys.argv[1] if len(sys.argv) > 1 else r"G:\SteamLibrary\steamapps\common\Path of Exile 2\Bundles2"
CACHE = os.path.join(REPO, "Tools", "re", "out", "poe2")
OUT = os.path.join(REPO, "data", "poe2", "monster-varieties.json")
LEVELS_OUT = os.path.join(REPO, "data", "poe2", "default-monster-stats.json")
COMBAT = ["Name", "LifeMultiplier", "DamageMultiplier", "ExperienceMultiplier", "AttackSpeed", "MinimumAttackDistance",
          "MaximumAttackDistance", "MovementSpeed", "ObjectSize"]
LEVEL_FIELDS = {"MonsterLife": "life", "Damage": "damage", "Experience": "experience", "Accuracy": "accuracy",
                "Evasion": "evasion", "Armour": "armour"}
VARIETIES = "Data/Balance/MonsterVarieties.datc64"
ROW_SIZE, ID, BASE_TYPE = 1030, 0, 72


def cached(path):
    return os.path.join(CACHE, path.replace("/", "_"))


def fetch(paths):
    todo = [p for p in paths if not os.path.exists(cached(p)) and not os.path.exists(cached(p) + ".missing")]
    if not todo:
        return
    cmd = ["dotnet", "run", "--project", os.path.join(HERE, "steamdump"), "-c", "Release", "--", BUNDLES, CACHE] + todo
    out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", check=True).stdout
    for line in out.splitlines():
        if line.endswith(": not found"):
            open(cached(line[:-len(": not found")]) + ".missing", "w").close()


def varieties():
    """MonsterVarieties Id -> (row, base type)."""
    fetch([VARIETIES])
    d = open(cached(VARIETIES), "rb").read()
    n = struct.unpack_from("<I", d, 0)[0]
    var = d.index(b"\xbb" * 8)
    if (var - 4) != n * ROW_SIZE:
        raise SystemExit(f"{VARIETIES}: {n} rows of {(var - 4) / max(n, 1)} bytes, expected {ROW_SIZE}")

    def text(row, field):
        p = var + struct.unpack_from("<Q", d, 4 + row * ROW_SIZE + field)[0]
        e = p
        while d[e:e + 2] != b"\0\0":
            e += 2
        return d[p:e].decode("utf-16le")

    return {text(i, ID): (i, text(i, BASE_TYPE)) for i in range(n)}


def load(types):
    """Parsed .ot files of the types and their extends chains."""
    files, frontier = {}, sorted(set(types))
    while frontier:
        fetch([p + ".ot" for p in frontier])
        nxt = []
        for p in frontier:
            f = cached(p + ".ot")
            if not os.path.exists(f):
                continue
            b = open(f, "rb").read()
            files[p] = ot_components.parse(b.decode("utf-16") if b[:2] in (b"\xff\xfe", b"\xfe\xff") else b.decode("utf-8"))
            nxt += [e for e in files[p]["extends"] if e != "nothing" and e not in files]
        frontier = sorted(set(nxt))
    return files


def main():
    placed = sorted({o["type"] for f in glob.glob(os.path.join(REPO, "data", "walkability", "*.objects.json"))
                     for o in json.load(open(f, encoding="utf-8")) if o["type"].startswith("Metadata/Monsters/")})
    table = varieties()
    wanted = [t for t in placed if t in table]
    files = load([table[t][1] for t in wanted])
    columns = dat.table("MonsterVarieties")
    rows = []
    for t in wanted:
        row, base = table[t]
        m = ot_components.merge(base, files, {})
        if m["missing"]:
            print(f"{t}: .ot missing for {', '.join(m['missing'])}", file=sys.stderr)
            continue
        if m["shared_timers"]:
            print(f"{t}: shared timers {m['shared_timers']} not written", file=sys.stderr)
            continue
        rows.append({"id": t, "row": row, "components": m["components"][:m["read"]],
                     "sharedStates": [name for name, _ in m["shared_states"]],
                     **{k[0].lower() + k[1:]: columns[row][k] for k in COMBAT}})
        print(f"{t} (row {row}, {base}): {len(rows[-1]['components'])} components", file=sys.stderr)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("[\n" + ",\n".join(" " + json.dumps(r, separators=(",", ":")) for r in rows) + "\n]\n")
    print(f"{len(rows)} varieties -> {OUT}", file=sys.stderr)
    levels = [{"level": i + 1, **{v: r[k] for k, v in LEVEL_FIELDS.items()}} for i, r in enumerate(dat.table("DefaultMonsterStats"))]
    with open(LEVELS_OUT, "w", encoding="utf-8") as f:
        f.write("[\n" + ",\n".join(" " + json.dumps(r, separators=(",", ":")) for r in levels) + "\n]\n")
    print(f"{len(levels)} levels -> {LEVELS_OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
