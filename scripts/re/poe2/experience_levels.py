"""Writes data/tables/ExperienceLevels.json from PoE2's Data/Balance/ExperienceLevels.datc64 (PORTING-POE2.md, wave 24):
each level's starting experience, the table the Gateway reads (CharacterStats.LevelStarts) for the player's experience
and the level a kill reaches.

    python scripts/re/poe2/experience_levels.py

PoE2 0.5.5e's 100 rows match PoE1's (level 2: 525, level 37: 12 361 842, level 100: 4 250 334 444), so a PoE1 extract
(scripts/dev/extract-data.ps1) already there is replaced by the same values.

Files come from the Steam bundles through scripts/re/poe2/steamdump and are cached in Tools/re/out/poe2.
"""
import json
import os
import sys

import dat

OUT = os.path.join(dat.REPO, "data", "tables", "ExperienceLevels.json")


def main():
    rows = [{"Level": r["Level"], "Experience": r["Experience"]} for r in dat.table("ExperienceLevels")]
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("[\n" + ",\n".join(" " + json.dumps(r, separators=(",", ":")) for r in rows) + "\n]\n")
    print(f"{len(rows)} levels -> {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
