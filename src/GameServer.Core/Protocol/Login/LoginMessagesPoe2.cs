using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Protocol.Login;

// PoE2 Steam client (build d46f4c27…, 0.5.5e) layouts that differ from PoE1. Addresses are in PathOfExileSteam.exe; see
// PORTING-POE2.md (wave 4). Every other login message reads as in PoE1 (LoginCodec, LoginDialect.Poe2).

/// <summary>
/// C→S 0x05 on PoE2 (vtable 0x143506370, write 0x1420e3670, read 0x1420e3740, sender 0x1420e7f80): PoE1's token login
/// fields, but the token's u16 length comes first and its bytes come last: u16 length, u8 realm|0x40, bytes[32] id B,
/// bytes[32] id A, u8, token. The Steam client sends it at the login screen without user input.
/// </summary>
public sealed record TokenLoginPoe2(byte Realm, byte[] MachineIdB, byte[] MachineIdA, byte B69, byte[] Token) : ILoginMessage
{
    public const ushort Id = 0x05;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) =>
        w.U16(checked((ushort)Token.Length)).U8(Realm).Fixed(MachineIdB, 32).Fixed(MachineIdA, 32).U8(B69).Bytes(Token);
    internal static TokenLoginPoe2 Read(ushort id, ref LoginReader r)
    {
        int n = r.U16();
        return new(r.U8(), r.Fixed(32), r.Fixed(32), r.U8(), r.Fixed(n));
    }
}

/// <summary>
/// S→C 0x04 on PoE2 (vtable 0x143509ed8, write 0x14214dad0, read 0x14214dbe0): PoE1's login result with a u32 in front.
/// The dispatch (0x1420e82b0 case 4) checks Result (+0x14); 0 sets state 3 (+0xee0) and appends a 0x78-byte record to
/// the vector at login+0xef0 that holds U32_10, Data (converted by 0x141020fb0) and the Flags0/Flags1 bits. Either way the
/// listeners at +0xa08 get (result, digest, name).
/// </summary>
public sealed record LoginResultPoe2(uint U32_10, ushort Result, string AccountName, byte[] Digest, byte Flags0, byte Flags1, byte[] Data) : ILoginMessage
{
    public const ushort Id = 0x04;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) =>
        w.U32(U32_10).U16(Result).WStr(AccountName).Fixed(Digest, 32).U8(Flags0).U8(Flags1).Blob(Data);
    internal static LoginResultPoe2 Read(ushort id, ref LoginReader r) =>
        new(r.U32(), r.U16(), r.WStr(), r.Fixed(32), r.U8(), r.U8(), r.Blob());
}

/// <summary>
/// Element of S→C 0x15 on PoE2 (0x58-byte struct, write 0x142716570). PoE1's entry (ListEntry15) with a u8 (+0x51) and
/// two u16s (+0x48, +0x4a) added; names are the PoE2 struct offsets. Level (+0x4c; the dispatch keeps the maximum at
/// login+0xee8) and the class B4e (+0x4e) keep their PoE1 wire positions: Level 12 with B4e 3 showed "Level 12 Duelist"
/// (live 2026-11-03), with "Hideout" under it. B4d sits where PoE1 had the dead marker and U46 where it had the portrait;
/// neither is checked on PoE2.
/// </summary>
public sealed record CharacterEntryPoe2(string Name, string League, byte B4d, byte B51, byte Level, uint U40, byte B4e,
    ushort U44, ushort U46, ushort U48, ushort U4a, byte B4f, byte B50)
{
    internal void Write(LoginWriter w) =>
        w.WStr(Name).WStr(League).U8(B4d).U8(B51).U8(Level).U32(U40).U8(B4e).U16(U44).U16(U46).U16(U48).U16(U4a).U8(B4f).U8(B50);
    internal static CharacterEntryPoe2 Read(ref LoginReader r) =>
        new(r.WStr(), r.WStr(), r.U8(), r.U8(), r.U8(), r.U32(), r.U8(), r.U16(), r.U16(), r.U16(), r.U16(), r.U8(), r.U8());
}

/// <summary>An element of 0x15's pair list (+0x48, write 0x1423bac30): u16, then a signed compact int.</summary>
public readonly record struct U16CompactPair(ushort Key, int Value);

