using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>PoE2 instance-connection messages that differ from <see cref="InstanceProtocol"/> (PORTING-POE2.md, waves 8–9).</summary>
public static class InstanceProtocolPoe2
{
    /// <summary>
    /// S→C 0x11 on PoE2: PoE1's encryption switch plus one byte, read in plaintext before the switch. The dispatcher
    /// (0x1421220f0, case at 0x1421221be) does not use it: it builds the Salsa20 pair from the 0x14 key (info +0x38) with
    /// the client flag, as PoE1 and the login connection do, so <see cref="InstanceKeyring.Ciphers"/> stays correct.
    /// </summary>
    public static byte[] StartEncryption(byte u8 = 0) => new LoginWriter().U16(InstanceProtocol.StartEncryptionId).U8(u8).ToArray();

    public const ushort AreaGeneratedId = 0x9a, EnterAreaId = 0x23, LocalPlayerId = 0x24, CreateObjectId = 0x2f7;

    public const ushort FrameEndId = 0x2f4, FrameLengthId = 0x2f5;

    public const ushort ConnectInstanceId = 0x29, ChangeAreaId = 0x2f1, LeavingId = 0x355;

    public const ushort MonsterTypesId = 0x21;

    public const ushort PauseRequestId = 0x1ff, PauseStateId = 0x1fe;

    /// <summary>C→S 0x1ff (4 bytes; sent by 0x142129330 / 0x1421293a0): PoE1's 0x1d4 with the route byte. u8 route, u8 1 =
    /// pause, 0 = resume. The client counts its requests (instance +0x2734) and sends only the first pause and the last
    /// resume, where 0x141003e60 allows a pause (not in towns); live it pauses while it loads an area.</summary>
    public static bool? ReadPauseRequest(ReadOnlySpan<byte> m) =>
        m.Length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(m) == PauseRequestId ? m[3] != 0 : null;

    /// <summary>S→C 0x1fe (world group, class 0x14350c430, read 0x142184420): PoE1's pause state 0x1d3 behind the entity
    /// header. u8 flags: bit 0 → +0x20, bit 1 → +0x21; the world is paused while both are set.</summary>
    public static byte[] PauseState(bool paused) =>
        new LoginWriter().U16(PauseStateId).U32(0).U32(0).U8(0).U16(0).U8(paused ? (byte)0x03 : (byte)0x00).ToArray();

    public const ushort SkillListId = 0x66;

    /// <summary>
    /// S→C 0x66 (read 0x142153f60; game state case 0x142106d28): the skills the player can put on the skill bar. The
    /// client keeps them, sorted by id, at game +0x7640 +0x140 as 0x44-byte entries; the skill picker (SkillsList
    /// 0x140c6b060 → 0x142139640) offers each entry whose id is a skill of the player's Actor
    /// (<see cref="ActorSkillsPoe2"/>), so a granted skill is not selectable without it. u8 route, u8 merge (0 = replace
    /// the list, 0x1421386c0; else update entries, 0x142138af0), u8 count, then per entry: u32 skill id (big-endian),
    /// u8 (+4), 5 bytes (+5: the skill bar slots holding the skill, bit n = slot n, as the client's own bind 0x142138c90
    /// sets them), u8 a [u8 if a] (+0x3e), u8 b [u8 if b] (+0x40), u8 (+0xa), 3 bytes (+0xb; the client toggles them
    /// through C→S 0x67), 24 bytes (+0x26), 24 bytes (+0xe). The rest is written as 0.
    /// </summary>
    public static byte[] SkillList(IReadOnlyList<(uint Id, ulong Slots)> skills, bool merge = false)
    {
        var w = new LoginWriter().U16(SkillListId).U8(0).U8(merge ? (byte)1 : (byte)0).U8(checked((byte)skills.Count));
        foreach (var (id, slots) in skills)
        {
            w.U32(id).U8(0);
            for (int i = 0; i < SkillBarSlotBytes; i++) w.U8((byte)(slots >> (8 * i)));
            w.U8(0).U8(0).U8(0).Bytes(new byte[3]).Bytes(new byte[24]).Bytes(new byte[24]);
        }
        return w.ToArray();
    }

    /// <summary>The skill entry's slot mask is 5 bytes: 40 skill bar slots.</summary>
    public const int SkillBarSlotBytes = 5, SkillBarSlots = 8 * SkillBarSlotBytes;

    public const ushort SetSkillSlotId = 0x5c;

    /// <summary>C→S 0x5c (8 bytes, sent by 0x142112ea4 after the client bound the slot itself): PoE1's 0x4e with a u32
    /// id. u8 route, u8 slot, u32 skill id (big-endian; 0 clears the slot). Slot 0 is the left mouse button.</summary>
    public static (byte Slot, uint SkillId)? ReadSetSkillSlot(ReadOnlySpan<byte> m) =>
        m.Length >= 8 && BinaryPrimitives.ReadUInt16BigEndian(m) == SetSkillSlotId
            ? (m[3], BinaryPrimitives.ReadUInt32BigEndian(m[4..])) : null;

