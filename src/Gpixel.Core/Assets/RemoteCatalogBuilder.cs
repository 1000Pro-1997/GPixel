using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.IO;
using Gpixel.Core.Localization;

namespace Gpixel.Core.Assets;

/// <summary>정적 카탈로그 만들기에서 찾은 문제의 종류(문구는 <c>DomainStrings</c>의 <c>CatalogBuild_{종류}</c>).</summary>
public enum CatalogBuildIssueCode
{
    /// <summary>카탈로그 폴더에 둘 수 없는 파일·폴더.</summary>
    UnexpectedFile,
    /// <summary>제출 폴더에 <c>.gpixel-asset</c>이 정확히 하나가 아님.</summary>
    PackageCount,
    /// <summary>패키지 검사 실패(<see cref="CatalogBuildIssue.Detail"/> = 검사 문제 문구).</summary>
    InvalidPackage,
    /// <summary>폴더 이름이 패키지 ID와 다름.</summary>
    FolderMismatch,
    /// <summary>패키지 ID의 첫 조각이 작성자 ID와 다름(다른 작성자의 ID 자리를 쓰지 못하게).</summary>
    AuthorMismatch,
    /// <summary>공개 작성자 정보가 없음(개인 내보내기의 <c>personal</c> 작성자).</summary>
    MissingAuthor,
    /// <summary>공개 라이선스가 없음(<c>LicenseRef-Personal</c>이거나 LICENSE 파일 없음).</summary>
    MissingLicense,
    /// <summary>review.json이 없거나 값이 틀림(<see cref="CatalogBuildIssue.Detail"/> = 필드).</summary>
    InvalidReview,
    /// <summary>검토 상태 <c>official</c>은 작성자 <c>gpixel</c>만, 작성자 <c>gpixel</c>은 <c>official</c>만.</summary>
    OfficialMismatch,
    /// <summary>다른 제출과 이름이나 패키지 파일이 겹침.</summary>
    Duplicate,
    /// <summary>이미 게시한 같은 버전의 내용이 바뀜.</summary>
    PublishedChanged,
    /// <summary>게시한 버전보다 낮은 버전.</summary>
    VersionDowngrade,
    /// <summary>목록 미리보기가 없음(review.json의 preview도, 미리보기가 있는 에셋도 없음).</summary>
    MissingPreview,
}

/// <summary>카탈로그 만들기 문제 하나: 종류, 카탈로그 원본 안 경로, 자세한 값.</summary>
public sealed record CatalogBuildIssue(CatalogBuildIssueCode Code, string Path, string? Detail = null)
{
    /// <summary>지금 UI 언어의 문구(자리 없이 — 앱 게시 창).</summary>
    public string Text
        => (DomainStrings.GetResourceString("CatalogBuild_" + Code) ?? Code.ToString()).Replace("{detail}", Detail ?? "", StringComparison.Ordinal);

    /// <summary>자리 + 문구(CatalogBuilder 출력).</summary>
    public string Message => string.Format(CultureInfo.InvariantCulture, "{0}: {1}", Path, Text);

    public override string ToString() => Message;
}

/// <summary>만든 정적 카탈로그: 출력 폴더 안 경로(슬래시) → 내용, 만든 항목, 문제(있으면 쓰지 않음).</summary>
public sealed record RemoteCatalogBuild(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<RemoteCatalogEntry> Entries, IReadOnlyList<CatalogBuildIssue> Issues)
{
    public bool Ok => Issues.Count == 0;
}

