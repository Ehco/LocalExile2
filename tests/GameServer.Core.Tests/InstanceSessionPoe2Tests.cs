using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>A PoE2 client's instance session after the area message (PORTING-POE2.md, wave 13).</summary>
public class InstanceSessionPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");

    static InstanceSessionPoe2 NewSession(double speed = 37) =>
        new(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 100, Mana: 30), walkSpeed: speed);

    [Fact]
    public void Area_generated_sends_the_player_its_inventories_enter_area_the_frame_length_and_skills_once()
    {
        var session = NewSession();

        var sent = session.Handle(AreaGenerated, 0);

        Assert.True(session.InWorld);
        Assert.Equal(["0024", "02F7", "00DF", "0023", "02F5", "0066", "003A", "02DB"], sent.Select(m => Convert.ToHexString(m, 0, 2)));
        Assert.Equal(InstanceProtocolPoe2.SetLocalPlayer(InstanceSessionPoe2.DefaultPlayerId), sent[0]);
        Assert.Equal(InstanceProtocolPoe2.EnterArea(InstanceSessionPoe2.DefaultPlayerId), sent[3]);
        Assert.Equal(InstanceProtocolPoe2.FrameLength(InstanceSessionPoe2.FrameMs), sent[4]);
        Assert.Empty(session.Handle(AreaGenerated, 1));
    }

    [Fact]
    public void Inventories_go_as_one_write_of_every_default_slot()
    {
        var sent = NewSession().Handle(AreaGenerated, 0);

        int expected = InstanceInventoriesPoe2.PlayerDefaults.Sum(s => InstanceInventoriesPoe2.AddInventory(InstanceInventoriesPoe2.InventoryId(s.Row), s).Length);
        Assert.Equal(expected, sent[2].Length);
    }

    [Fact]
    public void Moves_are_answered_only_in_the_world()
    {
        var session = NewSession();
        byte[] move = Convert.FromHexString("006300807F807F000000");

        Assert.Empty(session.Handle(move, 0));
        session.Handle(AreaGenerated, 0);
        Assert.Single(session.Handle(move, 0));
    }

    [Fact]
    public void Lockstep_moves_are_echoed_at_the_estimated_cell()
    {
        var session = NewSession(speed: 10);
        session.Handle(AreaGenerated, 0);

        var first = session.Handle(Convert.FromHexString("00630080B400000000"), 1);    // dx 180, dy 0
        var stop = session.Handle(Convert.FromHexString("0063000000000000"), 2);

        Assert.Equal(InstanceMovementPoe2.DirectionMoveEcho(0x1000, 180, 0, 200, 200), first.Single());
        Assert.Equal(InstanceMovementPoe2.DirectionMoveEcho(0x1000, 0, 0, 210, 200), stop.Single());
    }

    [Fact]
    public void Lockstep_echoes_stop_at_the_areas_walls()
    {
        // Only the row y = 200 is walkable, up to x = 204.
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 100), walkSpeed: 10,
            walkable: (x, y) => y == 200 && x <= 204);
        session.Handle(AreaGenerated, 0);

        session.Handle(Convert.FromHexString("00630080B400000000"), 1);    // dx 180, dy 0
        var stop = session.Handle(Convert.FromHexString("0063000000000000"), 3);

        Assert.Equal(InstanceMovementPoe2.DirectionMoveEcho(0x1000, 0, 0, 204, 200), stop.Single());
    }

    [Fact]
    public void The_player_walks_on_walk_map_values_from_1_inside_the_map()
    {
        var walkable = InstanceSessionPoe2.Walkable([0, 1, 2, 5], width: 2, height: 2);

        Assert.False(walkable(0, 0));
        Assert.True(walkable(1, 0));
        Assert.True(walkable(0, 1));
        Assert.True(walkable(1, 1));
        Assert.False(walkable(2, 0));
        Assert.False(walkable(-1, 1));
        Assert.False(walkable(0, 2));
    }

    [Fact]
    public void A_move_with_the_clients_cell_starts_from_it()
    {
        var session = NewSession();
        session.Handle(AreaGenerated, 0);
        var w = new GameServer.Core.Protocol.Login.LoginWriter().U16(0x63).U8(0);
        foreach (int v in new[] { 127, 127, 321, 654 }) InstanceProtocol.CompactInt(w, v);
        byte[] predictive = w.U8(0).ToArray();

        var echo = session.Handle(predictive, 5).Single();

        Assert.Equal(InstanceMovementPoe2.DirectionMoveEcho(0x1000, 127, 127, 321, 654), echo);
    }

    [Fact]
    public void Split_frames_known_messages_and_stops_at_an_unknown_id()
    {
        byte[] data = Convert.FromHexString("001C00000000AB" + "006300807F807F000000" + "00620001" + "0BAD0000");

        var messages = InstanceSessionPoe2.Split(data, out int rest);

        Assert.Equal(["001C00000000AB", "006300807F807F000000", "00620001"], messages.Select(Convert.ToHexString));
        Assert.Equal(data.Length - 4, rest);
    }

    [Fact]
    public void Split_leaves_a_cut_off_message_for_later()
    {
        byte[] data = Convert.FromHexString("00620001" + "009A0035");

        var messages = InstanceSessionPoe2.Split(data, out int rest);

        Assert.Single(messages);
        Assert.Equal(4, rest);
    }
}
