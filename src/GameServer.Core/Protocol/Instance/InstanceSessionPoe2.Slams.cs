using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Attacks at the ground (PORTING-POE2.md, wave 30 follow-up): a skill used without a target object (C→S 0x1a5 with
/// a cell relative to the player) hits every monster in its area. A gem's skill takes its level's
/// <see cref="Poe2Gems.Combat"/> values: Rolling Slam adds 1000 ms to the attack, slams once at 75 % of the damage in
/// front of the player, rolls and slams again at 150 % further on. The client plays the action from the 0x1c8 echo and
/// only the server's 0x1cf hits count; without them the slam was animated but harmless (live 2026-11-05).
/// The geometry and timing are ours: hit k of n lands at (k + 1) / (n + 1) of the attack time; the first hit's centre
/// is the weapon's reach towards the aim, the later ones' the reach plus the skill's added action distance plus
/// <see cref="RollCells"/>; radii get <see cref="AreaSlack"/> for the estimates' error.
/// </summary>
public sealed partial class InstanceSessionPoe2
{
    /// <summary>PoE's world units per grid cell (a tile is 23 cells and 250 units).</summary>
    public const double UnitsPerCell = 250.0 / 23;

    /// <summary>Cells added to an area's radius for the error of the walk and monster estimates (ours).</summary>
    public const double AreaSlack = ReachSlack;

    /// <summary>How far a skill with several hits carries the player before its last one (Rolling Slam's roll), in
    /// cells: our guess, the data has no roll distance.</summary>
    public const double RollCells = 3;

    // A hit of an attack at the ground: it lands at At on the monsters within Radius cells of Centre, rolling damage
    // in [Minimum, Maximum]; Carry, when set, is where the action has taken the player by then.
    private sealed record PendingAreaHit(double At, (int X, int Y) Centre, double Radius, int Minimum, int Maximum, (int X, int Y)? Carry);

    private readonly List<PendingAreaHit> _areaHits = [];

    // The level's combat values of the gem behind a skill id (FirstGemSkillId + slot), null for the other skills.
    private Poe2Gems.Combat? GemCombat(uint skillId)
    {
        uint slot = skillId - InstanceGemsPoe2.FirstGemSkillId;
        return skillId >= InstanceGemsPoe2.FirstGemSkillId && slot < InstanceGemsPoe2.SkillSlotCount && _gems.TryGetValue((byte)slot, out var g)
            ? g.Gem.At(g.Level)?.Combat
            : null;
    }

    // Queues the hits of an attack used at the ground from the player's cell towards aim (relative to it). The
    // action starts once the previous one has ended. Later re-aims (C→S 0x1a8) are not followed: the hits stay where
    // the use aimed.
    private void BeginAreaAttack(uint skillId, AttackPoe2 attack, (int X, int Y) from, (int X, int Y) aim, double now)
    {
        var combat = GemCombat(skillId);
        IReadOnlyList<Poe2Gems.Hit> hits = combat?.Hits is { Count: > 0 } h ? h : [new Poe2Gems.Hit(0, 0)];
        double seconds = (attack.AttackMs + (combat?.AddedAttackMs ?? 0)) / 1000.0, start = Math.Max(now, _attackFreeAt);
        double weapon = attack.Range / UnitsPerCell;
        double far = (attack.Range + (combat?.AddedReach ?? 0)) / UnitsPerCell + (hits.Count > 1 ? RollCells : 0);
        for (int k = 0; k < hits.Count; k++)
        {
            double scale = (10_000 + hits[k].Multiplier) / 10_000.0, reach = k == 0 ? weapon : far;
            bool last = k == hits.Count - 1 && hits.Count > 1;
            _areaHits.Add(new(start + seconds * (k + 1) / (hits.Count + 1), Toward(from, aim, reach),
                hits[k].Radius / UnitsPerCell + AreaSlack,
                (int)Math.Round(attack.MinimumDamage * scale, MidpointRounding.AwayFromZero),
                (int)Math.Round(attack.MaximumDamage * scale, MidpointRounding.AwayFromZero),
                last ? Toward(from, aim, Math.Max(0, far - weapon)) : null));
        }
        _attackFreeAt = start + seconds;
    }

    // The cell at most `reach` cells from `from` towards `aim` (relative), `from` itself without an aim.
    private static (int X, int Y) Toward((int X, int Y) from, (int X, int Y) aim, double reach)
    {
        double length = Math.Sqrt((double)aim.X * aim.X + (double)aim.Y * aim.Y);
        if (length == 0) return from;
        double t = Math.Min(length, reach) / length;
        return ((int)Math.Round(from.X + aim.X * t), (int)Math.Round(from.Y + aim.Y * t));
    }

    // The area hits that have landed by now: each monster alive within the radius takes a damage roll (0x1cf, and at
    // 0 its death, experience and drop) and turns on the player.
    private IEnumerable<byte[]> AreaHitTick(double now)
    {
        var landed = _areaHits.Where(a => a.At <= now).ToList();
        if (landed.Count == 0) yield break;
        _areaHits.RemoveAll(a => a.At <= now);
        foreach (var hit in landed)
        {
            if (hit.Carry is { } carry) Walk.Place(carry.X, carry.Y, now);
            for (int i = 0; i < _monsters.Count; i++)
            {
                if (_monsterLife[i] <= 0 || Distance(MonsterCell(i, now), hit.Centre) > hit.Radius) continue;
                Provoke(i);
                foreach (var m in DamageMonster(i, _random.Next(hit.Minimum, Math.Max(hit.Minimum, hit.Maximum) + 1), now)) yield return m;
            }
        }
    }
}
