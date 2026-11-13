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
    // or item, or with a hand already full: the client then keeps what it has. The cursor's room is checked before the
    // item leaves its inventory, so a pick up can never destroy an item the cursor cannot carry (wave 37 follow-up).
    private IReadOnlyList<byte[]> PickUpItem(InstanceItemsPoe2.PickUp pick)
    {
        if (InventoryOf(pick.InventoryId) is not { } from || from == _cursor || _cursor.Items.Count > 0) return [];
        if (from.Find(pick.ItemId) is not { } entry || _cursor.FreeCell(entry.Width, entry.Height) is null) return [];
        if (from.Remove(pick.ItemId) is not { } removed) return [];
        if (_cursor.Add(entry.Item, entry.Width, entry.Height, 1) is not { } held)
            return from.Restore(entry.Item, entry.Width, entry.Height, entry.X, entry.Y) is { } back
                ? [removed, back] : [removed];   // unreachable after the FreeCell check; keeps the item anyway
        ItemsChanged();
        return [removed, held];
    }

    // C→S 0x38: the held item goes to the cell clicked: into a free place, onto a stack of its type (what does not fit
    // stays held), or in exchange for the item there (now held). With Shift, one of the held stack.
    // Everything the cursor has to take back — the item a swap pushes out AND/OR the rest of the stack — is checked to
    // fit before anything moves, and both are restored independently: a shift-place that swapped used to drop the rest
    // of the held stack (wave 37 follow-up).
    private IReadOnlyList<byte[]> PlaceItem(InstanceItemsPoe2.Place place)
    {
        if (_cursor.Items.FirstOrDefault() is not { } held || InventoryOf(place.InventoryId) is not { } into || into == _cursor) return [];
        if (place.X > byte.MaxValue || place.Y > byte.MaxValue
            || place.X + held.Width > into.Width || place.Y + held.Height > into.Height) return [];
        var type = _itemTypesByHash.GetValueOrDefault(held.Item.TypeHash);
        int maxStack = type?.MaxStack ?? 1;
        var put = place.One && held.Item.StackSize > 1 ? held.Item with { StackSize = 1 } : held.Item;
        var over = into.Overlaps((byte)place.X, (byte)place.Y, held.Width, held.Height);
        if (over.Count > 1) return [];
        // a stack of its own type with room is merged into; anything else is swapped out
        bool merges = over.Count == 1 && over[0].Item.TypeHash == held.Item.TypeHash
            && maxStack > 1 && over[0].Item.StackSize < maxStack;
        var back = new List<(byte Width, byte Height)>();
        if (held.Item.StackSize > 1 || place.One) back.Add((held.Width, held.Height));        // the stack, whole or in part
        if (over.Count == 1 && !merges) back.Add((over[0].Width, over[0].Height));            // the item a swap pushes out
        if (!_cursor.CanHold(held, [.. back])) return [];
        if (_cursor.Remove(held.Id) is not { } gone) return [];
        if (into.Place(put, held.Width, held.Height, (byte)place.X, (byte)place.Y, maxStack) is not { } done)
            return _cursor.Add(held.Item, held.Width, held.Height, 1) is { } undone ? [gone, undone] : [gone];

        var messages = new List<byte[]> { done.Change, gone };
        if (done.Displaced is { } displaced && _cursor.Add(displaced.Item, displaced.Width, displaced.Height, 1) is { } swapped)
            messages.Add(swapped);
        int left = (place.One && held.Item.StackSize > 1 ? held.Item.StackSize - 1 : 0) + (done.Rest?.StackSize ?? 0);
        if (left > 0 && _cursor.Add(held.Item with { StackSize = (ushort)left }, held.Width, held.Height, 1) is { } rest)
            messages.Add(rest);
        ItemsChanged();
        return messages;
    }
}
