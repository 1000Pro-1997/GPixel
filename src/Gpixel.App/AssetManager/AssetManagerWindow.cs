using System.Globalization;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Gpixel.App.Localization;
using Gpixel.Core.Assets;
using Gpixel.Core.Effects;

namespace Gpixel.App;

/// 에셋 관리자: 설치됨·둘러보기·업데이트·휴지통 탭. 설치됨은 효과 타일 또는 그라데이션 한 줄로 개별 에셋만 보인다.
/// 패키지는 설치·업데이트의 저장 단위지만 설치 목록에서 에셋 아래에 중복 표시하지 않는다. 숨긴 항목은 보기 단추를 켰을 때만 보인다.
/// 종류 거르기(효과·그라데이션)와 이름 검색은 메모리 카탈로그에서 한다. 둘러보기·업데이트는 온라인 카탈로그를 쓴다.
/// 모달이며, 효과 창·그라데이션 프리셋 창에서도 종류 거르기를 정해 연다. 저장소가 바뀌면(<see cref="AppAssets.Changed"/>) 목록을 다시 만든다.
/// </summary>
public sealed partial class AssetManagerWindow : DialogBase
{
    private readonly ListBox _list = NewList(), _trashList = NewList();
    private static readonly IBrush HiddenTitleBrush = AppTheme.Brush(Color.FromRgb(230, 95, 95));
    private const double EffectTileWidth = 158;
    private readonly StackPanel _detail = new() { Spacing = 6 }, _trashDetail = new() { Spacing = 6 };
    private readonly TabStrip _filter;
    private readonly TextBox _search;
    private readonly ToggleButton _showHidden;
    private readonly TabControl _tabs = new();
    private readonly Action _onChanged;
    /// <summary>다시 만든 뒤 고를 항목(<see cref="RowKey"/>).</summary>
    private string? _selectKey, _trashSelectKey;
    private bool _refreshing;

    /// <summary>설치 목록 또는 휴지통의 한 줄.</summary>
    private sealed record Row(string Key, InstalledPackage? Package = null, AssetEntry? Asset = null,
        EffectDefinition? BuiltInEffect = null, DeletedPackage? Deleted = null, DeletedPersonalAsset? DeletedPersonal = null);

    /// <summary>에셋 관리자를 연다. <paramref name="kind"/>는 처음 종류 거르기(<see cref="AssetKinds"/>, null = 전체), <paramref name="selectAssetId"/>는 처음 고를 에셋.</summary>
    public static void Open(Window owner, string? kind = null, string? selectAssetId = null)
    {
        AppAssets.ShowProblems(owner);
        new AssetManagerWindow(kind, selectAssetId).ShowDialog(owner);
    }

