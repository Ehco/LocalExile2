using GameServer.Core.Accounts;
using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

// Experience from kills and the level it reaches (PORTING-POE2.md, wave 24).
public sealed partial class InstanceSessionPoe2
{
    private IReadOnlyList<uint> _levelStarts = [];
    private Action<int>? _onLevel;
    private Action<uint, uint>? _onProgress;
    private uint _experience;
    private int _level;
    private int _maximumLife;
    private int _maximumMana;

    /// <summary>The player's total experience (the server's).</summary>
    public uint Experience { get { lock (_lock) return _experience; } }

    /// <summary>The player's level from <see cref="Experience"/> (1 without the experience table).</summary>
    public int Level { get { lock (_lock) return _level; } }

    private void InitExperience(IReadOnlyList<uint>? levelStarts, Action<int>? onLevel)
    {
        (_levelStarts, _onLevel, _experience) = (levelStarts ?? [], onLevel, _player.Experience);
        _level = Progression.LevelFor(_experience, _levelStarts);
        (_maximumLife, _maximumMana) = ((int)_player.Life, (int)_player.Mana);
    }

    // The experience and the gold go to the save together whenever either moves (wave 29).
    private void ProgressChanged() => _onProgress?.Invoke(_experience, _gold);

    // A kill's experience, as PoE1's server: DefaultMonsterStats' Experience at the monster's level × the variety's
    // ExperienceMultiplier, with the level penalty (S→C 0x2f9 on the player). A new level refills life and mana at the
    // class's new maximums.
    private IEnumerable<byte[]> GainExperience(int monster)
    {
        var m = _monsters[monster];
        if (m.LevelStats is not { } stats || _playerLife <= 0) yield break;
        uint gain = Progression.MonsterExperience(stats.Experience, m.Variety?.ExperienceMultiplier ?? 100,
            MonsterRarity.Normal, _level, m.Level);
        uint after = Progression.AddExperience(_experience, gain, _levelStarts);
        if (after == _experience) yield break;
        _experience = after;
        yield return InstanceProgressionPoe2.PlayerExperience(PlayerId, _experience);
        int level = Progression.LevelFor(_experience, _levelStarts);
        if (level <= _level) { ProgressChanged(); yield break; }
        _level = level;
        var sheet = CurrentSheet();
        (_maximumLife, _maximumMana, _playerLife) = (sheet.MaximumLife, sheet.MaximumMana, sheet.MaximumLife);
        yield return InstanceCombatPoe2.StatsUpdate(PlayerId, sheet.Stats);
        yield return InstanceCombatPoe2.LifeUpdate(PlayerId, _maximumLife);
        yield return InstanceCombatPoe2.LifeUpdate(PlayerId, _maximumMana, InstanceCombatPoe2.ManaPool);
        yield return PassivesMessage();
        _onLevel?.Invoke(level);
        ProgressChanged();
    }
}
