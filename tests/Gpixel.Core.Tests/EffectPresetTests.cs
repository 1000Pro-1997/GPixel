using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>효과 레시피 JSON 왕복(개인 에셋이 쓰는 느슨한 읽기)과 예전 사용자 정의 효과 파일 읽기.</summary>
public class EffectPresetTests
{
    /// <summary>레시피를 JSON 글자로 썼다가 개인 에셋처럼(느슨하게) 다시 읽는다.</summary>
    private static EffectRecipe RoundTrip(EffectRecipe recipe)
    {
        var json = JsonNode.Parse(EffectRecipeFormat.Write(recipe).ToJsonString());
        var context = new AssetReadContext("recipe.json", strict: false);
        var read = EffectRecipeFormat.Read(json, context);
        Assert.Empty(context.Issues);
        return read!;
    }

    private static IReadOnlyList<LayerEffect> LegacySingle(string json)
        => Assert.Single(AssetMigration.ReadLegacyEffects(json)).Effects;

    [Fact]
    public void CaptureSerializeParseInstantiate_PreservesStackAndResetsFrames()
    {
        var recipe = EffectRecipe.Capture("personal.a", "그라데이션 외곽선",
        [
            new OutlineEffect { Color = new Color32(10, 20, 30), Thickness = 2, Frames = new FrameRange(1, 3), ClipToPrevious = true },
            new ColorOverlayEffect { Color = new Color32(40, 50, 60), ClipToPrevious = true, Amount = 75, Frames = new FrameRange(2, 4) },
        ]);

        Assert.All(recipe.Effects, effect => Assert.Null(effect.Frames));
        // 첫 효과는 클리핑할 앞 효과가 없으므로 꺼진다
        Assert.False(recipe.Effects[0].ClipToPrevious);
        var parsed = RoundTrip(recipe);
        Assert.Equal(recipe.Id, parsed.Id);
        Assert.Equal(recipe.Name, parsed.Name);
        Assert.Equal(recipe.Effects, parsed.Effects);

        var range = new FrameRange(4, 7);
        var instance = parsed.Instantiate(new SpriteProject(4, 4), range);
        Assert.All(instance, effect => Assert.Equal(range, effect.Frames));
        Assert.False(instance[0].ClipToPrevious);
        Assert.True(instance[1].ClipToPrevious);

        // 모든 프레임 레시피는 부르는 쪽 범위 대신 범위 없음(모든 프레임)
        var all = RoundTrip(parsed with { FrameRange = RecipeFrameRange.AllFrames, Description = "설명", PreviewFrames = 30 });
        Assert.Equal((RecipeFrameRange.AllFrames, "설명", 30), (all.FrameRange, all.Description, all.PreviewFrames));
        Assert.All(all.Instantiate(new SpriteProject(4, 4), range), effect => Assert.Null(effect.Frames));
    }

    [Fact]
    public void Parse_KeepsUnknownEffectsAndDuplicateNameUsesLastPreset()
    {
        const string json = """
            {
              "version": 1,
              "presets": [
                { "name": "효과", "effects": [ { "type": "outline", "thickness": 1 } ] },
                { "name": "다른 효과", "effects": [ { "type": "fade" } ] },
                { "name": "효과", "effects": [ { "type": "future", "power": 9 }, { "type": "shadow", "x": 3 } ] }
              ]
            }
            """;

        var legacy = AssetMigration.ReadLegacyEffects(json);
        Assert.Equal(["효과", "다른 효과"], legacy.Select(item => item.Name).ToArray());
        var effects = legacy[0].Effects;
        Assert.Equal(2, effects.Count);
        var unknown = Assert.IsType<MissingEffect>(effects[0]);
        Assert.Equal("future", unknown.TypeId);
        Assert.Equal(3, Assert.IsType<ShadowEffect>(effects[1]).OffsetX);

        // 개인 레시피로 다시 저장해도 모르는 효과의 원본 설정이 남는다
        var again = RoundTrip(EffectRecipe.Capture("personal.b", legacy[0].Name, effects));
        var kept = Assert.IsType<MissingEffect>(again.Effects[0]);
        Assert.Equal("future", kept.TypeId);
        Assert.Equal(EffectCodecJson(effects), EffectCodecJson(again.Effects));
    }

