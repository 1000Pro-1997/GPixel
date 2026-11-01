using System.Globalization;
using System.IO.Compression;
using System.Text;
using Gpixel.Core.Assets;
using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>패키지·저장소 테스트가 같이 쓰는 견본 패키지와 ZIP 도구.</summary>
internal static class AssetSamples
{
    public const string PackageId = "author.neon-pack";

    public static AssetManifest Header(string version = "1.0.0", string id = PackageId) => new()
    {
        Id = id, Version = version, Name = "Neon Pack", Description = "네온 효과 묶음",
        Author = new AssetAuthor("author", "Author", "https://example.com/author"),
        License = "CC0-1.0", MinAppVersion = "0.1.0",
    };

    public static EffectColorGradient OverlayGradient { get; } = new()
    {
        Stops = [new(0, new Color32(255, 0, 128)), new(0.5, new Color32(255, 255, 255)), new(1, new Color32(0, 200, 255))],
        AlphaStops = [new(0, 255), new(1, 120)],
        ColorSpace = GradientColorSpace.Rgb, Angle = 45, Shape = EffectGradientShape.Radial,
    };

    /// <summary>순서·클리핑·색 슬롯 그라데이션이 있는 레시피.</summary>
    public static EffectRecipe Recipe(string name = "Neon Hit", int thickness = 2) => EffectRecipe.Capture("", name,
    [
        new OutlineEffect { Color = new Color32(0, 255, 200), Thickness = thickness, Diagonal = true },
        new ShadowEffect { OffsetX = 2, OffsetY = 1, ClipToPrevious = true },
        new ColorOverlayEffect
        {
            Amount = 40, ClipToPrevious = true,
            ColorGradients = new Dictionary<string, EffectColorGradient> { ["Color"] = OverlayGradient },
        },
    ]) with { Description = "맞을 때 번쩍", FrameRange = RecipeFrameRange.AllFrames, PreviewFrames = 8 };

    /// <summary>모든 값을 기본값과 다르게 둔 그라데이션.</summary>
    public static GradientPreset Gradient(string name = "Neon Blue") => new()
    {
        Name = name,
        Stops = [new(0, new Color32(10, 20, 200, 200)), new(0.35, new Color32(0, 180, 255)), new(1, new Color32(240, 250, 255))],
        AlphaStops = [new(0, 40), new(0.5, 255), new(1, 90)],
        Interpolation = GradientInterpolation.Steps, Dither = GradientDither.Bayer4, ColorSpace = GradientColorSpace.OkLab,
        Angle = 120, Shape = EffectGradientShape.Square, Animated = true, Reverse = true, Cycles = 3,
        Easing = EffectEasing.EaseOut, PingPong = true,
    };

    /// <summary>레시피 하나와 그라데이션 하나(미리보기 포함)를 담은 패키지 바이트.</summary>
    public static byte[] Build(AssetManifest? header = null, EffectRecipe? recipe = null, GradientPreset? gradient = null, bool previews = true,
        Action<AssetPackageBuilder>? more = null)
    {
        var builder = new AssetPackageBuilder(header ?? Header());
        var r = recipe ?? Recipe();
        var g = gradient ?? Gradient();
        builder.Add(r, previews ? AssetPreview.Png(r) : null);
        builder.Add(g, previews ? AssetPreview.Png(new GradientAsset("", g)) : null);
        builder.AddDocument("LICENSE.txt", "CC0");
        more?.Invoke(builder);
        using var stream = new MemoryStream();
        builder.Write(stream);
        return stream.ToArray();
    }

    public static string Save(string dir, byte[] package, string name = "pack.gpixel-asset")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, package);
        return path;
    }

    /// <summary>항목을 주어진 순서·이름 그대로 담은 ZIP(검사기가 막아야 할 모양도 만든다).</summary>
    public static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, Action<string, ZipArchiveEntry>? tweak = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                tweak?.Invoke(name, entry);
                using var s = entry.Open();
                s.Write(data);
            }
        return stream.ToArray();
    }

    public static List<(string Name, byte[] Data)> Unzip(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return zip.Entries.Select(entry =>
        {
            using var s = entry.Open();
            using var buffer = new MemoryStream();
            s.CopyTo(buffer);
            return (entry.FullName, buffer.ToArray());
        }).ToList();
    }

    /// <summary>효과 목록을 저장 JSON으로 비교한다(색 그라데이션 사전은 참조 비교라).</summary>
    public static string[] EffectJson(IEnumerable<LayerEffect> effects) => effects.Select(e => EffectCodec.Write(e).ToJsonString()).ToArray();

    public static void AssertSameGradient(GradientPreset expected, GradientPreset actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Stops, actual.Stops);
        Assert.Equal(expected.AlphaStops, actual.AlphaStops);
        Assert.Equal((expected.Interpolation, expected.Dither, expected.ColorSpace, expected.Angle, expected.Shape,
                expected.Animated, expected.Reverse, expected.Cycles, expected.Easing, expected.PingPong),
            (actual.Interpolation, actual.Dither, actual.ColorSpace, actual.Angle, actual.Shape,
                actual.Animated, actual.Reverse, actual.Cycles, actual.Easing, actual.PingPong));
    }

    public static void AssertSameRecipe(EffectRecipe expected, EffectRecipe actual)
    {
        Assert.Equal((expected.Name, expected.Description, expected.FrameRange, expected.PreviewFrames),
            (actual.Name, actual.Description, actual.FrameRange, actual.PreviewFrames));
        Assert.Equal(EffectJson(expected.Effects), EffectJson(actual.Effects));
    }
}

