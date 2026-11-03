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
