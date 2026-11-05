namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// An item on the ground in PoE2, `Metadata/MiscellaneousObjects/WorldItem` (PORTING-POE2.md, wave 25). The .ot is PoE1's
/// (Positioned, BaseEvents, Animated, WorldItem, InteractionAction + client Render, Targetable) and so is the WorldItem
/// reader (vtable +0x98, 0x141e02860): u32 (non-zero → one more u32), u8 flags (bit 0 → +0x50, bit 1 → +0x56), u32
/// ItemVisualIdentity HASH16 override (0: the item's own), then the item data (0x1421ecfc0). It hands the item to
/// vtable +0xc0 (0x141e02d40). Positioned is the PoE2 player's (17 bytes).
/// </summary>
public sealed record WorldItemObjectPoe2(int GridX, int GridY, Item Item)
{
    public const string MetadataPath = WorldItemObject.MetadataPath;

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0x02);         // object flags: bit 1 puts it in the wake zone (drawn, targetable), as the exits
        w.U8(0);            // base data entry count
        w.I32(GridX).I32(GridY).F32(0).U16(0).U8(0).U8(0).U8(0);    // Positioned
        w.U8(0);            // Animated: none
        w.U32(0).U8(0).U32(0).Bytes(Item.Payload());              // WorldItem
        w.U8(0);            // InteractionAction
        w.U8(0);            // tasks
        return w.ToArray();
    }
}
