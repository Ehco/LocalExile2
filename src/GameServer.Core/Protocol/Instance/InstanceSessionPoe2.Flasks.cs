using GameServer.Core.Accounts;
using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>The character's flasks (PORTING-POE2.md, wave 32): the flask bar's inventory, a use that spends charges and
/// recovers life or mana over the flask's time, the charges kills give back, and the mana pool the server keeps for it.</summary>
public sealed partial class InstanceSessionPoe2
{
    private readonly Poe2Inventory _flasks = new(InstanceFlasksPoe2.FlaskInventoryId,
        InstanceInventoriesPoe2.PlayerDefaults[InstanceFlasksPoe2.FlaskRow].Width, InstanceInventoriesPoe2.PlayerDefaults[InstanceFlasksPoe2.FlaskRow].Height);
    private readonly List<byte[]> _flaskChanges = [];
    private int _playerMana;

    // A recovery in progress: Amount into Pool over Seconds from Start, Applied so far.
    private sealed class Recovery(byte pool, int amount, double start, double seconds)
    {
        public byte Pool = pool;
        public int Amount = amount, Applied;
        public double Start = start, Seconds = Math.Max(0.1, seconds);
    }

    private readonly List<Recovery> _recoveries = [];

    /// <summary>The flasks of the flask bar, as items (their charges in <see cref="Item.Charges"/>).</summary>
    public IReadOnlyList<Poe2Inventory.Entry> Flasks { get { lock (_lock) return [.. _flasks.Items]; } }

    /// <summary>The player's current mana (the server's: flasks refill it; skills do not spend it yet).</summary>
    public int PlayerMana { get { lock (_lock) return _playerMana; } }

    // The two flasks a new PoE2 character has (Lesser Life Flask, Lesser Mana Flask) for a character whose save holds no
    // flask (0xde on entering, after the inventory). Called after InitItems (the saved flasks) and InitExperience (the
    // mana maximum).
    private void InitFlasks(bool startingFlasks)
    {
        _playerMana = _maximumMana;
        if (!startingFlasks || _flasks.Items.Count > 0) return;
        foreach (var (id, x) in new[] { (Poe2Items.LesserLifeFlask, (byte)0), (Poe2Items.LesserManaFlask, (byte)1) })
            if (_itemTypes.GetValueOrDefault(id) is { Flask: not null } type
                && _flasks.Restore(Poe2Items.CreateFlask(type), type.Width, type.Height, x, 0) is { } change)
                _flaskChanges.Add(change);
    }

    private IEnumerable<byte[]> FlaskMessages() => _flaskChanges;

    // C→S 0x70: the flask spends a use's charges (0xde with the flask again) and its recovery starts; a flask without
    // the charges, a dead player or an unknown item change nothing.
    private IReadOnlyList<byte[]> UseFlask(InstanceFlasksPoe2.UseFlask use, double now)
    {
        if (use.InventoryId != _flasks.InventoryId || _flasks.Find(use.ItemId) is not { } entry || _playerLife <= 0) return [];
        if (_itemTypesByHash.GetValueOrDefault(entry.Item.TypeHash)?.Flask is not { } flask || entry.Item.Charges < flask.PerCharge) return [];
        if (_flasks.Replace(entry.Id, entry.Item with { Charges = entry.Item.Charges - flask.PerCharge }) is not { } change) return [];
        double seconds = flask.RecoveryTenths / 10.0;
        if (flask.LifePerUse > 0) _recoveries.Add(new(InstanceCombatPoe2.LifePool, flask.LifePerUse, now, seconds));
        if (flask.ManaPerUse > 0) _recoveries.Add(new(InstanceCombatPoe2.ManaPool, flask.ManaPerUse, now, seconds));
        return [change];
    }

    // The recoveries' due amounts since the last tick go into the pools (capped at the maximums): a life update (0x1cf)
    // per pool that moved.
    private IEnumerable<byte[]> FlaskTick(double now)
    {
        if (_recoveries.Count == 0) yield break;
        bool life = false, mana = false;
        foreach (var r in _recoveries)
        {
            int due = (int)Math.Min(r.Amount, Math.Max(0, r.Amount * (now - r.Start) / r.Seconds));
            int give = due - r.Applied;
            if (give <= 0) continue;
            r.Applied = due;
            if (r.Pool == InstanceCombatPoe2.LifePool && _playerLife > 0 && _playerLife < _maximumLife)
                (_playerLife, life) = (Math.Min(_maximumLife, _playerLife + give), true);
            else if (r.Pool == InstanceCombatPoe2.ManaPool && _playerMana < _maximumMana)
                (_playerMana, mana) = (Math.Min(_maximumMana, _playerMana + give), true);
        }
        _recoveries.RemoveAll(r => r.Applied >= r.Amount || _playerLife <= 0);
        if (life) yield return InstanceCombatPoe2.LifeUpdate(PlayerId, _playerLife);
        if (mana) yield return InstanceCombatPoe2.LifeUpdate(PlayerId, _playerMana, InstanceCombatPoe2.ManaPool);
    }

    // A kill gives every flask ChargesPerKill charges up to its maximum (0xde per flask that gained).
    private IEnumerable<byte[]> FlaskChargesOnKill()
    {
        foreach (var entry in _flasks.Items.ToList())
        {
            if (_itemTypesByHash.GetValueOrDefault(entry.Item.TypeHash)?.Flask is not { } flask || entry.Item.Charges >= flask.MaxCharges) continue;
            int charges = Math.Min(flask.MaxCharges, entry.Item.Charges + InstanceFlasksPoe2.ChargesPerKill);
            if (_flasks.Replace(entry.Id, entry.Item with { Charges = charges }) is { } change) yield return change;
        }
    }

    // Resurrecting refills the flasks and the mana, as a town visit does.
    private IEnumerable<byte[]> RefillFlasks()
    {
        _playerMana = _maximumMana;
        _recoveries.Clear();
        foreach (var entry in _flasks.Items.ToList())
        {
            if (_itemTypesByHash.GetValueOrDefault(entry.Item.TypeHash)?.Flask is not { } flask || entry.Item.Charges >= flask.MaxCharges) continue;
            if (_flasks.Replace(entry.Id, entry.Item with { Charges = flask.MaxCharges }) is { } change) yield return change;
        }
    }
}
