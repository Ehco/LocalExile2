using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

/// <summary>PoE2 login layouts that differ from PoE1 (PORTING-POE2.md, wave 4).</summary>
public class LoginMessagePoe2Tests
{
    private static byte[] B(int n, byte v) => Enumerable.Repeat(v, n).ToArray();

    private static readonly (LoginDirection Dir, ILoginMessage Msg)[] Samples =
    [
        (LoginDirection.ClientToServer, new TokenLoginPoe2(0x40, B(32, 0xbb), B(32, 0xaa), 0, B(159, 7))),
        (LoginDirection.ServerToClient, new LoginResultPoe2(7, 0, "a", B(32, 0xdd), 0x20, 1, [1, 2])),
        (LoginDirection.ClientToServer, new U32Message(0x18, 7)),
        (LoginDirection.ServerToClient, new CharacterListPoe2(7, [Entry("Hero")], 0, 3, 1, 2, [9], [new(0x10, -5), new(0x20, 0x12345)], 4, 5)),
        (LoginDirection.ClientToServer, new PlayPoe2(0x80, [new(1, 2), new(3, 4)])),
        (LoginDirection.ClientToServer, new CharacterDetailRequestPoe2(7, 2)),
        (LoginDirection.ServerToClient, new U32U16U16(0x16, 7, 0x1234, 0x5678)),
        (LoginDirection.ServerToClient, new InstanceInfoPoe2("127.0.0.1", 6112, 1, 0xabcd, [0x61, 0x62], 2, B(64, 0x11), B(8, 0x22), 0)),
    ];

    private static CharacterEntryPoe2 Entry(string name) => new(name, "Standard", 0, 0, 12, 0, 3, 0, 1, 0, 0, 0, 0);

    public static TheoryData<int> SampleIndexes() => [.. Enumerable.Range(0, Samples.Length)];

    [Theory]
    [MemberData(nameof(SampleIndexes))]
    public void Poe2_messages_round_trip_and_need_all_their_bytes(int index)
    {
        var (dir, msg) = Samples[index];
        byte[] wire = LoginMessage.Encode(msg);

        Assert.True(LoginCodec.TryDecode(dir, wire, out var decoded, out int consumed, LoginDialect.Poe2));
        Assert.Equal(wire.Length, consumed);
        Assert.Equal(msg.GetType(), decoded!.GetType());
        Assert.Equal(wire, LoginMessage.Encode(decoded));

        for (int n = 0; n < wire.Length; n++)
            Assert.False(LoginCodec.TryDecode(dir, wire.AsSpan(0, n), out _, out _, LoginDialect.Poe2), $"decoded from {n} of {wire.Length} bytes");
    }

    [Fact]
    public void Token_login_sends_the_token_length_before_the_fixed_fields_and_the_token_last()
    {
        // Writer 0x1420e3670: id, u16 token length, u8 +0x68, bytes[32] +0x48, bytes[32] +0x28, u8 +0x69, token bytes.
        // Same sizes as the live capture of 2026-11-03 (229 bytes, 159-byte token), synthetic contents.
        byte[] wire = [0x00, 0x05, 0x00, 0x9f, 0x40, .. B(32, 0xbb), .. B(32, 0xaa), 0x00, .. B(159, 7)];

        Assert.True(LoginCodec.TryDecode(LoginDirection.ClientToServer, wire, out var m, out int consumed, LoginDialect.Poe2));
        var login = Assert.IsType<TokenLoginPoe2>(m);
        Assert.Equal(229, consumed);
        Assert.Equal(0x40, login.Realm);
        Assert.Equal(B(32, 0xbb), login.MachineIdB);
        Assert.Equal(B(32, 0xaa), login.MachineIdA);
        Assert.Equal(0, login.B69);
        Assert.Equal(B(159, 7), login.Token);
    }

    [Fact]
    public void Login_result_starts_with_a_u32_before_the_result()
    {
        // Reader 0x14214dbe0: u32 +0x10, u16 +0x14 (the result the dispatch checks), wstr, bytes[32], u8, u8, blob.
        byte[] wire = LoginMessage.Encode(new LoginResultPoe2(7, 0, "a", B(32, 0xdd), 0x20, 1, [1, 2]));
        Assert.Equal("0004" + "00000007" + "0000" + "000161" + new string('D', 64) + "20" + "01" + "00020102", Convert.ToHexString(wire));
    }

