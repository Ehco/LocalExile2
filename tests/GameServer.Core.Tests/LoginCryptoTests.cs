using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

public class LoginCryptoTests
{
    [Fact]
    public void Salsa20_matches_estream_set1_vector0()
    {
        byte[] key = new byte[32]; key[0] = 0x80;
        byte[] ks = new byte[64];
        new Salsa20(key, new byte[8]).Process(ks);
        Assert.Equal("E3BE8FDD8BECA2E3EA8EF9475B29A6E7003951E1097A5C38D23B7A5FAD9F6844" +
                     "B22C97559E2723C7CBBD3FE4FC8D9A0744652A83E72A9C461876AF4D7EF1A117", Convert.ToHexString(ks));
    }

    [Fact]
    public void Salsa20_keystream_is_continuous_across_calls()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), iv = RandomNumberGenerator.GetBytes(8);
        byte[] whole = new byte[200], parts = new byte[200];
        new Salsa20(key, iv).Process(whole);
        var s = new Salsa20(key, iv);
        s.Process(parts.AsSpan(0, 3)); s.Process(parts.AsSpan(3, 70)); s.Process(parts.AsSpan(73));
        Assert.Equal(whole, parts);
    }

    [Fact]
    public void Dsa_sha1_p1363_interoperates_with_platform_dsa()
    {
        using var dsa = DSA.Create(1024);
        var prm = dsa.ExportParameters(includePrivateParameters: true);
        static BigInteger B(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);
        var key = new DsaKey(B(prm.P!), B(prm.Q!), B(prm.G!), B(prm.Y!), B(prm.X!));
        byte[] msg = Encoding.ASCII.GetBytes("server ephemeral key");

        byte[] ours = key.Sign(msg);
        Assert.True(dsa.VerifyData(msg, ours, HashAlgorithmName.SHA1, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        byte[] theirs = dsa.SignData(msg, HashAlgorithmName.SHA1, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.True(key.Verify(msg, theirs));
        theirs[^1] ^= 1;
        Assert.False(key.Verify(msg, theirs));
    }

    [Fact]
    public void Groups_are_safe_primes_with_order_q_generators()
    {
        foreach (var g in new[] { LoginGroups.Static, LoginGroups.Ephemeral })
        {
            Assert.Equal(g.P, 2 * g.Q + 1);
            Assert.True(BigInteger.ModPow(g.G, g.Q, g.P).IsOne);
        }
        Assert.Equal(384, LoginGroups.Static.ElementLength);
        Assert.Equal(128, LoginGroups.Ephemeral.ElementLength);
    }

    [Fact]
    public void Handshake_round_trip_with_synthetic_keys()
    {
        using var dsa = DSA.Create(1024);
        var prm = dsa.ExportParameters(true);
        static BigInteger B(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);
        var signing = new DsaKey(B(prm.P!), B(prm.Q!), B(prm.G!), B(prm.Y!), B(prm.X!));
        BigInteger clientStaticPriv = LoginGroups.Static.RandomExponent();
        var keys = new LoginServerKeys(signing, LoginGroups.Static.RandomExponent(), LoginGroups.Static.PublicFor(clientStaticPriv));

        var client = new ClientHandshake(clientStaticPriv, keys.ServerStaticPublic, signing with { X = null });
        AssertSessionWorks(client, new ServerHandshake(keys));
    }

    [Fact]
    public void Tampered_server_key_is_rejected_by_client()
    {
        using var dsa = DSA.Create(1024);
        var prm = dsa.ExportParameters(true);
        static BigInteger B(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);
        var signing = new DsaKey(B(prm.P!), B(prm.Q!), B(prm.G!), B(prm.Y!), B(prm.X!));
        BigInteger a = LoginGroups.Static.RandomExponent();
        var keys = new LoginServerKeys(signing, LoginGroups.Static.RandomExponent(), LoginGroups.Static.PublicFor(a));
        var client = new ClientHandshake(a, keys.ServerStaticPublic, signing with { X = null });

        KeyExchange.TryDecode(client.Hello(), out var hello, out _);
        var (reply, _) = new ServerHandshake(keys).Respond(hello!);
        reply[10] ^= 1; // inside the server's public value
        KeyExchange.TryDecode(reply, out var r, out _);
        Assert.Throws<CryptographicException>(() => client.Complete(r!));
    }

    /// <summary>Uses the real client: generate keys from it, patch a copy, then handshake with values read back from
    /// the patched image the way the client would. Skips silently when Client/Game.exe is not present.</summary>
    [Fact]
    public void Patched_client_image_handshakes_with_generated_keys()
    {
        string? exe = FindClient();
        if (exe is null) return;
        var image = new ClientImage(File.ReadAllBytes(exe));
        var original = image.ReadVerifyKey();
        Assert.Equal(2048, original.P.GetBitLength());
        Assert.Equal(224, original.Q.GetBitLength());
        Assert.True(LoginGroups.Static.IsValidElement(image.ReadServerStaticPublic()));

        var keys = LoginServerKeys.Generate(image);
        image.Patch(keys);
        var patched = new ClientImage(image.Bytes);
        Assert.Equal(keys.Signing.Y, patched.ReadVerifyKey().Y);
        Assert.Equal(original.P, patched.ReadVerifyKey().P);
        Assert.Equal(keys.ServerStaticPublic, patched.ReadServerStaticPublic());

        var restored = LoginServerKeys.FromJson(keys.ToJson());
        var client = new ClientHandshake(patched.ReadClientStaticPrivate(), patched.ReadServerStaticPublic(), patched.ReadVerifyKey());
        AssertSessionWorks(client, new ServerHandshake(restored));
    }

    [Fact]
    public void Detects_poe2_layout_with_short_client_static_private()
    {
        // PoE2 order: server static public, SPKI, gap to +0x350, client static private (zero-padded short exponent).
        BigInteger clientStaticPriv = BigInteger.Pow(2, 267) + 12345;
        byte[] serverStaticPub = Fixed(LoginGroups.Static.PublicFor(LoginGroups.Static.RandomExponent()), 384);
        byte[] spki = SyntheticVerifyKey();
        byte[] image = [.. new byte[16], .. serverStaticPub, .. spki, .. new byte[0x350 - spki.Length],
            .. Fixed(clientStaticPriv, 384), .. new byte[0x100]];

        var client = ClientImage.Load(image);

        Assert.Same(ClientLayout.Poe2_055e, client.Layout);
        Assert.Equal(16 + 384, client.Offset);
        Assert.Equal(clientStaticPriv, client.ReadClientStaticPrivate());
        Assert.Equal(new BigInteger(serverStaticPub, isUnsigned: true, isBigEndian: true), client.ReadServerStaticPublic());
    }

    [Fact]
    public void Detects_poe2_055f_layout_with_both_static_keys_before_the_verify_key()
    {
        // 0.5.5f order: server static public, client static private (short exponent), SPKI, unrelated data.
        BigInteger clientStaticPriv = BigInteger.Pow(2, 267) + 12345;
        byte[] serverStaticPub = RandomStaticPublic();

        var client = ClientImage.Load(Poe2_055fImage(serverStaticPub, clientStaticPriv));

        Assert.Same(ClientLayout.Poe2_055f, client.Layout);
        Assert.Equal(16 + 384 + 384, client.Offset);
        Assert.Equal(clientStaticPriv, client.ReadClientStaticPrivate());
        Assert.Equal(new BigInteger(serverStaticPub, isUnsigned: true, isBigEndian: true), client.ReadServerStaticPublic());
    }

    [Fact]
    public void Does_not_take_a_short_exponent_that_is_a_subgroup_element_for_the_server_static_public()
    {
        // Half of all values are subgroup elements (p = 2q + 1), short exponents included: a square always is.
        // In a 0.5.5f image such an exponent sits where 0.5.5e keeps the server static public.
        BigInteger clientStaticPriv = BigInteger.Pow(2, 266);
        Assert.True(LoginGroups.Static.IsValidElement(clientStaticPriv));

        var client = ClientImage.Load(Poe2_055fImage(RandomStaticPublic(), clientStaticPriv));

        Assert.Same(ClientLayout.Poe2_055f, client.Layout);
        Assert.Equal(clientStaticPriv, client.ReadClientStaticPrivate());
    }

    [Fact]
    public void Does_not_take_a_full_size_value_for_the_short_client_static_private()
    {
        // The real 0.5.5e image has a second subgroup element 0x180 before its server static public: without a
        // size check on the exponent, the 0.5.5f layout fits it too.
        BigInteger clientStaticPriv = BigInteger.Pow(2, 267) + 12345;
        byte[] serverStaticPub = RandomStaticPublic();
        byte[] spki = SyntheticVerifyKey();
        byte[] image = [.. new byte[16], .. RandomStaticPublic(), .. serverStaticPub, .. spki,
            .. new byte[0x350 - spki.Length], .. Fixed(clientStaticPriv, 384), .. new byte[0x100]];

        foreach (var layout in ClientLayout.Known.Reverse())
            Assert.Equal(ReferenceEquals(layout, ClientLayout.Poe2_055e), layout.Fits(image, 16 + 384 + 384));
        Assert.Same(ClientLayout.Poe2_055e, ClientImage.Load(image).Layout);
    }

    [Fact]
    public void Patch_of_a_055f_image_changes_the_verify_key_and_the_server_static_public_only()
    {
        BigInteger clientStaticPriv = BigInteger.Pow(2, 267) + 12345;
        byte[] original = Poe2_055fImage(RandomStaticPublic(), clientStaticPriv);
        var image = ClientImage.Load([.. original]);
        var signing = image.ReadVerifyKey() with { X = 7, Y = BigInteger.Pow(2, 2046) + 5 };
        var keys = new LoginServerKeys(signing, LoginGroups.Static.RandomExponent(),
            LoginGroups.Static.PublicFor(clientStaticPriv));

        image.Patch(keys);

        var patched = ClientImage.Load(image.Bytes);
        Assert.Same(ClientLayout.Poe2_055f, patched.Layout);
        Assert.Equal(keys.ServerStaticPublic, patched.ReadServerStaticPublic());
        Assert.Equal(signing.Y, patched.ReadVerifyKey().Y);
        Assert.Equal(clientStaticPriv, patched.ReadClientStaticPrivate());
        int spki = patched.Offset;
        var changed = Enumerable.Range(0, original.Length).Where(i => original[i] != image.Bytes[i]).ToList();
        Assert.NotEmpty(changed);
        Assert.All(changed, i => Assert.True(
            (i >= spki - 0x300 && i < spki - 0x180) || (i >= spki && i < spki + ClientImage.DefaultVerifyKeyLength),
            $"byte 0x{i:x} changed outside the two key blobs"));
    }

    [Fact]
    public void Unknown_layout_names_the_layouts_that_were_tried()
    {
        byte[] spki = SyntheticVerifyKey();
        byte[] image = [.. new byte[0x400], .. spki, .. new byte[0x800]];

        var ex = Assert.Throws<FormatException>(() => ClientImage.Load(image));

        Assert.Contains(ClientLayout.Poe2_055f.Name, ex.Message);
        Assert.Contains(ClientLayout.Poe1_3293b.Name, ex.Message);
    }

    [Fact]
    public void Detects_poe1_layout()
    {
        BigInteger clientStaticPriv = LoginGroups.Static.RandomExponent();
        byte[] spki = SyntheticVerifyKey();
        byte[] image = [.. new byte[16], .. spki, .. new byte[0x350 - spki.Length], .. Fixed(clientStaticPriv, 384),
            .. Fixed(LoginGroups.Static.PublicFor(LoginGroups.Static.RandomExponent()), 384), .. new byte[0x100]];

        var client = ClientImage.Load(image);

        Assert.Same(ClientLayout.Poe1_3293b, client.Layout);
        Assert.Equal(clientStaticPriv, client.ReadClientStaticPrivate());
    }

    /// <summary>A DER SPKI with the client's exact header and length (p 2048 bits, q 224, g and y 2047); the values
    /// only need the right sizes, since layout detection never uses them as a DSA key.</summary>
    private static byte[] SyntheticVerifyKey()
    {
        BigInteger p = BigInteger.Pow(2, 2047) + 1, q = BigInteger.Pow(2, 223) + 1;
        BigInteger g = BigInteger.Pow(2, 2046) + 1, y = BigInteger.Pow(2, 2046) + 3;
        byte[] der = ClientImage.EncodeVerifyKey(new DsaKey(p, q, g, y));
        Assert.Equal(ClientImage.DefaultVerifyKeyLength, der.Length);
        return der;
    }

    /// <summary>A synthetic 0.5.5f image: both static keys before the SPKI, and after it the unrelated full-size
    /// data the real build has where 0.5.5e kept the client static private.</summary>
    private static byte[] Poe2_055fImage(byte[] serverStaticPub, BigInteger clientStaticPriv)
    {
        byte[] spki = SyntheticVerifyKey();
        byte[] unrelated = new byte[384];
        Array.Fill(unrelated, (byte)0xa5);
        return [.. new byte[16], .. serverStaticPub, .. Fixed(clientStaticPriv, 384), .. spki,
            .. new byte[0x350 - spki.Length], .. unrelated, .. new byte[0x100]];
    }

    private static byte[] RandomStaticPublic() =>
        Fixed(LoginGroups.Static.PublicFor(LoginGroups.Static.RandomExponent()), 384);

    private static byte[] Fixed(BigInteger v, int length)
    {
        byte[] b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        return [.. new byte[length - b.Length], .. b];
    }

    private static void AssertSessionWorks(ClientHandshake client, ServerHandshake server)
    {
        byte[] helloBytes = client.Hello();
        Assert.Equal(6 + 128, helloBytes.Length);
        Assert.True(KeyExchange.TryDecode(helloBytes, out var hello, out int used));
        Assert.Equal(helloBytes.Length, used);
        Assert.Empty(hello!.Signature);

        var (replyBytes, serverCiphers) = server.Respond(hello);
        Assert.True(KeyExchange.TryDecode(replyBytes, out var reply, out _));
        var clientCiphers = client.Complete(reply!);

        foreach (string text in new[] { "first client packet", "second" })
        {
            byte[] wire = Encoding.ASCII.GetBytes(text);
            clientCiphers.Send.Process(wire);
            serverCiphers.Receive.Process(wire);
            Assert.Equal(text, Encoding.ASCII.GetString(wire));
        }
        byte[] down = Encoding.ASCII.GetBytes("server to client");
        serverCiphers.Send.Process(down);
        clientCiphers.Receive.Process(down);
        Assert.Equal("server to client", Encoding.ASCII.GetString(down));
    }

    private static string? FindClient()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "Client", "Game.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
