using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// PoE2's passive skill tree (PORTING-POE2.md, wave 28): the character's tree (S→C 0x3a, PoE1's 0x33) and the tree
/// screen's requests (C→S 0x3c allocate, 0x3e refund, 0x48 refund all; 0x46 / 0x360 and 0x47 / 0x361 not mapped).
/// </summary>
public static class InstancePassivesPoe2
{
    public const ushort CharacterPassivesId = 0x3a;

    /// <summary>
    /// S→C 0x3a (reader 0x142152800, class size 0xb8; per-player dispatcher case 0x142107efd → the tree's
    /// 0x141ea1920 / 0x141e9b870): u8 route; three u16 BE: the extra skill points (quests; +0xa8 → tree +0x2c8, added
    /// to the level's), a second count (+0xaa → +0x2d8; refund points?), the weapon set points (+0xac → +0x2cc, both
    /// sets); u8 level (+0xaf → +0x2c4: the client's points from the level are (level − 1) × 1); u8 class (+0xae → +0x2b8);
    /// a flag set (u16 BE count of (u8 block, u64) entries, +0x90; sent empty); u8 flags (bit 1: no lists follow); then
    /// u16 BE n × (u16 BE node, u8) (+0x18): the allocated PassiveSkillGraphIds, sorted, the u8 the weapon set of the
    /// node (0: both; the tree's list at +0x1a8, diffed by 0x141e9b870; nodes sent in the +0x30 list stayed
    /// unallocated, live 2026-11-04); u16 BE n × raw u16 (+0x30; empty); u16 BE n × raw u16 (+0x48; empty); u16 BE n ×
    /// raw u32 (+0x60; empty: the tree's +0x208 list, not the choices); u8 n × raw u32 (+0x78): the attribute choices,
    /// node | chosen node &lt;&lt; 16, sorted by node (the dispatcher 0x142108058 passes +0x78 as the applier's sixth
    /// argument, which replaces the tree's choice list at +0x1d8; 0x141e9dca0 drops an attribute node whose choice is
    /// not there, "Removing N invalid/unconnected passive skills"). Live 2026-11-04 with 10, 20, 40 and level 5:
    /// "Skill Points: 14" (4 + 10), "Weapon Set: 40 (40)" for both sets.
    /// </summary>
    public static byte[] CharacterPassives(byte level, byte classRow, IEnumerable<ushort>? nodes = null,
        IEnumerable<(ushort Node, ushort Choice)>? choices = null, ushort questPoints = 0, ushort weaponSetPoints = 0, ushort refundPoints = 0)
    {
        var sorted = (nodes ?? []).Distinct().Order().ToList();
        var chosen = (choices ?? []).Where(c => c.Choice != 0).OrderBy(c => c.Node).ToList();
        var w = new LoginWriter().U16(CharacterPassivesId).U8(0).U16(questPoints).U16(refundPoints).U16(weaponSetPoints)
            .U8(level).U8(classRow)
            .U16(0)                 // the flag set: no blocks
            .U8(0);                 // flags: the lists follow
        // +0x18: the allocated nodes, (u16 BE node, u8 0), sorted: the tree's own list at +0x1a8 holds the same
        // (u16, u8) entries and 0x141e9b870 diffs the two (the u16 lists at +0x30 and +0x48 are something else).
        w.U16(checked((ushort)sorted.Count));
        foreach (ushort n in sorted) w.U16(n).U8(0);
        w.U16(0).U16(0).U16(0);     // +0x30, +0x48, +0x60: none
        // +0x78: the attribute choices (at most 255: the count is a byte).
        w.U8(checked((byte)chosen.Count));
        foreach (var (node, choice) in chosen) w.RawU32((uint)(node | choice << 16));
        return w.ToArray();
    }

    public const ushort GoldId = 0x2db;

    /// <summary>
    /// S→C 0x2db (reader 0x14086d300, class 0x20; dispatcher 0x1420ee490, case 0x1420f14b6): u8 route, u32 BE the
    /// character's gold, kept at +0x39d0 of the player's state (what the script's GetCurrentGoldAmount, 0x14190a830,
    /// returns). The tree screen compares it with the refund cost (0x1409ad790 → 0x1409e3500, GoldRespecPrices by
    /// level) and ignores the click on an allocated node when the gold is short: live 2026-11-04, no refund at 0 gold.
    /// </summary>
    public static byte[] Gold(uint amount) => new LoginWriter().U16(GoldId).U8(0).U32(amount).ToArray();

    public const ushort AllocateId = 0x3c, RefundId = 0x3e, RefundAllId = 0x48;
    public const ushort ChooseId = 0x46, ChooseAltId = 0x360, ConfirmId = 0x47, ConfirmAltId = 0x361;
    public const int AllocateLength = 9, RefundLength = 6, RefundAllLength = 3, ChooseLength = 7, ConfirmLength = 5;

    /// <summary>C→S 0x3c: the tree screen asks for a node; for an attribute node, <paramref name="Choice"/> is the generic
    /// attribute node picked (generic_attribute_strength ...), else 0.</summary>
    public sealed record Allocate(ushort Node, byte Flag, ushort Choice, byte Flag2);

    /// <summary>
    /// C→S 0x3c (9 bytes; sender 0x1409ac830 at 0x1409acb14, class vtable 0x1434e5458): u8 route, u16 BE node
    /// (PassiveSkillGraphId, the node record's +0x30), u8, u16 BE the chosen attribute node or 0, u8. PoE1's 0x34. Live
    /// 2026-11-04: <c>003C 00 0F60 00 0000 00</c> (Melee Damage 3936), one message per node of a path, and
    /// <c>003C 00 3455 00 66B9 00</c> (Attribute 13397 as Strength 26297). Null for any other message.
    /// </summary>
    public static Allocate? ReadAllocate(ReadOnlySpan<byte> m) =>
        m.Length == AllocateLength && m[0] == 0 && m[1] == AllocateId
            ? new(BinaryPrimitives.ReadUInt16BigEndian(m[3..]), m[5], BinaryPrimitives.ReadUInt16BigEndian(m[6..]), m[8]) : null;

    /// <summary>C→S 0x3e (6 bytes; sender at 0x1409acd51, vtable 0x1434e64e8): u8 route, u16 BE node, u8. PoE1's 0x36.
    /// Sent on Apply for each node marked for refund (a click on an allocated node, once the gold covers it).</summary>
    public static (ushort Node, byte Flag)? ReadRefund(ReadOnlySpan<byte> m) =>
        m.Length == RefundLength && m[0] == 0 && m[1] == RefundId ? (BinaryPrimitives.ReadUInt16BigEndian(m[3..]), m[5]) : null;

    /// <summary>C→S 0x48 (3 bytes; sender 0x1409a0510, vtable 0x1434e5158): u8 route. PoE1's 0x37, refund all.</summary>
    public static bool IsRefundAll(ReadOnlySpan<byte> m) => m.Length == RefundAllLength && m[0] == 0 && m[1] == RefundAllId;
}
