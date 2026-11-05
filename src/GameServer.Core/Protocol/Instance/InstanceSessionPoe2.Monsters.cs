namespace GameServer.Core.Protocol.Instance;

/// <summary>The area's monsters fighting back (PORTING-POE2.md, wave 23): they chase the player within their aggro range
/// and attack in reach; their hits take the player's life, and at 0 the player dies.</summary>
public sealed partial class InstanceSessionPoe2
{
    /// <summary>The 0x1c8 flags of the server's monster actions (PoE1's 0x0408). A chase is not one: PoE1's Move action
    /// (0x1c8 with skill 0x2909) left PoE2's monsters standing (live 2026-11-04), so they glide with MoveTo (0x1e0).</summary>
    public const ushort MonsterActionFlags = 0x0408;

    /// <summary>How often a monster decides (seconds), and the cells its target must move before it re-orders a chase.</summary>
    public const double DecisionSeconds = 0.25;
    public const int RechaseCells = 3;

    /// <summary>Cells added to a monster's attack distance for its reach (the bodies' sizes), and to that again before a
    /// swing in progress misses a player who walked off.</summary>
    public const int ReachSlack = 2, DodgeSlack = 3;

    // Each monster's estimate (walking From → To between Start and Arrive) and fight state.
    private sealed class MonsterState
    {
        public (int X, int Y) From, To;
        public double Start, Arrive;
        public bool Aggro;
        public double NextDecision, FreeAt;
        public double? Hit;
    }

    /// <summary>Seconds after the player enters the area before the monsters act (the client is still loading).</summary>
    public const double ArrivalSeconds = 2;

    private MonsterState[] _monsterStates = [];
    private int _playerLife;
    private double? _pausedAt;

    /// <summary>The player's current life (the server's).</summary>
    public int PlayerLife { get { lock (_lock) return _playerLife; } }

    private void InitMonsters()
    {
        _playerLife = _maximumLife;
        _monsterStates = [.. _monsters.Select(m => new MonsterState
        {
            From = (m.Object.GridX, m.Object.GridY), To = (m.Object.GridX, m.Object.GridY),
        })];
    }

    // Monsters of a pack start deciding a little apart, so that they do not all swing on the same frame.
    private static double Stagger(int i) => i % 4 * DecisionSeconds;

    private void ArriveMonsters(double now)
    {
        for (int i = 0; i < _monsterStates.Length; i++) _monsterStates[i].NextDecision = now + ArrivalSeconds + Stagger(i);
    }

    // While the client has the game paused (loading, the Esc menu) the monsters wait; on resume their timers move on by
    // the pause.
    private void PauseMonsters(bool paused, double now)
    {
        if (paused) { _pausedAt ??= now; return; }
        if (_pausedAt is not double at) return;
        _pausedAt = null;
        double d = now - at;
        foreach (var s in _monsterStates)
        {
            (s.Start, s.Arrive, s.NextDecision, s.FreeAt) = (s.Start + d, s.Arrive + d, s.NextDecision + d, s.FreeAt + d);
            if (s.Hit is double hit) s.Hit = hit + d;
        }
    }

    /// <summary>Where the server estimates the i-th monster is at <paramref name="now"/>.</summary>
    public (int X, int Y) MonsterCell(int i, double now)
    {
        var s = _monsterStates[i];
        if (now >= s.Arrive || s.Arrive <= s.Start) return s.To;
        double t = (now - s.Start) / (s.Arrive - s.Start);
        return ((int)Math.Round(s.From.X + (s.To.X - s.From.X) * t), (int)Math.Round(s.From.Y + (s.To.Y - s.From.Y) * t));
    }

    private static double Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    private int MonsterReach(int i) => (_monsters[i].Attack?.Reach ?? 1) + ReachSlack;

    /// <summary>A hit on the monster wakes it up (whatever its aggro range).</summary>
    private void Provoke(int i) => _monsterStates[i].Aggro = true;

