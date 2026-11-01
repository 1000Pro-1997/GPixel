using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// 파일 브라우저: 루트 폴더 아래 폴더 트리 + 선택 폴더의 .gpixel 프로젝트와 그림(PNG·GIF·WebP·JPG …)·영상 썸네일 목록(하위 포함 토글,
/// 프로젝트가 내보내는 이미지는 프로젝트 하나로 묶어 보여 줌), 이름 검색, 파일 변경 감시, 우클릭 메뉴(새 프로젝트·이름 바꾸기·탐색기에서 열기·휴지통으로 삭제),
/// 항목을 목록 밖으로 끌어 내기(캔버스에 놓으면 새 레이어).
/// </summary>
public sealed class FileBrowserPanel : UserControl
{
    private const int ThumbSize = 56;
    private const int ItemWidth = 84;

    private readonly TextBlock _rootLabel;
    private readonly TextBox _search;
    private readonly CheckBox _includeSub;
    private readonly TreeView _tree;
    private readonly ListBox _list;
    private readonly Grid _split;
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, WriteableBitmap Thumb)> _thumbCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>지금 목록에 있는 항목(경로 → 항목).</summary>
    private readonly Dictionary<string, FileItem> _items = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>저장하지 않은 변경이 있는 파일(경로 → 메모리 속 그림과 그 썸네일; 그림이 없으면 디스크 썸네일).</summary>
    private readonly Dictionary<string, (PixelImage? Image, WriteableBitmap? Thumb)> _unsaved = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _thumbCts;
    private bool _structureChanged;
    private bool _suppressActivate;
    private bool _rebuildingTree;
    /// <summary>이름 바꾸기 중이면 끝내는 동작(true = 적용, false = 취소).</summary>
    private Action<bool>? _endRename;

    /// <summary>목록 한 칸: 경로, 그 칸의 컨트롤들(Dot = 저장 안 한 변경 빨간 점), 기본 도움말.</summary>
    private sealed record FileItem(string Path, ListBoxItem Container, Image Thumb, TextBlock Label, StackPanel Content, Control Dot, string Tip);

    /// <summary>
    /// 왼쪽 버튼을 누른 상태: 누른 곳(항목 패널 좌표, 스크롤과 함께 움직임), 그때의 선택(Ctrl이면 유지), 끌기 시작 여부,
    /// 목록 밖으로 끌어 내면 끌어 놓을 파일(항목을 눌렀을 때 — 고른 항목이면 고른 파일 모두, 아니면 그 파일 하나).
    /// </summary>
    private sealed class PressState(Avalonia.Point start, IReadOnlyList<object> baseSelection, bool modifier, IReadOnlyList<string> dragPaths)
    {
        public Avalonia.Point Start { get; } = start;
        public IReadOnlyList<object> BaseSelection { get; } = baseSelection;
        public bool Modifier { get; } = modifier;
        public IReadOnlyList<string> DragPaths { get; } = dragPaths;
        public bool Dragging { get; set; }
        public bool SelectionChanged { get; set; }
    }

    private readonly Grid _listHost;
    private readonly Border _marquee;
    private PressState? _press;
    /// <summary>고른 순서(합치기에서 "처음 고른 파일"·순서에 씀).</summary>
    private readonly List<string> _selectionOrder = new();

    /// <summary>합친 새 프로젝트의 Unity 설정 기본값(프로젝트 파일이 없을 때). MainForm이 마지막 PPU로 정한다.</summary>
    public Func<SpriteSettings>? SpriteDefaults { get; set; }

    /// <summary>
    /// 경로의 열린 문서(지금 문서 또는 메모리에 남겨 둔 저장 안 한 문서, 프로젝트 경로나 가져온 PNG 경로로 찾음). 없으면 null(디스크에서 읽음).
    /// MainForm이 정한다. 돌려준 인스턴스는 편집 중인 문서이므로 읽기만 한다.
    /// </summary>
    public Func<string, SpriteProject?>? FindOpenDocument { get; set; }

    /// <summary>지운 파일을 보관하는 앱 휴지통.</summary>
    public AppTrash Trash { get; set; } = new(AppTrash.DefaultRoot);

    /// <summary>휴지통 보관 정책(기간·용량). MainForm이 설정과 잇는다.</summary>
    public Func<TrashPolicy> TrashPolicy { get; set; } = () => Gpixel.App.TrashPolicy.Default;

    /// <summary>휴지통 창에서 보관 정책을 바꿨을 때.</summary>
    public event Action<TrashPolicy>? TrashPolicyChanged;

    /// <summary>상태 표시줄에 보일 알림.</summary>
    public event EventHandler<string>? Message;

    public FileBrowserPanel()
    {
        Background = AppTheme.PanelBrush;
        _rootLabel = new TextBlock
        {
            Height = 22,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = AppTheme.DimTextBrush,
            Padding = new Thickness(4, 4, 0, 0),
            Text = UiStrings.S_FileBrowserPanel_FileBrowserPanel_771668A357,
        };
        DockPanel.SetDock(_rootLabel, Dock.Top);

        _search = new TextBox { Watermark = UiStrings.S_FileBrowserPanel_FileBrowserPanel_0918E7E454, Margin = new Thickness(2) };
        _search.TextChanged += (_, _) => RefreshList();
        DockPanel.SetDock(_search, Dock.Top);
        _includeSub = Ui.Check(UiStrings.S_FileBrowserPanel_FileBrowserPanel_9C35775EF4, true);
        _includeSub.Margin = new Thickness(6, 0, 3, 2);
        _includeSub.IsCheckedChanged += (_, _) => RefreshList();
        DockPanel.SetDock(_includeSub, Dock.Top);

        _tree = new TreeView
        {
            SelectionMode = SelectionMode.Single,
            Background = AppTheme.InputBrush,
            Foreground = AppTheme.TextBrush,
        };
        _tree.SelectionChanged += (_, _) => { if (!_rebuildingTree) RefreshList(); };

        _list = new ListBox
        {
            // 여러 개 고르기: Ctrl+클릭·Shift+클릭, 또는 끌어서 사각형으로(합치기·삭제).
            SelectionMode = SelectionMode.Multiple,
            Background = AppTheme.InputBrush,
            Foreground = AppTheme.TextBrush,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, e) =>
        {
            foreach (var r in e.RemovedItems) if (r is ListBoxItem { Tag: FileItem f }) _selectionOrder.Remove(f.Path);
            foreach (var a in e.AddedItems) if (a is ListBoxItem { Tag: FileItem f } && !_selectionOrder.Contains(f.Path)) _selectionOrder.Add(f.Path);
            if (_suppressActivate) return;
            // 왼쪽 버튼을 누르고 있는 동안에는 끌어서 고르기일 수 있으므로 뗄 때 연다.
            if (_press is not null) { _press.SelectionChanged = true; return; }
            if (_list.SelectedItems?.Count == 1 && e.AddedItems.Count > 0 && e.AddedItems[0] is ListBoxItem { Tag: FileItem added })
                FileActivated?.Invoke(this, added.Path);
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2 && SelectedPaths.Count == 1 && SelectedItem is { } item) { BeginRename(item); e.Handled = true; }
            else if (IsDeleteKey(e) && SelectedPath is not null) { DeleteSelected(); e.Handled = true; }
        };
        // 빈 곳을 오른쪽 클릭하면 선택을 지운다(메뉴는 폴더 대상). 왼쪽 버튼은 끌어서 고르기.
        _list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(PointerCaptureLostEvent, (_, e) => { if (ReferenceEquals(e.Source, _list)) EndMarquee(); }, RoutingStrategies.Bubble, handledEventsToo: true);

        PopupMenus.Attach(_list, ListMenu);

        _marquee = new Border
        {
            BorderBrush = AppTheme.AccentBrush,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(AppTheme.AccentColor, 0.18),
            IsVisible = false,
            IsHitTestVisible = false,
        };
        var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true, Children = { _marquee } };
        _listHost = new Grid { Children = { _list, overlay } };

        var lower = new DockPanel { LastChildFill = true };
        lower.Children.Add(_search);
        lower.Children.Add(_includeSub);
        lower.Children.Add(_listHost);
        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Rows,
            Background = AppTheme.BorderBrush,
            Height = 4,
            MinHeight = 4,
        };
        _split = new Grid { RowDefinitions = new RowDefinitions("160,4,*") };
        _split.RowDefinitions[0].MinHeight = 40;
        _split.RowDefinitions[2].MinHeight = 60;
        Grid.SetRow(_tree, 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(lower, 2);
        _split.Children.Add(_tree);
        _split.Children.Add(splitter);
        _split.Children.Add(lower);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(BuildHeader());
        root.Children.Add(_rootLabel);
        root.Children.Add(_split);
        Content = root;

        _watchTimer.Tick += (_, _) => FlushWatcherChanges();
    }

    /// <summary>
    /// "파일" 제목 줄 + 오른쪽 끝 아이콘(VS Code처럼): 새 픽셀 파일(.gpixel — 새 프로젝트 창) · 새 폴더 · 새로 고침(누르면 한 바퀴 돌며).
    /// 새 파일·폴더는 트리에서 고른 폴더 안에 만든다. 폴더 열기는 파일 메뉴(Ctrl+Shift+O·Ctrl+Shift+U).
    /// </summary>
    private Border BuildHeader()
    {
        var header = Ui.Header(UiStrings.S_FileBrowserPanel_BuildHeader_6F62028F17);
        var title = (TextBlock)header.Child!;
        header.Child = null;
        var icon = new SpinningIcon(VectorIcon.Refresh) { Width = 14, Height = 14 };
        var refresh = HeaderButton(icon, UiStrings.S_FileBrowserPanel_BuildHeader_2F2AC43715, () =>
        {
            icon.Spin();
            RefreshAll();
        });
        var newFolder = HeaderButton(new IconView(VectorIcon.FolderPlus) { Width = 15, Height = 15 }, UiStrings.S_FileBrowserPanel_BuildHeader_FAE18EB89C, NewFolder);
        var newFile = HeaderButton(new IconView(VectorIcon.FilePlus) { Width = 15, Height = 15 }, UiStrings.S_FileBrowserPanel_BuildHeader_A9BCCFFDB7, () =>
        {
            if (RootFolder is not null) NewImageRequested?.Invoke(this, CurrentFolder ?? RootFolder);
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { newFile, newFolder, refresh } };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Child = new DockPanel { LastChildFill = true, Children = { buttons, title } };
        return header;
    }

    private static Button HeaderButton(Control icon, string tip, Action click)
    {
        var b = new Button
        {
            Content = icon,
            Width = 22,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 3, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        }.Tip(tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>트리에서 고른 폴더(없으면 맨 위 폴더) 안에 새 폴더를 만들고 그 폴더를 고른다.</summary>
    private void NewFolder()
    {
        if ((CurrentFolder ?? RootFolder) is not { } parent || !Directory.Exists(parent)) return;
        var suggested = UiStrings.S_FileBrowserPanel_NewFolder_C3C710DE5C;
        for (int n = 2; Directory.Exists(Path.Combine(parent, suggested)); n++) suggested = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_NewFolder_E8E2E7E79D,n);
        if (InputDialog.Ask(this, UiStrings.S_FileBrowserPanel_NewFolder_C3C710DE5C, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_NewFolder_24D67BC2F2,parent), suggested) is not { } name) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_NewFolder_E8C72D3C43,name));
            return;
        }
        var path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path))
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_NewFolder_B3AFB9D932,path));
            return;
        }
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_NewFolder_4D7D9C0D34,ex.Message));
            return;
        }
        RebuildTree(path);
    }

    /// <summary>선 아이콘. <see cref="Spin"/>하면 한 바퀴(감속) 돈다.</summary>
    private sealed class SpinningIcon : IconView
    {
        private readonly RotateTransform _rotate = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
        private DateTime _start;
        private const double DurationMs = 550;

        public SpinningIcon(VectorIcon icon) : base(icon)
        {
            RenderTransform = _rotate;
            RenderTransformOrigin = RelativePoint.Center;
            _timer.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.UtcNow - _start).TotalMilliseconds / DurationMs);
                _rotate.Angle = 360 * (1 - Math.Pow(1 - t, 3));
                if (t >= 1)
                {
                    _timer.Stop();
                    _rotate.Angle = 0;
                }
            };
        }

        public void Spin()
        {
            _start = DateTime.UtcNow;
            _timer.Start();
        }
    }

    public string? RootFolder { get; private set; }

    /// <summary>트리에서 고른 폴더.</summary>
    public string? CurrentFolder => (_tree.SelectedItem as TreeViewItem)?.Tag as string ?? RootFolder;

    public string? SelectedPath => SelectedItem?.Path;

    private FileItem? SelectedItem => _list.SelectedItem is ListBoxItem { Tag: FileItem f } ? f : null;

    /// <summary>고른 파일들(고른 순서).</summary>
    private IReadOnlyList<string> SelectedPaths
    {
        get
        {
            var selected = (_list.SelectedItems?.OfType<ListBoxItem>() ?? []).Select(c => c.Tag).OfType<FileItem>().Select(f => f.Path).ToList();
            var set = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ordered = _selectionOrder.Where(set.Contains).ToList();
            ordered.AddRange(selected.Where(p => !ordered.Contains(p, StringComparer.OrdinalIgnoreCase)));
            return ordered;
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSub.IsChecked == true;
        set => _includeSub.IsChecked = value;
    }

    /// <summary>폴더 트리 높이(화면 단위).</summary>
    public int TreeHeight
    {
        get
        {
            var row = _split.RowDefinitions[0];
            return row.Height.IsAbsolute ? (int)row.Height.Value : (int)row.ActualHeight;
        }
        set
        {
            // 아직 배치 전이면 높이를 모르므로 아래쪽 한계는 나중에 행 최소 높이가 지킨다.
            if (value > 40 && (_split.Bounds.Height <= 0 || value < _split.Bounds.Height - 60))
                _split.RowDefinitions[0].Height = new GridLength(value, GridUnitType.Pixel);
        }
    }

    public event EventHandler<string>? FileActivated;
    /// <summary>새 프로젝트를 만들 폴더.</summary>
    public event EventHandler<string>? NewImageRequested;
    /// <summary>"AI 그림 픽셀로 정리"할 그림 파일.</summary>
    public event EventHandler<string>? PixelateRequested;
    /// <summary>이름이 바뀐 파일(이전, 새 경로).</summary>
    public event EventHandler<(string OldPath, string NewPath)>? FileRenamed;
    /// <summary>휴지통으로 보낸 파일.</summary>
    public event EventHandler<string>? FileDeleted;
    /// <summary>외부(또는 앱)에서 바뀐 파일 경로들(감시 결과, 묶어서 전달).</summary>
    public event EventHandler<IReadOnlyCollection<string>>? FilesChanged;

    // ───────────── 폴더 ─────────────

    public void SetRoot(string folder)
    {
        RootFolder = Path.GetFullPath(folder);
        _rootLabel.Text = RootFolder;
        ToolTip.SetTip(_rootLabel, RootFolder);
        StartWatcher();
        RebuildTree(RootFolder);
    }

    private void RebuildTree(string? selectFolder)
    {
        _rebuildingTree = true;
        try
        {
            _tree.Items.Clear();
            if (RootFolder is not null && Directory.Exists(RootFolder))
            {
                var root = MakeNode(RootFolder);
                root.Header = Path.GetFileName(Path.TrimEndingDirectorySeparator(RootFolder)) is { Length: > 0 } n ? n : RootFolder;
                _tree.Items.Add(root);
                PopulateChildren(root);
                root.IsExpanded = true;
                var selected = FindOrExpand(root, selectFolder) ?? root;
                selected.IsSelected = true;
                _tree.SelectedItem = selected;
                Dispatcher.UIThread.Post(() => selected.BringIntoView(), DispatcherPriority.Background);
            }
        }
        finally
        {
            _rebuildingTree = false;
        }
        RefreshList();
    }

    private static TreeViewItem MakeNode(string dir)
    {
        var node = new TreeViewItem { Header = Path.GetFileName(dir), Tag = dir, MinHeight = 20 };
        if (HasSubdirectories(dir)) node.Items.Add(new TreeViewItem { Header = "…", MinHeight = 20 });
        // 펼칠 때 하위 폴더를 읽는다.
        node.PropertyChanged += (_, e) =>
        {
            if (e.Property == TreeViewItem.IsExpandedProperty && node.IsExpanded) PopulateChildren(node);
        };
        return node;
    }

    private static bool HasSubdirectories(string dir)
    {
        try { return EnumerateDirs(dir).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> EnumerateDirs(string dir)
        => Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .Order(StringComparer.OrdinalIgnoreCase);

    private static void PopulateChildren(TreeViewItem node)
    {
        if (node.Items.Count != 1 || node.Items[0] is not TreeViewItem { Tag: null }) return;
        node.Items.Clear();
        try
        {
            foreach (var d in EnumerateDirs((string)node.Tag!)) node.Items.Add(MakeNode(d));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>트리에서 폴더 노드를 찾는다(필요한 만큼 펼침).</summary>
    private static TreeViewItem? FindOrExpand(TreeViewItem root, string? folder)
    {
        if (folder is null) return null;
        var node = root;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        while (true)
        {
            var tag = Path.TrimEndingDirectorySeparator((string)node.Tag!);
            if (string.Equals(tag, target, StringComparison.OrdinalIgnoreCase)) return node;
            if (!target.StartsWith(tag + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            PopulateChildren(node);
            var next = node.Items.OfType<TreeViewItem>().FirstOrDefault(n => n.Tag is string t
                && (string.Equals(t, target, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(t + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            if (next is null) return null;
            node.IsExpanded = true;
            node = next;
        }
    }

    public void RefreshAll()
    {
        if (RootFolder is null) return;
        RebuildTree(CurrentFolder);
    }

    // ───────────── 파일 목록 ─────────────

    private void RefreshList()
    {
        var folder = CurrentFolder;
        var keepSelected = SelectedPaths;
        _thumbCts?.Cancel();
        _endRename?.Invoke(false);
        _suppressActivate = true;
        _list.Items.Clear();
        _items.Clear();
        _suppressActivate = false;
        if (folder is null || !Directory.Exists(folder)) return;

        List<string> files;
        var exportsOf = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = _includeSub.IsChecked == true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            var projects = Directory.EnumerateFiles(folder, "*" + SpriteProject.FileExtension, options).ToList();
            var projectKeys = projects.Select(p => Path.ChangeExtension(p, null)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // 프로젝트가 내보내는 이미지(같은 이름 PNG + 등록된 프레임 시트·부위 아틀라스)는 목록에서 빼고 프로젝트 하나로 보여 준다.
            var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in projects)
            {
                var outputs = ProjectIO.GetRegisteredExportPaths(p);
                exportsOf[p] = outputs;
                exported.UnionWith(outputs);
            }
            files = projects
                .Concat(Directory.EnumerateFiles(folder, "*", options)
                    .Where(p => MediaFormats.KindOf(p) is MediaKind.Image or MediaKind.Video
                                && !(p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && projectKeys.Contains(Path.ChangeExtension(p, null)))
                                && !exported.Contains(Path.GetFullPath(p))))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = new();
        }
        var query = (_search.Text ?? "").Trim();
        if (query.Length > 0)
            files = files.Where(f => Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase)
                                     || exportsOf.TryGetValue(f, out var outs) && outs.Any(o => Path.GetFileName(o).Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        var pending = new List<(string Path, DateTime Stamp)>();
        FileItem? kept = null;
        _suppressActivate = true;
        try
        {
            foreach (var f in files)
            {
                var tip = Path.GetRelativePath(RootFolder ?? folder, f);
                if (exportsOf.TryGetValue(f, out var outputs) && outputs.Count > 0)
                    tip += UiStrings.S_FileBrowserPanel_RefreshList_AB16A8824B + string.Join(", ", outputs.Select(o => Path.GetRelativePath(Path.GetDirectoryName(f)!, o)));
                var item = MakeItem(f, tip);
                var stamp = SafeStamp(f);
                if (_thumbCache.TryGetValue(f, out var cached) && cached.Stamp == stamp) item.Thumb.Source = cached.Thumb;
                else pending.Add((f, stamp));
                ApplyUnsaved(item);
                _list.Items.Add(item.Container);
            }
            // 고른 순서대로 다시 고른다(순서 기억도 그대로).
            foreach (var path in keepSelected)
            {
                if (!_items.TryGetValue(path, out var item)) continue;
                _list.SelectedItems?.Add(item.Container);
                kept ??= item;
            }
        }
        finally
        {
            _suppressActivate = false;
        }
        if (kept is not null) _list.ScrollIntoView(kept.Container);
        if (pending.Count > 0) LoadThumbnails(pending);
    }

    /// <summary>
    /// <c>.gpixel</c> 프로젝트(레이어·프레임을 보관하는 편집 원본) 표시: 썸네일 오른쪽 아래 파란 레이어 배지.
    /// 그냥 PNG(내보낸 결과물이나 프로젝트 없는 이미지)는 배지가 없다.
    /// </summary>
    private static Border ProjectBadge()
    {
        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = AppTheme.AccentBrush,
            BorderBrush = AppTheme.PanelBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -4, -2),
            Child = new IconView(VectorIcon.Layers, Brushes.White, 2.4),
        };
        ToolTip.SetTip(badge, UiStrings.S_FileBrowserPanel_ProjectBadge_E9D1D0E582);
        return badge;
    }

    /// <summary>저장하지 않은 변경 표시: 썸네일 왼쪽 위 빨간 점(어두운 테두리).</summary>
    private static Border UnsavedDot() => new()
    {
        Width = 9,
        Height = 9,
        CornerRadius = new CornerRadius(4.5),
        Background = AppTheme.UnsavedBrush,
        BorderBrush = AppTheme.BackBrush,
        BorderThickness = new Thickness(1.5),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(-3, -3, 0, 0),
        IsVisible = false,
        IsHitTestVisible = false,
    };

    private FileItem MakeItem(string path, string tip)
    {
        var thumb = new Image { Width = ThumbSize, Height = ThumbSize, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(thumb, BitmapInterpolationMode.None);
        var label = new TextBlock
        {
            Text = Path.GetFileName(path),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = AppTheme.TextBrush,
        };
        var content = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        var dot = UnsavedDot();
        var tile = new Grid { Width = ThumbSize, Height = ThumbSize, HorizontalAlignment = HorizontalAlignment.Center, Children = { thumb } };
        if (IsProject(path)) tile.Children.Add(ProjectBadge());
        else if (MediaFormats.KindOf(path) == MediaKind.Video)
        {
            // 썸네일(ffmpeg로 뽑은 첫 프레임)이 오기 전·ffmpeg가 없을 때 보이는 필름 그림 + 오른쪽 아래 배지.
            tile.Children.Insert(0, new IconView(VectorIcon.Film, AppTheme.DimTextBrush, 1.6) { Width = 30, Height = 30, IsHitTestVisible = false });
            tile.Children.Add(VideoBadge());
        }
        tile.Children.Add(dot);
        content.Children.Add(tile);
        content.Children.Add(label);
        var container = new ListBoxItem
        {
            Content = content,
            Width = ItemWidth,
            Padding = new Thickness(2, 4),
            Margin = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(container, tip);
        var item = new FileItem(path, container, thumb, label, content, dot, tip);
        container.Tag = item;
        _items[path] = item;
        return item;
    }

    private static DateTime SafeStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private void LoadThumbnails(List<(string Path, DateTime Stamp)> files)
    {
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        var token = cts.Token;
        var ffmpeg = new Lazy<string?>(() => FfmpegTool.Locate(App.Settings));
        Task.Run(() =>
        {
            foreach (var (path, stamp) in files)
            {
                if (token.IsCancellationRequested) return;
                WriteableBitmap thumb;
                try
                {
                    var img = IsProject(path) ? ProjectIO.LoadThumbnail(path) : LoadPreview(path, ffmpeg, token);
                    if (img is null) continue;
                    thumb = ImageUtil.MakeThumbnail(img, ThumbSize);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                {
                    continue;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    _thumbCache.TryGetValue(path, out var old);
                    _thumbCache[path] = (stamp, thumb);
                    if (_items.TryGetValue(path, out var item) && _unsaved.GetValueOrDefault(path).Thumb is null) item.Thumb.Source = thumb;
                    old.Thumb?.Dispose();
                });
            }
        }, token);
    }

    /// <summary>
    /// 그림·영상 썸네일 원본: 그림은 첫 프레임(PNG·GIF는 직접, 나머지는 SkiaSharp), 영상·AVIF·HEIC는 ffmpeg가 있으면 뽑은 첫 프레임.
    /// 못 만들면 null(영상은 필름 그림이 남음).
    /// </summary>
    private static PixelImage? LoadPreview(string path, Lazy<string?> ffmpeg, CancellationToken token)
    {
        if (!MediaFormats.NeedsFfmpeg(path) || ImageUtil.CountFrames(path) > 0)
            return ImageUtil.DecodeAnimation(path, maxFrames: 1)?.Images[0];
        return ffmpeg.Value is { } exe ? FfmpegTool.FirstFrame(exe, path, ThumbSize * 2, token) : null;
    }

    /// <summary>영상 표시: 썸네일 오른쪽 아래 필름 배지.</summary>
    private static Border VideoBadge()
    {
        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -4, -2),
            Child = new IconView(VectorIcon.Film, AppTheme.TextBrush, 2.0),
        };
        ToolTip.SetTip(badge, UiStrings.FileBrowser_VideoBadge);
        return badge;
    }

    /// <summary>파일 하나의 썸네일을 다시 만들고(저장 후) 목록을 갱신한다.</summary>
    public void RefreshFile(string path)
    {
        _thumbCache.Remove(path, out var old);
        RefreshList();
        old.Thumb?.Dispose();
    }

    /// <summary>
    /// 저장하지 않은 변경이 있는 파일들(경로 → 메모리 속 그림, null이면 디스크 썸네일 그대로)을 정한다:
    /// 칸 왼쪽 위 빨간 점, 도움말 "저장하지 않은 변경 있음", 그림이 있으면 그 그림을 썸네일로.
    /// </summary>
    public void SetUnsaved(IReadOnlyDictionary<string, PixelImage?> files)
    {
        var stale = new List<WriteableBitmap>();
        var changed = new HashSet<string>(_unsaved.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, old) in _unsaved.ToList())
        {
            if (files.TryGetValue(path, out var image) && ReferenceEquals(image, old.Image)) { changed.Remove(path); continue; }
            _unsaved.Remove(path);
            if (old.Thumb is not null) stale.Add(old.Thumb);
        }
        foreach (var (path, image) in files)
        {
            if (_unsaved.ContainsKey(path)) continue;
            _unsaved[path] = (image, image is null ? null : ImageUtil.MakeThumbnail(image, ThumbSize));
            changed.Add(path);
        }
        foreach (var path in changed)
            if (_items.TryGetValue(path, out var item)) ApplyUnsaved(item);
        foreach (var bmp in stale) bmp.Dispose();
    }

    /// <summary>칸에 저장 안 한 변경 표시(점·도움말·메모리 썸네일)를 맞춘다.</summary>
    private void ApplyUnsaved(FileItem item)
    {
        bool unsaved = _unsaved.TryGetValue(item.Path, out var entry);
        item.Dot.IsVisible = unsaved;
        ToolTip.SetTip(item.Container, unsaved ? item.Tip + UiStrings.S_FileBrowserPanel_ApplyUnsaved_AFDFDFD168 : item.Tip);
        if (entry.Thumb is not null) item.Thumb.Source = entry.Thumb;
        else if (item.Thumb.Source is not null && !_thumbCache.Values.Any(c => ReferenceEquals(c.Thumb, item.Thumb.Source)))
            item.Thumb.Source = _thumbCache.TryGetValue(item.Path, out var cached) ? cached.Thumb : null;
    }

    /// <summary>목록에서 파일을 선택 표시한다(열기 이벤트 없이).</summary>
    public void SelectFile(string? path)
    {
        _suppressActivate = true;
        try
        {
            _list.SelectedItem = null;
            if (path is not null && _items.TryGetValue(path, out var item))
            {
                _list.SelectedItem = item.Container;
                _list.ScrollIntoView(item.Container);
            }
        }
        finally
        {
            _suppressActivate = false;
        }
    }

    /// <summary>
    /// 파일이 든 폴더를 위 폴더 트리에서 고르고(필요하면 펼침·스크롤) 잠깐 깜빡여 표시한다. 목록은 그 폴더로 바뀌고 그 파일이 선택된 채로 남는다.
    /// (하위 폴더 포함으로 보던 파일이 실제로 어느 폴더에 있는지 찾을 때)
    /// </summary>
    public void RevealFolderOf(string path)
    {
        if (_tree.Items.Count == 0 || _tree.Items[0] is not TreeViewItem root || Path.GetDirectoryName(path) is not { } folder) return;
        if (FindOrExpand(root, folder) is not { } node) return;
        // 방금 펼친 노드는 화면에 만들어진 뒤에야 고를 수 있다.
        Dispatcher.UIThread.Post(() =>
        {
            _tree.SelectedItem = node;
            node.IsSelected = true;
            node.BringIntoView();
            FlashNode(node);
            // 목록이 그 폴더로 바뀐 뒤 그 파일을 다시 고른다.
            Dispatcher.UIThread.Post(() => SelectFile(path), DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    /// <summary>트리 노드를 강조색으로 세 번 깜빡인다(약 1초).</summary>
    private static void FlashNode(TreeViewItem node)
    {
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (ticks >= 7) { node.ClearValue(TemplatedControl.BackgroundProperty); timer.Stop(); return; }
            if (ticks % 2 == 1) node.Background = AppTheme.AccentBrush;
            else node.ClearValue(TemplatedControl.BackgroundProperty);
        };
        node.Background = AppTheme.AccentBrush;
        timer.Start();
    }

    /// <summary>목록 우클릭 메뉴(열 때마다 지금 선택에 맞춰 만든다). 동작은 메뉴가 닫힌 뒤에 한다(대화상자·이름 바꾸기 입력란 포커스).</summary>
    private Control[] ListMenu()
    {
        var paths = SelectedPaths;
        int count = paths.Count;
        static MenuItem Item(string header, Action action, bool enabled, KeyGesture? gesture = null, DispatcherPriority? priority = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = gesture };
            item.Click += (_, _) => Dispatcher.UIThread.Post(action, priority ?? DispatcherPriority.Default);
            return item;
        }
        List<string> projectsOnly;
        try
        {
            projectsOnly = ProjectFilesOnlyDeletion(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            projectsOnly = []; // 딸린 파일을 모르면 일반 삭제와 다른지 알 수 없으므로 숨긴다(삭제가 오류를 알린다).
        }
        MenuItem? projectsOnlyItem = null;
        if (projectsOnly.Count > 0)
        {
            projectsOnlyItem = Item(projectsOnly.Count > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_ListMenu_F35AB082B3,projectsOnly.Count) : UiStrings.S_FileBrowserPanel_ListMenu_9B8C4F5504,
                () => DeleteProjectFilesOnly(projectsOnly), true);
            ToolTip.SetTip(projectsOnlyItem, UiStrings.S_FileBrowserPanel_ListMenu_F72A938B51
                                             + UiStrings.S_FileBrowserPanel_ListMenu_1BFD2CD3AE);
        }
        bool image = count == 1 && PixelateDialog.IsImageFile(paths[0]);
        return
        [
            Item(UiStrings.S_FileBrowserPanel_ListMenu_1299ADE057, () => NewImageRequested?.Invoke(this, CurrentFolder ?? RootFolder ?? ""), RootFolder is not null),
            .. image ? [Item(UiStrings.Pixelate_MenuItem, () => PixelateRequested?.Invoke(this, paths[0]), true)] : Array.Empty<Control>(),
            new Separator(),
            Item(count >= 2 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_ListMenu_1C7165B3A5,count) : UiStrings.S_FileBrowserPanel_ListMenu_06B813FF5F, MergeSelected,
                count >= 2 && paths.All(p => IsProject(p) || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))),
            new Separator(),
            Item(UiStrings.S_FileBrowserPanel_ListMenu_9C29BBC4F7, () => { if (SelectedItem is { } item) BeginRename(item); }, count == 1, new KeyGesture(Key.F2), DispatcherPriority.Background),
            Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_ListMenu_B1C1369643,Platform.FileManagerName), ShowInExplorer, count > 0 || CurrentFolder is not null),
            Item(UiStrings.S_FileBrowserPanel_ListMenu_A16228CF35, () => { if (paths.Count == 1) RevealFolderOf(paths[0]); }, count == 1),
            Item(count > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_ListMenu_01B5A0C03C,count) : UiStrings.S_FileBrowserPanel_ListMenu_E78953BDA3, DeleteSelected, count > 0,
                Platform.IsMac ? new KeyGesture(Key.Back, KeyModifiers.Meta) : new KeyGesture(Key.Delete)),
            .. projectsOnlyItem is null ? Array.Empty<Control>() : [projectsOnlyItem],
            new Separator(),
            Item(UiStrings.S_FileBrowserPanel_ListMenu_FD792AE5B7, () => RestoreLastDeleted(), Trash.Entries().Count > 0, new KeyGesture(Key.Z, Platform.CommandModifier)),
            Item(UiStrings.S_FileBrowserPanel_ListMenu_5FC61831E6, ShowTrash, true),
        ];
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_list).Properties;
        var source = e.Source as Visual;
        if (props.IsRightButtonPressed)
        {
            if (source?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null)
                _list.SelectedItem = null;
            return;
        }
        if (!props.IsLeftButtonPressed || _list.ItemsPanelRoot is not { } panel) return;
        if (source?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null || source is TextBox || source?.FindAncestorOfType<TextBox>() is not null) return;
        bool command = Ui.HasCommand(e.KeyModifiers);
        bool modifier = command || (e.KeyModifiers & KeyModifiers.Shift) != 0;
        // Ctrl/⌘를 누르고 끌면 지금 선택에 더한다.
        var baseSelection = command ? (_list.SelectedItems?.Cast<object>().ToList() ?? []) : [];
        // 누른 항목(아직 목록이 고르기 전): 이미 고른 항목이면 고른 파일 모두를, 아니면 그 파일만 밖으로 끌어 낼 수 있다.
        IReadOnlyList<string> dragPaths = source?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { Tag: FileItem pressed } container
            ? !modifier && _list.SelectedItems?.Contains(container) == true ? SelectedPaths : [pressed.Path]
            : [];
        _press = new PressState(e.GetPosition(panel), baseSelection, modifier, dragPaths);
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _list.ItemsPanelRoot is not { } panel) return;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            EndMarquee();
            return;
        }
        var pos = e.GetPosition(panel);
        if (!press.Dragging)
        {
            if (Math.Abs(pos.X - press.Start.X) < 5 && Math.Abs(pos.Y - press.Start.Y) < 5) return;
            press.Dragging = true;
            e.Pointer.Capture(_list);
        }
        // 항목을 눌러 목록 왼쪽·오른쪽 밖(캔버스 쪽)으로 끌면 사각형 고르기 대신 파일 끌어 놓기(캔버스에 놓으면 새 레이어).
        double listX = e.GetPosition(_list).X;
        if (press.DragPaths.Count > 0 && (listX < 0 || listX > _list.Bounds.Width))
        {
            StartFileDrag(e, press.DragPaths);
            return;
        }
        // 목록 위·아래 밖으로 끌면 그쪽으로 스크롤한다.
        if (_list.FindDescendantOfType<ScrollViewer>() is { } sv)
        {
            double y = e.GetPosition(_list).Y;
            double step = y < 0 ? y : y > _list.Bounds.Height ? y - _list.Bounds.Height : 0;
            if (step != 0) sv.Offset = sv.Offset.WithY(Math.Clamp(sv.Offset.Y + step / 2, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
        }
        var rect = new Rect(press.Start, pos).Normalize();
        var wanted = new HashSet<object>(press.BaseSelection);
        foreach (var item in _items.Values)
            if (item.Container.Bounds.Intersects(rect)) wanted.Add(item.Container);
        SetSelection(wanted.Contains);
        if (_marquee.Parent is Visual overlay && panel.TranslatePoint(rect.TopLeft, overlay) is { } topLeft)
        {
            Canvas.SetLeft(_marquee, topLeft.X);
            Canvas.SetTop(_marquee, topLeft.Y);
            _marquee.Width = rect.Width;
            _marquee.Height = rect.Height;
            _marquee.IsVisible = true;
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is not { } press) return;
        if (press.Dragging)
        {
            e.Pointer.Capture(null);
            EndMarquee();
            return;
        }
        _press = null;
        // 그냥 클릭(수정자 없음)으로 하나만 골랐으면 연다.
        if (press.SelectionChanged && !press.Modifier && _list.SelectedItems?.Count == 1 && SelectedItem is { } item)
            FileActivated?.Invoke(this, item.Path);
    }

    /// <summary>끌어서 고르기를 끝낸다(사각형 숨김).</summary>
    private void EndMarquee()
    {
        if (_press is { Dragging: true }) _press = null;
        _marquee.IsVisible = false;
    }

    /// <summary>목록 칸(<see cref="ListBoxItem"/>) 중 <paramref name="wanted"/>인 것만 고른다(고른 파일을 열지는 않음).</summary>
    private void SetSelection(Func<object, bool> wanted)
    {
        _suppressActivate = true;
        try
        {
            _list.Selection.BeginBatchUpdate();
            for (int i = 0; i < _list.Items.Count; i++)
            {
                bool want = wanted(_list.Items[i]!);
                if (want != _list.Selection.IsSelected(i))
                {
                    if (want) _list.Selection.Select(i);
                    else _list.Selection.Deselect(i);
                }
            }
            _list.Selection.EndBatchUpdate();
        }
        finally
        {
            _suppressActivate = false;
        }
    }

    /// <summary>
    /// 목록 밖으로 끌어 낸 파일을 끌어 놓기로 넘긴다(앱 캔버스에 놓으면 새 레이어, 다른 프로그램에는 파일 복사).
    /// 사각형 고르기는 끝내고 끄는 파일만 고른 상태로 둔다(열지는 않음).
    /// </summary>
    private async void StartFileDrag(PointerEventArgs e, IReadOnlyList<string> paths)
    {
        _press = null;
        _marquee.IsVisible = false;
        e.Pointer.Capture(null);
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetSelection(item => item is ListBoxItem { Tag: FileItem f } && set.Contains(f.Path));
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var data = new DataTransfer();
        foreach (var path in paths)
            if (await storage.TryGetFileFromPathAsync(path) is { } file) data.Add(DataTransferItem.CreateFile(file));
        if (data.Items.Count > 0) await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    private static bool IsDeleteKey(KeyEventArgs e)
        => e.Key == Key.Delete || (Platform.IsMac && e.Key == Key.Back && Ui.HasCommand(e.KeyModifiers));

    // ───────────── 우클릭 동작 ─────────────

    /// <summary>목록 칸의 이름을 그 자리에서 고친다(Enter·포커스 이동 = 적용, Esc = 취소).</summary>
    private void BeginRename(FileItem item)
    {
        if (_endRename is not null || !_items.ContainsKey(item.Path)) return;
        var box = new TextBox
        {
            Text = item.Label.Text,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 0,
            Padding = new Thickness(2, 1),
            Width = ItemWidth - 8,
        };
        bool done = false;
        void End(bool commit)
        {
            if (done) return;
            done = true;
            _endRename = null;
            var text = box.Text;
            if (box.IsKeyboardFocusWithin) item.Container.Focus(); // 다른 곳을 눌러 끝났으면 포커스를 뺏지 않는다
            item.Content.Children.Remove(box);
            item.Label.IsVisible = true;
            if (commit && text is not null) Rename(item.Path, text);
        }
        box.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    End(true);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    End(false);
                    e.Handled = true;
                    break;
                case Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Delete or Key.Back or Key.F2:
                    e.Handled = true; // 입력란이 처리한 뒤 목록 이동·삭제로 번지지 않게
                    break;
            }
        };
        box.LostFocus += (_, _) => End(true);
        _endRename = End;
        item.Label.IsVisible = false;
        item.Content.Children.Add(box);
        Dispatcher.UIThread.Post(() =>
        {
            if (done) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void Rename(string oldPath, string label)
    {
        var newName = label.Trim();
        var ext = Path.GetExtension(oldPath);
        if (!newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName += ext;
        if (newName.Length <= ext.Length || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Ui.Error(this, UiStrings.S_FileBrowserPanel_Rename_55189D1414);
            return;
        }
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return;
        try
        {
            if (IsProject(oldPath))
                ProjectIO.RenameWithOwnedDefaultExport(oldPath, newPath);
            else if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                // 대소문자만 바꿀 때는 임시 이름을 거친다.
                var temp = oldPath + ".rename-tmp.png";
                DocumentIO.RenameWithCompanions(oldPath, temp);
                DocumentIO.RenameWithCompanions(temp, newPath);
            }
            else DocumentIO.RenameWithCompanions(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_Rename_A9EAEE0E61,ex.Message));
            return;
        }
        FileRenamed?.Invoke(this, (oldPath, newPath));
        Dispatcher.UIThread.Post(() =>
        {
            RefreshList();
            SelectFile(newPath);
        });
    }

    private void ShowInExplorer()
    {
        var target = SelectedPath;
        var folder = CurrentFolder;
        try
        {
            if (target is not null) Platform.RevealInFileManager(target);
            else if (folder is not null) Platform.OpenFolder(folder);
        }
        catch (IOException ex)
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_ShowInExplorer_08041FBD31,Platform.FileManagerName,ex.Message));
        }
    }

    /// <summary>
    /// 파일과 함께 휴지통으로 보낼 파일: 프로젝트면 소유한 기본 내보내기(PNG·.meta 등), PNG면 있는 .meta·사이드카.
    /// 읽을 수 없으면 예외.
    /// </summary>
    internal static List<string> CompanionsOf(string path)
        => IsProject(path)
            ? ProjectIO.GetOwnedDefaultExportPaths(path).ToList()
            : DocumentIO.GetCompanionPaths(path).Where(File.Exists).ToList();

    private void DeleteSelected()
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        List<string> companions;
        try
        {
            companions = paths.SelectMany(CompanionsOf).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_DeleteSelected_816311E9EF,ex.Message));
            return;
        }
        // 앱 휴지통에 보관하므로 묻지 않는다(Ctrl+Z·휴지통 보기로 되살림).
        var what = paths.Count == 1 ? Path.GetFileName(paths[0]) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_DeleteSelected_773E860265,Path.GetFileName(paths[0]),paths.Count - 1);
        TrashFiles(paths, companions, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_DeleteSelected_A43604BD66,what));
        RefreshList();
    }

    /// <summary>
    /// 고른 파일 중 <c>.gpixel</c> 프로젝트 파일만(내보낸 PNG·.meta와 다른 파일은 남김).
    /// 일반 삭제가 이보다 더 지우지 않으면(프로젝트가 없거나, 고른 것이 모두 프로젝트이고 딸린 내보내기도 없으면) 삭제와 같으므로 빈 목록.
    /// 딸린 파일을 읽을 수 없으면 예외.
    /// </summary>
    internal static List<string> ProjectFilesOnlyDeletion(IReadOnlyList<string> paths)
    {
        var projects = paths.Where(IsProject).ToList();
        if (projects.Count == 0) return [];
        if (projects.Count < paths.Count) return projects;
        return projects.Any(p => CompanionsOf(p).Count > 0) ? projects : [];
    }

    /// <summary>프로젝트 파일만 휴지통으로(내보내기는 남김). 일반 삭제처럼 묻지 않고 Ctrl+Z로 되살린다.</summary>
    private void DeleteProjectFilesOnly(IReadOnlyList<string> projects)
    {
        var existing = projects.Where(File.Exists).ToList();
        if (existing.Count == 0) return;
        var what = existing.Count == 1 ? Path.GetFileName(existing[0]) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_DeleteProjectFilesOnly_08F3E17C6D,Path.GetFileName(existing[0]),existing.Count - 1);
        TrashFiles(existing, [], string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_DeleteProjectFilesOnly_F581110A07,what));
        RefreshList();
    }

    /// <summary>
    /// 파일들(+딸린 파일)을 앱 휴지통의 항목 하나로 보낸다. 실패하면 알리고 false(옮긴 것은 휴지통에 남고 <see cref="FileDeleted"/>로 알림).
    /// </summary>
    private bool TrashFiles(IReadOnlyList<string> paths, IReadOnlyList<string> companions, string label)
    {
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var entry = Trash.Add(paths.Concat(companions), label);
            if (entry is not null) moved.UnionWith(entry.Files.Select(f => f.OriginalPath));
            // 용량이 넘치면 오래된 항목부터 지운다(방금 넣은 항목은 가장 새것이라 마지막에).
            int purged = Trash.Purge(TrashPolicy());
            Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_TrashFiles_0B4E51595A,label.Split(": ", 2)[^1],TrashPolicy().Describe())
                                  + (purged > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_TrashFiles_8A275448B2,purged) : ""));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_TrashFiles_DEFE206710,ex.Message));
            moved.UnionWith(paths.Where(p => !File.Exists(p)));
            return false;
        }
        finally
        {
            foreach (var p in paths) if (moved.Contains(Path.GetFullPath(p))) FileDeleted?.Invoke(this, p);
        }
    }

    /// <summary>파일 목록에서 Ctrl+Z: 가장 최근에 지운 항목을 되살린다. 휴지통이 비었으면 false.</summary>
    public bool RestoreLastDeleted()
    {
        Trash.Purge(TrashPolicy());
        var last = Trash.Entries().FirstOrDefault();
        if (last is null)
        {
            Message?.Invoke(this, UiStrings.S_FileBrowserPanel_RestoreLastDeleted_1E96406912);
            return false;
        }
        try
        {
            var restored = Trash.Restore(last);
            OnRestored(restored);
            Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_RestoreLastDeleted_9CA813D777,last.Label));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, ex.Message);
        }
        return true;
    }

    /// <summary>휴지통 보기 창.</summary>
    private void ShowTrash()
    {
        var dlg = new TrashDialog(Trash, TrashPolicy(), p => TrashPolicyChanged?.Invoke(p), OnRestored);
        dlg.ShowDialog(this);
    }

    /// <summary>되살린 파일을 목록에 보이고 고른다(열지는 않음).</summary>
    private void OnRestored(IReadOnlyList<string> restored)
    {
        RefreshList();
        _suppressActivate = true;
        try
        {
            _list.SelectedItem = null;
            foreach (var p in restored)
                if (_items.TryGetValue(p, out var item)) _list.SelectedItems?.Add(item.Container);
        }
        finally
        {
            _suppressActivate = false;
        }
        if (SelectedItem is { } first) _list.ScrollIntoView(first.Container);
    }

    /// <summary>
    /// 고른 파일들을 새 <c>.gpixel</c> 하나로 합친다(첫 파일의 폴더에 저장 + 기본 내보내기, 그다음 그 프로젝트를 엶).
    /// 원본 삭제를 켰으면: 결과 이름이 지울 원본과 겹치면 원본을 먼저 휴지통으로 보낸 뒤 저장하고, 아니면 저장한 뒤 지운다.
    /// </summary>
    private void MergeSelected()
    {
        var paths = SelectedPaths;
        if (paths.Count < 2) return;
        var items = new List<(string Path, MergeSource Source)>();
        foreach (var path in paths)
        {
            try
            {
                items.Add((path, MergeSource.Load(path, FindOpenDocument?.Invoke(path))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_MergeSelected_C3939B87F0,Path.GetFileName(path),ex.Message));
                return;
            }
        }
        var dlg = new MergeDialog(items, Path.GetDirectoryName(paths[0])!);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var project = ProjectMerge.Merge(dlg.Items.Select(i => i.Source).ToList(), dlg.Options, SpriteDefaults?.Invoke());
        var target = dlg.ProjectPath;
        var originals = dlg.Items.Select(i => i.Path).ToList();
        List<string> companions = [];
        if (dlg.DeleteOriginals)
        {
            try
            {
                companions = originals.SelectMany(CompanionsOf).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_MergeSelected_536B51355C,ex.Message));
                return;
            }
        }
        bool trashFirst = dlg.DeleteOriginals && dlg.TargetPaths(dlg.ResultName).Any(File.Exists);
        var trashLabel = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_MergeSelected_FA8A9430AD,Path.GetFileName(target),Path.GetFileName(originals[0]),originals.Count - 1);
        if (trashFirst && !TrashFiles(originals, companions, trashLabel))
        {
            RefreshList();
            return;
        }
        try
        {
            var job = ProjectIO.BeginSave(project, target);
            WorkOverlay.Run(this, UiStrings.Busy_Saving, progress => job.Run(progress));
            job.Finish();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_FileBrowserPanel_MergeSelected_724DC713A5,(trashFirst ? UiStrings.S_FileBrowserPanel_MergeSelected_4E46361A76 : ""),target,ex.Message));
            RefreshList();
            return;
        }
        if (dlg.DeleteOriginals && !trashFirst) TrashFiles(originals, companions, trashLabel);
        RefreshList();
        SelectFile(target);
        FileActivated?.Invoke(this, target);
    }

    private static bool IsProject(string path) => path.EndsWith(SpriteProject.FileExtension, StringComparison.OrdinalIgnoreCase);

    // ───────────── 변경 감시 ─────────────

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        var root = RootFolder;
        if (root is null || !Directory.Exists(root)) return;
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += (s, e) => { OnFsEvent(s, e); Queue(e.OldFullPath, true); };
            _watcher.Error += (_, _) => Queue(root, true);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>감시 스레드에서 불린다.</summary>
    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        bool isListed = MediaFormats.KindOf(e.FullPath) != MediaKind.None;
        bool isDir = e.ChangeType != WatcherChangeTypes.Changed && !Path.HasExtension(e.FullPath);
        if (!isListed && !isDir && !e.FullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            && !e.FullPath.EndsWith(SidecarIO.Extension, StringComparison.OrdinalIgnoreCase)) return;
        Queue(e.FullPath, isDir);
    }

    /// <summary>바뀐 경로를 UI 스레드에 모으고, 조용해지면(400ms) 한 번에 반영한다.</summary>
    private void Queue(string path, bool structure)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _pendingChanges.Add(path);
            _structureChanged |= structure;
            _watchTimer.Stop();
            _watchTimer.Start();
        });
    }

    private void FlushWatcherChanges()
    {
        _watchTimer.Stop();
        var changed = _pendingChanges.ToList();
        _pendingChanges.Clear();
        var stale = new List<WriteableBitmap>();
        foreach (var p in changed)
            if (_thumbCache.Remove(p, out var old)) stale.Add(old.Thumb);
        if (_structureChanged) RebuildTree(CurrentFolder);
        else RefreshList();
        _structureChanged = false;
        // 목록을 새로 만든 뒤라 더는 화면에 쓰이지 않는다.
        foreach (var b in stale) b.Dispose();
        FilesChanged?.Invoke(this, changed);
    }
}
