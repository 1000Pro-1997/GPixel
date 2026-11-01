using System.Globalization;
using Gpixel.App.Localization;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;

namespace Gpixel.App;

/// <summary>
/// 앱이 쓰는 로컬 에셋 저장소 하나(<c>&lt;설정 폴더&gt;/Gpixel/Assets</c>). 처음 열 때 예전 프리셋 파일을 개인 에셋으로 한 번 옮긴다.
/// 효과 창, 그라데이션 메뉴·프리셋 창, 에셋 관리자, AI 도구가 같은 저장소와 카탈로그를 쓰고, 저장소를 바꾸면 <see cref="Changed"/>로 목록을 다시 만든다.
/// 저장소를 바꾸는 호출은 UI 스레드에서 한다. 온라인 카탈로그 client(<see cref="Remote"/>)는 저장소와 따로이고 cache만 저장소 폴더에 둔다.
/// </summary>
public static class AppAssets
{
    /// <summary>원격에서 설치한 패키지의 출처 값(installed.json).</summary>
    public const string RemoteSource = "remote";

    /// <summary>홈페이지(GitHub Pages)의 정적 카탈로그 첫 쪽.</summary>
    public static readonly Uri CatalogIndexUrl = new(AppInfo.WebsiteUrl + "catalog/" + RemoteCatalogFormat.FirstPageName);

    private static readonly HttpClient RemoteHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static RemoteAssetClient? _remote;
    private static readonly Lock Gate = new();
    private static AssetStore? _store;
    private static string? _notice;

    /// <summary>예전 프리셋 파일이 있던 앱 설정 폴더.</summary>
    public static string SettingsDirectory => Path.Combine(Platform.AppDataDirectory, "Gpixel");

    public static string Root => Path.Combine(SettingsDirectory, "Assets");

    /// <summary>패키지 검사 설정: 개발 빌드(버전 0.0.0)는 최소 앱 버전을 보지 않는다.</summary>
    public static AssetValidationOptions Options => new(AppVersion: AppInfo.IsDevBuild ? null : AppInfo.Version);

    public static AssetStore Store
    {
        get
        {
            if (_store is { } store) return store;
            lock (Gate)
            {
                if (_store is null)
                {
                    var opened = AssetStore.Open(Root, Options);
                    var migration = AssetMigration.Run(opened, SettingsDirectory);
                    var problems = opened.LoadErrors.ToList();
                    if (migration.Error is { } error)
                        problems.Add(string.Format(CultureInfo.CurrentCulture, UiStrings.Assets_MigrationError, error));
                    _notice = problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null;
                    _store = opened;
                }
                return _store;
            }
        }
    }

    public static AssetCatalog Catalog => Store.Catalog;

    /// <summary>온라인 카탈로그 client(요청 시간 한도·재시도는 client 기본값, cache = 저장소의 <c>Cache/</c>).</summary>
    public static RemoteAssetClient Remote
        => _remote ??= new RemoteAssetClient(RemoteHttp, CatalogIndexUrl, Store.CacheDirectory, $"{AppUpdate.UserAgent}/{AppInfo.VersionText}");

    /// <summary>카탈로그가 바뀌었다(설치, 켜기·끄기, 삭제, 개인 에셋 저장).</summary>
    public static event Action? Changed
    {
        add => Store.Changed += value;
        remove => Store.Changed -= value;
    }

    /// <summary>저장소를 열 때 만난 문제(손상된 상태 파일, migration 실패)를 한 번 알린다.</summary>
    public static void ShowProblems(Visual? owner)
    {
        _ = Store;
        if (Interlocked.Exchange(ref _notice, null) is { } notice) Ui.Warn(owner, notice, UiStrings.Assets_Title);
    }

    /// <summary>
    /// 저장소를 바꾸는 동작을 하고, 실패하면 <paramref name="errorFormat"/>({0} = 까닭)으로 알린다. 성공하면 true.
    /// 패키지 검사 실패는 찾은 문제를 줄마다 보인다.
    /// </summary>
    public static bool Try(Visual? owner, Action action, string errorFormat)
    {
        try
        {
            action();
            return true;
        }
        catch (AssetPackageException ex)
        {
            Ui.Error(owner, string.Format(CultureInfo.CurrentCulture, errorFormat, IssueText(ex.Issues)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Ui.Error(owner, string.Format(CultureInfo.CurrentCulture, errorFormat, ex.Message));
        }
        return false;
    }

    /// <summary>문제 목록 글(처음 12개, 넘으면 "외 N개").</summary>
    public static string IssueText(IReadOnlyList<AssetIssue> issues)
    {
        var lines = issues.Take(12).Select(issue => "- " + issue.Message).ToList();
        if (issues.Count > 12) lines.Add(string.Format(CultureInfo.CurrentCulture, UiStrings.Assets_MoreIssues, issues.Count - 12));
        return Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