/// <summary>
/// 저장소의 <c>catalog/</c>(제출 폴더마다 <c>.gpixel-asset</c> 하나와 검토 정보 <c>review.json</c>)에서 홈페이지의 정적 카탈로그
/// <c>docs/catalog/</c>(쪽 index JSON, <c>packages/</c>, <c>previews/</c>)를 만든다(계획 9.1). 같은 원본이면 같은 바이트가 나온다
/// (시각·환경 값을 쓰지 않고, 패키지와 미리보기는 바이트 그대로 복사). CI는 <see cref="Stale"/>로 커밋한 결과가 최신인지 본다.
/// <para>
/// 제출 폴더 <c>catalog/&lt;패키지 ID&gt;/</c>: 패키지는 엄격 검사(<see cref="AssetPackageValidator"/>)와 공개 규칙(<see cref="AssetPublishCheck"/> —
/// 앱 게시 창과 같은 함수: ID 첫 조각 = 작성자 ID, 개인 작성자·개인 라이선스 금지, LICENSE 파일·목록 미리보기 필수, 이미 게시한 같은 버전은 바꿀 수 없고
/// 버전을 내릴 수 없음)을 통과해야 하고, 이름·파일 중복은 금지다.
/// <c>review.json</c> = <c>{ "schemaVersion": 1, "summary": "…", "tags": ["…"], "review": "official|reviewed",
/// "published": "YYYY-MM-DD", "updated": "YYYY-MM-DD", "preview": "previews/…png"(선택 — 없으면 첫 에셋 미리보기) }</c>.
/// </para>
/// </summary>
public static class RemoteCatalogBuilder
{
    public const string ReviewFileName = "review.json";
    public const int ReviewSchemaVersion = 1;
    public const string PackagesFolder = "packages/", PreviewsFolder = "previews/";
    /// <summary>카탈로그 원본 뿌리에 둘 수 있는 안내 문서.</summary>
    public const string SourceReadme = "README.md";
    /// <summary>공식 패키지의 작성자 ID.</summary>
    public const string OfficialAuthor = "gpixel";

    private static readonly HashSet<string> ReviewKeys = ["schemaVersion", "summary", "tags", "review", "published", "updated", "preview"];

