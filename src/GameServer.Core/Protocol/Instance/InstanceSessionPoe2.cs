using System.Buffers.Binary;
using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// One PoE2 client on the instance connection, after the area message (PORTING-POE2.md, waves 9–13): turns the
/// client's decrypted messages into the server's answers. The Gateway does the I/O and ends a server frame
/// (<see cref="InstanceProtocolPoe2.FrameEnd"/>) every <see cref="FrameMs"/> once <see cref="InWorld"/> is set.
/// </summary>
public sealed class InstanceSessionPoe2
{
    public const uint DefaultPlayerId = 0x1000;
    public const string DefaultObjectType = "Metadata/Characters/Int/IntFour";
    public const byte FrameMs = 33;

    /// <summary>A cell inside Clearfell Encampment (G1_town) where the player stood live; the server has no PoE2 area
    /// layouts to pick a spawn from yet.</summary>
    public static readonly (int X, int Y) G1TownSpawn = (200, 200);

    // C→S messages of a fixed length (id included), from live captures (2026-11-03). 0x63 is parsed; any other id stops
    // the split, as the stream has no length prefix.
    static readonly Dictionary<ushort, int> FixedLengths = new()
    {
        [0x1c] = 7, [0x5c] = 8, [0x62] = 4, [InstanceProtocolPoe2.AreaGeneratedId] = 11,
        [0x17a] = 3, [0x221] = 3, [0x2e6] = 4, [0x357] = 3,
    };

    private readonly PlayerObjectPoe2 _player;
    private readonly string _objectType;

    public InstanceSessionPoe2(PlayerObjectPoe2 player, string objectType = DefaultObjectType, uint playerId = DefaultPlayerId,
        double walkSpeed = DirectionWalk.DefaultSpeed)
    {
        (_player, _objectType, PlayerId) = (player, objectType, playerId);
        Walk = new DirectionWalk(player.GridX, player.GridY, walkSpeed);
    }

    public uint PlayerId { get; }
    public DirectionWalk Walk { get; }

    /// <summary>Set once the player is sent (after the client's 0x9a): from then on the server ends frames.</summary>
    public bool InWorld { get; private set; }

    /// <summary>The answers to one client message at <paramref name="now"/> (seconds, monotonic); empty for the
    /// messages the server does not answer.</summary>
    public IReadOnlyList<byte[]> Handle(ReadOnlySpan<byte> message, double now)
    {
        if (!InWorld && InstanceProtocolPoe2.ReadAreaGenerated(message) is not null)
        {
            InWorld = true;
            return EnterWorld();
        }
        if (InWorld && InstanceMovementPoe2.ReadDirectionMove(message, out _) is { } move)
        {
            // Outside lockstep the client reports its own cell: start from it instead of the estimate.
            if (move.X != 0 || move.Y != 0) Walk.Place(move.X, move.Y, now);
            var (x, y) = Walk.Turn(move.Dx, move.Dy, now);
            return [InstanceMovementPoe2.DirectionMoveEcho(PlayerId, move.Dx, move.Dy, x, y, move.Flag != 0)];
        }
        return [];
    }

    /// <summary>The local player (0x24), the player object (0x2f7), its inventories (0xdf), enter area (0x23) and the
    /// frame length (0x2f5), in the order the client accepted live.</summary>
    public IReadOnlyList<byte[]> EnterWorld() =>
    [
        InstanceProtocolPoe2.SetLocalPlayer(PlayerId),
        InstanceProtocolPoe2.CreateObject(PlayerId, InstanceEntities.ObjectTypeHash(_objectType), _player.Blob()),
        [.. InstanceInventoriesPoe2.PlayerDefaults.SelectMany(slot =>
            InstanceInventoriesPoe2.AddInventory(InstanceInventoriesPoe2.InventoryId(slot.Row), slot))],
        InstanceProtocolPoe2.EnterArea(PlayerId),
        InstanceProtocolPoe2.FrameLength(FrameMs),
    ];

    /// <summary>Whether <see cref="Split"/> knows the length of messages with this id.</summary>
    public static bool CanFrame(ushort id) => FixedLengths.ContainsKey(id) || id == InstanceMovementPoe2.DirectionMoveId;

    /// <summary>
    /// Splits decrypted client bytes into messages: the fixed-length ids above and 0x63. Stops at the first other id
    /// or at a message cut off at the end; <paramref name="rest"/> is where it stopped (the length when all was split).
    /// </summary>
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> data, out int rest)
    {
        var messages = new List<byte[]>();
        int at = 0;
        while (data.Length - at >= 2)
        {
            var tail = data[at..];
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(tail);
            int length;
            if (FixedLengths.TryGetValue(id, out int fixedLength)) length = fixedLength <= tail.Length ? fixedLength : 0;
            else if (id == InstanceMovementPoe2.DirectionMoveId) InstanceMovementPoe2.ReadDirectionMove(tail, out length);
            else length = 0;
            if (length == 0) break;
            messages.Add(tail[..length].ToArray());
            at += length;
        }
        rest = at;
        return messages;
    }
}
