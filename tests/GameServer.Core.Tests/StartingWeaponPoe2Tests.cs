using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>The class's starting weapon: equipped, worn and giving its basic attack (PORTING-POE2.md, wave 26).</summary>
public class StartingWeaponPoe2Tests
{
    static readonly Poe2Items.ItemType WoodenClub = new("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/FourOneHandMace1",
        2040, "Wooden Club", 1886603766, 2, 3, 1, 52869,
        ["Base", "LocalStats", "Mods", "AttributeRequirements", "Wieldable", "Weapon", "Quality", "Sockets"],
        ItemClass: 12, Weapon: new(6, 10, 690, 13, 500), StartingClasses: [6], Attack: new(400, 400), Wieldable: 5);

    static readonly Poe2Classes.Row Warrior = Poe2Classes.Of(CharacterClass.Warrior);

    [Fact]
    public void A_normal_weapon_is_base_poe2_mods_quality_and_no_sockets()
    {
        var club = Poe2Items.Create(WoodenClub);

        // Base; Mods: item level 1, no requirement, usable 7, flags 0, no nested items, identified normal properties;
        // Quality; Sockets: none. LocalStats, AttributeRequirements, Wieldable and Weapon read nothing.
        Assert.Equal("F6497370" + "0000" + "85CE" + "00" + "00" + "0000" + "00"
            + "01" + "00" + "07" + "00" + "00" + "00" + "0800" + "00"
            + "00" + "00", Convert.ToHexString(club.Payload()));
    }

    [Fact]
    public void The_warrior_starts_with_the_wooden_club()
    {
        var types = new Dictionary<string, Poe2Items.ItemType> { [WoodenClub.Id] = WoodenClub };

        Assert.Same(WoodenClub, Poe2Items.StartingWeapon(types, Warrior.Index));
        Assert.Null(Poe2Items.StartingWeapon(types, 1));
    }

    [Fact]
    public void A_weapon_is_swung_with_default_attack_and_its_values()
    {
        var attack = Poe2StartingSkills.For(Warrior, WoodenClub)[0];
        var unarmed = Poe2StartingSkills.For(Warrior)[0];

        Assert.Equal((Poe2StartingSkills.PunchId, Poe2StartingSkills.DefaultAttackEffectsPerLevelRow, Poe2StartingSkills.DefaultAttackStatSetsPerLevelRow),
            (attack.Id, attack.EffectsPerLevelRow, attack.StatSetsPerLevelRow));
        Assert.Equal(new AttackPoe2(6, 10, 690, 13), AttackPoe2.Of(attack));
        Assert.Equal((Poe2StartingSkills.PunchEffectsPerLevelRow, Poe2StartingSkills.PunchStatSetsPerLevelRow),
            (unarmed.EffectsPerLevelRow, unarmed.StatSetsPerLevelRow));
        Assert.Equal(new AttackPoe2(2, 8, 606, 8), AttackPoe2.Of(unarmed));
    }

    [Fact]
    public void The_main_hand_weapon_type_is_the_wieldable_class_so_the_client_allows_the_attack()
    {
        int TypeOf(IReadOnlyList<(uint Key, int Value)> stats) =>
            stats.Where(s => s.Key == Poe2StartingSkills.MainHandWeaponType).Select(s => s.Value).SingleOrDefault();

        Assert.Equal(5, TypeOf(Poe2StartingSkills.MainHandStats(Warrior, WoodenClub)));
        Assert.Equal(0, TypeOf(Poe2StartingSkills.MainHandStats(Warrior, null)));
    }

    [Fact]
    public void The_model_wears_the_weapon_in_the_main_hand()
    {
        var bare = new PlayerObjectPoe2(200, 200, 58).Blob();
        var armed = new PlayerObjectPoe2(200, 200, 58, Visible: [Poe2Items.Visible(WoodenClub)]).Blob();

        // Visual slot 0, type One Hand Mace + 1, its visual, no stance, then the three trailing bytes.
        string record = "00" + "0D" + "85CE" + "00000000" + "00" + "0000" + "00000000" + "00" + "FF" + "85CE" + "000000";
        Assert.Equal(PlayerObjectPoe2.BlobLength, bare.Length);
        Assert.Equal(bare.Length + record.Length / 2, armed.Length);
        Assert.Contains("01" + record + "00", Convert.ToHexString(armed));
    }

    [Fact]
    public void The_actor_gives_the_main_hand_the_weapons_type_for_skill_requirements()
    {
        var armed = new PlayerObjectPoe2(200, 200, 58, Visible: [Poe2Items.Visible(WoodenClub)]).Blob();
        var bare = new PlayerObjectPoe2(200, 200, 58).Blob();

        // The Actor's first (main, off, name) entry follows the Inventories component's closing count.
        string record = "00" + "0D" + "85CE" + "00000000" + "00" + "0000" + "00000000" + "00" + "FF" + "85CE" + "000000";
        Assert.Contains(record + "00" + "0D" + "00" + "00000000", Convert.ToHexString(armed));
        Assert.Contains("00" + "00" + "00" + "00000000" + "00" + "00" + "00000000", Convert.ToHexString(bare));
    }

    [Fact]
    public void Entering_puts_the_weapon_in_the_main_hand_after_the_empty_inventories()
    {
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(200, 200, 58), weapon: WoodenClub);

        var enter = session.EnterWorld();

        var entry = session.Weapon1.Single();
        var change = InstanceInventoriesPoe2.InventoryChange(InstanceInventoriesPoe2.InventoryId(InstanceSessionPoe2.Weapon1Row), 1, [],
            [new InventoryItem(entry.Id, 0, 0, Poe2Items.Create(WoodenClub))]);
        Assert.Equal(3, enter.ToList().FindIndex(m => m.SequenceEqual(change)));
    }

    [Fact]
    public void A_weapon_also_grants_its_own_attack_as_a_weapon_set_one_skill()
    {
        var skills = Poe2StartingSkills.For(Warrior, WoodenClub);

        Assert.Equal(3, skills.Count);
        var mace = skills[2];
        Assert.Equal((Poe2StartingSkills.WeaponAttackId, 400u, 400u, Poe2StartingSkills.WeaponSetOneSource),
            (mace.Id, mace.EffectsPerLevelRow, mace.StatSetsPerLevelRow, mace.Source));
        Assert.Equal(AttackPoe2.Of(skills[0]), AttackPoe2.Of(mace));
        Assert.Equal(2, Poe2StartingSkills.For(Warrior).Count);
        // The record's second byte carries the source.
        var w = new BlobWriter();
        ActorSkillsPoe2.WriteRecord(w, mace);
        Assert.Equal("04010000" + "40", Convert.ToHexString(w.ToArray(), 0, 5));
    }

    [Fact]
    public void Entering_with_a_weapon_sends_the_actors_weapon_records_after_the_skill_list()
    {
        var armed = new InstanceSessionPoe2(new PlayerObjectPoe2(200, 200, 58, Visible: [Poe2Items.Visible(WoodenClub)]), weapon: WoodenClub);
        var bare = new InstanceSessionPoe2(new PlayerObjectPoe2(200, 200, 58));

        var enter = armed.EnterWorld().ToList();

        int skills = enter.FindIndex(m => m[0] == 0 && m[1] == InstanceProtocolPoe2.SkillListId);
        Assert.Equal(InstanceSkillsPoe2.WeaponRecords(armed.PlayerId, 13), enter[skills + 1]);
        Assert.DoesNotContain(bare.EnterWorld(), m => m[0] == 0x03 && m[1] == 0x24);
    }
}
