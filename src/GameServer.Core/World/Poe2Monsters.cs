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
    /// the attack time in ms and the distances are in cells (wave 22).</param>
    public sealed record Variety(string Id, ushort Row, string[] Components, string[]? SharedStates = null,
        string? Name = null, int LifeMultiplier = 100, int DamageMultiplier = 100, int ExperienceMultiplier = 100,
        int AttackSpeed = 0, int MinimumAttackDistance = 0, int MaximumAttackDistance = 0, int MovementSpeed = 0,
        int ObjectSize = 0);

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
            monsters.Add(new(v.Id, v.Row, level, new MonsterObjectPoe2(o.X, o.Y, Life(v, stats), v.Components, o.Rotation,
                o.Model, SharedStates: v.SharedStates), v, stats));
        }
        return monsters;
    }
}
