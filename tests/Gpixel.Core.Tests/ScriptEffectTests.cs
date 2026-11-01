using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.Core.Tests;

/// <summary>
/// 스크립트 효과(7단계)를 프로젝트에 넣었을 때: 저장·열기, 원문이 없거나 허락하지 않은 스크립트, 값 비교(렌더 캐시 열쇠), 실패 격리,
/// 기본 효과와 같은 합성 결과. 신뢰 결정은 프로세스 전체 상태라 시험마다 원문 끝에 주석을 붙여 해시를 따로 쓴다.
/// </summary>
public class ScriptEffectTests
{
    private const int Frames = 9;

    private const string Loop = """
        effect({
          name: "멈추지 않음",
          render(img, ctx, s) {
            while (true) {}
          },
        });
        """;

    /// <summary>시험마다 다른 해시가 되도록 원문 끝에 시험 이름 주석을 붙인다.</summary>
    private static string Unique(string source, [CallerMemberName] string test = "") => source + "\n// " + test + "\n";

    private static string Trusted(string source)
    {
        var hash = ScriptLibrary.Add(source);
        ScriptLibrary.SetTrust(hash, true);
        return hash;
    }

    /// <summary>프레임마다 조금 움직이는 22×18 모양(반투명 픽셀 포함) 프로젝트.</summary>
    private static SpriteProject Project(IReadOnlyList<LayerEffect> effects, int frames = Frames)
    {
        var p = new SpriteProject(30, 26);
        for (int i = 1; i < frames; i++) p.DuplicateFrame(0);
        for (int f = 0; f < frames; f++)
        {
            var cel = new PixelImage(30, 26);
            for (int y = 4; y < 22; y++)
                for (int x = 4 + f % 3; x < 26; x++)
                    if ((x - 15) * (x - 15) + (y - 13) * (y - 13) < 90)
                        cel[x, y] = new Color32((byte)(x * 9), (byte)(y * 11), (byte)(120 + f * 10), (byte)(x % 5 == 0 ? 160 : 255));
            p.SetCel(0, f, cel);
        }
        p.SetLayerEffects(0, effects);
        return p;
    }

    private static ScriptSettingValues ShineSettings(int angle, int width, Color32 color, int strength, int softness) => ScriptSettingValues.From(
        new Dictionary<string, object> { ["angle"] = angle, ["width"] = width, ["color"] = color, ["strength"] = strength, ["softness"] = softness });

