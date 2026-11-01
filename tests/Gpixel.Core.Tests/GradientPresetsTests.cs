using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.Edit;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>그라데이션 에셋 JSON 왕복, 예전 그라데이션 프리셋 파일 읽기, 기본 그라데이션과 기본값 되돌리기.</summary>
public class GradientPresetsTests
{
    private static GradientPreset Example(string name = "Evening") => new()
    {
        Name = name,
        Stops = [new(0, new Color32(190, 20, 90, 127)), new(0.4, new Color32(250, 130, 10)), new(1, new Color32(16, 30, 170))],
        AlphaStops = [new(0, 10), new(0.7, 240), new(1, 100)],
        Interpolation = GradientInterpolation.Smooth, Dither = GradientDither.Bayer4,
        ColorSpace = GradientColorSpace.Hsl, Angle = 83, Shape = EffectGradientShape.Angular,
        Animated = true, Reverse = true, Cycles = 4, Easing = EffectEasing.EaseInOut,
        PingPong = true, Favorite = true,
    };

    /// <summary>그라데이션 에셋을 JSON 글자로 썼다가 엄격하게(패키지처럼) 다시 읽는다.</summary>
    private static GradientPreset RoundTrip(GradientPreset gradient, string id = "author.pack.g")
    {
        var json = JsonNode.Parse(GradientAssetFormat.Write(id, gradient).ToJsonString());
        var context = new AssetReadContext("assets/gradients/g.json", strict: true);
        var read = GradientAssetFormat.Read(json, context);
        Assert.Empty(context.Issues);
        Assert.Equal(id, read!.Id);
        return read.Gradient;
    }

    /// <summary>예전 사용자 데이터가 없는 새 저장소(기본 그라데이션만 있음).</summary>
    private static AssetStore SeededStore(TempDir dir)
    {
        var store = AssetStore.Open(Path.Combine(dir.Path, "Assets"));
        AssetMigration.Run(store, dir.Path);
        return store;
    }

    [Fact]
    public void RoundTrip_PreservesEverySettingAndIndependentStopLists_FavoriteIsNotWritten()
    {
        var originalStops = new List<GradientStop>(Example().Stops);
        var originalAlpha = new List<GradientAlphaStop>(Example().AlphaStops);
        var preset = Example() with { Stops = originalStops, AlphaStops = originalAlpha };
        originalStops[0] = new GradientStop(0, Color32.Black);
        originalAlpha[0] = new GradientAlphaStop(0, 0);

        // 즐겨찾기는 컴퓨터별 상태라 에셋 파일에 쓰지 않는다
        Assert.Null(GradientAssetFormat.Write("author.pack.g", preset)["favorite"]);
        var parsed = RoundTrip(preset);
        Assert.Equal(preset.Name, parsed.Name);
        Assert.Equal(preset.Stops, parsed.Stops);
        Assert.Equal(preset.AlphaStops, parsed.AlphaStops);
        Assert.Equal((preset.Interpolation, preset.Dither, preset.ColorSpace, preset.Angle, preset.Shape,
                preset.Animated, preset.Reverse, preset.Cycles, preset.Easing, preset.PingPong),
            (parsed.Interpolation, parsed.Dither, parsed.ColorSpace, parsed.Angle, parsed.Shape,
                parsed.Animated, parsed.Reverse, parsed.Cycles, parsed.Easing, parsed.PingPong));
        Assert.False(parsed.Favorite);
        Assert.NotSame(preset.Stops, parsed.Stops);
        Assert.NotSame(preset.AlphaStops, parsed.AlphaStops);
        Assert.Equal(new Color32(190, 20, 90, 127), parsed.Stops[0].Color);
        Assert.Equal((byte)10, parsed.AlphaStops[0].Alpha);

        var effect = parsed.ToGradient();
        Assert.Equal(parsed.Stops, effect.Stops);
        Assert.Equal(parsed.AlphaStops, effect.AlphaStops);
        Assert.Equal(parsed.ColorSpace, effect.ColorSpace);
        Assert.Equal(parsed.Shape, effect.Shape);
        Assert.Equal(parsed.Easing, effect.Easing);
        var recaptured = GradientPreset.Capture("Copy", effect);
        Assert.Equal(parsed.Stops, recaptured.Stops);
        Assert.Equal(parsed.AlphaStops, recaptured.AlphaStops);
        Assert.NotSame(effect.Stops, recaptured.Stops);
        Assert.NotSame(effect.AlphaStops, recaptured.AlphaStops);
    }

