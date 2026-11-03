namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The smallest component blob for a PoE2 player object (`Metadata/Characters/{class}`, base Character.ot), read by
/// 0x141e712a0: u8 object flags, base data (0x141e715a0), each component's vtable +0x98 deserializer in the object's
/// component order, then tasks. Order and sizes were traced live with Frida (2026-11-03, PORTING-POE2.md wave 10):
/// Positioned 17, Stats 13, Pathfinding 0, Buffs 2, Life 125, Animated 1, Player 39, PlayerClass 0, Inventories 2,
/// Actor 40. Only the position and the first two Life pools are mapped; everything else is sent as zeros, which the
/// client accepts (the HUD shows the life and energy shield values). Mana, the name and the class are not mapped yet.
/// </summary>
public sealed record PlayerObjectPoe2(int GridX, int GridY, uint Life, uint EnergyShield = 0)
{
    public const int BlobLength = 242;

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0);            // object flags (bit0 → +0x8d|1, bit1 → +0x8d|4)
        w.U8(0);            // base data entry count
        // Positioned (0x141dd89a0): i32 x, i32 y, f32 rotation, u16 +0x1e0, three flag bytes (PoE1 had two). The type's
        // +0x29 prefix is off. The client clamps the position to the area (size × 23).
        w.I32(GridX).I32(GridY).F32(0).U16(0).U8(0).U8(0).U8(0);
        w.Zeros(13);        // Stats (0x142181160)
        w.U16(0);           // Buffs (0x141db5080): count
        // Life (0x141d8c260): pool +0x1a8 (life; current life is the first u32, ≤ 0 runs the death path 0x141d87120),
        // base +0x200, pool +0x240 (energy shield), base +0x298, pools +0x2e0 and +0x330, a fixed array of three
        // (u32, i16) at +0x380, one more (u32, i16) at +0x398, flags u8. Pool = u32 current, u32, u16, u32, u32 (as PoE1);
        // base = 14 bytes.
        Pool(w, Life); w.Zeros(14);
        Pool(w, EnergyShield); w.Zeros(14);
        Pool(w, 0); Pool(w, 0);
        w.Zeros(3 * 6).Zeros(6).U8(0);
        w.U8(0);            // Animated (0x141da66c0): flags
        w.Zeros(39);        // Player (0x141dcf4a0)
        w.U8(0).U8(0);      // Inventories (0x1416e6d70)
        w.Zeros(40);        // Actor (0x1416d8210): 40 bytes while alive (48 at zero life)
        w.U8(0);            // tasks (0x141e7dd60)
        return w.ToArray();
    }

    private static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
}
