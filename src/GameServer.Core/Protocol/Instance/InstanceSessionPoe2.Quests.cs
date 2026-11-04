using GameServer.Core.World;

namespace GameServer.Core.Protocol.Instance;

/// <summary>The character's quest flags and waypoints (PORTING-POE2.md, wave 27): kept per character by the Gateway
/// (Auth's account store), sent to the client (0x2f9 on the Player, 0x5a), and driving the NPCs' states and the travel
/// from the World panel (C→S 0x5b → 0x2f1).</summary>
public sealed partial class InstanceSessionPoe2
{
    /// <summary>How near (in cells) the walk estimate must come to the area's waypoint to activate it, PoE1's rule.</summary>
    public const int WaypointActivationReach = 25;

    private readonly HashSet<string> _questFlags = [];
    private readonly HashSet<string> _waypoints = [];
    private Poe2Quests _quests = Poe2Quests.Empty;
    private Poe2Areas.Area? _area;
    private Action<IReadOnlyCollection<string>>? _onQuestFlags, _onWaypoints;
    private ServerObjectPoe2[] _objects = [];
    private int _waypointObject = -1;
    private bool _waypointSettled;
    // Objects whose states changed while their dialogue was open: created again once it ends.
    private readonly HashSet<int> _staleObjects = [];

    /// <summary>The character's quest flags (QuestFlags Ids), the area's entry flag included.</summary>
    public IReadOnlySet<string> QuestFlags { get { lock (_lock) return new HashSet<string>(_questFlags); } }

    /// <summary>The character's activated waypoints (WorldAreas Ids).</summary>
    public IReadOnlySet<string> Waypoints { get { lock (_lock) return new HashSet<string>(_waypoints); } }

    /// <summary>The area the player is leaving for (its HASH16; the change area was sent), or null.</summary>
    public ushort? LeavingTo { get; private set; }

    /// <summary>Whether the player leaves by waypoint (it arrives by the destination's) rather than by an exit.</summary>
    public bool LeavingAtWaypoint { get; private set; }

    private void InitQuests(IReadOnlyList<ServerObjectPoe2>? objects, IEnumerable<string>? questFlags, IEnumerable<string>? waypoints,
        Poe2Quests? quests, Poe2Areas.Area? area, Action<IReadOnlyCollection<string>>? onQuestFlags, Action<IReadOnlyCollection<string>>? onWaypoints)
    {
        (_quests, _area, _onQuestFlags, _onWaypoints) = (quests ?? Poe2Quests.Empty, area, onQuestFlags, onWaypoints);
        _questFlags.UnionWith(questFlags ?? []);
        _waypoints.UnionWith(waypoints ?? []);
        if (area is not null)
        {
            // Entering the area sets its flags (WorldAreas FlagOnEntered: Visited-G1_2 ...), and a town's waypoint is
            // active from the first visit, as PoE1's server does.
            int entered = 0;
            foreach (string flag in _quests.EnteredFlags(area.Id)) if (_questFlags.Add(flag)) entered++;
            if (entered > 0) _onQuestFlags?.Invoke([.. _questFlags]);
            if (area.Town && _quests.WaypointBit(area.Hash) is not null && _waypoints.Add(area.Id)) _onWaypoints?.Invoke([.. _waypoints]);
        }
        _objects = [.. (objects ?? []).Select(o => o.ForFlags(_questFlags))];
        _waypointObject = Array.FindIndex(_objects, o => o.IsWaypoint);
    }

    // On entering the world: the player's flags (its creation blob sends none) and the character's waypoints, after the
    // area's objects.
    private IEnumerable<byte[]> QuestMessages()
    {
        if (_questFlags.Count > 0 && _quests.FlagCount > 0) yield return InstanceQuestsPoe2.PlayerQuestFlags(PlayerId, _quests.FlagRows(_questFlags));
        if (_quests.Waypoints.Count > 0) yield return WaypointsMessage();
    }

