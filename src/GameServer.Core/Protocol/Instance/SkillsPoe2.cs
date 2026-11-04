namespace GameServer.Core.Protocol.Instance;

/// <summary>A skill the server grants a PoE2 Actor. Id is the instance's u32 (PoE1's was a u16); it shares the skill
/// manager with the type's basic actions, whose ids are ActionTypes HASH16 &amp; 0x3fff as in PoE1, so the server's ids
/// start at <see cref="ActorSkillsPoe2.FirstSkillId"/>. Rows are GrantedEffectsPerLevel (0x74-byte rows) and
/// GrantedEffectStatSetsPerLevel (0xb9-byte rows) indices.</summary>
public sealed record GrantedSkillPoe2(uint Id, uint EffectsPerLevelRow, uint StatSetsPerLevelRow,
    IReadOnlyList<(uint Key, int Value)>? Stats = null, byte Source = 0);

/// <summary>
/// The Actor's skill list (PORTING-POE2.md wave 21). The Actor body (0x142147300) reads it on create through
/// 0x142149510 (r8 = 1), always, right after its flags byte f0 (blob byte 19 of the Actor); later updates read it only
/// when f0 bit 3 is set.
/// </summary>
public static class ActorSkillsPoe2
{
    /// <summary>No ActionTypes HASH16 masks to it (0.5.5e), as in PoE1.</summary>
    public const uint FirstSkillId = 0x0102;

    /// <summary>The list (0x14218a9d0, PoE1's 0x142197b10):
    /// u8 n × skill record (0x14218b130 → 0x142188d50);
    /// u8 m × (u32 row of a 0x152-byte table, u32, u32) — PoE1's soul budgets;
    /// u8 c × u8 (into u32s at +0x190), new in PoE2;
    /// u32 k × (u32 skill id, u32 c × (u32, u32, u32, u32)) — cooldowns.</summary>
    public static void WriteList(BlobWriter w, IReadOnlyList<GrantedSkillPoe2> skills)
    {
        w.U8(checked((byte)skills.Count));
        foreach (var s in skills) WriteRecord(w, s);
        w.U8(0).U8(0).U32(0);
    }

    /// <summary>One record (0x142188d50):
    /// <code>
    /// u32 skill id                                       → +0x40
    /// u8  stats source: 0 = the Actor's stats, else the sub-stats whose +0xd8 is it (a weapon set?)
    /// GEPL reference (0x142684090): u8, u32 GEPL row, stat-set reference (0x142683db0: u8 a, [u8 if a], u32 GESSPL
    ///     row), one more stat-set reference per GrantedEffects AdditionalStatSets entry (GE +0xb8)
    /// u8  flags: bit0 → u32 parent object id, u32; bit4 → the stats come from the parent skill
    /// u8  n supports × (GEPL reference, u32)
    /// u32, u32, u32, u8                                  (PoE1: +0xc0, +0xc4, cooldown ms, +0xcc)
    /// compact stats list (count × (compact key, compact value)): the skill's own stats
    /// one stats list per additional stat set
    /// only when GrantedEffects MinionType (GE +0x10d) is set: u8 count × minion entries (not written)
    /// u8 x, then a stats list when x ≠ 0
    /// </code></summary>
    public static void WriteRecord(BlobWriter w, GrantedSkillPoe2 s)
    {
        w.U32(s.Id).U8(s.Source)
         .U8(0).U32(s.EffectsPerLevelRow)
         .U8(0).U32(s.StatSetsPerLevelRow)
         .U8(0)              // flags
         .U8(0)              // supports
         .U32(0).U32(0).U32(0).U8(0)
         .Stats(s.Stats)
         .U8(0);
    }
}
