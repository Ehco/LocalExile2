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

    /// <summary>Every layout <see cref="ClientImage.Detect"/> tries, <b>most specific first</b>.
    /// Order matters: the PoE2 layouts constrain the size of the client static private (a crypto++ short
    /// exponent), while <see cref="Poe1_3293b"/> constrains nothing but the subgroup of the public value. A weaker
    /// PoE2-era image therefore satisfies both, and a first-match scan returns PoE1 — which patches the wrong
    /// offsets and silently corrupts the client (seen on the 2026-11-13 client: the PoE1 layout fitted, the 0.5.5f
    /// layout was the right one). The most specific fit wins; everything that fits is reported by
    /// <see cref="ClientImage.DetectAll"/> so <c>probe</c> can show the tie.</summary>
    public static IReadOnlyList<ClientLayout> Known { get; } =
        [Poe2_055f, Poe2_055e, Poe1_3293b];

    /// <summary>The layout with this display name (spaces optional, case-insensitive); for <c>--layout</c>.</summary>
    public static ClientLayout ByName(string name)
    {
        var wanted = Normalize(name);
        return Known.FirstOrDefault(l => string.Equals(Normalize(l.Name), wanted, StringComparison.OrdinalIgnoreCase))
               ?? throw new FormatException($"unknown layout '{name}' (known: {string.Join(", ", Known.Select(l => l.Name))})");
    }

    // "PoE2 0.5.5f", "poe2 0.5.5f" and "PoE2055f" are the same layout name on a command line.
    private static string Normalize(string name) =>
        name.Replace(" ", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);

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

    /// <summary>Re-reads the bytes this image wrote and fails unless the patched file still detects the same
    /// layout and carries exactly the keys we meant to put there. Cheap, and it turns a silent bad patch
    /// (wrong offsets, wrong build) into a hard error.</summary>
    public void VerifyPatched(LoginServerKeys keys)
    {
        var after = new ClientImage(_bytes);
        if (!ReferenceEquals(after.Layout, _layout))
            throw new InvalidOperationException($"patched image detects as {after.Layout.Name}, not {_layout.Name}");
        if (after.ReadVerifyKey().Y != keys.Signing.Y)
            throw new InvalidOperationException("patched image does not carry the new DSA y");
        if (after.ReadServerStaticPublic() != keys.ServerStaticPublic)
            throw new InvalidOperationException("patched image does not carry the new server static public");
    }

    /// <summary>Size + SHA-256 of a client executable, matched against the builds this project has seen.</summary>
    public sealed record Fingerprint(long Size, string Sha256, string? Build)
    {
        /// <summary>Builds the project has actually identified (see PORTING-POE2.md). An unknown build is not an
        /// error — Steam ships new patches that keep a known key layout — but it is worth saying out loud.</summary>
        public static IReadOnlyList<Fingerprint> Known { get; } =
        [
            new(78_440_792, "7e0d36655e6e788202d2d588429c45064bbf87caec95938ebb19a317947c834e", "PoE2 0.5.5f"),
        ];

        public static Fingerprint Of(byte[] image)
        {
            string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image));
            return new(image.LongLength, sha, Known.FirstOrDefault(k => k.Size == image.LongLength && k.Sha256 == sha)?.Build);
        }

        public override string ToString() =>
            Build is null
                ? $"{Size:n0} bytes, sha256 {Sha256[..16]}… — unrecognised build (layout detected by heuristic)"
                : $"{Build} ({Size:n0} bytes, sha256 {Sha256[..16]}…)";
    }

    private byte[] StaticBlob(int? relativeOffset, string what) =>
        relativeOffset is int off
            ? _bytes.AsSpan(Offset + off, _layout.StaticLength).ToArray()
            : throw new NotSupportedException($"{what} location not yet mapped for this client (see PORTING-POE2.md)");

    /// <summary>Loads a client image and auto-detects its key layout; <paramref name="layoutName"/> forces one
    /// (<c>--layout "PoE2 0.5.5f"</c>) when the operator knows better than the heuristics.</summary>
    public static ClientImage Load(byte[] bytes, string? layoutName = null) =>
        new(bytes, layoutName is null ? Detect(bytes) : ClientLayout.ByName(layoutName));

    /// <summary>Picks the layout whose key placement checks out against the bytes around the SPKI.
    /// <see cref="ClientLayout.Known"/> is ordered most specific first, so a PoE2 match never loses to PoE1's
    /// looser test (see the note on <see cref="ClientLayout.Known"/>).</summary>
    public static ClientLayout Detect(byte[] bytes) => DetectAll(bytes)[0];

    /// <summary>Every layout that fits these bytes, most specific first. Empty when none fits.</summary>
    public static IReadOnlyList<ClientLayout> DetectAll(byte[] bytes)
    {
        int at = bytes.AsSpan().IndexOf(DerPrefix);
        if (at < 0) throw new FormatException("login verify key not found in client image");
        var fits = ClientLayout.Known.Where(l => l.Fits(bytes, at)).ToList();
        if (fits.Count == 0)
            throw new FormatException("no known key layout (" + string.Join(", ", ClientLayout.Known.Select(l => l.Name)) +
                ") matches the bytes around the SPKI");
        return fits;
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
