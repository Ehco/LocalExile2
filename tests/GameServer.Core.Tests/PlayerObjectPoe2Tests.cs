using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

/// <summary>The PoE2 player component blob (PORTING-POE2.md, wave 10).</summary>
public class PlayerObjectPoe2Tests
{
    [Fact]
    public void Blob_is_the_242_bytes_the_client_reads_for_a_character()
    {
        // Frida 2026-11-03: the client's reader consumed exactly 242 bytes of a character blob with life above zero.
        byte[] blob = new PlayerObjectPoe2(GridX: 200, GridY: 200, Life: 100).Blob();

        Assert.Equal(PlayerObjectPoe2.BlobLength, blob.Length);
        Assert.Equal(242, blob.Length);
    }

    [Fact]
    public void Blob_puts_position_life_energy_shield_and_mana_where_the_client_reads_them()
    {
        byte[] blob = new PlayerObjectPoe2(GridX: 200, GridY: -3, Life: 100, EnergyShield: 50, Mana: 30).Blob();

        Assert.Equal(200, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(2)));
        Assert.Equal(-3, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(6)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(34)));
        Assert.Equal(50u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(66)));
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(98)));
        Assert.Equal(PlayerObjectPoe2.BlobLength, blob.Length);
    }

    [Fact]
    public void Everything_else_is_zero()
    {
        byte[] blob = new PlayerObjectPoe2(GridX: 0, GridY: 0, Life: 1).Blob();

        Assert.Equal(0x02, blob[0]);                    // in the wake zone, or not drawn nor updated outside towns
        blob[0] = 0;
        blob[34] = 0;
        blob[PlayerObjectPoe2.PlayerOffset + 4] = 0;   // the Witch's row, 1
        Assert.All(blob, b => Assert.Equal(0, b));
    }

    [Fact]
    public void The_player_component_starts_with_the_name_the_class_row_and_the_experience()
    {
        byte[] blob = new PlayerObjectPoe2(GridX: 0, GridY: 0, Life: 1, Name: "Ilse", ClassRow: 9, Experience: 525).Blob();

        int at = PlayerObjectPoe2.PlayerOffset;
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at)));
        Assert.Equal("Ilse", System.Text.Encoding.Unicode.GetString(blob, at + 4, 8));
        Assert.Equal(9, blob[at + 12]);
        Assert.Equal(525u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at + 13)));
        Assert.Equal(PlayerObjectPoe2.BlobLength + 8, blob.Length);
    }

    [Fact]
    public void The_stats_follow_positioned_as_compact_pairs()
    {
        var stats = new List<(uint Key, int Value)> { (Poe2CharacterStats.MaximumLife, 42), (Poe2CharacterStats.Strength, 7) };
        byte[] expected = new BlobWriter().Stats(stats).ToArray();

        byte[] blob = new PlayerObjectPoe2(GridX: 0, GridY: 0, Life: 42, Stats: stats).Blob();

        // Object flags, base data count and Positioned's 17 bytes come first; the empty list was the one count byte.
        Assert.Equal(expected, blob[19..(19 + expected.Length)]);
        Assert.Equal(PlayerObjectPoe2.BlobLength + expected.Length - 1, blob.Length);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(34 + expected.Length - 1)));
    }

    [Fact]
    public void The_player_component_follows_animated()
    {
        // Positioned 17, Stats 13, Buffs 2, Life 125 and Animated 1 after the object flags and base data count.
        Assert.Equal(2 + 17 + 13 + 2 + 125 + 1, PlayerObjectPoe2.PlayerOffset);
    }
}
