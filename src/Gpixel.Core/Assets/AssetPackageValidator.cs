using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using Gpixel.Core.IO;

namespace Gpixel.Core.Assets;

/// <summary>패키지 검사 설정: 한도와 지금 앱 버전(null이면 최소 앱 버전을 보지 않음 — 개발 빌드).</summary>
public sealed record AssetValidationOptions(AssetPackageLimits? Limits = null, SemVersion? AppVersion = null)
{
    public AssetPackageLimits EffectiveLimits => Limits ?? AssetPackageLimits.Default;
}

/// <summary>
/// <c>.gpixel-asset</c> 읽기와 검사(계획 7.3의 2~6, 10.1). 파일을 쓰기 전에 모두 끝낸다:
/// 압축 크기 → ZIP 경로(절대 경로, <c>..</c>, 중복, 대소문자 충돌, symlink·특수 파일, 허용 확장자·자리) → 선언된 압축 해제 크기 →
/// 한도를 넘지 않게 끊어 읽으며 실제 크기 → manifest schema와 앱 호환성 → manifest에 없는 파일·없는 파일·SHA-256 →
/// 모든 에셋을 종류별로 실제로 읽고 schema 검사(스크립트 원문은 해석까지 — <see cref="ScriptAssetFormat.Check"/>) → 미리보기 PNG 크기와 디코드.
/// 문제는 모두 모아 <see cref="AssetPackageException"/>으로 던진다(앞 단계가 실패하면 뒤 단계는 보지 않음).
/// </summary>
public static class AssetPackageValidator
{
    /// <summary>패키지 뿌리에 둘 수 있는 문서.</summary>
    public static readonly IReadOnlyList<string> RootDocuments = ["LICENSE.txt", "LICENSE.md", "README.md", "README.txt"];

