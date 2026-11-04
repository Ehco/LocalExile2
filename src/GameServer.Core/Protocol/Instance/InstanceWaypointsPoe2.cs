using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>PoE2 waypoint messages (PORTING-POE2.md, wave 27).</summary>
public static class InstanceWaypointsPoe2
{
    public const ushort OpenUiId = 0x98;

    /// <summary>The <see cref="OpenUi"/> kind that opens the World panel for waypoint travel (PoE1's 0x7b kind 0).</summary>
    public const byte WorldPanelUi = 0;

    /// <summary>
    /// S→C 0x98 (reader 0x142155300, class size 0x30): PoE1's 0x7b "open a UI" with the route byte. u8 route, u8 kind,
    /// four u32 BE (PoE1's listener ignores them for the World panel; the server fills the first with the waypoint).
    /// </summary>
    public static byte[] OpenUi(byte kind, uint a = 0, uint b = 0, uint c = 0, uint d = 0) =>
        new LoginWriter().U16(OpenUiId).U8(0).U8(kind).U32(a).U32(b).U32(c).U32(d).ToArray();
}
