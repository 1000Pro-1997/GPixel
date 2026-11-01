using System.Globalization;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Gpixel.App.Localization;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;

namespace Gpixel.App;

/// <summary>
/// 에셋 관리자의 둘러보기·업데이트 탭(계획 4단계): 온라인 카탈로그(<see cref="AppAssets.Remote"/>)를 처음 그 탭을 열 때 받는다.
/// 받기 전에는 마지막으로 받은 목록(cache)을 먼저 보이고, 받지 못하면 그 목록과 까닭을 보인다(설치한 에셋은 그대로 쓸 수 있음).
/// 검색·종류·태그 거르기는 메모리 목록에서 하고, 미리보기는 목록/상세가 화면에 붙을 때만 받아 같은 작업을 공유한다. 설치·업데이트는 사용자가 누를 때만 하고,
/// 내려받는 동안 그 자리에 진행 막대와 취소 단추를 둔다(SHA-256이 맞을 때만 저장소에 설치).
/// </summary>
public sealed partial class AssetManagerWindow
{
    private readonly ListBox _remoteList = NewList();
    private readonly StackPanel _remoteDetail = new() { Spacing = 6 };
    private readonly TabStrip _remoteFilter = new(TabStripKind.Segmented, UiStrings.Assets_FilterEffects, UiStrings.Assets_FilterGradients);
    private readonly TextBox _remoteSearch = new() { Width = 180, Watermark = UiStrings.Assets_Search };
    private readonly ComboBox _tagFilter = new() { MinWidth = 130 };
    private readonly TextBlock _remoteStatus = StatusText(), _updatesStatus = StatusText();
    private readonly StackPanel _updatesList = new() { Spacing = 8 };
    private readonly CancellationTokenSource _closing = new();
    /// <summary>내려받는 동안 보이는 진행 막대들(상세와 업데이트 탭에 하나씩 있을 수 있음).</summary>
    private readonly List<(ProgressBar Bar, TextBlock Text)> _downloadViews = [];
    /// <summary>동시에 보이는 목록 타일과 상세가 같은 PNG를 중복 요청하지 않도록 창에서 공유한다.</summary>
    private readonly Dictionary<string, Task<byte[]?>> _previewTasks = [];
    private TabItem? _updatesTab;
    private RemoteCatalogSnapshot _snapshot = RemoteCatalogSnapshot.Empty(null);
    private bool _remoteStarted, _remoteLoading, _remoteRefreshing;
    private string? _remoteSelectId;
    private CancellationTokenSource? _download;
    private string? _downloadId;
    private double _downloadFraction;

    private static ListBox NewList()
    {
        var list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            Background = AppTheme.InputBrush,
            Foreground = AppTheme.TextBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }

    private static TextBlock StatusText() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = AppTheme.DimTextBrush,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static SemVersion? AppVersion => AppAssets.Options.AppVersion;

