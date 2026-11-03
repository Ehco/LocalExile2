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
    ];

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
    public void Poe2_knows_the_same_ids_as_poe1()
    {
        foreach (var dir in Enum.GetValues<LoginDirection>())
            for (int id = 0; id < 0x100; id++)
                Assert.Equal(LoginCodec.IsKnown(dir, (ushort)id), LoginCodec.IsKnown(dir, (ushort)id, LoginDialect.Poe2));
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
