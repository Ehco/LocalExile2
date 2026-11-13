using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// PoE2's inventory moves (PORTING-POE2.md, wave 31, written without a live test): a click on an item picks it up to
/// the cursor (C→S 0x39), a click with an item held places it (C→S 0x38), PoE1's 0x28 / 0x2a with the route byte. The
/// server answers with inventory changes (0xde) to the inventory and to the cursor inventory.
/// </summary>
public static class InstanceItemsPoe2
{
    public const ushort PickUpId = 0x39, PlaceId = 0x38;
    public const int PickUpLength = 18, PlaceLength = 19;

    /// <summary>The cursor inventory (Inventories row 12 "Cursor1"), where the held item is.</summary>
    public static uint CursorInventoryId => InstanceInventoriesPoe2.InventoryId(InstanceInventoriesPoe2.CursorRow);

    /// <summary>C→S 0x39 (18 bytes; built by 0x142113b90 from the panel's click record, vtable 0x1434e5c68's
    /// neighbour, writer 0x141f36d30 "2 1 4 4 4 1 1 1"): u8 route, u32 BE inventory id (the panel's +0x6f0), u32 BE
    /// item id, u32 BE (the record's third word), u8 0xff, u8, u8. PoE1's 0x28 (inventory, item, three bytes).</summary>
    public sealed record PickUp(uint InventoryId, uint ItemId, uint Extra, byte A, byte B);

    public static PickUp? ReadPickUp(ReadOnlySpan<byte> m) =>
        m.Length == PickUpLength && m[0] == 0 && m[1] == PickUpId
            ? new(U32(m[3..]), U32(m[7..]), U32(m[11..]), m[16], m[17]) : null;

    /// <summary>C→S 0x38 (19 bytes; built by 0x142113c90, writer 0x141f36fc0 "2 1 4 4 4 1 1 1 1"): u8 route, u32 BE
    /// inventory id, u32 BE x, u32 BE y, u8 (PoE1's +0x1d: 1 with Shift, one of the held stack), u8 0xff, u8, u8.
    /// PoE1's 0x2a. The client sends it for any click while it holds an item, an occupied cell included (a swap).</summary>
    public sealed record Place(uint InventoryId, uint X, uint Y, bool One);

    public static Place? ReadPlace(ReadOnlySpan<byte> m) =>
        m.Length == PlaceLength && m[0] == 0 && m[1] == PlaceId
            ? new(U32(m[3..]), U32(m[7..]), U32(m[11..]), m[15] != 0) : null;

    static uint U32(ReadOnlySpan<byte> m) => BinaryPrimitives.ReadUInt32BigEndian(m);
}
