using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;

namespace Gpixel.Core.Tests;

/// <summary>
/// 원격 카탈로그(4단계): index 형식, 정적 카탈로그 만들기와 최신 확인, 가짜 HTTP handler로 client의 쪽 읽기·재시도·취소·
/// 해시 확인·오프라인 cache·미리보기 cache, 최소 앱 버전 까닭, 업데이트 찾기.
/// </summary>
public class RemoteCatalogTests
{
    private const string Site = "https://example.test/GPixel/catalog/";
    private static readonly Uri IndexUrl = new(Site + RemoteCatalogFormat.FirstPageName);

    /// <summary>주소 → 응답 함수. 부른 주소를 차례로 남긴다. 응답이 없으면 404.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Routes = new(StringComparer.Ordinal);
        public readonly List<string> Calls = [];

        public void Serve(string url, byte[] data)
            => Routes[url] = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });

        /// <summary>출력 폴더의 모든 파일을 <see cref="Site"/> 아래 주소로 낸다(GitHub Pages 흉내).</summary>
        public void ServeFolder(string folder)
        {
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                Serve(Site + Path.GetRelativePath(folder, file).Replace('\\', '/'), File.ReadAllBytes(file));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            lock (Calls) Calls.Add(url);
            return Routes.TryGetValue(url, out var route)
                ? route(request, cancellationToken)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeHandler Handler = new();
        public readonly List<TimeSpan> Delays = [];

        public string CatalogSource => Path.Combine(Dir.Path, "catalog");
        public string Output => Path.Combine(Dir.Path, "docs-catalog");
        public string Cache => Path.Combine(Dir.Path, "cache");

        public RemoteAssetClient Client(RemoteClientOptions? options = null)
            => new(new HttpClient(Handler), IndexUrl, Cache, "Gpixel-test/1.0", (options ?? new RemoteClientOptions()) with
            {
                Delay = (delay, token) =>
                {
                    Delays.Add(delay);
                    return Task.CompletedTask;
                },
            });

        /// <summary>제출 폴더 하나(패키지 + review.json)를 만든다.</summary>
        public string Submit(byte[] package, string folder = AssetSamples.PackageId, string? review = null)
        {
            string dir = Path.Combine(CatalogSource, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "pack.gpixel-asset"), package);
            File.WriteAllText(Path.Combine(dir, RemoteCatalogBuilder.ReviewFileName), review ?? Review());
            return dir;
        }

        /// <summary>카탈로그를 만들어 출력 폴더에 쓰고 가짜 사이트로 낸다.</summary>
        public RemoteCatalogBuild Publish(int pageSize = RemoteCatalogFormat.PageSize)
        {
            var build = RemoteCatalogBuilder.Build(CatalogSource, Output, pageSize);
            Assert.True(build.Ok, string.Join("\n", build.Issues));
            RemoteCatalogBuilder.Write(build, Output);
            Handler.ServeFolder(Output);
            return build;
        }

        public void Dispose() => Dir.Dispose();
    }

    private static string Review(string status = "reviewed", string summary = "네온 효과 묶음", string extra = "")
        => $$"""{ "schemaVersion": 1, "summary": "{{summary}}", "tags": ["neon", "effect"], "review": "{{status}}", "published": "2026-10-01", "updated": "2026-10-28"{{extra}} }""";

    private static byte[] Pack(string version = "1.0.0", string id = AssetSamples.PackageId, string name = "Neon Pack", string license = "CC0-1.0",
        string minApp = "0.1.0", string author = "author", bool licenseFile = true)
    {
        var header = AssetSamples.Header(version, id) with { Name = name, License = license, MinAppVersion = minApp, Author = new AssetAuthor(author, "Author") };
        var builder = new AssetPackageBuilder(header);
        var recipe = AssetSamples.Recipe(thickness: version == "1.0.0" ? 2 : 3);
        builder.Add(recipe, AssetPreview.Png(recipe));
        builder.Add(AssetSamples.Gradient());
        if (licenseFile) builder.AddDocument("LICENSE.txt", "CC0");
        using var stream = new MemoryStream();
        builder.Write(stream);
        return stream.ToArray();
    }

    private static RemoteCatalogEntry Entry(string minApp = "1.0.0", IReadOnlyDictionary<string, int>? kinds = null) => new()
    {
        Id = AssetSamples.PackageId, Version = "1.2.0", Name = "Neon Pack", Summary = "요약", Tags = ["neon"],
        Author = new AssetAuthor("author", "Author", "https://example.com"), License = "CC0-1.0",
        Kinds = kinds ?? new Dictionary<string, int> { [AssetKinds.EffectRecipe] = 1, [AssetKinds.Gradient] = 2 },
        MinAppVersion = minApp, Package = new RemoteFile("packages/a.gpixel-asset", 10, new string('a', 64)),
        Preview = new RemoteFile("previews/a.png", 5, new string('b', 64)),
        Published = new DateOnly(2026, 10, 1), Updated = new DateOnly(2026, 10, 28), Review = RemoteReview.Reviewed,
    };

    private static byte[] PageBytes(JsonObject page) => Encoding.UTF8.GetBytes(page.ToJsonString());

    private static JsonObject OnePage(params RemoteCatalogEntry[] entries)
        => RemoteCatalogFormat.Write(new RemoteCatalogPage(1, 1, entries.Length, null, entries));

    // ───────────── index 형식 ─────────────

    [Fact]
    public void Index_RoundTripsEveryField()
    {
        var entry = Entry();
        var page = RemoteCatalogFormat.Read(PageBytes(OnePage(entry)));
        Assert.Equal((1, 1, 1, (string?)null), (page.Page, page.PageCount, page.TotalCount, page.Next));
        var read = Assert.Single(page.Packages);
        Assert.Equal(RemoteCatalogFormat.WriteEntry(entry).ToJsonString(), RemoteCatalogFormat.WriteEntry(read).ToJsonString());
        Assert.Equal(entry.Kinds, read.Kinds);
        Assert.Equal(entry.Preview, read.Preview);
    }

    [Theory]
    [InlineData("schemaVersion", null, "schemaVersion")]
    [InlineData("packages", "[{\"id\":\"x\"}]", "packages[0].id")]
    [InlineData("next", "\"index-2.json\"", "next")]
    public void Index_BadShape_IsRejectedWithField(string key, string? json, string field)
    {
        var page = OnePage(Entry());
        page[key] = json is null ? null : JsonNode.Parse(json);
        var ex = Assert.Throws<RemoteAssetException>(() => RemoteCatalogFormat.Read(PageBytes(page)));
        Assert.Equal((RemoteIssueCode.InvalidIndex, field), (ex.Code, ex.Detail));
    }

    [Theory]
    [InlineData("sha256", "\"ABC\"", "packages[0].package.sha256")]
    [InlineData("size", "0", "packages[0].package.size")]
    public void Index_BadPackageFile_IsRejected(string key, string json, string field)
    {
        var page = OnePage(Entry());
        page["packages"]![0]!["package"]![key] = JsonNode.Parse(json);
        var ex = Assert.Throws<RemoteAssetException>(() => RemoteCatalogFormat.Read(PageBytes(page)));
        Assert.Equal((RemoteIssueCode.InvalidIndex, field), (ex.Code, ex.Detail));
    }

    [Fact]
    public void Index_FutureSchema_IsRejectedWithoutReadingFields()
    {
        var page = new JsonObject { ["schemaVersion"] = RemoteCatalogFormat.SchemaVersion + 1, ["whatever"] = true };
        var ex = Assert.Throws<RemoteAssetException>(() => RemoteCatalogFormat.Read(PageBytes(page)));
        Assert.Equal((RemoteIssueCode.FutureSchema, "2"), (ex.Code, ex.Detail));
    }

    [Fact]
    public void IncompatibleReason_NamesRequiredAndCurrentVersion_UnknownKind_DevBuildSkipsVersion()
    {
        var entry = Entry(minApp: "1.20.0");
        string reason = Assert.IsType<string>(entry.IncompatibleReason(new SemVersion(1, 14, 55)));
        Assert.Contains("1.20.0", reason);
        Assert.Contains("1.14.55", reason);
        Assert.Null(entry.IncompatibleReason(new SemVersion(1, 20, 0)));
        Assert.Null(entry.IncompatibleReason(null));

        var future = Entry(kinds: new Dictionary<string, int> { ["future-kind"] = 1, [AssetKinds.Gradient] = 1 });
        Assert.Contains("future-kind", future.IncompatibleReason(null)!);
    }

    // ───────────── 정적 카탈로그 만들기 ─────────────

    [Fact]
    public void Builder_WritesPagesPackagesPreviews_AndCheckDetectsStaleness()
    {
        using var f = new Fixture();
        byte[] package = Pack();
        f.Submit(package);
        f.Submit(Pack(id: "author.second", name: "Second"), "author.second");

        var build = RemoteCatalogBuilder.Build(f.CatalogSource, f.Output, pageSize: 1);
        Assert.True(build.Ok, string.Join("\n", build.Issues));
        Assert.Equal(
            ["index-2.json", "index.json", "packages/author.neon-pack-1.0.0.gpixel-asset", "packages/author.second-1.0.0.gpixel-asset",
             "previews/author.neon-pack-1.0.0.png", "previews/author.second-1.0.0.png"],
            build.Files.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(package, build.Files["packages/author.neon-pack-1.0.0.gpixel-asset"]);
        var first = RemoteCatalogFormat.Read(build.Files["index.json"]);
        Assert.Equal((1, 2, 2, "index-2.json"), (first.Page, first.PageCount, first.TotalCount, first.Next));
        var entry = Assert.Single(first.Packages);
        Assert.Equal((AssetPackageWriter.Hash(package), (long)package.Length), (entry.Package.Sha256, entry.Package.Size));
        Assert.Equal(new Dictionary<string, int> { [AssetKinds.EffectRecipe] = 1, [AssetKinds.Gradient] = 1 }, entry.Kinds);
        Assert.Equal(("네온 효과 묶음", "reviewed", new DateOnly(2026, 10, 1)), (entry.Summary, entry.Review, entry.Published));

        // 빈 출력 폴더 → 모두 stale, 쓰고 나면 최신, 같은 원본으로 다시 만들어도 같은 바이트.
        Assert.Equal(build.Files.Count, RemoteCatalogBuilder.Stale(build, f.Output).Count);
        RemoteCatalogBuilder.Write(build, f.Output);
        Assert.Empty(RemoteCatalogBuilder.Stale(build, f.Output));
        Assert.Empty(RemoteCatalogBuilder.Stale(RemoteCatalogBuilder.Build(f.CatalogSource, f.Output, pageSize: 1), f.Output));

        // 손으로 고친 파일, 남는 파일, 원본만 바뀐 경우를 모두 잡는다.
        File.AppendAllText(Path.Combine(f.Output, "index.json"), " ");
        File.WriteAllText(Path.Combine(f.Output, "previews", "old.png"), "x");
        Assert.Equal(["index.json", "previews/old.png"], RemoteCatalogBuilder.Stale(build, f.Output));
        RemoteCatalogBuilder.Write(build, f.Output);
        Assert.Empty(RemoteCatalogBuilder.Stale(build, f.Output));
        File.WriteAllText(Path.Combine(f.CatalogSource, "author.second", RemoteCatalogBuilder.ReviewFileName), Review(summary: "바뀐 요약"));
        Assert.Contains("index-2.json", RemoteCatalogBuilder.Stale(RemoteCatalogBuilder.Build(f.CatalogSource, f.Output, pageSize: 1), f.Output));
    }

    private static readonly Dictionary<string, Action<Fixture>> BadSetups = new()
    {
        ["corrupt"] = fx => fx.Submit(AssetSamples.Zip(AssetSamples.Unzip(Pack()).Select(e => e.Name == "LICENSE.txt" ? (e.Name, "tampered"u8.ToArray()) : e))),
        ["folder"] = fx => fx.Submit(Pack(), "author.other"),
        ["personal-license"] = fx => fx.Submit(Pack(license: "LicenseRef-Personal")),
        ["no-license-file"] = fx => fx.Submit(Pack(licenseFile: false)),
        ["personal-author"] = fx => fx.Submit(Pack(id: "personal.neon", author: "personal"), "personal.neon"),
        ["author-id"] = fx => fx.Submit(Pack(author: "someone")),
        ["official"] = fx => fx.Submit(Pack(), review: Review(status: "official")),
        ["review-key"] = fx => fx.Submit(Pack(), review: Review(extra: ", \"downloads\": 5")),
        ["no-review"] = fx => File.Delete(Path.Combine(fx.Submit(Pack()), RemoteCatalogBuilder.ReviewFileName)),
        ["two-packages"] = fx => File.WriteAllBytes(Path.Combine(fx.Submit(Pack()), "b.gpixel-asset"), Pack("1.1.0")),
        ["stray-file"] = fx => File.WriteAllText(Path.Combine(fx.Submit(Pack()), "notes.txt"), "x"),
        ["duplicate-name"] = fx => { fx.Submit(Pack()); fx.Submit(Pack(id: "author.copy"), "author.copy"); },
    };

    public static TheoryData<string, CatalogBuildIssueCode> BadSubmissions => new()
    {
        { "corrupt", CatalogBuildIssueCode.InvalidPackage },
        { "folder", CatalogBuildIssueCode.FolderMismatch },
        { "personal-license", CatalogBuildIssueCode.MissingLicense },
        { "no-license-file", CatalogBuildIssueCode.MissingLicense },
        { "personal-author", CatalogBuildIssueCode.MissingAuthor },
        { "author-id", CatalogBuildIssueCode.AuthorMismatch },
        { "official", CatalogBuildIssueCode.OfficialMismatch },
        { "review-key", CatalogBuildIssueCode.InvalidReview },
        { "no-review", CatalogBuildIssueCode.InvalidReview },
        { "two-packages", CatalogBuildIssueCode.PackageCount },
        { "stray-file", CatalogBuildIssueCode.UnexpectedFile },
        { "duplicate-name", CatalogBuildIssueCode.Duplicate },
    };

    [Theory]
    [MemberData(nameof(BadSubmissions))]
    public void Builder_RejectsBadSubmissions(string label, CatalogBuildIssueCode code)
    {
        using var f = new Fixture();
        BadSetups[label](f);
        var build = RemoteCatalogBuilder.Build(f.CatalogSource, f.Output);
        Assert.False(build.Ok, label);
        Assert.Contains(build.Issues, issue => issue.Code == code);
        Assert.Empty(build.Files);
        Assert.Throws<InvalidOperationException>(() => RemoteCatalogBuilder.Write(build, f.Output));
        Assert.False(Directory.Exists(f.Output));
    }

    [Fact]
    public void Builder_PublishedVersionIsImmutable_AndCannotGoBackwards()
    {
        using var f = new Fixture();
        string dir = f.Submit(Pack("1.1.0"));
        f.Publish();

        // 같은 버전, 다른 내용
        var changed = Pack("1.1.0", name: "Neon Pack Renamed");
        File.WriteAllBytes(Path.Combine(dir, "pack.gpixel-asset"), changed);
        Assert.Contains(RemoteCatalogBuilder.Build(f.CatalogSource, f.Output).Issues, i => i.Code == CatalogBuildIssueCode.PublishedChanged);
        // 낮은 버전
        File.WriteAllBytes(Path.Combine(dir, "pack.gpixel-asset"), Pack("1.0.0"));
        Assert.Contains(RemoteCatalogBuilder.Build(f.CatalogSource, f.Output).Issues, i => i.Code == CatalogBuildIssueCode.VersionDowngrade);
        // 올린 버전은 된다
        File.WriteAllBytes(Path.Combine(dir, "pack.gpixel-asset"), Pack("1.2.0"));
        Assert.True(RemoteCatalogBuilder.Build(f.CatalogSource, f.Output).Ok);
    }

    // ───────────── client: 목록 ─────────────

    [Fact]
    public async Task Fetch_FollowsPages_ResolvesSameSiteHttpsUrls_AndWritesCache()
    {
        using var f = new Fixture();
        f.Submit(Pack());
        f.Submit(Pack(id: "author.second", name: "Second"), "author.second");
        f.Publish(pageSize: 1);

        var snapshot = await f.Client().FetchCatalogAsync();
        Assert.Null(snapshot.Error);
        Assert.False(snapshot.FromCache);
        Assert.Equal(["author.neon-pack", "author.second"], snapshot.Packages.Select(p => p.Id).ToArray());
        Assert.Equal(Site + "packages/author.second-1.0.0.gpixel-asset", snapshot.Packages[1].Package.Url);
        Assert.Equal(Site + "previews/author.neon-pack-1.0.0.png", snapshot.Packages[0].Preview!.Url);
        Assert.Equal([Site + "index.json", Site + "index-2.json"], f.Handler.Calls);

        var cached = f.Client().LoadCache()!;
        Assert.True(cached.FromCache);
        Assert.Equal(snapshot.Packages.Select(RemoteCatalogFormat.WriteEntry).Select(j => j.ToJsonString()),
            cached.Packages.Select(RemoteCatalogFormat.WriteEntry).Select(j => j.ToJsonString()));
    }

    [Fact]
    public async Task Fetch_Offline_ReturnsLastCachedListWithError_OrEmptyWithoutCache()
    {
        using var f = new Fixture();
        f.Submit(Pack());
        f.Publish();
        using var empty = new Fixture(); // 다른 cache 폴더, 빈 handler
        var none = await empty.Client().FetchCatalogAsync();
        Assert.Empty(none.Packages);
        Assert.Equal(RemoteIssueCode.HttpStatus, none.Error!.Code);

        var online = await f.Client().FetchCatalogAsync();
        Assert.Null(online.Error);

        f.Handler.Routes.Clear();
        f.Handler.Routes[IndexUrl.AbsoluteUri] = (_, _) => throw new HttpRequestException("no route to host");
        var offline = await f.Client().FetchCatalogAsync();
        Assert.True(offline.FromCache);
        Assert.Equal(RemoteIssueCode.Network, offline.Error!.Code);
        Assert.Equal(online.Packages.Select(p => p.Id), offline.Packages.Select(p => p.Id));
        Assert.Equal(online.FetchedUtc, offline.FetchedUtc);
        Assert.Equal(3, f.Delays.Count + 1); // 연결 실패는 다시 시도한 뒤 cache로
    }

    [Fact]
    public async Task Fetch_FutureOrForeignIndex_KeepsCacheAndReportsReason()
    {
        using var f = new Fixture();
        f.Submit(Pack());
        f.Publish();
        await f.Client().FetchCatalogAsync();

        f.Handler.Serve(IndexUrl.AbsoluteUri, PageBytes(new JsonObject { ["schemaVersion"] = 99 }));
        var future = await f.Client().FetchCatalogAsync();
        Assert.Equal((RemoteIssueCode.FutureSchema, 1), (future.Error!.Code, future.Packages.Count));

        // 다른 사이트나 HTTP를 가리키는 패키지 주소는 받지 않는다.
        var foreign = Entry() with { Package = new RemoteFile("http://example.test/GPixel/catalog/packages/a.gpixel-asset", 10, new string('a', 64)) };
        f.Handler.Serve(IndexUrl.AbsoluteUri, PageBytes(OnePage(foreign)));
        Assert.Equal(RemoteIssueCode.NotHttps, (await f.Client().FetchCatalogAsync()).Error!.Code);
        var otherHost = Entry() with { Preview = new RemoteFile("https://evil.test/a.png", 5, new string('b', 64)) };
        f.Handler.Serve(IndexUrl.AbsoluteUri, PageBytes(OnePage(otherHost)));
        Assert.Equal(RemoteIssueCode.NotHttps, (await f.Client().FetchCatalogAsync()).Error!.Code);

        Assert.Throws<ArgumentException>(() => new RemoteAssetClient(new HttpClient(f.Handler), new Uri("http://example.test/index.json"), f.Cache, "x"));
    }

    // ───────────── client: 내려받기 ─────────────

    [Fact]
    public async Task Download_VerifiedPackageInstalls_AndUpdateIsFound()
    {
        using var f = new Fixture();
        f.Submit(Pack());
        f.Publish();
        var store = AssetStore.Open(Path.Combine(f.Dir.Path, "Assets"));
        var client = f.Client();
        var entry = (await client.FetchCatalogAsync()).Packages[0];
        var reports = new List<double>();

        string file = await client.DownloadPackageAsync(entry, new SyncProgress(reports.Add));
        Assert.Equal(1.0, reports[^1]);
        store.Install(file, "remote");
        File.Delete(file);
        var installed = Assert.Single(store.Catalog.Packages);
        Assert.Equal((entry.Id, "1.0.0", "remote"), (installed.Id, installed.Version, installed.State.Source));
        Assert.Empty(RemoteAssetClient.FindUpdates(store.Catalog, [entry]));

        File.WriteAllBytes(Directory.GetFiles(Path.Combine(f.CatalogSource, AssetSamples.PackageId), "*.gpixel-asset")[0], Pack("1.1.0"));
        f.Publish();
        var newer = (await client.FetchCatalogAsync()).Packages[0];
        var update = Assert.Single(RemoteAssetClient.FindUpdates(store.Catalog, [newer]));
        Assert.Equal(("1.0.0", "1.1.0"), (update.Installed.Version, update.Remote.Version));
        // 확인만으로는 아무것도 바뀌지 않는다(자동 업데이트 없음).
        Assert.Equal("1.0.0", Assert.Single(store.Catalog.Packages).Version);
    }

    [Fact]
    public async Task Download_HashOrSizeMismatch_IsRefused_NothingWrittenOrInstalled()
    {
        using var f = new Fixture();
        f.Submit(Pack());
        f.Publish();
        var store = AssetStore.Open(Path.Combine(f.Dir.Path, "Assets"));
        var client = f.Client();
        var entry = (await client.FetchCatalogAsync()).Packages[0];

        // 같은 크기, 다른 내용(한 바이트 바꿈)
        byte[] tampered = (byte[])File.ReadAllBytes(Path.Combine(f.Output, "packages", "author.neon-pack-1.0.0.gpixel-asset")).Clone();
        tampered[^10] ^= 0xFF;
        f.Handler.Serve(entry.Package.Url, tampered);
        var ex = await Assert.ThrowsAsync<RemoteAssetException>(() => client.DownloadPackageAsync(entry));
        Assert.Equal(RemoteIssueCode.HashMismatch, ex.Code);

        f.Handler.Serve(entry.Package.Url, tampered[..^1]);
        Assert.Equal(RemoteIssueCode.SizeMismatch, (await Assert.ThrowsAsync<RemoteAssetException>(() => client.DownloadPackageAsync(entry))).Code);

        Assert.False(Directory.Exists(client.DownloadDirectory) && Directory.EnumerateFileSystemEntries(client.DownloadDirectory).Any());
        Assert.Empty(store.Catalog.Packages);
        Assert.False(File.Exists(store.InstalledPath));
    }

    [Fact]
    public async Task Download_RetriesTransientFailuresWithBackoff_ButNotNotFound()
    {
        using var f = new Fixture();
        f.Submit(Pack());
        f.Publish();
        var client = f.Client();
        var entry = (await client.FetchCatalogAsync()).Packages[0];
        byte[] good = File.ReadAllBytes(Path.Combine(f.Output, "packages", "author.neon-pack-1.0.0.gpixel-asset"));
        int attempts = 0;
        f.Handler.Routes[entry.Package.Url] = (_, _) => ++attempts switch
        {
            1 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            2 => throw new HttpRequestException("reset"),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(good) }),
        };
        f.Delays.Clear();

        string file = await client.DownloadPackageAsync(entry);
        Assert.Equal(good, File.ReadAllBytes(file));
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)], f.Delays);

        // 다 실패하면 마지막 까닭으로 끝난다.
        attempts = 0;
        f.Handler.Routes[entry.Package.Url] = (_, _) => { attempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)); };
        var ex = await Assert.ThrowsAsync<RemoteAssetException>(() => client.DownloadPackageAsync(entry));
        Assert.Equal((RemoteIssueCode.HttpStatus, "502", 3), (ex.Code, ex.Detail, attempts));

        attempts = 0;
        f.Handler.Routes[entry.Package.Url] = (_, _) => { attempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); };
        Assert.Equal(RemoteIssueCode.HttpStatus, (await Assert.ThrowsAsync<RemoteAssetException>(() => client.DownloadPackageAsync(entry))).Code);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Download_Cancel_StopsImmediatelyWithoutRetry_TimeoutIsRetried()
    {
        using var f = new Fixture();
        f.Submit(Pack());
        f.Publish();
        var client = f.Client(new RemoteClientOptions { PackageTimeout = TimeSpan.FromMilliseconds(50) });
        var entry = (await client.FetchCatalogAsync()).Packages[0];
        var started = new TaskCompletionSource();
        int attempts = 0;
        f.Handler.Routes[entry.Package.Url] = async (_, token) =>
        {
            Interlocked.Increment(ref attempts);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        };
        f.Delays.Clear();

        var timeout = await Assert.ThrowsAsync<RemoteAssetException>(() => client.DownloadPackageAsync(entry));
        Assert.Equal((RemoteIssueCode.Timeout, 3, 2), (timeout.Code, attempts, f.Delays.Count));

        attempts = 0;
        started = new TaskCompletionSource();
        var slow = f.Client(new RemoteClientOptions { PackageTimeout = TimeSpan.FromMinutes(5) });
        using var cancel = new CancellationTokenSource();
        var download = slow.DownloadPackageAsync(entry, null, cancel.Token);
        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(1, attempts);
        Assert.False(Directory.Exists(slow.DownloadDirectory) && Directory.EnumerateFileSystemEntries(slow.DownloadDirectory).Any());
    }

    [Fact]
    public async Task Download_OverSizeLimit_IsRefusedBeforeRequest()
    {
        using var f = new Fixture();
        var entry = Entry() with { Package = new RemoteFile(Site + "packages/a.gpixel-asset", AssetPackageLimits.Default.MaxPackageBytes + 1, new string('a', 64)) };
        var ex = await Assert.ThrowsAsync<RemoteAssetException>(() => f.Client().DownloadPackageAsync(entry));
        Assert.Equal(RemoteIssueCode.TooLarge, ex.Code);
        Assert.Empty(f.Handler.Calls);
    }

    // ───────────── client: 미리보기 cache ─────────────

    [Fact]
    public async Task Preview_IsVerifiedCachedAndTrimmedBySize()
    {
        using var f = new Fixture();
        f.Submit(Pack());
        f.Submit(Pack(id: "author.second", name: "Second", version: "1.1.0"), "author.second");
        f.Publish();
        var entries = (await f.Client().FetchCatalogAsync()).Packages;
        long one = entries[0].Preview!.Size;
        var client = f.Client(new RemoteClientOptions { PreviewCacheBytes = Math.Max(one, entries[1].Preview!.Size) });

        byte[] first = (await client.GetPreviewAsync(entries[0]))!;
        Assert.Equal(entries[0].Preview!.Sha256, AssetPackageWriter.Hash(first));
        int calls = f.Handler.Calls.Count;
        Assert.Equal(first, await client.GetPreviewAsync(entries[0]));
        Assert.Equal(calls, f.Handler.Calls.Count); // cache에서

        // 두 번째 미리보기를 넣으면 한도를 넘어 오래된 첫 미리보기가 빠진다.
        File.SetLastWriteTimeUtc(Path.Combine(client.PreviewDirectory, entries[0].Preview!.Sha256 + ".png"), DateTime.UtcNow.AddMinutes(-5));
        Assert.NotNull(await client.GetPreviewAsync(entries[1]));
        Assert.Equal([entries[1].Preview!.Sha256 + ".png"], Directory.GetFiles(client.PreviewDirectory).Select(p => Path.GetFileName(p)).ToArray());

        // 내용이 다른 미리보기는 버린다(null, cache에 남지 않음).
        f.Handler.Serve(entries[0].Preview!.Url, new byte[one]);
        Assert.Null(await client.GetPreviewAsync(entries[0]));
        Assert.False(File.Exists(Path.Combine(client.PreviewDirectory, entries[0].Preview!.Sha256 + ".png")));
    }

    /// <summary>바로 부르는 IProgress(Progress&lt;T&gt;는 동기화 문맥으로 미뤄 순서를 볼 수 없음).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
