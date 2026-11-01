using System.Text;
using Gpixel.Core.Assets;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>효과 레시피 매개변수(계획 14장 3단계): 연결 검사, 값 바꾸기, 프로젝트 저장·다시 열기, 실행취소.</summary>
public class RecipeParameterTests
{
    private const string AssetId = AssetSamples.PackageId + ".x";
    private static readonly Color32 Glow = new(0, 255, 200), Red = new(255, 0, 0);

    /// <summary>외곽선 → 그림자 → (클리핑) 색 덮기, 매개변수 넷: 색 둘(외곽선·색 덮기), 두께, 대각선, 섞기.</summary>
    private static EffectRecipe Recipe() => EffectRecipe.Capture("", "Neon Hit",
    [
        new OutlineEffect { Color = new Color32(10, 10, 10), Thickness = 1 },
        new ShadowEffect { OffsetX = 2, OffsetY = 1 },
        new ColorOverlayEffect { Color = new Color32(0, 0, 255), Amount = 60, ClipToPrevious = true },
    ]).WithParameters(
    [
        new RecipeParameter
        {
            Id = "glow", Name = "빛 색", Kind = RecipeParameterKind.Color, Default = RecipeValue.Of(Glow),
            Bindings = [new RecipeBinding(0, "color"), new RecipeBinding(2, "color")],
        },
        new RecipeParameter
        {
            Id = "width", Name = "두께", Kind = RecipeParameterKind.Int, Default = RecipeValue.Of(2), Min = 1, Max = 2,
            Bindings = [new RecipeBinding(0, "thickness")],
        },
        new RecipeParameter
        {
            Id = "corners", Name = "모서리", Kind = RecipeParameterKind.Bool, Default = RecipeValue.Of(true),
            Bindings = [new RecipeBinding(0, "diagonal")],
        },
        new RecipeParameter
        {
            Id = "blend", Name = "섞기", Kind = RecipeParameterKind.Choice, Default = RecipeValue.OfChoice("Screen"), Choices = ["Normal", "Screen"],
            Bindings = [new RecipeBinding(2, "blend")],
        },
    ]);