    [Fact]
    public void Character_list_request_carries_a_u32()
    {
        // Captured 2026-11-03 right after 0x04 (U32_10 = 0) and an empty 0x1b: 0018 00000000.
        Assert.True(LoginCodec.TryDecode(LoginDirection.ClientToServer, Convert.FromHexString("001800000000"), out var m, out int n, LoginDialect.Poe2));
        Assert.Equal(new U32Message(0x18, 0), m);
        Assert.Equal(6, n);
    }

    [Fact]
    public void Character_list_layout_follows_the_poe2_writer()
    {
        // Writer 0x14214df90: u32 +0x70, u8 count + entries (0x142716570), u8 +0x28, u8 +0x29, u16 +0x74, u16 +0x76,
        // blob +0x30, compact count + (u16, compact int) pairs +0x48, u32 +0x68, u32 +0x6c.
        var entry = new CharacterEntryPoe2("a", "b", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        byte[] wire = LoginMessage.Encode(new CharacterListPoe2(0x77, [entry], 0, 3, 0x74, 0x76, [0xee], [new(0x10, -5)], 0x68, 0x6c));

        string expectedEntry = "000161" + "000162" + "01" + "02" + "03" + "00000004" + "05" + "0006" + "0007" + "0008" + "0009" + "0A" + "0B";
        Assert.Equal("0015" + "00000077" + "01" + expectedEntry + "00" + "03" + "0074" + "0076" + "0001EE" + "01" + "0010" + "7B"
            + "00000068" + "0000006C", Convert.ToHexString(wire));
    }

    [Fact]
    public void Play_sends_a_flag_byte_and_a_compact_list_of_u32_pairs()
    {
        // Captured 2026-11-03 after Play on the one-character list: flag byte 0, one pair (0, 0).
        Assert.True(LoginCodec.TryDecode(LoginDirection.ClientToServer, Convert.FromHexString("000E00010000000000000000"), out var m, out int n, LoginDialect.Poe2));
        var play = Assert.IsType<PlayPoe2>(m);
        Assert.Equal(12, n);
        Assert.Equal(0, play.Flags);
        Assert.Equal([new U32Pair(0, 0)], play.Pairs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x3f)]
    [InlineData(-0x40)]
    [InlineData(0x40)]
    [InlineData(-0x2000)]
    [InlineData(0x1fffff)]
    [InlineData(-0x4000000)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Compact_ints_read_back_what_the_instance_encoder_writes(int value)
    {
        var w = new LoginWriter();
        GameServer.Core.Protocol.Instance.InstanceProtocol.CompactInt(w, value);
        GameServer.Core.Protocol.Instance.InstanceProtocol.CompactUInt(w, (uint)value);
        byte[] bytes = w.ToArray();

        var r = new LoginReader(bytes);
        Assert.Equal(value, r.CompactInt());
        Assert.Equal((uint)value, r.CompactUInt());
        Assert.False(r.Short);
        Assert.Equal(bytes.Length, r.Position);
    }

    [Fact]
    public void Character_detail_request_sends_the_record_id_then_the_character_index()
    {
        // Captured 2026-11-03 right after 0x15: 0010 00000000 00. Writer 0x1420e4b70: u32 +0x10, then u8 +0x14.
        Assert.True(LoginCodec.TryDecode(LoginDirection.ClientToServer, Convert.FromHexString("00100000000000"), out var m, out int n, LoginDialect.Poe2));
        Assert.Equal(new CharacterDetailRequestPoe2(0, 0), m);
        Assert.Equal(7, n);
        Assert.Equal("0010" + "00000007" + "02", Convert.ToHexString(LoginMessage.Encode(new CharacterDetailRequestPoe2(7, 2))));
    }

    [Fact]
    public void Message_0x16_is_a_u32_and_two_u16s()
    {
        // Reader 0x14214de80: u32 +0x10, u16 +0x14, u16 +0x16.
        Assert.Equal("0016" + "00000007" + "1234" + "5678", Convert.ToHexString(LoginMessage.Encode(new U32U16U16(0x16, 7, 0x1234, 0x5678))));
    }

    [Fact]
    public void Instance_info_has_a_length_prefixed_field_between_the_area_and_the_ticket()
    {
        // 0.5.5f (PORTING-POE2.md, wave 35): str host, u16 port, u32, u16 area hash, u16 length + bytes, u32 ticket,
        // bytes[64], bytes[8], u8. 0.5.5e had PoE1's 0x14, without the length-prefixed field.
        byte[] wire = LoginMessage.Encode(new InstanceInfoPoe2("h", 6112, 1, 0xabcd, [], 2, B(64, 0x11), B(8, 0x22), 3));

        Assert.Equal("0014" + "0001" + "68" + "17E0" + "00000001" + "ABCD" + "0000" + "00000002" +
            Convert.ToHexString(B(64, 0x11)) + Convert.ToHexString(B(8, 0x22)) + "03", Convert.ToHexString(wire));
        Assert.True(LoginCodec.TryDecode(LoginDirection.ServerToClient, wire, out var m, out _, LoginDialect.Poe2));
        var info = Assert.IsType<InstanceInfoPoe2>(m);
        Assert.Equal(0xabcd, info.AreaHash);
        Assert.Equal(2u, info.U32_38);
    }

    [Fact]
    public void Instance_info_reads_its_ticket_after_the_length_prefixed_field()
    {
        // What the 0.5.5f client did with PoE1's layout on 2026-11-10: the ticket's first two bytes read as the
        // field's length, so the message ran 2 + length bytes further and the ticket came from what followed.
        byte[] poe1 = LoginMessage.Encode(new InstanceInfo("h", 6112, 1, 0xabcd, 0x00030000, B(64, 0x11), B(8, 0x22), 3));
        byte[] follows = [0xde, 0xad, 0xbe, 0xef, .. B(64, 0x33), .. B(8, 0x44), 0x05];
        // Length 3: the field takes the ticket's last two bytes and the first key byte, the ticket is read from the
        // key, and the message ends 2 + 3 bytes into what follows.
        byte[] wire = [.. poe1, .. follows];

        Assert.True(LoginCodec.TryDecode(LoginDirection.ServerToClient, wire, out var m, out int consumed, LoginDialect.Poe2));

        var info = Assert.IsType<InstanceInfoPoe2>(m);
        Assert.Equal([0x00, 0x00, 0x11], info.Blob);
        Assert.Equal(0x11111111u, info.U32_38);
        Assert.Equal(poe1.Length + 2 + 3, consumed);
    }

    [Fact]
    public void Poe2_knows_poe1_ids_plus_0x10_and_0x16()
    {
        foreach (var dir in Enum.GetValues<LoginDirection>())
            for (int id = 0; id < 0x100; id++)
            {
                bool added = (dir, id) is (LoginDirection.ClientToServer, 0x10) or (LoginDirection.ServerToClient, 0x16);
                Assert.Equal(added || LoginCodec.IsKnown(dir, (ushort)id), LoginCodec.IsKnown(dir, (ushort)id, LoginDialect.Poe2));
            }
    }

    [Fact]
    public void Poe1_keeps_its_own_layouts()
    {
        byte[] wire = LoginMessage.Encode(new TokenLogin(0x40, B(32, 0xbb), B(32, 0xaa), 0, B(4, 7)));
        Assert.True(LoginCodec.TryDecode(LoginDirection.ClientToServer, wire, out var m, out _));
        Assert.IsType<TokenLogin>(m);
    }

    [Fact]
    public void Stream_decodes_a_poe2_token_login_then_the_keepalive()
    {
        var s = new LoginMessageStream(LoginDirection.ClientToServer, LoginDialect.Poe2);
        byte[] wire = [.. LoginMessage.Encode(Samples[0].Msg), 0x00, 0x01];

        var got = s.Push(wire);

        Assert.False(s.Desynced);
        Assert.Equal(2, got.Count);
        Assert.IsType<TokenLoginPoe2>(got[0]);
        Assert.Equal(new EmptyMessage(0x01), got[1]);
    }
}
