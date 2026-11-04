namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// PoE2 quest flags (PORTING-POE2.md, wave 27). The client keeps the character's quest flags in the Player component,
/// as PoE1's does; they decide which dialogue options an NPC offers (NPCTalk's required and excluded flags, checked by
/// the client) and what the World panel shows. PoE2 has no message of its own for them: the server sends the whole set
/// again as a Player component update (0x2f9), PoE1's 0x2ba with the same flag bit.
/// </summary>
public static class InstanceQuestsPoe2
{
    /// <summary>The Player update's flag (reader 0x141dd0b90, u16 read raw) for the quest flag set: bit 1, as PoE1's.
    /// The reader takes the set into a temporary and hands it to the setter 0x141dd2ad0 once the update is read.</summary>
    public const ushort HasQuestFlags = 0x0002;

    /// <summary>The largest flag row a set holds: 256 blocks of 64 bits.</summary>
    public const int MaxFlag = 256 * 64 - 1;

    /// <summary>
    /// A flag set (reader 0x141deebf0, PoE1's 0x141e83340): u16 LE block count, then per block, in ascending order, u8
    /// block (flag row >> 6) and u64 LE bits (bit = flag row &amp; 63). A flag is a QuestFlags row
    /// (<see cref="World.Poe2Quests"/>). The player's creation blob carries two such sets after its experience and four
    /// scalars (<see cref="PlayerObjectPoe2"/>); the update replaces the first.
    /// </summary>
    public static void WriteFlagSet(BlobWriter w, IEnumerable<int> flags)
    {
        var blocks = new SortedDictionary<int, ulong>();
        foreach (int f in flags)
            if (f >= 0 && f <= MaxFlag) blocks[f >> 6] = blocks.GetValueOrDefault(f >> 6) | 1UL << (f & 63);
        w.U16((ushort)blocks.Count);
        foreach (var (block, bits) in blocks) w.U8((byte)block).U32((uint)bits).U32((uint)(bits >> 32));
    }

    /// <summary>The bytes of <see cref="WriteFlagSet"/>.</summary>
    public static byte[] FlagSet(IEnumerable<int> flags)
    {
        var w = new BlobWriter();
        WriteFlagSet(w, flags);
        return w.ToArray();
    }

    /// <summary>
    /// S→C 0x2f9 to the Player component (<see cref="InstanceProgressionPoe2.ComponentUpdate"/>): u16 LE flags
    /// <see cref="HasQuestFlags"/>, then the whole quest flag set. The client replaces its set: the NPC dialogue windows
    /// opened from then on offer the options the flags allow.
    /// </summary>
    public static byte[] PlayerQuestFlags(uint playerId, IEnumerable<int> flags)
    {
        var w = new BlobWriter().U16(HasQuestFlags);
        WriteFlagSet(w, flags);
        return InstanceProgressionPoe2.ComponentUpdate(playerId, InstanceProgressionPoe2.PlayerComponent, w.ToArray());
    }
}
