using System.Text.RegularExpressions;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A PoE2 server object other than an exit or a monster (PORTING-POE2.md, wave 27): an NPC, the waypoint, the stash, a
/// healing well or a townsperson, created with 0x2f7 under its type's path. Its components are the type's .ot chain
/// (data/poe2/server-objects.json, scripts/re/poe2/server_objects.py), written in that order:
/// <list type="bullet">
/// <item>Positioned: the player's 17 bytes (i32 x, i32 y, f32 rotation, u16 team, three u8);</item>
/// <item>Animated: u8 bit 0, then the `WStr32` .ao the room places;</item>
/// <item>InteractionAction: u8 0; MinimapIcon: u8, bit 1 shown (as the exits');</item>
/// <item>NPC: PoE1's two bytes, u8 1 (has dialogue) and u8 0xff (no component must be alive to talk);</item>
/// <item>Stats: an empty table (7 bytes, as the exits'); Transitionable: u8 state;</item>
/// <item>StateMachine: u8 shared-state count, u8 timer count 0, per state a compact value and u8 0x02 (the client
/// keeps the server's value: the states follow the player's quest flags, <see cref="ForFlags"/>);</item>
/// <item>BaseEvents, Pathfinding, Functions: no data. HideoutDoodad: no data outside a hideout (PoE1's reader reads
/// its u32 + u8 + u8 only for hideout objects).</item>
/// </list>
/// Then the tasks byte. The flags byte 0x02 puts the object in the wake zone (wave 20).
/// </summary>
public sealed record ServerObjectPoe2(string TypePath, int GridX, int GridY, IReadOnlyList<string> Components, float Rotation = 0,
    string? Model = null, IReadOnlyList<int>? SharedStates = null, byte TransitionState = 1)
{
    static readonly HashSet<string> Supported =
        ["Positioned", "BaseEvents", "Animated", "InteractionAction", "NPC", "MinimapIcon", "Stats", "Pathfinding",
         "Transitionable", "StateMachine", "Functions", "HideoutDoodad"];

    /// <summary>Whether <see cref="Blob"/> can write a type with these components.</summary>
    public static bool CanSerialize(IReadOnlyList<string> components) => components.Count > 0 &&
        components[0] == "Positioned" && components.All(Supported.Contains);

    /// <summary>Whether the type talks (has the NPC component).</summary>
    public bool IsNpc => Components.Contains("NPC");

    /// <summary>Whether the type is a waypoint (opens the World panel).</summary>
    public bool IsWaypoint => TypePath.EndsWith("/Waypoint", StringComparison.Ordinal);

    /// <summary>The StateMachine's shared states with their .ot defaults, in .ot order.</summary>
    public IReadOnlyList<(string Name, int Default)> StateDefaults { get; init; } = [];

    /// <summary>The type's per_player_quest_flag_state_&lt;state&gt;_&lt;value&gt; rules, in .ot order: the state takes the
    /// value when the player has the flag.</summary>
    public IReadOnlyList<(string State, int Value, string Flag)> StateRules { get; init; } = [];

    /// <summary>The NPC's dialogue options by index (what C→S 0xa1 sends).</summary>
    public IReadOnlyDictionary<int, NpcTalk> Talks { get; init; } = new Dictionary<int, NpcTalk>();

    /// <summary>The object with its shared states for a player with these quest flags: each state's default, then every
    /// rule whose flag the player has, in .ot order (a later rule wins).</summary>
    public ServerObjectPoe2 ForFlags(IReadOnlySet<string> questFlags)
    {
        var values = StateDefaults.Select(s => s.Default).ToArray();
        foreach (var (state, value, flag) in StateRules)
        {
            int i = StateDefaults.ToList().FindIndex(s => s.Name == state);
            if (i >= 0 && questFlags.Contains(flag)) values[i] = value;
        }
        return this with { SharedStates = values };
    }

    public byte[] Blob()
    {
        if (!CanSerialize(Components)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(0x02);         // object flags: in the wake zone
        w.U8(0);            // base data entry count
        foreach (string c in Components)
        {
            switch (c)
            {
                case "Positioned": w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0).U8(0); break;
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "InteractionAction": w.U8(0); break;
                case "NPC": w.U8(1).U8(0xff); break;
                case "MinimapIcon": w.U8(0x02); break;
                case "Stats": w.Stats(null).U32(0).U8(0).U8(0); break;
                case "Transitionable": w.U8((byte)(TransitionState & 0x7f)); break;
                case "StateMachine":
                    w.U8((byte)(SharedStates?.Count ?? 0)).U8(0);
                    foreach (int v in SharedStates ?? []) w.CompactI(v).U8(0x02);
                    break;
                // BaseEvents, Pathfinding, Functions, HideoutDoodad (outside a hideout): no data.
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }
}

/// <summary>
/// An NPC's dialogue option, an NPCTalk row (data/poe2/server-objects.json "talk"; PORTING-POE2.md, wave 27). The
/// client offers it when the player has all of <paramref name="Requires"/> and none of <paramref name="Excludes"/>
/// (NPCTalk's columns _3 and _4, PoE1's QuestFlag1 and QuestFlag2), and sends its <paramref name="Index"/> (C→S 0xa1).
/// </summary>
/// <param name="Category">The NPCTalkCategory Id: Gossip, Quests, QuestReward, BuyItems, SellItems, Music ...</param>
/// <param name="Start">The flags set when the option is picked (QuestFlags1, PoE1's QuestFlag4).</param>
/// <param name="Finish">The flag set once its talk has been heard (Continue; QuestFlag2, PoE1's QuestFlag5), or null.</param>
/// <param name="Script">The row's Script: <c>SetQuestFlag(...)</c> sets a flag (its delay is ignored),
/// <c>triggerer.FinishNPCChat()</c> ends the dialogue at once instead of playing the talk in the window.</param>
public sealed record NpcTalk(int Index, string Category, string[]? Requires = null, string[]? Excludes = null,
    string[]? Start = null, string? Finish = null, string? Script = null)
{
    /// <summary>Whether the client offers the option to a player with these flags.</summary>
    public bool Available(IReadOnlySet<string> questFlags) =>
        (Requires ?? []).All(questFlags.Contains) && !(Excludes ?? []).Any(questFlags.Contains);

    /// <summary>The flags the Script sets with SetQuestFlag(flag).</summary>
    public IReadOnlyList<string> ScriptFlags =>
        [.. Regex.Matches(Script ?? "", @"SetQuestFlag\(\s*([\w\-]+)\s*\)").Select(m => m.Groups[1].Value)];

    /// <summary>Whether the Script ends the dialogue at once (triggerer.FinishNPCChat()).</summary>
    public bool FinishesChat => (Script ?? "").Contains("FinishNPCChat(", StringComparison.Ordinal);

    /// <summary>Every flag picking the option sets: <see cref="Start"/> and the Script's.</summary>
    public IEnumerable<string> StartFlags => (Start ?? []).Concat(ScriptFlags);
}
