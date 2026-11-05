using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

/// <summary>The PoE2 character screen's preview blob (PORTING-POE2.md, wave 18).</summary>
public class CharacterPreviewPoe2Tests
{
    // FourWand1 (Withered Wand): ItemVisualIdentity HASH16 0x8a43, ItemClasses row 8 (Wand), ItemStances row 2 (Wand).
    static readonly VisibleItem Wand = new(CharacterPreviewPoe2.Weapon1, 0x8a43, Stance: 2, Type: 9);

    [Fact]
    public void No_item_is_the_empty_blob()
    {
        Assert.Empty(CharacterPreviewPoe2.Blob([]));
    }

    [Fact]
    public void Every_slot_is_sent_and_an_empty_one_is_its_zero_type()
    {
        byte[] blob = CharacterPreviewPoe2.Blob([Wand with { Slot = CharacterPreviewPoe2.Helm1 }]);

        // 30 empty slots of one byte and the item's 21 bytes: a blob that ends early throws in the client.
        Assert.Equal(CharacterPreviewPoe2.Slots - 1 + 21, blob.Length);
        Assert.All(blob[..CharacterPreviewPoe2.Helm1], b => Assert.Equal(0, b));
        Assert.Equal(9, blob[CharacterPreviewPoe2.Helm1]);
        Assert.All(blob[(CharacterPreviewPoe2.Helm1 + 21)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void An_item_is_its_type_skin_effect_stance_look_and_three_bytes()
    {
        byte[] blob = CharacterPreviewPoe2.Blob([Wand]);

        Assert.Equal(new byte[]
        {
            9,                              // ItemClasses row + 1
            0x43, 0x8a, 0, 0, 0, 0, 0,      // skin: the item's own look, no MTX, u8
            0, 0, 0, 0, 0, 0, 0,            // effect: none
            2,                              // ItemStances row
            0x43, 0x8a,                     // ItemVisualIdentity HASH16
            0, 0, 0,
        }, blob[..21]);
    }

    [Fact]
    public void A_skin_and_an_effect_replace_the_items_own_references()
    {
        var skinned = Wand with { SkinVisual = 0x1234, SkinHash = 0xaabbccdd, EffectVisual = 0x0102, EffectHash = 7 };

        byte[] blob = CharacterPreviewPoe2.Blob([skinned]);

        Assert.Equal(new byte[] { 0x34, 0x12, 0xdd, 0xcc, 0xbb, 0xaa, 0 }, blob[1..8]);
        Assert.Equal(new byte[] { 0x02, 0x01, 7, 0, 0, 0, 0 }, blob[8..15]);
        Assert.Equal(new byte[] { 0x43, 0x8a }, blob[16..18]);
    }

    [Fact]
    public void An_item_without_a_type_or_a_look_is_an_empty_slot()
    {
        byte[] blob = CharacterPreviewPoe2.Blob([Wand with { Type = 0 }, Wand with { Slot = CharacterPreviewPoe2.Helm1, VisualIdentity = 0 }]);

        Assert.Equal(new byte[CharacterPreviewPoe2.Slots], blob);
    }

    [Fact]
    public void Two_items_in_one_slot_are_refused()
    {
        Assert.Throws<ArgumentException>(() => CharacterPreviewPoe2.Blob([Wand, Wand]));
    }

    [Fact]
    public void A_slot_past_the_last_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterPreviewPoe2.Blob([Wand with { Slot = CharacterPreviewPoe2.Slots }]));
    }
}
