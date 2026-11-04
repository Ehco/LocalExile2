using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>PoE2 waypoint messages (PORTING-POE2.md, wave 27): the character's activated waypoints (S→C 0x5a), the World
/// panel opened for travel (S→C 0x98 kind 0) and the pin picked on it (C→S 0x5b). PoE1's 0x4c, 0x7b and 0x4d.</summary>
public static class InstanceWaypointsPoe2
{
    public const ushort OpenUiId = 0x98;

    /// <summary>The <see cref="OpenUi"/> kind that opens the World panel for waypoint travel (PoE1's 0x7b kind 0).</summary>
    public const byte WorldPanelUi = 0;

    /// <summary>
    /// S→C 0x98 (reader 0x142155300, class size 0x30): PoE1's 0x7b "open a UI" with the route byte. u8 route, u8 kind,
    /// four u32 BE (PoE1's listener ignores them for the World panel; the server fills the first with the waypoint, which
    /// the panel's pin click sends back in 0x5b).
    /// </summary>
    public static byte[] OpenUi(byte kind, uint a = 0, uint b = 0, uint c = 0, uint d = 0) =>
        new LoginWriter().U16(OpenUiId).U8(0).U8(kind).U32(a).U32(b).U32(c).U32(d).ToArray();

    public const ushort WaypointsId = 0x5a;

    /// <summary>Bytes of the activated-waypoint bitset: 176 bits for the client's at most 175 waypoint areas (PoE1: 19).</summary>
    public const int BitsetLength = 22;

    /// <summary>
    /// S→C 0x5a (class 0x142153b40, reader 0x142153c00; the instance dispatcher 0x1421220f0's case 0x142123878): PoE1's
    /// 0x4c with the route byte. u8 route, u32 BE (+0x18, passed with the bitset to the listeners at instance +0xff0;
    /// 0 works in PoE1), then <see cref="BitsetLength"/> raw bytes (+0x1c), copied to instance +0x2318. Bit i is the i-th
    /// WorldAreas row with HasWaypoint (+0x15) whose +0x1b4 is 1 (a town), 2 (an area) or 31, in row order (the client's
    /// list 0x141f68930, at most 175 entries; <see cref="World.Poe2Quests"/>): G1_town is bit 0, G1_2 (Clearfell) bit 1.
    /// The World panel lights those pins and lets the player travel to them. The case compares the current area's bit
    /// before and after (0x141f68b30): when a later 0x5a of the connection turns it on, the client plays the waypoint's
    /// activation (0x142121fe0, state 2). Areas of the kinds in mask 0x4109008 (0x141f68d10) count as always on.
    /// </summary>
    public static byte[] Waypoints(IEnumerable<int> unlockedBits, uint u32 = 0)
    {
        var bits = new byte[BitsetLength];
        foreach (int i in unlockedBits)
            if (i >= 0 && i < BitsetLength * 8) bits[i >> 3] |= (byte)(1 << (i & 7));
        return new LoginWriter().U16(WaypointsId).U8(0).U32(u32).Fixed(bits, BitsetLength).ToArray();
    }

    public const ushort TravelId = 0x5b;

    /// <summary>The length of a C→S 0x5b: id, route, u16, u32, u8.</summary>
    public const int TravelLength = 10;

    /// <summary>C→S 0x5b: a waypoint picked on the World panel. <paramref name="Waypoint"/> is the object the panel was
    /// opened from (the first u32 of 0x98); Ctrl asks for the instance chooser.</summary>
    public sealed record Travel(ushort AreaHash16, uint Waypoint, bool Ctrl);

    /// <summary>
    /// C→S 0x5b (sender 0x142110920, class vtable 0x1434e6548, writer 0x141f396f0; sent by the World panel's pin click
    /// 0x140bb4a70, as PoE1's 0x4d by 0x140a9b580): u8 route (the local player's +0x181), u16 BE destination WorldAreas
    /// HASH16 (the pin's row +0x2a), u32 BE (the panel's +0x340), u8 Ctrl held. Null for any other message.
    /// </summary>
    public static Travel? ReadTravel(ReadOnlySpan<byte> m) =>
        m.Length == TravelLength && m[0] == 0 && m[1] == TravelId
            ? new(BinaryPrimitives.ReadUInt16BigEndian(m[3..]), BinaryPrimitives.ReadUInt32BigEndian(m[5..]), m[9] != 0)
            : null;
}
