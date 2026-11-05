using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// PoE2 experience (PORTING-POE2.md, wave 24). PoE2 has no message of its own for it, as PoE1's 0x2ba: the server
/// updates the player's Player component.
/// </summary>
public static class InstanceProgressionPoe2
{
    public const ushort ComponentUpdateId = 0x2f9;

    /// <summary>The Player component's index in a player's component list (Positioned 0, Stats 1, Pathfinding 2,
    /// Buffs 3, Life 4, Animated 5, Player 6).</summary>
    public const byte PlayerComponent = 6;

    /// <summary>The Player update's flag for the experience (reader 0x141dd0b90, setter 0x141dd1360).</summary>
    public const ushort HasExperience = 0x0001;

    /// <summary>
    /// S→C 0x2f9 (world group, read 0x141f6c8d0): object header, compact length, then that many bytes: the component's
    /// index and its update. The world handler (case at 0x142136123) calls that component's vtable +0xb0 with the rest.
    /// </summary>
    public static byte[] ComponentUpdate(uint objectId, byte component, ReadOnlySpan<byte> update)
    {
        var w = new LoginWriter().U16(ComponentUpdateId).U32(objectId).U32(0).U8(0).U16(0);
        InstanceProtocol.CompactUInt(w, (uint)update.Length + 1);
        return w.U8(component).Bytes(update).ToArray();
    }

    /// <summary>
    /// The player's experience: a Player update of u16 flags (little-endian, read raw) with <see cref="HasExperience"/>,
    /// then the total experience as a raw u32. The client derives the level from it, as at the player's creation.
    /// </summary>
    public static byte[] PlayerExperience(uint playerId, uint experience)
    {
        var update = new LoginWriter().U8((byte)HasExperience).U8(HasExperience >> 8).RawU32(experience).ToArray();
        return ComponentUpdate(playerId, PlayerComponent, update);
    }
}