    [Fact]
    public void LegacyFile_SkipsBlankNamesAndEmptyLists_RejectsCorruptOrFutureFile()
    {
        const string json = """
            { "version": 2, "presets": [
              { "name": "  ", "effects": [ { "type": "gpixel.effect.fade" } ] },
              { "name": "비어 있음", "effects": [] },
              { "name": " 남음 ", "effects": [ { "type": "gpixel.effect.fade", "version": 1, "settings": { "fadeOut": true } } ] }
            ] }
            """;
        var item = Assert.Single(AssetMigration.ReadLegacyEffects(json));
        Assert.Equal("남음", item.Name);
        Assert.True(Assert.IsType<FadeEffect>(Assert.Single(item.Effects)).FadeOut);

        Assert.Throws<InvalidDataException>(() => AssetMigration.ReadLegacyEffects("{ \"version\": 2, \"presets\": ["));
        Assert.Throws<InvalidDataException>(() => AssetMigration.ReadLegacyEffects("""{ "version": 3, "presets": [] }"""));
    }

    [Fact]
    public void OutlineHideOriginal_RoundTrips_OldJsonLoadsOff()
    {
        var on = EffectRecipe.Capture("personal.c", "선만", [new OutlineEffect { Color = new Color32(10, 20, 30), Inside = true, HideOriginal = true }]);
        Assert.Equal(on.Effects, RoundTrip(on).Effects);

        const string old = """
            { "version": 1, "presets": [ { "name": "예전", "effects": [ { "type": "outline", "thickness": 2, "inside": false } ] } ] }
            """;
        var outline = Assert.IsType<OutlineEffect>(Assert.Single(LegacySingle(old)));
        Assert.False(outline.HideOriginal);
        Assert.Equal(2, outline.Thickness);
    }

    [Fact]
    public void OutlineShadeMode_RoundTrips_OldJsonLoadsFixedColor()
    {
        var shade = EffectRecipe.Capture("personal.d", "명암", [new OutlineEffect { ColorMode = OutlineColorMode.Shade, ShadeAmount = 25, Thickness = 2 }]);
        Assert.Equal(shade.Effects, RoundTrip(shade).Effects);

        const string old = """
            { "version": 1, "presets": [ { "name": "예전", "effects": [
              { "type": "outline", "color": "#102030", "thickness": 1 },
              { "type": "outline", "mode": "Shade", "amount": 25 }
            ] } ] }
            """;
        var loaded = LegacySingle(old).Cast<OutlineEffect>().ToArray();
        Assert.Equal(OutlineColorMode.Fixed, loaded[0].ColorMode);
        Assert.Equal(OutlineEffect.DefaultShadeAmount, loaded[0].ShadeAmount);
        Assert.Equal(new Color32(16, 32, 48), loaded[0].Color);
        Assert.Equal((OutlineColorMode.Shade, 25), (loaded[1].ColorMode, loaded[1].ShadeAmount));
    }

    [Fact]
    public void ReflectionDirection_RoundTrips_OldAboveFlagKeepsMeaning()
    {
        var walls = EffectRecipe.Capture("personal.e", "벽", [
            new ReflectionEffect { Direction = ReflectionDirection.Left, Gap = 2, Wave = 1 },
            new ReflectionEffect { Direction = ReflectionDirection.Right, Height = 30 },
        ]);
        Assert.Equal(walls.Effects, RoundTrip(walls).Effects);

        const string old = """
            { "version": 1, "presets": [ { "name": "예전", "effects": [
              { "type": "reflection", "above": true },
              { "type": "reflection", "above": false },
              { "type": "reflection" },
              { "type": "reflection", "direction": "nowhere", "above": true }
            ] } ] }
            """;
        var loaded = LegacySingle(old).Cast<ReflectionEffect>().Select(r => r.Direction);
        Assert.Equal([ReflectionDirection.Up, ReflectionDirection.Down, ReflectionDirection.Down, ReflectionDirection.Up], loaded.ToArray());
    }

    private static string[] EffectCodecJson(IEnumerable<LayerEffect> effects)
        => effects.Select(e => Gpixel.Core.Effects.EffectCodec.Write(e).ToJsonString()).ToArray();
}