/// <summary>.gpixel-asset 검사(계획 15.3): 정상 왕복과 악의적·손상된 패키지마다 구조화된 문제 종류.</summary>
public class AssetPackageTests
{
    private const string AssetId = AssetSamples.PackageId + ".x";

    private static IReadOnlyList<AssetIssue> Rejects(byte[] package, AssetValidationOptions? options = null)
        => Assert.Throws<AssetPackageException>(() => AssetPackageValidator.Read(new MemoryStream(package), options)).Issues;

    private static void AssertIssue(IReadOnlyList<AssetIssue> issues, AssetIssueCode code, string? path = null, string? detail = null)
        => Assert.True(issues.Any(i => i.Code == code && (path is null || i.Path == path) && (detail is null || i.Detail == detail)),
            $"{code} {path} {detail} 없음: " + string.Join(" | ", issues.Select(i => $"{i.Code} {i.Path} {i.Detail}")));

    /// <summary>에셋 파일 하나를 manifest에 적고 해시까지 맞춘 패키지(내용 검사만 실패하게).</summary>
    private static byte[] WithAsset(string kind, string path, string json, string id = AssetId, AssetManifest? header = null,
        IReadOnlyDictionary<string, byte[]>? extra = null, string? preview = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = Encoding.UTF8.GetBytes(json) };
        foreach (var (key, value) in extra ?? new Dictionary<string, byte[]>()) files[key] = value;
        using var stream = new MemoryStream();
        AssetPackageWriter.Write(stream, (header ?? AssetSamples.Header()) with { Assets = [new AssetManifestEntry(id, kind, path, preview)] }, files);
        return stream.ToArray();
    }

    private static string RecipeJson(string effects, string id = AssetId, int schema = 1, string name = "X")
        => $$"""{ "schemaVersion": {{schema}}, "id": "{{id}}", "name": "{{name}}", "effects": [ {{effects}} ] }""";

    private static string GradientJson(string stops = """{ "position": 0, "color": "#000000" }, { "position": 1, "color": "#FFFFFF" }""", int schema = 1)
        => $$"""{ "schemaVersion": {{schema}}, "id": "{{AssetId}}", "name": "G", "stops": [ {{stops}} ] }""";

    private const string GoodGradient = """{ "stops": [ { "position": 0, "color": "#FF0000" }, { "position": 1, "color": "#0000FF" } ] }""";

    private static byte[] Png(int width, int height)
    {
        using var stream = new MemoryStream();
        PngIO.Save(new PixelImage(width, height), stream);
        return stream.ToArray();
    }

    // ───────────── 정상 패키지 ─────────────

    [Fact]
    public void ValidPackage_RoundTripsEveryValue_AndInstalledAssetsAreReadable()
    {
        var recipe = AssetSamples.Recipe();
        var gradient = AssetSamples.Gradient();
        byte[] bytes = AssetSamples.Build(recipe: recipe, gradient: gradient);

        var package = AssetPackageValidator.Read(new MemoryStream(bytes), new AssetValidationOptions(AppVersion: new SemVersion(1, 0, 0)));
        Assert.Equal((AssetSamples.PackageId, "1.0.0", "Neon Pack"), (package.Manifest.Id, package.Manifest.Version, package.Manifest.Name));
        Assert.Equal([AssetSamples.PackageId + ".neon-hit", AssetSamples.PackageId + ".neon-blue"], package.Items.Select(i => i.Entry.Id).ToArray());
        var readRecipe = Assert.IsType<EffectRecipe>(package.Items[0].Data);
        AssetSamples.AssertSameRecipe(recipe, readRecipe);
        Assert.Equal([false, true, true], readRecipe.Effects.Select(e => e.ClipToPrevious).ToArray());
        Assert.Equal(AssetSamples.OverlayGradient.Stops, readRecipe.Effects[2].ColorGradients["Color"].Stops);
        AssetSamples.AssertSameGradient(gradient, Assert.IsType<GradientAsset>(package.Items[1].Data).Gradient);
        // 미리보기는 패키지에 들어가 디코드된다
        string preview = package.Items[0].Entry.Preview!;
        Assert.Equal(AssetPreview.Png(recipe), package.Files[preview]);
        Assert.Contains("LICENSE.txt", package.Files.Keys);

        // 설치하면 디스크의 파일에서 같은 값을 다시 읽는다
        using var dir = new TempDir();
        var store = AssetStore.Open(Path.Combine(dir.Path, "Assets"));
        var installed = store.Install(AssetSamples.Save(dir.Path, bytes));
        Assert.False(installed.IsBroken);
        var entry = Assert.Single(store.Catalog.Recipes);
        AssetSamples.AssertSameRecipe(recipe, entry.Recipe!);
        AssetSamples.AssertSameGradient(gradient, Assert.Single(store.Catalog.Gradients).Gradient!);
        Assert.Equal(AssetPreview.Png(recipe), File.ReadAllBytes(entry.PreviewPath!));
    }

    [Fact]
    public void Writer_IsDeterministic_AndExportedInstalledPackageValidatesAgain()
    {
        byte[] first = AssetSamples.Build();
        Assert.Equal(first, AssetSamples.Build());

        using var dir = new TempDir();
        var store = AssetStore.Open(Path.Combine(dir.Path, "Assets"));
        store.Install(AssetSamples.Save(dir.Path, first));
        using var exported = new MemoryStream();
        store.ExportPackage(AssetSamples.PackageId, exported);
        exported.Position = 0;
        var again = AssetPackageValidator.Read(exported);
        Assert.Equal(AssetPackageValidator.Read(new MemoryStream(first)).Manifest.Files, again.Manifest.Files);
    }

    // ───────────── ZIP 경로 ─────────────

    [Theory]
    [InlineData("../x.json", AssetIssueCode.PathEscape)]
    [InlineData("assets/../../x.json", AssetIssueCode.PathEscape)]
    [InlineData("assets/./effects/a.json", AssetIssueCode.PathEscape)]
    [InlineData("/x.json", AssetIssueCode.AbsolutePath)]
    [InlineData("C:x.json", AssetIssueCode.AbsolutePath)]
    [InlineData("C:/x.json", AssetIssueCode.AbsolutePath)]
    [InlineData("assets\\effects\\a.json", AssetIssueCode.InvalidPath)]
    [InlineData("assets/effects/CON.json", AssetIssueCode.InvalidPath)]
    [InlineData("assets/effects/a.json.", AssetIssueCode.InvalidPath)]
    [InlineData("assets//effects/a.json", AssetIssueCode.InvalidPath)]
    public void UnsafePaths_AreRejectedBeforeAnythingIsRead(string path, AssetIssueCode code)
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        entries.Add((path, "{}"u8.ToArray()));
        var issues = Rejects(AssetSamples.Zip(entries));
        AssertIssue(issues, code, path);
    }

    [Fact]
    public void CaseOnlyDifferentPaths_AreRejected()
    {
        var issues = Rejects(AssetSamples.Zip([
            ("manifest.json", "{}"u8.ToArray()), ("assets/effects/A.json", "{}"u8.ToArray()), ("assets/effects/a.json", "{}"u8.ToArray()),
        ]));
        AssertIssue(issues, AssetIssueCode.CaseConflict, "assets/effects/a.json");
    }

    [Fact]
    public void DuplicateEntries_AreRejected()
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        var (name, data) = entries.First(e => e.Name.StartsWith("assets/effects/", StringComparison.Ordinal));
        entries.Add((name, data));
        AssertIssue(Rejects(AssetSamples.Zip(entries)), AssetIssueCode.DuplicatePath, name);
    }

    [Fact]
    public void SymlinkEntry_IsRejected()
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        string target = entries.First(e => e.Name.StartsWith("assets/gradients/", StringComparison.Ordinal)).Name;
        // 상위 16비트 = Unix 모드 0xA1FF(symlink, rwxrwxrwx)
        var package = AssetSamples.Zip(entries, (n, entry) =>
        {
            if (n == target) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        });
        AssertIssue(Rejects(package), AssetIssueCode.SpecialFile, target);
    }

    [Theory]
    [InlineData("assets/effects/hack.dll")]
    [InlineData("assets/effects/run.js")]
    [InlineData("previews/icon.svg")]
    [InlineData("README.html")]
    [InlineData("assets/scripts/a.json")]
    [InlineData("tool.exe")]
    public void DisallowedFiles_AreRejected(string path)
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        entries.Add((path, "x"u8.ToArray()));
        AssertIssue(Rejects(AssetSamples.Zip(entries)), AssetIssueCode.DisallowedFile, path);
    }

    // ───────────── manifest와 해시 ─────────────

    [Fact]
    public void UnlistedFile_IsRejected_EvenWhenAllowed()
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        entries.Add(("README.md", "몰래 넣은 문서"u8.ToArray()));
        AssertIssue(Rejects(AssetSamples.Zip(entries)), AssetIssueCode.UnlistedFile, "README.md");
    }

    [Fact]
    public void MissingManifest_IsRejected()
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build()).Where(e => e.Name != AssetManifest.FileName);
        AssertIssue(Rejects(AssetSamples.Zip(entries)), AssetIssueCode.MissingManifest);
    }

    [Fact]
    public void HashMismatch_AndMissingListedFile_AreRejected()
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        int index = entries.FindIndex(e => e.Name.StartsWith("assets/gradients/", StringComparison.Ordinal));
        string changed = entries[index].Name;
        entries[index] = (changed, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries[index].Data).Replace("Neon Blue", "Neon Blub")));
        AssertIssue(Rejects(AssetSamples.Zip(entries)), AssetIssueCode.HashMismatch, changed);

        var missing = AssetSamples.Unzip(AssetSamples.Build()).Where(e => e.Name != "LICENSE.txt");
        AssertIssue(Rejects(AssetSamples.Zip(missing)), AssetIssueCode.MissingFile, "LICENSE.txt");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("name")]
    [InlineData("author")]
    [InlineData("license")]
    [InlineData("minAppVersion")]
    [InlineData("assets")]
    [InlineData("files")]
    public void ManifestMissingRequiredField_IsReportedByFieldName(string field)
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        int index = entries.FindIndex(e => e.Name == AssetManifest.FileName);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(entries[index].Data)!.AsObject();
        manifest.Remove(field);
        entries[index] = (AssetManifest.FileName, AssetJson.ToUtf8(manifest));
        AssertIssue(Rejects(AssetSamples.Zip(entries)), AssetIssueCode.InvalidField, AssetManifest.FileName, field);
    }

    [Theory]
    [InlineData("version", "\"1.0\"")]
    [InlineData("id", "\"Author.Pack\"")]
    [InlineData("license", "\"CC0 1.0\"")]
    [InlineData("schemaVersion", "\"1\"")]
    public void ManifestInvalidValues_AreReportedByFieldName(string field, string value)
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        int index = entries.FindIndex(e => e.Name == AssetManifest.FileName);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(entries[index].Data)!.AsObject();
        manifest[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        entries[index] = (AssetManifest.FileName, AssetJson.ToUtf8(manifest));
        AssertIssue(Rejects(AssetSamples.Zip(entries)), AssetIssueCode.InvalidField, AssetManifest.FileName, field);
    }

    [Fact]
    public void FutureManifestSchema_IsReportedAsFutureSchemaOnly()
    {
        var entries = AssetSamples.Unzip(AssetSamples.Build());
        int index = entries.FindIndex(e => e.Name == AssetManifest.FileName);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(entries[index].Data)!.AsObject();
        manifest["schemaVersion"] = 2;
        manifest["somethingNew"] = true;
        manifest.Remove("license");
        entries[index] = (AssetManifest.FileName, AssetJson.ToUtf8(manifest));
        var issue = Assert.Single(Rejects(AssetSamples.Zip(entries)));
        Assert.Equal((AssetIssueCode.FutureSchema, "2"), (issue.Code, issue.Detail));
    }

    [Fact]
    public void FutureRecipeAndGradientSchemas_AreRejectedPerAsset()
    {
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, "assets/effects/x.json", RecipeJson("""{ "type": "gpixel.effect.fade" }""", schema: 4))),
            AssetIssueCode.FutureSchema, "assets/effects/x.json", "4");
        AssertIssue(Rejects(WithAsset(AssetKinds.Gradient, "assets/gradients/x.json", GradientJson(schema: 5))),
            AssetIssueCode.FutureSchema, "assets/gradients/x.json", "5");
    }

    [Fact]
    public void MinAppVersion_AboveApp_IsAppTooOld_AndUnknownAppVersionSkipsTheCheck()
    {
        byte[] bytes = AssetSamples.Build(AssetSamples.Header() with { MinAppVersion = "2.1.0" });
        var issue = Assert.Single(Rejects(bytes, new AssetValidationOptions(AppVersion: new SemVersion(2, 0, 9))));
        Assert.Equal((AssetIssueCode.AppTooOld, "2.1.0"), (issue.Code, issue.Detail));
        AssetPackageValidator.Read(new MemoryStream(bytes), new AssetValidationOptions(AppVersion: new SemVersion(2, 1, 0)));
        AssetPackageValidator.Read(new MemoryStream(bytes), new AssetValidationOptions(AppVersion: null));
    }

    // ───────────── 크기와 개수 한도 ─────────────

    // 문서(README)는 내용을 해석하지 않으므로, 크기 검사가 없으면 그대로 통과했을 파일이다. manifest에도 해시와 함께 적는다.
    private static readonly AssetPackageLimits BombLimits = new() { MaxFileBytes = 64 * 1024, MaxExpandedBytes = 96 * 1024 };

    private static string Spaces(int count) => new(' ', count);

    /// <summary>압축 폭탄: 아주 잘 줄어드는 내용이 한도를 넘으면 설치는 아무것도 쓰지 않는다.</summary>
    [Fact]
    public void ZipBomb_DeclaredOversizedEntryOrTotal_IsRejected_WithNothingWritten()
    {
        using var dir = new TempDir();
        var store = AssetStore.Open(Path.Combine(dir.Path, "Assets"), new AssetValidationOptions(BombLimits));

        // 1MB 공백 → 몇 KB로 줄어든다
        byte[] bomb = AssetSamples.Build(more: b => b.AddDocument("README.md", Spaces(1024 * 1024)));
        Assert.True(bomb.Length < 64 * 1024);
        AssertIssue(Rejects(bomb, new AssetValidationOptions(BombLimits)), AssetIssueCode.FileTooLarge, "README.md", (64 * 1024).ToString(CultureInfo.InvariantCulture));
        var ex = Assert.Throws<AssetPackageException>(() => store.Install(AssetSamples.Save(dir.Path, bomb)));
        AssertIssue(ex.Issues, AssetIssueCode.FileTooLarge, "README.md");

        // 하나하나는 한도 안이지만 합이 넘음
        byte[] many = AssetSamples.Build(more: b =>
        {
            b.AddDocument("README.md", Spaces(60 * 1024));
            b.AddDocument("README.txt", Spaces(60 * 1024));
        });
        AssertIssue(Rejects(many, new AssetValidationOptions(BombLimits)), AssetIssueCode.ExpandedTooLarge);
        Assert.Throws<AssetPackageException>(() => store.Install(AssetSamples.Save(dir.Path, many, "many.gpixel-asset")));

        AssertNothingInstalled(store);
        // 한도 안이면 같은 모양의 패키지가 설치된다(위 거부가 다른 까닭이 아님)
        store.Install(AssetSamples.Save(dir.Path, AssetSamples.Build(more: b => b.AddDocument("README.md", Spaces(60 * 1024))), "ok.gpixel-asset"));
        Assert.Single(store.Catalog.Packages);
    }

    /// <summary>ZIP 머리에 작은 크기를 거짓으로 적어도 한도보다 많이 풀어 받아들이지 않는다.</summary>
    [Fact]
    public void ZipBomb_LyingDeclaredSize_IsRejected_WithNothingWritten()
    {
        byte[] bomb = AssetSamples.Build(more: b => b.AddDocument("README.md", Spaces(1024 * 1024)));
        PatchUncompressedSize(bomb, "README.md", 10);
        using (var zip = new ZipArchive(new MemoryStream(bomb), ZipArchiveMode.Read))
            Assert.Equal(10, zip.GetEntry("README.md")!.Length);

        var issues = Rejects(bomb, new AssetValidationOptions(BombLimits));
        Assert.Contains(issues, i => i.Path == "README.md" && i.Code is AssetIssueCode.FileTooLarge or AssetIssueCode.HashMismatch or AssetIssueCode.NotZip);
        using var dir = new TempDir();
        var store = AssetStore.Open(Path.Combine(dir.Path, "Assets"), new AssetValidationOptions(BombLimits));
        Assert.Throws<AssetPackageException>(() => store.Install(AssetSamples.Save(dir.Path, bomb)));
        AssertNothingInstalled(store);
    }

    /// <summary>로컬 머리와 중앙 디렉터리의 압축 해제 크기 칸을 <paramref name="size"/>로 바꾼다.</summary>
    private static void PatchUncompressedSize(byte[] zip, string name, uint size)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int patched = 0;
        for (int i = 0; i + 46 < zip.Length; i++)
        {
            uint signature = BitConverter.ToUInt32(zip, i);
            if (signature == 0x04034b50 && zip.AsSpan(i + 30, nameBytes.Length).SequenceEqual(nameBytes)
                                        && BitConverter.ToUInt16(zip, i + 26) == nameBytes.Length)
            {
                BitConverter.TryWriteBytes(zip.AsSpan(i + 22, 4), size);
                patched++;
            }
            else if (signature == 0x02014b50 && zip.AsSpan(i + 46, nameBytes.Length).SequenceEqual(nameBytes)
                                             && BitConverter.ToUInt16(zip, i + 28) == nameBytes.Length)
            {
                BitConverter.TryWriteBytes(zip.AsSpan(i + 24, 4), size);
                patched++;
            }
        }
        Assert.Equal(2, patched);
    }

    private static void AssertNothingInstalled(AssetStore store)
    {
        Assert.Empty(Directory.GetFileSystemEntries(store.PackagesDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(store.StagingDirectory));
        Assert.False(File.Exists(store.InstalledPath));
        Assert.Empty(store.Catalog.Packages);
    }

    [Fact]
    public void PackageFileOverLimit_IsRejectedBeforeOpening()
    {
        byte[] bytes = AssetSamples.Build();
        var limits = new AssetPackageLimits { MaxPackageBytes = bytes.Length - 1 };
        AssertIssue(Rejects(bytes, new AssetValidationOptions(limits)), AssetIssueCode.PackageTooLarge);
        AssertIssue(Rejects("not a zip at all"u8.ToArray()), AssetIssueCode.NotZip);
    }

    [Fact]
    public void TooManyFilesOrAssets_AreRejected()
    {
        byte[] bytes = AssetSamples.Build(); // manifest, 레시피, 그라데이션, 미리보기 2, LICENSE = 6개
        AssertIssue(Rejects(bytes, new AssetValidationOptions(new AssetPackageLimits { MaxFiles = 5 })), AssetIssueCode.TooManyFiles, detail: "5");
        AssertIssue(Rejects(bytes, new AssetValidationOptions(new AssetPackageLimits { MaxAssets = 1 })), AssetIssueCode.TooManyItems, AssetManifest.FileName, "assets");
        AssetPackageValidator.Read(new MemoryStream(bytes), new AssetValidationOptions(new AssetPackageLimits { MaxFiles = 6, MaxAssets = 2 }));
    }

    [Fact]
    public void TooManyEffectsOrGradientStops_AreRejected()
    {
        byte[] bytes = AssetSamples.Build();
        var recipePath = AssetPackageValidator.Read(new MemoryStream(bytes)).Items[0].Entry.Path;
        var gradientPath = AssetPackageValidator.Read(new MemoryStream(bytes)).Items[1].Entry.Path;
        // 견본 레시피는 효과 3개, 그라데이션은 색·알파 정지점 3개씩
        AssertIssue(Rejects(bytes, new AssetValidationOptions(new AssetPackageLimits { MaxEffectsPerRecipe = 2 })), AssetIssueCode.TooManyItems, recipePath, "effects");
        var stops = Rejects(bytes, new AssetValidationOptions(new AssetPackageLimits { MaxGradientStops = 2 }));
        AssertIssue(stops, AssetIssueCode.TooManyItems, gradientPath, "stops");
        AssertIssue(stops, AssetIssueCode.TooManyItems, gradientPath, "alphaStops");
        // 레시피 효과의 색 슬롯 그라데이션도 정지점 한도를 따른다
        AssertIssue(stops, AssetIssueCode.InvalidEffectSetting, recipePath, "effects[2].colorGradients.Color");
        AssetPackageValidator.Read(new MemoryStream(bytes), new AssetValidationOptions(new AssetPackageLimits { MaxEffectsPerRecipe = 3, MaxGradientStops = 3 }));
    }

    // ───────────── 효과 schema ─────────────

    [Fact]
    public void ValidEffectJson_WithColorSlotGradient_IsAccepted()
    {
        string effect = $$"""
            { "type": "{{OutlineDefinition.Id}}", "version": 1, "enabled": true, "clipToPrevious": false,
              "settings": { "thickness": 3, "colorMode": "shade", "shadeAmount": -100, "color": "#FF00FF80" },
              "colorGradients": { "Color": {{GoodGradient}} } }
            """;
        var package = AssetPackageValidator.Read(new MemoryStream(WithAsset(AssetKinds.EffectRecipe, "assets/effects/x.json", RecipeJson(effect))));
        var outline = Assert.IsType<OutlineEffect>(Assert.Single(Assert.IsType<EffectRecipe>(Assert.Single(package.Items).Data).Effects));
        Assert.Equal((3, OutlineColorMode.Shade, -100), (outline.Thickness, outline.ColorMode, outline.ShadeAmount));
        Assert.Equal(2, outline.ColorGradients["Color"].Stops.Count);
    }

    [Theory]
    [InlineData("""{ "type": "gpixel.effect.nope", "settings": {} }""", AssetIssueCode.UnknownEffect, "effects[0]: gpixel.effect.nope")]
    [InlineData("""{ "type": "outline", "thickness": 2 }""", AssetIssueCode.UnknownEffect, "effects[0]: outline")]
    [InlineData("""{ "type": "gpixel.effect.outline", "settings": { "thickness": 99 } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].settings.thickness")]
    [InlineData("""{ "type": "gpixel.effect.outline", "settings": { "thickness": 0 } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].settings.thickness")]
    [InlineData("""{ "type": "gpixel.effect.outline", "settings": { "thickness": "2" } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].settings.thickness")]
    [InlineData("""{ "type": "gpixel.effect.outline", "settings": { "glowRadius": 4 } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].settings.glowRadius")]
    [InlineData("""{ "type": "gpixel.effect.outline", "settings": { "colorMode": "Rainbow" } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].settings.colorMode")]
    [InlineData("""{ "type": "gpixel.effect.outline", "settings": { "color": "red" } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].settings.color")]
    [InlineData("""{ "type": "gpixel.effect.outline", "version": 99 }""", AssetIssueCode.InvalidEffectSetting, "effects[0].version")]
    [InlineData("""{ "type": "gpixel.effect.outline", "frames": [1, 2] }""", AssetIssueCode.InvalidEffectSetting, "effects[0].frames")]
    [InlineData("""{ "type": "gpixel.effect.outline", "colorGradients": { "Nope": { "stops": [ { "position": 0, "color": "#FF0000" }, { "position": 1, "color": "#0000FF" } ] } } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].colorGradients.Nope")]
    [InlineData("""{ "type": "gpixel.effect.outline", "colorGradients": { "Color": { "stops": [ { "position": 0, "color": "#FF0000" } ] } } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].colorGradients.Color")]
    [InlineData("""{ "type": "gpixel.effect.outline", "colorGradients": { "Color": { "stops": [ { "position": 0, "color": "#FF0000" }, { "position": 2, "color": "#0000FF" } ] } } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].colorGradients.Color")]
    [InlineData("""{ "type": "gpixel.effect.outline", "colorGradients": { "Color": { "shape": "Blob", "stops": [ { "position": 0, "color": "#FF0000" }, { "position": 1, "color": "#0000FF" } ] } } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].colorGradients.Color")]
    [InlineData("""{ "type": "gpixel.effect.outline", "colorGradients": { "Color": { "script": 1, "stops": [ { "position": 0, "color": "#FF0000" }, { "position": 1, "color": "#0000FF" } ] } } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].colorGradients.Color")]
    [InlineData("""{ "type": "gpixel.effect.outline", "colorGradients": { "Color": { "stops": [ { "position": 0, "color": "#FF0000" }, { "position": 1, "color": "#0000FF" } ], "alphaStops": [ { "position": 0, "alpha": 300 } ] } } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].colorGradients.Color")]
    [InlineData("""{ "type": "gpixel.effect.colorcycle", "settings": { "colors": ["#FF0000", 5] } }""", AssetIssueCode.InvalidEffectSetting, "effects[0].settings.colors")]
    public void EffectSchemaViolations_AreRejectedWithFieldDetail(string effect, AssetIssueCode code, string detail)
    {
        const string path = "assets/effects/x.json";
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, RecipeJson(effect))), code, path, detail);
    }

    [Fact]
    public void RecipeUnknownTopLevelKey_AndEmptyEffects_AreRejected()
    {
        const string path = "assets/effects/x.json";
        string extra = """{ "schemaVersion": 1, "id": "author.neon-pack.x", "name": "X", "script": "evil()", "effects": [ { "type": "gpixel.effect.fade" } ] }""";
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, extra)), AssetIssueCode.InvalidField, path, "script");
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, RecipeJson(""))), AssetIssueCode.InvalidField, path, "effects");
    }

    // ───────────── 미리보기 ─────────────

    [Fact]
    public void CorruptOrOversizedPreview_IsRejected()
    {
        const string asset = "assets/gradients/x.json";
        byte[] good = Png(4, 4);
        byte[] corrupt = good.ToArray();
        corrupt.AsSpan(40).Fill(0x5A); // 머리는 맞고 데이터는 손상
        AssertIssue(Rejects(WithAsset(AssetKinds.Gradient, asset, GradientJson(), extra: new Dictionary<string, byte[]> { ["previews/x.png"] = corrupt }, preview: "previews/x.png")),
            AssetIssueCode.InvalidPreview, "previews/x.png");
        AssertIssue(Rejects(WithAsset(AssetKinds.Gradient, asset, GradientJson(), extra: new Dictionary<string, byte[]> { ["previews/x.png"] = "not a png"u8.ToArray() }, preview: "previews/x.png")),
            AssetIssueCode.InvalidPreview, "previews/x.png");

        // 실제 600×1 그림, 그리고 머리만 거대한 그림(풀지 않고 거부)
        AssertIssue(Rejects(WithAsset(AssetKinds.Gradient, asset, GradientJson(), extra: new Dictionary<string, byte[]> { ["previews/x.png"] = Png(600, 1) }, preview: "previews/x.png")),
            AssetIssueCode.PreviewTooLarge, "previews/x.png");
        byte[] giant = good.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(giant.AsSpan(16), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(giant.AsSpan(20), 100_000);
        AssertIssue(Rejects(WithAsset(AssetKinds.Gradient, asset, GradientJson(), extra: new Dictionary<string, byte[]> { ["previews/giant.png"] = giant })),
            AssetIssueCode.PreviewTooLarge, "previews/giant.png");

        // 미리보기 자리가 아닌 경로를 미리보기로 적음
        AssertIssue(Rejects(WithAsset(AssetKinds.Gradient, asset, GradientJson(), preview: "assets/gradients/x.json")),
            AssetIssueCode.InvalidPreview, "assets/gradients/x.json");
        AssetPackageValidator.Read(new MemoryStream(WithAsset(AssetKinds.Gradient, asset, GradientJson(), extra: new Dictionary<string, byte[]> { ["previews/x.png"] = Png(512, 16) }, preview: "previews/x.png")));
    }

    // ───────────── 에셋 JSON ─────────────

    [Fact]
    public void CorruptDeepOrLongJson_IsRejected()
    {
        const string path = "assets/effects/x.json";
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, "{ \"schemaVersion\": 1, ")), AssetIssueCode.InvalidJson, path);
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, "[1, 2]")), AssetIssueCode.InvalidJson, path);

        string deep = new string('[', 40) + new string(']', 40);
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, RecipeJson("""{ "type": "gpixel.effect.fade" }""").Replace("\"name\"", "\"deep\": " + deep + ", \"name\""))),
            AssetIssueCode.JsonTooDeep, path, "16");
        // 한도 바로 밑 깊이는 깊이 문제가 아니다(모르는 키로만 거부)
        string fine = new string('[', 14) + new string(']', 14);
        var shallow = Rejects(WithAsset(AssetKinds.EffectRecipe, path, RecipeJson("""{ "type": "gpixel.effect.fade" }""").Replace("\"name\"", "\"deep\": " + fine + ", \"name\"")));
        Assert.DoesNotContain(shallow, i => i.Code == AssetIssueCode.JsonTooDeep);

        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, RecipeJson("""{ "type": "gpixel.effect.fade" }""", name: new string('가', 4097)))),
            AssetIssueCode.StringTooLong, path, "4096");
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, RecipeJson("""{ "type": "gpixel.effect.fade" }""", name: new string('a', 101)))),
            AssetIssueCode.InvalidField, path, "name");
    }

    [Fact]
    public void UnknownAssetKind_IsRejected()
        => AssertIssue(Rejects(WithAsset("effect-shader", "assets/effects/x.json", RecipeJson("""{ "type": "gpixel.effect.fade" }"""))),
            AssetIssueCode.UnknownKind, AssetManifest.FileName, "effect-shader");

    [Fact]
    public void AssetIdOutsidePackage_OrMismatchedWithFile_IsRejected()
    {
        const string path = "assets/effects/x.json";
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, RecipeJson("""{ "type": "gpixel.effect.fade" }""", id: "other.pack.x"), id: "other.pack.x")),
            AssetIssueCode.InvalidField, AssetManifest.FileName, "assets[0].id");
        // manifest와 파일의 ID가 다름
        AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, path, RecipeJson("""{ "type": "gpixel.effect.fade" }""", id: AssetSamples.PackageId + ".y"))),
            AssetIssueCode.InvalidField, path, "id");
    }

    [Fact]
    public void AssetFileOutsideItsKindFolder_IsRejected()
        => AssertIssue(Rejects(WithAsset(AssetKinds.EffectRecipe, "assets/gradients/x.json", RecipeJson("""{ "type": "gpixel.effect.fade" }"""))),
            AssetIssueCode.DisallowedFile, "assets/gradients/x.json");

    /// <summary>문제 문구는 지금 UI 언어로 만들고 자세한 값을 끼운다.</summary>
    [Fact]
    public void IssueMessage_SubstitutesDetailInCurrentUiLanguage()
    {
        var issue = new AssetIssue(AssetIssueCode.AppTooOld, "manifest.json", "9.8.7");
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko");
            string korean = issue.Message;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            string english = issue.Message;
            Assert.All(new[] { korean, english }, message =>
            {
                Assert.StartsWith("manifest.json: ", message);
                Assert.Contains("9.8.7", message);
                Assert.DoesNotContain("{detail}", message);
            });
            Assert.NotEqual(korean, english);
            Assert.Contains(korean, c => c is >= '가' and <= '힣');
            Assert.DoesNotContain(english, c => c is >= '가' and <= '힣');
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
