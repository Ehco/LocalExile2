using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// PoE2's skill gems and uncut gems (PORTING-POE2.md, wave 30), from data/poe2/gems.json (scripts/re/poe2/gems.py): a
/// gem's base item, colour, kind, level requirement, its granted effect and, per gem level, the GrantedEffectsPerLevel
/// row and the GrantedEffectStatSetsPerLevel rows the Actor's skill record refers to. An uncut gem is cut into a skill
/// gem of its tier's level by the gem window.
/// </summary>
public sealed class Poe2Gems
{
    public sealed record Effect(int Row, string Id, uint Hash, string? ActiveSkill, string? SkillName);

    /// <summary>One gem level: the GEPL row and the GESSPL rows (the effect's StatSet first, then its AdditionalStatSets).</summary>
    public sealed record Level(int GemLevel, uint EffectsPerLevelRow, IReadOnlyList<uint> StatSetRows);

    public sealed record Gem(string Id, int Row, string Name, uint Hash, int SkillGemsRow, int GemEffectsRow, byte Colour,
        string Kind, int MinLevel, int Strength, int Dexterity, int Intelligence, Effect Effect, IReadOnlyList<Level> Levels)
    {
        /// <summary>The gem's rows at a level: the highest level the table has up to it (the first one below the table).</summary>
        public Level? At(int level) => Levels.LastOrDefault(l => l.GemLevel <= level) ?? Levels.FirstOrDefault();
    }

    public sealed record Uncut(string Id, int Row, string Name, uint Hash, int Tier, string Kind);

    public const string DefaultPath = "data/poe2/gems.json";
    public const string SkillKind = "skill", SupportKind = "support", SpiritKind = "spirit";

    readonly IReadOnlyDictionary<int, Gem> _byRow;
    readonly IReadOnlyDictionary<uint, Gem> _byHash;
    readonly IReadOnlyList<Uncut> _uncut;

    public Poe2Gems(IReadOnlyList<Gem> gems, IReadOnlyList<Uncut> uncut)
    {
        _byRow = gems.GroupBy(g => g.Row).ToDictionary(g => g.Key, g => g.First());
        _byHash = gems.GroupBy(g => g.Hash).ToDictionary(g => g.Key, g => g.First());
        _uncut = uncut;
    }

    public static readonly Poe2Gems Empty = new([], []);

    public int Count => _byRow.Count;
    public IEnumerable<Gem> Gems => _byRow.Values;
    public IReadOnlyList<Uncut> UncutGems => _uncut;

    public Gem? FindRow(int row) => _byRow.GetValueOrDefault(row);
    public Gem? FindHash(uint hash) => _byHash.GetValueOrDefault(hash);
    public Uncut? UncutByHash(uint hash) => _uncut.FirstOrDefault(u => u.Hash == hash);

    /// <summary>The uncut gem of a kind and tier (the tiered "Uncut Skill Gem (Level n)" items), or null.</summary>
    public Uncut? UncutFor(string kind, int tier) => _uncut.FirstOrDefault(u => u.Kind == kind && u.Tier == tier);

    /// <summary>
    /// The gem the gem window's cut request names. Which key the client sends is not verified live (the builder
    /// 0x140a4ca20 sends the selected entry's +0x8c): the BaseItemTypes row is tried first, then the item HASH32, the
    /// SkillGems row and the GemEffects row. Null when none names a gem.
    /// </summary>
    public Gem? Resolve(uint key) =>
        FindRow((int)key) ?? FindHash(key)
        ?? _byRow.Values.FirstOrDefault(g => g.SkillGemsRow == key) ?? _byRow.Values.FirstOrDefault(g => g.GemEffectsRow == key);

    /// <summary>A socketed gem as the character record keeps it: the BaseItemTypes row in the low 16 bits, the gem
    /// level in the next 8, the skill slot in the top 7.</summary>
    public static int Encode(int row, int level, int slot) => (row & 0xffff) | (level & 0xff) << 16 | (slot & 0x7f) << 24;

    public static (int Row, int Level, int Slot) Decode(int value) => (value & 0xffff, (value >> 16) & 0xff, (value >> 24) & 0x7f);

    /// <summary>The tables of <paramref name="path"/>, or <see cref="Empty"/> without the file.</summary>
    public static Poe2Gems Load(string path = DefaultPath)
    {
        if (!File.Exists(path)) return Empty;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gems = new List<Gem>();
        foreach (var e in doc.RootElement.GetProperty("skills").EnumerateArray())
        {
            var fx = e.GetProperty("effect");
            var levels = e.GetProperty("levels").EnumerateArray()
                .Select(l => new Level(l[0].GetInt32(), l[1].GetUInt32(), [.. l[2].EnumerateArray().Select(r => r.GetUInt32())])).ToList();
            gems.Add(new Gem(Str(e, "id"), e.GetProperty("row").GetInt32(), Str(e, "name"), e.GetProperty("hash").GetUInt32(),
                Int(e, "skillGemsRow"), Int(e, "gemEffectsRow"), (byte)Int(e, "colour"), Str(e, "kind"), Int(e, "minLevel"),
                Int(e, "strength"), Int(e, "dexterity"), Int(e, "intelligence"),
                new Effect(Int(fx, "row"), Str(fx, "id"), fx.GetProperty("hash").GetUInt32(), NullableStr(fx, "activeSkill"), NullableStr(fx, "skillName")),
                levels));
        }
        var uncut = doc.RootElement.GetProperty("uncut").EnumerateArray()
            .Select(u => new Uncut(Str(u, "id"), Int(u, "row"), Str(u, "name"), u.GetProperty("hash").GetUInt32(), Int(u, "tier"), Str(u, "kind"))).ToList();
        return new(gems, uncut);
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static string? NullableStr(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
