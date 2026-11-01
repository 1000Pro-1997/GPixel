using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gpixel.Core.IO;

namespace Gpixel.Core.Assets;

/// <summary>패키지 작성자. <see cref="Id"/>는 영구 ID 조각(소문자·숫자·하이픈), <see cref="Url"/>은 사용자가 눌렀을 때만 여는 HTTPS 주소.</summary>
public sealed record AssetAuthor(string Id, string Name, string? Url = null);

/// <summary>manifest의 에셋 한 줄: 영구 ID(패키지 ID + "." + 조각), 종류, 패키지 안 경로, 미리보기 PNG 경로(없으면 null).</summary>
public sealed record AssetManifestEntry(string Id, string Kind, string Path, string? Preview = null);

/// <summary>
/// <c>.gpixel-asset</c>의 <c>manifest.json</c>(계획 6.2). 표시 이름은 바꿀 수 있지만 <see cref="Id"/>는 영구적이다.
/// 패키지 버전(<see cref="Version"/>, SemVer)과 manifest 구조 버전(<see cref="SchemaVersion"/>)은 따로다.
/// <see cref="Files"/>는 manifest를 뺀 모든 파일의 SHA-256(소문자 16진수)이다.
/// </summary>
public sealed record AssetManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "manifest.json";
    public const int MaxNameLength = 100, MaxDescriptionLength = 2000, MaxLicenseLength = 64;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Id { get; init; } = "";
    /// <summary>SemVer 글자("1.2.0"). 설치 폴더 이름으로도 쓴다.</summary>
    public string Version { get; init; } = "1.0.0";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public AssetAuthor Author { get; init; } = new("", "");
    /// <summary>SPDX 식별자(<c>CC0-1.0</c>, <c>LicenseRef-…</c>).</summary>
    public string License { get; init; } = "";
    /// <summary>설치에 필요한 가장 낮은 앱 버전.</summary>
    public string MinAppVersion { get; init; } = "0.0.0";
    public IReadOnlyList<AssetManifestEntry> Assets { get; init; } = [];
    /// <summary>패키지 안 경로 → SHA-256(소문자 16진수 64자).</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    public SemVersion SemanticVersion => SemVersion.TryParse(Version, out var v) ? v : new SemVersion(0, 0, 0);

    // 패키지 ID: 점으로 나눈 조각 둘 이상, 조각은 소문자·숫자와 가운데 하이픈("author.neon-pack").
    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex SegmentPattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex LicensePattern = new(@"^[A-Za-z0-9][A-Za-z0-9.+-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>패키지 ID 형식인지(최대 100자).</summary>
    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 100 } && IdPattern.IsMatch(id);

    /// <summary>ID 조각 하나(작성자 ID, 에셋 ID의 끝 조각) 형식인지.</summary>
    public static bool IsValidSegment(string? segment) => segment is { Length: > 0 and <= 64 } && SegmentPattern.IsMatch(segment);

    /// <summary>엄격한 SemVer "주.부.수[-프리릴리스]"인지(빌드 메타데이터 없음, 최대 64자).</summary>
    public static bool IsValidVersion(string? version) => version is { Length: > 0 and <= 64 } && VersionPattern.IsMatch(version);

    /// <summary>라이선스 SPDX 식 글자(<c>CC0-1.0</c>, <c>LicenseRef-…</c>, 최대 64자)인지.</summary>
    public static bool IsValidLicense(string? license) => license is { Length: > 0 and <= MaxLicenseLength } && LicensePattern.IsMatch(license);

    /// <summary>표시 이름(이름·작성자 이름)으로 쓸 수 있는지: 앞뒤 공백 뺀 1~100자, 제어 문자 없음.</summary>
    public static bool IsValidName(string? name)
        => name is not null && name.Trim().Length is > 0 and <= MaxNameLength && !name.Any(char.IsControl);

    /// <summary>
    /// manifest JSON을 읽는다. 문제는 <paramref name="issues"/>에 모두 더하고, 하나라도 있으면 null.
    /// 미래 schema는 다른 필드를 보지 않고 <see cref="AssetIssueCode.FutureSchema"/> 하나만 남긴다.
    /// </summary>
    public static AssetManifest? Read(JsonNode? node, List<AssetIssue> issues)
    {
        int before = issues.Count;
        if (node is not JsonObject o)
        {
            issues.Add(new AssetIssue(AssetIssueCode.InvalidJson, FileName));
            return null;
        }
        void Bad(string field) => issues.Add(new AssetIssue(AssetIssueCode.InvalidField, FileName, field));

        if (!AssetJson.TryInt(o["schemaVersion"], out int schema) || schema < 1)
        {
            Bad("schemaVersion");
            return null;
        }
        if (schema > CurrentSchemaVersion)
        {
            issues.Add(new AssetIssue(AssetIssueCode.FutureSchema, FileName, schema.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return null;
        }

        string id = AssetJson.String(o["id"]) ?? "";
        if (!IsValidId(id)) Bad("id");
        string version = AssetJson.String(o["version"]) ?? "";
        if (!IsValidVersion(version)) Bad("version");
        string name = AssetJson.String(o["name"]) ?? "";
        if (!IsValidName(name)) Bad("name");
        string? description = AssetJson.String(o["description"]);
        if (o["description"] is not null && (description is null || description.Length > MaxDescriptionLength)) Bad("description");

        AssetAuthor author = new("", "");
        if (o["author"] is JsonObject a)
        {
            string authorId = AssetJson.String(a["id"]) ?? "";
            string authorName = AssetJson.String(a["name"]) ?? "";
            string? url = AssetJson.String(a["url"]);
            if (!IsValidSegment(authorId)) Bad("author.id");
            if (!IsValidName(authorName)) Bad("author.name");
            if (a["url"] is not null && !IsHttpsUrl(url)) Bad("author.url");
            author = new AssetAuthor(authorId, authorName.Trim(), url);
        }
        else Bad("author");

        string license = AssetJson.String(o["license"]) ?? "";
        if (!IsValidLicense(license)) Bad("license");
        string minApp = AssetJson.String(o["minAppVersion"]) ?? "";
        if (!SemVersion.TryParse(minApp, out _) || minApp.Length > 64) Bad("minAppVersion");

        var assets = new List<AssetManifestEntry>();
        if (o["assets"] is JsonArray list && list.Count > 0)
        {
            for (int i = 0; i < list.Count; i++)
            {
                string field = $"assets[{i}]";
                if (list[i] is not JsonObject entry)
                {
                    Bad(field);
                    continue;
                }
                string assetId = AssetJson.String(entry["id"]) ?? "";
                string kind = AssetJson.String(entry["kind"]) ?? "";
                string path = AssetJson.String(entry["path"]) ?? "";
                string? preview = AssetJson.String(entry["preview"]);
                if (!IsValidAssetId(id, assetId)) Bad(field + ".id");
                if (kind.Length == 0) Bad(field + ".kind");
                if (path.Length == 0) Bad(field + ".path");
                if (entry["preview"] is not null && preview is null) Bad(field + ".preview");
                assets.Add(new AssetManifestEntry(assetId, kind, path, preview));
            }
        }
        else Bad("assets");

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o["files"] is JsonObject map)
        {
            foreach (var (path, value) in map)
            {
                string? hash = AssetJson.String(value);
                if (hash is null || !HashPattern.IsMatch(hash)) Bad($"files[{path}]");
                else files[path] = hash;
            }
        }
        else Bad("files");

        if (issues.Count > before) return null;
        return new AssetManifest
        {
            SchemaVersion = schema, Id = id, Version = version, Name = name.Trim(), Description = description,
            Author = author, License = license, MinAppVersion = minApp, Assets = assets, Files = files,
        };
    }

    /// <summary>에셋 ID: 패키지 ID + "." + 조각 하나.</summary>
    public static bool IsValidAssetId(string packageId, string assetId)
        => assetId.Length <= 164 && assetId.StartsWith(packageId + ".", StringComparison.Ordinal)
                                 && IsValidSegment(assetId[(packageId.Length + 1)..]);

    /// <summary>작성자 웹 주소로 쓸 수 있는 HTTPS 절대 주소(2000자 이하)인지.</summary>
    public static bool IsHttpsUrl(string? url)
        => url is { Length: <= 2000 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>manifest JSON(필드 순서 고정, 지금 schema로만 쓴다).</summary>
    public JsonObject ToJson()
    {
        var author = new JsonObject { ["id"] = Author.Id, ["name"] = Author.Name };
        if (Author.Url is { Length: > 0 } url) author["url"] = url;
        var json = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["id"] = Id,
            ["version"] = Version,
            ["name"] = Name,
        };
        if (Description is { Length: > 0 } description) json["description"] = description;
        json["author"] = author;
        json["license"] = License;
        json["minAppVersion"] = MinAppVersion;
        var assets = new JsonArray();
        foreach (var entry in Assets)
        {
            var item = new JsonObject { ["id"] = entry.Id, ["kind"] = entry.Kind, ["path"] = entry.Path };
            if (entry.Preview is { Length: > 0 } preview) item["preview"] = preview;
            assets.Add(item);
        }
        json["assets"] = assets;
        var files = new JsonObject();
        foreach (var (path, hash) in Files.OrderBy(pair => pair.Key, StringComparer.Ordinal)) files[path] = hash;
        json["files"] = files;
        return json;
    }

    public string Serialize() => ToJson().ToJsonString(AssetJson.Indented);
}

