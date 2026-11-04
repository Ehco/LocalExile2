using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>PoE2 monsters: their types, blob and placement (PORTING-POE2.md, wave 20).</summary>
public class MonsterPoe2Tests
{
    const string Unarmed = "Metadata/Monsters/Zombies/CourtGuardZombieUnarmed";

    static readonly string[] ZombieComponents = [.. MonsterObjectPoe2.BaseComponents, "Transitionable", "StateMachine"];

    static MonsterObjectPoe2 Zombie(string? model = null, string[]? states = null) =>
        new(1158, 1326, 30, ZombieComponents, Rotation: 1.5f, Model: model, SharedStates: states ?? ["dead"]);

    [Fact]
    public void A_zombie_blob_is_the_310_bytes_the_client_read_live()
    {
        // Live 2026-11-04: each CourtGuardZombie blob was read 310/310, with its 59-character model.
        const string model = "Metadata/Monsters/Zombies/CourtGuards/CourtGuardZombie01.ao";
        Assert.Equal(188, Zombie().Blob().Length);
        Assert.Equal(310, Zombie(model).Blob().Length);
    }

    [Fact]
    public void The_blob_puts_flags_position_and_life_where_the_client_reads_them()
    {
        byte[] blob = Zombie().Blob();

        Assert.Equal(0x02, blob[0]);                                                 // in the wake zone
        Assert.Equal(0, blob[1]);                                                    // no base data
        Assert.Equal(1158, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(2)));
        Assert.Equal(1326, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(6)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(10)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(14)));  // team
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(32))); // Life's pool
    }

    [Fact]
    public void The_state_machine_writes_one_entry_per_shared_state()
    {
        byte[] none = Zombie(states: []).Blob();
        byte[] two = Zombie(states: ["dead", "emerged"]).Blob();

        Assert.Equal(none.Length + 4, two.Length);
        Assert.Equal("0200" + "0000" + "0000" + "00", Convert.ToHexString(two[^7..]));  // counts, two states, tasks
    }

    [Fact]
    public void Only_the_base_components_and_known_extras_can_be_written()
    {
        Assert.True(MonsterObjectPoe2.CanSerialize(MonsterObjectPoe2.BaseComponents));
        Assert.True(MonsterObjectPoe2.CanSerialize(ZombieComponents));
        Assert.False(MonsterObjectPoe2.CanSerialize([.. MonsterObjectPoe2.BaseComponents, "Chest"]));
        Assert.False(MonsterObjectPoe2.CanSerialize(MonsterObjectPoe2.BaseComponents[1..]));
        Assert.Throws<ArgumentException>(() => new MonsterObjectPoe2(0, 0, 1, ["Positioned"]).Blob());
    }

    [Fact]
    public void Monster_types_are_rows_then_levels()
    {
        byte[] message = InstanceProtocolPoe2.MonsterTypes([(1735, 2), (1736, 2)]);

        Assert.Equal("0021" + "00" + "0002" + "0002" + "C706" + "C806" + "02" + "02", Convert.ToHexString(message));
        Assert.Equal(Unarmed + "@2", MonsterObjectPoe2.TypePath(Unarmed, 2));
    }

    [Fact]
    public void The_area_capture_places_the_varieties_the_server_can_write()
    {
        var varieties = new Dictionary<string, Poe2Monsters.Variety>(StringComparer.OrdinalIgnoreCase)
        {
            [Unarmed] = new(Unarmed, 1736, ZombieComponents, ["dead"]),
            ["Metadata/Monsters/Odd"] = new("Metadata/Monsters/Odd", 7, ["Positioned", "Chest"]),
        };
        Poe2Areas.PlacedObject[] objects =
        [
            new(Unarmed, "zombie.ao", "campsite_03.arm", 1158, 1326, 0.5f),
            new("Metadata/Monsters/Odd", null, null, 1, 2, 0),
            new("Metadata/Monsters/MonsterMods/SomeController", null, null, 3, 4, 0),
            new("Metadata/Chests/CampsiteChest", null, null, 5, 6, 0),
        ];
        var notes = new List<string>();

        var monsters = Poe2Monsters.InArea(objects, varieties, level: 2, life: 30, notes);

        var zombie = Assert.Single(monsters);
        Assert.Equal((Unarmed, (ushort)1736, (byte)2), (zombie.VarietyId, zombie.Row, zombie.Level));
        Assert.Equal((1158, 1326, 0.5f, "zombie.ao"), (zombie.Object.GridX, zombie.Object.GridY, zombie.Object.Rotation, zombie.Object.Model));
        Assert.Equal(["dead"], zombie.Object.SharedStates!);
        Assert.Contains("Odd", Assert.Single(notes));
    }

    [Fact]
    public void Entering_the_world_registers_each_type_once_then_creates_the_monsters_after_the_exits()
    {
        var zombie = new Poe2Monster(Unarmed, 1736, 2, Zombie());
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 1150, GridY: 1300, Life: 58),
            exits: [new AreaTransitionObjectPoe2(1001, 474, 0x3f70)], monsters: [zombie, zombie]);

        var sent = session.EnterWorld();

        Assert.Equal(["0021", "02F7", "02F7"], sent.Skip(7).Select(m => Convert.ToHexString(m, 0, 2)));
        Assert.Equal(InstanceProtocolPoe2.MonsterTypes([(1736, 2)]), sent[7]);
        Assert.Equal(InstanceProtocolPoe2.CreateObject(session.MonsterId(1),
            InstanceEntities.ObjectTypeHash(Unarmed + "@2"), zombie.Object.Blob()), sent[9]);
        Assert.Equal(session.ExitId(0) + 2, session.MonsterId(1));
    }

    [Fact]
    public void Pause_requests_are_answered_with_the_pause_state()
    {
        var session = new InstanceSessionPoe2(new PlayerObjectPoe2(GridX: 0, GridY: 0, Life: 1));

        var paused = session.Handle(Convert.FromHexString("01FF0001"), 0);
        var resumed = session.Handle(Convert.FromHexString("01FF0000"), 1);

        Assert.Equal("01FE" + "0000000000000000000000" + "03", Convert.ToHexString(paused.Single()));
        Assert.Equal(InstanceProtocolPoe2.PauseState(false), resumed.Single());
        Assert.Equal(2, InstanceSessionPoe2.Split(Convert.FromHexString("01FF0001" + "023D0002"), out _).Count);
    }
}
