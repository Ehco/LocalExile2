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

    /// <summary>The middle of Clearfell Encampment (G1_town): the walkable cell farthest from any blocked one in the
    /// client's walk map (43 cells, captured 2026-11-03), like PoE1's town spawns. The server has no PoE2 area
    /// layouts to read a start point from yet.</summary>
    public static readonly (int X, int Y) G1TownSpawn = (296, 262);

    /// <summary>Whether the PoE2 player walks on a cell of a walk map (1 byte per cell, row by row): on any value from 1.
    /// PoE1's player needs 2 (its size 1 + 1); live 2026-11-03 the PoE2 player stood on 1s (56 of 230 samples) and
    /// never on a 0, and a held walk stopped where the 1s end.</summary>
    public static Func<int, int, bool> Walkable(byte[] cells, int width, int height) =>
        (x, y) => x >= 0 && y >= 0 && x < width && y < height && cells[y * width + x] >= 1;

    // C→S messages of a fixed length (id included), from live captures (2026-11-03). 0x63 is parsed; any other id stops
    // the split, as the stream has no length prefix.
    static readonly Dictionary<ushort, int> FixedLengths = new()
    {
        [0x1c] = 7, [0x5c] = 8, [0x62] = 4, [InstanceProtocolPoe2.AreaGeneratedId] = 11, [0xbc] = 5, [0x13d] = 3,
        [0x173] = 3, [0x17a] = 3, [0x1ac] = 3,
        [0x221] = 3, [0x23d] = 4, [InstanceProtocolPoe2.PauseRequestId] = 4, [0x2e6] = 4, [0x357] = 3,
        [InstanceProtocolPoe2.LeavingId] = 3,
    };

    /// <summary>How near (in cells) the walk estimate must come to an exit to take it. The client's click on an exit
    /// is not mapped yet (the exits are not targetable), so walking into the gate takes it.</summary>
    public const int ExitReach = 6;

    private readonly PlayerObjectPoe2 _player;
    private readonly string _objectType;
    private readonly IReadOnlyList<AreaTransitionObjectPoe2> _exits;
    private readonly IReadOnlyList<Poe2Monster> _monsters;

    /// <param name="walkable">The area's walkability (cells the client walks on), when the server has its walk map;
    /// without it the walk estimate goes through walls.</param>
    public InstanceSessionPoe2(PlayerObjectPoe2 player, string objectType = DefaultObjectType, uint playerId = DefaultPlayerId,
        double walkSpeed = DirectionWalk.DefaultSpeed, Func<int, int, bool>? walkable = null,
        IReadOnlyList<AreaTransitionObjectPoe2>? exits = null, IReadOnlyList<Poe2Monster>? monsters = null)
    {
        (_player, _objectType, PlayerId, _exits, _monsters) = (player, objectType, playerId, exits ?? [], monsters ?? []);
        Walk = new DirectionWalk(player.GridX, player.GridY, walkSpeed, walkable);
    }

    public uint PlayerId { get; }
    public DirectionWalk Walk { get; }

    /// <summary>The exit the player took (the change area was sent), or null.</summary>
    public AreaTransitionObjectPoe2? LeavingBy { get; private set; }

    /// <summary>Set when the client answered the change area with C→S <see cref="InstanceProtocolPoe2.LeavingId"/>:
    /// the server can send it to the destination.</summary>
    public bool LeaveAcknowledged { get; private set; }

    // Exits are taken only once the player has been out of reach of all of them (an arrival can be beside one).
    private bool _awayFromExits;
    private readonly object _lock = new();

    /// <summary>Set once the player is sent (after the client's 0x9a): from then on the server ends frames.</summary>
    public bool InWorld { get; private set; }

    /// <summary>The answers to one client message at <paramref name="now"/> (seconds, monotonic); empty for the
    /// messages the server does not answer.</summary>
    public IReadOnlyList<byte[]> Handle(ReadOnlySpan<byte> message, double now)
    {
        lock (_lock) return HandleLocked(message, now);
    }

    private IReadOnlyList<byte[]> HandleLocked(ReadOnlySpan<byte> message, double now)
    {
        if (LeavingBy is not null && message.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(message) == InstanceProtocolPoe2.LeavingId)
        {
            LeaveAcknowledged = true;
            return [];
        }
        if (!InWorld && InstanceProtocolPoe2.ReadAreaGenerated(message) is not null)
        {
            InWorld = true;
            return EnterWorld();
        }
        // Outside towns the client asks to pause while it loads the area, then to resume: answer with the state, as
        // PoE1's server answers 0x1d4 with 0x1d3.
        if (InstanceProtocolPoe2.ReadPauseRequest(message) is { } pause) return [InstanceProtocolPoe2.PauseState(pause)];
        if (InWorld && InstanceMovementPoe2.ReadDirectionMove(message, out _) is { } move)
        {
            // Outside lockstep the client reports its own cell: start from it instead of the estimate.
            if (move.X != 0 || move.Y != 0) Walk.Place(move.X, move.Y, now);
            var (x, y) = Walk.Turn(move.Dx, move.Dy, now);
            return [InstanceMovementPoe2.DirectionMoveEcho(PlayerId, move.Dx, move.Dy, x, y, move.Flag != 0)];
        }
        if (InWorld && InstanceProtocolPoe2.ReadSetSkillSlot(message) is { } bind) return BindSlot(bind.Slot, bind.SkillId);
        if (InWorld && InstanceSkillsPoe2.ReadUseSkill(message, out _) is { TargetId: { } target } use && SkillIds.Contains(use.SkillId))
        {
            // An attack on an object waits for the server, as in PoE1: start it from where the player is; the action
            // walks into range itself.
            var (x, y) = Walk.Cell(now);
            return [InstanceSkillsPoe2.ActionStarted(PlayerId, x, y, use.X, use.Y, use.SkillId, use.Flags, target)];
        }
        return [];
    }

    // Skill bar: slot → skill id, as the client set it (C→S 0x5c).
    private readonly Dictionary<byte, uint> _slots = [];

    /// <summary>The slots holding each skill, as the skill list's mask.</summary>
    private ulong SlotMask(uint skillId) =>
        _slots.Where(s => s.Value == skillId).Aggregate(0UL, (mask, s) => mask | 1UL << s.Key);

    private IReadOnlyList<byte[]> BindSlot(byte slot, uint skillId)
    {
        if (slot >= InstanceProtocolPoe2.SkillBarSlots) return [];
        var changed = new List<uint>();
        if (_slots.TryGetValue(slot, out uint old)) { _slots.Remove(slot); changed.Add(old); }
        if (skillId != 0 && SkillIds.Contains(skillId)) { _slots[slot] = skillId; changed.Add(skillId); }
        return changed.Count == 0 ? [] :
            [InstanceProtocolPoe2.SkillList([.. changed.Distinct().Select(id => (id, SlotMask(id)))], merge: true)];
    }

    private IEnumerable<uint> SkillIds => (_player.Skills ?? []).Select(s => s.Id);

    /// <summary>The server's per-frame work at <paramref name="now"/>: the change area (0x2f1) when the walk reaches an
    /// exit. Empty otherwise, and after the player has left.</summary>
    public IReadOnlyList<byte[]> Tick(double now)
    {
        lock (_lock)
        {
            if (!InWorld || LeavingBy is not null || _exits.Count == 0) return [];
            var (x, y) = Walk.Cell(now);
            var near = _exits.FirstOrDefault(e => Math.Abs(e.GridX - x) <= ExitReach && Math.Abs(e.GridY - y) <= ExitReach);
            if (near is null) { _awayFromExits = true; return []; }
            if (!_awayFromExits) return [];
            LeavingBy = near;
            return [InstanceProtocolPoe2.ChangeArea(near.DestinationHash16)];
        }
    }

    /// <summary>The local player (0x24), the player object (0x2f7), its inventories (0xdf), enter area (0x23) and the
    /// frame length (0x2f5), in the order the client accepted live, and the skills the skill bar offers (0x66); then the
    /// area's exits, and its monsters after their types (0x21).</summary>
    public IReadOnlyList<byte[]> EnterWorld() =>
    [
        InstanceProtocolPoe2.SetLocalPlayer(PlayerId),
        InstanceProtocolPoe2.CreateObject(PlayerId, InstanceEntities.ObjectTypeHash(_objectType), _player.Blob()),
        [.. InstanceInventoriesPoe2.PlayerDefaults.SelectMany(slot =>
            InstanceInventoriesPoe2.AddInventory(InstanceInventoriesPoe2.InventoryId(slot.Row), slot))],
        InstanceProtocolPoe2.EnterArea(PlayerId),
        InstanceProtocolPoe2.FrameLength(FrameMs),
        InstanceProtocolPoe2.SkillList([.. SkillIds.Select(id => (id, SlotMask(id)))]),
        .. _exits.Select((exit, i) => InstanceProtocolPoe2.CreateObject(ExitId(i),
            InstanceEntities.ObjectTypeHash(AreaTransitionObjectPoe2.MetadataPath), exit.Blob())),
        .. MonsterMessages(),
    ];

    private IEnumerable<byte[]> MonsterMessages()
    {
        if (_monsters.Count == 0) yield break;
        yield return InstanceProtocolPoe2.MonsterTypes([.. _monsters.Select(m => (m.Row, m.Level)).Distinct()]);
        for (int i = 0; i < _monsters.Count; i++)
            yield return InstanceProtocolPoe2.CreateObject(MonsterId(i), InstanceEntities.ObjectTypeHash(_monsters[i].TypePath),
                _monsters[i].Object.Blob());
    }

    /// <summary>The entity id of the area's i-th exit.</summary>
    public uint ExitId(int i) => PlayerId + 1 + (uint)i;

    /// <summary>The entity id of the area's i-th monster, after the exits.</summary>
    public uint MonsterId(int i) => ExitId(_exits.Count) + (uint)i;

    /// <summary>Whether <see cref="Split"/> knows the length of messages with this id.</summary>
    public static bool CanFrame(ushort id) =>
        FixedLengths.ContainsKey(id) || id == InstanceMovementPoe2.DirectionMoveId || id == InstanceSkillsPoe2.UseSkillId;

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
            else if (id == InstanceSkillsPoe2.UseSkillId) InstanceSkillsPoe2.ReadUseSkill(tail, out length);
            else length = 0;
            if (length == 0) break;
            messages.Add(tail[..length].ToArray());
            at += length;
        }
        rest = at;
        return messages;
    }
}
