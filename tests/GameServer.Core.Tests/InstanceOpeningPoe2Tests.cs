using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>The PoE2 client's instance opening and the first S→C messages (PORTING-POE2.md, waves 7–8).</summary>
public class InstanceOpeningPoe2Tests
{
    // The first bytes the PoE2 Steam client (0.5.5e) sent to 127.0.0.1:6112 after login 0x14 with ticket 1, 2026-11-03.
    private static readonly byte[] RealOpening = Convert.FromHexString("000F00000001000E12BEA056029C953E6AE9B7C87F0000010000000004");

    [Fact]
    public void Real_opening_parses_as_ticket_then_hello()
    {
        var o = InstanceOpeningPoe2.TryRead(RealOpening)!;
        Assert.Equal(1u, o.Ticket);
        Assert.Equal("12BEA056029C953E", Convert.ToHexString(o.Digest));
        Assert.Equal(0x6AE9B7C8u, o.Time); // 3 s after the login server's 0x1b time, 1793701829
        Assert.Equal((0x7F000001u, 0u), (o.Address, o.U32_28));
        Assert.Equal(4, o.Flag);
        Assert.Equal((false, false, false, false), (o.Bit4, o.Bit5, o.Bit6, o.Bit7));
    }

    [Fact]
    public void Last_byte_packs_the_flag_in_the_low_nibble_and_four_bools_above()
    {
        byte[] wire = [.. RealOpening];
        wire[^1] = 0xa7;
        var o = InstanceOpeningPoe2.TryRead(wire)!;
        Assert.Equal(7, o.Flag);
        Assert.Equal((false, true, false, true), (o.Bit4, o.Bit5, o.Bit6, o.Bit7));
    }

    [Fact]
    public void Opening_needs_all_29_bytes_and_starts_with_0x0f()
    {
        Assert.Equal(29, InstanceOpeningPoe2.Length);
        Assert.Null(InstanceOpeningPoe2.TryRead(RealOpening.AsSpan(0, 28)));
        byte[] bad = [.. RealOpening];
        bad[1] = 0x0e;
        Assert.Throws<InvalidDataException>(() => InstanceOpeningPoe2.TryRead(bad));
    }

    [Fact]
    public void Poe1_parser_does_not_accept_the_poe2_opening()
    {
        Assert.Null(InstanceProtocol.TryReadOpening(RealOpening));
    }

    [Fact]
    public void Area_instance_moved_to_0x1e_and_follows_the_poe2_writer()
    {
        // Writer 0x14211cb00: id, u8 +0x10, u32 id, u16 area, wstr, u32 seed, u8 +0x2a, u8 +0x2c, u8 +0x2b, u8 +0x30,
        // bytes[3] flags +0x2d, u8 level +0x28, u8 +0x29, u8 +0x34, u32 +0x24, vec16<u32> +0x70, vec16<u8> +0x88,
        // u8 list +0x58, vec16<u16> +0xe8, u8 list +0x100, vec16<u16> +0x118 (flag-gated parts off).
        byte[] wire = new AreaInstancePoe2(InstanceId: 0x01020304, AreaHash: 0x3f70, Seed: 7, Level: 2, Text: "a", B10: 9, U24: 0x11223344).Encode();
        Assert.Equal("001E" + "09" + "01020304" + "3F70" + "000161" + "00000007" + "03" + "00" + "02" + "23" + "000000" + "02" + "00" + "3C"
            + "11223344" + "0000" + "0000" + "00" + "0000" + "00" + "0000", Convert.ToHexString(wire));
    }

    [Fact]
    public void Area_instance_defaults_match_the_poe1_ones_for_the_shared_fields()
    {
        byte[] poe1 = new AreaInstance(InstanceId: 1, AreaHash: 0x3f70, Seed: 7, Level: 0).Encode();
        byte[] poe2 = new AreaInstancePoe2(InstanceId: 1, AreaHash: 0x3f70, Seed: 7, Level: 0).Encode();
        // PoE1 0x1d after the id: u32 .. B28 is 4+2+2+4+4+3+3 = 22 bytes; PoE2 inserts B10 before them.
        Assert.Equal(poe1.AsSpan(2, 22).ToArray(), poe2.AsSpan(3, 22).ToArray());
        Assert.Equal(AreaInstancePoe2.Id, (ushort)(poe2[0] << 8 | poe2[1]));
    }

    [Fact]
    public void Start_encryption_carries_one_byte_on_poe2()
    {
        // Live 2026-11-03: after a bare 00 11 the client took our first encrypted byte as a 0x11 field and decrypted from
        // the second ("Unable to deserialise packet with pid 6628/40109. Last PID was 17" both match that offset exactly).
        Assert.Equal([0x00, 0x11, 0x00], InstanceProtocolPoe2.StartEncryption());
    }
}
