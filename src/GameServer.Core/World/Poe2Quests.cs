using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The PoE2 quest tables the server needs (PORTING-POE2.md, wave 27), from data/poe2/quests.json
/// (scripts/re/poe2/quests.py): the QuestFlags rows (the client's flag set holds row indices; the server keeps the Ids),
/// the World panel's waypoint areas in the client's bit order (S→C 0x5a), and the flags an area sets when the character
/// enters it (WorldAreas FlagOnEntered: Visited-G1_2 ...).
/// </summary>
public sealed class Poe2Quests
{
    /// <summary>An area of the waypoint bitset: a WorldAreas row with HasWaypoint of kind 1 (town), 2 (area) or 31.</summary>
    public sealed record WaypointArea(string Id, ushort Hash, int Act, bool Town);

    public const string DefaultPath = "data/poe2/quests.json";

    readonly Dictionary<string, int> _flagRows = new(StringComparer.Ordinal);
    readonly Dictionary<ushort, int> _bitOfHash = [];
    readonly Dictionary<string, int> _bitOfId = new(StringComparer.Ordinal);
    readonly IReadOnlyDictionary<string, string[]> _entered, _granted;

    /// <param name="entered">Per area, the flags entering it sets (WorldAreas FlagOnEntered).</param>
    /// <param name="granted">Per area, the flags the game expects a character there to have (its RequiredFlags, and a
    /// town's the visit flags of the act's first area): Auth drops characters straight into the camp, so entering grants
    /// them too. Without them the World panel hides the camp's pin and the map (MapPins G1_town needs Visited-G1_1).</param>
    public Poe2Quests(IReadOnlyList<string> flags, IReadOnlyList<WaypointArea> waypoints,
        IReadOnlyDictionary<string, string[]>? entered = null, IReadOnlyDictionary<string, string[]>? granted = null)
    {
        for (int i = 0; i < flags.Count; i++) _flagRows.TryAdd(flags[i], i);
        Waypoints = waypoints;
        for (int i = 0; i < waypoints.Count; i++)
        {
            _bitOfHash.TryAdd(waypoints[i].Hash, i);
            _bitOfId.TryAdd(waypoints[i].Id, i);
        }
        _entered = entered ?? new Dictionary<string, string[]>();
        _granted = granted ?? new Dictionary<string, string[]>();
    }

    /// <summary>No tables: flags are kept but not sent, no waypoint is known.</summary>
    public static readonly Poe2Quests Empty = new([], []);

    /// <summary>The tables of <paramref name="path"/>, or <see cref="Empty"/> without the file.</summary>
    public static Poe2Quests Load(string path = DefaultPath)
    {
        if (!File.Exists(path)) return Empty;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var flags = root.TryGetProperty("flags", out var f) ? f.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
        var waypoints = root.TryGetProperty("waypoints", out var w)
            ? w.EnumerateArray().Select(e => new WaypointArea(e.GetProperty("id").GetString() ?? "", (ushort)e.GetProperty("hash").GetUInt32(),
                e.TryGetProperty("act", out var act) ? act.GetInt32() : 0, e.TryGetProperty("town", out var town) && town.GetBoolean())).ToList()
            : [];
        var entered = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var granted = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (root.TryGetProperty("areas", out var areas))
            foreach (var area in areas.EnumerateObject())
            {
                if (area.Value.TryGetProperty("entered", out var list))
                    entered[area.Name] = [.. list.EnumerateArray().Select(e => e.GetString() ?? "")];
                if (area.Value.TryGetProperty("granted", out var given))
                    granted[area.Name] = [.. given.EnumerateArray().Select(e => e.GetString() ?? "")];
            }
        return new(flags, waypoints, entered, granted);
    }

    /// <summary>How many QuestFlags rows the table has (0 without it: nothing to send the client).</summary>
    public int FlagCount => _flagRows.Count;

    /// <summary>The QuestFlags row of a flag Id, or null for an Id the table does not have.</summary>
    public int? FlagRow(string id) => _flagRows.TryGetValue(id, out int row) ? row : null;

    /// <summary>The rows of the Ids the table has, for the client's flag set.</summary>
    public IEnumerable<int> FlagRows(IEnumerable<string> ids) => ids.Select(FlagRow).Where(r => r is not null).Select(r => r!.Value);

    /// <summary>The waypoint areas in bit order (bit i of the 0x5a bitset is the i-th).</summary>
    public IReadOnlyList<WaypointArea> Waypoints { get; }

    /// <summary>The bitset bit of the area with this HASH16, or null when it has no waypoint.</summary>
    public int? WaypointBit(ushort hash) => _bitOfHash.TryGetValue(hash, out int bit) ? bit : null;

    /// <summary>The bitset bit of the area with this WorldAreas Id, or null when it has no waypoint.</summary>
    public int? WaypointBit(string id) => _bitOfId.TryGetValue(id, out int bit) ? bit : null;

    /// <summary>The waypoint area with this HASH16, or null.</summary>
    public WaypointArea? WaypointOf(ushort hash) => WaypointBit(hash) is int bit ? Waypoints[bit] : null;

    /// <summary>The flags entering the area sets (WorldAreas FlagOnEntered); none for an unknown area.</summary>
    public IReadOnlyList<string> EnteredFlags(string areaId) => _entered.GetValueOrDefault(areaId) ?? [];

    /// <summary>The flags a character in the area is expected to have (see the constructor); none for an unknown area.</summary>
    public IReadOnlyList<string> GrantedFlags(string areaId) => _granted.GetValueOrDefault(areaId) ?? [];
}
