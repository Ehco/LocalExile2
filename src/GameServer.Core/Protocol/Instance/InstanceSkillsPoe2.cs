using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>A skill use the client asks for (C→S <see cref="InstanceSkillsPoe2.UseSkillId"/>). X, Y are the target cell:
/// absolute when the use names a target object, else relative to the player (the client subtracts its own cell).</summary>
public sealed record UseSkillPoe2(uint SkillId, ushort Flags, byte Sequence, byte Options, uint? TargetId, int X, int Y);

/// <summary>PoE2 skill use and its echo (PORTING-POE2.md, wave 21).</summary>
public static class InstanceSkillsPoe2
{
    public const ushort UseSkillId = 0x1a5, ActionStartedId = 0x1c8;

    public const ushort WeaponRecordsId = 0x324;

    /// <summary>The hand type of an empty hand: no ItemClasses row (0x141e862c0 refuses it for a weapon requirement).</summary>
    public const byte NoWeaponType = 0x77;

    /// <summary>
    /// S→C 0x324 (world group, vtable 0x14350c450, reader 0x141f6b3a0; the Actor's handler 0x1421497c0, case at
    /// 0x14214b15e): the Actor's three weapon records again, PoE2's "weapons changed". Object header, u8 (passed to the
    /// Actor's vtable +0x3d0 after the copy), then three records of u8 main hand type, u8 off hand type (ItemClasses row
    /// + 1, <see cref="NoWeaponType"/> for none) and a wstr (u16 BE unit count + UTF-16), copied to the Actor's +0x2c8,
    /// +0x2f0 and +0x318 (stride 0x28). The skills' weapon requirements (0x141e859c0) read the first record's types
    /// (or, with weapon sets, the set's); the client resets them to Unarmed / none after the creation blob
    /// (live 2026-11-04, Frida: 37 and 0x77 whatever the blob held), so the server sends this after the skills.
    /// </summary>
    public static byte[] WeaponRecords(uint objectId, byte mainType, byte offType = NoWeaponType, bool flag = false)
    {
        var w = new LoginWriter().U16(WeaponRecordsId).U32(objectId).U32(0).U8(0).U16(0).U8((byte)(flag ? 1 : 0));
        // The current hands, weapon set I (the same), weapon set II (empty).
        w.U8(mainType).U8(offType).U16(0);
        w.U8(mainType).U8(offType).U16(0);
        w.U8(NoWeaponType).U8(NoWeaponType).U16(0);
        return w.ToArray();
    }

    /// <summary>The C→S 0x1a5 option bit for a target object id.</summary>
    public const byte TargetOption = 0x08;

    /// <summary>
    /// C→S 0x1a5 (class 0x1434e5ae8, built by 0x14210faa0): PoE1's 0x24/0x25 in one message. u8 route, u32 skill id
    /// (0 = the Melee action 0xb188's skill), u16 flags (0x0400 live, PoE1's +0x122), u8 sequence (0xff live), u8 options
    /// (bit 3: a u32 target object id follows; bit 2 outside lockstep), i32 x, i32 y; all big-endian. 19 bytes, 23 with a
    /// target. Live: Space (dodge roll) sends 0103 0400 ff 00 and a cell relative to the player; a left click on a
    /// monster with Punch bound sends 0102 0400 ff 08, the monster's id and its absolute cell.
    /// </summary>
    public static UseSkillPoe2? ReadUseSkill(ReadOnlySpan<byte> m, out int length)
    {
        length = 0;
        if (m.Length < 19 || BinaryPrimitives.ReadUInt16BigEndian(m) != UseSkillId) return null;
        byte options = m[10];
        int at = 11;
        uint? target = null;
        if ((options & TargetOption) != 0)
        {
            if (m.Length < 23) return null;
            target = BinaryPrimitives.ReadUInt32BigEndian(m[at..]);
            at += 4;
        }
        int x = BinaryPrimitives.ReadInt32BigEndian(m[at..]), y = BinaryPrimitives.ReadInt32BigEndian(m[(at + 4)..]);
        length = at + 8;
        return new(BinaryPrimitives.ReadUInt32BigEndian(m[3..]), BinaryPrimitives.ReadUInt16BigEndian(m[7..]), m[9], options,
            target, x, y);
    }

    /// <summary>
    /// S→C 0x1c8 (world group, read 0x141f69830): PoE1's action started 0x1ab. The object header (u32 object id, u32, u8,
    /// u16), u16 flags (bit 0x80: a u32 target object id follows), compact x, y of the actor when the action starts
    /// (+0x2c/+0x30), compact target x, y (+0x24/+0x28), u32 skill id, u16 sequence (0xffff: not a predicted action, the
    /// client starts it), u8 (+0x3c).
    /// </summary>
    public static byte[] ActionStarted(uint objectId, int posX, int posY, int targetX, int targetY, uint skillId,
        ushort flags, uint? targetObjectId = null, ushort sequence = 0xffff)
    {
        var w = new LoginWriter().U16(ActionStartedId).U32(objectId).U32(0).U8(0).U16(0)
            .U16((ushort)(targetObjectId is null ? flags & 0xff7f : flags | 0x80));
        if (targetObjectId is { } t) w.U32(t);
        InstanceProtocol.CompactInt(w, posX); InstanceProtocol.CompactInt(w, posY);
        InstanceProtocol.CompactInt(w, targetX); InstanceProtocol.CompactInt(w, targetY);
        return w.U32(skillId).U16(sequence).U8(0).ToArray();
    }
}
