using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Gpixel.Core.IO;

namespace Gpixel.Core.Assets;

/// <summary>원격 client 설정: 요청 시간 한도, 재시도 횟수와 첫 대기(매번 두 배), 미리보기 cache 크기, 패키지 한도, 대기 함수(test가 바꿈).</summary>
public sealed record RemoteClientOptions
{
    public TimeSpan IndexTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PackageTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PreviewTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>요청 하나를 시도하는 최대 횟수(첫 시도 포함).</summary>
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>미리보기 디스크 cache 최대 바이트(넘으면 오래 안 쓴 것부터 지움).</summary>
    public long PreviewCacheBytes { get; init; } = 16 << 20;
    public AssetPackageLimits Limits { get; init; } = AssetPackageLimits.Default;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// 원격 카탈로그 목록 하나: 패키지(모든 쪽을 합친 것), 받은 시각, cache에서 읽었는지, 받지 못한 까닭(받았으면 null).
/// <see cref="Error"/>가 있으면 화면은 설치된 에셋과 이 (이전) 목록을 보이고 연결 문제를 알린다.
/// </summary>
public sealed record RemoteCatalogSnapshot(IReadOnlyList<RemoteCatalogEntry> Packages, DateTime FetchedUtc, bool FromCache, RemoteAssetException? Error)
{
    public static RemoteCatalogSnapshot Empty(RemoteAssetException? error) => new([], default, true, error);

    public RemoteCatalogEntry? Find(string id) => Packages.FirstOrDefault(entry => entry.Id == id);
}

/// <summary>설치된 패키지와 그보다 새 원격 버전.</summary>
public sealed record RemoteUpdate(InstalledPackage Installed, RemoteCatalogEntry Remote);

/// <summary>
/// 정적 원격 카탈로그 client(계획 9·10.2). 저장소(<see cref="AssetStore"/>)와 따로이고 <see cref="HttpClient"/>는 부르는 쪽이 준다(test는 가짜 handler).
/// - 주소는 HTTPS만, 그리고 index와 같은 사이트(scheme + host + port)만 요청한다. 에셋 데이터 안의 주소는 요청하지 않는다.
/// - 요청마다 시간 한도·최대 응답 크기가 있고, 연결 실패·시간 초과·5xx·408·429는 대기를 두 배씩 늘려 다시 시도한다. 취소는 바로 끝낸다.
/// - 목록은 모든 쪽을 받은 뒤에만 쓰고 마지막 성공 목록을 <c>Cache/remote-catalog.json</c>에 둔다. 받지 못하면 cache 목록과 까닭을 돌려준다.
/// - 패키지는 index의 크기·SHA-256과 같을 때만 <c>Cache/downloads/</c>에 파일로 쓴다(설치는 부르는 쪽이 <see cref="AssetStore.Install"/>로).
/// - 미리보기는 SHA-256 이름으로 <c>Cache/previews/</c>에 두고 <see cref="RemoteClientOptions.PreviewCacheBytes"/>를 넘으면 오래된 것부터 지운다.
/// 통계나 식별 정보는 보내지 않는다(User-Agent에 앱 이름·버전만).
/// </summary>
public sealed class RemoteAssetClient
{
    public const string CacheFileName = "remote-catalog.json";
    public const int CacheSchemaVersion = 1;
    private const string PreviewFolder = "previews", DownloadFolder = "downloads";

    private readonly HttpClient _http;
    private readonly RemoteClientOptions _options;
    private readonly string _userAgent;

