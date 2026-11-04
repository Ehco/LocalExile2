using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>The PoE2 session's NPCs, dialogue options, quest flags and waypoints (PORTING-POE2.md, wave 27).</summary>
public class NpcSessionPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly byte[] WalkEast = Convert.FromHexString("00630080B400000000");
    static readonly byte[] Goodbye = Convert.FromHexString("00A400");
    static byte[] Option(int index) => Convert.FromHexString("00A100" + index.ToString("X2"));
    static byte[] Click(uint id) => Convert.FromHexString("01A5" + "00" + "2000C266" + "0400" + "FF" + "08" + id.ToString("X8") + "000000D2" + "000000C8");
    static byte[] TravelTo(ushort hash, uint waypoint = 0x1002) => Convert.FromHexString("005B" + "00" + hash.ToString("X4") + waypoint.ToString("X8") + "00");

    // Flags in row order; two waypoint areas; the entry flags of both.
    static readonly string[] Flags = ["Visited-G1_town", "UnaIntroduced", "UnaClearfell1GossipSeen", "TreeOfSoulsQuestReceived",
        "IronCountKilled", "UnaRingAnimationPlayed", "Visited-G1_2"];
    static readonly Poe2Quests Quests = new(Flags, [new("G1_town", 0x3f70, 1, true), new("G1_2", 0x9875, 1, false)],
        new Dictionary<string, string[]> { ["G1_town"] = ["Visited-G1_town"], ["G1_2"] = ["Visited-G1_2"] });
    static readonly Poe2Areas.Area Town = Poe2Areas.ById("G1_town")!, Clearfell = Poe2Areas.ById("G1_2")!;
    static readonly string[] NpcComponents =
        ["Positioned", "Animated", "BaseEvents", "InteractionAction", "NPC", "MinimapIcon", "Transitionable", "StateMachine"];

    static ServerObjectPoe2 Una(int x, int y) => new("Metadata/NPC/Four_Act1/Una", x, y, NpcComponents, SharedStates: [1])
    {
        StateDefaults = [("visible", 1)], StateRules = [("visible", 0, "TreeOfSoulsMentorReleased")],
        Talks = new Dictionary<int, NpcTalk>
        {
            [0] = new(0, "Gossip", Start: ["UnaIntroduced"], Finish: "UnaIntroduced"),
            [1] = new(1, "Gossip", Excludes: ["IronCountKilled"], Finish: "UnaClearfell1GossipSeen"),
            [13] = new(13, "Quests", Start: ["TreeOfSoulsQuestReceived"], Finish: "TreeOfSoulsQuestReceived"),
            [21] = new(21, "BuyItems"),
        },
    };

    // The injured Hooded One's Una: "Use Ring" ends the chat and makes her convulse.
    static ServerObjectPoe2 UnaInjured(int x, int y) => new("Metadata/NPC/Four_Act1/UnaHoodedOneInjured", x, y, NpcComponents, SharedStates: [0, 0, 0])
    {
        StateDefaults = [("visible", 0), ("hide", 0), ("convulse", 0)],
        StateRules = [("convulse", 1, "UnaRingAnimationPlayed")],
        Talks = new Dictionary<int, NpcTalk>
        {
            [2] = new(2, "Quests", Start: ["UnaRingAnimationPlayed"], Finish: "UnaRingAnimationPlayed", Script: "DirtyStateMachine();\r\ntriggerer.FinishNPCChat();"),
        },
    };

    // A bystander whose state follows the same flag as Una's introduction (an invented rule, for the test).
    static ServerObjectPoe2 Bystander(int x, int y) => new("Metadata/NPC/Four_Act1/Finn", x, y, NpcComponents, SharedStates: [1])
        { StateDefaults = [("visible", 1)], StateRules = [("visible", 0, "UnaIntroduced")] };

    static ServerObjectPoe2 Waypoint(int x, int y) => new("Metadata/MiscellaneousObjects/Waypoint", x, y,
        ["Positioned", "BaseEvents", "Animated", "HideoutDoodad", "MinimapIcon", "InteractionAction"]);

    sealed class Saved
    {
        public IReadOnlyCollection<string>? Flags, Waypoints;
    }

    static InstanceSessionPoe2 Session(Poe2Areas.Area area, Saved saved, IEnumerable<string>? flags = null, IEnumerable<string>? waypoints = null,
        Poe2Quests? quests = null, params ServerObjectPoe2[] objects) =>
        new(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 100, Mana: 30), walkSpeed: 10, serverObjects: objects,
            questFlags: flags, waypoints: waypoints, quests: quests ?? Quests, area: area,
            onQuestFlags: f => saved.Flags = f, onWaypoints: w => saved.Waypoints = w);

    static byte[] FlagsMessage(InstanceSessionPoe2 session) =>
        InstanceQuestsPoe2.PlayerQuestFlags(session.PlayerId, Quests.FlagRows(session.QuestFlags));

    static IEnumerable<string> Ids(IEnumerable<byte[]> messages) => messages.Select(m => Convert.ToHexString(m, 0, 2));

    [Fact]
    public void Entering_a_town_sets_its_visit_flag_activates_its_waypoint_and_sends_both_after_the_objects()
    {
        var saved = new Saved();
        var session = Session(Town, saved, objects: [Una(210, 200), Waypoint(200, 260)]);

        var sent = session.Handle(AreaGenerated, 0);

        Assert.Equal(["0024", "02F7", "00DF", "0023", "02F5", "0066", "02F7", "02F7", "02F9", "005A"], Ids(sent));
        Assert.Equal(InstanceQuestsPoe2.PlayerQuestFlags(session.PlayerId, [0]), sent[^2]);
        Assert.Equal(InstanceWaypointsPoe2.Waypoints([0]), sent[^1]);
        Assert.Equal(["Visited-G1_town"], saved.Flags);
        Assert.Equal(["G1_town"], saved.Waypoints);
        Assert.Equal((uint)0x1001, session.ServerObjectId(0));
    }

    [Fact]
    public void A_known_character_keeps_its_flags_and_waypoints()
    {
        var saved = new Saved();
        var session = Session(Clearfell, saved, flags: ["UnaIntroduced"], waypoints: ["G1_town"]);

        var sent = session.Handle(AreaGenerated, 0);

        // The entry flag is added (saved), the town's waypoint kept; Clearfell's own is not active yet.
        Assert.Equal(["UnaIntroduced", "Visited-G1_2"], saved.Flags!.Order());
        Assert.Null(saved.Waypoints);
        Assert.Equal(InstanceQuestsPoe2.PlayerQuestFlags(session.PlayerId, [1, 6]), sent[^2]);
        Assert.Equal(InstanceWaypointsPoe2.Waypoints([0]), sent[^1]);
    }

    [Fact]
    public void Without_the_quest_tables_flags_are_kept_but_nothing_is_sent()
    {
        var saved = new Saved();
        var session = Session(Town, saved, quests: Poe2Quests.Empty, objects: [Una(210, 200)]);

        var sent = session.Handle(AreaGenerated, 0);
        session.Handle(Click(0x1001), 0);
        session.Tick(1);
        var picked = session.Handle(Option(0), 1);

        Assert.Equal(["0024", "02F7", "00DF", "0023", "02F5", "0066", "02F7"], Ids(sent));
        Assert.Equal([InstanceNpcsPoe2.Talk(0x1001, 0)], picked);
        Assert.Contains("UnaIntroduced", session.QuestFlags);
        Assert.Equal(["UnaIntroduced"], saved.Flags);
    }

    [Fact]
    public void A_click_on_an_npc_walks_into_reach_then_opens_its_dialogue()
    {
        var session = Session(Town, new Saved(), objects: [Una(210, 200)]);
        session.Handle(AreaGenerated, 0);

        var started = session.Handle(Click(0x1001), 0);

        Assert.Equal("01C8", Convert.ToHexString(started.Single(), 0, 2));
        Assert.Empty(session.Tick(0.2));                                   // 4 cells at 10 cells/s
        Assert.Equal([InstanceNpcsPoe2.Talk(0x1001)], session.Tick(0.5));
        Assert.Equal((204, 200), session.Walk.Cell(0.5));
        Assert.Equal((uint)0x1001, session.TalkingTo);
        Assert.Empty(session.Tick(0.6));
    }

    [Fact]
    public void A_move_during_the_approach_drops_the_talk()
    {
        var session = Session(Town, new Saved(), objects: [Una(210, 200)]);
        session.Handle(AreaGenerated, 0);
        session.Handle(Click(0x1001), 0);

        session.Handle(WalkEast, 0.2);

        Assert.DoesNotContain(session.Tick(0.5), m => m[0] == 0 && m[1] == InstanceNpcsPoe2.TalkId);
        Assert.Equal(0u, session.TalkingTo);
    }

    [Fact]
    public void An_option_sets_its_start_flags_then_plays_its_talk_and_continue_sets_the_finish_flag()
    {
        var saved = new Saved();
        var session = Session(Town, saved, objects: [Una(210, 200)]);
        session.Handle(AreaGenerated, 0);
        session.Handle(Click(0x1001), 0);
        session.Tick(1);

        var introduction = session.Handle(Option(0), 1);
        var backFromIntroduction = session.Handle(Goodbye, 2);
        var clearfell = session.Handle(Option(1), 3);
        var backFromClearfell = session.Handle(Goodbye, 4);
        var closed = session.Handle(Goodbye, 5);

        // Introduction: its start flag, then the talk; its finish flag is the same, so Continue only goes back.
        Assert.Equal([InstanceQuestsPoe2.PlayerQuestFlags(session.PlayerId, [0, 1]), InstanceNpcsPoe2.Talk(0x1001, 0)], introduction);
        Assert.Equal([InstanceNpcsPoe2.Talk(0x1001)], backFromIntroduction);
        // Clearfell: no start flag; Continue sets its finish flag.
        Assert.Equal([InstanceNpcsPoe2.Talk(0x1001, 1)], clearfell);
        Assert.Equal([InstanceQuestsPoe2.PlayerQuestFlags(session.PlayerId, [0, 1, 2]), InstanceNpcsPoe2.Talk(0x1001)], backFromClearfell);
        Assert.Equal([InstanceNpcsPoe2.Talk(0)], closed);
        Assert.Equal(0u, session.TalkingTo);
        Assert.Equal(["UnaClearfell1GossipSeen", "UnaIntroduced", "Visited-G1_town"], saved.Flags!.Order());
    }

    [Fact]
    public void An_option_the_flags_exclude_is_refused_and_buy_items_opens_the_buy_window()
    {
        var session = Session(Town, new Saved(), flags: ["IronCountKilled"], objects: [Una(210, 200)]);
        session.Handle(AreaGenerated, 0);
        session.Handle(Click(0x1001), 0);
        session.Tick(1);

        Assert.Empty(session.Handle(Option(1), 1));
        Assert.Equal([InstanceNpcsPoe2.ShopWindow(0x1001)], session.Handle(Option(21), 2));
        Assert.Equal([InstanceNpcsPoe2.Talk(0)], session.Handle(Goodbye, 3));
    }

    [Fact]
    public void Options_are_ignored_without_an_open_dialogue()
    {
        var session = Session(Town, new Saved(), objects: [Una(210, 200)]);
        session.Handle(AreaGenerated, 0);

        Assert.Empty(session.Handle(Option(0), 1));
        Assert.Equal([InstanceNpcsPoe2.Talk(0)], session.Handle(Goodbye, 2));
    }

    [Fact]
    public void A_flag_changes_the_states_of_the_other_npcs_at_once()
    {
        var session = Session(Town, new Saved(), objects: [Una(210, 200), Bystander(230, 200)]);
        session.Handle(AreaGenerated, 0);
        session.Handle(Click(0x1001), 0);
        session.Tick(1);

        var sent = session.Handle(Option(0), 1);

        Assert.Equal(["02F9", "02FA", "02F7", "00A3"], Ids(sent));
        Assert.Equal(InstanceProtocolPoe2.RemoveObject(0x1002), sent[1]);
        Assert.Equal(InstanceProtocolPoe2.CreateObject(0x1002, InstanceEntities.ObjectTypeHash(Bystander(0, 0).TypePath),
            Bystander(230, 200).ForFlags(new HashSet<string> { "UnaIntroduced" }).Blob()), sent[2]);
        Assert.Equal([0], session.ServerObject(1).SharedStates);
        Assert.Equal([1], session.ServerObject(0).SharedStates);
    }

    [Fact]
    public void A_finish_npc_chat_script_ends_the_dialogue_then_recreates_the_npc_whose_state_changed()
    {
        var saved = new Saved();
        var session = Session(Town, saved, objects: [UnaInjured(210, 200)]);
        session.Handle(AreaGenerated, 0);
        session.Handle(Click(0x1001), 0);
        session.Tick(1);

        var sent = session.Handle(Option(2), 1);

        // The flags, the end of the dialogue (object 0), then the NPC again with convulse = 1.
        Assert.Equal(["02F9", "00A3", "02FA", "02F7"], Ids(sent));
        Assert.Equal(InstanceNpcsPoe2.Talk(0), sent[1]);
        Assert.Equal(InstanceProtocolPoe2.RemoveObject(0x1001), sent[2]);
        Assert.Equal([0, 0, 1], session.ServerObject(0).SharedStates);
        Assert.Equal(InstanceProtocolPoe2.CreateObject(0x1001, InstanceEntities.ObjectTypeHash(session.ServerObject(0).TypePath),
            session.ServerObject(0).Blob()), sent[3]);
        Assert.Equal(0u, session.TalkingTo);
        Assert.Contains("UnaRingAnimationPlayed", saved.Flags!);
        Assert.Equal([InstanceNpcsPoe2.Talk(0)], session.Handle(Goodbye, 2));
    }

    [Fact]
    public void A_click_on_the_waypoint_activates_it_then_opens_the_world_panel()
    {
        var saved = new Saved();
        var session = Session(Clearfell, saved, objects: [Waypoint(240, 200)]);
        session.Handle(AreaGenerated, 0);

        var started = session.Handle(Click(0x1001), 0);

        Assert.Equal("01C8", Convert.ToHexString(started.Single(), 0, 2));
        Assert.Empty(session.Tick(3));                                     // 34 cells at 10 cells/s
        Assert.Equal([InstanceWaypointsPoe2.Waypoints([1]), InstanceWaypointsPoe2.OpenUi(InstanceWaypointsPoe2.WorldPanelUi, 0x1001)],
            session.Tick(3.5));
        Assert.Equal(["G1_2"], saved.Waypoints);
        Assert.Equal(["G1_2"], session.Waypoints);
        // A second click only opens the panel.
        session.Handle(Click(0x1001), 4);
        Assert.Equal([InstanceWaypointsPoe2.OpenUi(InstanceWaypointsPoe2.WorldPanelUi, 0x1001)], session.Tick(4.1));
    }

    [Fact]
    public void Walking_up_to_the_waypoint_activates_it()
    {
        var saved = new Saved();
        var session = Session(Clearfell, saved, objects: [Waypoint(240, 200)]);
        session.Handle(AreaGenerated, 0);

        session.Handle(WalkEast, 0);

        Assert.Empty(session.Tick(1));                                     // at 210: 30 cells away
        Assert.Equal([InstanceWaypointsPoe2.Waypoints([1])], session.Tick(2));   // at 220: 20 cells away
        Assert.Empty(session.Tick(3));
        Assert.Equal(["G1_2"], saved.Waypoints);
    }

    [Fact]
    public void Travel_from_the_world_panel_changes_area_to_an_activated_waypoint()
    {
        var session = Session(Town, new Saved(), waypoints: ["G1_2"], objects: [Waypoint(200, 260)]);
        session.Handle(AreaGenerated, 0);

        var sent = session.Handle(TravelTo(0x9875, 0x1001), 1);

        Assert.Equal([InstanceProtocolPoe2.ChangeArea(0x9875)], sent);
        Assert.Equal((ushort)0x9875, session.LeavingTo);
        Assert.True(session.LeavingAtWaypoint);
        Assert.Null(session.LeavingBy);
        Assert.Empty(session.Tick(2));
        Assert.Empty(session.Handle(TravelTo(0x9875, 0x1001), 2));
        Assert.False(session.LeaveAcknowledged);
        session.Handle(Convert.FromHexString("035500"), 3);
        Assert.True(session.LeaveAcknowledged);
    }

    [Fact]
    public void Travel_to_a_waypoint_not_activated_or_to_this_area_does_nothing()
    {
        var fromClearfell = Session(Clearfell, new Saved());
        fromClearfell.Handle(AreaGenerated, 0);
        var fromTown = Session(Town, new Saved(), waypoints: ["G1_2"]);
        fromTown.Handle(AreaGenerated, 0);

        Assert.Empty(fromClearfell.Handle(TravelTo(0x3f70), 1));          // the town's waypoint: never visited
        Assert.Null(fromClearfell.LeavingTo);
        Assert.Empty(fromTown.Handle(TravelTo(0x3f70), 1));               // this area
        Assert.Empty(fromTown.Handle(TravelTo(0x0f14), 1));               // The Riverbank: no waypoint
        Assert.Null(fromTown.LeavingTo);
    }
}