/// <summary>
/// S→C 0x15 on PoE2 (vtable 0x143509f98, write 0x14214df90, read 0x14214e210): the character list. U32_70 must equal the
/// U32_10 of the 0x04 that logged in (dispatch 0x1420e86b4 looks the login record up by it and drops the list
/// otherwise). Entries and B28/B29 go into that record, as in PoE1 (selected index, character slots). When B28 indexes
/// an entry, Data is parsed into 31 records of 0x80 bytes (PoE1: 17) and, with U16_74, U16_76, Pairs, U32_68 and
/// U32_6c (looked up in a hash map; a missing key gives an empty result), goes to the listeners at login+0xd08.
/// </summary>
public sealed record CharacterListPoe2(uint U32_70, IReadOnlyList<CharacterEntryPoe2> Entries, byte B28, byte B29,
    ushort U16_74, ushort U16_76, byte[] Data, IReadOnlyList<U16CompactPair> Pairs, uint U32_68, uint U32_6c) : ILoginMessage
{
    public const ushort Id = 0x15;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w)
    {
        w.U32(U32_70).U8(checked((byte)Entries.Count));
        foreach (var e in Entries) e.Write(w);
        w.U8(B28).U8(B29).U16(U16_74).U16(U16_76).Blob(Data);
        WritePairs(w, Pairs);
        w.U32(U32_68).U32(U32_6c);
    }

    internal static void WritePairs(LoginWriter w, IReadOnlyList<U16CompactPair> pairs)
    {
        InstanceProtocol.CompactUInt(w, (uint)pairs.Count);
        foreach (var p in pairs)
        {
            w.U16(p.Key);
            InstanceProtocol.CompactInt(w, p.Value);
        }
    }

    internal static List<U16CompactPair> ReadPairs(ref LoginReader r)
    {
        uint count = r.CompactUInt();
        var pairs = new List<U16CompactPair>();
        for (uint i = 0; i < count && !r.Short; i++) pairs.Add(new(r.U16(), r.CompactInt()));
        return pairs;
    }
    internal static CharacterListPoe2 Read(ushort id, ref LoginReader r)
    {
        uint u70 = r.U32();
        int n = r.U8();
        var entries = new List<CharacterEntryPoe2>(n);
        for (int i = 0; i < n && !r.Short; i++) entries.Add(CharacterEntryPoe2.Read(ref r));
        byte b28 = r.U8(), b29 = r.U8();
        ushort u74 = r.U16(), u76 = r.U16();
        byte[] data = r.Blob();
        var pairs = ReadPairs(ref r);
        return new(u70, entries, b28, b29, u74, u76, data, pairs, r.U32(), r.U32());
    }
}

/// <summary>An element of PoE2's Play (0x0e): two u32s from the list the character screen builds.</summary>
public readonly record struct U32Pair(uint A, uint B);

/// <summary>
/// C→S 0x0e on PoE2 (vtable 0x143506470, write 0x1420e47e0, sender 0x1420e9ff0): Play. PoE1 sent u8 realm + u32 index;
/// PoE2 sends one byte (bits 0–6: the realm byte at 0x143a0c068, bit 7: the sender's bool argument), then a compact count
/// and one pair per local player slot (the screen has two): A = the character's index in the 0x15 list, B = the 0x04
/// record id that list was sent for (0x1420e9ff0 looks the record up by B, then bounds-checks A). With one character
/// the client sent flags 0 and the pair (0, 0) (live 2026-11-03), then waited ("Contacting Server...") for 0x14.
/// </summary>
public sealed record PlayPoe2(byte Flags, IReadOnlyList<U32Pair> Pairs) : ILoginMessage
{
    public const ushort Id = 0x0e;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w)
    {
        w.U8(Flags);
        InstanceProtocol.CompactUInt(w, (uint)Pairs.Count);
        foreach (var p in Pairs) w.U32(p.A).U32(p.B);
    }
    internal static PlayPoe2 Read(ushort id, ref LoginReader r)
    {
        byte flags = r.U8();
        uint n = r.CompactUInt();
        var pairs = new List<U32Pair>();
        for (uint i = 0; i < n && !r.Short; i++) pairs.Add(new(r.U32(), r.U32()));
        return new(flags, pairs);
    }
}