/// <summary>에셋 JSON 공통 도구: 깊이·문자열 길이 검사, 엄격한 값 읽기, 들여 쓴 UTF-8 쓰기.</summary>
public static class AssetJson
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// UTF-8 JSON을 읽는다. 깊이가 <paramref name="maxDepth"/>를 넘거나 문자열(키 포함)이 <paramref name="maxString"/>자를 넘으면
    /// 그 문제를 더하고 null. 앞의 BOM은 버린다. <paramref name="longStrings"/>가 있으면 맨 위 객체의 그 속성 객체 안 값 글(레시피 <c>scripts</c>의
    /// 원문)은 글자 수 대신 UTF-8 <c>MaxBytes</c>로 잰다.
    /// </summary>
    public static JsonNode? Parse(ReadOnlySpan<byte> utf8, string path, int maxDepth, int maxString, List<AssetIssue> issues,
        (string Property, int MaxBytes)? longStrings = null)
    {
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) utf8 = utf8[3..];
        try
        {
            // 한도보다 한 단계 깊은 토큰까지 읽어야 아래에서 깊이 문제로 알린다(그 토큰이 배열·객체면 reader 깊이는 한도 + 2).
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = maxDepth + 2 });
            string? topProperty = null;
            while (reader.Read())
            {
                if (reader.CurrentDepth > maxDepth)
                {
                    issues.Add(new AssetIssue(AssetIssueCode.JsonTooDeep, path, maxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    return null;
                }
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1) topProperty = reader.GetString();
                long raw = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
                // 긴 글 자리: 맨 위 longStrings.Property 객체(깊이 1) 바로 안의 값 글(깊이 2)
                if (reader.TokenType == JsonTokenType.String && reader.CurrentDepth == 2 && longStrings is { } scripts && topProperty == scripts.Property)
                {
                    if (raw > scripts.MaxBytes && System.Text.Encoding.UTF8.GetByteCount(reader.GetString()!) > scripts.MaxBytes)
                    {
                        issues.Add(new AssetIssue(AssetIssueCode.StringTooLong, path, scripts.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        return null;
                    }
                    continue;
                }
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && raw > maxString && reader.GetString()!.Length > maxString)
                {
                    issues.Add(new AssetIssue(AssetIssueCode.StringTooLong, path, maxString.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    return null;
                }
            }
            return JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions { MaxDepth = maxDepth + 1 });
        }
        catch (JsonException)
        {
            // 깊이 한도는 위에서 먼저 잡으므로 여기는 모양이 틀린 JSON이다.
            issues.Add(new AssetIssue(AssetIssueCode.InvalidJson, path));
            return null;
        }
    }

    public static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>JSON 정수(소수점 아래가 0인 수도 받음, 글자는 받지 않음).</summary>
    public static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;
        if (v.TryGetValue(out value)) return true;
        if (v.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue)
        {
            value = (int)d;
            return true;
        }
        return false;
    }

    public static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value) && double.IsFinite(value);
    }

    public static bool TryBool(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False && v.TryGetValue(out value);
    }

    /// <summary>들여 쓴 UTF-8(BOM 없음) 바이트.</summary>
    public static byte[] ToUtf8(JsonNode node) => System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(Indented));
}
