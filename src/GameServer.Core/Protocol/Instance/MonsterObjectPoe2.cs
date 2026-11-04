namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A PoE2 monster (PORTING-POE2.md, wave 20): a 0x2f7 object whose type is a MonsterVarieties row registered at a level
/// by S→C 0x21 (<see cref="InstanceProtocolPoe2.MonsterTypes"/>), "&lt;variety Id&gt;@&lt;level&gt;" as in PoE1. The
/// components come from the variety's .ot chain (Positioned first, then the sections parent-first; `Metadata/Monsters/
/// Monster`: Stats, ObjectMagicProperties, BaseEvents, Pathfinding, Buffs, Life, Animated, Inventories, Actor, Monster,
/// Functions, then what the type adds).
/// </summary>
/// <param name="Components">The type's components in deserialize order.</param>
/// <param name="TransitionState">Transitionable's state, 1..num_states (PoE1: state 0 crashes the client).</param>
/// <param name="SharedStates">The StateMachine's shared states in .ot order (define_shared_state and
/// define_shared_checkpoint_state); the client throws unless the count matches the type.</param>
/// <param name="Skills">The Actor's skills (its attack, wave 23), as the player's.</param>
public sealed record MonsterObjectPoe2(int GridX, int GridY, uint Life, IReadOnlyList<string> Components, float Rotation = 0,
    string? Model = null, ushort Team = 1, byte TransitionState = 1, IReadOnlyList<string>? SharedStates = null,
    IReadOnlyList<GrantedSkillPoe2>? Skills = null)
{
    public static readonly string[] BaseComponents =
        ["Positioned", "Stats", "ObjectMagicProperties", "BaseEvents", "Pathfinding", "Buffs", "Life", "Animated",
         "Inventories", "Actor", "Monster", "Functions"];

    /// <summary>Components a type may add after the base list that this blob can write.</summary>
    static readonly HashSet<string> Extras = ["Transitionable", "StateMachine"];

    /// <summary>The registered type path of a variety at a level (1..100).</summary>
    public static string TypePath(string varietyId, byte level) => $"{varietyId}@{level}";

    public static bool CanSerialize(IReadOnlyList<string> components) =>
        components.Count >= BaseComponents.Length && components.Take(BaseComponents.Length).SequenceEqual(BaseComponents) &&
        components.Skip(BaseComponents.Length).All(Extras.Contains);

    public byte[] Blob()
    {
        if (!CanSerialize(Components)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(0x02);         // object flags: bit 1, in the wake zone (outside it the object is neither drawn nor updated)
        w.U8(0);            // base data entry count
        foreach (string c in Components)
        {
            switch (c)
            {
                case "Positioned": w.I32(GridX).I32(GridY).F32(Rotation).U16(Team).U8(0).U8(0).U8(0); break;
                // The life bar's maximum: maximum_life, as the player's (Poe2CharacterStats).
                case "Stats": w.Stats([(Poe2CharacterStats.MaximumLife, (int)Life)]).U32(0).U8(0).U8(0); break;
                case "ObjectMagicProperties": w.U16(0).U8(0).U8(0); break;
                case "Buffs": w.U16(0); break;
                case "Life":
                    Pool(w, Life); w.Zeros(14);
                    Pool(w, 0); w.Zeros(14);
                    Pool(w, 0);
                    w.Zeros(3 * 6).Zeros(6).U8(0);
                    break;
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "Inventories": w.U8(0).U8(0); break;
                // As the player's: three empty entries, the flags byte, the skill list, 14 bytes (40 with no skills).
                case "Actor": w.Zeros(18).U8(0); ActorSkillsPoe2.WriteList(w, Skills ?? []); w.Zeros(14); break;
                case "Transitionable": w.U8((byte)(TransitionState & 0x7f)); break;
                // 0x141dfa4a0 (as PoE1): u8 shared-state count, u8 shared-timer count, then per shared state a compact
                // value and a u8 (0x02 keeps the sent value), per timer a u32 and a u8. Every state starts at 0.
                case "StateMachine":
                    w.U8((byte)(SharedStates?.Count ?? 0)).U8(0);
                    foreach (var _ in SharedStates ?? []) w.CompactI(0).U8(0);
                    break;
                // BaseEvents, Pathfinding, Monster, Functions: no data.
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }

    private static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
}

/// <summary>A monster to create in an area: its MonsterVarieties Id and row, its level and its object, and the variety's
/// and level's combat values when the server has them.</summary>
public sealed record Poe2Monster(string VarietyId, ushort Row, byte Level, MonsterObjectPoe2 Object,
    World.Poe2Monsters.Variety? Variety = null, World.Poe2Monsters.LevelStats? LevelStats = null,
    World.Poe2Monsters.Attack? Attack = null)
{
    public string TypePath => MonsterObjectPoe2.TypePath(VarietyId, Level);
}
