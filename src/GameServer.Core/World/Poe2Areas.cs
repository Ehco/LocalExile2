using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.World;

/// <summary>
/// The PoE2 areas the server knows (PORTING-POE2.md, waves 19–20): Act 1's WorldAreas ids, HASH16s, levels and
/// connections (WorldAreas.datc64 of 0.5.5e: id +0, Connections at +22, i32 AreaLevel at +38, HASH16 at row byte 42),
/// and what each (area, seed) has where: the server objects its rooms place, as scripts/walkmaps/poe2_walkmap.py
/// captured them from the client's level generation (data/walkability/&lt;id&gt;_seed&lt;seed&gt;.objects.json).
/// </summary>
public static class Poe2Areas
{
    /// <param name="Level">The area level: its monsters' level (the client logs "Generating level N area").</param>
    /// <param name="Town">A town's logins start in its middle, not by its first exit.</param>
    public sealed record Area(string Id, ushort Hash, byte Level, string[] Connections, bool Town = false);

    public static readonly Area[] Act1 =
    [
        new("G1_town", 0x3f70, 15, ["G1_1", "G1_2", "G1_2", "G1_2", "G2_1"], Town: true),
        new("G1_1", 0x0f14, 1, ["G1_town"]),
        new("G1_2", 0x9875, 2, ["G1_town", "G1_3", "G1_4"]),
        new("G1_3", 0x9ab2, 3, ["G1_2"]),
        new("G1_4", 0xcf6a, 4, ["G1_2", "G1_5", "G1_6", "ExpeditionSubArea_Kalguur_Act1"]),
        new("G1_5", 0x42ef, 5, ["G1_4"]),
        new("G1_6", 0x21db, 6, ["G1_4", "G1_7"]),
        new("G1_7", 0xe911, 7, ["G1_6", "G1_8", "G1_11", "G1_8", "G1_9", "G1_9"]),
        new("G1_8", 0x918a, 8, ["G1_7", "G1_7"]),
        new("G1_9", 0x2bcc, 8, ["G1_7", "G1_7"]),
        new("G1_10", 0x65b2, 15, ["G1_4"]),
        new("G1_11", 0xa117, 10, ["G1_7", "G1_13_1", "G1_12"]),
        new("G1_12", 0xac89, 11, ["G1_11"]),
        new("G1_13_1", 0xf0ca, 12, ["G1_11", "G1_13_2"]),
        new("G1_13_2", 0x946a, 13, ["G1_13_1", "G1_14"]),
        new("G1_14", 0x6123, 14, ["G1_13_2", "G1_15"]),
        new("G1_15", 0xc351, 15, ["G1_14"]),
    ];

    public static Area? ByHash(ushort hash) => Act1.FirstOrDefault(a => a.Hash == hash);
    public static Area? ById(string id) => Act1.FirstOrDefault(a => a.Id == id);

    /// <summary>Where each exit leads. A level's graph (.tgr) labels the node of each exit room "entranceK", the area's
    /// K-th connection (Clearfell's felling_*.tgr: town_N entrance1, mud_entrance entrance2, exit_woods entrance3; the
    /// town's entrance1 by the lumbermill, entrance2–4 by its three gates). The capture names the exit's room (.arm) and
    /// model, not the node, so a fragment of either picks the destination; the first that matches wins.</summary>
    static readonly Dictionary<string, (string Fragment, string Destination)[]> ExitRules = new()
    {
        ["G1_town"] = [("lumbermill_transition", "G1_1"), ("generic_cylinder", "G1_2")],
        ["G1_2"] = [("lumbermill_exterior", "G1_town"), ("mud_entrance", "G1_3"), ("exit_woods", "G1_4")],
    };

    static readonly HashSet<string> ExitTypes =
        ["Metadata/MiscellaneousObjects/AreaTransition", "Metadata/MiscellaneousObjects/AreaTransition_Animate"];
    const string ArrivalType = "Metadata/MiscellaneousObjects/AreaTransition_Auto";
    /// <summary>How far an exit's arrival marker (AreaTransition_Auto, in the same room) can be: 14–18 cells in
    /// Clearfell's exit rooms.</summary>
    const int ArrivalMarkerReach = 30;

