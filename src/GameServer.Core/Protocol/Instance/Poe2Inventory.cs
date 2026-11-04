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