    private static JsonObject ProjectJson(MemoryStream stream)
    {
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(zip.GetEntry(ProjectIO.ProjectEntryName)!.Open());
        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    private static (SpriteProject Project, List<string> Warnings) Reopen(SpriteProject p, out JsonObject json)
    {
        var stream = new MemoryStream();
        ProjectIO.Save(p, stream);
        json = ProjectJson(stream);
        stream.Position = 0;
        var warnings = new List<string>();
        return (ProjectIO.Load(stream, warnings), warnings);
    }

    [Fact]
    public void Save_and_open_keeps_the_source_and_settings_and_drops_unused_sources()
    {
        var source = Unique(ScriptTemplates.Shine);
        var p = Project([]);
        var hash = p.AddScript(source);
        ScriptLibrary.SetTrust(hash, true);
        var program = ScriptLibrary.Runnable(hash)!;
        var effect = new ScriptEffect
        {
            ScriptHash = hash,
            Settings = ScriptSettingValues.Normalized(program, new Dictionary<string, object> { ["angle"] = 120, ["color"] = new Color32(255, 128, 0), ["softness"] = 2 }),
            Frames = new FrameRange(1, 6), Easing = EffectEasing.EaseIn, Repeat = EffectRepeat.Loop, RepeatCount = 3, ClipToPrevious = false,
        };
        p.SetLayerEffects(0, [new OutlineEffect(), effect]);
        var oldHash = p.AddScript(Unique(ScriptTemplates.Shine.Replace("빛 지나가기", "예전 원문")));

        var (loaded, warnings) = Reopen(p, out var json);

        Assert.Empty(warnings);
        // 표에는 쓰는 원문 하나만, 효과는 해시와 설정(스크립트 설정 뒤에 움직임 설정)만
        var scripts = json["scripts"]!.AsObject();
        Assert.Equal([hash], scripts.Select(entry => entry.Key));
        Assert.Equal(ScriptEffectProgram.NormalizeSource(source), scripts[hash]!.GetValue<string>());
        Assert.False(scripts.ContainsKey(oldHash));
        var saved = json["layers"]![0]!["effects"]![1]!.AsObject();
        Assert.Equal(ScriptDefinition.Id, saved["type"]!.GetValue<string>());
        Assert.Equal(hash, saved["script"]!.GetValue<string>());
        Assert.Equal(["angle", "color", "softness", "strength", "width", "easing", "repeat", "repeatCount"],
            saved["settings"]!.AsObject().Select(entry => entry.Key));
        Assert.Equal("#FF8000", saved["settings"]!["color"]!.GetValue<string>());

        Assert.Equal(ScriptEffectProgram.NormalizeSource(source), loaded.Scripts[hash]);
        Assert.Equal(effect, Assert.IsType<ScriptEffect>(loaded.Layers[0].Effects[1]));
        for (int f = 0; f < Frames; f++) Assert.True(p.Flatten(f).ContentEquals(loaded.Flatten(f)), $"frame {f}");
    }

    [Fact]
    public void Reading_with_a_known_source_fits_the_settings_to_its_schema()
    {
        var hash = Trusted(Unique(ScriptTemplates.Shine));
        var json = JsonNode.Parse($$"""
            { "type": "{{ScriptDefinition.Id}}", "script": "{{hash}}",
              "settings": { "angle": 999, "width": 2.6, "color": "red?", "gone": 1, "repeat": "PingPong" } }
            """)!.AsObject();

        var effect = Assert.IsType<ScriptEffect>(EffectCodec.Read(json));

        Assert.Equal(EffectRepeat.PingPong, effect.Repeat);
        Assert.Equal(359d, effect.Settings["angle"]);
        Assert.Equal(3d, effect.Settings["width"]);
        Assert.Equal("#FFFFFF", effect.Settings["color"]);
        Assert.Equal(80d, effect.Settings["strength"]);
        Assert.False(effect.Settings.ContainsKey("gone"));
    }

    [Fact]
    public void Effect_with_a_missing_source_is_kept_as_written_and_not_drawn()
    {
        var hash = ScriptEffectProgram.HashOf(Unique("effect({ name: 'not in this file', render() {} });"));
        var settings = JsonNode.Parse("""{ "angle": 10, "points": [1, { "a": 2 }], "flag": true, "note": null, "mode": "x" }""")!.AsObject();
        var effect = new ScriptEffect { ScriptHash = hash, Settings = ScriptSettingValues.FromJson(settings), Frames = new FrameRange(2, 4) };
        var plain = Project([]);
        var p = Project([effect]);

        var (loaded, warnings) = Reopen(p, out var json);

        Assert.Null(json["scripts"]);
        Assert.Contains(warnings, w => w.Contains(hash[..12], StringComparison.Ordinal));
        var read = Assert.IsType<ScriptEffect>(Assert.Single(loaded.Layers[0].Effects));
        Assert.Equal(effect, read);
        Assert.Equal(ScriptEffectState.MissingSource, ScriptEffectRuntime.StatusOf(loaded.Layers[0], read).State);
        for (int f = 0; f < Frames; f++) Assert.True(plain.Flatten(f).ContentEquals(loaded.Flatten(f)), $"frame {f}");
        // 다시 저장해도 설정 JSON은 처음 그대로
        Reopen(loaded, out var again);
        var written = again["layers"]![0]!["effects"]![0]!["settings"]!.AsObject();
        foreach (var (key, value) in settings) Assert.True(JsonNode.DeepEquals(value, written[key]), key);

        // 해시 모양이 아니면 모르는 효과로 원본 보존
        var broken = JsonNode.Parse($$"""{ "type": "{{ScriptDefinition.Id}}", "script": "nope", "settings": { "a": 1 } }""")!.AsObject();
        var missing = Assert.IsType<MissingEffect>(EffectCodec.Read(broken));
        Assert.True(JsonNode.DeepEquals(broken, EffectCodec.Write(missing)));
    }

    [Fact]
    public void Untrusted_script_is_not_run_until_allowed()
    {
        var hash = ScriptLibrary.Add(Unique(ScriptTemplates.Shine));
        var effect = new ScriptEffect { ScriptHash = hash, Settings = ShineSettings(30, 6, Color32.White, 100, 0) };
        var plain = Project([]);
        var p = Project([effect]);

        bool Same() => Enumerable.Range(0, Frames).All(f => plain.Flatten(f).ContentEquals(p.Flatten(f)));

        Assert.True(Same());
        Assert.Equal(ScriptEffectState.NotTrusted, ScriptEffectRuntime.StatusOf(p.Layers[0], effect).State);
        // 선언을 읽으려고 맨 위를 실행하는 해석조차 하지 않았다
        Assert.Null(ScriptLibrary.TryGetParsed(hash));

        ScriptLibrary.SetTrust(hash, true);
        Assert.False(Same());
        ScriptLibrary.SetTrust(hash, false);
        Assert.True(Same());
        Assert.Equal(ScriptEffectState.NotTrusted, ScriptEffectRuntime.StatusOf(p.Layers[0], effect).State);
    }

    [Fact]
    public void Equal_settings_give_equal_effects_and_render_cache_keys()
    {
        var hash = ScriptEffectProgram.HashOf(Unique("effect({ name: 'x', render() {} });"));
        var typed = new ScriptEffect
        {
            ScriptHash = hash,
            Settings = ScriptSettingValues.From(new Dictionary<string, object>
            {
                ["angle"] = 30, ["color"] = new Color32(255, 128, 0), ["on"] = true, ["mode"] = "x", ["scale"] = 0.5,
            }),
        };
        var read = new ScriptEffect
        {
            ScriptHash = hash,
            Settings = ScriptSettingValues.FromJson(JsonNode.Parse("""{ "scale": 0.5, "mode": "x", "on": true, "color": "#FF8000", "angle": 30.0 }""")!.AsObject()),
        };

        Assert.Equal(typed, read);
        Assert.Equal(typed.GetHashCode(), read.GetHashCode());
        Assert.Equal(new EffectListKey([typed]), new EffectListKey([read]));
        // 그릴 때 채운 사본(다른 프레임 원본 함수 포함)도 같은 열쇠
        var drawn = typed.AtFrame(3, Frames, null, _ => null);
        Assert.Equal(new EffectListKey([typed.AtFrame(3, Frames, null)]), new EffectListKey([drawn]));
        Assert.Equal(typed, EffectCodec.Read(EffectCodec.Write(typed)));

        var changed = typed with { Settings = typed.Settings.With("angle", 31) };
        Assert.NotEqual(typed, changed);
        Assert.NotEqual(new EffectListKey([typed]), new EffectListKey([changed]));
        Assert.NotEqual(typed, typed with { ScriptHash = ScriptEffectProgram.HashOf("other") });
    }

    [Fact]
    public void Failing_script_is_skipped_alone_and_stops_after_repeated_failures()
    {
        var hash = Trusted(Unique(Loop));
        var loop = new ScriptEffect { ScriptHash = hash };
        var outline = new OutlineEffect { Color = new Color32(10, 20, 30), Thickness = 2 };
        var p = Project([loop, outline], frames: 1);
        var expected = Project([outline], frames: 1).Flatten(0);
        var layer = p.Layers[0];

        Assert.True(expected.ContentEquals(p.Flatten(0)));
        var status = ScriptEffectRuntime.StatusOf(layer, loop);
        Assert.Equal(ScriptEffectState.Failed, status.State);
        var error = Assert.Single(status.Errors);
        Assert.Contains(error.Kind, new[] { ScriptErrorKind.Statements, ScriptErrorKind.Timeout });
        Assert.Equal(4, error.Line);

        for (int i = 1; i < ScriptEffectRuntime.MaxConsecutiveFailures; i++) Assert.True(expected.ContentEquals(p.Flatten(0)));
        Assert.Equal(ScriptEffectState.Suspended, ScriptEffectRuntime.StatusOf(layer, loop).State);
        // 멈춘 동안에도 다른 효과는 그대로, 끄고 켠 효과 값(켜짐만 다름)도 같은 기록
        Assert.True(expected.ContentEquals(p.Flatten(0)));
        Assert.Equal(ScriptEffectState.Suspended, ScriptEffectRuntime.StatusOf(layer, loop with { Enabled = false }).State);

        ScriptEffectRuntime.Resume(layer, loop);
        Assert.Equal(ScriptEffectState.Ready, ScriptEffectRuntime.StatusOf(layer, loop).State);
        // 다른 레이어의 같은 효과는 따로 센다
        Assert.Equal(ScriptEffectState.Ready, ScriptEffectRuntime.StatusOf(Project([loop], frames: 1).Layers[0], loop).State);
    }

    [Fact]
    public void Shine_script_effect_matches_the_built_in_shine_through_flatten()
    {
        var hash = Trusted(Unique(ScriptTemplates.Shine));
        var color = new Color32(255, 220, 64);
        var range = new FrameRange(1, 7);
        var outline = new OutlineEffect { Color = new Color32(30, 10, 60) };
        var builtIn = new ShineEffect
        {
            Angle = 135, Width = 4, Color = color, Strength = 85, Softness = 1, Frames = range,
            Easing = EffectEasing.EaseInOut, Repeat = EffectRepeat.PingPong, RepeatCount = 2,
        };
        var script = new ScriptEffect
        {
            ScriptHash = hash, Settings = ShineSettings(135, 4, color, 85, 1), Frames = range,
            Easing = EffectEasing.EaseInOut, Repeat = EffectRepeat.PingPong, RepeatCount = 2,
        };
        var expected = Project([outline, builtIn]);
        var actual = Project([outline, script]);
        var without = Project([outline]);

        int shone = 0;
        for (int f = 0; f < Frames; f++)
        {
            var image = actual.Flatten(f);
            Assert.True(expected.Flatten(f).ContentEquals(image), $"frame {f}");
            if (!without.Flatten(f).ContentEquals(image)) shone++;
        }
        Assert.True(shone >= 3);
        Assert.Equal(ScriptEffectState.Ready, ScriptEffectRuntime.StatusOf(actual.Layers[0], script).State);
    }
}
