using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gpixel.Core.IO;
using Gpixel.Core.Localization;

namespace Gpixel.Core.Assets;

/// <summary>원격 파일 하나: 주소(앱 안에서는 늘 HTTPS 절대 주소, index 파일에는 page 기준 상대 주소), 바이트 수, SHA-256(소문자 16진수).</summary>
public sealed record RemoteFile(string Url, long Size, string Sha256);

/// <summary>원격 카탈로그의 검토 상태. <see cref="Official"/>은 Gpixel이 만든 패키지(작성자 ID <c>gpixel</c>)만 쓴다.</summary>
public static class RemoteReview
{
    public const string Official = "official";
    public const string Reviewed = "reviewed";

    public static bool IsKnown(string? value) => value is Official or Reviewed;
}

/// <summary>
/// 원격 카탈로그 항목 하나(계획 9.2): 패키지 ID·버전, 이름·짧은 설명·태그, 작성자, 라이선스, 종류별 에셋 수, 최소 앱 버전,
/// 패키지 파일(주소·크기·SHA-256), 미리보기 PNG(없을 수 있음), 게시·수정 날짜, 검토 상태.
/// 패키지 하나는 최신 버전 한 줄만 있다.
/// </summary>
public sealed record RemoteCatalogEntry
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public AssetAuthor Author { get; init; } = new("", "");
    public string License { get; init; } = "";
    /// <summary>에셋 종류(<see cref="AssetKinds"/>의 kind 값) → 개수. 앱이 모르는 종류도 그대로 남는다(호환성 판단에 씀).</summary>
    public IReadOnlyDictionary<string, int> Kinds { get; init; } = new Dictionary<string, int>();
    public string MinAppVersion { get; init; } = "0.0.0";
    public RemoteFile Package { get; init; } = new("", 0, "");
    public RemoteFile? Preview { get; init; }
    public DateOnly Published { get; init; }
    public DateOnly Updated { get; init; }
    /// <summary><see cref="RemoteReview"/> 값.</summary>
    public string Review { get; init; } = RemoteReview.Reviewed;

    public SemVersion SemanticVersion => SemVersion.TryParse(Version, out var v) ? v : new SemVersion(0, 0, 0);

    /// <summary>
    /// 이 앱에 설치할 수 없는 정확한 까닭(지금 UI 언어). 설치할 수 있으면 null.
    /// <paramref name="appVersion"/>이 null이면(개발 빌드) 앱 버전은 보지 않는다 — 패키지 검사(<see cref="AssetValidationOptions"/>)와 같은 기준.
    /// </summary>
    public string? IncompatibleReason(SemVersion? appVersion)
    {
        if (appVersion is not null && SemVersion.TryParse(MinAppVersion, out var required) && required > appVersion)
            return string.Format(CultureInfo.CurrentCulture, DomainStrings.Remote_Incompatible_AppTooOld, MinAppVersion, appVersion);
        var unknown = Kinds.Keys.Where(kind => AssetKinds.Find(kind) is null).Order(StringComparer.Ordinal).ToArray();
        return unknown.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, DomainStrings.Remote_Incompatible_UnknownKind, string.Join(", ", unknown))
            : null;
    }
}

/// <summary>원격 index 한 쪽: 쪽 번호(1부터), 전체 쪽 수, 전체 패키지 수, 다음 쪽 주소(마지막 쪽이면 null), 이 쪽의 패키지.</summary>
public sealed record RemoteCatalogPage(int Page, int PageCount, int TotalCount, string? Next, IReadOnlyList<RemoteCatalogEntry> Packages);

