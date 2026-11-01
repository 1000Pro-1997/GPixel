using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>예전 형식(짧은 효과 ID) 파일 읽기와 모르는 효과의 원본 보존.</summary>
public class EffectCompatibilityTests
{
    private static string Fixture(string name) => TestUtil.Fixture(Path.Combine("effects", name));

    private static JsonNode ProjectJson(Stream stream)
    {
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var r = zip.GetEntry(ProjectIO.ProjectEntryName)!.Open();
        return JsonNode.Parse(r)!;
    }

    private static void RewriteProjectJson(Stream stream, Action<JsonNode> change)
    {
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
        var entry = zip.GetEntry(ProjectIO.ProjectEntryName)!;
        JsonNode root;
        using (var r = entry.Open()) root = JsonNode.Parse(r)!;
        change(root);
        entry.Delete();
        using var w = zip.CreateEntry(ProjectIO.ProjectEntryName).Open();
        using var writer = new Utf8JsonWriter(w);
        root.WriteTo(writer);
    }

    /// <summary>효과 목록을 값으로 비교한다(색 그라데이션 사전은 참조 비교라 저장 JSON으로).</summary>
    private static void AssertSameEffects(IEnumerable<LayerEffect> expected, IEnumerable<LayerEffect> actual)
        => Assert.Equal(expected.Select(e => EffectCodec.Write(e).ToJsonString()), actual.Select(e => EffectCodec.Write(e).ToJsonString()));

    [Fact]
    public void OldProjectFixture_OpensWithTheSameEffectsAndPixels_AndResavesWithTypeIds()
    {
        var warnings = new List<string>();
        var old = ProjectIO.Load(Fixture("effects-v3.gpixel"), warnings);
        Assert.Empty(warnings);
        var expected = EffectCharacterizationTests.SavedKinds();
        AssertSameEffects(expected, old.Layers[0].Effects);
        Assert.Equal(EffectCharacterizationTests.Hash(EffectCharacterizationTests.Project(expected)), EffectCharacterizationTests.Hash(old));

        using var stream = new MemoryStream();
        ProjectIO.Save(old, stream);
        var json = ProjectJson(stream);
        Assert.Equal(ProjectIO.CurrentVersion, json["version"]!.GetValue<int>());
        Assert.All(json["layers"]![0]!["effects"]!.AsArray(), e => Assert.NotNull(EffectRegistry.Find(e!["type"]!.GetValue<string>())));
        stream.Position = 0;
        AssertSameEffects(expected, ProjectIO.Load(stream).Layers[0].Effects);
    }

    [Fact]
    public void OldPresetFixture_LoadsTheSameEffects()
    {
        var preset = Assert.Single(AssetMigration.ReadLegacyEffects(File.ReadAllText(Fixture("effect-presets-v1.json"))));
        AssertSameEffects(EffectRecipe.Capture("", "all", EffectCharacterizationTests.SavedKinds()).Effects, preset.Effects);
    }

    [Fact]
    public void UnknownEffects_SurviveOpenAndSaveWithTheirJson_AndDrawNothing()
    {
        var p = new SpriteProject(8, 8);
        p.DuplicateFrame(0);
        p.DuplicateFrame(0);
        var cel = new PixelImage(8, 8);
        for (int y = 2; y < 6; y++)
            for (int x = 2; x < 6; x++)
                cel[x, y] = new Color32(200, 40, 40);
        for (int f = 0; f < 3; f++) p.SetCel(0, f, cel);
        var outline = new OutlineEffect { Thickness = 2 };
        p.SetLayerEffects(0, [outline]);

        // 새 형식의 모르는 종류(패키지 정보 포함), 예전 형식의 모르는 종류, 이 앱보다 새 설정 버전의 아는 종류
        var unknown = JsonNode.Parse("""
            { "type": "gpixel.effect.future", "version": 3, "enabled": true, "clipToPrevious": false, "frames": [1, 2],
              "settings": { "power": 7, "colors": ["#FF0000"] }, "colorGradients": { "Glow": { "weird": true } },
              "requires": { "package": "acme.fx", "version": "1.2.0" } }
            """)!.AsObject();
        var legacy = JsonNode.Parse("""{ "type": "sparkle", "enabled": false, "clipToPrevious": true, "amount": 3 }""")!.AsObject();
        var newer = JsonNode.Parse($$"""
            { "type": "{{OutlineDefinition.Id}}", "version": 99, "enabled": true, "clipToPrevious": false, "settings": { "thickness": 3, "glowRadius": 4 } }
            """)!.AsObject();

        using var stream = new MemoryStream();
        ProjectIO.Save(p, stream);
        RewriteProjectJson(stream, root =>
        {
            var effects = root["layers"]![0]!["effects"]!.AsArray();
            effects.Insert(0, unknown.DeepClone());
            effects.Add(legacy.DeepClone());
            effects.Add(newer.DeepClone());
        });
        stream.Position = 0;
        var warnings = new List<string>();
        var opened = ProjectIO.Load(stream, warnings);
        var effects = opened.Layers[0].Effects;
        Assert.Equal(["gpixel.effect.future", "sparkle", OutlineDefinition.Id],
            effects.OfType<MissingEffect>().Select(m => m.TypeId));
        Assert.Equal(outline, effects[1]);
        Assert.Equal(new FrameRange(1, 2), effects[0].Frames);
        Assert.Equal((false, true), (effects[2].Enabled, effects[2].ClipToPrevious));
        Assert.Equal(3, warnings.Count);

        // 그리지 않는다: 외곽선만 있을 때와 같은 그림
        for (int f = 0; f < 3; f++) Assert.Equal(p.Flatten(f).Pixels, opened.Flatten(f).Pixels);

        // 다시 저장하면 원본 JSON 그대로
        using var again = new MemoryStream();
        ProjectIO.Save(opened, again);
        var saved = ProjectJson(again)["layers"]![0]!["effects"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(unknown, saved[0]), saved[0]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(legacy, saved[2]), saved[2]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(newer, saved[3]), saved[3]!.ToJsonString());

        // 목록 편집(켜짐·범위·클리핑)은 그 키만 바꾼다
        opened.SetLayerEffects(0, [effects[0] with { Enabled = false, Frames = null }, .. effects.Skip(1)]);
        using var edited = new MemoryStream();
        ProjectIO.Save(opened, edited);
        var changed = ProjectJson(edited)["layers"]![0]!["effects"]![0]!.AsObject();
        var expected = unknown.DeepClone().AsObject();
        expected["enabled"] = false;
        expected.Remove("frames");
        Assert.True(JsonNode.DeepEquals(expected, changed), changed.ToJsonString());
    }
}
