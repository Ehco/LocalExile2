using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The PoE2 client's plaintext instance opening (PathOfExileSteam.exe 0.5.5e; PORTING-POE2.md, wave 7), sent by
/// 0x14211fac0 right after the tick 0x142120cf0 logs "Connect time to instance server". 0x0F is PoE1's: u32 Ticket (login
/// 0x14's u32 at +0x34). 0x0E (vtable 0x1434e5798, write 0x141f373a0, read 0x141f37490) is bytes[8] Digest (hash over an
/// 8-byte constant, 0x14's Data8 and Time), u32 Time (from 0x142c28850, Unix seconds), u32 Address (0x14's u32 at +0x30),
/// u32 U32_28 (0), then one packed byte where PoE1 sent realm and flag: bits 0–3 the opening function's argument (global
/// 0x14458c75d, 4 live), bit 4 +0x2c, bit 5 +0x2d, bit 6 +0x2f, bit 7 +0x2e (the client sends them all 0).
/// </summary>
public sealed record InstanceOpeningPoe2(uint Ticket, byte[] Digest, uint Time, uint Address, uint U32_28, byte Packed)
{
    /// <summary>0x0F (2 + 4) followed by 0x0E (2 + 8 + 4 + 4 + 4 + 1).</summary>
    public const int Length = 29;

    public int Flag => Packed & 0x0f;
    public bool Bit4 => (Packed & 0x10) != 0;
    public bool Bit5 => (Packed & 0x20) != 0;
    public bool Bit6 => (Packed & 0x40) != 0;
    public bool Bit7 => (Packed & 0x80) != 0;

    /// <summary>Parses the opening, or null if fewer than <see cref="Length"/> bytes arrived.</summary>
    public static InstanceOpeningPoe2? TryRead(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < Length) return null;
        var r = new LoginReader(buf);
        if (r.U16() != InstanceProtocol.TicketId) throw new InvalidDataException("instance connection must start with 0x000F");
        uint ticket = r.U32();
        if (r.U16() != InstanceProtocol.HelloId) throw new InvalidDataException("expected 0x000E after 0x000F");
        return new InstanceOpeningPoe2(ticket, r.Fixed(8), r.U32(), r.U32(), r.U32(), r.U8());
    }
}
