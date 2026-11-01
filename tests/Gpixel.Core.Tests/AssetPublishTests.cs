using System.IO.Compression;
using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;

namespace Gpixel.Core.Tests;

/// <summary>
/// 게시 흐름(5단계): 게시 창 검사가 CI(카탈로그 만들기)와 같은 공개 규칙으로 작성자·라이선스 없는 제출을 막고, 앱이 만든 제출 zip이
/// 그대로 카탈로그 만들기·최신 확인을 통과하며 다른 컴퓨터(새 저장소)에 설치되는지, 버전 제안과 게시 버전 규칙.
/// </summary>
public class AssetPublishTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);

    /// <summary>개인 레시피 하나와 그라데이션 하나가 든 저장소.</summary>
    private static AssetStore PersonalStore(TempDir dir)
    {
        var store = AssetStore.Open(Path.Combine(dir.Path, "Assets"));
        store.SavePersonalRecipe(AssetSamples.Recipe());
        store.SavePersonalGradient(null, AssetSamples.Gradient());
        return store;
    }

    private static PublishRequest Request(AssetStore store, string version = "1.0.0", string license = "CC0-1.0") => new()
    {
        Author = new AssetAuthor("pixel-cat", "Pixel Cat", "https://example.com/cat"),
        Id = "pixel-cat.neon",
        Version = version,
        Name = "Neon Set",
        Summary = "네온 외곽선 효과와 파란 그라데이션",
        Tags = AssetPublishCheck.ParseTags("Neon, effect gradient"),
        License = license,
        LicenseText = AssetPublishCheck.LicenseNotice(license, "Neon Set", "Pixel Cat", 2026),
        Assets = store.Catalog.Personal.Select(entry => new PublishAsset(entry.Kind, entry.Data, AssetPreview.Png(entry.Data))).ToArray(),
        MinAppVersion = "1.14.55",
    };

    private static PublishResult Check(PublishRequest request, IReadOnlyList<RemoteCatalogEntry>? catalog = null, IEnumerable<PublishedVersion>? saved = null)
        => AssetPublishCheck.Check(request, catalog ?? [], saved ?? [], Today);

    /// <summary>제출 zip을 관리자처럼 <paramref name="catalog"/>에 푼다.</summary>
    private static void Unpack(PublishResult result, string catalog)
    {
        using var zip = new MemoryStream();
        AssetPublishCheck.WriteSubmission(result, zip);
        zip.Position = 0;
        ZipFile.ExtractToDirectory(zip, catalog);
    }

    // ───────────── 작성자·라이선스 없는 공개 제출 ─────────────

    private static readonly Dictionary<string, Func<PublishRequest, PublishRequest>> BadRequests = new()
    {
        ["no-author"] = r => r with { Author = new AssetAuthor("", "") },
        ["bad-author-url"] = r => r with { Author = r.Author with { Url = "http://example.com" } },
        ["no-license"] = r => r with { License = "", LicenseText = null },
        ["license-without-text"] = r => r with { License = "LicenseRef-MyTerms", LicenseText = "  " },
        ["personal-license"] = r => r with { License = "LicenseRef-Personal", LicenseText = "personal" },
        ["personal-author"] = r => r with { Author = new AssetAuthor("personal", "Me"), Id = "personal.neon" },
        ["other-author-namespace"] = r => r with { Id = "gpixel.neon" },
        ["reserved-gpixel"] = r => r with { Author = new AssetAuthor("gpixel", "Not Gpixel"), Id = "gpixel.neon" },
        ["no-preview"] = r => r with { Assets = r.Assets.Select(a => a with { Preview = null }).ToArray() },
        ["no-summary"] = r => r with { Summary = " " },
        ["bad-tag"] = r => r with { Tags = ["Neon!"] },
        ["bad-version"] = r => r with { Version = "1.0" },
        ["no-assets"] = r => r with { Assets = [] },
    };

    public static TheoryData<string, string> BadRequestCodes => new()
    {
        { "no-author", nameof(PublishIssueCode.AuthorId) },
        { "bad-author-url", nameof(PublishIssueCode.AuthorUrl) },
        { "no-license", nameof(PublishIssueCode.License) },
        { "license-without-text", nameof(CatalogBuildIssueCode.MissingLicense) },
        { "personal-license", nameof(CatalogBuildIssueCode.MissingLicense) },
        { "personal-author", nameof(CatalogBuildIssueCode.MissingAuthor) },
        { "other-author-namespace", nameof(CatalogBuildIssueCode.AuthorMismatch) },
        { "reserved-gpixel", nameof(CatalogBuildIssueCode.OfficialMismatch) },
        { "no-preview", nameof(CatalogBuildIssueCode.MissingPreview) },
        { "no-summary", nameof(PublishIssueCode.Summary) },
        { "bad-tag", nameof(PublishIssueCode.Tags) },
        { "bad-version", nameof(PublishIssueCode.Version) },
        { "no-assets", nameof(PublishIssueCode.NoAssets) },
    };

    [Theory]
    [MemberData(nameof(BadRequestCodes))]
    public void PublicSubmission_BrokenRule_IsAnErrorThatBlocksSaving(string label, string code)
    {
        using var dir = new TempDir();
        var result = Check(BadRequests[label](Request(PersonalStore(dir))));
        Assert.False(result.Ok, label);
        Assert.Contains(result.Issues, issue => issue.IsError && (issue.Code?.ToString() ?? issue.CatalogCode?.ToString()) == code);
        Assert.All(result.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Message)));
        Assert.Throws<InvalidOperationException>(() => AssetPublishCheck.WriteSubmission(result, new MemoryStream()));
    }

    [Fact]
    public void SubmissionsWithoutLicenseOrAuthor_AreRejectedByTheCatalogBuilderToo()
    {
        // 게시 창이 CI 규칙(패키지가 만들어진 뒤의 규칙)으로 막는 패키지는 카탈로그 만들기도 같은 문제로 거부한다.
        using var dir = new TempDir();
        var store = PersonalStore(dir);
        foreach (var (label, code) in new[] { ("license-without-text", CatalogBuildIssueCode.MissingLicense), ("personal-author", CatalogBuildIssueCode.MissingAuthor) })
        {
            var request = BadRequests[label](Request(store));
            var header = new AssetManifest
            {
                Id = request.Id, Version = request.Version, Name = request.Name, Author = request.Author, License = request.License, MinAppVersion = "1.0.0",
            };
            using var package = new MemoryStream();
            store.ExportPersonal(store.Catalog.Personal.Select(e => e.Id), header, package, entry => AssetPreview.Png(entry.Data));
            string folder = Path.Combine(dir.Path, label, "catalog", request.Id);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "pack" + AssetPackage.Extension), package.ToArray());
            File.WriteAllBytes(Path.Combine(folder, RemoteCatalogBuilder.ReviewFileName),
                RemoteCatalogBuilder.WriteReview("요약", ["neon"], RemoteReview.Reviewed, Today, Today, null));

            var build = RemoteCatalogBuilder.Build(Path.GetDirectoryName(folder)!);
            Assert.False(build.Ok, label);
            Assert.Contains(build.Issues, issue => issue.Code == code);
            Assert.Contains(Check(request).Issues, issue => issue.CatalogCode == code);
        }
    }

    // ───────────── 앱이 만든 패키지: CI 통과와 다른 컴퓨터 설치 ─────────────

    [Fact]
    public void AppMadeSubmission_PassesCatalogBuildAndCheck_AndInstallsIntoAFreshStore()
    {
        using var dir = new TempDir();
        var store = PersonalStore(dir);
        byte[] customPreview = AssetPreview.Png(new GradientAsset("", AssetSamples.Gradient("Cover")))!;
        var result = Check(Request(store) with { CustomPreview = customPreview });
        Assert.True(result.Ok, string.Join("\n", result.Issues));
        Assert.Empty(result.Issues);
        Assert.Equal("pixel-cat.neon-1.0.0.gpixel-asset", result.PackageFileName);

        // 관리자 흐름: 제출 zip을 catalog/에 풀고 만들기 → 쓰기 → --check(다시 만들어 최신인지).
        string catalog = Path.Combine(dir.Path, "repo", "catalog");
        string output = Path.Combine(dir.Path, "repo", "docs", "catalog");
        Directory.CreateDirectory(catalog);
        Unpack(result, catalog);
        Assert.Equal(["pixel-cat.neon-1.0.0.gpixel-asset", RemoteCatalogBuilder.ReviewFileName],
            Directory.GetFiles(Path.Combine(catalog, "pixel-cat.neon")).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal).ToArray());
        var build = RemoteCatalogBuilder.Build(catalog, output);
        Assert.True(build.Ok, string.Join("\n", build.Issues));
        RemoteCatalogBuilder.Write(build, output);
        var check = RemoteCatalogBuilder.Build(catalog, output);
        Assert.True(check.Ok, string.Join("\n", check.Issues));
        Assert.Empty(RemoteCatalogBuilder.Stale(check, output));

        var entry = Assert.Single(build.Entries);
        Assert.Equal(("pixel-cat.neon", "1.0.0", "Neon Set", "네온 외곽선 효과와 파란 그라데이션", "CC0-1.0", RemoteReview.Reviewed),
            (entry.Id, entry.Version, entry.Name, entry.Summary, entry.License, entry.Review));
        Assert.Equal(["neon", "effect", "gradient"], entry.Tags);
        Assert.Equal(new AssetAuthor("pixel-cat", "Pixel Cat", "https://example.com/cat"), entry.Author);
        Assert.Equal((Today, Today), (entry.Published, entry.Updated));
        Assert.Equal(customPreview, File.ReadAllBytes(Path.Combine(output, entry.Preview!.Url)));
        byte[] published = File.ReadAllBytes(Path.Combine(output, entry.Package.Url));
        Assert.Equal(result.Package, published);

        // 다른 컴퓨터: 새 저장소에 게시된 파일을 설치하면 같은 레시피·그라데이션이 생긴다.
        var other = AssetStore.Open(Path.Combine(dir.Path, "other", "Assets"));
        var installed = other.Install(AssetSamples.Save(dir.Path, published, "downloaded.gpixel-asset"));
        Assert.Equal(("pixel-cat.neon", "1.0.0", false), (installed.Id, installed.Version, installed.IsBroken));
        var recipe = Assert.Single(other.Catalog.Recipes);
        Assert.Equal(("Neon Hit", "pixel-cat.neon.neon-hit"), (recipe.Name, recipe.Id));
        AssetSamples.AssertSameRecipe(store.Catalog.Personal.Single(e => e.Recipe is not null).Recipe!, recipe.Recipe!);
        var gradient = Assert.Single(other.Catalog.Gradients);
        AssetSamples.AssertSameGradient(AssetSamples.Gradient(), gradient.Gradient!);
        Assert.Contains("CC0-1.0", File.ReadAllText(Path.Combine(installed.Folder, AssetPublishCheck.LicenseFileName)));
    }

    [Fact]
    public void PreviewChoice_UsesTheChosenAssetPreview_AndSubmissionUrlPrefillsTheIssueForm()
    {
        using var dir = new TempDir();
        var result = Check(Request(PersonalStore(dir)) with { PreviewAsset = 1 });
        Assert.True(result.Ok, string.Join("\n", result.Issues));
        var review = JsonNode.Parse(result.Review!)!;
        Assert.Equal(result.Manifest!.Assets[1].Preview, review["preview"]!.GetValue<string>());
        Assert.Equal("reviewed", review["review"]!.GetValue<string>());

        string url = AssetPublishCheck.SubmissionUrl(result);
        Assert.StartsWith(AppUpdate.RepoUrl + "/issues/new?template=asset-submission.yml&", url);
        var query = new Uri(url).Query.TrimStart('?').Split('&')
            .ToDictionary(pair => pair.Split('=')[0], pair => Uri.UnescapeDataString(pair.Split('=')[1]));
        Assert.Equal(("pixel-cat.neon", "1.0.0", "Neon Set", "neon, effect, gradient", "CC0-1.0"),
            (query["package-id"], query["version"], query["name"], query["tags"], query["license"]));
        Assert.Equal("네온 외곽선 효과와 파란 그라데이션", query["summary"]);
    }

    // ───────────── 버전 ─────────────

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("not-a-version", "1.0.0")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("2.0.0-beta.1", "2.0.0")]
    public void SuggestVersion_FirstIsOneZero_ReleaseRaisesMinor_PrereleaseBecomesRelease(string? previous, string expected)
        => Assert.Equal(expected, AssetPublishCheck.SuggestVersion(previous));

    [Fact]
    public void PublishedVersions_SameVersionMustKeepContent_NoDowngrade_SuggestedBumpPasses()
    {
        using var dir = new TempDir();
        var store = PersonalStore(dir);
        var first = Check(Request(store, "1.2.0"));
        Assert.True(first.Ok, string.Join("\n", first.Issues));

        // 게시된 1.2.0(온라인 목록)
        string catalog = Path.Combine(dir.Path, "catalog");
        string output = Path.Combine(dir.Path, "docs-catalog");
        Directory.CreateDirectory(catalog);
        Unpack(first, catalog);
        var build = RemoteCatalogBuilder.Build(catalog, output);
        RemoteCatalogBuilder.Write(build, output);
        var online = build.Entries;

        // 같은 내용 다시 저장은 문제없음(같은 바이트).
        var again = Check(Request(store, "1.2.0"), online);
        Assert.True(again.Ok, string.Join("\n", again.Issues));
        Assert.Equal(first.Package, again.Package);

        // 내용을 바꾸고 같은 버전 → 거부, 낮은 버전 → 거부, 제안 버전 → 통과(게시 날짜는 처음 날짜를 이음).
        store.SavePersonalRecipe(AssetSamples.Recipe(thickness: 3) with { Id = store.Catalog.Personal.Single(e => e.Recipe is not null).Id });
        Assert.Contains(Check(Request(store, "1.2.0"), online).Issues, i => i.CatalogCode == CatalogBuildIssueCode.PublishedChanged);
        Assert.Contains(Check(Request(store, "1.1.9"), online).Issues, i => i.CatalogCode == CatalogBuildIssueCode.VersionDowngrade);
        string next = AssetPublishCheck.SuggestVersion(AssetPublishCheck.LatestVersion("pixel-cat.neon", online, []));
        Assert.Equal("1.3.0", next);
        var bumped = Check(Request(store, next), online);
        Assert.True(bumped.Ok, string.Join("\n", bumped.Issues));

        // 관리자가 새 버전으로 바꾸면 CI도 통과한다.
        Directory.Delete(Path.Combine(catalog, "pixel-cat.neon"), recursive: true);
        Unpack(bumped with { Review = RemoteCatalogBuilder.WriteReview("새 두께", ["neon"], RemoteReview.Reviewed, Today, Today.AddDays(3), null) }, catalog);
        var rebuilt = RemoteCatalogBuilder.Build(catalog, output);
        Assert.True(rebuilt.Ok, string.Join("\n", rebuilt.Issues));
        Assert.Equal("1.3.0", Assert.Single(rebuilt.Entries).Version);

        // 이 컴퓨터의 저장 기록: 가장 높은 버전으로 제안, 같은 버전을 다른 내용으로 다시 저장하면 경고만.
        PublishedVersion[] saved = [new("pixel-cat.neon", "1.4.0-rc.1", "x"), new("pixel-cat.neon", "1.3.0", first.Sha256!)];
        Assert.Equal("1.4.0-rc.1", AssetPublishCheck.LatestVersion("pixel-cat.neon", online, saved));
        var warned = Check(Request(store, "1.3.0"), online, saved);
        Assert.True(warned.Ok, string.Join("\n", warned.Issues));
        var warning = Assert.Single(warned.Issues);
        Assert.Equal((PublishSeverity.Warning, PublishIssueCode.SavedVersionChanged), (warning.Severity, warning.Code));
    }
}
