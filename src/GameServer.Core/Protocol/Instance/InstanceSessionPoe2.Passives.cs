using GameServer.Core.Accounts;
using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>The character's passive skill tree (PORTING-POE2.md, wave 28): its allocated nodes, kept per character by
/// the Gateway, sent with 0x3a on entering and after every change, allocated and refunded from the tree screen; their
/// stats go into the player's (<see cref="Poe2CharacterStats"/>). A refund costs gold (0x2db), kept for the session.</summary>
public sealed partial class InstanceSessionPoe2
{
    /// <summary>Passive points a level gives, from level 2 on (the client's points from the level are (level − 1) × this).</summary>
    public const int PointsPerLevel = 1;

    // node → the chosen attribute node (0 for none).
    private readonly SortedDictionary<ushort, ushort> _passives = [];
    private Poe2Passives _passiveNodes = Poe2Passives.Empty;
    private Action<IReadOnlyCollection<int>>? _onPassives;
    private ushort _questPassivePoints;
    private uint _gold;

    /// <summary>The allocations as the character record keeps them (<see cref="Poe2Passives.Encode"/>).</summary>
    public IReadOnlyCollection<int> Passives { get { lock (_lock) return [.. _passives.Select(p => Poe2Passives.Encode(p.Key, p.Value))]; } }

    /// <summary>Points the character may still allocate: (level − 1) × <see cref="PointsPerLevel"/> plus the quest
    /// points, less the allocated nodes.</summary>
    public int PassivePointsLeft { get { lock (_lock) return PointsLeft(); } }

    /// <summary>The character's gold, as the client shows it.</summary>
    public uint Gold { get { lock (_lock) return _gold; } }

    /// <summary>The gold a refund of one node costs at the current level.</summary>
    public int RefundCost { get { lock (_lock) return _passiveNodes.RefundCost(_level); } }

    private int PointsLeft() => (_level - 1) * PointsPerLevel + _questPassivePoints - _passives.Count;

    private void InitPassives(IEnumerable<int>? passives, ushort questPoints, Poe2Passives? nodes, Action<IReadOnlyCollection<int>>? onPassives, uint gold)
    {
        foreach (var (node, choice) in (passives ?? []).Select(Poe2Passives.Decode)) _passives[node] = choice;
        (_questPassivePoints, _passiveNodes, _onPassives, _gold) = (questPoints, nodes ?? Poe2Passives.Empty, onPassives, gold);
    }

    private Poe2Classes.Row ClassRow => Poe2Classes.Rows[Math.Min(_player.ClassRow, Poe2Classes.Rows.Count - 1)];

    /// <summary>The class's sheet at the current level with the allocated passives' stats.</summary>
    public Poe2CharacterStats.Sheet Sheet { get { lock (_lock) return CurrentSheet(); } }

    private Poe2CharacterStats.Sheet CurrentSheet() =>
        Poe2CharacterStats.For(ClassRow, _level, _passiveNodes.Modifiers(_passives.Select(p => (p.Key, p.Value))));

    private byte[] PassivesMessage() =>
        InstancePassivesPoe2.CharacterPassives((byte)Math.Clamp(_level, 1, 255), _player.ClassRow, _passives.Keys,
            _passives.Select(p => (p.Key, p.Value)), questPoints: _questPassivePoints);

    private byte[] GoldMessage() => InstancePassivesPoe2.Gold(_gold);

    // The player's stats again after a change of the tree: the maximums move with it (the pools are not refilled).
    private IEnumerable<byte[]> PassiveStatsMessages()
    {
        var sheet = CurrentSheet();
        (_maximumLife, _maximumMana) = (sheet.MaximumLife, sheet.MaximumMana);
        if (_playerLife > _maximumLife) _playerLife = _maximumLife;
        yield return InstanceCombatPoe2.StatsUpdate(PlayerId, sheet.Stats);
    }

    private IReadOnlyList<byte[]> PassivesChanged()
    {
        _onPassives?.Invoke([.. _passives.Select(p => Poe2Passives.Encode(p.Key, p.Value))]);
        return [PassivesMessage(), .. PassiveStatsMessages()];
    }

    // C→S 0x3c: the client checks the node's connection itself; the server checks the points, keeps the node (with the
    // attribute chosen) and sends the tree again (the client undoes the click when the tree comes back without it).
    private IReadOnlyList<byte[]> AllocatePassive(InstancePassivesPoe2.Allocate allocate)
    {
        // A node the server has but the client asks for again (it dropped it as unconnected, live 2026-11-04) costs no
        // point: the allocation is sent back as it is, with the choice the client makes now.
        bool known = _passives.ContainsKey(allocate.Node);
        if (!known && PointsLeft() <= 0) return [PassivesMessage()];
        _passives[allocate.Node] = allocate.Choice;
        return PassivesChanged();
    }

    // C→S 0x3e: a refund costs the level's gold per node (the client checks the same before it asks, 0x1409ad790);
    // short of gold, the tree and the gold go back unchanged and the client undoes the refund.
    private IReadOnlyList<byte[]> RefundPassive(ushort node) =>
        _passives.ContainsKey(node) ? RefundPassives([node]) : [PassivesMessage()];

    // C→S 0x48: every node at once, at the same price each.
    private IReadOnlyList<byte[]> RefundAllPassives() =>
        _passives.Count == 0 ? [PassivesMessage()] : RefundPassives([.. _passives.Keys]);

    private IReadOnlyList<byte[]> RefundPassives(IReadOnlyList<ushort> nodes)
    {
        long cost = (long)_passiveNodes.RefundCost(_level) * nodes.Count;
        if (cost > _gold) return [PassivesMessage(), GoldMessage()];
        _gold -= (uint)cost;
        foreach (ushort node in nodes) _passives.Remove(node);
        return [.. PassivesChanged(), GoldMessage()];
    }
}
