using GameServer.Core.Accounts;
using System.Buffers.Binary;
using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// One PoE2 client on the instance connection, after the area message (PORTING-POE2.md, waves 9–13): turns the
/// client's decrypted messages into the server's answers. The Gateway does the I/O and ends a server frame
/// (<see cref="InstanceProtocolPoe2.FrameEnd"/>) every <see cref="FrameMs"/> once <see cref="InWorld"/> is set.
/// </summary>
public sealed partial class InstanceSessionPoe2
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
        [0x1c] = 7, [0x5c] = 8, [0x62] = 4, [InstanceCombatPoe2.ResurrectId] = 4, [InstanceProtocolPoe2.AreaGeneratedId] = 11, [0xbc] = 5, [0x13d] = 3,
        [InstanceNpcsPoe2.ChooseTalkId] = 4, [InstanceNpcsPoe2.CloseTalkId] = 3, [InstanceWaypointsPoe2.TravelId] = InstanceWaypointsPoe2.TravelLength,
        [InstancePassivesPoe2.AllocateId] = InstancePassivesPoe2.AllocateLength, [InstancePassivesPoe2.RefundId] = InstancePassivesPoe2.RefundLength,
        [InstancePassivesPoe2.RefundAllId] = InstancePassivesPoe2.RefundAllLength, [InstancePassivesPoe2.ChooseId] = InstancePassivesPoe2.ChooseLength,
        [InstancePassivesPoe2.ChooseAltId] = InstancePassivesPoe2.ChooseLength, [InstancePassivesPoe2.ConfirmId] = InstancePassivesPoe2.ConfirmLength,
        [InstancePassivesPoe2.ConfirmAltId] = InstancePassivesPoe2.ConfirmLength, [0x16] = 7, [0x137] = 4,
        [InstanceGemsPoe2.CutGemId] = InstanceGemsPoe2.CutGemLength, [InstanceGemsPoe2.CutGemIntoSlotId] = InstanceGemsPoe2.CutGemIntoSlotLength,
        [InstanceItemsPoe2.PickUpId] = InstanceItemsPoe2.PickUpLength, [InstanceItemsPoe2.PlaceId] = InstanceItemsPoe2.PlaceLength,
        [InstanceFlasksPoe2.UseFlaskId] = InstanceFlasksPoe2.UseFlaskLength, [InstanceSkillsPoe2.AimId] = InstanceSkillsPoe2.AimLength,
        [0x173] = 3, [0x17a] = 3, [0x1ac] = 3,
        [0x221] = 3, [0x23d] = 4, [InstanceProtocolPoe2.PauseRequestId] = 4, [0x2e6] = 4, [0x357] = 3,
        [InstanceProtocolPoe2.LeavingId] = 3,
    };

    /// <summary>How near (in cells) the walk estimate must come to an exit to take it. The client's click on an exit
    /// is not mapped yet (the exits are not targetable), so walking into the gate takes it.</summary>
    public const int ExitReach = 6;

    private PlayerObjectPoe2 _player;
    private readonly string _objectType;
    private readonly IReadOnlyList<AreaTransitionObjectPoe2> _exits;
    private readonly IReadOnlyList<Poe2Monster> _monsters;
    private readonly int[] _monsterLife;
    private readonly Random _random;

    /// <param name="walkable">The area's walkability (cells the client walks on), when the server has its walk map;
    /// without it the walk estimate goes through walls.</param>
    /// <param name="levelStarts">Each level's starting experience (ExperienceLevels, index = level − 1); without it the
    /// player stays level 1.</param>
    /// <param name="onLevel">Called with the new level when a kill levels the player up (under the session's lock).</param>
    /// <param name="itemTypes">The item types monsters drop (<see cref="Poe2Items"/>); without them nothing drops.</param>
    /// <param name="serverObjects">The area's NPCs, waypoint and other server objects (<see cref="World.Poe2ServerObjects"/>);
    /// their states are set for the character's quest flags.</param>
    /// <param name="questFlags">The character's quest flags (QuestFlags Ids) and <paramref name="waypoints"/> its activated
    /// waypoints (WorldAreas Ids), from its save; <paramref name="onQuestFlags"/> and <paramref name="onWaypoints"/> get
    /// each new whole list to save (under the session's lock).</param>
    /// <param name="quests">The quest tables (<see cref="Poe2Quests"/>); without them flags are kept but not sent and no
    /// waypoint is known.</param>
    /// <param name="area">The area the session serves: its entry flags are set and, in a town, its waypoint activated.</param>
    /// <param name="passives">The character's allocated passive nodes (<see cref="Poe2Passives.Encode"/>), from its save;
    /// <paramref name="onPassives"/> gets each new whole list to save; <paramref name="passiveNodes"/> gives their stats
    /// and the refund's cost, paid from <paramref name="gold"/>, the character's gold; <paramref name="onProgress"/> gets
    /// the total experience and the gold whenever either changes (a kill, a gold pile taken, a refund), to save.</param>
    /// <param name="gems">The character's socketed skill gems (<see cref="Poe2Gems.Encode"/>), from its save;
    /// <paramref name="onGems"/> gets each new whole list to save; <paramref name="gemTable"/> gives their skills;
    /// <paramref name="startingGem"/> sockets the class's starting gem for a character with no gem (as PoE2 does);
    /// <paramref name="startingUncutGem"/> puts an Uncut Skill Gem (Level 1) in its inventory as well (for tests).</param>
    /// <param name="items">The character's saved inventory (<see cref="SavedItem"/>); <paramref name="onItems"/> gets the
    /// whole inventory to save after each change (a pickup, a move, a gem cut). <paramref name="startingFlasks"/> gives a
    /// character without flasks the two a new PoE2 character has.</param>
    public InstanceSessionPoe2(PlayerObjectPoe2 player, string objectType = DefaultObjectType, uint playerId = DefaultPlayerId,
        double walkSpeed = DirectionWalk.DefaultSpeed, Func<int, int, bool>? walkable = null,
        IReadOnlyList<AreaTransitionObjectPoe2>? exits = null, IReadOnlyList<Poe2Monster>? monsters = null,
        Random? random = null, IReadOnlyList<uint>? levelStarts = null, Action<int>? onLevel = null,
        IReadOnlyDictionary<string, Poe2Items.ItemType>? itemTypes = null, Poe2Items.ItemType? weapon = null,
        IReadOnlyList<ServerObjectPoe2>? serverObjects = null, IEnumerable<string>? questFlags = null,
        IEnumerable<string>? waypoints = null, Poe2Quests? quests = null, Poe2Areas.Area? area = null,
        Action<IReadOnlyCollection<string>>? onQuestFlags = null, Action<IReadOnlyCollection<string>>? onWaypoints = null,
        IEnumerable<int>? passives = null, Action<IReadOnlyCollection<int>>? onPassives = null, ushort questPassivePoints = 0,
        Poe2Passives? passiveNodes = null, uint gold = 0, Action<uint, uint>? onProgress = null,
        IEnumerable<int>? gems = null, Action<IReadOnlyCollection<int>>? onGems = null, Poe2Gems? gemTable = null, bool startingGem = false,
        bool startingUncutGem = false, IEnumerable<SavedItem>? items = null, Action<IReadOnlyList<SavedItem>>? onItems = null,
        bool startingFlasks = false)
    {
        _onProgress = onProgress;
        (_player, _objectType, PlayerId, _exits, _monsters) = (player, objectType, playerId, exits ?? [], monsters ?? []);
        InitQuests(serverObjects, questFlags, waypoints, quests, area, onQuestFlags, onWaypoints);
        InitPassives(passives, questPassivePoints, passiveNodes, onPassives, gold);
        Walk = new DirectionWalk(player.GridX, player.GridY, walkSpeed, walkable);
        _walkSpeed = walkSpeed;
        _monsterLife = [.. _monsters.Select(m => (int)m.Object.Life)];
        _random = random ?? Random.Shared;
        InitExperience(levelStarts, onLevel);
        InitLoot(itemTypes, weapon);
        InitItems(items, onItems);
        InitFlasks(startingFlasks);
        InitGems(gems, gemTable, onGems, startingGem, startingUncutGem);
        InitMonsters();
    }

    private readonly double _walkSpeed;

    public uint PlayerId { get; }
    public DirectionWalk Walk { get; }

    /// <summary>The exit the player took (the change area was sent), or null; null too when it travels by waypoint
    /// (<see cref="LeavingTo"/> is set either way).</summary>
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
        if (LeavingTo is not null && message.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(message) == InstanceProtocolPoe2.LeavingId)
        {
            LeaveAcknowledged = true;
            return [];
        }
        if (!InWorld && InstanceProtocolPoe2.ReadAreaGenerated(message) is not null)
        {
            InWorld = true;
            ArriveMonsters(now);
            return EnterWorld();
        }
        // Outside towns the client asks to pause while it loads the area, then to resume: answer with the state, as
        // PoE1's server answers 0x1d4 with 0x1d3.
        if (InstanceProtocolPoe2.ReadPauseRequest(message) is { } pause)
        {
            PauseMonsters(pause, now);
            return [InstanceProtocolPoe2.PauseState(pause)];
        }
        if (InWorld && _playerLife <= 0 && InstanceCombatPoe2.IsResurrect(message)) return Resurrect(now);
        // A dead player neither walks nor attacks (until resurrected).
        if (InWorld && _playerLife <= 0 && (InstanceMovementPoe2.ReadDirectionMove(message, out _) is not null ||
            InstanceSkillsPoe2.ReadUseSkill(message, out _) is not null)) return [];
        if (InWorld && InstanceMovementPoe2.ReadDirectionMove(message, out _) is { } move)
        {
            // A move ends the attack in progress (the client drops its action). Outside lockstep the client reports its
            // own cell: start from it instead of the estimate.
            CancelAttack(now);
            CancelPickup(now);
            CancelTalk(now);
            if (move.X != 0 || move.Y != 0) Walk.Place(move.X, move.Y, now);
            var (x, y) = Walk.Turn(move.Dx, move.Dy, now);
            return [InstanceMovementPoe2.DirectionMoveEcho(PlayerId, move.Dx, move.Dy, x, y, move.Flag != 0)];
        }
        if (InWorld && InstanceProtocolPoe2.ReadSetSkillSlot(message) is { } bind) return BindSlot(bind.Slot, bind.SkillId);
        if (InWorld && InstanceNpcsPoe2.ReadChooseTalk(message) is byte option) return ChooseTalk(option);
        if (InWorld && InstanceNpcsPoe2.IsCloseTalk(message)) return CloseTalk();
        if (InWorld && InstanceWaypointsPoe2.ReadTravel(message) is { } travel) return Travel(travel);
        if (InWorld && InstancePassivesPoe2.ReadAllocate(message) is { } allocate) return AllocatePassive(allocate);
        if (InWorld && InstancePassivesPoe2.ReadRefund(message) is { } refund) return RefundPassive(refund.Node);
        if (InWorld && InstancePassivesPoe2.IsRefundAll(message)) return RefundAllPassives();
        if (InWorld && InstanceGemsPoe2.ReadCutGem(message) is { } cut) return CutGem(cut);
        if (InWorld && InstanceItemsPoe2.ReadPickUp(message) is { } pickUp) return PickUpItem(pickUp);
        if (InWorld && InstanceItemsPoe2.ReadPlace(message) is { } place) return PlaceItem(place);
        if (InWorld && InstanceFlasksPoe2.ReadUseFlask(message) is { } flask) return UseFlask(flask, now);
        if (InWorld && InstanceSkillsPoe2.ReadUseSkill(message, out _) is { TargetId: { } item } pick && _groundItems.ContainsKey(item))
            return BeginPickup(item, pick, now);
        if (InWorld && InstanceSkillsPoe2.ReadUseSkill(message, out _) is { TargetId: { } npc } talk && IsUsable(npc))
            return BeginTalk(npc, talk, now);
        if (InWorld && InstanceSkillsPoe2.ReadUseSkill(message, out _) is { TargetId: { } target } use &&
            _player.Skills?.FirstOrDefault(s => s.Id == use.SkillId) is { } skill)
        {
            // An attack on an object waits for the server, as in PoE1: start it from where the player is; the action
            // walks into range itself.
            CancelAttack(now);
            var (x, y) = Walk.Cell(now);
            if (MonsterIndex(target) is int monster && AttackPoe2.Of(skill) is { } attack) BeginAttack(monster, attack, now);
            return [InstanceSkillsPoe2.ActionStarted(PlayerId, x, y, use.X, use.Y, use.SkillId, use.Flags, target)];
        }
        // An attack at the ground (x, y relative to the player) waits for the server too: unanswered, the client sent no
        // further use (live 2026-11-04). It hits the monsters in its area (InstanceSessionPoe2.Slams.cs). The dodge roll
        // runs by itself and gets no answer.
        if (InWorld && InstanceSkillsPoe2.ReadUseSkill(message, out _) is { TargetId: null } ground &&
            _player.Skills?.FirstOrDefault(s => s.Id == ground.SkillId) is { } groundSkill && AttackPoe2.Of(groundSkill) is { } groundAttack)
        {
            CancelAttack(now);
            var (x, y) = Walk.Cell(now);
            BeginAreaAttack(ground.SkillId, groundAttack, (x, y), (ground.X, ground.Y), now);
            return [InstanceSkillsPoe2.ActionStarted(PlayerId, x, y, x + ground.X, y + ground.Y, ground.SkillId, ground.Flags)];
        }
        // Anything else gets no answer, the re-aims of an action in progress among them (C→S 0x1a8, every ~70 ms while
        // the button is held), which the server does not follow.
        return [];
    }

    /// <summary>The share of the attack time at which a melee hit lands (its contact point).</summary>
    public const double ContactPoint = 0.5;

    // The attack in progress on a monster: the player walks from From (at Start) to Stop (at Arrive), and the hit lands
    // at Hit.
    private sealed record PendingAttack(int Monster, AttackPoe2 Attack, (int X, int Y) From, (int X, int Y) Stop,
        double Start, double Arrive, double Hit);

    private PendingAttack? _attack;
    private double _attackFreeAt;

    /// <summary>The current life of the area's i-th monster (0 once dead).</summary>
    public int MonsterLife(int i) => _monsterLife[i];

    private int? MonsterIndex(uint objectId)
    {
        long i = (long)objectId - MonsterId(0);
        return i >= 0 && i < _monsters.Count && _monsterLife[i] > 0 ? (int)i : null;
    }

    // The player walks into reach of the monster (the client's action does it), then swings; a swing starts once the
    // previous one has ended.
    private void BeginAttack(int monster, AttackPoe2 attack, double now)
    {
        Provoke(monster);
        var from = Walk.Cell(now);
        var m = MonsterCell(monster, now);
        double dx = m.X - from.X, dy = m.Y - from.Y, distance = Math.Sqrt(dx * dx + dy * dy);
        double walk = Math.Max(0, distance - attack.Range);
        var stop = walk <= 0 ? from : ((int)Math.Round(from.X + dx * walk / distance), (int)Math.Round(from.Y + dy * walk / distance));
        double arrive = now + walk / _walkSpeed, swing = Math.Max(arrive, _attackFreeAt), seconds = attack.AttackMs / 1000.0;
        _attack = new(monster, attack, from, stop, now, arrive, swing + seconds * ContactPoint);
        _attackFreeAt = swing + seconds;
    }

    // Drops the attack in progress, the walk estimate where the approach has brought the player by now.
    private void CancelAttack(double now)
    {
        if (_attack is not { } a || now >= a.Hit) return;
        double t = a.Arrive <= a.Start ? 1 : Math.Clamp((now - a.Start) / (a.Arrive - a.Start), 0, 1);
        if (a.Stop != a.From)
            Walk.Place((int)Math.Round(a.From.X + (a.Stop.X - a.From.X) * t), (int)Math.Round(a.From.Y + (a.Stop.Y - a.From.Y) * t), now);
        _attack = null;
        _attackFreeAt = Math.Min(_attackFreeAt, now);
    }

    // The hit of the attack in progress once it lands: a damage roll off the monster's life (S→C 0x1cf; at 0 the client
    // runs the monster's death).
    private IEnumerable<byte[]> AttackTick(double now)
    {
        if (_attack is not { } a || now < a.Hit) yield break;
        _attack = null;
        if (a.Stop != a.From) Walk.Place(a.Stop.X, a.Stop.Y, now);
        if (_monsterLife[a.Monster] <= 0) yield break;
        foreach (var m in DamageMonster(a.Monster, _random.Next(a.Attack.MinimumDamage, a.Attack.MaximumDamage + 1), now)) yield return m;
    }

    // A hit of `damage` on the i-th monster: its new life (0x1cf) and, at 0, its death's experience, drop and flask charges.
    private IEnumerable<byte[]> DamageMonster(int i, int damage, double now)
    {
        _monsterLife[i] = Math.Max(0, _monsterLife[i] - damage);
        // At 0 the client stops refreshing the mini life bar (as PoE1): hide it.
        if (_monsterLife[i] == 0)
            yield return InstanceCombatPoe2.StatsUpdate(MonsterId(i), [(InstanceCombatPoe2.HideMiniLifeBar, 1)]);
        yield return InstanceCombatPoe2.LifeUpdate(MonsterId(i), _monsterLife[i]);
        if (_monsterLife[i] == 0)
            foreach (var m in GainExperience(i).Concat(DropLoot(i, now)).Concat(FlaskChargesOnKill())) yield return m;
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

    /// <summary>The server's per-frame work at <paramref name="now"/>: the hit of an attack when it lands (0x1cf, and the
    /// kill's experience 0x2f9 and drop 0x2f7), the monsters' chases, attacks and hits (0x1c8, 0x1cf on the player), and the change area (0x2f1) when the walk reaches
    /// an exit. Empty otherwise, and after the player has left.</summary>
    public IReadOnlyList<byte[]> Tick(double now)
    {
        lock (_lock)
        {
            if (!InWorld || LeavingTo is not null) return [];
            var sent = AttackTick(now).ToList();
            sent.AddRange(AreaHitTick(now));
            sent.AddRange(PickupTick(now));
            sent.AddRange(TalkTick(now));
            sent.AddRange(WaypointTick(now));
            sent.AddRange(MonsterTick(now));
            sent.AddRange(FlaskTick(now));
            if (ExitTick(now) is { } change) sent.Add(change);
            return sent;
        }
    }

    private byte[]? ExitTick(double now)
    {
        if (_exits.Count == 0) return null;
        var (x, y) = Walk.Cell(now);
        var near = _exits.FirstOrDefault(e => Math.Abs(e.GridX - x) <= ExitReach && Math.Abs(e.GridY - y) <= ExitReach);
        if (near is null) { _awayFromExits = true; return null; }
        if (!_awayFromExits) return null;
        (LeavingBy, LeavingTo) = (near, near.DestinationHash16);
        return InstanceProtocolPoe2.ChangeArea(near.DestinationHash16);
    }

    /// <summary>The local player (0x24), the player object (0x2f7), its inventories (0xdf), the starting weapon in
    /// them and the socketed gems (0xde), enter area (0x23) and the
    /// frame length (0x2f5), in the order the client accepted live, and the skills the skill bar offers (0x66); then the
    /// passive tree (0x3a) and gold (0x2db), the area's exits, its monsters after their types (0x21), its NPCs and other
    /// server objects, and the character's quest flags (0x2f9) and waypoints (0x5a).</summary>
    public IReadOnlyList<byte[]> EnterWorld() =>
    [
        InstanceProtocolPoe2.SetLocalPlayer(PlayerId),
        InstanceProtocolPoe2.CreateObject(PlayerId, InstanceEntities.ObjectTypeHash(_objectType), _player.Blob()),
        [.. InstanceInventoriesPoe2.PlayerDefaults.SelectMany(slot =>
            InstanceInventoriesPoe2.AddInventory(InstanceInventoriesPoe2.InventoryId(slot.Row), slot))],
        .. EquipmentMessages(),
        .. ItemMessages(),
        .. FlaskMessages(),
        .. GemMessages(),
        InstanceProtocolPoe2.EnterArea(PlayerId),
        InstanceProtocolPoe2.FrameLength(FrameMs),
        InstanceProtocolPoe2.SkillList([.. SkillIds.Select(id => (id, SlotMask(id)))]),
        // With a weapon in hand, the Actor's weapon records again (0x324): the client resets them after the creation
        // blob, which greys the weapon's own attack (wave 28).
        .. _player.MainHandType != 0 ? new[] { InstanceSkillsPoe2.WeaponRecords(PlayerId, _player.MainHandType) } : [],
        PassivesMessage(),
        GoldMessage(),
        .. _exits.Select((exit, i) => InstanceProtocolPoe2.CreateObject(ExitId(i),
            InstanceEntities.ObjectTypeHash(AreaTransitionObjectPoe2.MetadataPath), exit.Blob())),
        .. MonsterMessages(),
        .. _objects.Select((o, i) => InstanceProtocolPoe2.CreateObject(ServerObjectId(i), InstanceEntities.ObjectTypeHash(o.TypePath),
            o.Blob())),
        .. QuestMessages(),
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

    /// <summary>The entity id of the area's i-th server object (NPC, waypoint, ...), after the monsters.</summary>
    public uint ServerObjectId(int i) => MonsterId(_monsters.Count) + (uint)i;

    /// <summary>The area's i-th server object as the client has it (its states for the character's flags).</summary>
    public ServerObjectPoe2 ServerObject(int i) { lock (_lock) return _objects[i]; }

    /// <summary>Whether <see cref="Split"/> knows the length of messages with this id.</summary>
    public static bool CanFrame(ushort id) =>
        FixedLengths.ContainsKey(id) || id == InstanceMovementPoe2.DirectionMoveId || id == InstanceSkillsPoe2.UseSkillId;

    /// <summary>
    /// Splits decrypted client bytes into messages: the fixed-length ids above and 0x63. Stops at the first other id
    /// or at a message cut off at the end; <paramref name="rest"/> is where it stopped (the length when all was split).
    /// </summary>
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> wire, out int rest, Poe2WireIds? ids = null)
    {
        var messages = new List<byte[]>();
        // With the client's own numbering (ids), each message's id is put back to the code's before it is measured
        // and returned; an id the code has no number for stops the split like any id without a length.
        Span<byte> data = wire.ToArray();
        int at = 0;
        while (data.Length - at >= 2)
        {
            var tail = data[at..];
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(tail);
            if (ids is not null)
            {
                if (!ids.TryFromWire(id, out id)) break;
                BinaryPrimitives.WriteUInt16BigEndian(tail, id);
            }
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