    private static IReadOnlyList<AssetIssue> Rejects(string recipeJson)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["assets/effects/x.json"] = Encoding.UTF8.GetBytes(recipeJson) };
        using var stream = new MemoryStream();
        AssetPackageWriter.Write(stream, AssetSamples.Header() with { Assets = [new AssetManifestEntry(AssetId, AssetKinds.EffectRecipe, "assets/effects/x.json", null)] }, files);
        return Assert.Throws<AssetPackageException>(() => AssetPackageValidator.Read(new MemoryStream(stream.ToArray()))).Issues;
    }

    private static string RecipeJson(string parameters, int schema = 2) => $$"""
        { "schemaVersion": {{schema}}, "id": "{{AssetId}}", "name": "X",
          "effects": [ { "type": "gpixel.effect.outline" }, { "type": "gpixel.effect.shadow" }, { "type": "gpixel.effect.coloroverlay" } ],
          "parameters": [ {{parameters}} ] }
        """;

    private static PixelImage Square()
    {
        var cel = new PixelImage(24, 24);
        for (int y = 8; y < 16; y++)
            for (int x = 8; x < 16; x++)
                cel[x, y] = new Color32(200, 40, 40);
        return cel;
    }

    // ───────────── 패키지 검사 ─────────────

    [Fact]
    public void PackageWithParameters_RoundTrips_AndInstantiateAppliesDefaultsAsOneGroup()
    {
        var recipe = Recipe();
        var package = AssetPackageValidator.Read(new MemoryStream(AssetSamples.Build(recipe: recipe)));
        var read = Assert.IsType<EffectRecipe>(package.Items[0].Data);
        Assert.Equal(recipe.Parameters, read.Parameters);
        AssetSamples.AssertSameRecipe(recipe, read);

        var effects = read.Instantiate(new SpriteProject(4, 4), new FrameRange(1, 3));
        var outline = Assert.IsType<OutlineEffect>(effects[0]);
        var overlay = Assert.IsType<ColorOverlayEffect>(effects[2]);
        Assert.Equal((Glow, 2, true), (outline.Color, outline.Thickness, outline.Diagonal));
        Assert.Equal((Glow, EffectBlend.Screen, 60), (overlay.Color, overlay.Blend, overlay.Amount));
        // 세 효과가 같은 묶음(새 ID, 출처 = 레시피 ID)의 0·1·2번 자리
        Assert.Equal([0, 1, 2], effects.Select(e => e.Recipe!.Slot).ToArray());
        var instance = effects[0].Recipe!.Instance;
        Assert.All(effects, e => Assert.Equal(instance, e.Recipe!.Instance));
        Assert.Equal((read.Id, "Neon Hit", 32), (instance.SourceId, instance.Name, instance.Id.Length));
        Assert.NotEqual(instance.Id, read.Instantiate(new SpriteProject(4, 4), null)[0].Recipe!.Instance.Id);

        // 개인 에셋도 매개변수를 저장하고, 잘못된 연결은 저장하지 않는다
        using var dir = new TempDir();
        var store = AssetStore.Open(Path.Combine(dir.Path, "Assets"));
        var saved = store.SavePersonalRecipe(recipe);
        Assert.Equal(recipe.Parameters, AssetStore.Open(store.Root).Catalog.Find(saved.Id)!.Recipe!.Parameters);
        var wrong = recipe with { Parameters = [recipe.Parameters[0] with { Bindings = [new RecipeBinding(9, "color")] }] };
        Assert.Equal(AssetIssueCode.RecipeBindingMissingEffect, Assert.Single(Assert.Throws<AssetPackageException>(() => store.SavePersonalRecipe(wrong)).Issues).Code);
    }

    [Fact]
    public void RecipeWithoutParameters_IsWrittenAsSchemaOne()
        => Assert.Equal(1, (int)EffectRecipeFormat.Write(EffectRecipe.Capture("", "X", [new FadeEffect()]))["schemaVersion"]!);

    public static TheoryData<string, AssetIssueCode, string> WrongBindings => new()
    {
        {
            """{ "id": "a", "name": "A", "kind": "color", "default": "#FFFFFF", "bindings": [ { "effect": 5, "key": "color" } ] }""",
            AssetIssueCode.RecipeBindingMissingEffect, "parameters[0].bindings[0]: effects[5]"
        },
        {
            """{ "id": "a", "name": "A", "kind": "color", "default": "#FFFFFF", "bindings": [ { "effect": 0, "key": "glow" } ] }""",
            AssetIssueCode.RecipeBindingUnknownKey, "parameters[0].bindings[0]: gpixel.effect.outline.glow"
        },
        {
            """{ "id": "a", "name": "A", "kind": "color", "default": "#FFFFFF", "bindings": [ { "effect": 0, "key": "thickness" } ] }""",
            AssetIssueCode.RecipeBindingKindMismatch, "parameters[0].bindings[0]: effects[0].thickness int / color"
        },
        {
            """{ "id": "a", "name": "A", "kind": "int", "default": 2, "min": 1, "max": 9, "bindings": [ { "effect": 0, "key": "thickness" } ] }""",
            AssetIssueCode.RecipeParameterOutOfRange, "parameters[0].max: 9 > effects[0].thickness 3"
        },
        {
            // 두 설정 범위의 겹침 밖(그림자 거리는 음수도 받지만 외곽선 두께는 1부터)
            """{ "id": "a", "name": "A", "kind": "int", "default": 1, "min": 0, "max": 3, "bindings": [ { "effect": 1, "key": "offsetX" }, { "effect": 0, "key": "thickness" } ] }""",
            AssetIssueCode.RecipeParameterOutOfRange, "parameters[0].min: 0 < effects[0].thickness 1"
        },
        {
            """{ "id": "a", "name": "A", "kind": "int", "default": 4, "min": 1, "max": 3, "bindings": [ { "effect": 0, "key": "thickness" } ] }""",
            AssetIssueCode.RecipeParameterOutOfRange, "parameters[0].default: 4"
        },
        {
            // 고를 수 있는 값은 연결된 모든 설정에서 골라져야 한다(디더에는 Screen이 없음)
            """{ "id": "a", "name": "A", "kind": "choice", "default": "Screen", "choices": [ "Screen" ], "bindings": [ { "effect": 2, "key": "blend" }, { "effect": 1, "key": "dither" } ] }""",
            AssetIssueCode.RecipeParameterOutOfRange, "parameters[0].choices: Screen / effects[1].dither"
        },
        {
            """{ "id": "a", "name": "A", "kind": "choice", "default": "Multiply", "choices": [ "Normal", "Screen" ], "bindings": [ { "effect": 2, "key": "blend" } ] }""",
            AssetIssueCode.RecipeParameterOutOfRange, "parameters[0].default: Multiply"
        },
        {
            """{ "id": "a", "name": "A", "kind": "color", "default": "#FFFFFF", "bindings": [ { "effect": 0, "key": "color" } ] }""" + ", " + """{ "id": "b", "name": "B", "kind": "color", "default": "#000000", "bindings": [ { "effect": 0, "key": "color" } ] }""",
            AssetIssueCode.RecipeBindingDuplicate, "parameters[1].bindings[0]: effects[0].color"
        },
        {
            """{ "id": "a", "name": "A", "kind": "bool", "default": true, "bindings": [ ] }""",
            AssetIssueCode.InvalidField, "parameters[0].bindings"
        },
        {
            """{ "id": "a", "name": "A", "kind": "bool", "default": true, "bindings": [ { "effect": 0, "key": "diagonal" } ] }""" + ", " + """{ "id": "a", "name": "B", "kind": "bool", "default": true, "bindings": [ { "effect": 0, "key": "inside" } ] }""",
            AssetIssueCode.InvalidField, "parameters[1].id"
        },
        {
            """{ "id": "a", "name": "A", "kind": "int", "default": 1, "min": 1, "max": 3, "choices": [ "x" ], "bindings": [ { "effect": 0, "key": "thickness" } ] }""",
            AssetIssueCode.InvalidField, "parameters[0].choices"
        },
        {
            """{ "id": "a", "name": "A", "kind": "number", "default": 1, "bindings": [ { "effect": 0, "key": "thickness" } ] }""",
            AssetIssueCode.InvalidField, "parameters[0].kind"
        },
    };

    [Theory]
    [MemberData(nameof(WrongBindings))]
    public void WrongBindingsAndRanges_AreRejectedWithStructuredIssues(string parameters, AssetIssueCode code, string detail)
    {
        var issues = Rejects(RecipeJson(parameters));
        var issue = Assert.Single(issues, i => i.Code == code && i.Detail == detail);
        Assert.Equal("assets/effects/x.json", issue.Path);
        // 문구는 종류 이름으로 떨어지지 않는다(한국어·영어 문구가 있음)
        Assert.DoesNotContain(code.ToString(), issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameters_InSchemaOneRecipe_AreRejected()
        => Assert.Contains(Rejects(RecipeJson("""{ "id": "a", "name": "A", "kind": "bool", "default": true, "bindings": [ { "effect": 0, "key": "diagonal" } ] }""", schema: 1)),
            i => i.Code == AssetIssueCode.InvalidField && i.Detail == "parameters");

    [Fact]
    public void BindingToListSetting_IsKindMismatch()
    {
        var bad = RecipeParameters.Check([new PaletteSwapEffect()],
        [
            new RecipeParameter
            {
                Id = "a", Name = "A", Kind = RecipeParameterKind.Color, Default = RecipeValue.Of(Red),
                Bindings = [new RecipeBinding(0, EffectRegistry.Of(new PaletteSwapEffect())!.Parameters.First(p => !p.Exposed).Key)],
            },
        ], (code, _) => Assert.Equal(AssetIssueCode.RecipeBindingKindMismatch, code));
        Assert.Equal([0], bad.ToArray());
    }

    // ───────────── 값 바꾸기 ─────────────

    [Fact]
    public void ChangingParameter_UpdatesEveryLinkedSetting_AndNothingElse()
    {
        var plain = new OutlineEffect { Color = new Color32(1, 2, 3) };
        IReadOnlyList<LayerEffect> list = [plain, .. Recipe().Instantiate(new SpriteProject(4, 4), null)];
        var instance = list[1].Recipe!.Instance;

        var changed = RecipeInstances.WithValue(list, instance.Id, "glow", RecipeValue.Of(Red));
        Assert.Same(plain, changed[0]);
        Assert.Equal(Red, ((OutlineEffect)changed[1]).Color);
        Assert.Same(list[2], changed[2]);
        Assert.Equal(Red, ((ColorOverlayEffect)changed[3]).Color);
        Assert.Equal(RecipeValue.Of(Red), RecipeInstances.ValueOf(changed, instance, instance.Parameters[0]));

        // 정수는 매개변수 범위(1~2)로 자른다
        var wide = RecipeInstances.WithValue(changed, instance.Id, "width", RecipeValue.Of(9));
        Assert.Equal(2, ((OutlineEffect)wide[1]).Thickness);
        var thin = RecipeInstances.WithValue(changed, instance.Id, "width", RecipeValue.Of(1));
        Assert.Equal(1, ((OutlineEffect)thin[1]).Thickness);
        // 내보내지 않은 고르기 값은 받지 않는다
        Assert.Same(changed, RecipeInstances.WithValue(changed, instance.Id, "blend", RecipeValue.OfChoice("Multiply")));
        Assert.Same(changed, RecipeInstances.WithValue(changed, instance.Id, "blend", RecipeValue.Of(3)));
        Assert.Equal(EffectBlend.Normal, ((ColorOverlayEffect)RecipeInstances.WithValue(changed, instance.Id, "blend", RecipeValue.OfChoice("Normal"))[3]).Blend);

        // 구성 효과를 하나 지워도 남은 연결은 바뀐다
        IReadOnlyList<LayerEffect> without = [.. changed.Where((_, i) => i != 1)];
        var after = RecipeInstances.WithValue(without, instance.Id, "glow", RecipeValue.Of(Glow));
        Assert.Equal(Glow, ((ColorOverlayEffect)after[2]).Color);
        Assert.Equal(RecipeValue.Of(Glow), RecipeInstances.ValueOf(after, instance, instance.Parameters[0]));

        // 묶음을 풀면 값은 그대로 두고 보통 효과가 된다
        var detached = RecipeInstances.Detach(changed, instance.Id);
        Assert.All(detached, e => Assert.Null(e.Recipe));
        Assert.Equal(Red, ((OutlineEffect)detached[1]).Color);
    }

    // ───────────── 프로젝트 ─────────────

    [Fact]
    public void InsertedRecipe_SurvivesSaveAndOpen_WithoutPackage_AndStillEditsAndRenders()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(Path.Combine(dir.Path, "Assets"));
        store.Install(AssetSamples.Save(dir.Path, AssetSamples.Build(recipe: Recipe())));
        var project = new SpriteProject(24, 24);
        project.SetCel(0, 0, Square());
        project.SetLayerEffects(0, store.Catalog.Recipes.Single().Recipe!.Instantiate(project, null));
        string file = dir.File("hero.gpixel");
        ProjectIO.Save(project, file);
        var instance = project.Layers[0].Effects[0].Recipe!.Instance;
        var expected = project.Flatten(0).Pixels.ToArray();

        store.Delete(AssetSamples.PackageId);
        Assert.Empty(store.Catalog.Recipes);
        var reloaded = ProjectIO.Load(file);
        var effects = reloaded.Layers[0].Effects;
        Assert.All(effects, e => Assert.Equal(instance, e.Recipe!.Instance));
        Assert.Equal([0, 1, 2], effects.Select(e => e.Recipe!.Slot).ToArray());
        Assert.Equal(expected, reloaded.Flatten(0).Pixels);

        // 패키지 없이 매개변수를 바꾸면 연결된 설정이 모두 바뀌고 그림도 바뀐다
        reloaded.SetLayerEffects(0, RecipeInstances.WithValue(effects, instance.Id, "glow", RecipeValue.Of(Red)));
        Assert.Equal(Red, ((OutlineEffect)reloaded.Layers[0].Effects[0]).Color);
        Assert.Equal(Red, ((ColorOverlayEffect)reloaded.Layers[0].Effects[2]).Color);
        Assert.NotEqual(expected, reloaded.Flatten(0).Pixels);
        // 같은 값을 손으로 넣은 효과와 그림이 같다
        var manual = new SpriteProject(24, 24);
        manual.SetCel(0, 0, Square());
        manual.SetLayerEffects(0, [.. RecipeInstances.Detach(reloaded.Layers[0].Effects, instance.Id)]);
        Assert.Equal(manual.Flatten(0).Pixels, reloaded.Flatten(0).Pixels);

        // 바꾼 값도 저장·다시 열기에 남는다
        ProjectIO.Save(reloaded, file);
        var again = ProjectIO.Load(file);
        Assert.Equal(RecipeValue.Of(Red), RecipeInstances.ValueOf(again.Layers[0].Effects, instance, instance.Parameters[0]));
    }

    [Fact]
    public void ParameterChange_IsOneUndoStep()
    {
        var project = new SpriteProject(24, 24);
        project.SetCel(0, 0, Square());
        project.SetLayerEffects(0, Recipe().Instantiate(project, null));
        var before = project.Layers[0].Effects;
        var instance = before[0].Recipe!.Instance;

        project.SetLayerEffects(0, RecipeInstances.WithValue(before, instance.Id, "glow", RecipeValue.Of(Red)));
        var after = project.Layers[0].Effects;
        Assert.Equal([Red, Red], new[] { ((OutlineEffect)after[0]).Color, ((ColorOverlayEffect)after[2]).Color });

        Assert.True(project.Undo());
        Assert.Equal(before, project.Layers[0].Effects);
        Assert.True(project.Redo());
        Assert.Equal(after, project.Layers[0].Effects);
    }
}