/// <summary>
/// C→S 0x10 on PoE2 (vtable 0x143506350, write 0x1420e4b70): u32 RecordId, then u8 Index. The character screen
/// (0x140f08950) queues (RecordId = the 0x04 record's U32_10, Index = key of its per-character cache at screen+0x2b8)
/// through 0x1420ea450 when that character's cached entry is missing or flagged dirty (+0x3d); the login update
/// 0x1420e6d40 pops the queue on a timer and sends it only if a login record with that id exists. Seen live right after
/// 0x15 as 0010 00000000 00 (2026-11-03). The reply is S→C 0x11 (<see cref="CharacterDetailPoe2"/>).
/// </summary>
public sealed record CharacterDetailRequestPoe2(uint RecordId, byte Index) : ILoginMessage
{
    public const ushort Id = 0x10;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U32(RecordId).U8(Index);
    internal static CharacterDetailRequestPoe2 Read(ushort id, ref LoginReader r) => new(r.U32(), r.U8());
}

/// <summary>
/// S→C 0x11 on PoE2 (vtable near 0x14350a038, write 0x14214e6b0, read 0x14214e7d0): one character's details, the
/// answer to C→S 0x10. The tail is 0x15's (U16_74 to U32_6c), for one entry: dispatch case 0x1420e8cc6 decodes Data into
/// 31 records of 0x80 bytes (0x1420e5c10) and hands it to the listeners at login+0xd08. The character screen's listener
/// 0x140f0c090 drops it unless RecordId is its record's, then caches the entry Index (name and class from the 0x15 entry)
/// and clears its dirty flag, which ends the 0x10 requests. An empty detail is what 0x15 already sends for B28.
/// </summary>
public sealed record CharacterDetailPoe2(uint RecordId, byte Index, ushort A, ushort B, byte[] Data,
    IReadOnlyList<U16CompactPair> Pairs, uint C, uint D) : ILoginMessage
{
    public const ushort Id = 0x11;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w)
    {
        w.U32(RecordId).U8(Index).U16(A).U16(B).Blob(Data);
        CharacterListPoe2.WritePairs(w, Pairs);
        w.U32(C).U32(D);
    }
    internal static CharacterDetailPoe2 Read(ushort id, ref LoginReader r)
    {
        uint record = r.U32();
        byte index = r.U8();
        ushort a = r.U16(), b = r.U16();
        byte[] data = r.Blob();
        var pairs = CharacterListPoe2.ReadPairs(ref r);
        return new(record, index, a, b, data, pairs, r.U32(), r.U32());
    }
}

/// <summary>
/// S→C 0x16 on PoE2 (vtable 0x143509e58, write 0x14214ddd0, read 0x14214de80): u32 +0x10, u16 +0x14, u16 +0x16, all
/// handed to the listeners at login+0x288 (dispatch 0x1420e8f26). The character screen's listener 0x140f0b340 only
/// passes them to its two slot panels (vfunc +0x38): a notice to the UI, not the answer to 0x10. Not sent.
/// </summary>
public sealed record U32U16U16(ushort MessageId, uint U32, ushort A, ushort B) : ILoginMessage
{
    public void WriteFields(LoginWriter w) => w.U32(U32).U16(A).U16(B);
    internal static U32U16U16 Read(ushort id, ref LoginReader r) => new(id, r.U32(), r.U16(), r.U16());
}

/// <summary>
/// C→S 0x12 on PoE2 (vtable 0x143506430, write 0x1420e4cc0, read 0x1420e4e30, sender 0x1420e9ce0 from the create
/// screen 0x140ece810): create a character. PoE1's fields with UTF-8 Name and League and a third u32: RecordId is the
/// login record's id (the 0x04 U32_10, via [ui+0x328]), U54 and U58 are always 0, StartState is the chosen
/// CharacterStartStates Id ("IntFourDefault" for the Witch), PasswordDigest the private league password's digest (zeros
/// without one). The client throws if no league is selected, so 0x1b must offer one.
/// </summary>
public sealed record CreateCharacterPoe2(string Name, string League, uint RecordId, uint U54, uint U58, string StartState,
    byte[] PasswordDigest) : ILoginMessage
{
    public const ushort Id = 0x12;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) =>
        w.Str(Name).Str(League).U32(RecordId).U32(U54).U32(U58).WStr(StartState).Fixed(PasswordDigest, 32);
    internal static CreateCharacterPoe2 Read(ushort id, ref LoginReader r) =>
        new(r.Str(), r.Str(), r.U32(), r.U32(), r.U32(), r.WStr(), r.Fixed(32));
}