    /// <summary>S→C 0x21 (read 0x14215aac0, write 0x14215a9e0): PoE1's 0x20 with the route byte. u16 count and u16
    /// preload (big-endian), then count × raw u16 MonsterVarieties row and count × u8 level; the client registers
    /// "&lt;variety Id&gt;@&lt;level&gt;" per pair (<see cref="MonsterObjectPoe2.TypePath"/>).</summary>
    public static byte[] MonsterTypes(IReadOnlyList<(ushort Row, byte Level)> types)
    {
        var w = new LoginWriter().U16(MonsterTypesId).U8(0).U16((ushort)types.Count).U16((ushort)types.Count);
        foreach (var (row, _) in types) w.U8((byte)row).U8((byte)(row >> 8));
        foreach (var (_, level) in types) w.U8(level);
        return w.ToArray();
    }

    /// <summary>
    /// S→C 0x2f1 (instance dispatcher, third switch case 2 at 0x1421250c0): PoE1's change area 0x2b2. u8 route, u16
    /// destination WorldAreas HASH16 (+0x18, looked up by 0x1423e3f10), u8 (+0x1a; PoE1 had a u16 loading screen
    /// row). The handler shows the loading screen and calls 0x14211fc50(this, 1), which sends C→S
    /// <see cref="LeavingId"/> once.
    /// </summary>
    public static byte[] ChangeArea(ushort destinationHash16, byte u8 = 0) =>
        new LoginWriter().U16(ChangeAreaId).U8(0).U16(destinationHash16).U8(u8).ToArray();

    /// <summary>
    /// S→C 0x29 (reader 0x142152020, class size 0x98; handled by the in-game state's slot 0xf8, 0x14100b2a0, "Got
    /// Instance Details"): PoE1's connect to instance 0x27 (<see cref="InstanceTransfer.ConnectInstance"/>) with the
    /// route byte first and one more u8 last. In 0.5.5e: u8 route, str host, u16 port, u32 IPv4 (big-endian), u32, u16
    /// area HASH16 (+0x8c), u32 ticket (+0x40), u8, u8, 64-byte instance key, 8 bytes, u8. The client then opens a new
    /// instance connection with the ticket and keeps the old one until the new one is up.
    /// <para>0.5.5f, found live (PORTING-POE2.md, wave 36): the u32 before the area is gone and the area is followed by
    /// the length-prefixed field the login's 0x14 gained (<see cref="Login.InstanceInfoPoe2"/>), sent empty: u8 route, str
    /// host, u16 port, u32 IPv4, u16 area HASH16, u16 length + bytes, u32 ticket, u8, u8, key, 8 bytes, u8.</para>
    /// </summary>
    public static byte[] ConnectInstance(string host, ushort port, uint ticket, ushort areaHash16, byte[] key64)
    {
        uint ip = BinaryPrimitives.ReadUInt32BigEndian(System.Net.IPAddress.Parse(host).GetAddressBytes());
        return new LoginWriter().U16(ConnectInstanceId).U8(0).Str(host).U16(port).U32(ip).U16(areaHash16).Blob([]).U32(ticket)
            .U8(0).U8(0).Fixed(key64, 64).Fixed(new byte[8], 8).U8(0).ToArray();
    }

    /// <summary>
    /// S→C 0x2f4 (id only; built by the world-group factory 0x142186c20, class 0x143506450): PoE1's frame end 0x2b5. The
    /// PoE2 client runs in lockstep on localhost (Network Mode "auto"): until frames arrive the world neither runs nor is
    /// drawn (the screen stays the fog colour behind the HUD, mana does not regenerate). Live 2026-11-03: 0x2f5 then one
    /// 0x2f4 every 33 ms draws the area and the player.
    /// </summary>
    public static byte[] FrameEnd() => [FrameEndId >> 8, FrameEndId & 0xff];

    /// <summary>S→C 0x2f5 (same factory, class 0x14350c330: u8 at +0x10): PoE1's frame length 0x2b6, the length in ms of
    /// the server frames that follow.</summary>
    public static byte[] FrameLength(byte ms) => [FrameLengthId >> 8, FrameLengthId & 0xff, ms];

    /// <summary>
    /// C→S 0x9a (11 bytes): PoE1's 0x7d with the route byte. u8 (0), u32 BE tile hash, u32 BE doodad hash, sent once the
    /// client has generated the area; the values match its "Tile hash" / "Doodad hash" log lines (live 2026-11-03).
    /// </summary>
    public static (uint Tile, uint Doodad)? ReadAreaGenerated(ReadOnlySpan<byte> m)
    {
        if (m.Length < 11 || BinaryPrimitives.ReadUInt16BigEndian(m) != AreaGeneratedId) return null;
        return (BinaryPrimitives.ReadUInt32BigEndian(m[3..]), BinaryPrimitives.ReadUInt32BigEndian(m[7..]));
    }

