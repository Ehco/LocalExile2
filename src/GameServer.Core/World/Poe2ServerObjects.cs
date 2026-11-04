using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.World;

/// <summary>
/// The PoE2 server objects other than exits and monsters (PORTING-POE2.md, wave 27): NPCs, the waypoint, the stash and
/// the like, from data/poe2/server-objects.json (scripts/re/poe2/server_objects.py), and the ones an area's capture
/// places. An NPC's shared states follow the player's quest flags: each type's per_player_quest_flag_state_&lt;state&gt;_&lt;value&gt;
/// rules set the state to the value when the player has the flag, else it keeps its .ot default. The town places every
/// variant of an NPC (Renly, RenlyIntro, RenlyAfterIronCount, ...); their "visible" states show the one for the
/// player's progress (the client's on_or_create_state_visible_* scripts hide the others).
/// </summary>
public static class Poe2ServerObjects
{
    /// <param name="Components">The type's .ot components in deserialize order.</param>
    /// <param name="SharedStates">Its StateMachine's shared states and .ot defaults, in .ot order.</param>
    /// <param name="QuestFlagStates">[state, value, quest flag] rules, in .ot order.</param>
    /// <param name="Talk">The NPC's dialogue options: [index, NPCTalkCategory Id] (the index C→S 0xa1 sends).</param>
    public sealed record ObjectType(string Id, string[] Components, string? Model = null, JsonElement[][]? SharedStates = null,
        JsonElement[][]? QuestFlagStates = null, JsonElement[][]? Talk = null)
    {
        public IReadOnlyDictionary<int, string> TalkCategories =>
            (Talk ?? []).GroupBy(r => r[0].GetInt32()).ToDictionary(g => g.Key, g => g.First()[1].GetString()!);

        public IReadOnlyList<(string Name, int Default)> States =>
            [.. (SharedStates ?? []).Select(s => (s[0].GetString()!, s[1].GetInt32()))];

        public IReadOnlyList<(string State, int Value, string Flag)> Rules =>
            [.. (QuestFlagStates ?? []).Select(r => (r[0].GetString()!, r[1].GetInt32(), r[2].GetString()!))];
    }

    public const string DefaultPath = "data/poe2/server-objects.json";

    /// <summary>The types by Id, or an empty table without the file.</summary>
    public static IReadOnlyDictionary<string, ObjectType> Load(string path = DefaultPath)
    {
        if (!File.Exists(path)) return new Dictionary<string, ObjectType>();
        var rows = JsonSerializer.Deserialize<ObjectType[]>(File.ReadAllText(path), JsonOptions) ?? [];
        return rows.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>A type's shared state values for a player with these quest flags: each state's default, then every
    /// rule whose flag the player has, in .ot order (a later rule wins).</summary>
    public static IReadOnlyList<int> States(ObjectType type, IReadOnlySet<string> questFlags)
    {
        var states = type.States;
        var values = states.Select(s => s.Default).ToArray();
        foreach (var (state, value, flag) in type.Rules)
        {
            int i = states.ToList().FindIndex(s => s.Name == state);
            if (i >= 0 && questFlags.Contains(flag)) values[i] = value;
        }
        return values;
    }

    /// <summary>The captured objects of the types the server can write, with their states for these quest flags; one
    /// note per type left out.</summary>
    public static IReadOnlyList<ServerObjectPoe2> InArea(IReadOnlyList<Poe2Areas.PlacedObject> objects,
        IReadOnlyDictionary<string, ObjectType> types, IReadOnlySet<string> questFlags, List<string> notes)
    {
        var placed = new List<ServerObjectPoe2>();
        var skipped = new HashSet<string>();
        foreach (var o in objects)
        {
            if (!types.TryGetValue(o.Type, out var t)) continue;
            if (!ServerObjectPoe2.CanSerialize(t.Components))
            {
                if (skipped.Add(t.Id)) notes.Add($"{t.Id}: components {string.Join(", ", t.Components)} not written");
                continue;
            }
            placed.Add(new ServerObjectPoe2(t.Id, o.X, o.Y, t.Components, o.Rotation, o.Model ?? t.Model, States(t, questFlags))
                { TalkCategories = t.TalkCategories });
        }
        return placed;
    }
}
