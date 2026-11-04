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
/// keeps the server's value: the states follow the player's quest flags, <see cref="World.Poe2ServerObjects"/>);</item>
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

    /// <summary>The NPC's dialogue options by index (C→S 0xa1): their NPCTalkCategory Id (Gossip, Quests, BuyItems ...).</summary>
    public IReadOnlyDictionary<int, string> TalkCategories { get; init; } = new Dictionary<int, string>();

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
