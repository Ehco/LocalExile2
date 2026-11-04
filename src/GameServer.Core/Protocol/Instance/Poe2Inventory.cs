namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// One PoE2 grid inventory as the server keeps it (PORTING-POE2.md, wave 25): its items by cell, the revision the client
/// expects next and the item ids. Changes go out as S→C 0xde (<see cref="InstanceInventoriesPoe2.InventoryChange"/>).
/// </summary>
public sealed class Poe2Inventory(uint inventoryId, byte width, byte height)
{
    /// <summary>An item at a cell, with its grid size and stack size.</summary>
    public sealed record Entry(uint Id, byte X, byte Y, byte Width, byte Height, int MaxStack, Item Item);

    private readonly List<Entry> _items = [];
    private uint _revision;
    private uint _nextId = 1;

    public uint InventoryId => inventoryId;
    public IReadOnlyList<Entry> Items => _items;

    /// <summary>
    /// Puts an item in: onto stacks of its type first (each grown stack is removed and added again with a new id, as
    /// PoE1's server does), the rest in the first free cell, row by row. Returns the change to send, or null with no room
    /// for all of it (then nothing changes).
    /// </summary>
    public byte[]? Add(Item item, byte itemWidth, byte itemHeight, int maxStack)
    {
        int left = item.StackSize;
        var removed = new List<uint>();
        var added = new List<Entry>();
        var grown = new List<(Entry Old, Entry New)>();
        if (maxStack > 1)
            foreach (var e in _items.Where(e => e.Item.TypeHash == item.TypeHash && e.Item.StackSize < maxStack).ToList())
            {
                if (left == 0) break;
                int put = Math.Min(left, maxStack - e.Item.StackSize);
                left -= put;
                grown.Add((e, e with { Item = e.Item with { StackSize = (ushort)(e.Item.StackSize + put) } }));
            }
        (byte X, byte Y)? cell = null;
        if (left > 0 && (cell = FreeCell(itemWidth, itemHeight)) is null) return null;
        foreach (var (old, grownEntry) in grown)
        {
            _items.Remove(old);
            removed.Add(old.Id);
            var renewed = grownEntry with { Id = _nextId++ };
            _items.Add(renewed);
            added.Add(renewed);
        }
        if (cell is { } at)
        {
            var entry = new Entry(_nextId++, at.X, at.Y, itemWidth, itemHeight, maxStack, item with { StackSize = (ushort)left });
            _items.Add(entry);
            added.Add(entry);
        }
        return InstanceInventoriesPoe2.InventoryChange(inventoryId, ++_revision, removed,
            [.. added.Select(e => new InventoryItem(e.Id, e.X, e.Y, e.Item))]);
    }

    /// <summary>The entry with this id, or null.</summary>
    public Entry? Find(uint itemId) => _items.FirstOrDefault(e => e.Id == itemId);

    /// <summary>Takes an item out whole. Returns the change to send, or null for an id not here.</summary>
    public byte[]? Remove(uint itemId)
    {
        if (_items.FirstOrDefault(e => e.Id == itemId) is not { } entry) return null;
        _items.Remove(entry);
        return InstanceInventoriesPoe2.InventoryChange(inventoryId, ++_revision, [entry.Id], []);
    }

    /// <summary>Puts a saved item back at its cell when the cell is free; null when it is not (nothing changes).</summary>
    public byte[]? Restore(Item item, byte itemWidth, byte itemHeight, byte x, byte y)
    {
        if (x + itemWidth > width || y + itemHeight > height || Overlapping(x, y, itemWidth, itemHeight).Any()) return null;
        var entry = new Entry(_nextId++, x, y, itemWidth, itemHeight, 1, item);
        _items.Add(entry);
        return InstanceInventoriesPoe2.InventoryChange(inventoryId, ++_revision, [], [new InventoryItem(entry.Id, x, y, item)]);
    }

