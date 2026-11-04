using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Placed = GameServer.Core.World.Poe2Areas.PlacedObject;

namespace GameServer.Core.Tests;

/// <summary>PoE2 areas, their captured exits and arrivals (PORTING-POE2.md, wave 19).</summary>
public class Poe2AreasTests
{
    const string Animate = "Metadata/MiscellaneousObjects/AreaTransition_Animate", Auto = "Metadata/MiscellaneousObjects/AreaTransition_Auto";
    const string Rooms = "Metadata/Terrain/Gallows/Act1/1_2/Rooms/Unique/";
    const string Cylinder = "Metadata/Effects/Environment/area_transition/generic_cylinder.ao";

    // Clearfell seed 1 as captured live (2026-11-04).
    static readonly Placed[] Clearfell =
    [
        new(Animate, Cylinder, Rooms + "lumbermill_exteriorN.arm", 1001, 474, MathF.PI),
        new(Auto, null, Rooms + "lumbermill_exteriorN.arm", 1001, 460, 0),
        new("Metadata/MiscellaneousObjects/Checkpoint", null, Rooms + "lumbermill_exteriorN.arm", 1037, 534, MathF.PI),
        new(Animate, Cylinder, Rooms + "exit_woods.arm", 280, 999, MathF.PI / 2),
        new(Auto, null, Rooms + "exit_woods.arm", 262, 1001, MathF.PI / 2),
        new(Animate, Cylinder.Replace(".ao", "_vert.ao"), Rooms + "mud_entrance_woods_2.arm", 655, 1406, 0),
        new(Auto, null, Rooms + "mud_entrance_woods_2.arm", 658, 1412, MathF.PI / 2),
    ];

    static Poe2Areas.Area Area(string id) => Poe2Areas.ById(id)!;

    [Fact]
    public void Each_exit_leads_to_the_connection_its_room_is_the_entrance_to()
    {
        var notes = new List<string>();

        var exits = Poe2Areas.Exits(Area("G1_2"), Clearfell, notes);

        Assert.Empty(notes);
        Assert.Equal([("G1_town", 1001, 474), ("G1_4", 280, 999), ("G1_3", 655, 1406)],
            exits.Select(e => (Poe2Areas.ByHash(e.DestinationHash16)!.Id, e.GridX, e.GridY)));
        Assert.All(exits, e => Assert.Null(e.Index));
        Assert.Equal(MathF.PI, exits[0].Rotation);
        Assert.Equal(Cylinder, exits[0].Model);
    }

    [Fact]
    public void Town_exits_carry_an_index_and_follow_their_model()
    {
        Placed[] town =
        [
            new("Metadata/MiscellaneousObjects/AreaTransition", "Metadata/Terrain/Woods/Lumbermill/lumbermill_transition.ao", "town_1.arm", 115, 376, 4.61f),
            new(Animate, Cylinder, "town_1.arm", 324, 189, MathF.PI),
        ];

        var exits = Poe2Areas.Exits(Area("G1_town"), town, []);

        Assert.Equal(["G1_1", "G1_2"], exits.Select(e => Poe2Areas.ByHash(e.DestinationHash16)!.Id));
        Assert.Equal([0, 1], exits.Select(e => e.Index));
    }

    [Fact]
    public void An_exit_without_a_known_destination_is_left_out_with_a_note()
    {
        var notes = new List<string>();

        var exits = Poe2Areas.Exits(Area("G1_3"), [Clearfell[0]], notes);

        Assert.Empty(exits);
        Assert.Contains("no destination", Assert.Single(notes));
    }

    [Fact]
    public void Arriving_from_an_area_is_at_the_marker_by_the_exit_back()
    {
        var exits = Poe2Areas.Exits(Area("G1_2"), Clearfell, []);

        Assert.Equal((1001, 460), Poe2Areas.Arrival(Area("G1_town").Hash, exits, Clearfell, null));
        Assert.Equal((262, 1001), Poe2Areas.Arrival(Area("G1_4").Hash, exits, Clearfell, null));
        Assert.Equal((658, 1412), Poe2Areas.Arrival(Area("G1_3").Hash, exits, Clearfell, null));
        Assert.Null(Poe2Areas.Arrival(Area("G1_5").Hash, exits, Clearfell, null));
    }

    [Fact]
    public void A_marker_off_the_walkable_ground_gives_way_to_a_walkable_cell_by_the_exit()
    {
        // Clearfell's markers sit in the exit tiles' blocked border (live 2026-11-04: the player arrived through walls).
        var exits = Poe2Areas.Exits(Area("G1_2"), Clearfell, []);
        bool Walkable(int x, int y) => y >= 478 && x is >= 990 and <= 1012;

        var (x, y) = Poe2Areas.Arrival(Area("G1_town").Hash, exits, Clearfell, Walkable)!.Value;

        Assert.True(Walkable(x, y));
        Assert.True(Math.Max(Math.Abs(x - 1001), Math.Abs(y - 474)) > InstanceSessionPoe2.ExitReach);
        Assert.Equal((1001, 460), Poe2Areas.Arrival(Area("G1_town").Hash, exits, Clearfell, (_, _) => true));
    }

    [Fact]
    public void Without_a_marker_the_arrival_is_a_walkable_cell_out_of_the_exits_reach()
    {
        AreaTransitionObjectPoe2[] gate = [new(324, 189, Area("G1_2").Hash)];

        var (x, y) = Poe2Areas.Arrival(Area("G1_2").Hash, gate, [], (cx, cy) => cy >= 197)!.Value;

        Assert.True(y >= 197);
        Assert.True(Math.Max(Math.Abs(x - 324), Math.Abs(y - 189)) > InstanceSessionPoe2.ExitReach);
    }

    [Fact]
    public void Captured_objects_load_from_the_area_and_seed_file()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "G1_2_seed1.objects.json"),
                """[{"type": "Metadata/MiscellaneousObjects/Waypoint", "model": "w.ao", "room": "r.arm", "x": 585, "y": 1344, "rotation": 1.5}]""");

            var objects = Poe2Areas.LoadObjects("G1_2", 1, folder);

            Assert.Equal(new Placed("Metadata/MiscellaneousObjects/Waypoint", "w.ao", "r.arm", 585, 1344, 1.5f), Assert.Single(objects!));
            Assert.Null(Poe2Areas.LoadObjects("G1_2", 2, folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Act_one_areas_are_known_by_hash_and_id()
    {
        Assert.Equal("G1_2", Poe2Areas.ByHash(0x9875)?.Id);
        Assert.True(Area("G1_town").Town);
        Assert.False(Area("G1_2").Town);
        Assert.All(Poe2Areas.Act1, a => Assert.Equal(a, Poe2Areas.ByHash(a.Hash)));
    }
}