/// <summary>
/// 정적 원격 카탈로그 index 형식(계획 9). 첫 쪽은 <c>catalog/index.json</c>, 다음 쪽은 <c>index-2.json</c> …이고 쪽마다 같은 모양이다:
/// <code>{ "schemaVersion": 1, "page": 1, "pageCount": 1, "totalCount": 2, "next": null,
///   "packages": [ { "id": "gpixel.basic-gradients", "version": "1.0.0", "name": "…", "summary": "…", "tags": ["gradient"],
///     "author": { "id": "gpixel", "name": "Gpixel", "url": "https://…" }, "license": "MIT", "kinds": { "gradient": 8 },
///     "minAppVersion": "1.14.55",
///     "package": { "url": "packages/gpixel.basic-gradients-1.0.0.gpixel-asset", "size": 4321, "sha256": "…" },
///     "preview": { "url": "previews/gpixel.basic-gradients-1.0.0.png", "size": 321, "sha256": "…" },
///     "published": "2026-10-28", "updated": "2026-10-28", "review": "official" } ] }</code>
/// 주소는 그 쪽 주소 기준 상대 주소(앱은 HTTPS·같은 사이트인지 보고 절대 주소로 바꾼다). 미래 schema는 읽지 않는다.
/// </summary>
public static class RemoteCatalogFormat
{
    public const int SchemaVersion = 1;
    public const string FirstPageName = "index.json";
    public const int PageSize = 50;
    /// <summary>쪽 수 한도(이보다 많으면 index가 틀린 것으로 본다).</summary>
    public const int MaxPages = 200;
    /// <summary>index 쪽 하나의 최대 바이트.</summary>
    public const int MaxPageBytes = 1 << 20;
    public const int MaxSummaryLength = 200, MaxTags = 8, MaxTagLength = 32;

    private static readonly Regex TagPattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex LicensePattern = new(@"^[A-Za-z0-9][A-Za-z0-9.+-]*$", RegexOptions.CultureInvariant);

    /// <summary>쪽 번호의 파일 이름(1 = <c>index.json</c>, 2 = <c>index-2.json</c> …).</summary>
    public static string PageName(int page) => page == 1 ? FirstPageName : $"index-{page.ToString(CultureInfo.InvariantCulture)}.json";

    public static bool IsValidTag(string? tag) => tag is { Length: > 0 and <= MaxTagLength } && TagPattern.IsMatch(tag);

    public static bool IsValidSummary(string? summary)
        => summary is not null && summary.Trim().Length is > 0 and <= MaxSummaryLength && !summary.Any(char.IsControl);

