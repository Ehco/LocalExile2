using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>PoE2 NPC messages (PORTING-POE2.md, wave 27).</summary>
public static class InstanceNpcsPoe2
{
    /// <summary>The action id of C→S 0x1a5 on an NPC or another interactive object (the client's interact action;
    /// PoE1's 0x0266 with the top bits of PoE2's skill ids). Live 2026-11-04 a click on Una sent
    /// <c>01A5 00 2000 C266 0400 FF 08 00001009 ...</c>.</summary>
    public const ushort InteractSkillId = 0xc266;

    public const ushort TalkId = 0xa3;

    /// <summary>C→S 0xa1 (4 bytes): u8 route, u8 the chosen option, PoE1's 0x82 (the NPCTalk row's index among the
    /// NPC's rows). Live 2026-11-04 Una's options sent 0x00, 0x01, 0x0d, 0x15 and 0x16.</summary>
    public const ushort ChooseTalkId = 0xa1;

    /// <summary>C→S 0xa4 (3 bytes): u8 route; PoE1's 0x85, the dialogue window was closed (Goodbye).</summary>
    public const ushort CloseTalkId = 0xa4;

    public static byte? ReadChooseTalk(ReadOnlySpan<byte> m) =>
        m.Length == 4 && m[0] == 0 && m[1] == ChooseTalkId ? m[3] : null;

    public static bool IsCloseTalk(ReadOnlySpan<byte> m) => m.Length == 3 && m[0] == 0 && m[1] == CloseTalkId;

    /// <summary>The <see cref="Talk"/> mode that opens the NPC's dialogue window on its option list (PoE1's 0x84 0xff).</summary>
    public const byte OpenDialogue = 0xff;

    /// <summary>
    /// S→C 0xa3 (reader 0x141f6d0d0, class 0x14350c470): PoE1's 0x84 ("talk to the NPC"). The object header
    /// (0x141f4b800: u32 BE NPC object id → +0x10, u32, u8, u16), u8 mode (+0x20), u8 player slot (+0x21, the player's
    /// +0x181, PoE2's route byte). The world handler (0x1421343c0, case 0x1421349ed) finds the player with that slot and
    /// calls 0x1421065a0(player, id, mode), PoE1's 0x84 logic: object id 0 ends the dialogue (state +0x8150 = 0); mode 0xff
    /// opens the NPC's option list (state 1, NPC id at +0x8154).
    /// <para>Not 0x87 (reader 0x142154aa0: u8, u32, u8 and a 17-byte block; live 2026-11-04 it was read and did nothing,
    /// with mode 1 or 0xff).</para>
    /// </summary>
    public static byte[] Talk(uint npcObjectId, byte mode = OpenDialogue, byte playerSlot = 0) =>
        new LoginWriter().U16(TalkId).U32(npcObjectId).U32(0).U8(0).U16(0).U8(mode).U8(playerSlot).ToArray();

    public const ushort ShopWindowId = 0xaf;

    /// <summary>
    /// S→C 0xaf (reader 0x142156ad0; per-player dispatcher 0x142106bb0 case 0x14210793d): the NPC buy window, PoE1's 0x8c
    /// with an extra bool. u8 route, u32 BE NPC object id (+0x30), u16 BE (+0x34, PoE1's NPCShop row, passed to
    /// 0x140850590), u8 (+0x36), u8 bool (+0x37, PoE1's "has tabs"), u16 BE n + n × u32 BE (+0x18, 0x141f4d030),
    /// u16 BE m + m tabs (+0x38, 0x38-byte elements, 0x142175980). The case looks the NPC up (0x141e721b0) and calls the
    /// listeners at +0x13d0 (0x142118780). Live 2026-11-04 the empty one opened an empty buy/sell window on Una. Not 0xda
    /// either: its listener 0x140de08d0 is the trade close (plays Trade_Complete.ogg and erases a trade by id from local
    /// player +0x308); live it crashed the client at 0x140de0a90 on the empty trade list.
    /// </summary>
    public static byte[] ShopWindow(uint npcObjectId) =>
        new LoginWriter().U16(ShopWindowId).U8(0).U32(npcObjectId).U16(0).U8(0).U8(0).U16(0).U16(0).ToArray();
}
