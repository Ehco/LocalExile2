using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.World;

/// <summary>
/// The PoE2 monster varieties the server can create (PORTING-POE2.md, wave 20): MonsterVarieties rows with their .ot
/// components, from data/poe2/monster-varieties.json, and the monsters an area's capture places.
/// </summary>
public static class Poe2Monsters
{
    /// <param name="Components">The variety's .ot components in deserialize order.</param>
    /// <param name="SharedStates">Its StateMachine's shared states, in .ot order.</param>
    /// <param name="LifeMultiplier">MonsterVarieties' multipliers of the level's base values, in percent; AttackSpeed is
    /// the attack time in ms, the distances and MaxAgroRange are in cells and MovementSpeed in cells per second, as PoE1's
    /// server reads them (wave 22).</param>
    /// <param name="DamageSpread">Its MonsterTypes DamageSpread, in percent either side.</param>
    /// <param name="AttackEffect">Its first GrantedEffects row (its attack), and that effect's rows by level:
    /// [level, GrantedEffectsPerLevel row, GrantedEffectStatSetsPerLevel row] (wave 23).</param>
    public sealed record Variety(string Id, ushort Row, string[] Components, string[]? SharedStates = null,
        string? Name = null, int LifeMultiplier = 100, int DamageMultiplier = 100, int ExperienceMultiplier = 100,
        int AttackSpeed = 0, int MinimumAttackDistance = 0, int MaximumAttackDistance = 0, int MovementSpeed = 0,
        int ObjectSize = 0, int MaxAgroRange = 0, int DamageSpread = 20, int AttackEffect = -1, int[][]? AttackLevels = null);

    /// <summary>A monster's attack: its damage per hit, attack time, reach (cells) and the skill rows its Actor gets.</summary>
    public sealed record Attack(int MinimumDamage, int MaximumDamage, int AttackMs, int Reach, uint EffectsPerLevelRow,
        uint StatSetsPerLevelRow);

    /// <summary>A playability scale on monster hit damage (percent), not a rule from the game: the client's own
    /// formula is not known yet (see <see cref="AttackOf"/>). Env Game_POE2_MONSTER_DAMAGE overrides it.</summary>
    public static int DamageScalePercent { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("Game_POE2_MONSTER_DAMAGE"), out int percent) && percent > 0 ? percent : 50;

    /// <summary>The attack time of varieties without one.</summary>
    public const int DefaultAttackMs = 1500;

    /// <summary>
    /// A variety's attack at a level, or null without an attack effect or level stats: the level's Damage ×
    /// DamageMultiplier, spread ± DamageSpread. PoE1's server (and Path of Building) also multiply by the attack time in
    /// seconds; for PoE2 that gave a level 2 Rotting Guard 25–37 per hit against a 58-life Warrior, so it is left out
    /// until PoE2's rule is known. Without it the three Rotting Guards by Clearfell's camp still killed the Warrior in
    /// four seconds (10–15 each every 2.5 s, live 2026-11-04), so <see cref="DamageScalePercent"/> scales it too.
    /// The skill rows are those of the highest level not above the monster's.
    /// </summary>
    public static Attack? AttackOf(Variety v, LevelStats? level)
    {
        if (level is null || v.AttackLevels is not { Length: > 0 } rows) return null;
        var row = rows.Where(r => r[0] <= level.Level).DefaultIfEmpty(rows[0]).MaxBy(r => r[0])!;
        double damage = level.Damage * v.DamageMultiplier / 100.0 * DamageScalePercent / 100.0, spread = v.DamageSpread / 100.0;
        int min = Math.Max(1, (int)Math.Round(damage * (1 - spread))), max = Math.Max(min, (int)Math.Round(damage * (1 + spread)));
        return new(min, max, v.AttackSpeed > 0 ? v.AttackSpeed : DefaultAttackMs, Math.Max(1, v.MaximumAttackDistance),
            (uint)row[1], (uint)Math.Max(0, row[2]));
    }

    /// <summary>DefaultMonsterStats at a level: the base values a variety's multipliers scale.</summary>
    public sealed record LevelStats(int Level, int Life, double Damage, int Experience, int Accuracy, int Evasion, int Armour);

    public const string DefaultPath = "data/poe2/monster-varieties.json", DefaultLevelsPath = "data/poe2/default-monster-stats.json";

    /// <summary>The life of monsters without level stats (no data/poe2/default-monster-stats.json).</summary>
    public const uint FallbackLife = 30;

    /// <summary>DefaultMonsterStats by level (index = level - 1), or empty without the file.</summary>
    public static IReadOnlyList<LevelStats> LoadLevels(string path = DefaultLevelsPath) =>
        File.Exists(path) ? JsonSerializer.Deserialize<LevelStats[]>(File.ReadAllText(path), JsonOptions) ?? [] : [];

    /// <summary>A variety's life at a level: the level's base life × LifeMultiplier, at least 1.</summary>
    public static uint Life(Variety v, LevelStats? level) =>
        level is null ? FallbackLife : (uint)Math.Max(1, (int)Math.Round(level.Life * v.LifeMultiplier / 100.0));

    /// <summary>The varieties by Id, or an empty table without the file.</summary>
    public static IReadOnlyDictionary<string, Variety> Load(string path = DefaultPath)
    {
        if (!File.Exists(path)) return new Dictionary<string, Variety>();
        var rows = JsonSerializer.Deserialize<Variety[]>(File.ReadAllText(path), JsonOptions) ?? [];
        return rows.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The captured objects that are monster varieties the server can write, at <paramref name="level"/>, with
    /// their life from <paramref name="levels"/>; one note per monster left out.</summary>
    public static IReadOnlyList<Poe2Monster> InArea(IReadOnlyList<Poe2Areas.PlacedObject> objects,
        IReadOnlyDictionary<string, Variety> varieties, byte level, IReadOnlyList<LevelStats> levels, List<string> notes)
    {
        var stats = level >= 1 && level <= levels.Count ? levels[level - 1] : null;
        var monsters = new List<Poe2Monster>();
        foreach (var o in objects.Where(o => o.Type.StartsWith("Metadata/Monsters/", StringComparison.OrdinalIgnoreCase)))
        {
            if (!varieties.TryGetValue(o.Type, out var v)) continue;    // not a variety: a marker or a controller
            if (!MonsterObjectPoe2.CanSerialize(v.Components))
            {
                notes.Add($"{v.Id} at ({o.X}, {o.Y}): components {string.Join(", ", v.Components)} not written");
                continue;
            }
            var attack = AttackOf(v, stats);
            IReadOnlyList<GrantedSkillPoe2>? skills = attack is null ? null
                : [new(ActorSkillsPoe2.FirstSkillId, attack.EffectsPerLevelRow, attack.StatSetsPerLevelRow)];
            monsters.Add(new(v.Id, v.Row, level, new MonsterObjectPoe2(o.X, o.Y, Life(v, stats), v.Components, o.Rotation,
                o.Model, SharedStates: v.SharedStates, Skills: skills), v, stats, attack));
        }
        return monsters;
    }
}
