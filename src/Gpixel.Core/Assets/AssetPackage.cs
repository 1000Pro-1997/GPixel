using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Gpixel.Core.IO;

namespace Gpixel.Core.Assets;

/// <summary>검사를 통과한 패키지 에셋 하나: manifest 줄과 읽은 값.</summary>
public sealed record AssetPackageItem(AssetManifestEntry Entry, IAssetData Data);

/// <summary>
/// 검사를 통과한 <c>.gpixel-asset</c>(계획 6.1): manifest, manifest를 뺀 파일 내용(패키지 안 경로 → 바이트), 읽은 에셋.
/// 만들기는 <see cref="AssetPackageValidator"/>(읽기)만 하므로 이 객체가 있으면 모든 검사가 끝난 것이다.
/// </summary>
public sealed class AssetPackage
{
    public const string Extension = ".gpixel-asset";

    internal AssetPackage(AssetManifest manifest, IReadOnlyDictionary<string, byte[]> files, IReadOnlyList<AssetPackageItem> items)
    {
        Manifest = manifest;
        Files = files;
        Items = items;
    }

    public AssetManifest Manifest { get; }

    /// <summary>manifest를 뺀 파일(경로 순서 무관).</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>manifest 순서의 에셋.</summary>
    public IReadOnlyList<AssetPackageItem> Items { get; }

    /// <summary>같은 패키지 파일 집합인지(manifest의 경로·해시가 모두 같음).</summary>
    public bool SameContent(AssetManifest other)
        => Manifest.Files.Count == other.Files.Count
           && Manifest.Files.All(pair => other.Files.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
}

/// <summary>패키지 ZIP 쓰기: 파일 SHA-256을 계산해 manifest의 <c>files</c>를 채우고 manifest.json을 맨 앞에 둔다.</summary>
public static class AssetPackageWriter
{
    // 같은 내용이면 같은 바이트가 나오도록 ZIP 항목 시각을 고정한다.
    private static readonly DateTimeOffset FixedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>SHA-256 소문자 16진수.</summary>
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// <paramref name="manifest"/>(files는 무시하고 다시 계산)와 <paramref name="files"/>로 패키지를 쓴다.
    /// 돌려주는 manifest는 실제로 쓴 것(files 포함)이다.
    /// </summary>
    public static AssetManifest Write(Stream output, AssetManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        var hashes = files.ToDictionary(pair => pair.Key, pair => Hash(pair.Value), StringComparer.Ordinal);
        var written = manifest with { SchemaVersion = AssetManifest.CurrentSchemaVersion, Files = hashes };
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        Add(zip, AssetManifest.FileName, Encoding.UTF8.GetBytes(written.Serialize()));
        foreach (var (path, data) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Add(zip, path, data);
        return written;
    }

    private static void Add(ZipArchive zip, string path, byte[] data)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTime;
        entry.ExternalAttributes = 0x81A4 << 16; // 일반 파일 rw-r--r--
        using var stream = entry.Open();
        stream.Write(data);
    }
}

/// <summary>
/// 효과 레시피·그라데이션 값으로 새 패키지를 만든다(내보내기, 5단계 게시). 에셋 ID는 "패키지 ID.이름 조각"으로 새로 붙이고,
/// 파일은 종류 폴더(<see cref="AssetKindDefinition.Folder"/>)와 <c>previews/</c>에 둔다.
/// </summary>
public sealed class AssetPackageBuilder(AssetManifest header)
{
    private readonly List<AssetManifestEntry> _entries = [];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _segments = new(StringComparer.Ordinal);

    /// <summary>패키지 정보(ID, 버전, 이름, 작성자, 라이선스, 최소 앱 버전). assets와 files는 무시한다.</summary>
    public AssetManifest Header { get; } = header;

    public int Count => _entries.Count;

    /// <summary>에셋 하나를 더하고 새 에셋 ID를 돌려준다. <paramref name="previewPng"/>가 있으면 미리보기로 넣는다.</summary>
    public string Add(string kind, IAssetData value, byte[]? previewPng = null)
    {
        var definition = AssetKinds.Find(kind) ?? throw new ArgumentException(kind, nameof(kind));
        string segment = UniqueSegment(Slug(value.Name, kind));
        string id = Header.Id + "." + segment;
        string path = definition.Folder + segment + definition.Extensions[0];
        _files[path] = definition.Write(value, id).ToArray();
        string? preview = null;
        if (previewPng is { Length: > 0 })
        {
            preview = "previews/" + segment + ".png";
            _files[preview] = previewPng;
        }
        _entries.Add(new AssetManifestEntry(id, kind, path, preview));
        return id;
    }

    public string Add(EffectRecipe recipe, byte[]? previewPng = null) => Add(AssetKinds.EffectRecipe, recipe, previewPng);

    public string Add(GradientPreset gradient, byte[]? previewPng = null) => Add(AssetKinds.Gradient, new GradientAsset("", gradient), previewPng);

    /// <summary>
    /// 에셋에 매이지 않은 미리보기 PNG(게시할 때 고른 목록 미리보기)를 <c>previews/</c>에 넣고 패키지 안 경로를 돌려준다.
    /// 이름 조각은 에셋 ID 조각과 겹치지 않게 고른다.
    /// </summary>
    public string AddPreview(string name, byte[] png)
    {
        string path = AssetPackageValidator.PreviewFolder + UniqueSegment(Slug(name, "preview")) + ".png";
        _files[path] = png;
        return path;
    }

    /// <summary>뿌리 문서(<c>LICENSE.txt</c>, <c>README.md</c>).</summary>
    public void AddDocument(string name, string text) => _files[name] = Encoding.UTF8.GetBytes(text);

    public AssetManifest Write(Stream output)
        => AssetPackageWriter.Write(output, Header with { Assets = _entries.ToArray() }, _files);

    private string UniqueSegment(string slug)
    {
        string segment = slug;
        for (int n = 2; !_segments.Add(segment); n++) segment = $"{slug}-{n}";
        return segment;
    }

    /// <summary>
    /// 이름에서 ID 조각을 만든다: ASCII 글자·숫자는 소문자로, 나머지는 하이픈 하나로. 남는 것이 없으면(한국어 이름 등) 종류 이름.
    /// 번역 표시 이름을 ID로 쓰지 않도록 한 번 정한 ID는 이름을 바꿔도 그대로 둔다.
    /// </summary>
    public static string Slug(string name, string fallback)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 48) break;
        }
        string slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : fallback;
    }
}