    public sealed record PlacedObject(string Type, string? Model, string? Room, int X, int Y, float Rotation);

    /// <summary>The captured objects of (area, seed) in <paramref name="folder"/>, or null without a capture.</summary>
    public static IReadOnlyList<PlacedObject>? LoadObjects(string id, uint seed, string folder = "data/walkability")
    {
        string path = Path.Combine(folder, $"{id}_seed{seed}.objects.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PlacedObject[]>(File.ReadAllText(path), JsonOptions) ?? [];
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The area's exits among its objects, each with its destination; one note per exit left out.</summary>
    public static IReadOnlyList<AreaTransitionObjectPoe2> Exits(Area area, IReadOnlyList<PlacedObject> objects, List<string> notes)
    {
        var rules = ExitRules.GetValueOrDefault(area.Id) ?? [];
        var exits = new List<AreaTransitionObjectPoe2>();
        foreach (var o in objects.Where(o => ExitTypes.Contains(o.Type)))
        {
            string where = $"{o.Model?.Split('/')[^1]} at ({o.X}, {o.Y}) in {o.Room?.Split('/')[^1]}";
            var rule = rules.FirstOrDefault(r => (o.Room?.Contains(r.Fragment) ?? false) || (o.Model?.Contains(r.Fragment) ?? false));
            if (rule.Destination is null) { notes.Add($"{where}: no destination known, not created"); continue; }
            if (ById(rule.Destination) is not { } to) { notes.Add($"{where}: {rule.Destination} is not served, not created"); continue; }
            // The client reads an exit's index only in some areas (AreaTransitionObjectPoe2.Index): in the town, not in Clearfell.
            exits.Add(new AreaTransitionObjectPoe2(o.X, o.Y, to.Hash, Index: area.Town ? exits.Count : null, Rotation: o.Rotation,
                Model: o.Model));
        }
        return exits;
    }

    /// <summary>Where a player coming from <paramref name="cameFrom"/> appears: the arrival marker by the exit back to it,
    /// else a walkable cell a few cells from that exit (the town's gates have no marker); null when no exit leads back.</summary>
    public static (int X, int Y)? Arrival(ushort cameFrom, IReadOnlyList<AreaTransitionObjectPoe2> exits,
        IReadOnlyList<PlacedObject> objects, Func<int, int, bool>? walkable)
    {
        if (exits.FirstOrDefault(e => e.DestinationHash16 == cameFrom) is not { } back) return null;
        var marker = objects.Where(o => o.Type == ArrivalType && Chebyshev(o.X, o.Y, back) <= ArrivalMarkerReach)
            .MinBy(o => Chebyshev(o.X, o.Y, back));
        return marker is not null ? (marker.X, marker.Y) : Near(back.GridX, back.GridY, walkable);
    }

    static int Chebyshev(int x, int y, AreaTransitionObjectPoe2 exit) => Math.Max(Math.Abs(x - exit.GridX), Math.Abs(y - exit.GridY));

    /// <summary>The first walkable cell on the squares 8 to 20 cells around (x, y), out of the exit's reach
    /// (<see cref="InstanceSessionPoe2.ExitReach"/>); (x + 8, y) without a walk map.</summary>
    static (int X, int Y) Near(int x, int y, Func<int, int, bool>? walkable)
    {
        if (walkable is null) return (x + 8, y);
        for (int r = 8; r <= 20; r++)
            for (int d = -r; d <= r; d++)
                foreach (var (cx, cy) in new[] { (x + d, y - r), (x + d, y + r), (x - r, y + d), (x + r, y + d) })
                    if (walkable(cx, cy)) return (cx, cy);
        return (x, y);
    }

    /// <summary>The area each transfer's ticket left (by an exit); the new connection arrives by the exit back. In memory:
    /// a Gateway restart drops the client to the login anyway.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ushort> CameFrom = new();
}
