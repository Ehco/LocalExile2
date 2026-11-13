using System.Numerics;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

/// <summary>
/// Regression tests for login-key layout detection. The bug this pins: the 2026-11-13 client kept the 0.5.5f
/// key order, but its image also satisfies the PoE1 layout (a full-size, valid subgroup element happens to sit
/// where PoE1 keeps the server static public, and the value at PoE1's client-private offset is non-zero).
/// A first-match scan over [PoE1, PoE2...] therefore returned PoE1 and <c>patch</c> wrote the wrong offsets
/// into the client without complaining. Detection must prefer the more specific (size-constrained) layout.
/// </summary>
public class ClientLayoutDetectionTests
{
    private const int SpkiLength = 0x346;
    private const int SpkiOffset = 16 + 384 + 384;   // both static keys precede the SPKI, as in 0.5.5f

    /// <summary>A 0.5.5f-shaped image with the extra full-size blobs the 2026-11-13 build has at +0x350 and
    /// +0x4d0 — the two positions PoE1's layout reads.</summary>
    private static byte[] ModernPoe2Image(byte[] serverStaticPublic, BigInteger clientStaticPrivate,
        out byte[] poe1PrivateDecoy, out byte[] poe1PublicDecoy)
    {
        poe1PrivateDecoy = RandomStaticPublic();     // what PoE1 reads as the client static private
        poe1PublicDecoy = RandomStaticPublic();      // what PoE1 reads as the server static public
        byte[] spki = SyntheticVerifyKey();
        Assert.Equal(SpkiLength, spki.Length);
        byte[] image =
        [
            .. new byte[16],
            .. serverStaticPublic,
            .. Fixed(clientStaticPrivate, 384),
            .. spki,
            .. new byte[0x350 - SpkiLength],
            .. poe1PrivateDecoy,
            .. poe1PublicDecoy,
            .. new byte[0x100],
        ];
        // the decoys must land exactly where the PoE1 layout looks
        Assert.Equal(SpkiOffset + 0x350, 16 + 384 + 384 + SpkiLength + (0x350 - SpkiLength));
        Assert.Equal(16 + 384 + 384 + SpkiLength + (0x350 - SpkiLength) + 384, SpkiOffset + 0x4d0);
        return image;
    }

    [Fact]
    public void Both_layouts_fit_the_modern_image_but_the_specific_PoE2_one_wins()
    {
        BigInteger clientStaticPrivate = BigInteger.Pow(2, 267) + 12345;   // short exponent, 268 bits
        byte[] serverStaticPublic = RandomStaticPublic();
        byte[] image = ModernPoe2Image(serverStaticPublic, clientStaticPrivate, out _, out _);

        // the tie that caused the bad patch: PoE1's looser test also passes here
        Assert.True(ClientLayout.Poe2_055f.Fits(image, SpkiOffset));
        Assert.True(ClientLayout.Poe1_3293b.Fits(image, SpkiOffset));

        var fits = ClientImage.DetectAll(image);
        Assert.Equal([ClientLayout.Poe2_055f, ClientLayout.Poe1_3293b], fits);
        Assert.Same(ClientLayout.Poe2_055f, fits[0]);
        Assert.Same(ClientLayout.Poe2_055f, ClientImage.Load(image).Layout);

        var client = ClientImage.Load(image);
        Assert.Equal(clientStaticPrivate, client.ReadClientStaticPrivate());
        Assert.Equal(new BigInteger(serverStaticPublic, isUnsigned: true, isBigEndian: true), client.ReadServerStaticPublic());
    }

    [Fact]
    public void Patching_the_modern_image_writes_only_the_PoE2_blobs_and_verifies()
    {
        BigInteger clientStaticPrivate = BigInteger.Pow(2, 267) + 12345;
        byte[] original = ModernPoe2Image(RandomStaticPublic(), clientStaticPrivate, out _, out _);
        var image = ClientImage.Load([.. original]);
        var signing = image.ReadVerifyKey() with { X = 11, Y = BigInteger.Pow(2, 2046) + 7 };
        var keys = new LoginServerKeys(signing, LoginGroups.Static.RandomExponent(),
            LoginGroups.Static.PublicFor(clientStaticPrivate));

        image.Patch(keys);
        image.VerifyPatched(keys);   // the integrity gate that would have caught the PoE1 mis-detection

        int spki = image.Offset;
        var changed = Enumerable.Range(0, original.Length).Where(i => original[i] != image.Bytes[i]).ToList();
        Assert.NotEmpty(changed);
        Assert.All(changed, i => Assert.True(
            (i >= spki - 0x300 && i < spki - 0x180) || (i >= spki && i < spki + SpkiLength),
            $"byte 0x{i:x} changed outside the server-static-public and verify-key blobs"));
        // the PoE1 decoy blobs must be untouched: that is what the old code would have overwritten
        Assert.All(changed, i => Assert.False(
            (i >= spki + 0x350 && i < spki + 0x350 + 384) || (i >= spki + 0x4d0 && i < spki + 0x4d0 + 384),
            $"byte 0x{i:x} changed inside a PoE1-layout blob"));
    }

    [Fact]
    public void Operator_can_force_a_layout_by_name()
    {
        byte[] image = ModernPoe2Image(RandomStaticPublic(), BigInteger.Pow(2, 267) + 1, out _, out _);

        Assert.Same(ClientLayout.Poe1_3293b, ClientImage.Load(image, "PoE1 3.29.3b").Layout);
        Assert.Same(ClientLayout.Poe2_055f, ClientImage.Load(image, "poe2 0.5.5f").Layout);
        Assert.Same(ClientLayout.Poe2_055f, ClientImage.Load(image, "PoE2055f").Layout);
        var ex = Assert.Throws<FormatException>(() => ClientImage.Load(image, "PoE2 0.5.9z"));
        Assert.Contains("unknown layout", ex.Message);
    }

    [Fact]
    public void A_known_build_is_named_and_an_unknown_one_is_reported_as_such()
    {
        var known = ClientImage.Fingerprint.Known.Single(f => f.Build == "PoE2 0.5.5f");
        Assert.Equal(78_440_792, known.Size);
        Assert.StartsWith("7e0d3665", known.Sha256);

        var unknown = ClientImage.Fingerprint.Of(ModernPoe2Image(RandomStaticPublic(), BigInteger.Pow(2, 267) + 1, out _, out _));
        Assert.Null(unknown.Build);
        Assert.Contains("unrecognised build", unknown.ToString());

        byte[] real = [.. new byte[16], .. SyntheticVerifyKey(), .. new byte[0x100]];
        Assert.Null(ClientImage.Fingerprint.Of(real).Build);
    }

    /// <summary>A DER SPKI with the client's exact header and length; the values only need the right sizes,
    /// since layout detection never uses them as a DSA key.</summary>
    private static byte[] SyntheticVerifyKey()
    {
        BigInteger p = BigInteger.Pow(2, 2047) + 1, q = BigInteger.Pow(2, 223) + 1;
        BigInteger g = BigInteger.Pow(2, 2046) + 1, y = BigInteger.Pow(2, 2046) + 3;
        byte[] der = ClientImage.EncodeVerifyKey(new DsaKey(p, q, g, y));
        Assert.Equal(SpkiLength, der.Length);
        return der;
    }

    private static byte[] RandomStaticPublic() =>
        Fixed(LoginGroups.Static.PublicFor(LoginGroups.Static.RandomExponent()), 384);

    private static byte[] Fixed(BigInteger v, int length)
    {
        byte[] b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        return [.. new byte[length - b.Length], .. b];
    }
}