    /// <param name="indexUrl">첫 index 쪽 주소(HTTPS만 — 아니면 <see cref="ArgumentException"/>).</param>
    /// <param name="cacheDirectory">목록·미리보기·내려받은 파일 cache 폴더(저장소의 <see cref="AssetStore.CacheDirectory"/>).</param>
    /// <param name="userAgent">User-Agent 값(앱 이름/버전).</param>
    public RemoteAssetClient(HttpClient http, Uri indexUrl, string cacheDirectory, string userAgent, RemoteClientOptions? options = null)
    {
        if (!indexUrl.IsAbsoluteUri || indexUrl.Scheme != Uri.UriSchemeHttps) throw new ArgumentException(indexUrl.ToString(), nameof(indexUrl));
        _http = http;
        IndexUrl = indexUrl;
        CacheDirectory = cacheDirectory;
        _userAgent = userAgent;
        _options = options ?? new RemoteClientOptions();
        // 앞 실행이 끊겨 남긴 내려받기 파일을 치운다(설치가 끝나면 부르는 쪽이 지움).
        try
        {
            if (Directory.Exists(DownloadDirectory)) Directory.Delete(DownloadDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public Uri IndexUrl { get; }
    public string CacheDirectory { get; }
    public string CachePath => Path.Combine(CacheDirectory, CacheFileName);
    public string PreviewDirectory => Path.Combine(CacheDirectory, PreviewFolder);
    public string DownloadDirectory => Path.Combine(CacheDirectory, DownloadFolder);

    // ───────────── 목록 ─────────────

    /// <summary>마지막으로 받은 목록(cache). 없거나 손상됐거나 다른 index 주소의 것이면 null.</summary>
    public RemoteCatalogSnapshot? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var issues = new List<AssetIssue>();
            if (AssetJson.Parse(File.ReadAllBytes(CachePath), CacheFileName, 16, 4096, issues) is not JsonObject o) return null;
            if (!AssetJson.TryInt(o["schemaVersion"], out int schema) || schema != CacheSchemaVersion) return null;
            if (AssetJson.String(o["indexUrl"]) != IndexUrl.AbsoluteUri) return null;
            if (!DateTime.TryParse(AssetJson.String(o["fetchedAt"]), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fetched)) return null;
            var entries = new List<RemoteCatalogEntry>();
            foreach (var node in o["packages"] as JsonArray ?? [])
            {
                var entry = RemoteCatalogFormat.ReadEntry(node, "packages");
                // cache 파일도 디스크에서 바뀔 수 있으므로 주소 규칙을 다시 본다.
                Resolve(IndexUrl, entry.Package.Url);
                if (entry.Preview is { } preview) Resolve(IndexUrl, preview.Url);
                entries.Add(entry);
            }
            return new RemoteCatalogSnapshot(entries, fetched, FromCache: true, Error: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RemoteAssetException)
        {
            return null;
        }
    }

    /// <summary>
    /// 모든 index 쪽을 받아 목록을 만들고 cache에 쓴다. 받지 못하면(연결, 시간 초과, 틀린·미래 index) 예외 대신
    /// cache 목록(없으면 빈 목록)과 <see cref="RemoteCatalogSnapshot.Error"/>를 돌려준다. 부르는 쪽의 취소만 예외다.
    /// </summary>
    public async Task<RemoteCatalogSnapshot> FetchCatalogAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await FetchAllPagesAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = new RemoteCatalogSnapshot(entries, DateTime.UtcNow, FromCache: false, Error: null);
            TrySaveCache(snapshot);
            return snapshot;
        }
        catch (RemoteAssetException ex)
        {
            return (LoadCache() ?? RemoteCatalogSnapshot.Empty(null)) with { Error = ex };
        }
    }

    private async Task<List<RemoteCatalogEntry>> FetchAllPagesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<RemoteCatalogEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Uri? url = IndexUrl;
        int expectedPage = 1, pageCount = 0, total = 0;
        while (url is not null)
        {
            byte[] data = await GetAsync(url, RemoteCatalogFormat.MaxPageBytes, null, _options.IndexTimeout, null, cancellationToken).ConfigureAwait(false);
            var page = RemoteCatalogFormat.Read(data);
            // 쪽 번호가 이어지고 쪽 수·전체 수가 첫 쪽과 같아야 한다(돌고 도는 next, 섞인 배포를 막음).
            if (page.Page != expectedPage) throw new RemoteAssetException(RemoteIssueCode.InvalidIndex, "page");
            if (expectedPage == 1) (pageCount, total) = (page.PageCount, page.TotalCount);
            else if (page.PageCount != pageCount || page.TotalCount != total) throw new RemoteAssetException(RemoteIssueCode.InvalidIndex, "pageCount");
            foreach (var entry in page.Packages)
            {
                if (!ids.Add(entry.Id)) throw new RemoteAssetException(RemoteIssueCode.InvalidIndex, entry.Id);
                var preview = entry.Preview is { } p ? p with { Url = Resolve(url, p.Url).AbsoluteUri } : null;
                entries.Add(entry with { Package = entry.Package with { Url = Resolve(url, entry.Package.Url).AbsoluteUri }, Preview = preview });
            }
            url = page.Next is { } next ? Resolve(url, next) : null;
            expectedPage++;
        }
        if (entries.Count != total) throw new RemoteAssetException(RemoteIssueCode.InvalidIndex, "totalCount");
        return entries;
    }