    private AssetManagerWindow(string? kind, string? selectAssetId) : base(UiStrings.Assets_Title)
    {
        CloseOnly();
        CancelButtonControl.IsDefault = false;
        CanResize = true;
        SizeToContent = SizeToContent.Manual;
        Width = 980;
        Height = 660;
        MinWidth = 720;
        MinHeight = 460;
        _selectKey = selectAssetId is null ? null : "asset:" + selectAssetId;
        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });

        _filter = new TabStrip(TabStripKind.Segmented, UiStrings.Assets_FilterEffects, UiStrings.Assets_FilterGradients)
        {
            SelectedIndex = kind == AssetKinds.Gradient ? 1 : 0,
        };
        _filter.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(() => Refresh());
        _showHidden = new ToggleButton { Content = Ui.IconLabel(VectorIcon.Eye, UiStrings.Assets_ShowHidden) };
        _showHidden.IsCheckedChanged += (_, _) => Refresh();
        _search = new TextBox { Width = 180, Watermark = UiStrings.Assets_Search };
        _search.TextChanged += (_, _) => Refresh();
        var import = Ui.Button(VectorIcon.Download, UiStrings.Assets_Import, (_, _) => Import());
        ToolTip.SetTip(import, UiStrings.Assets_ImportTip);
        var exportPersonal = Ui.Button(VectorIcon.Upload, UiStrings.Assets_ExportPersonal, (_, _) => ExportPersonal(null));
        ToolTip.SetTip(exportPersonal, UiStrings.Assets_ExportPersonalTip);
        var publish = Ui.Button(VectorIcon.Globe, UiStrings.Assets_Publish, (_, _) => PublishPersonal(null)).Tip(UiStrings.Assets_PublishTip);
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
        toolbar.Children.AddRange([_filter, _showHidden, _search, import, exportPersonal, publish]);
        foreach (var child in toolbar.Children) child.Margin = new Thickness(0, 0, 8, 0);

        _list.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _selectKey = Selected?.Key;
            ShowDetail();
        };
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), Children = { _list, DetailScroller(_detail) } };
        var installed = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        installed.Children.Add(toolbar);
        installed.Children.Add(split);
        _trashList.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _trashSelectKey = Selected?.Key;
            ShowDetail();
        };
        var trash = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), Children = { _trashList, DetailScroller(_trashDetail) } };

        _updatesTab = new TabItem { Header = UiStrings.Assets_TabUpdates, Content = UpdatesTab() };
        _tabs.Items.Add(new TabItem { Header = UiStrings.Assets_TabInstalled, Content = installed });
        _tabs.Items.Add(new TabItem { Header = UiStrings.Assets_TabBrowse, Content = BrowseTab(_filter.SelectedIndex) });
        _tabs.Items.Add(_updatesTab);
        _tabs.Items.Add(new TabItem { Header = UiStrings.Assets_TabTrash, Content = trash });
        // 온라인 목록은 둘러보기·업데이트 탭을 처음 열 때 받는다(관리자를 열기만 해서는 요청하지 않음).
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex is 1 or 2 && !_remoteStarted) LoadRemote();
            ShowDetail();
        };

        var root = (DockPanel)Content!;
        root.Children.Remove(Body);
        root.Children.Add(_tabs);

        _onChanged = () => Dispatcher.UIThread.Post(() =>
        {
            Refresh(preserveScroll: true);
            RefreshRemoteViews();
        });
        AppAssets.Changed += _onChanged;
        Closed += (_, _) =>
        {
            AppAssets.Changed -= _onChanged;
            _closing.Cancel();
        };
        Refresh();
    }

    private Row? Selected => ((_tabs.SelectedIndex == 3 ? _trashList : _list).SelectedItem as ListBoxItem)?.Tag as Row;

    /// <summary>효과(레시피·스크립트) 또는 그라데이션.</summary>
    private static string[] FilterKinds(int index) => index == 1
        ? [AssetKinds.Gradient]
        : [AssetKinds.EffectRecipe, AssetKinds.EffectScript];

    private bool Matches(EffectDefinition definition)
    {
        if (_filter.SelectedIndex != 0 || !AppAssets.Store.IsBuiltInEffectEnabled(definition.TypeId) && _showHidden.IsChecked != true) return false;
        string query = _search.Text?.Trim() ?? "";
        return query.Length == 0 || LayerEffectsDialog.UiOf(definition).Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private string[] KindFilter => FilterKinds(_filter.SelectedIndex);

    private bool Matches(AssetEntry entry)
    {
        if (!KindFilter.Contains(entry.Kind) || !entry.Enabled && _showHidden.IsChecked != true) return false;
        string query = _search.Text?.Trim() ?? "";
        return query.Length == 0 || entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                 || (entry.PackageName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }


    /// <summary>표시할 개별 에셋을 다시 만들고 같은 항목을 고른다. 저장소 변경 뒤에는 스크롤 위치를 보존한다.</summary>
    private void Refresh(bool preserveScroll = false)
    {
        var viewer = preserveScroll ? _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() : null;
        var offset = viewer?.Offset;
        var catalog = AppAssets.Catalog;
        _refreshing = true;
        try
        {
            _list.Items.Clear();
            if (_filter.SelectedIndex == 0)
                foreach (var effect in EffectRegistry.BuiltIn.Where(Matches))
                    AddRow(new Row("effect:" + effect.TypeId, BuiltInEffect: effect), BuiltInRow(effect));
            foreach (var entry in catalog.DisplayPersonal.Where(Matches))
                AddRow(new Row("asset:" + entry.Id, Asset: entry), AssetRow(entry));
            foreach (var package in catalog.Packages)
            {
                foreach (var entry in package.Entries.Where(Matches))
                    AddRow(new Row("asset:" + entry.Id, Asset: entry), AssetRow(entry));
                // 읽지 못한 패키지는 상세의 문제와 삭제/파일 위치를 볼 수 있게 효과 목록에만 남긴다.
                if (_filter.SelectedIndex == 0 && package.IsBroken && package.Entries.Count == 0)
                    AddRow(new Row("pkg:" + package.Id, Package: package), PackageRow(package));
            }
            if (_list.Items.Count == 0)
                _list.Items.Add(new ListBoxItem { Content = Ui.Label(UiStrings.Assets_EmptyInstalled), IsEnabled = false });

            _trashList.Items.Clear();
            var deletedRows = AppAssets.Store.DeletedPackages.Select(item =>
                    (item.DeletedUtc, Row: new Row("deleted:" + item.Folder, Deleted: item), View: DeletedRow(item)))
                .Concat(AppAssets.Store.DeletedPersonalAssets.Select(item =>
                    (item.DeletedUtc, Row: new Row("deleted-personal:" + item.Folder, DeletedPersonal: item), View: DeletedPersonalRow(item))))
                .OrderByDescending(item => item.DeletedUtc);
            foreach (var item in deletedRows) AddTrashRow(item.Row, item.View);
            if (_trashList.Items.Count == 0)
                _trashList.Items.Add(new ListBoxItem { Content = Ui.Label(UiStrings.Assets_EmptyTrash), IsEnabled = false });
            var trashRows = _trashList.Items.OfType<ListBoxItem>().Where(item => item.Tag is Row).ToList();
            var trashTarget = trashRows.FirstOrDefault(item => ((Row)item.Tag!).Key == _trashSelectKey) ?? trashRows.FirstOrDefault();
            _trashList.SelectedItem = trashTarget;
            _trashSelectKey = (trashTarget?.Tag as Row)?.Key;
            var rows = _list.Items.OfType<ListBoxItem>().Where(item => item.Tag is Row).ToList();
            var target = rows.FirstOrDefault(item => ((Row)item.Tag!).Key == _selectKey) ?? rows.FirstOrDefault();
            _list.SelectedItem = target;
            _selectKey = (target?.Tag as Row)?.Key;
        }
        finally { _refreshing = false; }
        ShowDetail();
        if (viewer is not null && offset is { } saved)
            Dispatcher.UIThread.Post(() => viewer.Offset = saved, DispatcherPriority.Loaded);
        else if (_list.SelectedItem is { } selected) _list.ScrollIntoView(selected);
        if (_trashList.SelectedItem is { } trashSelected) _trashList.ScrollIntoView(trashSelected);
    }

    private void AddRow(Row row, Control content)
        => _list.Items.Add(new ListBoxItem
        {
            Content = content, Tag = row, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Width = _filter.SelectedIndex == 0 && row.Package is null ? EffectTileWidth : 330,
        });

    private void AddTrashRow(Row row, Control content)
        => _trashList.Items.Add(new ListBoxItem { Content = content, Tag = row, HorizontalContentAlignment = HorizontalAlignment.Stretch });

    private static TextBlock Dim(string text) => new()
    {
        Text = text,
        Foreground = AppTheme.DimTextBrush,
        FontSize = AppTheme.SmallFontSize,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Control BuiltInRow(EffectDefinition effect)
    {
        bool enabled = AppAssets.Store.IsBuiltInEffectEnabled(effect.TypeId);
        return EffectTile(new AssetPreviewView(effect), LayerEffectsDialog.UiOf(effect).Name, enabled, favorite: false);
    }

    private static Control EffectTile(AssetPreviewView preview, string title, bool enabled, bool favorite)
    {
        preview.Height = 92;
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        if (favorite)
        {
            var star = Ui.Icon(VectorIcon.Star, 12, filled: true);
            star.Foreground = AppTheme.FavoriteBrush;
            name.Children.Add(star);
        }
        name.Children.Add(new TextBlock
        {
            Text = title, Foreground = enabled ? AppTheme.TextBrush : HiddenTitleBrush,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 135,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return new StackPanel { Children = { preview, name } };
    }

    private static Control AssetRow(AssetEntry entry)
    {
        if (entry.Kind != AssetKinds.Gradient)
            return EffectTile(new AssetPreviewView(entry, useStoredPreview: entry.Kind == AssetKinds.EffectScript || entry.Recipe?.Scripts.Count > 0),
                entry.Name, entry.Enabled, entry.Favorite);
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (entry.Favorite)
        {
            var star = Ui.Icon(VectorIcon.Star, 12, filled: true);
            star.Foreground = AppTheme.FavoriteBrush;
            name.Children.Add(star);
        }
        name.Children.Add(Ui.Icon(VectorIcon.SlidersHorizontal, 13));
        name.Children.Add(new TextBlock
        {
            Text = entry.Name, Foreground = entry.Enabled ? AppTheme.TextBrush : HiddenTitleBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var panel = new StackPanel { Children = { name } };
        if (entry.Gradient is { } gradient)
            panel.Children.Add(new GradientSwatch { Preset = gradient, Height = 10, Margin = new Thickness(0, 3, 0, 1) });
        return panel;
    }

    private static Control PackageRow(InstalledPackage package)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        name.Children.Add(Ui.Icon(VectorIcon.Package, 14));
        name.Children.Add(Ui.Label(package.Name, bold: true));
        string status = package.IsBroken ? UiStrings.Assets_Broken : package.Enabled ? "" : UiStrings.Assets_Disabled;
        var sub = Dim(string.Join(" · ", new[] { package.Version, package.Manifest?.Author.Name, status }.Where(s => !string.IsNullOrEmpty(s))));
        if (package.IsBroken) sub.Foreground = AppTheme.WarningBrush;
        var panel = new StackPanel { Children = { name, sub } };
        if (!package.Enabled) panel.Opacity = 0.7;
        return panel;
    }

    private static Control DeletedRow(DeletedPackage deleted)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        name.Children.Add(Ui.Icon(VectorIcon.Trash, 13));
        name.Children.Add(new TextBlock { Text = deleted.Name, Foreground = AppTheme.DimTextBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        return new StackPanel
        {
            Children = { name, Dim(deleted.Version + " · " + deleted.DeletedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) },
        };
    }

    private static Control DeletedPersonalRow(DeletedPersonalAsset deleted)
        => Ui.Row(Ui.Icon(VectorIcon.Trash, 13), Ui.Label(deleted.Name));
}
