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
        InstanceProtocol.CompactUInt(w, (uint)Pairs.Count);
        foreach (var p in Pairs)
        {
            w.U16(p.Key);
            InstanceProtocol.CompactInt(w, p.Value);
        }
        w.U32(U32_68).U32(U32_6c);
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
        uint pairCount = r.CompactUInt();
        var pairs = new List<U16CompactPair>();
        for (uint i = 0; i < pairCount && !r.Short; i++) pairs.Add(new(r.U16(), r.CompactInt()));
        return new(u70, entries, b28, b29, u74, u76, data, pairs, r.U32(), r.U32());
    }
}

/// <summary>An element of PoE2's Play (0x0e): two u32s from the list the character screen builds.</summary>
public readonly record struct U32Pair(uint A, uint B);

/// <summary>
/// C→S 0x0e on PoE2 (vtable 0x143506470, write 0x1420e47e0, sender 0x1420e9ff0): Play. PoE1 sent u8 realm + u32 index;
/// PoE2 sends one byte (bits 0–6: the realm byte at 0x143a0c068, bit 7: the sender's bool argument), then a compact count
/// and that many u32 pairs. With one character selected the client sent flags 0 and the pair (0, 0) (live 2026-11-03),
/// then waited ("Contacting Server...") for the instance assignment 0x14.
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
