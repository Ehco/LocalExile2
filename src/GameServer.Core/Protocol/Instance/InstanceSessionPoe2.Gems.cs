using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>The character's skill gems (PORTING-POE2.md, wave 30): the gems socketed in the skill slots, kept per
/// character, each granting its skill; the gem window cuts an uncut gem from the inventory into a free slot.</summary>
public sealed partial class InstanceSessionPoe2
{
    // slot → the gem and its level.
    private readonly SortedDictionary<byte, (Poe2Gems.Gem Gem, int Level)> _gems = [];
    private Poe2Gems _gemTable = Poe2Gems.Empty;
    private Action<IReadOnlyCollection<int>>? _onGems;
    private readonly Poe2Inventory _skillSlots = new(InstanceGemsPoe2.SkillSlotsInventoryId, InstanceGemsPoe2.SkillSlotCount, 1);
    private readonly List<byte[]> _gemChanges = [];

    /// <summary>The socketed gems as the character record keeps them (<see cref="Poe2Gems.Encode"/>).</summary>
    public IReadOnlyCollection<int> Gems { get { lock (_lock) return EncodedGems(); } }

    /// <summary>The skill gems in the skill slots, as items.</summary>
    public IReadOnlyList<Poe2Inventory.Entry> SkillSlots { get { lock (_lock) return [.. _skillSlots.Items]; } }

    private IReadOnlyCollection<int> EncodedGems() => [.. _gems.Select(g => Poe2Gems.Encode(g.Value.Gem.Row, g.Value.Level, g.Key))];

    // The saved gems take their slots and become items (0xde on entering, after the starting weapon). A character with
    // none gets its class's starting gem (Characters StartSkillGem) socketed in slot 0 and saved, as a new PoE2
    // character has it; the test knob startingUncutGem puts an Uncut Skill Gem (Level 1) in its inventory as well (once:
    // not while one is there).
    // Called after InitLoot: the item types are needed.
    private void InitGems(IEnumerable<int>? gems, Poe2Gems? table, Action<IReadOnlyCollection<int>>? onGems, bool startingGem, bool startingUncutGem)
    {
        (_gemTable, _onGems) = (table ?? Poe2Gems.Empty, onGems);
        foreach (var (row, level, slot) in (gems ?? []).Select(Poe2Gems.Decode))
        {
            if (_gemTable.FindRow(row) is not { } gem || _gems.ContainsKey((byte)slot) || slot >= InstanceGemsPoe2.SkillSlotCount) continue;
            _gems[(byte)slot] = (gem, Math.Max(1, level));
            if (SocketItem(gem) is { } change) _gemChanges.Add(change);
        }
        if (startingGem && _gems.Count == 0 && _gemTable.StartingGem(_objectType) is { } first
            && InstanceGemsPoe2.Skill(first, 1, 0, InstanceGemsPoe2.HandOf(_player.Skills)) is { } skill)
        {
            _gems[0] = (first, 1);
            if (SocketItem(first) is { } change) _gemChanges.Add(change);
            _player = _player with { Skills = [.. (_player.Skills ?? []).Where(s => s.Id != skill.Id), skill] };
            _onGems?.Invoke(EncodedGems());
        }
        if (startingUncutGem && _gemTable.UncutFor(Poe2Gems.SkillKind, 1) is { } uncut
            && !_mainInventory.Items.Any(e => e.Item.TypeHash == uncut.Hash)
            && _itemTypesByHash.GetValueOrDefault(uncut.Hash) is { } type
            && _mainInventory.Add(Poe2Items.Create(type), type.Width, type.Height, type.MaxStack) is { } given)
            _gemChanges.Add(given);
    }

    // The gem as an item of the skill slots inventory: its .it components (Base, SkillGem, Quality, Sockets) with the
    // SkillGem data as PoE1's (experience 0, variant 0; not verified on the PoE2 reader).
    private byte[]? SocketItem(Poe2Gems.Gem gem)
    {
        var type = _itemTypesByHash.GetValueOrDefault(gem.Hash);
        var item = new Item(gem.Hash, type?.Components ?? ["Base", "SkillGem", "Quality", "Sockets"], type?.VisualIdentity ?? 0) { Poe2 = true };
        return _skillSlots.Add(item, 1, 1, 1);
    }

    private IEnumerable<byte[]> GemMessages() => _gemChanges;

    // C→S 0x5f / 0x60: the uncut gem named leaves the inventory, the chosen gem takes the first free slot (or the one
    // asked for) at the uncut gem's tier, and its skill goes to the client (Actor update, 0x66 merge). Nothing goes out
    // when the request names no uncut gem of ours or no skill gem, or no slot is free: the client's window stays as it is.
    private IReadOnlyList<byte[]> CutGem(InstanceGemsPoe2.CutGem cut)
    {
        if (cut.InventoryId != _mainInventory.InventoryId || _mainInventory.Items.FirstOrDefault(e => e.Id == cut.ItemId) is not { } entry) return [];
        if (_gemTable.UncutByHash(entry.Item.TypeHash) is not { } uncut || _gemTable.Resolve(cut.Key) is not { } gem) return [];
        if (gem.Kind != Poe2Gems.SkillKind || uncut.Kind != Poe2Gems.SkillKind) return [];   // supports and spirit gems: wave 30's follow-up
        byte? free = cut.Target is { } target && target < InstanceGemsPoe2.SkillSlotCount && !_gems.ContainsKey((byte)target)
            ? (byte)target
            : Enumerable.Range(0, InstanceGemsPoe2.SkillSlotCount).Select(s => (byte)s).FirstOrDefault(s => !_gems.ContainsKey(s), (byte)255) is var s && s != 255 ? s : null;
        if (free is not { } slot) return [];
        int level = Math.Max(1, uncut.Tier);
        if (InstanceGemsPoe2.Skill(gem, level, slot, InstanceGemsPoe2.HandOf(_player.Skills)) is not { } skill) return [];
        var messages = new List<byte[]>();
        if (_mainInventory.Take(entry.Id, 1) is { } taken) messages.Add(taken);
        _gems[slot] = (gem, level);
        if (SocketItem(gem) is { } socketed) messages.Add(socketed);
        _player = _player with { Skills = [.. (_player.Skills ?? []).Where(s => s.Id != skill.Id), skill] };
        messages.Add(InstanceGemsPoe2.ActorSkills(PlayerId, [skill]));
        messages.Add(InstanceProtocolPoe2.SkillList([(skill.Id, 0)], merge: true));
        _onGems?.Invoke(EncodedGems());
        ItemsChanged();
        return messages;
    }
}
