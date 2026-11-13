using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// The Data blob of PoE2's 0x15 (for its selected entry) and 0x11: what the character screen shows the character
/// wearing. The client (0x1420e5c10) reads it as 31 records of 0x80 bytes, one per visual slot, each by 0x1426c41a0:
/// u8 the item's type (0 = empty, nothing else follows; else its ItemClasses row + 1), then the record of PoE1's
/// <see cref="VisibleItem"/> without its slot byte: (u16 ItemVisualIdentity HASH16, u32 MTXTypeGameSpecific HASH32, u8)
/// skin (0x1426c3d80 → +0x18), (u16 ItemVisualEffect HASH16, u32 HASH32, u8) effect (0x1426c3f50 → +0x40), u8
/// ItemStances row (0x1426c4fd0 → +8), u16 ItemVisualIdentity HASH16 (0x1426c4c50 → +0x68), then three u8s (+0x78, +0x79,
/// +0x7a; PoE1 had one). A blob that ends early throws, so all 31 are sent; an empty blob is an empty preview
/// (0x1420eb1e0). The visual slot of an equipment inventory is its Inventories.datc64 u32 at row byte 23 (31 for none):
/// Weapon1 0, Offhand1 1, Weapon2 2, Offhand2 3, Weapon3 4, Offhand3 5, BodyArmour1 6, Helm1 8, Gloves1 9, Boots1 10,
/// Ring1 14, Ring2 15, Belt1 16, Amulet1 17.
/// </summary>
public static class CharacterPreviewPoe2
{
    public const int Slots = 31;
    public const byte Weapon1 = 0, Offhand1 = 1, BodyArmour1 = 6, Helm1 = 8, Gloves1 = 9, Boots1 = 10;

    /// <summary>The blob for the items shown, by their <see cref="VisibleItem.Slot"/> (a visual slot here, not PoE1's
    /// inventory slot); the other slots are empty, and so is an item without a look or a type (type 0 ends the record,
    /// so writing the rest would shift every later slot). No item gives the empty blob.</summary>
    public static byte[] Blob(IReadOnlyList<VisibleItem> items)
    {
        if (items.Count == 0) return [];
        var bySlot = new VisibleItem?[Slots];
        foreach (var item in items)
        {
            if (item.Slot >= Slots) throw new ArgumentOutOfRangeException(nameof(items), $"visual slot {item.Slot} is not below {Slots}");
            if (bySlot[item.Slot] is not null) throw new ArgumentException($"two items in visual slot {item.Slot}", nameof(items));
            bySlot[item.Slot] = item;
        }
        var w = new BlobWriter();
        foreach (var item in bySlot) Record(w, item);
        return w.ToArray();
    }

    /// <summary>One record as 0x1426c41a0 reads it: the item's type, then the rest unless the slot is empty.</summary>
    public static void Record(BlobWriter w, VisibleItem? item)
    {
        if (item is not { VisualIdentity: not 0, Type: not 0 }) { w.U8(0); return; }
        w.U8(item.Type)
            .U16(item.SkinVisual != 0 ? item.SkinVisual : item.VisualIdentity).U32(item.SkinHash).U8(0)
            .U16(item.EffectVisual).U32(item.EffectHash).U8(0)
            .U8(item.Stance).U16(item.VisualIdentity).U8(0).U8(0).U8(0);
    }
}
