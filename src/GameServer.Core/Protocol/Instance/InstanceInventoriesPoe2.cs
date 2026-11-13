using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>PoE2 inventories (PORTING-POE2.md, wave 10): PoE1's <see cref="InstanceInventories"/> moved to the local player.</summary>
public static class InstanceInventoriesPoe2
{
    public const ushort InventoryChangeId = 0xde, AddInventoryId = 0xdf;

    /// <summary>One Inventories.datc64 row (0.5.5e): row index, InventoryType (first u32), width (second u32), height
    /// (byte 10 after the Id), single item (byte 8) and equipment (byte 9), the same columns as PoE1.</summary>
    public readonly record struct Slot(byte Row, byte Kind, byte Width, byte Height, bool SingleItem, bool Equipment)
    {
        public byte Flags => (byte)((SingleItem ? 1 : 0) | (Equipment ? 2 : 0));
    }

    public const byte CursorRow = 12;

    /// <summary>The inventory id the server gives a row, as on PoE1 (row + 1).</summary>
    public static uint InventoryId(byte row) => row + 1u;

    /// <summary>Every Inventories.datc64 row of 0.5.5e (121, dumped from data/balance/inventories.datc64). As on PoE1 the
    /// UI looks inventories up when it is built on entering (0x14042daa0 throws "Invalid inventory id" when the lookup
    /// 0x141cf1b70 finds nothing), so an empty inventory is registered for every row.</summary>
    public static readonly Slot[] PlayerDefaults =
    [
        new(0, 0, 12, 5, false, false), new(1, 1, 2, 3, true, true), new(2, 2, 2, 4, true, true),
        new(3, 3, 2, 4, true, true), new(4, 4, 2, 2, true, true), new(5, 5, 1, 1, true, true),
        new(6, 6, 1, 1, true, true), new(7, 6, 1, 1, true, true), new(8, 7, 2, 2, true, true),
        new(9, 8, 2, 2, true, true), new(10, 9, 2, 1, true, true), new(11, 10, 5, 2, false, true),
        new(12, 11, 4, 4, true, false), new(13, 12, 2, 2, false, false), new(14, 2, 2, 4, true, true),
        new(15, 3, 2, 4, true, true), new(16, 15, 2, 4, true, false), new(17, 15, 2, 4, true, false),
        new(18, 15, 2, 4, true, false), new(19, 15, 2, 4, true, false), new(20, 15, 2, 4, true, false),
        new(21, 15, 2, 4, true, false), new(22, 15, 2, 4, true, false), new(23, 13, 57, 1, false, false),
        new(24, 14, 10, 4, false, false), new(25, 12, 6, 1, false, false), new(26, 17, 1, 1, false, false),
        new(27, 15, 4, 8, true, false), new(28, 15, 2, 4, true, false), new(29, 15, 5, 1, false, false),
        new(30, 16, 3, 1, false, false), new(31, 15, 2, 4, true, false), new(32, 15, 2, 5, false, false),
        new(33, 15, 2, 4, true, false), new(34, 15, 6, 4, false, false), new(35, 15, 2, 4, true, false),
        new(36, 15, 4, 1, false, false), new(37, 15, 2, 4, true, false), new(38, 15, 5, 1, false, false),
        new(39, 28, 4, 1, false, false), new(40, 28, 4, 1, false, false), new(41, 15, 1, 1, true, false),
        new(42, 15, 2, 4, true, false), new(43, 15, 2, 4, true, false), new(44, 15, 1, 1, true, false),
        new(45, 34, 7, 2, false, false), new(46, 34, 7, 2, false, false), new(47, 34, 7, 2, false, false),
        new(48, 34, 7, 2, false, false), new(49, 34, 7, 2, false, false), new(50, 34, 7, 2, false, false),
        new(51, 34, 7, 2, false, false), new(52, 34, 7, 2, false, false), new(53, 34, 7, 2, false, false),
        new(54, 35, 1, 1, true, true), new(55, 15, 1, 1, true, false), new(56, 36, 107, 12, false, false),
        new(57, 37, 100, 4, false, false), new(58, 12, 3, 2, false, false), new(59, 12, 3, 2, false, false),
        new(60, 12, 3, 2, false, false), new(61, 12, 3, 2, false, false), new(62, 41, 15, 1, false, false),
        new(63, 42, 5, 5, false, false), new(64, 43, 3, 1, false, false), new(65, 12, 64, 5, false, false),
        new(66, 44, 2, 4, true, false), new(67, 15, 1, 1, true, false), new(68, 12, 5, 5, false, false),
        new(69, 29, 14, 9, false, false), new(70, 45, 6, 5, false, false), new(71, 15, 1, 1, true, false),
        new(72, 48, 6, 2, false, false), new(73, 49, 37, 10, false, false), new(74, 50, 1, 1, true, false),
        new(75, 12, 5, 1, false, false), new(76, 52, 5, 4, false, false), new(77, 53, 12, 12, false, false),
        new(78, 54, 1, 2, true, true), new(79, 2, 2, 4, true, true), new(80, 3, 2, 4, true, true),
        new(81, 15, 1, 1, true, false), new(82, 55, 1, 1, true, false), new(83, 1, 2, 3, true, false),
        new(84, 4, 2, 2, true, false), new(85, 7, 2, 2, true, false), new(86, 8, 2, 2, true, false),
        new(87, 2, 2, 4, true, false), new(88, 3, 2, 4, true, false), new(89, 15, 2, 4, true, false),
        new(90, 17, 2, 2, true, false), new(91, 15, 6, 4, false, false), new(92, 17, 2, 2, true, false),
        new(93, 56, 1, 1, true, false), new(94, 57, 3, 2, false, false), new(95, 57, 8, 1, false, false),
        new(96, 58, 4, 1, false, false), new(97, 0, 4, 5, false, false), new(98, 15, 2, 4, true, false),
        new(99, 15, 2, 4, true, false), new(100, 59, 1, 1, true, false), new(101, 6, 1, 1, true, true),
        new(102, 15, 8, 4, false, false), new(103, 15, 4, 4, true, false), new(104, 63, 1, 1, true, false),
        new(105, 65, 5, 1, false, false), new(106, 15, 6, 4, false, false), new(107, 66, 1, 1, true, true),
        new(108, 66, 1, 1, true, true), new(109, 67, 1, 1, true, true), new(110, 67, 1, 1, true, true),
        new(111, 68, 4, 4, true, false), new(112, 15, 60, 1, false, false), new(113, 15, 60, 1, false, false),
        new(114, 17, 2, 3, true, false), new(115, 15, 4, 3, false, false), new(116, 70, 1, 1, true, false),
        new(117, 15, 3, 1, false, false), new(118, 15, 1, 1, true, false), new(119, 15, 2, 4, true, false),
        new(120, 40, 12, 10, false, false)
    ];

