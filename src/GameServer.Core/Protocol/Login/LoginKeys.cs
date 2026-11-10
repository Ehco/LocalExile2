using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// Everything the login server needs to complete the client's handshake. Generated once from a client image
/// (<see cref="Generate"/>) and stored as JSON; the same keys are then patched into the client (<see cref="ClientImage.Patch"/>).
/// </summary>
public sealed record LoginServerKeys(DsaKey Signing, BigInteger ServerStaticPrivate, BigInteger ClientStaticPublic)
{
    public BigInteger ServerStaticPublic => LoginGroups.Static.PublicFor(ServerStaticPrivate);

    /// <summary>New signing key over the client's own DSA domain (so p, q, g and the DER length stay the same) and a new
    /// server static key; the client's embedded static private key gives us its static public value.</summary>
    public static LoginServerKeys Generate(ClientImage client)
    {
        DsaKey original = client.ReadVerifyKey();
        DsaKey signing;
        do
        {
            BigInteger x = Big.RandomBelow(original.Q - 1) + 1;
            signing = original with { X = x, Y = BigInteger.ModPow(original.G, x, original.P) };
        } while (ClientImage.EncodeVerifyKey(signing).Length != client.VerifyKeyLength);
        return new LoginServerKeys(signing, LoginGroups.Static.RandomExponent(),
            LoginGroups.Static.PublicFor(client.ReadClientStaticPrivate()));
    }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["dsaP"] = H(Signing.P), ["dsaQ"] = H(Signing.Q), ["dsaG"] = H(Signing.G), ["dsaY"] = H(Signing.Y),
        ["dsaX"] = H(Signing.X ?? throw new InvalidOperationException("signing key has no private part")),
        ["serverStaticPrivate"] = H(ServerStaticPrivate), ["clientStaticPublic"] = H(ClientStaticPublic),
    }, new JsonSerializerOptions { WriteIndented = true });

    public static LoginServerKeys FromJson(string json)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new FormatException("empty key file");
        BigInteger F(string k) => d.TryGetValue(k, out var v)
            ? BigInteger.Parse("0" + v, NumberStyles.HexNumber) : throw new FormatException($"key file missing '{k}'");
        return new LoginServerKeys(new DsaKey(F("dsaP"), F("dsaQ"), F("dsaG"), F("dsaY"), F("dsaX")),
            F("serverStaticPrivate"), F("clientStaticPublic"));
    }

    private static string H(BigInteger v) => v.ToString("x");
}

/// <summary>
/// Per-client layout of the login key blobs around the DER-encoded X.509 SPKI.
/// The blobs are located relative to the SPKI, which itself is found by the DER
/// prefix (no fixed file offsets), so a layout only describes adjacency.
/// </summary>
public sealed record ClientLayout(
    string Name,
    int VerifyKeyLength,
    int? ClientStaticPrivateOffset,
    int? ServerStaticPublicOffset,
    int StaticLength,
    int? ClientStaticPrivateMaxBits = null)
{
    /// <summary>Crypto++ caps a private exponent of the 3072-bit static group at
    /// 2 * DiscreteLogWorkFactor(3072) bits, so its 384-byte big-endian blob starts with 350 zero bytes.</summary>
    public const int ShortExponentBits = 268;

    /// <summary>A public value is a random element of the group: it has this many leading zero bits with
    /// probability 2^-64. Tells it apart from a short exponent, which is a subgroup element half the time.</summary>
    private const int FullSizeSlackBits = 64;

    /// <summary>PoE1 3.29.3b: client static private after the SPKI, server static public after that.</summary>
    public static readonly ClientLayout Poe1_3293b = new("PoE1 3.29.3b", 0x346, 0x350, 0x4d0, 384);

    /// <summary>PoE2 0.5.5e (Steam): server static public immediately *before* the SPKI, the client static
    /// private (a short exponent) at +0x350.</summary>
    public static readonly ClientLayout Poe2_055e = new("PoE2 0.5.5e", 0x346, 0x350, -0x180, 384, ShortExponentBits);

    /// <summary>PoE2 0.5.5f (Steam): both static keys before the SPKI, server static public then client static
    /// private (PORTING-POE2.md, wave 34). What follows the SPKI is unrelated data.</summary>
    public static readonly ClientLayout Poe2_055f = new("PoE2 0.5.5f", 0x346, -0x180, -0x300, 384, ShortExponentBits);

    /// <summary>Every layout <see cref="ClientImage.Detect"/> tries, in order. A PoE2 layout never fits another PoE2
    /// build's image (the exponent's size settles it); PoE1 against PoE2 rests on the subgroup check alone.</summary>
    public static IReadOnlyList<ClientLayout> Known { get; } = [Poe1_3293b, Poe2_055e, Poe2_055f];

    /// <summary>True when the blobs this layout places around the SPKI at <paramref name="spkiOffset"/> are what it
    /// says: a full-size subgroup element for the server static public, and a non-zero exponent of the expected
    /// size for the client static private.</summary>
    public bool Fits(ReadOnlySpan<byte> image, int spkiOffset)
    {
        if (ServerStaticPublicOffset is not int sso || !TryBlob(image, spkiOffset + sso, out var publicBlob)) return false;
        BigInteger serverPublic = Big.FromBigEndian(publicBlob);
        if (serverPublic.GetBitLength() <= StaticLength * 8 - FullSizeSlackBits) return false;
        if (!LoginGroups.Static.IsValidElement(serverPublic)) return false;

        if (ClientStaticPrivateOffset is not int cso) return true;
        if (!TryBlob(image, spkiOffset + cso, out var privateBlob)) return false;
        BigInteger clientPrivate = Big.FromBigEndian(privateBlob);
        return !clientPrivate.IsZero &&
            (ClientStaticPrivateMaxBits is not int maxBits || clientPrivate.GetBitLength() <= maxBits);
    }

    private bool TryBlob(ReadOnlySpan<byte> image, int at, out ReadOnlySpan<byte> blob)
    {
        bool inside = at >= 0 && at + StaticLength <= image.Length;
        blob = inside ? image.Slice(at, StaticLength) : default;
        return inside;
    }
}

