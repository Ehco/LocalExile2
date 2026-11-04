using System.Buffers.Binary;
using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// PoE2's gem window (PORTING-POE2.md, wave 30, written without a live test): an uncut gem is cut into a skill gem
/// that sits in a skill slot and grants its skill. C→S 0x5f cuts a gem, 0x60 cuts it into a chosen slot; the skill
/// reaches the client as an Actor component update with the whole skill list and a 0x66 merge.
/// </summary>
public static class InstanceGemsPoe2
{
    public const ushort CutGemId = 0x5f, CutGemIntoSlotId = 0x60;
    public const int CutGemLength = 15, CutGemIntoSlotLength = 20;

    /// <summary>A cut request: <paramref name="Key"/> names the chosen gem (<see cref="Poe2Gems.Resolve"/>), the uncut
    /// gem is <paramref name="ItemId"/> in <paramref name="InventoryId"/>; <paramref name="Target"/> is the slot asked
    /// for with 0x60, null with 0x5f.</summary>
    public sealed record CutGem(uint Key, uint InventoryId, uint ItemId, uint? Target);

    /// <summary>
    /// C→S 0x5f (15 bytes; builder 0x140a4ca20 at 0x140a4d1f1, vtable 0x1434e5198, writer 0x141f388f0 "2 1 4 4 4"): u8
    /// route, u32 BE the selected entry's +0x8c (the gem), u32 BE the uncut gem's inventory (the window's +0x424, an
    /// inventory id looked up with 0x141cf1b70), u32 BE its item (+0x428). C→S 0x60 (20 bytes; 0x142113800, writer
    /// 0x141f39f80 "2 1 4 4 4 4 1"): u8 route, u32 BE inventory, u32 BE item, u32 BE the target, u32 BE the gem, u8.
    /// The field order is the builders' store order, not seen on the wire yet. Null for any other message.
    /// </summary>
    public static CutGem? ReadCutGem(ReadOnlySpan<byte> m)
    {
        if (m.Length == CutGemLength && m[0] == 0 && m[1] == CutGemId)
            return new(U32(m[3..]), U32(m[7..]), U32(m[11..]), null);
        if (m.Length == CutGemIntoSlotLength && m[0] == 0 && m[1] == CutGemIntoSlotId)
            return new(U32(m[15..]), U32(m[3..]), U32(m[7..]), U32(m[11..]));
        return null;
    }

    static uint U32(ReadOnlySpan<byte> m) => BinaryPrimitives.ReadUInt32BigEndian(m);

    /// <summary>The Actor's index in the PoE2 player's component order (Positioned 0, Stats 1, Pathfinding 2, Buffs 3,
    /// Life 4, Animated 5, Player 6, Inventories 7, Actor 8; <see cref="PlayerObjectPoe2.Blob"/>).</summary>
    public const byte ActorComponent = 8;

    /// <summary>The Actor update's flags byte bit that brings a skill list (0x142149510 reads the list on an update
    /// only when f0 bit 3 is set; wave 21). Not seen live yet.</summary>
    public const byte ActorHasSkills = 0x08;

    /// <summary>S→C 0x2f9 to the Actor component: the flags byte with <see cref="ActorHasSkills"/>, then the whole skill
    /// list (<see cref="ActorSkillsPoe2.WriteList"/>). The client's list only adds (as PoE1's 0x40 list does).</summary>
    public static byte[] ActorSkills(uint playerId, IReadOnlyList<GrantedSkillPoe2> skills)
    {
        var w = new BlobWriter().U8(ActorHasSkills);
        ActorSkillsPoe2.WriteList(w, skills);
        return InstanceProgressionPoe2.ComponentUpdate(playerId, ActorComponent, w.ToArray());
    }

    /// <summary>Inventories row 62 "SkillSlots1" (InventoryType 41, 15 × 1), where PoE2 keeps the socketed skill gems.</summary>
    public const byte SkillSlotsRow = 62, SkillSlotCount = 15;

    public static uint SkillSlotsInventoryId => InstanceInventoriesPoe2.InventoryId(SkillSlotsRow);

    /// <summary>The first skill id of the socketed gems: slot n's gem has <see cref="FirstGemSkillId"/> + n, after the
    /// starting skills (<see cref="Poe2StartingSkills"/>).</summary>
    public const uint FirstGemSkillId = ActorSkillsPoe2.FirstSkillId + 0x10;

    /// <summary>The Actor skill of a gem at a level in a slot, or null for a gem without rows.</summary>
    public static GrantedSkillPoe2? Skill(Poe2Gems.Gem gem, int level, int slot)
    {
        if (gem.At(level) is not { } rows || rows.StatSetRows.Count == 0) return null;
        return new(FirstGemSkillId + (uint)slot, rows.EffectsPerLevelRow, rows.StatSetRows[0],
            AdditionalStatSetRows: rows.StatSetRows.Count > 1 ? [.. rows.StatSetRows.Skip(1)] : null);
    }

    /// <summary>The Actor skills of a character's saved gems (<see cref="Poe2Gems.Encode"/>), for its player object.</summary>
    public static IReadOnlyList<GrantedSkillPoe2> Skills(Poe2Gems table, IEnumerable<int> gems) =>
    [
        .. gems.Select(Poe2Gems.Decode)
            .Select(g => table.FindRow(g.Row) is { } gem ? Skill(gem, Math.Max(1, g.Level), g.Slot) : null)
            .Where(s => s is not null)!,
    ];
}