    [Fact]
    public void Snapshot_RetainsRenderedColorsForGradientToolAndEffect()
    {
        var source = new EffectColorGradient
        {
            Stops = [new(0, new Color32(233, 30, 11, 100)), new(1, new Color32(13, 81, 201, 233))],
            AlphaStops = [new(0, 255), new(1, 80)],
            ColorSpace = GradientColorSpace.OkLab, Interpolation = GradientInterpolation.Steps,
            Dither = GradientDither.Bayer2, Shape = EffectGradientShape.Square,
            Angle = 72, Reverse = true, Cycles = 3, Animated = true,
            Easing = EffectEasing.EaseOut, PingPong = true,
        };
        var restored = RoundTrip(GradientPreset.Capture("Colors", source)).ToGradient();
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                double t = (x + y) / 6.0;
                Assert.Equal(Gradient.Sample(source.Stops, source.AlphaStops, t, source.Interpolation,
                        source.Dither, x, y, source.ColorSpace),
                    Gradient.Sample(restored.Stops, restored.AlphaStops, t, restored.Interpolation,
                        restored.Dither, x, y, restored.ColorSpace));
            }
        Assert.Equal(source.Angle, restored.Angle);
        Assert.Equal(source.Shape, restored.Shape);
        Assert.Equal(source.Reverse, restored.Reverse);
        Assert.Equal(source.Cycles, restored.Cycles);
        Assert.Equal(source.Animated, restored.Animated);
        Assert.Equal(source.Easing, restored.Easing);
        Assert.Equal(source.PingPong, restored.PingPong);
    }

    [Fact]
    public void Capture_NoAlphaStops_MakesOpaqueCurveWithoutChangingSampling()
    {
        var effect = new EffectColorGradient
        {
            Stops = [new(0, new Color32(100, 40, 20, 128)), new(1, new Color32(20, 100, 40, 240))],
        };
        var preset = GradientPreset.Capture("Opaque", effect);
        Assert.Equal([new GradientAlphaStop(0, 255), new GradientAlphaStop(1, 255)], preset.AlphaStops);
        var loaded = RoundTrip(preset);
        Assert.Equal(Gradient.Sample(effect.Stops, effect.AlphaStops, 0.3, colorSpace: effect.ColorSpace),
            Gradient.Sample(loaded.Stops, loaded.AlphaStops, 0.3, colorSpace: loaded.ColorSpace));
    }

    [Fact]
    public void LegacyParse_EmptyCatalogAndDuplicateNames_LastCaseInsensitiveEntryWinsInFirstSlot()
    {
        Assert.Empty(AssetMigration.ReadLegacyGradients("""{"version":2,"presets":[]}"""));
        const string onDisk = """
            {"version":2,"presets":[{"name":"  SKY  ","favorite":true,"colorSpace":"Hsl","stops":[{"position":0,"color":"#FF0000"},{"position":1,"color":"#0000FF"}]},
            {"name":"Moon","stops":[{"position":0,"color":"#FFFFFF"},{"position":1,"color":"#000000"}]},
            {"name":"sky","stops":[{"position":0,"color":"#00FF00"},{"position":1,"color":"#0000FF"}]},
            {"name":"Sun","stops":[{"position":0,"color":"#FF0000"},{"position":1,"color":"#0000FF"}]},
            {"name":"sun","favorite":true,"stops":[{"position":0,"color":"#00FF00"},{"position":1,"color":"#0000FF"}]}]}
            """;
        var deduplicated = AssetMigration.ReadLegacyGradients(onDisk);
        Assert.Equal(["sky", "Moon", "sun"], deduplicated.Select(p => p.Name).ToArray());
        Assert.False(deduplicated[0].Favorite);
        Assert.Equal(new Color32(0, 255, 0), deduplicated[0].Stops[0].Color);
        Assert.Equal(new Color32(0, 255, 0), deduplicated[2].Stops[0].Color);
        Assert.True(deduplicated[2].Favorite);
    }

    [Fact]
    public void LegacyParse_OlderMissingFields_DefaultsToRgbAndOpaqueAlpha()
    {
        const string json = """
            {"version":1,"presets":[{"name":"Older","stops":[
              {"position":0,"color":"#FF000080"},{"position":1,"color":"#0000FF"}
            ]}]}
            """;
        var old = AssetMigration.ReadLegacyGradients(json)[0];
        Assert.Equal("Older", old.Name);
        Assert.Equal(GradientColorSpace.Rgb, old.ColorSpace);
        Assert.Equal(GradientDither.None, old.Dither);
        Assert.Equal(GradientInterpolation.Smooth, old.Interpolation);
        Assert.Equal(EffectGradientShape.Linear, old.Shape);
        Assert.Equal(1, old.Cycles);
        Assert.Equal([new GradientAlphaStop(0, 255), new GradientAlphaStop(1, 255)], old.AlphaStops);
        Assert.False(old.Favorite);
        Assert.Equal(new Color32(255, 0, 0, 128), old.Stops[0].Color);
        Assert.Equal(Gradient.Sample(old.Stops, [], 0.5, colorSpace: GradientColorSpace.Rgb),
            Gradient.Sample(old.Stops, old.AlphaStops, 0.5, colorSpace: old.ColorSpace));
        // 새 형식으로 옮겨 써도 RGB로 남는다(새 기본값 OkLch로 바뀌지 않음)
        Assert.Equal(GradientColorSpace.Rgb, RoundTrip(old).ColorSpace);
    }

    /// <summary>1판 파일은 2판에서 더한 기본 그라데이션(Fire, Forest, Grayscale) 중 없는 이름만 받는다(대소문자 무시).</summary>
    [Fact]
    public void LegacyVersionOne_GainsNewBuiltInsMissingByName()
    {
        var upgraded = AssetMigration.ReadLegacyGradients("""
            {"version":1,"presets":[{"name":"Mine","stops":[{"position":0,"color":"#FF0000"},{"position":1,"color":"#0000FF"}]},
            {"name":"fire","stops":[{"position":0,"color":"#000000"},{"position":1,"color":"#FFFFFF"}]}]}
            """);
        Assert.Equal(["Mine", "fire", "Forest", "Grayscale"], upgraded.Select(p => p.Name).ToArray());
        Assert.Equal(Color32.Black, upgraded[1].Stops[0].Color);
        var builtIns = GradientPresets.BuiltIns();
        Assert.Equal(builtIns.Single(p => p.Name == "Grayscale").Stops, upgraded[3].Stops);

        // 2판 파일은 사용자가 지운 기본 그라데이션을 되살리지 않는다
        var second = AssetMigration.ReadLegacyGradients("""
            {"version":2,"presets":[{"name":"Mine","stops":[{"position":0,"color":"#FF0000"},{"position":1,"color":"#0000FF"}]}]}
            """);
        Assert.Equal(["Mine"], second.Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"version\":3,\"presets\":[]}")]
    [InlineData("{\"version\":0,\"presets\":[]}")]
    [InlineData("{\"version\":1,\"presets\":[null]}")]
    [InlineData("{\"version\":1,\"presets\":[{\"name\":\"Blank\",\"stops\":[]}]}")]
    [InlineData("{\"version\":1,\"presets\":[{\"name\":\"Broken\",\"stops\":[{\"position\":0,\"color\":\"#123456\"},{\"position\":1,\"color\":\"not-a-color\"}]}]}")]
    public void LegacyParse_DoesNotTreatMalformedCatalogAsEmpty(string json)
        => Assert.Throws<InvalidDataException>(() => AssetMigration.ReadLegacyGradients(json));

    [Theory]
    [InlineData("\"alphaStops\":[{\"position\":0,\"alpha\":-1},{\"position\":1,\"alpha\":255}]")]
    [InlineData("\"alphaStops\":[{\"position\":0,\"alpha\":256},{\"position\":1,\"alpha\":255}]")]
    [InlineData("\"alphaStops\":[]")]
    [InlineData("\"interpolation\":\"Future\"")]
    [InlineData("\"colorSpace\":\"999\"")]
    [InlineData("\"angle\":360")]
    [InlineData("\"cycles\":0")]
    [InlineData("\"stops\":[{\"position\":-0.01,\"color\":\"#FFFFFF\"},{\"position\":1,\"color\":\"#000000\"}]")]
    public void InvalidValues_AreRejectedByLegacyParseAndAssetFormat(string field)
    {
        string stops = field.StartsWith("\"stops\"", StringComparison.Ordinal) ? "" :
            "\"stops\":[{\"position\":0,\"color\":\"#FFFFFF\"},{\"position\":1,\"color\":\"#000000\"}],";
        var legacy = "{\"version\":1,\"presets\":[{\"name\":\"Broken\"," + stops + field + "}]}";
        Assert.Throws<InvalidDataException>(() => AssetMigration.ReadLegacyGradients(legacy));

        var asset = JsonNode.Parse("{\"schemaVersion\":1,\"id\":\"author.pack.broken\",\"name\":\"Broken\"," + stops + field + "}");
        foreach (bool strict in new[] { true, false })
        {
            var context = new AssetReadContext("assets/gradients/broken.json", strict);
            Assert.Null(GradientAssetFormat.Read(asset, context));
            Assert.Contains(context.Issues, issue => issue.Code == AssetIssueCode.InvalidField);
        }
    }

    [Fact]
    public void BuiltIns_AreFreshEditableCopiesWithExpectedColors()
    {
        var first = GradientPresets.BuiltIns();
        var second = GradientPresets.BuiltIns();
        var rainbow = Assert.Single(first, p => p.Name == "Rainbow");
        Assert.Equal([new Color32(255, 0, 0), new Color32(255, 140, 0), new Color32(255, 255, 0),
            new Color32(0, 190, 50), new Color32(0, 80, 255), new Color32(148, 0, 211)],
            rainbow.Stops.Select(s => s.Color).ToArray());
        var repeated = Assert.Single(first, p => p.Name == "Repeating Rainbow");
        Assert.Equal(repeated.Stops[0].Color, repeated.Stops[^1].Color);
        Assert.Equal(0, repeated.Stops[0].Position);
        Assert.Equal(1, repeated.Stops[^1].Position);
        Assert.Contains(first, p => p.Name == "Sunset");
        Assert.Contains(first, p => p.Name == "Ocean");
        Assert.Contains(first, p => p.Name == "Transparent Fade");
        Assert.NotSame(first[0].Stops, second[0].Stops);
        Assert.NotSame(first[0].AlphaStops, second[0].AlphaStops);
        // 모든 기본 그라데이션은 개인 에셋으로 저장할 수 있는 값이다
        Assert.All(first, GradientAssetFormat.Validate);
    }

    [Fact]
    public void FailedPersonalSave_LeavesStoredGradientsIntact()
    {
        using var dir = new TempDir();
        var store = SeededStore(dir);
        var saved = store.SavePersonalGradient(null, Example());
        byte[] before = File.ReadAllBytes(store.PersonalPath);

        Assert.Throws<InvalidDataException>(() => store.SavePersonalGradient(saved.Id, Example() with { Cycles = 17 }));
        Assert.Throws<InvalidDataException>(() => store.SavePersonalGradient(null, Example() with { Name = "  " }));
        Assert.Equal(before, File.ReadAllBytes(store.PersonalPath));
        var reopened = AssetStore.Open(store.Root);
        var kept = Assert.Single(reopened.Catalog.Gradients, e => e.Id == saved.Id).Gradient!;
        Assert.Equal((4, "Evening", true), (kept.Cycles, kept.Name, kept.Favorite));
    }

    /// <summary>기본값은 삭제할 수 없고 비활성화할 수 있다. 초기화는 값을 되돌리되 비활성화 상태와 직접 만든 값은 유지한다.</summary>
    [Fact]
    public void RestoreBuiltIns_ResetsEditedAndKeepsDisabledAndCustom()
    {
        using var dir = new TempDir();
        var store = SeededStore(dir);
        var original = GradientPresets.BuiltIns();
        Assert.Equal(original.Select(p => p.Name), store.Catalog.Gradients.Select(e => e.Name));

        var ocean = store.Catalog.Gradients.Single(e => e.Name == "Ocean");
        Assert.True(ocean.IsBuiltIn);
        Assert.Throws<InvalidOperationException>(() => store.RemovePersonal(ocean.Id));
        store.SetPersonalEnabled(ocean.Id, false);
        var sunset = store.Catalog.Gradients.Single(e => e.Name == "Sunset");
        store.SavePersonalGradient(sunset.Id, sunset.Gradient! with
        {
            Name = "sunset", Stops = [new(0, Color32.Black), new(1, Color32.Black)], Favorite = true,
        });
        var mine = store.SavePersonalGradient(null, Example("Mine"));

        store.RestoreBuiltInGradients();
        var restored = store.Catalog.Gradients.Single(e => e.Id == sunset.Id);
        Assert.Equal(original.Single(p => p.Name == "Sunset").Stops, restored.Gradient!.Stops);
        Assert.True(restored.Favorite);
        Assert.True(restored.IsBuiltIn);
        Assert.DoesNotContain(store.Catalog.Gradients, e => e.Id == ocean.Id);
        Assert.False(store.Catalog.Find(ocean.Id)!.Enabled);
        Assert.Equal(Example("Mine").Stops, store.Catalog.Gradients.Single(e => e.Id == mine.Id).Gradient!.Stops);
        Assert.Equal(original.Count, store.Catalog.Gradients.Count);

        // 되돌린 결과는 파일에 남는다
        var reopened = AssetStore.Open(store.Root);
        Assert.Equal(store.Catalog.Gradients.Select(e => (e.Id, e.Name, e.Favorite)), reopened.Catalog.Gradients.Select(e => (e.Id, e.Name, e.Favorite)));
    }

    [Fact]
    public void OfficialGradient_ShadowsOnlyBuiltIn_DoesNotDeleteItOrExposeItWhenDisabled()
    {
        using var dir = new TempDir();
        var store = SeededStore(dir);
        var builtIn = Assert.Single(store.Catalog.Personal, e => e.IsBuiltIn && e.Name == "Rainbow");
        var custom = store.SavePersonalGradient(null, Example("Rainbow"));
        var header = AssetSamples.Header(id: "gpixel.gradients.rainbow") with
        {
            Author = new AssetAuthor("gpixel", "Gpixel"),
        };
        var path = AssetSamples.Save(dir.Path, AssetSamples.Build(header, gradient: AssetSamples.Gradient("rainbow")));
        var installed = store.Install(path, source: "remote");
        Assert.True(installed.IsOfficial);
        var official = Assert.Single(installed.Entries, e => e.Kind == AssetKinds.Gradient);

        Assert.DoesNotContain(store.Catalog.DisplayPersonal, e => e.Id == builtIn.Id);
        Assert.Contains(store.Catalog.DisplayPersonal, e => e.Id == custom.Id);
        Assert.Equal([custom.Id, official.Id],
            store.Catalog.Available(AssetKinds.Gradient).Where(e => e.Name.Equals("Rainbow", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Id).ToArray());
        Assert.Equal(builtIn, store.Catalog.Find(builtIn.Id));
        Assert.Contains(store.Catalog.Personal, e => e.Id == builtIn.Id);

        store.SetEnabled(installed.Id, false);
        Assert.DoesNotContain(store.Catalog.Gradients, e => e.Id == builtIn.Id || e.Id == official.Id);
        Assert.Contains(store.Catalog.Gradients, e => e.Id == custom.Id);
        Assert.DoesNotContain(store.Catalog.DisplayPersonal, e => e.Id == builtIn.Id);
        Assert.Equal(builtIn.Id, AssetStore.Open(store.Root).Catalog.Find(builtIn.Id)!.Id);

        // 설치 항목이 사라진 뒤 새 카탈로그를 만들면 저장된 기본 항목이 다시 보인다.
        var withoutPackage = new AssetCatalog(store.Catalog.Personal, []);
        Assert.Contains(withoutPackage.DisplayPersonal, e => e.Id == builtIn.Id);
        Assert.Contains(withoutPackage.Gradients, e => e.Id == builtIn.Id);
        Assert.Contains(withoutPackage.Gradients, e => e.Id == custom.Id);

        var localPackage = installed with { State = installed.State with { Source = "file" } };
        var withLocalPackage = new AssetCatalog(store.Catalog.Personal, [localPackage]);
        Assert.Contains(withLocalPackage.DisplayPersonal, e => e.Id == builtIn.Id);
        Assert.Contains(withLocalPackage.Gradients, e => e.Id == builtIn.Id);
    }
}