/// <summary>
/// The login key blobs inside the client executable. They sit together in .rdata and are only referenced from
/// the login connect function. Located by the DER header, not by fixed offsets; the surrounding layout is
/// per-client (<see cref="ClientLayout"/>).
/// </summary>
public sealed class ClientImage
{
    public const int DefaultVerifyKeyLength = 0x346;     // X.509 SubjectPublicKeyInfo, DSA p=2048 q=224

    // SEQUENCE(0x342) SEQUENCE(0x235) OID 1.2.840.10040.4.1 (dsa)
    private static readonly byte[] DerPrefix = Convert.FromHexString("308203423082023506072a8648ce380401");

    private readonly byte[] _bytes;
    private readonly ClientLayout _layout;

    public ClientImage(byte[] bytes) : this(bytes, Detect(bytes)) { }

    public ClientImage(byte[] bytes, ClientLayout layout)
    {
        _bytes = bytes;
        _layout = layout;
        Offset = FindUnique(bytes, layout.VerifyKeyLength);
    }

    public byte[] Bytes => _bytes;
    public ClientLayout Layout => _layout;
    public int Offset { get; }

    public int VerifyKeyLength => _layout.VerifyKeyLength;

    public DsaKey ReadVerifyKey()
    {
        var spki = new AsnReader(_bytes.AsMemory(Offset, VerifyKeyLength), AsnEncodingRules.DER).ReadSequence();
        var alg = spki.ReadSequence();
        alg.ReadObjectIdentifier();
        var prm = alg.ReadSequence();
        BigInteger p = prm.ReadInteger(), q = prm.ReadInteger(), g = prm.ReadInteger();
        byte[] bits = spki.ReadBitString(out _);
        BigInteger y = new AsnReader(bits, AsnEncodingRules.DER).ReadInteger();
        return new DsaKey(p, q, g, y);
    }

    public BigInteger ReadClientStaticPrivate() =>
        Big.FromBigEndian(StaticBlob(_layout.ClientStaticPrivateOffset, "client static private"));

    public BigInteger ReadServerStaticPublic() =>
        Big.FromBigEndian(StaticBlob(_layout.ServerStaticPublicOffset, "server static public"));

    /// <summary>Overwrites the verify key and the server static public value in place (same sizes).</summary>
    public void Patch(LoginServerKeys keys)
    {
        byte[] der = EncodeVerifyKey(keys.Signing);
        if (der.Length != VerifyKeyLength) throw new InvalidOperationException("verify key would change size");
        der.CopyTo(_bytes, Offset);
        if (_layout.ServerStaticPublicOffset is not int sso)
            throw new NotSupportedException("server static public offset not mapped for this client");
        Big.ToFixed(keys.ServerStaticPublic, _layout.StaticLength).CopyTo(_bytes, Offset + sso);
    }

    private byte[] StaticBlob(int? relativeOffset, string what) =>
        relativeOffset is int off
            ? _bytes.AsSpan(Offset + off, _layout.StaticLength).ToArray()
            : throw new NotSupportedException($"{what} location not yet mapped for this client (see PORTING-POE2.md)");

    /// <summary>Loads a client image and auto-detects its key layout.</summary>
    public static ClientImage Load(byte[] bytes) => new(bytes, Detect(bytes));

    /// <summary>Picks the layout whose key placement checks out against the bytes around the SPKI.</summary>
    public static ClientLayout Detect(byte[] bytes)
    {
        int at = bytes.AsSpan().IndexOf(DerPrefix);
        if (at < 0) throw new FormatException("login verify key not found in client image");
        foreach (var layout in ClientLayout.Known)
            if (layout.Fits(bytes, at)) return layout;
        throw new FormatException("no known key layout (" + string.Join(", ", ClientLayout.Known.Select(l => l.Name)) +
            ") matches the bytes around the SPKI");
    }

    public static byte[] EncodeVerifyKey(DsaKey key)
    {
        var inner = new AsnWriter(AsnEncodingRules.DER);
        inner.WriteInteger(key.Y);
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier("1.2.840.10040.4.1");
                using (w.PushSequence()) { w.WriteInteger(key.P); w.WriteInteger(key.Q); w.WriteInteger(key.G); }
            }
            w.WriteBitString(inner.Encode());
        }
        return w.Encode();
    }

    private static int FindUnique(byte[] bytes, int verifyKeyLength)
    {
        int at = bytes.AsSpan().IndexOf(DerPrefix);
        if (at < 0) throw new FormatException("login verify key not found in client image");
        if (bytes.AsSpan(at + 1).IndexOf(DerPrefix) >= 0) throw new FormatException("login verify key header is not unique");
        if (at + verifyKeyLength > bytes.Length) throw new FormatException("truncated client image");
        return at;
    }
}
