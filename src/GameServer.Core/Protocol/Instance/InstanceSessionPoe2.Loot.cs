using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

// Monster drops: items on the ground and their pickup into the main inventory (PORTING-POE2.md, wave 25).
public sealed partial class InstanceSessionPoe2
{
    /// <summary>How near (cells) the player walks to an item before it is picked up.</summary>
    public const int PickupReach = 2;

    /// <summary>The main inventory's Inventories row (12 × 5).</summary>
    public const byte MainInventoryRow = 0;

    /// <summary>The main hand's Inventories row (Weapon1, 2 × 4; row 1 is the body armour).</summary>
    public const byte Weapon1Row = 2;

    private IReadOnlyDictionary<string, Poe2Items.ItemType> _itemTypes = new Dictionary<string, Poe2Items.ItemType>();
    private Dictionary<uint, Poe2Items.ItemType> _itemTypesByHash = [];
    private readonly Dictionary<uint, WorldItemObjectPoe2> _groundItems = [];
    private uint _nextItemId;
    private readonly Poe2Inventory _mainInventory = new(InstanceInventoriesPoe2.InventoryId(MainInventoryRow),
        InstanceInventoriesPoe2.PlayerDefaults[MainInventoryRow].Width, InstanceInventoriesPoe2.PlayerDefaults[MainInventoryRow].Height);

    // The pickup in progress: the player walks from From (at Start) to Stop (at Arrive), then takes the item.
    private sealed record PendingPickup(uint Item, (int X, int Y) From, (int X, int Y) Stop, double Start, double Arrive);

    private PendingPickup? _pickup;

    private readonly Poe2Inventory _weapon1 = new(InstanceInventoriesPoe2.InventoryId(Weapon1Row),
        InstanceInventoriesPoe2.PlayerDefaults[Weapon1Row].Width, InstanceInventoriesPoe2.PlayerDefaults[Weapon1Row].Height);
    private byte[]? _weaponChange;

    /// <summary>The items lying in the area, by entity id.</summary>
    public IReadOnlyDictionary<uint, WorldItemObjectPoe2> GroundItems { get { lock (_lock) return new Dictionary<uint, WorldItemObjectPoe2>(_groundItems); } }

    /// <summary>The main inventory's items.</summary>
    public IReadOnlyList<Poe2Inventory.Entry> MainInventory { get { lock (_lock) return [.. _mainInventory.Items]; } }

    /// <summary>The main hand's items.</summary>
    public IReadOnlyList<Poe2Inventory.Entry> Weapon1 { get { lock (_lock) return [.. _weapon1.Items]; } }

    private void InitLoot(IReadOnlyDictionary<string, Poe2Items.ItemType>? itemTypes, Poe2Items.ItemType? weapon)
    {
        _itemTypes = itemTypes ?? _itemTypes;
        _itemTypesByHash = _itemTypes.Values.GroupBy(t => t.Hash).ToDictionary(g => g.Key, g => g.First());
        _nextItemId = MonsterId(_monsters.Count);
        if (weapon is not null) _weaponChange = _weapon1.Add(Poe2Items.Create(weapon), weapon.Width, weapon.Height, 1);
    }

    // The starting weapon in the main hand (0xde after the empty inventories), wave 26.
    private IEnumerable<byte[]> EquipmentMessages()
    {
        if (_weaponChange is { } change) yield return change;
    }

    // A kill's drop (Poe2Items.Roll) where the monster fell: a WorldItem object (S→C 0x2f7).
    private IEnumerable<byte[]> DropLoot(int monster, double now)
    {
        if (Poe2Items.Roll(_itemTypes, _monsters[monster].Level, _random) is not { } item) yield break;
        var at = MonsterCell(monster, now);
        var ground = new WorldItemObjectPoe2(at.X, at.Y, item);
        uint id = _nextItemId++;
        _groundItems[id] = ground;
        yield return InstanceProtocolPoe2.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObjectPoe2.MetadataPath), ground.Blob());
    }

    // A click on a ground item (C→S 0x1a5 with the interaction 0x2000c266 and the item as the target): echoed at once, as
    // PoE1's server does, so that the client walks to it; the item is taken on arrival.
    private IReadOnlyList<byte[]> BeginPickup(uint id, UseSkillPoe2 use, double now)
    {
        CancelAttack(now);
        var ground = _groundItems[id];
        var from = Walk.Cell(now);
        double dx = ground.GridX - from.X, dy = ground.GridY - from.Y, distance = Math.Sqrt(dx * dx + dy * dy);
        double walk = Math.Max(0, distance - PickupReach);
        var stop = walk <= 0 ? from : ((int)Math.Round(from.X + dx * walk / distance), (int)Math.Round(from.Y + dy * walk / distance));
        _pickup = new(id, from, stop, now, now + walk / _walkSpeed);
        return [InstanceSkillsPoe2.ActionStarted(PlayerId, from.X, from.Y, ground.GridX, ground.GridY, use.SkillId, use.Flags, id)];
    }

    // A move drops the pickup, the walk estimate where the approach has brought the player by now.
    private void CancelPickup(double now)
    {
        if (_pickup is not { } p || now >= p.Arrive) return;
        double t = p.Arrive <= p.Start ? 1 : Math.Clamp((now - p.Start) / (p.Arrive - p.Start), 0, 1);
        if (p.Stop != p.From)
            Walk.Place((int)Math.Round(p.From.X + (p.Stop.X - p.From.X) * t), (int)Math.Round(p.From.Y + (p.Stop.Y - p.From.Y) * t), now);
        _pickup = null;
    }

    // On arrival the item leaves the ground (S→C 0x2fa) for the main inventory (0xde); with no room it stays.
    private IEnumerable<byte[]> PickupTick(double now)
    {
        if (_pickup is not { } p || now < p.Arrive) yield break;
        _pickup = null;
        if (p.Stop != p.From) Walk.Place(p.Stop.X, p.Stop.Y, now);
        if (!_groundItems.TryGetValue(p.Item, out var ground)) yield break;
        var type = _itemTypesByHash.GetValueOrDefault(ground.Item.TypeHash);
        if (_mainInventory.Add(ground.Item, type?.Width ?? 1, type?.Height ?? 1, type?.MaxStack ?? 1) is not { } change) yield break;
        _groundItems.Remove(p.Item);
        yield return InstanceProtocolPoe2.RemoveObject(p.Item);
        yield return change;
    }
}
