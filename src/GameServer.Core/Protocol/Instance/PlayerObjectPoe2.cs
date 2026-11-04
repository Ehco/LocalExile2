namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The smallest component blob for a PoE2 player object (`Metadata/Characters/{class}`, base Character.ot), read by
/// 0x141e712a0: u8 object flags, base data (0x141e715a0), each component's vtable +0x98 deserializer in the object's
/// component order, then tasks. Order and sizes were traced live with Frida (2026-11-03, PORTING-POE2.md wave 10):
/// Positioned 17, Stats 13, Pathfinding 0, Buffs 2, Life 125, Animated 1, Player 39, PlayerClass 0, Inventories 2,
/// Actor 40 (sizes with no stats and an empty name). The position, the stats, the life, energy shield and mana pools,
/// and the Player component's name, class and experience are mapped; everything else is sent as zeros, which the client
/// accepts (the HUD shows the three pools).
/// </summary>
/// <param name="ClassRow">Characters.datc64 row (<see cref="Accounts.Poe2Classes"/>); 1 is the Witch.</param>
/// <param name="Experience">The client derives the level from it (0x141dd1360 → +0x204), as PoE1 does.</param>
/// <param name="Stats">Final stat values by client key (<see cref="Poe2CharacterStats"/>): the character panel and the
/// HUD's maximums show them.</param>
/// <param name="Skills">The Actor's granted skills (<see cref="ActorSkillsPoe2"/>): the skill bar offers them.</param>
public sealed record PlayerObjectPoe2(int GridX, int GridY, uint Life, uint EnergyShield = 0, uint Mana = 0,
    string Name = "", byte ClassRow = 1, uint Experience = 0, IReadOnlyList<(uint Key, int Value)>? Stats = null,
    IReadOnlyList<GrantedSkillPoe2>? Skills = null)
{
    /// <summary>The blob's length with an empty name and no skills; each name character adds two bytes.</summary>
    public const int BlobLength = 242;

    /// <summary>Where the Player component starts in the blob.</summary>
    public const int PlayerOffset = 160;

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0x02);         // object flags (bit0 → +0x8d|1, bit1 → +0x8d|4: in the wake zone)
        w.U8(0);            // base data entry count
        // Positioned (0x141dd89a0): i32 x, i32 y, f32 rotation, u16 +0x1e0, three flag bytes (PoE1 had two). The type's
        // +0x29 prefix is off. The client clamps the position to the area (size × 23).
        w.I32(GridX).I32(GridY).F32(0).U16(0).U8(0).U8(0).U8(0);
        // Stats (0x142181160): compact count of (compact key, compact value) pairs as PoE1 (0x1426acb60 sets each),
        // then 12 bytes.
        w.Stats(Stats).Zeros(12);
        w.U16(0);           // Buffs (0x141db5080): count
        // Life (0x141d8c260): pool +0x1a8 (life; current life is the first u32, ≤ 0 runs the death path 0x141d87120),
        // base +0x200, pool +0x240 (energy shield), base +0x298, pools +0x2e0 (mana: the HUD showed 30 sent here, live
        // 2026-11-03) and +0x330, a fixed array of three (u32, i16) at +0x380, one more (u32, i16) at +0x398, flags u8.
        // Pool = u32 current, u32, u16, u32, u32 (as PoE1); base = 14 bytes.
        Pool(w, Life); w.Zeros(14);
        Pool(w, EnergyShield); w.Zeros(14);
        Pool(w, Mana); Pool(w, 0);
        w.Zeros(3 * 6).Zeros(6).U8(0);
        w.U8(0);            // Animated (0x141da66c0): flags
        // Player (0x141dcf4a0): u32-counted UTF-16 name (0x1421c04a0 → +0x1b0), u8 Characters row (PlayerClass +0x158),
        // u32 experience, u32 +0x1d0, u32 +0x1d4, u16 +0x200, u16 +0x202, two flag sets (0x141deebf0, u16 count), 5 raw
        // bytes +0x278, two u16 dat keys (+0x280, +0x290), u8 flags (bits 0-3), u8 +0x205, u8 +0x3e7, u8 count of u32,
        // u8 count (0x141deecb0). 35 bytes after an empty name.
        w.WStr32(Name).U8(ClassRow).U32(Experience);
        w.Zeros(30);
        w.U8(0).U8(0);      // Inventories (0x1416e6d70)
        // Actor (0x1416d8210 → body 0x142147300): three (u8, u8, WStr32) entries, then 0x142149510's flags byte f0 and,
        // on create, the skill list; 40 bytes in all with no skills while alive (48 at zero life).
        w.Zeros(18).U8(0);
        ActorSkillsPoe2.WriteList(w, Skills ?? []);
        w.Zeros(14);
        w.U8(0);            // tasks (0x141e7dd60)
        return w.ToArray();
    }

    private static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
}
