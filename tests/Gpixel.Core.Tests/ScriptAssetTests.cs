using System.Drawing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.Core.Tests;

/// <summary>
/// 스크립트 효과와 에셋·기록의 연결(7단계 + 2~5단계): <c>effect-script</c> 패키지, 원문을 실은 레시피, 설치한 스크립트의 실행 허락,
/// 실행취소 기록의 원문, 원문을 모르는 효과의 보존, 여백 실패와 실패한 실행의 캐시.
/// 신뢰 결정·원문 표는 프로세스 전체 상태라 시험마다 원문 끝에 주석을 붙여 해시를 따로 쓴다.
/// </summary>
public class ScriptAssetTests
{
    private const int Frames = 6;
    private const string AssetId = AssetSamples.PackageId + ".x";

    private static string Unique(string source, [CallerMemberName] string test = "") => source + "\n// " + test + "\n";

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
                        cel[x, y] = new Color32((byte)(x * 9), (byte)(y * 11), (byte)(120 + f * 10));
            p.SetCel(0, f, cel);
        }
        p.SetLayerEffects(0, effects);
        return p;
    }

    private static bool SameFrames(SpriteProject a, SpriteProject b)
        => Enumerable.Range(0, a.Frames.Count).All(f => a.Flatten(f).ContentEquals(b.Flatten(f)));

    private static byte[] Package(Action<AssetPackageBuilder> add)
    {
        var builder = new AssetPackageBuilder(AssetSamples.Header());
        add(builder);
        builder.AddDocument("LICENSE.txt", "CC0");
        using var stream = new MemoryStream();
        builder.Write(stream);
        return stream.ToArray();
    }

    /// <summary>에셋 파일 하나를 manifest에 적고 해시까지 맞춘 패키지(내용 검사만 실패하게).</summary>
    private static byte[] WithAsset(string kind, string path, string content)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = Encoding.UTF8.GetBytes(content) };
        using var stream = new MemoryStream();
        AssetPackageWriter.Write(stream, AssetSamples.Header() with { Assets = [new AssetManifestEntry(AssetId, kind, path, null)] }, files);
        return stream.ToArray();
    }

    private static IReadOnlyList<AssetIssue> Rejects(byte[] package)
        => Assert.Throws<AssetPackageException>(() => AssetPackageValidator.Read(new MemoryStream(package))).Issues;

    private static void AssertIssue(IReadOnlyList<AssetIssue> issues, AssetIssueCode code, string? detailStart = null)
        => Assert.True(issues.Any(i => i.Code == code && (detailStart is null || (i.Detail ?? "").StartsWith(detailStart, StringComparison.Ordinal))),
            $"{code} {detailStart} 없음: " + string.Join(" | ", issues.Select(i => $"{i.Code} {i.Path} {i.Detail}")));

    /// <summary>스크립트 효과 하나와 원문 표를 담은 레시피 JSON.</summary>
    private static string ScriptRecipe(string hash, string settings, string? scripts, int schema = 3)
        => $$"""
           { "schemaVersion": {{schema}}, "id": "{{AssetId}}", "name": "X",
             "effects": [ { "type": "{{ScriptDefinition.Id}}", "settings": { {{settings}} }, "script": "{{hash}}" } ]
             {{(scripts is null ? "" : ", \"scripts\": " + scripts)}} }
           """;

    private static string Table(params (string Hash, string Source)[] entries)
        => new JsonObject(entries.Select(e => KeyValuePair.Create(e.Hash, (JsonNode?)JsonValue.Create(e.Source)))).ToJsonString();

    // ───────────── effect-script 패키지 ─────────────

    [Fact]
    public void Package_script_installs_as_an_untrusted_tile_that_renders_after_allow()
    {
        var program = ScriptEffectProgram.Parse(Unique(ScriptTemplates.Shine)).Program!;
        byte[] bytes = Package(builder => builder.Add(AssetKinds.EffectScript, ScriptAsset.From("", program)));
        // 패키지 안에는 UTF-8 원문 그대로(이름이 한국어라 ID 조각은 종류 이름)
        var file = Assert.Single(AssetSamples.Unzip(bytes), entry => entry.Name.StartsWith("assets/scripts/", StringComparison.Ordinal));
        Assert.Equal(("assets/scripts/effect-script.js", program.Source), (file.Name, Encoding.UTF8.GetString(file.Data)));

        using var dir = new TempDir();
        var store = AssetStore.Open(dir.File("Assets"));
        store.Install(AssetSamples.Save(dir.Path, bytes));

        var entry = Assert.Single(store.Catalog.EffectTemplates);
        var script = Assert.IsType<ScriptAsset>(entry.Data);
        Assert.Equal((AssetSamples.PackageId + ".effect-script", program.ContentHash, "빛 지나가기", 5, true),
            (entry.Id, script.Hash, script.Name, script.Settings.Count, script.Animated));
        // 설치는 실행을 허락하지 않는다
        Assert.Equal(ScriptTrust.Unknown, ScriptLibrary.TrustOf(script.Hash));

        var plain = Project([]);
        var p = Project([]);
        p.SetLayerEffects(0, entry.EffectTemplate!.Instantiate(p, null));
        Assert.Equal(program.Source, p.Scripts[script.Hash]);
        var effect = Assert.IsType<ScriptEffect>(Assert.Single(p.Layers[0].Effects));
        Assert.Equal(ScriptEffectState.NotTrusted, ScriptEffectRuntime.StatusOf(p.Layers[0], effect).State);
        Assert.True(SameFrames(plain, p));

        ScriptLibrary.SetTrust(script.Hash, true);
        // 효과 창에서 바로 넣은 기본 설정 효과와 같은 값·같은 그림
        var direct = new ScriptEffect { ScriptHash = script.Hash, Settings = ScriptSettingValues.Normalized(ScriptLibrary.Runnable(script.Hash)!, null) };
        Assert.Equal(direct, effect);
        Assert.False(SameFrames(plain, p));
        Assert.True(SameFrames(Project([direct]), p));
        Assert.Equal(ScriptEffectState.Ready, ScriptEffectRuntime.StatusOf(p.Layers[0], effect).State);
    }

    [Fact]
    public void Personal_script_asset_round_trips_and_same_source_is_not_duplicated()
    {
        var source = Unique(ScriptTemplates.Shine);
        using var dir = new TempDir();
        var store = AssetStore.Open(dir.File("Assets"));
        var saved = store.SavePersonalScript(source);
        Assert.Equal(saved.Id, store.SavePersonalScript(source.ReplaceLineEndings("\r\n")).Id);

        var reopened = AssetStore.Open(dir.File("Assets"));
        var entry = Assert.Single(reopened.Catalog.Personal);
        Assert.Equal((AssetKinds.EffectScript, saved.Id, ScriptEffectProgram.HashOf(source)), (entry.Kind, entry.Id, entry.Script!.Hash));
        var data = JsonNode.Parse(File.ReadAllText(reopened.PersonalPath))!["assets"]![0]!["data"]!;
        Assert.Equal(ScriptEffectProgram.NormalizeSource(source), data["source"]!.GetValue<string>());

        Assert.Throws<AssetPackageException>(() => store.SavePersonalScript("effect({ name: 'x', render( {} });"));
    }

    // ───────────── 원문을 실은 레시피 ─────────────

    [Fact]
    public void Recipe_with_a_script_round_trips_through_personal_store_and_package_and_renders_in_a_fresh_project()
    {
        var source = Unique(ScriptTemplates.Shine);
        var p = Project([]);
        var hash = p.AddScript(source);
        ScriptLibrary.SetTrust(hash, true);
        var program = ScriptLibrary.Runnable(hash)!;
        var script = new ScriptEffect
        {
            ScriptHash = hash, Easing = EffectEasing.EaseIn,
            Settings = ScriptSettingValues.Normalized(program, new Dictionary<string, object> { ["angle"] = 120, ["width"] = 5, ["color"] = new Color32(255, 128, 0) }),
        };
        p.SetLayerEffects(0, [new OutlineEffect { Color = new Color32(30, 10, 60) }, script]);
        var recipe = EffectRecipe.Capture("", "Shine outline", p.Layers[0].Effects, p);
        Assert.Equal([KeyValuePair.Create(hash, program.Source)], recipe.Scripts);

        using var dir = new TempDir();
        var personal = AssetStore.Open(dir.File("A"));
        personal.SavePersonalRecipe(recipe);
        var reopened = AssetStore.Open(dir.File("A"));
        var entry = Assert.Single(reopened.Catalog.Recipes);
        Assert.Equal(recipe.Scripts, entry.Recipe!.Scripts);
        var json = JsonNode.Parse(File.ReadAllText(reopened.PersonalPath))!["assets"]![0]!["data"]!;
        Assert.Equal(3, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal(program.Source, json["scripts"]![hash]!.GetValue<string>());

        // 개인 레시피를 패키지로 내보내 다른 저장소(다른 컴퓨터)에 설치
        using var buffer = new MemoryStream();
        reopened.ExportPersonal([entry.Id], AssetSamples.Header(), buffer);
        var other = AssetStore.Open(dir.File("B"));
        other.Install(AssetSamples.Save(dir.Path, buffer.ToArray()));
        var installed = Assert.Single(other.Catalog.Recipes).Recipe!;
        Assert.Equal(recipe.Scripts, installed.Scripts);

        var fresh = Project([]);
        fresh.SetLayerEffects(0, installed.Instantiate(fresh, null));
        Assert.Equal(program.Source, fresh.Scripts[hash]);
        Assert.True(SameFrames(p, fresh));
        // 프로젝트 파일에도 원문이 들어간다(설치 에셋에 기대지 않음)
        using var file = new MemoryStream();
        ProjectIO.Save(fresh, file);
        file.Position = 0;
        Assert.Equal(program.Source, ProjectIO.Load(file).Scripts[hash]);
    }

    [Fact]
    public void Recipe_without_scripts_keeps_its_old_schema_and_lenient_read_drops_a_mismatched_source()
    {
        Assert.Equal(1, EffectRecipeFormat.Write(AssetSamples.Recipe())["schemaVersion"]!.GetValue<int>());
        Assert.Null(EffectRecipeFormat.Write(AssetSamples.Recipe())["scripts"]);

        var source = Unique("effect({ name: 'x', render() {} });");
        var hash = ScriptEffectProgram.HashOf(source);
        var node = JsonNode.Parse(ScriptRecipe(hash, "", Table((hash, source + "// 바뀜"))));
        var recipe = EffectRecipeFormat.Read(node, new AssetReadContext("personal", strict: false))!;
        Assert.Empty(recipe.Scripts);
        Assert.Equal(hash, Assert.IsType<ScriptEffect>(Assert.Single(recipe.Effects)).ScriptHash);
    }

    [Fact]
    public void Publishing_accepts_script_assets_and_the_catalog_counts_them()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(dir.File("Assets"));
        store.SavePersonalRecipe(AssetSamples.Recipe());
        store.SavePersonalScript(Unique(ScriptTemplates.Shine));
        var result = AssetPublishCheck.Check(new PublishRequest
        {
            Author = new AssetAuthor("pixel-cat", "Pixel Cat", "https://example.com/cat"),
            Id = "pixel-cat.shine", Version = "1.0.0", Name = "Shine Set", Summary = "빛 지나가기 스크립트",
            Tags = AssetPublishCheck.ParseTags("shine"), License = "CC0-1.0",
            LicenseText = AssetPublishCheck.LicenseNotice("CC0-1.0", "Shine Set", "Pixel Cat", 2026),
            Assets = store.Catalog.Personal.Select(entry => new PublishAsset(entry.Kind, entry.Data, AssetPreview.Png(entry.Data))).ToArray(),
            MinAppVersion = "1.14.55",
        }, [], [], new DateOnly(2026, 10, 28));
        Assert.True(result.Ok, string.Join("\n", result.Issues));

        string catalog = Path.Combine(dir.Path, "repo", "catalog"), output = Path.Combine(dir.Path, "repo", "docs", "catalog");
        Directory.CreateDirectory(catalog);
        using (var zip = new MemoryStream())
        {
            AssetPublishCheck.WriteSubmission(result, zip);
            zip.Position = 0;
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, catalog);
        }
        var build = RemoteCatalogBuilder.Build(catalog, output);
        Assert.True(build.Ok, string.Join("\n", build.Issues));
        var entry = Assert.Single(build.Entries);
        Assert.Equal([KeyValuePair.Create(AssetKinds.EffectRecipe, 1), KeyValuePair.Create(AssetKinds.EffectScript, 1)],
            entry.Kinds.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Null(entry.IncompatibleReason(null));
    }

    [Fact]
    public void Bad_script_sources_are_rejected_before_install()
    {
        const string recipePath = "assets/effects/x.json", scriptPath = "assets/scripts/x.js";
        var good = Unique(ScriptTemplates.Shine);
        var hash = ScriptEffectProgram.HashOf(good);
        const string syntax = "effect({ name: 'x', render( {} });";
        var syntaxHash = ScriptEffectProgram.HashOf(syntax);

        // effect-script 파일: 문법 오류, 맨 위에서 끝나지 않는 실행(설치 검사 한도), UTF-8 아님, 크기 한도
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectScript, scriptPath, syntax)), AssetIssueCode.InvalidScript, "source:1:");
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectScript, scriptPath, "while (true) {}\neffect({ name: 'x', render() {} });")),
            AssetIssueCode.InvalidScript, "source:");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [scriptPath] = [0x65, 0xFF, 0xFE] };
        using (var stream = new MemoryStream())
        {
            AssetPackageWriter.Write(stream, AssetSamples.Header() with { Assets = [new AssetManifestEntry(AssetId, AssetKinds.EffectScript, scriptPath, null)] }, files);
            AssertIssue(Rejects(stream.ToArray()), AssetIssueCode.InvalidScript, "UTF-8");
        }
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectScript, scriptPath, good + new string(' ', 70_000))), AssetIssueCode.FileTooLarge);

        // 레시피: 해시와 다른 원문, 원문 없음, 해석되지 않는 원문, 원문 schema 밖 설정, 쓰지 않는 원문, 형식 2의 scripts
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, recipePath, ScriptRecipe(hash, "", Table((hash, good + "// 몰래"))))),
            AssetIssueCode.ScriptHashMismatch, "scripts." + hash[..12]);
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, recipePath, ScriptRecipe(hash, "", null, schema: 1))),
            AssetIssueCode.MissingScript, "effects[0]: " + hash[..12]);
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, recipePath, ScriptRecipe(syntaxHash, "", Table((syntaxHash, syntax))))),
            AssetIssueCode.InvalidScript, "scripts." + syntaxHash[..12] + ":1:");
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, recipePath, ScriptRecipe(hash, "\"angle\": 999", Table((hash, good))))),
            AssetIssueCode.InvalidEffectSetting, "effects[0].settings.angle");
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, recipePath, ScriptRecipe(hash, "\"gone\": 1", Table((hash, good))))),
            AssetIssueCode.InvalidEffectSetting, "effects[0].settings.gone");
        var unused = Unique("effect({ name: 'y', render() {} });");
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, recipePath,
                ScriptRecipe(hash, "", Table((hash, good), (ScriptEffectProgram.HashOf(unused), unused))))),
            AssetIssueCode.InvalidField, "scripts." + ScriptEffectProgram.HashOf(unused)[..12]);
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, recipePath, ScriptRecipe(hash, "", Table((hash, good)), schema: 2))),
            AssetIssueCode.InvalidField, "scripts");

        // 맞는 레시피는 통과하고, 설정은 원문 schema 안이면 받는다
        var package = AssetPackageValidator.Read(new MemoryStream(WithAsset(AssetKinds.EffectRecipe, recipePath,
            ScriptRecipe(hash, "\"angle\": 30, \"color\": \"#FF0000\", \"repeat\": \"Loop\"", Table((hash, good))))));
        var read = Assert.IsType<EffectRecipe>(Assert.Single(package.Items).Data);
        Assert.Equal(ScriptEffectProgram.NormalizeSource(good), read.Scripts[hash]);
    }

    [Fact]
    public void Long_script_source_in_a_recipe_is_measured_by_the_script_limit()
    {
        // 문자열 한도(4096자)보다 긴 원문도 원문 한도(64KiB) 안이면 받는다
        var source = Unique(ScriptTemplates.Shine) + "// " + new string('a', 6000) + "\n";
        var hash = ScriptEffectProgram.HashOf(source);
        var package = AssetPackageValidator.Read(new MemoryStream(WithAsset(AssetKinds.EffectRecipe, "assets/effects/x.json",
            ScriptRecipe(hash, "", Table((hash, source))))));
        Assert.Single(package.Items);
        // 다른 자리의 긴 글은 여전히 문자열 한도
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, "assets/effects/x.json",
            ScriptRecipe(hash, "", Table((hash, source))).Replace("\"name\": \"X\"", "\"name\": \"X\", \"description\": \"" + new string('b', 5000) + "\""))),
            AssetIssueCode.StringTooLong, "4096");
    }

    // ───────────── 실행취소 기록 ─────────────

    [Fact]
    public void Undo_history_keeps_older_script_sources_for_a_new_session()
    {
        using var tmp = new TempDir();
        var file = tmp.File("a.gpixel");
        var dir = tmp.File("history");
        var v1 = Unique(ScriptTemplates.Shine);
        var v2 = Unique(ScriptTemplates.Shine.Replace("const amount", "const unused = 1;\n    const amount"));
        var p = Project([]);
        var h1 = p.AddScript(v1);
        var h2 = p.AddScript(v2);
        ScriptLibrary.SetTrust(h1, true);
        ScriptLibrary.SetTrust(h2, true);
        var settings = ScriptSettingValues.Normalized(ScriptLibrary.Runnable(h1)!, new Dictionary<string, object> { ["angle"] = 200, ["width"] = 6 });
        p.SetLayerEffects(0, [new ScriptEffect { ScriptHash = h1, Settings = settings }]);
        p.SetLayerEffects(0, [new ScriptEffect { ScriptHash = h2, Settings = settings }]);
        ProjectIO.Save(p, file);
        UndoHistoryIO.Write(UndoHistoryIO.Capture(p, 1000)!, dir);

        var manifest = JsonNode.Parse(UndoHistoryIO.Decompress(File.ReadAllBytes(Path.Combine(dir, UndoHistoryIO.ManifestName))))!;
        Assert.Equal(UndoHistoryIO.FormatVersion, manifest["version"]!.GetValue<int>());
        Assert.Equal([h1, h2], manifest["scripts"]!.AsObject().Select(pair => pair.Key));

        // 새 실행: 프로젝트 파일에는 지금 원문만 있고, 기록을 읽으면 예전 원문이 표에 돌아온다
        var q = ProjectIO.Load(file);
        Assert.False(q.Scripts.ContainsKey(h1));
        int steps = UndoHistoryIO.Read(q, dir);
        Assert.Equal(ScriptEffectProgram.NormalizeSource(v1), q.Scripts[h1]);
        q.Undo();
        p.Undo();
        Assert.Equal(h1, Assert.IsType<ScriptEffect>(Assert.Single(q.Layers[0].Effects)).ScriptHash);
        Assert.True(SameFrames(p, q));
        Assert.Contains(h1, q.ScriptsInUse().Keys);

        // 형식 2 기록(원문 없음)도 읽는다
        manifest["version"] = 2;
        manifest.AsObject().Remove("scripts");
        File.WriteAllBytes(Path.Combine(dir, UndoHistoryIO.ManifestName), UndoHistoryIO.Compress(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
        var r = ProjectIO.Load(file);
        Assert.Equal(steps, UndoHistoryIO.Read(r, dir));
        Assert.False(r.Scripts.ContainsKey(h1));
    }

    // ───────────── 원문을 모르는 효과 ─────────────

    [Fact]
    public void Unreadable_script_effect_keeps_its_source_and_recipe_group_on_save()
    {
        var source = Unique("effect({ name: 'later', render() {} });");
        var p = Project([], frames: 1);
        var hash = p.AddScript(source);
        var link = new RecipeLink(new EffectRecipeInstance { Id = "group1", Name = "묶음", Parameters = [] }, 1);
        var future = JsonNode.Parse($$"""
            { "type": "{{ScriptDefinition.Id}}", "version": 99, "script": "{{hash}}", "settings": { "x": 1 },
              "recipe": {{RecipeParameterFormat.WriteLink(link).ToJsonString()}} }
            """)!.AsObject();
        var missing = Assert.IsType<MissingEffect>(EffectCodec.Read(future));
        Assert.Equal(("group1", 1), (missing.Recipe!.Instance.Id, missing.Recipe.Slot));
        p.SetLayerEffects(0, [new OutlineEffect { Recipe = link with { Slot = 0 } }, missing]);

        using var stream = new MemoryStream();
        ProjectIO.Save(p, stream);
        stream.Position = 0;
        var loaded = ProjectIO.Load(stream);
        Assert.Equal(ScriptEffectProgram.NormalizeSource(source), loaded.Scripts[hash]);
        var read = Assert.IsType<MissingEffect>(loaded.Layers[0].Effects[1]);
        Assert.Equal("group1", read.Recipe?.Instance.Id);

        // 묶음을 풀면(자리 없음) 쓸 때 자리도 빠진다
        Assert.Null(EffectCodec.Write(read with { Recipe = null })[RecipeParameterFormat.LinkKey]);
    }

    // ───────────── 실패 ─────────────

    [Fact]
    public void Failing_padding_counts_as_a_failure_and_is_not_called_again()
    {
        var source = Unique("""
            effect({
              name: "여백 실패",
              padding: (s, w, h) => { throw new Error("padding broke"); },
              render(img, ctx, s) {},
            });
            """);
        var p = Project([], frames: 1);
        var hash = p.AddScript(source);
        ScriptLibrary.SetTrust(hash, true);
        var effect = new ScriptEffect { ScriptHash = hash };
        p.SetLayerEffects(0, [effect]);
        var layer = p.Layers[0];

        p.Flatten(0);
        var status = ScriptEffectRuntime.StatusOf(layer, effect);
        Assert.Equal(ScriptEffectState.Failed, status.State);
        Assert.Contains("padding broke", Assert.Single(status.Errors).Message, StringComparison.Ordinal);
        Assert.NotNull(ScriptLibrary.PaddingErrorOf(hash, effect.Settings));
        for (int i = 1; i < ScriptEffectRuntime.MaxConsecutiveFailures; i++) p.Flatten(0);
        Assert.Equal(ScriptEffectState.Suspended, ScriptEffectRuntime.StatusOf(layer, effect).State);

        // 다시 실행하면 기억한 여백 실패도 지운다
        ScriptEffectRuntime.Resume(layer, effect);
        Assert.Null(ScriptLibrary.PaddingErrorOf(hash, effect.Settings));
    }

    [Fact]
    public void Run_where_a_script_failed_is_not_stored_as_checkpoint_or_ghost()
    {
        var failing = new ScriptEffect { ScriptHash = Trust(Unique("effect({ name: 'f', render() { throw new Error('boom'); } });")) };
        var fine = new ScriptEffect { ScriptHash = Trust(Unique("effect({ name: 'g', render() {} });")) };
        var outline = new OutlineEffect { Color = new Color32(10, 20, 30) };
        var shadow = new ShadowEffect();
        var source = new PixelImage(12, 12);
        source[5, 5] = Color32.White;

        bool Stored(LayerEffect middle, int index)
        {
            var cache = new EffectRenderCache(1 << 24);
            var scope = new object();
            IReadOnlyList<LayerEffect> effects = [outline, middle, shadow];
            var image = source.Clone();
            var rect = new Rectangle(0, 0, 12, 12);
            LayerEffects.Apply(image, effects, rect, new EffectRunHooks { Cache = cache, CheckpointScope = scope });
            return cache.TryCheckpoint(new CacheKey("prefix", scope, 12, 12, rect, new EffectListKey(effects, index + 1)), out _);
        }
        Assert.True(Stored(fine, 0));
        Assert.True(Stored(fine, 1));
        Assert.True(Stored(failing, 0));
        Assert.False(Stored(failing, 1));

        var ghosts = new EffectRenderCache(1 << 24);
        int renders = 0;
        var key = new CacheKey("ghost", source, 0);
        (PixelImage, int, int) Ghost(EffectRunHooks hooks)
        {
            renders++;
            return LayerEffects.Render(source, [failing], default, hooks);
        }
        ghosts.Ghost(key, Ghost);
        ghosts.Ghost(key, Ghost);
        Assert.Equal(2, renders);
    }

    private static string Trust(string source)
    {
        var hash = ScriptLibrary.Add(source);
        ScriptLibrary.SetTrust(hash, true);
        return hash;
    }
}