    /// <summary>
    /// index 쪽 하나를 읽는다. 크기·JSON 모양·schema·모든 항목 값을 검사하고, 틀리면 <see cref="RemoteAssetException"/>
    /// (<see cref="RemoteIssueCode.InvalidIndex"/> — 자세한 값은 틀린 필드, 미래 schema는 <see cref="RemoteIssueCode.FutureSchema"/>).
    /// </summary>
    public static RemoteCatalogPage Read(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxPageBytes) throw new RemoteAssetException(RemoteIssueCode.TooLarge, MaxPageBytes.ToString(CultureInfo.InvariantCulture));
        var issues = new List<AssetIssue>();
        if (AssetJson.Parse(utf8, FirstPageName, 16, 4096, issues) is not JsonObject o)
            throw new RemoteAssetException(RemoteIssueCode.InvalidIndex, issues.FirstOrDefault()?.Code.ToString() ?? "json");
        if (!AssetJson.TryInt(o["schemaVersion"], out int schema) || schema < 1) throw Bad("schemaVersion");
        if (schema > SchemaVersion) throw new RemoteAssetException(RemoteIssueCode.FutureSchema, schema.ToString(CultureInfo.InvariantCulture));
        if (!AssetJson.TryInt(o["page"], out int page) || page < 1 || page > MaxPages) throw Bad("page");
        if (!AssetJson.TryInt(o["pageCount"], out int pageCount) || pageCount < page || pageCount > MaxPages) throw Bad("pageCount");
        if (!AssetJson.TryInt(o["totalCount"], out int total) || total < 0 || total > MaxPages * PageSize) throw Bad("totalCount");
        string? next = AssetJson.String(o["next"]);
        if (o["next"] is not null && next is null) throw Bad("next");
        if ((next is null) != (page == pageCount)) throw Bad("next");
        if (o["packages"] is not JsonArray list || list.Count > PageSize) throw Bad("packages");
        var entries = new List<RemoteCatalogEntry>(list.Count);
        for (int i = 0; i < list.Count; i++) entries.Add(ReadEntry(list[i], $"packages[{i}]"));
        return new RemoteCatalogPage(page, pageCount, total, next, entries);
    }

    /// <summary>항목 하나를 읽고 검사한다(index 쪽과 앱의 cache 파일이 같이 쓴다). 틀리면 <see cref="RemoteIssueCode.InvalidIndex"/>.</summary>
    public static RemoteCatalogEntry ReadEntry(JsonNode? node, string field)
    {
        if (node is not JsonObject o) throw Bad(field);
        string id = AssetJson.String(o["id"]) ?? "";
        if (!AssetManifest.IsValidId(id)) throw Bad(field + ".id");
        string version = AssetJson.String(o["version"]) ?? "";
        if (!AssetManifest.IsValidVersion(version)) throw Bad(field + ".version");
        string name = AssetJson.String(o["name"]) ?? "";
        if (!AssetManifest.IsValidName(name)) throw Bad(field + ".name");
        string summary = AssetJson.String(o["summary"]) ?? "";
        if (!IsValidSummary(summary)) throw Bad(field + ".summary");

        var tags = new List<string>();
        if (o["tags"] is not JsonArray tagList || tagList.Count > MaxTags) throw Bad(field + ".tags");
        foreach (var item in tagList)
        {
            string? tag = AssetJson.String(item);
            if (!IsValidTag(tag) || tags.Contains(tag!)) throw Bad(field + ".tags");
            tags.Add(tag!);
        }

        if (o["author"] is not JsonObject a) throw Bad(field + ".author");
        string authorId = AssetJson.String(a["id"]) ?? "", authorName = AssetJson.String(a["name"]) ?? "";
        string? authorUrl = AssetJson.String(a["url"]);
        if (!AssetManifest.IsValidSegment(authorId) || !AssetManifest.IsValidName(authorName)) throw Bad(field + ".author");
        if (a["url"] is not null && !IsHttps(authorUrl)) throw Bad(field + ".author.url");

        string license = AssetJson.String(o["license"]) ?? "";
        if (license.Length is 0 or > AssetManifest.MaxLicenseLength || !LicensePattern.IsMatch(license)) throw Bad(field + ".license");

        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        if (o["kinds"] is not JsonObject kindMap || kindMap.Count == 0) throw Bad(field + ".kinds");
        foreach (var (kind, value) in kindMap)
        {
            if (!AssetManifest.IsValidSegment(kind) || !AssetJson.TryInt(value, out int count) || count < 1) throw Bad(field + ".kinds");
            kinds[kind] = count;
        }

        string minApp = AssetJson.String(o["minAppVersion"]) ?? "";
        if (minApp.Length > 64 || !SemVersion.TryParse(minApp, out _)) throw Bad(field + ".minAppVersion");
        var package = ReadFile(o["package"], field + ".package") ?? throw Bad(field + ".package");
        var preview = o["preview"] is null ? null : ReadFile(o["preview"], field + ".preview") ?? throw Bad(field + ".preview");
        var published = ReadDate(o["published"], field + ".published");
        var updated = ReadDate(o["updated"], field + ".updated");
        if (updated < published) throw Bad(field + ".updated");
        string review = AssetJson.String(o["review"]) ?? "";
        if (!RemoteReview.IsKnown(review)) throw Bad(field + ".review");

        return new RemoteCatalogEntry
        {
            Id = id, Version = version, Name = name.Trim(), Summary = summary.Trim(), Tags = tags,
            Author = new AssetAuthor(authorId, authorName.Trim(), authorUrl), License = license, Kinds = kinds, MinAppVersion = minApp,
            Package = package, Preview = preview, Published = published, Updated = updated, Review = review,
        };
    }

    private static RemoteFile? ReadFile(JsonNode? node, string field)
    {
        if (node is not JsonObject o) return null;
        string url = AssetJson.String(o["url"]) ?? "";
        if (url.Length is 0 or > 2000 || url.Any(char.IsControl)) throw Bad(field + ".url");
        if (!AssetJson.TryDouble(o["size"], out double size) || size < 1 || size > int.MaxValue || size != Math.Floor(size)) throw Bad(field + ".size");
        string hash = AssetJson.String(o["sha256"]) ?? "";
        if (!HashPattern.IsMatch(hash)) throw Bad(field + ".sha256");
        return new RemoteFile(url, (long)size, hash);
    }

    private static DateOnly ReadDate(JsonNode? node, string field)
        => DateOnly.TryParseExact(AssetJson.String(node), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw Bad(field);

    private static bool IsHttps(string? url)
        => url is { Length: <= 2000 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static RemoteAssetException Bad(string field) => new(RemoteIssueCode.InvalidIndex, field);

    /// <summary>index 쪽 JSON(필드 순서 고정, 지금 schema로만 쓴다).</summary>
    public static JsonObject Write(RemoteCatalogPage page) => new()
    {
        ["schemaVersion"] = SchemaVersion,
        ["page"] = page.Page,
        ["pageCount"] = page.PageCount,
        ["totalCount"] = page.TotalCount,
        ["next"] = page.Next,
        ["packages"] = new JsonArray(page.Packages.Select(entry => (JsonNode)WriteEntry(entry)).ToArray()),
    };

    public static JsonObject WriteEntry(RemoteCatalogEntry entry)
    {
        var author = new JsonObject { ["id"] = entry.Author.Id, ["name"] = entry.Author.Name };
        if (entry.Author.Url is { Length: > 0 } url) author["url"] = url;
        var kinds = new JsonObject();
        foreach (var (kind, count) in entry.Kinds.OrderBy(pair => pair.Key, StringComparer.Ordinal)) kinds[kind] = count;
        var json = new JsonObject
        {
            ["id"] = entry.Id,
            ["version"] = entry.Version,
            ["name"] = entry.Name,
            ["summary"] = entry.Summary,
            ["tags"] = new JsonArray(entry.Tags.Select(tag => (JsonNode)JsonValue.Create(tag)!).ToArray()),
            ["author"] = author,
            ["license"] = entry.License,
            ["kinds"] = kinds,
            ["minAppVersion"] = entry.MinAppVersion,
            ["package"] = WriteFile(entry.Package),
        };
        if (entry.Preview is { } preview) json["preview"] = WriteFile(preview);
        json["published"] = entry.Published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        json["updated"] = entry.Updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        json["review"] = entry.Review;
        return json;
    }

    private static JsonObject WriteFile(RemoteFile file) => new() { ["url"] = file.Url, ["size"] = file.Size, ["sha256"] = file.Sha256 };
}

/// <summary>원격 카탈로그·내려받기 문제의 종류(문구는 <c>DomainStrings</c>의 <c>Remote_Issue_{종류}</c>).</summary>
public enum RemoteIssueCode
{
    /// <summary>연결 실패(DNS, 연결 끊김 …). 다시 시도할 수 있다.</summary>
    Network,
    /// <summary>요청 시간 초과. 다시 시도할 수 있다.</summary>
    Timeout,
    /// <summary>성공이 아닌 HTTP 응답(<see cref="RemoteAssetException.Detail"/> = 상태 번호).</summary>
    HttpStatus,
    /// <summary>index 모양이 틀림(<see cref="RemoteAssetException.Detail"/> = 필드).</summary>
    InvalidIndex,
    /// <summary>index가 이 앱보다 새 schema.</summary>
    FutureSchema,
    /// <summary>HTTPS가 아니거나 index와 다른 사이트의 주소.</summary>
    NotHttps,
    /// <summary>응답이 크기 한도를 넘음.</summary>
    TooLarge,
    /// <summary>받은 파일 크기가 index와 다름.</summary>
    SizeMismatch,
    /// <summary>받은 파일 SHA-256이 index와 다름.</summary>
    HashMismatch,
}

/// <summary>원격 카탈로그 읽기·내려받기 실패. <see cref="Transient"/>면 다시 시도할 만한 문제(연결, 시간 초과, 5xx·408·429).</summary>
public sealed class RemoteAssetException(RemoteIssueCode code, string? detail = null, bool transient = false, Exception? inner = null)
    : Exception(MessageFor(code, detail), inner)
{
    public RemoteIssueCode Code { get; } = code;
    public string? Detail { get; } = detail;
    public bool Transient { get; } = transient;

    private static string MessageFor(RemoteIssueCode code, string? detail)
        => (DomainStrings.GetResourceString("Remote_Issue_" + code) ?? code.ToString()).Replace("{detail}", detail ?? "", StringComparison.Ordinal);
}
