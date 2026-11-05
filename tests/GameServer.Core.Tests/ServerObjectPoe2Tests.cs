using System.Text;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2 server objects other than exits and monsters: NPCs, the waypoint and their quest-flag states
/// (PORTING-POE2.md, wave 27).</summary>
public class ServerObjectPoe2Tests
{
    static readonly string[] NpcComponents =
        ["Positioned", "Animated", "BaseEvents", "InteractionAction", "NPC", "MinimapIcon", "Transitionable", "StateMachine"];

    [Fact]
    public void Npc_blob_is_positioned_model_interaction_npc_icon_transition_states_and_tasks()
    {
        const string model = "Metadata/NPC/Una.ao";
        byte[] b = new ServerObjectPoe2("Metadata/NPC/Four_Act1/Una", 282, 252, NpcComponents, Model: model, SharedStates: [1, 0]).Blob();

        string hex = Convert.ToHexString(b);
        // Object flags 0x02 (wake zone), no base data; Positioned: i32 x, i32 y, f32 rotation, u16, three u8.
        Assert.StartsWith("0200" + "1A010000" + "FC000000" + "00000000" + "0000" + "000000", hex);
        // Animated: 1, u32 unit count, UTF-16.
        int at = 2 + 17;
        Assert.Equal(1, b[at]);
        Assert.Equal((uint)model.Length, BitConverter.ToUInt32(b, at + 1));
        Assert.Equal(Encoding.Unicode.GetBytes(model), b[(at + 5)..(at + 5 + 2 * model.Length)]);
        at += 5 + 2 * model.Length;
        // BaseEvents nothing; InteractionAction 0; NPC 01 FF; MinimapIcon 02; Transitionable 1; StateMachine: 2 states, 0
        // timers, compact 1 + 02, compact 0 + 02; tasks 0.
        Assert.Equal("00" + "01FF" + "02" + "01" + "0200" + "0102" + "0002" + "00", Convert.ToHexString(b[at..]));
    }

    [Fact]
    public void Waypoint_blob_has_no_hideout_doodad_data_outside_a_hideout()
    {
        byte[] b = new ServerObjectPoe2("Metadata/MiscellaneousObjects/Waypoint", 10, 20,
            ["Positioned", "BaseEvents", "Animated", "HideoutDoodad", "MinimapIcon", "InteractionAction"], Model: "w.ao").Blob();

        // Positioned, Animated (1 + 4 + 2 × 4), MinimapIcon 02, InteractionAction 0, tasks 0.
        Assert.Equal(2 + 17 + 13 + 3, b.Length);
        Assert.Equal("02" + "00" + "00", Convert.ToHexString(b[^3..]));
    }

    [Fact]
    public void Types_with_a_component_the_server_cannot_write_are_not_serialized()
    {
        Assert.True(ServerObjectPoe2.CanSerialize(NpcComponents));
        Assert.False(ServerObjectPoe2.CanSerialize([.. NpcComponents, "ControlZone"]));   // RenlyIntro
        Assert.False(ServerObjectPoe2.CanSerialize(["Animated", "Positioned"]));          // Positioned comes first
        Assert.False(ServerObjectPoe2.CanSerialize([]));
        Assert.Throws<ArgumentException>(() => new ServerObjectPoe2("x", 0, 0, ["Positioned", "ControlZone"]).Blob());
    }

    [Fact]
    public void States_follow_the_players_flags_and_a_later_rule_wins()
    {
        var una = new ServerObjectPoe2("Metadata/NPC/Four_Act1/UnaHoodedOneInjured", 0, 0, NpcComponents)
        {
            StateDefaults = [("visible", 0), ("hide", 0), ("convulse", 0)],
            StateRules = [("visible", 1, "TreeOfSoulsMentorReleased"), ("hide", 1, "UnaFindTheSeedSeen"), ("convulse", 1, "UnaRingAnimationPlayed"),
                          ("visible", 0, "UnaFindTheSeedSeen")],
        };

        Assert.Equal([0, 0, 0], una.ForFlags(new HashSet<string>()).SharedStates);
        Assert.Equal([1, 0, 0], una.ForFlags(new HashSet<string> { "TreeOfSoulsMentorReleased" }).SharedStates);
        Assert.Equal([0, 1, 1], una.ForFlags(new HashSet<string> { "TreeOfSoulsMentorReleased", "UnaFindTheSeedSeen", "UnaRingAnimationPlayed" }).SharedStates);
        Assert.True(una.IsNpc);
        Assert.False(una.IsWaypoint);
        Assert.True(new ServerObjectPoe2("Metadata/MiscellaneousObjects/Waypoint", 0, 0, ["Positioned"]).IsWaypoint);
    }