    /// <summary>What a placement did: the change to send for this inventory, the item it pushed out (a swap), and what
    /// of the placed stack did not fit on the stack it went onto (still the holder's).</summary>
    public sealed record Placement(byte[] Change, Entry? Displaced = null, Item? Rest = null);

    /// <summary>
    /// Puts an item at a cell (PoE1's rules, wave 31): where nothing overlaps it goes there; where exactly one item
    /// overlaps, a stack of its type takes as much as it can (the rest stays with the holder) and any other item is
    /// swapped out; two or more overlapping items, or a cell out of the grid, refuse (null, nothing changes).
    /// </summary>
    public Placement? Place(Item item, byte itemWidth, byte itemHeight, byte x, byte y, int maxStack)
    {
        if (x + itemWidth > width || y + itemHeight > height) return null;
        var over = Overlapping(x, y, itemWidth, itemHeight).ToList();
        if (over.Count > 1) return null;
        if (over.Count == 0)
        {
            var entry = new Entry(_nextId++, x, y, itemWidth, itemHeight, maxStack, item);
            _items.Add(entry);
            return new(InstanceInventoriesPoe2.InventoryChange(inventoryId, ++_revision, [], [new InventoryItem(entry.Id, x, y, item)]));
        }
        var other = over[0];
        if (other.Item.TypeHash == item.TypeHash && maxStack > 1 && other.Item.StackSize < maxStack)
        {
            int put = Math.Min(item.StackSize, maxStack - other.Item.StackSize);
            _items.Remove(other);
            var grown = other with { Id = _nextId++, Item = other.Item with { StackSize = (ushort)(other.Item.StackSize + put) } };
            _items.Add(grown);
            var rest = item.StackSize > put ? item with { StackSize = (ushort)(item.StackSize - put) } : null;
            return new(InstanceInventoriesPoe2.InventoryChange(inventoryId, ++_revision, [other.Id], [new InventoryItem(grown.Id, grown.X, grown.Y, grown.Item)]), null, rest);
        }
        _items.Remove(other);
        var placed = new Entry(_nextId++, x, y, itemWidth, itemHeight, maxStack, item);
        _items.Add(placed);
        return new(InstanceInventoriesPoe2.InventoryChange(inventoryId, ++_revision, [other.Id], [new InventoryItem(placed.Id, x, y, item)]), other);
    }

    private IEnumerable<Entry> Overlapping(int x, int y, int itemWidth, int itemHeight) =>
        _items.Where(e => x < e.X + e.Width && e.X < x + itemWidth && y < e.Y + e.Height && e.Y < y + itemHeight);

    /// <summary>Takes <paramref name="count"/> of a stack out (the whole item when the stack is not bigger; a bigger
    /// stack is removed and added again smaller with a new id, as a grown one is). Returns the change to send, or null
    /// for an id not here.</summary>
    public byte[]? Take(uint itemId, int count = 1)
    {
        if (_items.FirstOrDefault(e => e.Id == itemId) is not { } entry) return null;
        _items.Remove(entry);
        var added = new List<InventoryItem>();
        if (entry.Item.StackSize > count)
        {
            var smaller = entry with { Id = _nextId++, Item = entry.Item with { StackSize = (ushort)(entry.Item.StackSize - count) } };
            _items.Add(smaller);
            added.Add(new InventoryItem(smaller.Id, smaller.X, smaller.Y, smaller.Item));
        }
        return InstanceInventoriesPoe2.InventoryChange(inventoryId, ++_revision, [entry.Id], added);
    }

    /// <summary>The first cell, row by row, where an item of this size fits, or null.</summary>
    public (byte X, byte Y)? FreeCell(byte itemWidth, byte itemHeight)
    {
        for (int y = 0; y + itemHeight <= height; y++)
            for (int x = 0; x + itemWidth <= width; x++)
                if (!_items.Any(e => x < e.X + e.Width && e.X < x + itemWidth && y < e.Y + e.Height && e.Y < y + itemHeight))
                    return ((byte)x, (byte)y);
        return null;
    }
}
