using GameServer.Core.Accounts;
using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>The character's items between the inventory and the cursor (PORTING-POE2.md, wave 31): picking up, placing,
/// swapping and stacking, and the inventory saved per character (<see cref="SavedItem"/>).</summary>
public sealed partial class InstanceSessionPoe2
{
    private readonly Poe2Inventory _cursor = new(InstanceItemsPoe2.CursorInventoryId,
        InstanceInventoriesPoe2.PlayerDefaults[InstanceInventoriesPoe2.CursorRow].Width, InstanceInventoriesPoe2.PlayerDefaults[InstanceInventoriesPoe2.CursorRow].Height);
    private Action<IReadOnlyList<SavedItem>>? _onItems;
    private readonly List<byte[]> _itemChanges = [];

    /// <summary>The item held on the cursor, if any.</summary>
    public Poe2Inventory.Entry? Held { get { lock (_lock) return _cursor.Items.FirstOrDefault(); } }

    /// <summary>The main inventory, the cursor and the flasks as the character record keeps them.</summary>
    public IReadOnlyList<SavedItem> SavedItems { get { lock (_lock) return SaveItems(); } }

    private IReadOnlyList<SavedItem> SaveItems() =>
    [
        .. _mainInventory.Items.Select(e => new SavedItem(_mainInventory.InventoryId, e.X, e.Y, e.Item.TypeHash, e.Item.StackSize)),
        .. _cursor.Items.Select(e => new SavedItem(_cursor.InventoryId, e.X, e.Y, e.Item.TypeHash, e.Item.StackSize)),
        .. _flasks.Items.Select(e => new SavedItem(_flasks.InventoryId, e.X, e.Y, e.Item.TypeHash, e.Item.StackSize)),
    ];

    // The saved items take their cells again (0xde on entering, after the starting weapon); what was on the cursor goes
    // back into the inventory where it fits. Items of a type the server does not know are dropped from the save. Called
    // after InitLoot: the item types are needed.
    private void InitItems(IEnumerable<SavedItem>? items, Action<IReadOnlyList<SavedItem>>? onItems)
    {
        _onItems = onItems;
        foreach (var saved in items ?? [])
        {
            if (_itemTypesByHash.GetValueOrDefault(saved.TypeHash) is not { } type) continue;
            if (saved.InventoryId == _flasks.InventoryId && type.Flask is not null)
            {
                // A flask comes back full (a relog is a town visit's worth of refill).
                if (_flasks.Restore(Poe2Items.CreateFlask(type), type.Width, type.Height, saved.X, saved.Y) is { } refilled) _flaskChanges.Add(refilled);
                continue;
            }
            var item = Poe2Items.Create(type, saved.Stack);
            byte[]? change = saved.InventoryId == _mainInventory.InventoryId
                ? _mainInventory.Restore(item, type.Width, type.Height, saved.X, saved.Y) ?? _mainInventory.Add(item, type.Width, type.Height, type.MaxStack)
                : _mainInventory.Add(item, type.Width, type.Height, type.MaxStack);
            if (change is not null) _itemChanges.Add(change);
        }
    }

    private IEnumerable<byte[]> ItemMessages() => _itemChanges;

    private void ItemsChanged() => _onItems?.Invoke(SaveItems());

    /// <summary>The inventories a click may name: the main inventory and the cursor (the equipment, the flasks and the
    /// skill slots are not moved yet).</summary>
    private Poe2Inventory? InventoryOf(uint id) =>
        id == _mainInventory.InventoryId ? _mainInventory : id == _cursor.InventoryId ? _cursor : null;

    // C→S 0x39: the item leaves its inventory for the cursor (0xde to each). Nothing goes out for an unknown inventory
    // or item, or with a hand already full: the client then keeps what it has.
    private IReadOnlyList<byte[]> PickUpItem(InstanceItemsPoe2.PickUp pick)
    {
        if (InventoryOf(pick.InventoryId) is not { } from || from == _cursor || _cursor.Items.Count > 0) return [];
        if (from.Find(pick.ItemId) is not { } entry || from.Remove(pick.ItemId) is not { } removed) return [];
        var held = _cursor.Add(entry.Item, entry.Width, entry.Height, 1);
        ItemsChanged();
        return held is null ? [removed] : [removed, held];
    }

    // C→S 0x38: the held item goes to the cell clicked: into a free place, onto a stack of its type (what does not fit
    // stays held), or in exchange for the item there (now held). With Shift, one of the held stack.
    private IReadOnlyList<byte[]> PlaceItem(InstanceItemsPoe2.Place place)
    {
        if (_cursor.Items.FirstOrDefault() is not { } held || InventoryOf(place.InventoryId) is not { } into || into == _cursor) return [];
        var type = _itemTypesByHash.GetValueOrDefault(held.Item.TypeHash);
        var put = place.One && held.Item.StackSize > 1 ? held.Item with { StackSize = 1 } : held.Item;
        if (place.X > byte.MaxValue || place.Y > byte.MaxValue
            || into.Place(put, held.Width, held.Height, (byte)place.X, (byte)place.Y, type?.MaxStack ?? 1) is not { } done) return [];
        var messages = new List<byte[]> { done.Change };
        int left = (place.One && held.Item.StackSize > 1 ? held.Item.StackSize - 1 : 0) + (done.Rest?.StackSize ?? 0);
        if (_cursor.Remove(held.Id) is { } gone) messages.Add(gone);
        if (done.Displaced is { } displaced && _cursor.Add(displaced.Item, displaced.Width, displaced.Height, 1) is { } swapped) messages.Add(swapped);
        else if (left > 0 && _cursor.Add(held.Item with { StackSize = (ushort)left }, held.Width, held.Height, 1) is { } rest) messages.Add(rest);
        ItemsChanged();
        return messages;
    }
}