    private void TrySaveCache(RemoteCatalogSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            AtomicFile.Write(CachePath, AssetJson.ToUtf8(new JsonObject
            {
                ["schemaVersion"] = CacheSchemaVersion,
                ["indexUrl"] = IndexUrl.AbsoluteUri,
                ["fetchedAt"] = snapshot.FetchedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["packages"] = new JsonArray(snapshot.Packages.Select(entry => (JsonNode)RemoteCatalogFormat.WriteEntry(entry)).ToArray()),
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 상대 주소를 <paramref name="baseUrl"/> 기준으로 풀고, HTTPS이며 index와 같은 사이트인지 본다.
    /// 아니면 <see cref="RemoteIssueCode.NotHttps"/>.
    /// </summary>
    private Uri Resolve(Uri baseUrl, string url)
    {
        if (!Uri.TryCreate(baseUrl, url, out var resolved) || resolved.Scheme != Uri.UriSchemeHttps
            || !string.Equals(resolved.Authority, IndexUrl.Authority, StringComparison.OrdinalIgnoreCase))
            throw new RemoteAssetException(RemoteIssueCode.NotHttps, url);
        return resolved;
    }

    // ───────────── 패키지 ─────────────

    /// <summary>
    /// 패키지를 내려받아 index의 크기·SHA-256과 같은지 본 뒤 <see cref="DownloadDirectory"/>에 파일로 쓰고 그 경로를 돌려준다.
    /// 다르거나 크기 한도를 넘으면 <see cref="RemoteAssetException"/>이고 아무 파일도 남기지 않는다. <paramref name="progress"/>는 0~1.
    /// 받은 파일을 <see cref="AssetStore.Install"/>에 넘긴 뒤에는 부르는 쪽이 지운다.
    /// </summary>
    public async Task<string> DownloadPackageAsync(RemoteCatalogEntry entry, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        long max = _options.Limits.MaxPackageBytes;
        if (entry.Package.Size > max) throw new RemoteAssetException(RemoteIssueCode.TooLarge, max.ToString(CultureInfo.InvariantCulture));
        var url = Resolve(IndexUrl, entry.Package.Url);
        byte[] data = await GetAsync(url, entry.Package.Size, entry.Package.Size, _options.PackageTimeout, progress, cancellationToken).ConfigureAwait(false);
        if (AssetPackageWriter.Hash(data) != entry.Package.Sha256) throw new RemoteAssetException(RemoteIssueCode.HashMismatch, entry.Id);
        Directory.CreateDirectory(DownloadDirectory);
        string path = Path.Combine(DownloadDirectory, $"{Guid.NewGuid():N}{AssetPackage.Extension}");
        AtomicFile.Write(path, data);
        return path;
    }

    // ───────────── 미리보기 ─────────────

    /// <summary>
    /// 미리보기 PNG 바이트. cache에 있으면 그것(해시를 다시 봄), 없으면 받아서 크기·SHA-256·PNG 머리와 디코드를 본 뒤 cache에 둔다.
    /// 미리보기가 없거나 받지 못하면 null(목록은 미리보기 없이 계속 쓸 수 있다). 부르는 쪽의 취소만 예외다.
    /// </summary>
    public async Task<byte[]?> GetPreviewAsync(RemoteCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.Preview is not { } preview || preview.Size > _options.Limits.MaxPreviewBytes) return null;
        string file = Path.Combine(PreviewDirectory, preview.Sha256 + ".png");
        try
        {
            if (File.Exists(file))
            {
                byte[] cached = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
                if (AssetPackageWriter.Hash(cached) == preview.Sha256)
                {
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                    return cached;
                }
                File.Delete(file);
            }
            var url = Resolve(IndexUrl, preview.Url);
            byte[] data = await GetAsync(url, preview.Size, preview.Size, _options.PreviewTimeout, null, cancellationToken).ConfigureAwait(false);
            if (AssetPackageWriter.Hash(data) != preview.Sha256) return null;
            var issues = new List<AssetIssue>();
            AssetPackageValidator.CheckPreview(preview.Url, data, _options.Limits, issues);
            if (issues.Count > 0) return null;
            Directory.CreateDirectory(PreviewDirectory);
            AtomicFile.Write(file, data);
            TrimPreviews();
            return data;
        }
        catch (Exception ex) when (ex is RemoteAssetException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>미리보기 cache가 한도를 넘으면 가장 오래 안 쓴 파일부터 지운다.</summary>
    private void TrimPreviews()
    {
        var files = new DirectoryInfo(PreviewDirectory).GetFiles("*.png").OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= _options.PreviewCacheBytes) break;
            total -= file.Length;
            file.Delete();
        }
    }

    // ───────────── 업데이트 ─────────────

    /// <summary>설치된 패키지 중 원격 목록에 더 새 버전이 있는 것(설치 출처와 무관 — 파일로 설치했어도 같은 ID면 알림). 자동으로 설치하지 않는다.</summary>
    public static IReadOnlyList<RemoteUpdate> FindUpdates(AssetCatalog catalog, IReadOnlyList<RemoteCatalogEntry> remote)
    {
        var updates = new List<RemoteUpdate>();
        foreach (var package in catalog.Packages)
            if (remote.FirstOrDefault(entry => entry.Id == package.Id) is { } entry
                && SemVersion.TryParse(package.Version, out var installed) && entry.SemanticVersion > installed)
                updates.Add(new RemoteUpdate(package, entry));
        return updates;
    }

    // ───────────── HTTP ─────────────

    /// <summary>
    /// GET 하나를 재시도와 함께 한다: 시도마다 <paramref name="timeout"/>, 응답은 <paramref name="maxBytes"/>까지만 읽고
    /// (<paramref name="expected"/>가 있으면 그 크기와 같아야 함). 다시 시도할 만한 실패만 대기 후 다시 하고, 부르는 쪽 취소는 바로 던진다.
    /// </summary>
    private async Task<byte[]> GetAsync(Uri url, long maxBytes, long? expected, TimeSpan timeout, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        TimeSpan delay = _options.RetryDelay;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await GetOnceAsync(url, maxBytes, expected, timeout, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteAssetException ex) when (ex.Transient && attempt < _options.MaxAttempts)
            {
                progress?.Report(0);
                await _options.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    private async Task<byte[]> GetOnceAsync(Uri url, long maxBytes, long? expected, TimeSpan timeout, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                bool transient = status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                throw new RemoteAssetException(RemoteIssueCode.HttpStatus, status.ToString(CultureInfo.InvariantCulture), transient);
            }
            // 넘겨주기(redirect)를 따라간 끝도 HTTPS·같은 사이트여야 한다.
            if (response.RequestMessage?.RequestUri is { } final) Resolve(IndexUrl, final.AbsoluteUri);
            if (response.Content.Headers.ContentLength is { } length && (length > maxBytes || (expected is { } size && length != size)))
                throw length > maxBytes
                    ? new RemoteAssetException(RemoteIssueCode.TooLarge, maxBytes.ToString(CultureInfo.InvariantCulture))
                    : new RemoteAssetException(RemoteIssueCode.SizeMismatch, url.AbsoluteUri);

            await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            // 한도 + 1바이트까지만 읽어 넘는 응답을 알아챈다(선언된 길이는 거짓일 수 있음).
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxBytes + 1 - buffer.Length)), attempt.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    throw new RemoteAssetException(RemoteIssueCode.TooLarge, maxBytes.ToString(CultureInfo.InvariantCulture));
                if (expected is { } total) progress?.Report((double)buffer.Length / total);
            }
            if (expected is { } want && buffer.Length != want) throw new RemoteAssetException(RemoteIssueCode.SizeMismatch, url.AbsoluteUri);
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteAssetException(RemoteIssueCode.Timeout, null, transient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteAssetException(RemoteIssueCode.Network, ex.Message, transient: true, ex);
        }
        catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 응답 본문을 읽는 도중 끊김.
            throw new RemoteAssetException(RemoteIssueCode.Network, ex.Message, transient: true, ex);
        }
    }
}
