"""Writes data/poe2/gems.json: PoE2's skill gems and uncut gems (PORTING-POE2.md, wave 30).

    python scripts/re/poe2/gems.py

"skills": one entry per SkillGems row whose base item is under Metadata/Items/Gems/ or Metadata/Items/Gem/ and has a
granted effect: the base item (id, BaseItemTypes row, name, HASH32), the gem's colour (GemColour: 1 red, 2 green,
3 blue, 4 white), its kind ("skill", "support" or "spirit" from the effect's IsSupport and the GemType), MinLevelReq, the
attribute requirement percents, the SkillGems and GemEffects rows (what the gem window may send), the GemEffects tags, the granted effect (GrantedEffects row and Id, its ActiveSkills Id
and name, the effect HASH32) and, per gem level, the GrantedEffectsPerLevel row and the GrantedEffectStatSetsPerLevel
rows the Actor's skill record refers to (the effect's StatSet first, then its AdditionalStatSets). Only the first of a
gem's GemEffects is taken (the regular gem; the others are variants).
"uncut": the uncut gems (UncutGems tiers and the other Metadata/Items/Gems/*Uncut* bases): id, row, name, HASH32, tier
(the gem level the cut gem gets) and kind.

The server grants a socketed gem's skill from this (Poe2Gems); the items themselves need data/poe2/item-types.json to
hold the gems too (item_types.py). Files come from the Steam bundles through steamdump, cached in Tools/re/out/poe2.
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
sys.path.insert(0, HERE)
import dat  # noqa: E402

OUT = os.path.join(REPO, "data", "poe2", "gems.json")
PREFIXES = ("Metadata/Items/Gems/", "Metadata/Items/Gem/")


def main():
    bases, gems, effects = dat.table("BaseItemTypes"), dat.table("SkillGems"), dat.table("GrantedEffects")
    gem_effects, active = dat.table("GemEffects"), dat.table("ActiveSkills")
    per_level = {}
    for r in dat.table("GrantedEffectsPerLevel"):
        per_level.setdefault(r["GrantedEffect"], {})[r["Level"]] = r["_row"]
    stat_rows = {}
    for r in dat.table("GrantedEffectStatSetsPerLevel"):
        stat_rows[(r["StatSet"], r["GemLevel"])] = r["_row"]
    skills = []
    for g in gems:
        base = bases[g["BaseItemType"]]
        if not base["Id"].startswith(PREFIXES) or not g["GemEffects"]:
            continue
        fx = gem_effects[g["GemEffects"][0]]
        if fx["GrantedEffect"] is None:
            continue
        e = effects[fx["GrantedEffect"]]
        kind = "support" if e["IsSupport"] else ("spirit" if g["GemType"] == 2 else "skill")
        skill = active[e["ActiveSkill"]] if e["ActiveSkill"] is not None else None
        levels = []
        for level, gepl in sorted(per_level.get(e["_row"], {}).items()):
            sets = [stat_rows.get((s, level)) for s in [e["StatSet"], *(e["AdditionalStatSets"] or [])]]
            if sets[0] is None:
                continue
            levels.append([level, gepl, [s for s in sets if s is not None]])
        if not levels:
            continue
        skills.append({"id": base["Id"], "row": base["_row"], "name": base["Name"], "hash": base["HASH32"],
                       "skillGemsRow": g["_row"], "gemEffectsRow": g["GemEffects"][0],
                       "colour": g["GemColour"], "kind": kind, "gemType": g["GemType"], "minLevel": g["MinLevelReq"],
                       "strength": g["StrengthRequirementPercent"], "dexterity": g["DexterityRequirementPercent"],
                       "intelligence": g["IntelligenceRequirementPercent"], "tags": fx["GemTags"] or [],
                       "effect": {"row": e["_row"], "id": e["Id"], "hash": e["HASH32"],
                                  "activeSkill": skill and skill["Id"], "skillName": skill and skill["DisplayedName"]},
                       "levels": levels})
    tiers = {r["BaseItemType"]: r["Tier"] for r in dat.table("UncutGems")}
    uncut = []
    for b in bases:
        if not b["Id"].startswith(PREFIXES) or "Uncut" not in b["Id"]:
            continue
        kind = "support" if "Support" in b["Id"] else ("spirit" if "Reservation" in b["Id"] else "skill")
        uncut.append({"id": b["Id"], "row": b["_row"], "name": b["Name"], "hash": b["HASH32"], "tier": tiers.get(b["_row"], 0), "kind": kind})
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write('{"skills":[\n' + ",\n".join(" " + json.dumps(s, separators=(",", ":"), ensure_ascii=False) for s in skills)
                + '\n],"uncut":[\n' + ",\n".join(" " + json.dumps(u, separators=(",", ":"), ensure_ascii=False) for u in uncut) + "\n]}\n")
    print(f"{len(skills)} gems, {len(uncut)} uncut -> {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
