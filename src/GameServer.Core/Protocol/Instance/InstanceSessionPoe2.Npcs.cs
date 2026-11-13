namespace GameServer.Core.Protocol.Instance;

/// <summary>The area's NPCs and other server objects (PORTING-POE2.md, wave 27): a click on an NPC walks the player
/// into reach (as an attack or a pickup), then opens its dialogue (S→C 0xa3); its options set quest flags and play
/// their talks (C→S 0xa1, 0xa4); a click on the waypoint walks there, activates it (S→C 0x5a), then opens the World
/// panel (S→C 0x98 kind 0).</summary>
public sealed partial class InstanceSessionPoe2
{
    /// <summary>How near (in cells) the player stops from an NPC to talk: NPC.ot's object_size 5 plus a cell.</summary>
    public const int TalkReach = 6;

    private sealed record PendingTalk(uint Npc, (int X, int Y) From, (int X, int Y) Stop, double Start, double Arrive);

    private PendingTalk? _talk;

    // The NPC whose dialogue window is open (0 = none), whether one of its talks plays in it, and the flag that talk sets
    // once heard (Continue).
    private uint _talkingTo;
    private bool _inNpcTalk;
    private string? _finishFlag;

    /// <summary>The NPC whose dialogue window is open (its entity id), 0 for none.</summary>
    public uint TalkingTo { get { lock (_lock) return _talkingTo; } }

    private int? ServerObjectIndex(uint objectId)
    {
        long i = (long)objectId - ServerObjectId(0);
        return i >= 0 && i < _objects.Length ? (int)i : null;
    }

    private bool IsNpc(uint objectId) => ServerObjectIndex(objectId) is int i && _objects[i].IsNpc;

    // The objects a click walks to and uses: NPCs and the waypoint.
    private bool IsUsable(uint objectId) => ServerObjectIndex(objectId) is int i && (_objects[i].IsNpc || _objects[i].IsWaypoint);

    private IReadOnlyList<byte[]> BeginTalk(uint id, UseSkillPoe2 use, double now)
    {
        CancelAttack(now);
        CancelPickup(now);
        var npc = _objects[ServerObjectIndex(id)!.Value];
        var from = Walk.Cell(now);
        double dx = npc.GridX - from.X, dy = npc.GridY - from.Y, distance = Math.Sqrt(dx * dx + dy * dy);
        double walk = Math.Max(0, distance - TalkReach);
        var stop = walk <= 0 ? from : ((int)Math.Round(from.X + dx * walk / distance), (int)Math.Round(from.Y + dy * walk / distance));
        _talk = new(id, from, stop, now, now + walk / _walkSpeed);
        return [InstanceSkillsPoe2.ActionStarted(PlayerId, from.X, from.Y, npc.GridX, npc.GridY, use.SkillId, use.Flags, id)];
    }

    // A move drops the talk, the walk estimate where the approach has brought the player by now.
    private void CancelTalk(double now)
    {
        if (_talk is not { } t || now >= t.Arrive) return;
        double f = t.Arrive <= t.Start ? 1 : Math.Clamp((now - t.Start) / (t.Arrive - t.Start), 0, 1);
        if (t.Stop != t.From)
            Walk.Place((int)Math.Round(t.From.X + (t.Stop.X - t.From.X) * f), (int)Math.Round(t.From.Y + (t.Stop.Y - t.From.Y) * f), now);
        _talk = null;
    }

    private IEnumerable<byte[]> TalkTick(double now)
    {
        if (_talk is not { } t || now < t.Arrive) yield break;
        _talk = null;
        if (t.Stop != t.From) Walk.Place(t.Stop.X, t.Stop.Y, now);
        if (_objects[ServerObjectIndex(t.Npc)!.Value].IsWaypoint)
        {
            // As PoE1's server: the click activates the waypoint (the client plays it) before the panel opens.
            foreach (var m in ActivateWaypoint()) yield return m;
            yield return InstanceWaypointsPoe2.OpenUi(InstanceWaypointsPoe2.WorldPanelUi, t.Npc);
            yield break;
        }
        (_talkingTo, _inNpcTalk, _finishFlag) = (t.Npc, false, null);
        yield return InstanceNpcsPoe2.Talk(t.Npc);
    }

    // C→S 0xa1: an option of the open dialogue, an NPCTalk row the client offered for the player's flags. As PoE1's
    // server answers 0x82: the option's start flags are set (0x2f9; the NPCs whose states they change are created again),
    // then BuyItems opens the buy window (0xaf, no stock yet), a FinishNPCChat script ends the dialogue (0xa3, object 0)
    // with the finish flag set too, and any other option plays its talk in the window (0xa3 with the index).
    private IReadOnlyList<byte[]> ChooseTalk(byte option)
    {
        if (_talkingTo == 0 || ServerObjectIndex(_talkingTo) is not int i) return [];
        var sent = new List<byte[]>();
        if (_objects[i].Talks.GetValueOrDefault(option) is not { } talk)
        {
            // An option the table does not know (no server-objects.json): play it anyway.
            (_inNpcTalk, _finishFlag) = (true, null);
            sent.Add(InstanceNpcsPoe2.Talk(_talkingTo, option));
            return sent;
        }
        if (!talk.Available(_questFlags)) return [];
        sent.AddRange(SetQuestFlags(talk.StartFlags));
        if (talk.Category == "BuyItems")
        {
            (_inNpcTalk, _finishFlag) = (false, null);
            sent.Add(InstanceNpcsPoe2.ShopWindow(_talkingTo));
        }
        else if (talk.FinishesChat)
        {
            sent.AddRange(EndDialogue());
            if (talk.Finish is { } finish) sent.AddRange(SetQuestFlags([finish]));
        }
        else
        {
            (_inNpcTalk, _finishFlag) = (true, talk.Finish);
            sent.Add(InstanceNpcsPoe2.Talk(_talkingTo, option));
        }
        return sent;
    }

    // C→S 0xa4: Goodbye, Esc or Continue at the end of a talk. As PoE1's server answers 0x85: after a talk, its finish
    // flag is set and the window goes back to the option list; otherwise the dialogue ends (object id 0).
    private IReadOnlyList<byte[]> CloseTalk()
    {
        if (_talkingTo == 0 || !_inNpcTalk) return [.. EndDialogue()];
        var sent = new List<byte[]>();
        _inNpcTalk = false;
        if (_finishFlag is { } finish) sent.AddRange(SetQuestFlags([finish]));
        _finishFlag = null;
        sent.Add(InstanceNpcsPoe2.Talk(_talkingTo));
        return sent;
    }

    // Ends the dialogue (0xa3 with object id 0); the NPC talked to is then created again if its states changed meanwhile.
    private IEnumerable<byte[]> EndDialogue()
    {
        (_talkingTo, _inNpcTalk, _finishFlag) = (0, false, null);
        yield return InstanceNpcsPoe2.Talk(0);
        foreach (var m in RefreshStaleObjects()) yield return m;
    }
}
