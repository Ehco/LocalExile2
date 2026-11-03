using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// S→C 0x1e on PoE2 (class 0x1435079d8, write 0x14211cb00, read 0x14211d340; dispatcher 0x1421220f0 case at 0x14212269f,
/// which logs "Client-Safe Instance ID" and "Generating level"): PoE1's <see cref="AreaInstance"/> (0x1d) under a new id.
/// PoE2's 0x1d is something else (u16 at +0x18 compared with clocks, likely PoE1's 0x1c pong). The shared fields keep their
/// wire order with struct offsets 0xc higher; names are the PoE2 offsets (B2a = PoE1 B1e, …, B34 = PoE1 B28). New:
/// B10 (u8, first), U24 (u32, after B34), a u16 +0x1e when flag +0x2d bit 3 is set, and two lists at the end (u8 count of
/// 8-byte items +0x100, vec16&lt;u16&gt; +0x118). The flag bytes (+0x2d..+0x2f) are written as 0, so every flag-gated part
/// (league stats on +0x2d bit 7, terrain and lists on +0x2e bits 0–3) stays off.
/// </summary>
public sealed record AreaInstancePoe2(uint InstanceId, ushort AreaHash, uint Seed, byte Level, string Text = "",
    byte B10 = 0, byte B2a = 3, byte B2c = 0, byte B2b = 2, byte B30 = 0x23, byte B29 = 0, byte B34 = 0x3c, uint U24 = 0)
{
    public const ushort Id = 0x1e;

    public byte[] Encode() => new LoginWriter()
        .U16(Id).U8(B10).U32(InstanceId).U16(AreaHash).WStr(Text).U32(Seed)
        .U8(B2a).U8(B2c).U8(B2b).U8(B30).Bytes([0, 0, 0]).U8(Level).U8(B29).U8(B34).U32(U24)
        .U16(0)  // vec16<u32> +0x70
        .U16(0)  // vec16<u8> +0x88
        .U8(0)   // u8 list +0x58 (PoE1: achievement sets)
        .U16(0)  // vec16<u16> +0xe8
        .U8(0)   // u8 count of 8-byte items +0x100
        .U16(0)  // vec16<u16> +0x118
        .ToArray();
}
