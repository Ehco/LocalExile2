using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>An attack skill's weapon values, from the stats the server granted it (<see cref="Poe2StartingSkills"/>):
/// damage per hit, the attack time and the reach in cells.</summary>
public sealed record AttackPoe2(int MinimumDamage, int MaximumDamage, int AttackMs, int Range)
{
    /// <summary>The attack of a skill whose stats carry a main-hand damage range and attack time, else null (the dodge
    /// roll has no stats).</summary>
    public static AttackPoe2? Of(GrantedSkillPoe2 skill)
    {
        var stats = (skill.Stats ?? []).ToDictionary(s => s.Key, s => s.Value);
        if (!stats.TryGetValue(Poe2StartingSkills.MainHandMinimumTotalDamage, out int min) ||
            !stats.TryGetValue(Poe2StartingSkills.MainHandMaximumTotalDamage, out int max) ||
            !stats.TryGetValue(Poe2StartingSkills.MainHandBaseWeaponAttackDurationMs, out int ms)) return null;
        int range = stats.GetValueOrDefault(Poe2StartingSkills.MainHandMaximumAttackDistance, Poe2StartingSkills.UnarmedAttackDistance);
        return new(min, Math.Max(min, max), ms, range);
    }
}

/// <summary>PoE2 hits and life (PORTING-POE2.md, wave 22).</summary>
public static class InstanceCombatPoe2
{
    public const ushort LifeUpdateId = 0x1cf;

    /// <summary>The Life component's pools in <see cref="LifeUpdate"/> (handler 0x141d8ea60): 0 life (+0x1a8), 1 +0x200,
    /// 2 energy shield (+0x240), 3 +0x298, 4 mana (+0x2e0), 5 +0x330.</summary>
    public const byte LifePool = 0, EnergyShieldPool = 2, ManaPool = 4;

    // The flags byte's field bits (reader 0x141f6cb20); the low 3 bits are the pool.
    const byte HasCurrent = 0x80, HasU24 = 0x40, HasU28 = 0x20, HasU2e = 0x10;

    /// <summary>
    /// S→C 0x1cf (world group, read 0x141f6cb20; the world handler routes 0x1cf–0x1d1 and 0x296 to the Life component's
    /// 0x141d8ea60), PoE1's life update 0x1b1 made compact: object header, u8 flags (pool in bits 0–2; bit 7: a compact
    /// current value, bit 6: u32 → +0x24, bit 5: u32 → +0x28, bit 4: u16 → +0x2e; all big-endian), u8 (+0x30, passed to
    /// the death slot), then the fields. The pool store 0x141d88f20 copies every field whether sent or not, so all are
    /// sent (0 as in the blob). For the life pool, life going from ≥ 1 to ≤ 0 calls the owner's death slot (vtable
    /// +0x130), and back above 0 its revive slot (+0x158), as in PoE1.
    /// </summary>
    public static byte[] LifeUpdate(uint objectId, int current, byte pool = LifePool, byte reason = 0)
    {
        var w = new LoginWriter().U16(LifeUpdateId).U32(objectId).U32(0).U8(0).U16(0)
            .U8((byte)(HasCurrent | HasU24 | HasU28 | HasU2e | (pool & 7))).U8(reason);
        InstanceProtocol.CompactInt(w, current);
        return w.U32(0).U32(0).U16(0).ToArray();
    }

    public const ushort StatsUpdateId = 0x1d3;

    /// <summary>Stats keys (Stats.datc64 row + 1): hide_mini_life_bar.</summary>
    public const uint HideMiniLifeBar = 0x2733;

    /// <summary>
    /// S→C 0x1d3 (world group, read 0x141f6de60; the world handler routes 0x1d3–0x1d7 to the Stats component's
    /// 0x1421820a0), PoE1's 0x1b3 with a u8 after the object header: u8 (+0x20, 0 here), compact count, then per stat a
    /// compact key and a compact signed value.
    /// </summary>
    public static byte[] StatsUpdate(uint objectId, IReadOnlyList<(uint Key, int Value)> stats)
    {
        var w = new LoginWriter().U16(StatsUpdateId).U32(objectId).U32(0).U8(0).U16(0).U8(0);
        InstanceProtocol.CompactUInt(w, (uint)stats.Count);
        foreach (var (key, value) in stats)
        {
            InstanceProtocol.CompactUInt(w, key);
            InstanceProtocol.CompactInt(w, value);
        }
        return w.ToArray();
    }
}
