using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2 waypoint messages and tables (PORTING-POE2.md, wave 27).</summary>
public class InstanceWaypointsPoe2Tests
{
    [Fact]
    public void Open_world_panel_is_ui_kind_0_after_the_route_byte()
    {
        // S→C 0x98 (reader 0x142155300): u8 route, u8 kind, four u32 BE (the first: the waypoint object).
        Assert.Equal("0098" + "00" + "00" + "0000100A" + "00000000" + "00000000" + "00000000",
            Convert.ToHexString(InstanceWaypointsPoe2.OpenUi(InstanceWaypointsPoe2.WorldPanelUi, 0x100a)));
    }

    [Fact]
    public void Waypoints_message_is_the_route_a_u32_and_a_22_byte_bitset()
    {
        // S→C 0x5a (reader 0x142153c00): u8 route, u32 BE, then 22 raw bytes; bit i = the i-th waypoint area.
        byte[] m = InstanceWaypointsPoe2.Waypoints([0, 1, 9, 175]);

        Assert.Equal(2 + 1 + 4 + 22, m.Length);
        Assert.Equal("005A" + "00" + "00000000" + "03" + "02" + new string('0', 38) + "80", Convert.ToHexString(m));
        Assert.Equal(InstanceWaypointsPoe2.Waypoints([]), InstanceWaypointsPoe2.Waypoints([-1, 176, 1000]));
    }

    [Fact]
    public void Travel_request_reads_the_area_the_waypoint_and_ctrl()
    {
        // C→S 0x5b (writer 0x141f396f0, 10 bytes): u8 route, u16 BE HASH16, u32 BE, u8 Ctrl.
        byte[] m = Convert.FromHexString("005B" + "00" + "9875" + "0000100A" + "01");

        Assert.Equal(InstanceWaypointsPoe2.TravelLength, m.Length);
        Assert.Equal(new InstanceWaypointsPoe2.Travel(0x9875, 0x100a, true), InstanceWaypointsPoe2.ReadTravel(m));
        Assert.Null(InstanceWaypointsPoe2.ReadTravel(m[..9]));
        Assert.Null(InstanceWaypointsPoe2.ReadTravel(Convert.FromHexString("005A" + "00" + "9875" + "0000100A" + "01")));
        Assert.True(InstanceSessionPoe2.CanFrame(InstanceWaypointsPoe2.TravelId));
    }

    static readonly Poe2Quests Quests = new(["a1q1-KilledHillock", "Visited-G1_town", "NEVER_SET", "Visited-G1_2"],
        [new("G1_town", 0x3f70, 1, true), new("G1_2", 0x9875, 1, false), new("G1_4", 0xcf6a, 1, false)],
        new Dictionary<string, string[]> { ["G1_town"] = ["Visited-G1_town"], ["G1_2"] = ["Visited-G1_2"] });

    [Fact]
    public void Quest_tables_give_flag_rows_waypoint_bits_and_entry_flags()
    {
        Assert.Equal(4, Quests.FlagCount);
        Assert.Equal(3, Quests.FlagRow("Visited-G1_2"));
        Assert.Null(Quests.FlagRow("Unknown"));
        Assert.Equal([1, 3], Quests.FlagRows(["Visited-G1_town", "Unknown", "Visited-G1_2"]));
        Assert.Equal(0, Quests.WaypointBit((ushort)0x3f70));
        Assert.Equal(1, Quests.WaypointBit("G1_2"));
        Assert.Null(Quests.WaypointBit((ushort)0x0f14));                       // The Riverbank has no waypoint
        Assert.Equal("G1_4", Quests.WaypointOf(0xcf6a)?.Id);
        Assert.True(Quests.WaypointOf(0x3f70)?.Town);
        Assert.Equal(["Visited-G1_2"], Quests.EnteredFlags("G1_2"));
        Assert.Empty(Quests.EnteredFlags("G1_1"));
        Assert.Equal(0, Poe2Quests.Empty.FlagCount);
        Assert.Empty(Poe2Quests.Empty.Waypoints);
    }

    [Fact]
    public void Quest_tables_load_from_json()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quests-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
             "flags": ["a1q1-KilledHillock","Visited-G1_town"],
             "waypoints": [
              {"id":"G1_town","hash":16240,"act":1,"town":true},
              {"id":"G1_2","hash":39029,"act":1,"town":false}
             ],
             "areas": {
              "G1_town":{"entered":["Visited-G1_town"],"granted":["Visited-G1_1","MillerDefeated"]},
              "G1_2":{"entered":["Visited-G1_2"]}
             }
            }
            """);
        try
        {
            var q = Poe2Quests.Load(path);

            Assert.Equal(2, q.FlagCount);
            Assert.Equal(1, q.FlagRow("Visited-G1_town"));
            Assert.Equal(["G1_town", "G1_2"], q.Waypoints.Select(w => w.Id));
            Assert.Equal(1, q.WaypointBit((ushort)39029));
            Assert.Equal(["Visited-G1_town"], q.EnteredFlags("G1_town"));
            Assert.Equal(["Visited-G1_1", "MillerDefeated"], q.GrantedFlags("G1_town"));
            Assert.Empty(q.GrantedFlags("G1_2"));
        }
        finally { File.Delete(path); }
        Assert.Same(Poe2Quests.Empty, Poe2Quests.Load(Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void A_waypoint_arrival_is_a_walkable_cell_near_the_areas_waypoint()
    {
        Poe2Areas.PlacedObject[] objects = [new("Metadata/MiscellaneousObjects/Waypoint", "w.ao", "room", 100, 100, 0),
            new("Metadata/NPC/Four_Act1/Una", null, "room", 50, 50, 0)];

        Assert.Equal((108, 100), Poe2Areas.WaypointArrival(objects, null));
        var (x, y) = Poe2Areas.WaypointArrival(objects, (x, y) => x == 100 && y == 112)!.Value;
        Assert.Equal((100, 112), (x, y));
        Assert.Null(Poe2Areas.WaypointArrival(objects[1..], null));
    }
}
