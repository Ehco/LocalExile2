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
    public sealed record Variety(string Id, ushort Row, string[] Components, string[]? SharedStates = null);

    public const string DefaultPath = "data/poe2/monster-varieties.json";

    /// <summary>The varieties by Id, or an empty table without the file.</summary>
    public static IReadOnlyDictionary<string, Variety> Load(string path = DefaultPath)
    {
        if (!File.Exists(path)) return new Dictionary<string, Variety>();
        var rows = JsonSerializer.Deserialize<Variety[]>(File.ReadAllText(path), JsonOptions) ?? [];
        return rows.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The captured objects that are monster varieties the server can write, at <paramref name="level"/>;
    /// one note per monster left out.</summary>
    public static IReadOnlyList<Poe2Monster> InArea(IReadOnlyList<Poe2Areas.PlacedObject> objects,
        IReadOnlyDictionary<string, Variety> varieties, byte level, uint life, List<string> notes)
    {
        var monsters = new List<Poe2Monster>();
        foreach (var o in objects.Where(o => o.Type.StartsWith("Metadata/Monsters/", StringComparison.OrdinalIgnoreCase)))
        {
            if (!varieties.TryGetValue(o.Type, out var v)) continue;    // not a variety: a marker or a controller
            if (!MonsterObjectPoe2.CanSerialize(v.Components))
            {
                notes.Add($"{v.Id} at ({o.X}, {o.Y}): components {string.Join(", ", v.Components)} not written");
                continue;
            }
            monsters.Add(new(v.Id, v.Row, level, new MonsterObjectPoe2(o.X, o.Y, life, v.Components, o.Rotation, o.Model,
                SharedStates: v.SharedStates)));
        }
        return monsters;
    }
}
