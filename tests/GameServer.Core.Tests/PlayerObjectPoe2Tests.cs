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

        blob[34] = 0;
        Assert.All(blob, b => Assert.Equal(0, b));
    }
}