    public const string PreviewFolder = "previews/";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static AssetPackage Read(string path, AssetValidationOptions? options = null)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, options);
    }

    /// <summary>ZIP 스트림을 검사해 패키지를 돌려준다. 문제가 있으면 <see cref="AssetPackageException"/>.</summary>
    public static AssetPackage Read(Stream stream, AssetValidationOptions? options = null)
    {
        options ??= new AssetValidationOptions();
        var limits = options.EffectiveLimits;
        if (stream.CanSeek && stream.Length > limits.MaxPackageBytes)
            throw new AssetPackageException(new AssetIssue(AssetIssueCode.PackageTooLarge, null, Bytes(limits.MaxPackageBytes)));

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new AssetPackageException(new AssetIssue(AssetIssueCode.NotZip));
        }
        using (zip)
        {
            var files = Extract(zip, limits);
            return Validate(files, options);
        }
    }

    /// <summary>ZIP 항목의 경로·종류·크기를 검사하고, 한도 안에서 내용을 메모리로 읽는다.</summary>
    private static Dictionary<string, byte[]> Extract(ZipArchive zip, AssetPackageLimits limits)
    {
        var issues = new List<AssetIssue>();
        if (zip.Entries.Count > limits.MaxFiles)
            throw new AssetPackageException(new AssetIssue(AssetIssueCode.TooManyFiles, null, limits.MaxFiles.ToString(CultureInfo.InvariantCulture)));

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ZipArchiveEntry>();
        long declared = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            bool directory = name.EndsWith('/') && entry.Length == 0;
            string path = directory ? name[..^1] : name;
            if (!CheckPath(path, limits, issues)) continue;
            // 상위 16비트 = Unix 모드. 일반 파일(0x8000)·폴더(0x4000)가 아니면 symlink 같은 특수 항목이다.
            int mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode != 0 && mode != (directory ? 0x4000 : 0x8000))
            {
                issues.Add(new AssetIssue(AssetIssueCode.SpecialFile, path));
                continue;
            }
            if (directory) continue;
            if (!exact.Add(path))
            {
                issues.Add(new AssetIssue(AssetIssueCode.DuplicatePath, path));
                continue;
            }
            if (!folded.Add(path))
            {
                issues.Add(new AssetIssue(AssetIssueCode.CaseConflict, path));
                continue;
            }
            if (!AllowedFile(path))
            {
                issues.Add(new AssetIssue(AssetIssueCode.DisallowedFile, path));
                continue;
            }
            long max = MaxBytes(path, limits);
            if (entry.Length > max)
            {
                issues.Add(new AssetIssue(AssetIssueCode.FileTooLarge, path, Bytes(max)));
                continue;
            }
            declared += entry.Length;
            entries.Add(entry);
        }
        if (declared > limits.MaxExpandedBytes)
            issues.Add(new AssetIssue(AssetIssueCode.ExpandedTooLarge, null, Bytes(limits.MaxExpandedBytes)));
        if (issues.Count > 0) throw new AssetPackageException(issues);

        // 선언된 크기는 거짓일 수 있으므로 한도 + 1바이트까지만 읽어 실제 크기를 본다.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in entries)
        {
            long max = MaxBytes(entry.FullName, limits);
            byte[] data;
            try
            {
                data = ReadBounded(entry, max + 1);
            }
            catch (InvalidDataException)
            {
                throw new AssetPackageException(new AssetIssue(AssetIssueCode.NotZip, entry.FullName));
            }
            if (data.Length > max)
                throw new AssetPackageException(new AssetIssue(AssetIssueCode.FileTooLarge, entry.FullName, Bytes(max)));
            total += data.Length;
            if (total > limits.MaxExpandedBytes)
                throw new AssetPackageException(new AssetIssue(AssetIssueCode.ExpandedTooLarge, null, Bytes(limits.MaxExpandedBytes)));
            files[entry.FullName] = data;
        }
        return files;
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, long limit)
    {
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < limit && (read = source.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - buffer.Length))) > 0)
            buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    /// <summary>패키지 안 경로 규칙: 슬래시 구분 상대 경로, 빈·<c>.</c>·<c>..</c> 조각 없음, 역슬래시·콜론·제어 문자 없음, Windows 예약 이름 없음.</summary>
    public static bool CheckPath(string path, AssetPackageLimits limits, List<AssetIssue> issues)
    {
        if (path.Length == 0 || path.Length > limits.MaxPathLength || path.Any(char.IsControl) || path.Contains('\\'))
        {
            issues.Add(new AssetIssue(AssetIssueCode.InvalidPath, Shorten(path)));
            return false;
        }
        if (path[0] == '/' || path.Contains(':'))
        {
            issues.Add(new AssetIssue(AssetIssueCode.AbsolutePath, path));
            return false;
        }
        foreach (string segment in path.Split('/'))
        {
            if (segment is "." or "..")
            {
                issues.Add(new AssetIssue(AssetIssueCode.PathEscape, path));
                return false;
            }
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || ReservedNames.Contains(segment.Split('.')[0]))
            {
                issues.Add(new AssetIssue(AssetIssueCode.InvalidPath, path));
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 둘 수 있는 파일: manifest.json, 뿌리 문서, 등록된 종류 폴더의 허용 확장자(스크립트는 <c>assets/scripts/*.js</c>에만), <c>previews/*.png</c>.
    /// 실행 파일·DLL·HTML·SVG는 어느 자리에도 둘 수 없다.
    /// </summary>
    public static bool AllowedFile(string path)
        => path == AssetManifest.FileName || RootDocuments.Contains(path) || IsPreviewPath(path)
           || AssetKinds.All.Any(kind => kind.Accepts(path));

    private static bool IsPreviewPath(string path)
        => path.StartsWith(PreviewFolder, StringComparison.Ordinal) && path.Length > PreviewFolder.Length + 4
                                                                    && path.EndsWith(".png", StringComparison.Ordinal);

    /// <summary>패키지 안 파일 하나의 크기 한도(JSON, 스크립트, 미리보기, 그 밖).</summary>
    internal static long MaxBytes(string path, AssetPackageLimits limits)
        => path.EndsWith(".json", StringComparison.Ordinal) ? limits.MaxJsonBytes
            : path.EndsWith(".js", StringComparison.Ordinal) ? limits.MaxScriptBytes
            : IsPreviewPath(path) ? limits.MaxPreviewBytes
            : limits.MaxFileBytes;

    /// <summary>
    /// 메모리로 읽은 파일 집합(패키지 안 경로 → 바이트, manifest.json 포함)을 검사해 패키지를 만든다.
    /// ZIP 경로 검사는 <see cref="Read(Stream, AssetValidationOptions?)"/>가 이미 했다고 본다.
    /// </summary>
    public static AssetPackage Validate(IReadOnlyDictionary<string, byte[]> files, AssetValidationOptions? options = null)
    {
        options ??= new AssetValidationOptions();
        var limits = options.EffectiveLimits;
        var issues = new List<AssetIssue>();
        if (!files.TryGetValue(AssetManifest.FileName, out var manifestBytes))
            throw new AssetPackageException(new AssetIssue(AssetIssueCode.MissingManifest));
        var node = AssetJson.Parse(manifestBytes, AssetManifest.FileName, limits.MaxJsonDepth, limits.MaxStringLength, issues);
        var manifest = node is null ? null : AssetManifest.Read(node, issues);
        if (manifest is null) throw new AssetPackageException(issues);

        if (options.AppVersion is { } app && SemVersion.TryParse(manifest.MinAppVersion, out var required) && required > app)
            throw new AssetPackageException(new AssetIssue(AssetIssueCode.AppTooOld, null, manifest.MinAppVersion));
        if (manifest.Assets.Count > limits.MaxAssets)
            throw new AssetPackageException(new AssetIssue(AssetIssueCode.TooManyItems, AssetManifest.FileName, "assets"));

        // 파일 목록과 해시: manifest에 없는 파일 금지, 적힌 파일은 모두 있고 SHA-256이 같아야 한다.
        foreach (var path in files.Keys.Where(path => path != AssetManifest.FileName).Order(StringComparer.Ordinal))
            if (!manifest.Files.ContainsKey(path)) issues.Add(new AssetIssue(AssetIssueCode.UnlistedFile, path));
        foreach (var (path, hash) in manifest.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!files.TryGetValue(path, out var data)) issues.Add(new AssetIssue(AssetIssueCode.MissingFile, path));
            else if (AssetPackageWriter.Hash(data) != hash) issues.Add(new AssetIssue(AssetIssueCode.HashMismatch, path));
        }
        if (issues.Count > 0) throw new AssetPackageException(issues);

        var items = new List<AssetPackageItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var previews = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Assets)
        {
            if (!ids.Add(entry.Id))
            {
                issues.Add(new AssetIssue(AssetIssueCode.DuplicateAssetId, AssetManifest.FileName, entry.Id));
                continue;
            }
            if (AssetKinds.Find(entry.Kind) is not { } kind)
            {
                issues.Add(new AssetIssue(AssetIssueCode.UnknownKind, AssetManifest.FileName, entry.Kind));
                continue;
            }
            if (!kind.Accepts(entry.Path) || !paths.Add(entry.Path))
            {
                issues.Add(new AssetIssue(AssetIssueCode.DisallowedFile, entry.Path, entry.Kind));
                continue;
            }
            if (!files.TryGetValue(entry.Path, out var data))
            {
                issues.Add(new AssetIssue(AssetIssueCode.MissingFile, entry.Path));
                continue;
            }
            if (entry.Preview is { } preview)
            {
                if (!IsPreviewPath(preview) || !files.TryGetValue(preview, out var png)) issues.Add(new AssetIssue(AssetIssueCode.InvalidPreview, preview));
                else if (previews.Add(preview)) CheckPreview(preview, png, limits, issues);
            }
            var context = new AssetReadContext(entry.Path, strict: true, limits, issues) { AssetId = entry.Id };
            var value = kind.Read(data, context);
            if (value is null) continue;
            if (value.Id != entry.Id)
            {
                context.Bad("id");
                continue;
            }
            items.Add(new AssetPackageItem(entry, value));
        }
        // 미리보기로 적히지 않은 PNG도 디코드해 손상 여부를 본다(허용된 파일이지만 쓰이지 않는 그림).
        foreach (var path in files.Keys.Where(path => IsPreviewPath(path) && !previews.Contains(path)))
            CheckPreview(path, files[path], limits, issues);
        if (issues.Count > 0) throw new AssetPackageException(issues);

        var content = files.Where(pair => pair.Key != AssetManifest.FileName).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new AssetPackage(manifest, content, items);
    }

    /// <summary>미리보기 PNG: 머리(IHDR)의 폭·높이를 먼저 보고(큰 그림을 풀지 않음), 그다음 실제로 디코드한다.</summary>
    public static void CheckPreview(string path, byte[] png, AssetPackageLimits limits, List<AssetIssue> issues)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(signature) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            issues.Add(new AssetIssue(AssetIssueCode.InvalidPreview, path));
            return;
        }
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width == 0 || height == 0 || width > limits.MaxPreviewSize || height > limits.MaxPreviewSize)
        {
            issues.Add(new AssetIssue(AssetIssueCode.PreviewTooLarge, path, $"{limits.MaxPreviewSize}x{limits.MaxPreviewSize}"));
            return;
        }
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            PngIO.Load(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or ArgumentException
                                       or IndexOutOfRangeException or NotSupportedException or OverflowException or FormatException)
        {
            issues.Add(new AssetIssue(AssetIssueCode.InvalidPreview, path));
        }
    }

    private static string Bytes(long bytes) => bytes.ToString(CultureInfo.InvariantCulture);

    private static string Shorten(string path) => path.Length > 80 ? path[..80] + "…" : path;
}
