using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// PoE2 direction movement (PORTING-POE2.md, wave 12). PoE2 does not send PoE1's "use Move at a cell" (0x25 / lockstep
/// 0x18e) for a click on the ground: holding the button sends the direction from the player to the cursor, and a zero
/// direction when it is released. In lockstep (network mode 2) the client only sends it; the player moves when the
/// server echoes it back as S→C 0x64.
/// </summary>
public static class InstanceMovementPoe2
{
    public const ushort DirectionMoveId = 0x63, DirectionMoveEchoId = 0x64;

    /// <summary>C→S 0x63 (write 0x141f3a6e0, sender 0x142113a1d).</summary>
    /// <param name="Slot">The local player's slot (+0x181), PoE2's route byte.</param>
    /// <param name="Dx">Direction x, a vector of length about 180 (±127 on a diagonal, ±180 along an axis); 0, 0 = stop.</param>
    /// <param name="Dy">Direction y.</param>
    /// <param name="X">The player's grid cell (Positioned +0x444); 0 in lockstep, where the client does not predict.</param>
    /// <param name="Y">Grid cell y (+0x448).</param>
    /// <param name="Flag">1 when stat 0x5746 is non-zero (sender, while +0x3e7 is 1).</param>
    public sealed record DirectionMove(byte Slot, int Dx, int Dy, int X, int Y, byte Flag)
    {
        public bool IsStop => Dx == 0 && Dy == 0;
    }

    /// <summary>Reads a 0x63 at the start of <paramref name="m"/>: u16 id, u8 slot, four signed compact ints (dx, dy,
    /// x, y), u8. Returns null for another id or a cut-off message; <paramref name="length"/> is the bytes it took.</summary>
    public static DirectionMove? ReadDirectionMove(ReadOnlySpan<byte> m, out int length)
    {
        length = 0;
        if (m.Length < 3 || BinaryPrimitives.ReadUInt16BigEndian(m) != DirectionMoveId) return null;
        int at = 2;
        byte slot = m[at++];
        Span<int> v = stackalloc int[4];
        for (int i = 0; i < 4; i++)
            if (!TryReadCompactInt(m, ref at, out v[i])) return null;
        if (at >= m.Length) return null;
        byte flag = m[at++];
        length = at;
        return new DirectionMove(slot, v[0], v[1], v[2], v[3], flag);
    }

    /// <summary>
    /// S→C 0x64 (world group; factory 0x142186c20, class 0x14350c250, read 0x141f6b260; handler case 0x142149852):
    /// the object header (0x141f4b800: u32 object id, u32, u8, u16, all big-endian), then dx, dy, x, y as signed compact
    /// ints and a u8 bool. A zero direction stops the object. Otherwise, when (x, y) is not the object's cell the client
    /// first puts it there (in predictive mode only when it is 46 cells or more away; closer, it bends the direction),
    /// so x, y must be where the server has the player; then it walks in the direction (0x141e4fb40).
    /// </summary>
    public static byte[] DirectionMoveEcho(uint objectId, int dx, int dy, int x, int y, bool flag = false)
    {
        var w = new LoginWriter().U16(DirectionMoveEchoId).U32(objectId).U32(0).U8(0).U16(0);
        InstanceProtocol.CompactInt(w, dx);
        InstanceProtocol.CompactInt(w, dy);
        InstanceProtocol.CompactInt(w, x);
        InstanceProtocol.CompactInt(w, y);
        return w.U8(flag ? (byte)1 : (byte)0).ToArray();
    }

    public const ushort TeleportId = 0x1dd, MoveToId = 0x1e0;

    /// <summary>S→C 0x1e0 (world group, read 0x141f6d540, Positioned): the object glides to x, y (cells) in
    /// <paramref name="seconds"/>. Object header, u32 x, u32 y, u32 height marker (0x7fffffff: on the terrain), u32 float
    /// duration, u8 ×3 (+0x30..+0x32, 0 here); all big-endian.</summary>
    public static byte[] MoveTo(uint objectId, int x, int y, float seconds) =>
        new LoginWriter().U16(MoveToId).U32(objectId).U32(0).U8(0).U16(0)
            .U32((uint)x).U32((uint)y).U32(0x7fffffff).U32(BitConverter.SingleToUInt32Bits(seconds)).U8(0).U8(0).U8(0).ToArray();

    /// <summary>S→C 0x1dd (world group, read 0x141f6d1c0; the world handler routes 0x1dd–0x1ea to Positioned's
    /// 0x141dda7c0): PoE1's teleport 0x1bf behind the PoE2 object header. u32 x, u32 y (cells), u32 height marker
    /// (0x7fffffff: on the terrain, the z ignored), u32 float z; all big-endian.</summary>
    public static byte[] Teleport(uint objectId, int x, int y) =>
        new LoginWriter().U16(TeleportId).U32(objectId).U32(0).U8(0).U16(0)
            .U32((uint)x).U32((uint)y).U32(0x7fffffff).U32(0).ToArray();

    /// <summary>Reads <see cref="InstanceProtocol.CompactInt"/>'s encoding (client reader 0x141cfc160): the prefix bits
    /// give the length, the value bits are sign-extended.</summary>
    public static bool TryReadCompactInt(ReadOnlySpan<byte> m, ref int at, out int value)
    {
        value = 0;
        if (at >= m.Length) return false;
        byte b = m[at];
        (int extra, int bits) = b switch
        {
            < 0x80 => (0, 7),
            < 0xc0 => (1, 14),
            < 0xe0 => (2, 21),
            < 0xf0 => (3, 28),
            _ => (4, 32),
        };
        if (at + 1 + extra > m.Length) return false;
        uint u = extra == 4 ? 0 : b & (0x7fu >> extra);
        for (int i = 1; i <= extra; i++) u = u << 8 | m[at + i];
        at += 1 + extra;
        value = bits == 32 ? (int)u : (int)(u << (32 - bits)) >> (32 - bits);
        return true;
    }
}
