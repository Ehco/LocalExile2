using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>PoE2 exits and the change of area (PORTING-POE2.md, wave 19).</summary>
public class AreaTransitionPoe2Tests
{
    const ushort Clearfell = 0x9875, Town = 0x3f70;

    [Fact]
    public void An_exit_is_its_flags_place_model_destination_and_the_fixed_tail()
    {
        byte[] blob = new AreaTransitionObjectPoe2(1001, 474, Town).Blob();

        Assert.Equal(0x02, blob[0]);                                                 // in the wake zone
        Assert.Equal(0, blob[1]);                                                    // no base data
        Assert.Equal(1001, BitConverter.ToInt32(blob, 2));
        Assert.Equal(474, BitConverter.ToInt32(blob, 6));
        Assert.Equal(0, blob[19]);                                                   // no model
        Assert.Equal("000270" + "3F", Convert.ToHexString(blob, 20, 4));             // state 0, variant 2, HASH16 (LE)
        Assert.Equal(0x02, blob[^3]);                                                // minimap icon shown
        Assert.Equal(2 + 17 + 1 + 4 + 7 + 4 + 1 + 1 + 1, blob.Length);
    }

    [Fact]
    public void The_index_is_written_only_when_given()
    {
        byte[] without = new AreaTransitionObjectPoe2(324, 189, Clearfell).Blob();
        byte[] with = new AreaTransitionObjectPoe2(324, 189, Clearfell, Index: 1).Blob();

        Assert.Equal(without.Length + 1, with.Length);
        Assert.Equal(without[..24], with[..24]);
        Assert.Equal(1, with[24]);
    }

    [Fact]
    public void A_model_goes_after_its_bit()
    {
        const string model = "Metadata/Effects/Environment/area_transition/generic_cylinder.ao";
        byte[] blob = new AreaTransitionObjectPoe2(0, 0, Clearfell, Model: model).Blob();

        Assert.Equal(1, blob[19]);
        Assert.Equal(new AreaTransitionObjectPoe2(0, 0, Clearfell).Blob().Length + 4 + 2 * model.Length, blob.Length);
    }

    [Fact]
    public void Change_area_names_the_destination()
    {
        Assert.Equal("02F1" + "00" + "9875" + "00", Convert.ToHexString(InstanceProtocolPoe2.ChangeArea(Clearfell)));
    }

    [Fact]
    public void Connect_instance_ends_with_the_area_ticket_and_key()
    {
        byte[] key = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();

        byte[] message = InstanceProtocolPoe2.ConnectInstance("127.0.0.1", 6112, 0x1d1bd7dc, Clearfell, key);

        Assert.Equal("0029" + "00", Convert.ToHexString(message, 0, 3));
        Assert.Equal(key, message[^73..^9]);
        Assert.Equal(new byte[9], message[^9..]);
        Assert.Equal("9875" + "1D1BD7DC" + "0000", Convert.ToHexString(message[^81..^73]));
        Assert.Equal("17E0" + "7F000001" + "00000000", Convert.ToHexString(message[^91..^81]));
    }

    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");
    static readonly byte[] WalkEast = Convert.FromHexString("00630080B400000000"), Stop = Convert.FromHexString("0063000000000000");

    static InstanceSessionPoe2 Session(params AreaTransitionObjectPoe2[] exits) =>
        new(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 100, Mana: 30), walkSpeed: 10, exits: exits);

    [Fact]
    public void Entering_the_world_creates_each_exit_after_the_player()
    {
        var session = Session(new(220, 200, Clearfell), new(300, 300, Town));

        var sent = session.Handle(AreaGenerated, 0);

        Assert.Equal(7, sent.Count);
        Assert.Equal(InstanceProtocolPoe2.CreateObject(session.ExitId(0),
            InstanceEntities.ObjectTypeHash(AreaTransitionObjectPoe2.MetadataPath), new AreaTransitionObjectPoe2(220, 200, Clearfell).Blob()), sent[5]);
        Assert.Equal(session.PlayerId + 2, session.ExitId(1));
    }

    [Fact]
    public void Walking_into_an_exit_changes_area_once_and_waits_for_the_client()
    {
        var exit = new AreaTransitionObjectPoe2(220, 200, Clearfell);
        var session = Session(exit);
        session.Handle(AreaGenerated, 0);

        Assert.Empty(session.Tick(0));                       // 20 cells away
        session.Handle(WalkEast, 0);
        Assert.Equal([InstanceProtocolPoe2.ChangeArea(Clearfell)], session.Tick(1.5));
        Assert.Same(exit, session.LeavingBy);
        Assert.Empty(session.Tick(1.6));
        Assert.False(session.LeaveAcknowledged);

        session.Handle(Convert.FromHexString("035500"), 2);

        Assert.True(session.LeaveAcknowledged);
    }

    [Fact]
    public void An_arrival_beside_an_exit_takes_none_before_walking_out_of_reach()
    {
        var session = Session(new AreaTransitionObjectPoe2(196, 200, Town), new AreaTransitionObjectPoe2(220, 200, Clearfell));
        session.Handle(AreaGenerated, 0);

        Assert.Empty(session.Tick(0));                       // beside the exit back
        session.Handle(WalkEast, 0);
        Assert.Empty(session.Tick(0.5));                     // (205, 200): out of reach of both
        Assert.Equal([InstanceProtocolPoe2.ChangeArea(Clearfell)], session.Tick(1.5));
    }
}