    // 운영체제와 무관하게 같은 바이트(줄 끝 LF, 비ASCII는 \u 이스케이프).
    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.Default };
    // review.json은 사람이 읽고 고치므로 한국어 요약을 \u로 이스케이프하지 않는다.
    private static readonly JsonSerializerOptions ReviewJson = new()
    {
        WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    /// <summary>
    /// <paramref name="sourceDirectory"/>(<c>catalog/</c>)를 검사해 카탈로그를 만든다. <paramref name="publishedDirectory"/>에 이전에 만든
    /// 카탈로그가 있으면 이미 게시한 버전과 비교한다(같은 버전의 내용 변경·버전 내림 금지). 디스크에는 쓰지 않는다.
    /// </summary>
    public static RemoteCatalogBuild Build(string sourceDirectory, string? publishedDirectory = null, int pageSize = RemoteCatalogFormat.PageSize)
    {
        var issues = new List<CatalogBuildIssue>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var entries = new List<RemoteCatalogEntry>();
        var published = ReadPublished(publishedDirectory);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(sourceDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal))
            if (file != SourceReadme) issues.Add(new CatalogBuildIssue(CatalogBuildIssueCode.UnexpectedFile, file!));

        foreach (var folder in Directory.GetDirectories(sourceDirectory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(folder);
            int before = issues.Count;
            var submission = ReadSubmission(folder, name, issues);
            if (submission is null || issues.Count > before) continue;
            var (package, packageBytes, review) = submission.Value;
            var manifest = package.Manifest;

            string hash = AssetPackageWriter.Hash(packageBytes);
            if (names.TryGetValue(manifest.Name.Trim(), out var other) || hashes.TryGetValue(hash, out other))
            {
                issues.Add(new CatalogBuildIssue(CatalogBuildIssueCode.Duplicate, name, other));
                continue;
            }
            names[manifest.Name.Trim()] = name;
            hashes[hash] = name;

            if (published.TryGetValue(manifest.Id, out var old) && AssetPublishCheck.PublishedRule(manifest, hash, old.Version, old.Sha256, name) is { } changed)
            {
                issues.Add(changed);
                continue;
            }

            string stem = $"{manifest.Id}-{manifest.Version}";
            string packagePath = PackagesFolder + stem + AssetPackage.Extension;
            files[packagePath] = packageBytes;
            RemoteFile? previewFile = null;
            string? previewSource = AssetPublishCheck.ListPreview(manifest, review.Preview);
            if (previewSource is not null)
            {
                byte[] png = package.Files[previewSource];
                string previewPath = PreviewsFolder + stem + ".png";
                files[previewPath] = png;
                previewFile = new RemoteFile(previewPath, png.Length, AssetPackageWriter.Hash(png));
            }
            entries.Add(new RemoteCatalogEntry
            {
                Id = manifest.Id,
                Version = manifest.Version,
                Name = manifest.Name,
                Summary = review.Summary,
                Tags = review.Tags,
                Author = manifest.Author,
                License = manifest.License,
                Kinds = manifest.Assets.GroupBy(asset => asset.Kind).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                MinAppVersion = manifest.MinAppVersion,
                Package = new RemoteFile(packagePath, packageBytes.Length, hash),
                Preview = previewFile,
                Published = review.Published,
                Updated = review.Updated,
                Review = review.Status,
            });
        }

        if (issues.Count > 0) return new RemoteCatalogBuild(new Dictionary<string, byte[]>(), [], issues);

        var sorted = entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToList();
        int pageCount = Math.Max(1, (sorted.Count + pageSize - 1) / pageSize);
        for (int page = 1; page <= pageCount; page++)
        {
            var slice = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
            var json = RemoteCatalogFormat.Write(new RemoteCatalogPage(page, pageCount, sorted.Count,
                page < pageCount ? RemoteCatalogFormat.PageName(page + 1) : null, slice));
            files[RemoteCatalogFormat.PageName(page)] = Encoding.UTF8.GetBytes(json.ToJsonString(IndexJson) + "\n");
        }
        return new RemoteCatalogBuild(files, sorted, issues);
    }

    private sealed record Review(string Summary, IReadOnlyList<string> Tags, string Status, DateOnly Published, DateOnly Updated, string? Preview);

    /// <summary>제출 폴더 하나를 읽는다. 문제는 <paramref name="issues"/>에 더하고 읽지 못하면 null.</summary>
    private static (AssetPackage Package, byte[] Bytes, Review Review)? ReadSubmission(string folder, string name, List<CatalogBuildIssue> issues)
    {
        foreach (var dir in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
            issues.Add(new CatalogBuildIssue(CatalogBuildIssueCode.UnexpectedFile, name + "/" + Path.GetFileName(dir)));
        var packages = new List<string>();
        foreach (var file in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.EndsWith(AssetPackage.Extension, StringComparison.Ordinal)) packages.Add(file);
            else if (fileName != ReviewFileName) issues.Add(new CatalogBuildIssue(CatalogBuildIssueCode.UnexpectedFile, name + "/" + fileName));
        }
        if (packages.Count != 1)
        {
            issues.Add(new CatalogBuildIssue(CatalogBuildIssueCode.PackageCount, name, packages.Count.ToString(CultureInfo.InvariantCulture)));
            return null;
        }

        byte[] bytes = File.ReadAllBytes(packages[0]);
        AssetPackage package;
        try
        {
            // 엄격 검사(설치와 같은 검사). 최소 앱 버전은 카탈로그가 앱마다 보여 주므로 여기서 보지 않는다.
            using var stream = new MemoryStream(bytes, writable: false);
            package = AssetPackageValidator.Read(stream, new AssetValidationOptions());
        }
        catch (AssetPackageException ex)
        {
            foreach (var issue in ex.Issues) issues.Add(new CatalogBuildIssue(CatalogBuildIssueCode.InvalidPackage, name, issue.Message));
            return null;
        }

        if (package.Manifest.Id != name) issues.Add(new CatalogBuildIssue(CatalogBuildIssueCode.FolderMismatch, name, package.Manifest.Id));
        issues.AddRange(AssetPublishCheck.PackageRules(package, name));

        var review = ReadReview(Path.Combine(folder, ReviewFileName), name, package, issues);
        if (review is null) return null;
        issues.AddRange(AssetPublishCheck.ReviewRules(package, review.Status, review.Preview, name));
        return (package, bytes, review);
    }

    private static Review? ReadReview(string path, string name, AssetPackage package, List<CatalogBuildIssue> issues)
    {
        string where = name + "/" + ReviewFileName;
        void Bad(string field) => issues.Add(new CatalogBuildIssue(CatalogBuildIssueCode.InvalidReview, where, field));
        if (!File.Exists(path))
        {
            Bad(ReviewFileName);
            return null;
        }
        var parseIssues = new List<AssetIssue>();
        if (AssetJson.Parse(File.ReadAllBytes(path), ReviewFileName, 8, 4096, parseIssues) is not JsonObject o)
        {
            Bad(parseIssues.FirstOrDefault()?.Code.ToString() ?? "json");
            return null;
        }
        int before = issues.Count;
        foreach (var key in o.Select(pair => pair.Key).Where(key => !ReviewKeys.Contains(key))) Bad(key);
        if (!AssetJson.TryInt(o["schemaVersion"], out int schema) || schema != ReviewSchemaVersion) Bad("schemaVersion");
        string summary = AssetJson.String(o["summary"]) ?? "";
        if (!RemoteCatalogFormat.IsValidSummary(summary)) Bad("summary");
        var tags = o["tags"] is JsonArray list ? list.Select(AssetJson.String).ToList() : null;
        if (tags is null || !AssetPublishCheck.IsValidTags(tags)) Bad("tags");
        string status = AssetJson.String(o["review"]) ?? "";
        if (!RemoteReview.IsKnown(status)) Bad("review");
        DateOnly Date(string field)
        {
            if (DateOnly.TryParseExact(AssetJson.String(o[field]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
            Bad(field);
            return default;
        }
        var publishedDate = Date("published");
        var updatedDate = Date("updated");
        if (updatedDate < publishedDate) Bad("updated");
        string? preview = AssetJson.String(o["preview"]);
        if (o["preview"] is not null && (preview is null || !preview.StartsWith(AssetPackageValidator.PreviewFolder, StringComparison.Ordinal)
                                                          || !package.Files.ContainsKey(preview)))
            Bad("preview");
        return issues.Count > before ? null : new Review(summary.Trim(), tags!.OfType<string>().ToArray(), status, publishedDate, updatedDate, preview);
    }

    /// <summary>
    /// <c>review.json</c>을 쓴다(키 순서 고정, 줄 끝 LF — 앱 게시 창이 제출 zip에 넣는 검토 정보, 관리자가 검토 뒤 고칠 수 있음).
    /// </summary>
    public static byte[] WriteReview(string summary, IReadOnlyList<string> tags, string status, DateOnly published, DateOnly updated, string? preview)
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = ReviewSchemaVersion,
            ["summary"] = summary,
            ["tags"] = new JsonArray(tags.Select(tag => (JsonNode)JsonValue.Create(tag)!).ToArray()),
            ["review"] = status,
            ["published"] = published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["updated"] = updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (preview is not null) json["preview"] = preview;
        return Encoding.UTF8.GetBytes(json.ToJsonString(ReviewJson) + "\n");
    }

    /// <summary>이전에 만든 카탈로그의 패키지 ID → (버전, 패키지 SHA-256). 없거나 읽지 못하면 빈 표.</summary>
    private static Dictionary<string, (string Version, string Sha256)> ReadPublished(string? directory)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (directory is null) return map;
        string? name = RemoteCatalogFormat.FirstPageName;
        try
        {
            for (int guard = 0; name is not null && guard < RemoteCatalogFormat.MaxPages; guard++)
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path)) break;
                var page = RemoteCatalogFormat.Read(File.ReadAllBytes(path));
                foreach (var entry in page.Packages) map[entry.Id] = (entry.Version, entry.Package.Sha256);
                name = page.Next;
            }
        }
        catch (RemoteAssetException) { }
        return map;
    }

    /// <summary>출력 폴더가 <paramref name="build"/>와 다른 곳(바뀐·없는·남는 파일, 슬래시 경로). 비어 있으면 최신.</summary>
    public static IReadOnlyList<string> Stale(RemoteCatalogBuild build, string outputDirectory)
    {
        var stale = new List<string>();
        var existing = ExistingFiles(outputDirectory);
        foreach (var (path, data) in build.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (!existing.Contains(path) || !File.ReadAllBytes(Combine(outputDirectory, path)).AsSpan().SequenceEqual(data))
                stale.Add(path);
        stale.AddRange(existing.Where(path => !build.Files.ContainsKey(path)).Order(StringComparer.Ordinal));
        return stale;
    }

    /// <summary>출력 폴더를 <paramref name="build"/>와 같게 만든다(바뀐 파일만 쓰고, 남는 파일과 빈 폴더는 지움).</summary>
    public static void Write(RemoteCatalogBuild build, string outputDirectory)
    {
        if (!build.Ok) throw new InvalidOperationException(build.Issues[0].Message);
        foreach (var path in Stale(build, outputDirectory))
        {
            string file = Combine(outputDirectory, path);
            if (build.Files.TryGetValue(path, out var data))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, data);
            }
            else File.Delete(file);
        }
        if (!Directory.Exists(outputDirectory)) return;
        foreach (var dir in Directory.GetDirectories(outputDirectory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    private static HashSet<string> ExistingFiles(string directory)
        => Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static string Combine(string directory, string path) => Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
}