    private Control BrowseTab(int kindIndex)
    {
        _remoteFilter.SelectedIndex = kindIndex;
        _remoteList.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });
        _remoteFilter.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(RefreshRemote);
        _remoteSearch.TextChanged += (_, _) => RefreshRemote();
        _tagFilter.SelectionChanged += (_, _) =>
        {
            if (!_remoteRefreshing) RefreshRemote();
        };
        var refresh = Ui.Button(VectorIcon.Refresh, UiStrings.Assets_Refresh, (_, _) => LoadRemote());
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
        toolbar.Children.AddRange([_remoteFilter, _remoteSearch, _tagFilter, refresh]);
        foreach (var child in toolbar.Children) child.Margin = new Thickness(0, 0, 8, 0);

        _remoteList.SelectionChanged += (_, _) =>
        {
            if (_remoteRefreshing) return;
            _remoteSelectId = SelectedRemote?.Id;
            ShowRemoteDetail();
        };
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), Children = { _remoteList, DetailScroller(_remoteDetail) } };
        var panel = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_remoteStatus, Dock.Top);
        panel.Children.AddRange([toolbar, _remoteStatus, split]);
        return panel;
    }

    private Control UpdatesTab()
    {
        var check = Ui.Button(VectorIcon.Refresh, UiStrings.Assets_CheckUpdates, (_, _) => LoadRemote());
        var note = new TextBlock { Text = UiStrings.Assets_UpdatesNote, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.DimTextBrush, Margin = new Thickness(0, 0, 0, 6) };
        var top = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children = { check, note, _updatesStatus } };
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Margin = new Thickness(0, 0, 0, 8);
        var scroll = new ScrollViewer { Content = _updatesList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.AddRange([top, scroll]);
        return panel;
    }

    private static ScrollViewer DetailScroller(Control content)
    {
        var right = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(right, 1);
        return right;
    }

    // ───────────── 목록 받기 ─────────────

    /// <summary>
    /// 원격 목록을 받는다. 처음이면 cache 목록을 먼저 보인다. 받지 못하면 <see cref="RemoteCatalogSnapshot.Error"/>와 함께 cache 목록이 남는다.
    /// 창을 닫으면 요청을 취소한다.
    /// </summary>
    private async void LoadRemote()
    {
        if (_remoteLoading) return;
        _remoteLoading = true;
        foreach (var key in _previewTasks.Where(pair => pair.Value.IsCompletedSuccessfully && pair.Value.Result is null)
                     .Select(pair => pair.Key).ToArray())
            _previewTasks.Remove(key);
        if (!_remoteStarted)
        {
            _remoteStarted = true;
            if (AppAssets.Remote.LoadCache() is { } cached) _snapshot = cached;
        }
        RefreshRemoteViews();
        try
        {
            _snapshot = await AppAssets.Remote.FetchCatalogAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _remoteLoading = false;
        }
        RefreshRemoteViews();
    }

    private void RefreshRemoteViews()
    {
        if (_closing.IsCancellationRequested) return;
        RefreshRemote();
        RefreshUpdates();
    }

    /// <summary>목록 상태 글: 받는 중, 받은 시각, 또는 연결 실패와 보이는 목록이 언제 것인지.</summary>
    private void UpdateStatus(TextBlock status)
    {
        status.Foreground = AppTheme.DimTextBrush;
        if (_remoteLoading) status.Text = UiStrings.Assets_RemoteLoading;
        else if (_snapshot.Error is { } error)
        {
            status.Foreground = AppTheme.WarningBrush;
            status.Text = _snapshot.Packages.Count > 0
                ? F(UiStrings.Assets_RemoteOffline, FetchedText(), error.Message)
                : F(UiStrings.Assets_RemoteOfflineEmpty, error.Message);
        }
        else status.Text = _remoteStarted ? F(UiStrings.Assets_RemoteStatus, _snapshot.Packages.Count, FetchedText()) : "";
    }

    private string FetchedText() => _snapshot.FetchedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    // ───────────── 둘러보기 ─────────────

    private RemoteCatalogEntry? SelectedRemote => (_remoteList.SelectedItem as ListBoxItem)?.Tag as RemoteCatalogEntry;

    private string[] RemoteKindFilter => FilterKinds(_remoteFilter.SelectedIndex);

    private string? TagFilter => _tagFilter.SelectedIndex > 0 ? _tagFilter.SelectedItem as string : null;

    private bool RemoteMatches(RemoteCatalogEntry entry)
    {
        if (!RemoteKindFilter.Any(entry.Kinds.ContainsKey)) return false;
        if (TagFilter is { } tag && !entry.Tags.Contains(tag)) return false;
        string query = _remoteSearch.Text?.Trim() ?? "";
        return query.Length == 0
               || entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || entry.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || entry.Author.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
               || entry.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshRemote()
    {
        _remoteRefreshing = true;
        try
        {
            // 태그 칸: 받은 목록의 태그(고른 태그는 남긴다).
            string? tag = TagFilter;
            var tags = _snapshot.Packages.SelectMany(e => e.Tags).Distinct().Order(StringComparer.Ordinal).ToList();
            _tagFilter.ItemsSource = new[] { UiStrings.Assets_AllTags }.Concat(tags).ToArray();
            _tagFilter.SelectedIndex = tag is not null && tags.IndexOf(tag) is int index and >= 0 ? index + 1 : 0;

            _remoteList.Items.Clear();
            var shown = _snapshot.Packages.Where(RemoteMatches)
                .OrderBy(e => e.Review == RemoteReview.Official ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var entry in shown)
                _remoteList.Items.Add(new ListBoxItem
                {
                    Content = RemoteRow(entry, _remoteFilter.SelectedIndex == 1), Tag = entry,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Width = _remoteFilter.SelectedIndex == 1 ? 330 : EffectTileWidth,
                });
            if (shown.Count == 0)
            {
                string empty = _remoteLoading ? UiStrings.Assets_RemoteLoading : _snapshot.Packages.Count > 0 ? UiStrings.Assets_BrowseNoMatch : UiStrings.Assets_BrowseNothing;
                _remoteList.Items.Add(new ListBoxItem { Content = Ui.Label(empty), IsEnabled = false });
            }
            var rows = _remoteList.Items.OfType<ListBoxItem>().Where(item => item.Tag is RemoteCatalogEntry).ToList();
            var target = rows.FirstOrDefault(item => ((RemoteCatalogEntry)item.Tag!).Id == _remoteSelectId) ?? rows.FirstOrDefault();
            _remoteList.SelectedItem = target;
            _remoteSelectId = (target?.Tag as RemoteCatalogEntry)?.Id;
        }
        finally { _remoteRefreshing = false; }
        UpdateStatus(_remoteStatus);
        ShowRemoteDetail();
    }

    /// <summary>들어 있는 종류만 "효과 레시피 3개, 그라데이션 8개"처럼(앱이 모르는 종류는 kind 값 그대로).</summary>
    private static string RemoteContents(RemoteCatalogEntry entry)
        => string.Join(", ", entry.Kinds.OrderBy(pair => KindOrder(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key switch
            {
                AssetKinds.EffectRecipe => F(UiStrings.Assets_ContentsRecipes, pair.Value),
                AssetKinds.Gradient => F(UiStrings.Assets_ContentsGradients, pair.Value),
                AssetKinds.EffectScript => F(UiStrings.Assets_ContentsScripts, pair.Value),
                _ => F(UiStrings.Assets_ContentsOther, pair.Key, pair.Value),
            }));

    /// <summary>설치 상태 한 줄: 설치 안 됨(null), 같은 버전 설치됨, 새 버전 있음, 설치할 수 없음.</summary>
    private static (string Text, IBrush Brush)? RemoteState(RemoteCatalogEntry entry)
    {
        if (entry.IncompatibleReason(AppVersion) is not null) return (UiStrings.Assets_CannotInstall, AppTheme.WarningBrush);
        if (AppAssets.Catalog.FindPackage(entry.Id) is not { } installed) return null;
        return SemVersion.TryParse(installed.Version, out var version) && entry.SemanticVersion > version
            ? (F(UiStrings.Assets_UpdateAvailable, entry.Version), AppTheme.AccentBrush)
            : (F(UiStrings.Assets_InstalledVersion, installed.Version), AppTheme.DimTextBrush);
    }

    private Task<byte[]?> LoadPreview(RemoteCatalogEntry entry)
    {
        if (entry.Preview is not { } preview) return Task.FromResult<byte[]?>(null);
        if (!_previewTasks.TryGetValue(preview.Sha256, out var task))
            _previewTasks.Add(preview.Sha256, task = AppAssets.Remote.GetPreviewAsync(entry, _closing.Token));
        return task;
    }

    private Control RemoteRow(RemoteCatalogEntry entry, bool gradient)
    {
        var panel = new StackPanel();
        if (!gradient)
            panel.Children.Add(new RemotePreviewView(() => LoadPreview(entry), 92, showSource: true));
        var name = new TextBlock
        {
            Text = entry.Name, Foreground = AppTheme.TextBrush, FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        panel.Children.Add(name);
        if (gradient && entry.Preview is not null)
            panel.Children.Add(new RemotePreviewView(() => LoadPreview(entry), 44));
        panel.Children.Add(Dim(gradient
            ? string.Join(" · ", entry.Version, entry.Author.Name, RemoteContents(entry))
            : string.Join(" · ", entry.Version, entry.Author.Name)));
        if (RemoteState(entry) is { } state)
        {
            var line = Dim(state.Text);
            line.Foreground = state.Brush;
            panel.Children.Add(line);
        }
        return panel;
    }

    private void ShowRemoteDetail()
    {
        _remoteDetail.Children.Clear();
        if (SelectedRemote is not { } entry)
        {
            _remoteDetail.Children.Add(Dim(UiStrings.Assets_SelectHint));
            return;
        }
        _remoteDetail.Children.Add(TitleText(entry.Name));
        if (entry.Preview is not null || entry.Kinds.ContainsKey(AssetKinds.EffectRecipe) || entry.Kinds.ContainsKey(AssetKinds.EffectScript))
            _remoteDetail.Children.Add(new RemotePreviewView(() => LoadPreview(entry),
                showSource: entry.Kinds.ContainsKey(AssetKinds.EffectRecipe) || entry.Kinds.ContainsKey(AssetKinds.EffectScript)));
        _remoteDetail.Children.Add(new TextBlock { Text = entry.Summary, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.TextBrush });
        var installed = AppAssets.Catalog.FindPackage(entry.Id);
        _remoteDetail.Children.Add(FactsGrid(
            (UiStrings.Assets_Id, entry.Id),
            (UiStrings.Assets_Version, entry.Version),
            (UiStrings.Assets_InstalledColumn, installed?.Version),
            (UiStrings.Assets_Author, entry.Author.Name),
            (UiStrings.Assets_License, entry.License),
            (UiStrings.Assets_Contents, RemoteContents(entry)),
            (UiStrings.Assets_MinApp, entry.MinAppVersion),
            (UiStrings.Assets_Size, SizeText(entry.Package.Size)),
            (UiStrings.Assets_Published, entry.Published.ToString("d", CultureInfo.CurrentCulture)),
            (UiStrings.Assets_Updated, entry.Updated.ToString("d", CultureInfo.CurrentCulture)),
            (UiStrings.Assets_Review, entry.Review == RemoteReview.Official ? UiStrings.Assets_ReviewOfficial : UiStrings.Assets_ReviewReviewed),
            (UiStrings.Assets_Tags, string.Join(", ", entry.Tags))));
        if (entry.IncompatibleReason(AppVersion) is { } reason)
            _remoteDetail.Children.Add(new TextBlock { Text = F(UiStrings.Assets_Incompatible, reason), Foreground = AppTheme.WarningBrush, TextWrapping = TextWrapping.Wrap });

        var web = entry.Author.Url is { } url ? Ui.Button(VectorIcon.ExternalLink, UiStrings.Assets_WebPage, (_, _) => Updater.OpenUrl(this, url)).Tip(url) : null;
        if (_downloadId == entry.Id)
        {
            _remoteDetail.Children.Add(DownloadRow());
            _remoteDetail.Children.Add(ActionPanel(web));
            return;
        }
        Control? main;
        if (installed is null) main = InstallButton(entry, VectorIcon.Download, UiStrings.Assets_Install);
        else if (SemVersion.TryParse(installed.Version, out var version) && entry.SemanticVersion > version)
            main = InstallButton(entry, VectorIcon.Download, F(UiStrings.Assets_UpdateTo, entry.Version));
        else main = Ui.Button(VectorIcon.Package, UiStrings.Assets_ShowInstalled, (_, _) => ShowInstalled(entry.Id));
        _remoteDetail.Children.Add(ActionPanel(main, web));
    }

    private Button InstallButton(RemoteCatalogEntry entry, VectorIcon icon, string text)
    {
        var button = Ui.Button(icon, text, (_, _) => InstallRemote(entry));
        button.IsEnabled = _download is null && entry.IncompatibleReason(AppVersion) is null;
        return button;
    }

    private static string SizeText(long bytes)
        => bytes < 1024 ? F(UiStrings.Assets_SizeBytes, bytes) : F(UiStrings.Assets_SizeKilobytes, Math.Ceiling(bytes / 1024.0));

    private void ShowInstalled(string packageId)
    {
        var package = AppAssets.Catalog.FindPackage(packageId);
        var asset = package?.Entries.FirstOrDefault();
        _selectKey = asset is null ? "pkg:" + packageId : "asset:" + asset.Id;
        _search.Text = "";
        _filter.SelectedIndex = asset?.Kind == AssetKinds.Gradient ? 1 : 0;
        _showHidden.IsChecked = asset?.Enabled == false;
        _tabs.SelectedIndex = 0;
        Refresh();
    }

    // ───────────── 업데이트 ─────────────

    private void RefreshUpdates()
    {
        _updatesList.Children.Clear();
        UpdateStatus(_updatesStatus);
        var updates = RemoteAssetClient.FindUpdates(AppAssets.Catalog, _snapshot.Packages);
        if (_updatesTab is not null)
            _updatesTab.Header = updates.Count > 0 ? F(UiStrings.Assets_TabUpdatesCount, updates.Count) : UiStrings.Assets_TabUpdates;
        if (updates.Count == 0)
        {
            if (!_remoteLoading && _snapshot.Error is null && _remoteStarted) _updatesList.Children.Add(Dim(UiStrings.Assets_NoUpdates));
            return;
        }
        foreach (var update in updates)
        {
            var remote = update.Remote;
            var info = new StackPanel { Spacing = 2 };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Ui.Icon(VectorIcon.Package, 14), Ui.Label(update.Installed.Name, bold: true) } };
            info.Children.Add(name);
            info.Children.Add(Dim(F(UiStrings.Assets_UpdateRow, update.Installed.Version, remote.Version)));
            info.Children.Add(new TextBlock { Text = remote.Summary, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.TextBrush });
            if (remote.IncompatibleReason(AppVersion) is { } reason)
                info.Children.Add(new TextBlock { Text = F(UiStrings.Assets_Incompatible, reason), Foreground = AppTheme.WarningBrush, TextWrapping = TextWrapping.Wrap });
            Control action = _downloadId == remote.Id ? DownloadRow() : InstallButton(remote, VectorIcon.Download, F(UiStrings.Assets_UpdateTo, remote.Version));
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 1);
            _updatesList.Children.Add(new Border
            {
                Background = AppTheme.InputBrush,
                BorderBrush = AppTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8),
                Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { info, action } },
            });
        }
    }

    // ───────────── 설치·업데이트 ─────────────

    /// <summary>진행 막대 + 퍼센트 + 취소 단추(내려받는 동안 상세와 업데이트 탭에 보임).</summary>
    private Control DownloadRow()
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 220, MinWidth = 220, Height = 4, MinHeight = 4, Foreground = AppTheme.AccentBrush, Value = _downloadFraction };
        var text = new TextBlock { Text = DownloadText(), Foreground = AppTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center };
        _downloadViews.Add((bar, text));
        var cancel = Ui.Button(VectorIcon.X, UiStrings.Assets_CancelDownload, (_, _) => _download?.Cancel());
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { bar, text, cancel },
        };
    }

    private string DownloadText() => F(UiStrings.Assets_Downloading, (int)Math.Floor(_downloadFraction * 100));

    private void ReportDownload(double fraction)
    {
        _downloadFraction = fraction;
        _downloadViews.RemoveAll(view => view.Bar.GetVisualRoot() is null);
        foreach (var (bar, text) in _downloadViews)
        {
            bar.Value = fraction;
            text.Text = DownloadText();
        }
    }

    /// <summary>
    /// 패키지를 내려받아(SHA-256 확인) 저장소에 설치한다. 다른 버전이 있으면 업데이트이고 이전 버전은 되돌리기용으로 남는다.
    /// 취소하면 아무것도 바뀌지 않는다. 받은 파일은 설치 뒤 지운다.
    /// </summary>
    private async void InstallRemote(RemoteCatalogEntry entry)
    {
        if (_download is not null) return;
        if (entry.IncompatibleReason(AppVersion) is { } reason)
        {
            Ui.Warn(this, reason, UiStrings.Assets_Title);
            return;
        }
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _download = cancel;
        _downloadId = entry.Id;
        _downloadFraction = 0;
        RefreshRemoteViews();
        string? file = null;
        try
        {
            file = await AppAssets.Remote.DownloadPackageAsync(entry, new Progress<double>(ReportDownload), cancel.Token);
            if (!cancel.IsCancellationRequested)
            {
                InstalledPackage? installed = null;
                if (AppAssets.Try(this, () => installed = AppAssets.Store.Install(file, AppAssets.RemoteSource), UiStrings.Assets_InstallRemoteError) && installed is not null)
                    _selectKey = installed.Entries.Count > 0 ? "asset:" + installed.Entries[0].Id : "pkg:" + installed.Id;
            }
        }
        catch (OperationCanceledException) { }
        catch (RemoteAssetException ex)
        {
            Ui.Error(this, F(UiStrings.Assets_DownloadError, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, F(UiStrings.Assets_DownloadError, ex.Message));
        }
        finally
        {
            if (file is not null)
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _download = null;
            _downloadId = null;
            _downloadViews.Clear();
            cancel.Dispose();
            RefreshRemoteViews();
        }
    }
}