    /// <summary>
    /// S→C 0xdf (class 0x1431064e0, read 0x140ca2c20; local player dispatcher 0x142106bb0): PoE1's add inventory 0xc0,
    /// now routed to a local player. u8 route, u8 manager (0x142115810), u32 inventory id, then the inventory object
    /// (0x1425aa790): u8 kind (+0x140), u8 row (+0x144), u8 width, u8 height, u8 flags, compact item count (0x1425aaa50).
    /// Flags bit 0: single item, bit 1: equipment; bits 3 and 4 add a u64 and four u32 (off here). PoE1's two trailing
    /// bytes are gone. Neighbours: 0xde changes an inventory (PoE1 0xbf), 0xe0 removes one (PoE1 0xc1).
    /// </summary>
    /// <summary>
    /// S→C 0xde (read 0x142158b20, body 0x1427174f0): PoE1's change one inventory 0xbf with the route byte. u8 route, u8
    /// manager, u32 inventory id, u32 revision, u32 removed count (0xffffffff: every item) + the item ids, u32 added count +
    /// per item (0x1425aae20) u32 id, u8 x, u8 y and the item with its u16 length (0x1421ecef0), then u8 flag (1: four u32
    /// follow); big-endian but the item data.
    /// </summary>
    public static byte[] InventoryChange(uint inventoryId, uint revision, IReadOnlyList<uint> removed, IReadOnlyList<InventoryItem> added,
        byte manager = 0)
    {
        var w = new LoginWriter().U16(InventoryChangeId).U8(0).U8(manager).U32(inventoryId).U32(revision).U32((uint)removed.Count);
        foreach (uint id in removed) w.U32(id);
        w.U32((uint)added.Count);
        foreach (var it in added) w.U32(it.Id).U8(it.X).U8(it.Y).Bytes(it.Item.Wire());
        return w.U8(0).ToArray();
    }

    /// <summary>The length of one <see cref="AddInventory"/> message (the entry sends them as one buffer).</summary>
    public const int AddInventoryLength = 14;

    public static byte[] AddInventory(uint inventoryId, Slot slot, byte manager = 0)
    {
        var w = new LoginWriter().U16(AddInventoryId).U8(0).U8(manager).U32(inventoryId)
            .U8(slot.Kind).U8(slot.Row).U8(slot.Width).U8(slot.Height).U8(slot.Flags);
        InstanceProtocol.CompactUInt(w, 0);
        return w.ToArray();
    }
}