    private IEnumerable<byte[]> MonsterTick(double now)
    {
        if (_pausedAt is not null) yield break;
        var player = Walk.Cell(now);
        for (int i = 0; i < _monsters.Count; i++)
        {
            if (_monsterLife[i] <= 0 || _monsters[i] is not { Attack: { } attack, Variety: { } variety }) continue;
            var s = _monsterStates[i];
            if (s.Hit is double hitAt && now >= hitAt)
            {
                s.Hit = null;
                foreach (var m in MonsterHit(i, attack, player)) yield return m;
            }
            if (now < s.NextDecision || _playerLife <= 0) continue;
            s.NextDecision = now + DecisionSeconds;
            var at = MonsterCell(i, now);
            double d = Distance(at, player);
            if (!s.Aggro && d > variety.MaxAgroRange) continue;
            s.Aggro = true;
            if (now < s.FreeAt) continue;                       // mid-swing
            if (d <= MonsterReach(i))
            {
                (s.From, s.To, s.Start, s.Arrive) = (at, at, now, now);
                s.Hit = now + attack.AttackMs / 1000.0 * ContactPoint;
                s.FreeAt = s.NextDecision = now + attack.AttackMs / 1000.0;
                yield return InstanceSkillsPoe2.ActionStarted(MonsterId(i), at.X, at.Y, player.X, player.Y,
                    ActorSkillsPoe2.FirstSkillId, MonsterActionFlags, PlayerId);
                continue;
            }
            // Chase to the edge of its reach on the line to the player, re-ordered once the player has moved on.
            double stop = (d - MonsterReach(i) + 1) / d;
            (int X, int Y) target = ((int)Math.Round(at.X + (player.X - at.X) * stop), (int)Math.Round(at.Y + (player.Y - at.Y) * stop));
            bool walking = now < s.Arrive;
            if (walking && Distance(target, s.To) < RechaseCells) continue;
            double speed = Math.Max(1, variety.MovementSpeed);
            (s.From, s.To, s.Start, s.Arrive) = (at, target, now, now + Distance(at, target) / speed);
            yield return InstanceMovementPoe2.MoveTo(MonsterId(i), target.X, target.Y, (float)(s.Arrive - now));
        }
    }

    // Resurrect at Checkpoint, as PoE1's server: the death screen hidden, back to full life where the player entered the
    // area (life from 0 runs the client's revive, then the teleport), and the monsters calm down and heal (PoE keeps the
    // instance).
    private IReadOnlyList<byte[]> Resurrect(double now)
    {
        _playerLife = _maximumLife;
        Walk.Place(_player.GridX, _player.GridY, now);
        var sent = new List<byte[]>
        {
            InstanceCombatPoe2.DeathScreen(InstanceCombatPoe2.HideDeathScreen),
            InstanceCombatPoe2.LifeUpdate(PlayerId, _playerLife),
            InstanceMovementPoe2.Teleport(PlayerId, _player.GridX, _player.GridY),
        };
        sent.AddRange(RefillFlasks());
        for (int i = 0; i < _monsters.Count; i++)
        {
            var s = _monsterStates[i];
            var at = MonsterCell(i, now);
            (s.From, s.To, s.Start, s.Arrive, s.Aggro, s.Hit, s.FreeAt) = (at, at, now, now, false, null, now);
            s.NextDecision = now + ArrivalSeconds + Stagger(i);
            int full = (int)_monsters[i].Object.Life;
            if (_monsterLife[i] <= 0 || _monsterLife[i] == full) continue;
            _monsterLife[i] = full;
            sent.Add(InstanceCombatPoe2.LifeUpdate(MonsterId(i), full));
        }
        return sent;
    }

    // The swing lands if the player is still in reach: the damage roll off the player's life (S→C 0x1cf on the player).
    private IEnumerable<byte[]> MonsterHit(int i, World.Poe2Monsters.Attack attack, (int X, int Y) player)
    {
        if (_playerLife <= 0 || Distance(MonsterCell(i, _monsterStates[i].Start), player) > MonsterReach(i) + DodgeSlack) yield break;
        _playerLife = Math.Max(0, _playerLife - _random.Next(attack.MinimumDamage, attack.MaximumDamage + 1));
        yield return InstanceCombatPoe2.LifeUpdate(PlayerId, _playerLife);
        if (_playerLife > 0) yield break;
        _attack = null;
        yield return InstanceCombatPoe2.DeathScreen();
    }
}