    [Fact]
    public void A_talk_option_is_offered_by_its_required_and_excluded_flags()
    {
        var count = new NpcTalk(3, "Gossip", Requires: ["IronCountQuestReceived"], Excludes: ["IronCountKilled"], Finish: "UnaCountGossipSeen");

        Assert.False(count.Available(new HashSet<string>()));
        Assert.True(count.Available(new HashSet<string> { "IronCountQuestReceived" }));
        Assert.False(count.Available(new HashSet<string> { "IronCountQuestReceived", "IronCountKilled" }));
        Assert.True(new NpcTalk(21, "BuyItems").Available(new HashSet<string>()));
    }

    [Fact]
    public void A_talk_options_script_sets_flags_and_may_end_the_chat()
    {
        var roots = new NpcTalk(0, "Quests", Start: ["UnaGrimTangleOpenRoots"], Script:
            "Delay( 2.0, {ForEachOfType( \"Metadata/QuestObjects/Four_Act1/GrimTangleRoots\", (){ DirtyStateMachine(); } );});\r\n" +
            "DirtyStateMachine();\r\ntriggerer.FinishNPCChat();\r\ntriggerer.Delay( 8.0, {SetQuestFlag(GrimTangleRootsUnblocked);});");

        Assert.Equal(["GrimTangleRootsUnblocked"], roots.ScriptFlags);
        Assert.Equal(["UnaGrimTangleOpenRoots", "GrimTangleRootsUnblocked"], roots.StartFlags);
        Assert.True(roots.FinishesChat);
        Assert.False(new NpcTalk(16, "Quests", Script: "DirtyTransition();").FinishesChat);
        Assert.Empty(new NpcTalk(1, "Gossip").ScriptFlags);
    }

    [Fact]
    public void Object_types_load_their_states_rules_and_talk_options_from_json()
    {
        string path = Path.Combine(Path.GetTempPath(), $"server-objects-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            [
             {"id":"Metadata/NPC/Four_Act1/Una","components":["Positioned","Animated","BaseEvents","InteractionAction","NPC","MinimapIcon","Transitionable","StateMachine"],
              "model":null,"sharedStates":[["visible",1]],"questFlagStates":[["visible",0,"TreeOfSoulsMentorReleased"]],
              "talk":[{"index":0,"category":"Gossip","requires":[],"excludes":[],"start":["UnaIntroduced"],"finish":"UnaIntroduced","script":""},
                      {"index":21,"category":"BuyItems","requires":[],"excludes":[],"start":[],"finish":null,"script":""},
                      {"index":2,"category":"Gossip","requires":["Met-Renly"],"excludes":["IronCountKilled"],"start":[],"finish":"UnaRenlyGossipSeen","script":"triggerer.FinishNPCChat();"}]},
             {"id":"Metadata/NPC/Four_Act1/RenlyIntro","components":["Positioned","Animated","NPC","StateMachine","ControlZone"],"model":null,"sharedStates":[],"questFlagStates":[],"talk":[]}
            ]
            """);
        try
        {
            var types = Poe2ServerObjects.Load(path);
            var una = types["Metadata/NPC/Four_Act1/Una"];

            Assert.Equal([("visible", 1)], una.States);
            Assert.Equal([("visible", 0, "TreeOfSoulsMentorReleased")], una.Rules);
            Assert.Equal(3, una.Talks.Count);
            Assert.Equal(["UnaIntroduced"], una.Talks[0].Start!);
            Assert.Equal("UnaIntroduced", una.Talks[0].Finish);
            Assert.Null(una.Talks[21].Finish);
            Assert.Equal(["Met-Renly"], una.Talks[2].Requires!);
            Assert.True(una.Talks[2].FinishesChat);
            Assert.Equal([1], Poe2ServerObjects.States(una, new HashSet<string>()));
            Assert.Equal([0], Poe2ServerObjects.States(una, new HashSet<string> { "TreeOfSoulsMentorReleased" }));

            // The capture's objects: the supported type is placed with its defaults, rules and talks; the other is noted.
            var notes = new List<string>();
            var placed = Poe2ServerObjects.InArea(
                [new("Metadata/NPC/Four_Act1/Una", "Metadata/NPC/Una.ao", "room", 282, 252, 1.5f),
                 new("Metadata/NPC/Four_Act1/RenlyIntro", null, "room", 300, 250, 0),
                 new("Metadata/Monsters/Zombie", null, "room", 1, 1, 0)], types, notes);

            var o = Assert.Single(placed);
            Assert.Equal(("Metadata/NPC/Four_Act1/Una", 282, 252, 1.5f, "Metadata/NPC/Una.ao"), (o.TypePath, o.GridX, o.GridY, o.Rotation, o.Model));
            Assert.Equal([1], o.SharedStates);
            Assert.Equal(una.Talks.Keys.Order(), o.Talks.Keys.Order());
            Assert.Equal([0], o.ForFlags(new HashSet<string> { "TreeOfSoulsMentorReleased" }).SharedStates);
            Assert.Contains(notes, n => n.Contains("RenlyIntro") && n.Contains("ControlZone"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Without_the_file_there_are_no_types()
    {
        Assert.Empty(Poe2ServerObjects.Load(Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}.json")));
    }
}
