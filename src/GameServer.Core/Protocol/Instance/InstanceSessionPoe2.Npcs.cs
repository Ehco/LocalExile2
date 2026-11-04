namespace GameServer.Core.Protocol.Instance;

/// <summary>The area's NPCs and other server objects (PORTING-POE2.md, wave 27): a click on an NPC walks the player
/// into reach (as an attack or a pickup), then opens its dialogue (S→C 0xa3); a click on the waypoint walks there, then
/// opens the World panel (S→C 0x98 kind 0).</summary>
public sealed partial class InstanceSessionPoe2
{
    /// <summary>How near (in cells) the player stops from an NPC to talk: NPC.ot's object_size 5 plus a cell.</summary>
    public const int TalkReach = 6;

    private sealed record PendingTalk(uint Npc, (int X, int Y) From, (int X, int Y) Stop, double Start, double Arrive);

    private PendingTalk? _talk;

    // The NPC whose dialogue window is open (0 = none), and whether one of its talks plays in it.
    private uint _talkingTo;
    private bool _inNpcTalk;

    private int? ServerObjectIndex(uint objectId)
    {
        long i = (long)objectId - ServerObjectId(0);
        return i >= 0 && i < _objects.Count ? (int)i : null;
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
            yield return InstanceWaypointsPoe2.OpenUi(InstanceWaypointsPoe2.WorldPanelUi, t.Npc);
            yield break;
        }
        _talkingTo = t.Npc;
        _inNpcTalk = false;
        yield return InstanceNpcsPoe2.Talk(t.Npc);
    }

    // C→S 0xa1: an option of the open dialogue. As PoE1's server answers 0x82: BuyItems opens the buy window (0xaf, no
    // stock yet), any other option opens the window on that NPCTalk row.
    private IReadOnlyList<byte[]> ChooseTalk(byte option)
    {
        if (_talkingTo == 0) return [];
        string? category = ServerObjectIndex(_talkingTo) is int i ? _objects[i].TalkCategories.GetValueOrDefault(option) : null;
        if (category == "BuyItems")
        {
            _inNpcTalk = false;
            return [InstanceNpcsPoe2.ShopWindow(_talkingTo)];
        }
        _inNpcTalk = true;
        return [InstanceNpcsPoe2.Talk(_talkingTo, option)];
    }

    // C→S 0xa4: Goodbye, Esc or Continue at the end of a talk. As PoE1's server answers 0x85: back to the option list
    // after a talk, else end the dialogue (object id 0).
    private IReadOnlyList<byte[]> CloseTalk()
    {
        if (_talkingTo != 0 && _inNpcTalk)
        {
            _inNpcTalk = false;
            return [InstanceNpcsPoe2.Talk(_talkingTo)];
        }
        _talkingTo = 0;
        _inNpcTalk = false;
        return [InstanceNpcsPoe2.Talk(0)];
    }
}