    private byte[] WaypointsMessage() =>
        InstanceWaypointsPoe2.Waypoints(_waypoints.Select(_quests.WaypointBit).Where(b => b is not null).Select(b => b!.Value));

    // Sets quest flags: the character's list (saved through the callback), the client's set (0x2f9), and the server
    // objects whose states the flags change, created again (0x2fa, 0x2f7). Nothing for flags already set.
    private IEnumerable<byte[]> SetQuestFlags(IEnumerable<string> flags)
    {
        int added = 0;
        foreach (string flag in flags) if (_questFlags.Add(flag)) added++;
        if (added == 0) yield break;
        _onQuestFlags?.Invoke([.. _questFlags]);
        if (_quests.FlagCount > 0) yield return InstanceQuestsPoe2.PlayerQuestFlags(PlayerId, _quests.FlagRows(_questFlags));
        for (int i = 0; i < _objects.Length; i++)
        {
            var updated = _objects[i].ForFlags(_questFlags);
            if (updated.SharedStates!.SequenceEqual(_objects[i].SharedStates ?? [])) continue;
            _objects[i] = updated;
            // The NPC whose dialogue is open keeps its object until the dialogue ends.
            if (ServerObjectId(i) == _talkingTo) _staleObjects.Add(i);
            else foreach (var m in ObjectAgain(i)) yield return m;
        }
    }

    private IEnumerable<byte[]> RefreshStaleObjects()
    {
        foreach (int i in _staleObjects.Where(i => ServerObjectId(i) != _talkingTo).ToList())
        {
            _staleObjects.Remove(i);
            foreach (var m in ObjectAgain(i)) yield return m;
        }
    }

    private IEnumerable<byte[]> ObjectAgain(int i)
    {
        yield return InstanceProtocolPoe2.RemoveObject(ServerObjectId(i));
        yield return InstanceProtocolPoe2.CreateObject(ServerObjectId(i), InstanceEntities.ObjectTypeHash(_objects[i].TypePath), _objects[i].Blob());
    }

    // Activates the area's waypoint for the character: its list (saved through the callback) and the client's bitset
    // again (0x5a; the client plays the activation when the area's bit turns on). Nothing when it is active already or
    // the area has no waypoint.
    private IEnumerable<byte[]> ActivateWaypoint()
    {
        _waypointSettled = true;
        if (_area is null || _quests.WaypointBit(_area.Hash) is null || !_waypoints.Add(_area.Id)) yield break;
        _onWaypoints?.Invoke([.. _waypoints]);
        yield return WaypointsMessage();
    }

    // Walking up to the area's waypoint activates it, as PoE1's server does.
    private IEnumerable<byte[]> WaypointTick(double now)
    {
        if (_waypointSettled || _waypointObject < 0) yield break;
        var (x, y) = Walk.Cell(now);
        var w = _objects[_waypointObject];
        if (Math.Abs(w.GridX - x) > WaypointActivationReach || Math.Abs(w.GridY - y) > WaypointActivationReach) yield break;
        foreach (var m in ActivateWaypoint()) yield return m;
    }

    // C→S 0x5b: a pin of the World panel. As PoE1's server: the destination's waypoint must be activated (and not this
    // area's); then the change area (0x2f1) as for an exit, and the transfer arrives by the destination's waypoint.
    // Ctrl (PoE1's instance chooser) travels too.
    private IReadOnlyList<byte[]> Travel(InstanceWaypointsPoe2.Travel travel)
    {
        if (LeavingTo is not null) return [];
        if (_quests.WaypointOf(travel.AreaHash16) is not { } destination || !_waypoints.Contains(destination.Id)
            || destination.Hash == _area?.Hash) return [];
        (LeavingTo, LeavingAtWaypoint) = (destination.Hash, true);
        return [InstanceProtocolPoe2.ChangeArea(destination.Hash)];
    }
}