    /// <summary>
    /// S→C 0x24 (class 0x1434e6210, write 0x141f3ce30; dispatcher case 0x142123014): PoE1's 0x22. u8 route, u8 slot, u32
    /// entity id. The handler finds the local player whose slot (+0x181) is the byte, sets its +0x184 to the id and has
    /// 0x142133390 store the id in the world's per-slot arrays (+0x608 / +0x630), where create object 0x2f7 looks it up
    /// to pick the local player. The client creates one local player per connection, slot 0 (ctor 0x142103a60).
    /// </summary>
    public static byte[] SetLocalPlayer(uint entityId, byte slot = 0) =>
        new LoginWriter().U16(LocalPlayerId).U8(0).U8(slot).U32(entityId).ToArray();

    /// <summary>
    /// S→C 0x23 (class 0x14350b338, write 0x142157e80, read 0x1421582b0; dispatcher case 0x14212306d): PoE1's enter
    /// area 0x21, with its fields moved into one entry per local player. u8 route, compact u32 (+0x1f8), u8 bits (+0x1fc,
    /// +0x1fd, +0x1fe), u8 count of entries: two stash tab lists (0x142176240, u16 count first), u8 count of u32 pairs,
    /// wstr, bytes[3] (PoE1's flag bits); then u8 count of player records (+0x110, 0x68 bytes each).
    /// <para>The handler gives the message to every local player (0x14210c170), which takes the record whose entity id is
    /// its own (+0x184, set by 0x24) and returns early without one. A record: u32 entity id; u8 bits 0–5 = index of its
    /// entry above, bit 6 → +7, bit 7 → +6 (+6 set: look up a skill); u8 flags (bit 0 → +8, bit 1 → +9, bit 2 → +0xa;
    /// the reader's default is +8 = 1); u8 +5; three rows of Data/Balance/CharacterShapeshiftBasicSkills.dat (0x141f4d170,
    /// 0xff = none), one per weapon set, copied to the local player's +0x7980; u16 count of u16 (at most three kept).
    /// Each weapon inventory panel (rows 2/3, 14/15, 79/80 → set 0, 1, 2; 0x142606af0) reads its set's row without a
    /// null check (0x14049849b), so all three must be valid; the table has exactly three rows.</para>
    /// </summary>
    public static byte[] EnterArea(params uint[] localPlayerIds)
    {
        var w = new LoginWriter().U16(EnterAreaId).U8(0);
        InstanceProtocol.CompactUInt(w, 0);
        w.U8(0).U8(checked((byte)localPlayerIds.Length));
        foreach (var _ in localPlayerIds)
        {
            InstanceStash.WriteTabs(w, []);
            InstanceStash.WriteTabs(w, []);
            w.U8(0).WStr("").Bytes([0, 0, 0]);
        }
        w.U8((byte)localPlayerIds.Length);
        for (int i = 0; i < localPlayerIds.Length; i++)
            w.U32(localPlayerIds[i]).U8(checked((byte)i)).U8(1).U8(0).Bytes([0, 1, 2]).U16(0);
        return w.ToArray();
    }

    /// <summary>
    /// S→C 0x2f7 (class 0x14350c4f0, write 0x141f6c540, read 0x141f6c5a0; handler 0x1421343c0): PoE1's create object
    /// 0x2b9. The entity header (0x141f4b740) gained a u8: u32 entity id, u32, u8, u16; then u32 object type and the
    /// component blob behind a compact length (PoE1: u16). When the id is in the world's local player array (set by
    /// 0x24) the object becomes the local player (event 0xe794fcda). The handler only runs while the world is active
    /// (+0x84a); before that the client queues the message (+0x6b0).
    /// </summary>
    public const ushort RemoveObjectId = 0x2fa;

    /// <summary>S→C 0x2fa (world group, read 0x141f4b800: the object header only): PoE1's remove object 0x2bb.</summary>
    public static byte[] RemoveObject(uint entityId) => new LoginWriter().U16(RemoveObjectId).U32(entityId).U32(0).U8(0).U16(0).ToArray();

    public static byte[] CreateObject(uint entityId, uint objectType, ReadOnlySpan<byte> blob)
    {
        var w = new LoginWriter().U16(CreateObjectId).U32(entityId).U32(0).U8(0).U16(0).U32(objectType);
        InstanceProtocol.CompactUInt(w, (uint)blob.Length);
        return w.Bytes(blob).ToArray();
    }
}