/// <summary>
/// S→C 0x13 on PoE2 (vtable 0x143509e98, write 0x14214eb50, read 0x14214ebf0): PoE1's result and tracking flag plus the
/// login record id, which only selects the record for the tracking-conversion request (when Tracking is set). On 0 the
/// create screen (listener 0x140ed2ab0, then 0x140ecee50) finds the new character by exact name in the last 0x15 list
/// (skipping entries whose B51 has bit 3) and sends Play with (its index, the record id): send the list first.
/// </summary>
public sealed record CreateCharacterResultPoe2(ushort Result, bool Tracking, uint RecordId) : ILoginMessage
{
    public const ushort Id = 0x13;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w) => w.U16(Result).Bool(Tracking).U32(RecordId);
    internal static CreateCharacterResultPoe2 Read(ushort id, ref LoginReader r) => new(r.U16(), r.Bool(), r.U32());
}

/// <summary>A PoE2 league (0xe8 bytes, read by 0x142716e10): PoE1's <see cref="LeagueEntry"/> without RawC4, with one
/// more byte, C2 (+0xc2).</summary>
public sealed record LeagueEntryPoe2(string Name, string Description, string SsfParent, string HardcoreParent,
    string RuthlessParent, ulong RawD0, ulong RawD8, ulong RawC8, byte Flags0, byte Flags1, byte Flags2, byte StartStateSet,
    byte C1, byte C2)
{
    public static LeagueEntryPoe2 From(LeagueEntry e) => new(e.Name, e.Description, e.SsfParent, e.HardcoreParent,
        e.RuthlessParent, e.RawD0, e.RawD8, e.RawC8, e.Flags0, e.Flags1, e.Flags2, e.StartStateSet, e.C1, 0);

    internal void Write(LoginWriter w) =>
        w.WStr(Name).WStr(Description).WStr(SsfParent).WStr(HardcoreParent).WStr(RuthlessParent).RawU64(RawD0)
         .RawU64(RawD8).RawU64(RawC8).U8(Flags0).U8(Flags1).U8(Flags2).U8(StartStateSet).U8(C1).U8(C2);
    internal static LeagueEntryPoe2 Read(ref LoginReader r) =>
        new(r.WStr(), r.WStr(), r.WStr(), r.WStr(), r.WStr(), r.RawU64(), r.RawU64(), r.RawU64(),
            r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8());
}

/// <summary>S→C 0x1b on PoE2 (vtable 0x143509fd8, read 0x14214f1f0): PoE1's <see cref="LeagueList"/> with
/// <see cref="LeagueEntryPoe2"/> entries. Dispatch 0x1420e930e replaces (or appends to) the list at login+0xf08.</summary>
public sealed record LeagueListPoe2(long ServerTime, IReadOnlyList<LeagueEntryPoe2> Leagues, bool Flag0, bool Append)
    : ILoginMessage
{
    public const ushort Id = 0x1b;
    public ushort MessageId => Id;
    public void WriteFields(LoginWriter w)
    {
        w.RawU64((ulong)ServerTime).U32((uint)Leagues.Count);
        foreach (var e in Leagues) e.Write(w);
        w.U8((byte)((Flag0 ? 1 : 0) | (Append ? 2 : 0)));
    }
    internal static LeagueListPoe2 Read(ushort id, ref LoginReader r)
    {
        long t = (long)r.RawU64();
        uint n = r.U32();
        var list = new List<LeagueEntryPoe2>();
        for (uint i = 0; i < n && !r.Short; i++) list.Add(LeagueEntryPoe2.Read(ref r));
        byte f = r.U8();
        return new(t, list, (f & 1) != 0, (f & 2) != 0);
    }
}
