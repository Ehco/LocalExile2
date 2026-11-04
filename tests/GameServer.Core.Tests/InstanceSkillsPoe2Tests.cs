using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>PoE2 skills: the skill list, the skill bar, skill use and its echo (PORTING-POE2.md, wave 21).</summary>
public class InstanceSkillsPoe2Tests
{
    static readonly byte[] AreaGenerated = Convert.FromHexString("009A003533F5D53A64F181");

    // Live 2026-11-04: Space (the dodge roll, a cell relative to the player) and a left click with Punch on monster 0x1006.
    static readonly byte[] DodgeUse = Convert.FromHexString("01A500000001030400FF0000000007FFFFFFE8");
    static readonly byte[] PunchUse = Convert.FromHexString("01A500000001020400FF0800001006000004860000052E");

    static InstanceSessionPoe2 InWorld()
    {
        var skills = Poe2StartingSkills.For(Accounts.Poe2Classes.Of(Accounts.CharacterClass.Warrior));
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 100, Skills: skills));
        session.Handle(AreaGenerated, 0);
        return session;
    }

    [Fact]
    public void Skill_list_entries_are_64_bytes_with_the_slot_mask_after_the_id()
    {
        byte[] list = InstanceProtocolPoe2.SkillList([(0x102, 0b101)], merge: true);

        Assert.Equal(69, list.Length);
        Assert.Equal("0066000101" + "00000102" + "00" + "0500000000", Convert.ToHexString(list, 0, 15));
        Assert.All(list[15..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Dodge_roll_use_is_19_bytes_relative_to_the_player()
    {
        var use = InstanceSkillsPoe2.ReadUseSkill(DodgeUse, out int length);

        Assert.Equal(19, length);
        Assert.Equal(new UseSkillPoe2(0x103, 0x0400, 0xff, 0, null, 7, -24), use);
    }

    [Fact]
    public void Targeted_use_is_23_bytes_with_the_target_and_its_absolute_cell()
    {
        var use = InstanceSkillsPoe2.ReadUseSkill(PunchUse, out int length);

        Assert.Equal(23, length);
        Assert.Equal(new UseSkillPoe2(0x102, 0x0400, 0xff, InstanceSkillsPoe2.TargetOption, 0x1006, 1158, 1326), use);
        Assert.Null(InstanceSkillsPoe2.ReadUseSkill(PunchUse.AsSpan(0, 19), out length));
        Assert.Equal(0, length);
    }

    [Fact]
    public void Action_started_names_the_target_and_is_not_predicted()
    {
        byte[] started = InstanceSkillsPoe2.ActionStarted(0x1000, 200, 200, 1158, 1326, 0x102, 0x0400, 0x1006);

        Assert.Equal("01C8" + "00001000" + "00000000" + "00" + "0000" + "0480" + "00001006" + "80C880C88486852E"
            + "00000102" + "FFFF" + "00", Convert.ToHexString(started));
        Assert.Equal("0400", Convert.ToHexString(InstanceSkillsPoe2.ActionStarted(1, 0, 0, 0, 0, 0x103, 0x0480), 13, 2));
    }

    [Fact]
    public void Binding_a_slot_answers_a_merge_with_the_slot_mask_and_rebinding_clears_the_old_skill()
    {
        var session = InWorld();

        var bound = session.Handle(Convert.FromHexString("005C000000000102"), 1);
        var moved = session.Handle(Convert.FromHexString("005C000000000103"), 2);

        Assert.Equal(InstanceProtocolPoe2.SkillList([(0x102, 1)], merge: true), bound.Single());
        Assert.Equal(InstanceProtocolPoe2.SkillList([(0x102, 0), (0x103, 1)], merge: true), moved.Single());
        Assert.Empty(session.Handle(Convert.FromHexString("005C000500000999"), 3));
    }

    [Fact]
    public void Targeted_use_of_a_granted_skill_is_echoed_from_the_player_and_others_are_not()
    {
        var session = InWorld();

        var sent = session.Handle(PunchUse, 1);

        Assert.Equal(InstanceSkillsPoe2.ActionStarted(0x1000, 200, 200, 1158, 1326, 0x102, 0x0400, 0x1006), sent.Single());
        Assert.Empty(session.Handle(DodgeUse, 1));
        Assert.Empty(session.Handle(Convert.FromHexString("01A500000009990400FF0800001006000004860000052E"), 1));
    }

    [Fact]
    public void Split_frames_both_lengths_of_skill_use()
    {
        var messages = InstanceSessionPoe2.Split([.. DodgeUse, .. PunchUse, .. PunchUse[..10]], out int rest);

        Assert.Equal([DodgeUse, PunchUse], messages);
        Assert.Equal(42, rest);
    }

    [Fact]
    public void Dodge_roll_record_has_its_rows_and_no_stats()
    {
        var w = new BlobWriter();
        ActorSkillsPoe2.WriteRecord(w, Poe2StartingSkills.For(Accounts.Poe2Classes.Of(Accounts.CharacterClass.Warrior))[1]);

        Assert.Equal("03010000" + "00" + "00" + "83030000" + "00" + "89030000" + "00" + "00" + new string('0', 24) + "00"
            + "00" + "00", Convert.ToHexString(w.ToArray()));
    }
}
