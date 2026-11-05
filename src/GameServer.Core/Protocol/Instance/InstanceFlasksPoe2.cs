using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// PoE2's flasks (PORTING-POE2.md, wave 32, written without a live test): the life and mana flasks of the flask
/// inventory, used from the flask bar (C→S 0x70), recovering life or mana over a few seconds and spending charges that
/// kills give back.
/// </summary>
public static class InstanceFlasksPoe2
{
    public const ushort UseFlaskId = 0x70;
    public const int UseFlaskLength = 11;

    /// <summary>Inventories row 11 "Flask1" (type 10, 5 × 2), the flask bar.</summary>
    public const byte FlaskRow = 11;

    public static uint FlaskInventoryId => InstanceInventoriesPoe2.InventoryId(FlaskRow);

    /// <summary>Charges every flask gains on a kill: our own number (PoE2's comes from the flask's stats).</summary>
    public const int ChargesPerKill = 1;

    /// <summary>C→S 0x70 (11 bytes; built by 0x142110150 from the flask bar 0x14047775d with the bar's inventory id
    /// (+0x6f0) and the flask's item id, vtable 0x1434e5c68's family, writer 0x141f38ae0 "2 1 4 4"): u8 route, u32 BE
    /// inventory id, u32 BE item id. PoE1's 0x70 (6 bytes) named the slot; the builder looks the item up by id.</summary>
    public sealed record UseFlask(uint InventoryId, uint ItemId);

    public static UseFlask? ReadUseFlask(ReadOnlySpan<byte> m) =>
        m.Length == UseFlaskLength && m[0] == 0 && m[1] == UseFlaskId
            ? new(BinaryPrimitives.ReadUInt32BigEndian(m[3..]), BinaryPrimitives.ReadUInt32BigEndian(m[7..])) : null;
}
