namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// A PoE2 area exit, `Metadata/MiscellaneousObjects/AreaTransition` (PORTING-POE2.md, wave 19). Its .ot has PoE1's
/// components: Positioned, BaseEvents (server only), Animated, AreaTransition, Stats, ObjectMagicProperties,
/// MinimapIcon, InteractionAction (+ client Render, Targetable); the layout is PoE1's <see cref="AreaTransitionObject"/>
/// with the PoE2 player's Positioned (17 bytes), as the client's readers consumed it live (2026-11-04).
/// </summary>
/// <param name="Index">The exit's compact index after its destination. The AreaTransition reader (0x1416e2dc0) reads it
/// only when a flag of the area is set ([[object +8] +0x78] +0x98, byte +0x14) and the state is 0: live, it read one
/// in Clearfell Encampment and none in Clearfell, where a sent one shifts every later component (Buffer Underflow).
/// Null writes none.</param>
public sealed record AreaTransitionObjectPoe2(int GridX, int GridY, ushort DestinationHash16, int? Index = null, float Rotation = 0,
    string? Model = null)
{
    public const string MetadataPath = AreaTransitionObject.MetadataPath;

    public byte[] Blob()
    {
        var w = new BlobWriter();
        // object flags: bit1 sets +0x8d bit2, and create object (0x1421343c0) then puts the object in the wake zone
        // (0x141e73d70, PoE1's separate 0x1a9); without it the exit is neither drawn, targetable nor on the map.
        w.U8(0x02);
        w.U8(0);            // base data entry count
        w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0).U8(0);   // Positioned
        if (Model is null) w.U8(0); else w.U8(1).WStr32(Model);          // Animated: bit0 + .ao path
        w.U8(0).U8(2).U16(DestinationHash16);                            // AreaTransition: state, HASH16 variant
        if (Index is { } index) w.CompactI(index);
        w.Stats(null).U32(0).U8(0).U8(0);                                 // Stats (no player part)
        w.U16(0).U8(0).U8(0);                                             // ObjectMagicProperties
        // MinimapIcon (0x141d91260): bit1 → shown (+0x39, object +0x8d |= 8), bit2 → +0x38, bit0 → u16 icon, bit3 → variant.
        w.U8(0x02);
        w.U8(0);            // InteractionAction
        w.U8(0);            // tasks
        return w.ToArray();
    }
}
