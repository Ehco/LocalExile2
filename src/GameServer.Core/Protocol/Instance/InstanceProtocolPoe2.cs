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
    /// wstr, bytes[3] (PoE1's flag bits); then u8 count of a second list (+0x110; empty here). The handler gives the
    /// message to every local player (0x14210c170).
    /// </summary>
    public static byte[] EnterArea(int localPlayers = 1)
    {
        var w = new LoginWriter().U16(EnterAreaId).U8(0);
        InstanceProtocol.CompactUInt(w, 0);
        w.U8(0).U8(checked((byte)localPlayers));
        for (int i = 0; i < localPlayers; i++)
        {
            InstanceStash.WriteTabs(w, []);
            InstanceStash.WriteTabs(w, []);
            w.U8(0).WStr("").Bytes([0, 0, 0]);
        }
        return w.U8(0).ToArray();
    }

    /// <summary>
    /// S→C 0x2f7 (class 0x14350c4f0, write 0x141f6c540, read 0x141f6c5a0; handler 0x1421343c0): PoE1's create object
    /// 0x2b9. The entity header (0x141f4b740) gained a u8: u32 entity id, u32, u8, u16; then u32 object type and the
    /// component blob behind a compact length (PoE1: u16). When the id is in the world's local player array (set by
    /// 0x24) the object becomes the local player (event 0xe794fcda). The handler only runs while the world is active
    /// (+0x84a); before that the client queues the message (+0x6b0).
    /// </summary>
    public static byte[] CreateObject(uint entityId, uint objectType, ReadOnlySpan<byte> blob)
    {
        var w = new LoginWriter().U16(CreateObjectId).U32(entityId).U32(0).U8(0).U16(0).U32(objectType);
        InstanceProtocol.CompactUInt(w, (uint)blob.Length);
        return w.Bytes(blob).ToArray();
    }
}
