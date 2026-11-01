using Avalonia.Input.Platform;
using Gpixel.App.PlayTest;
using Gpixel.Core.Edit;
using Gpixel.App.Localization;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.App;

/// <summary>
/// Gpixel 메인 창(Aseprite식 배치): 메뉴, 도구 옵션 막대(위), 세로 도구 막대(왼쪽 끝), 팔레트·색 열,
/// 캔버스(가운데), 타임라인(아래), 미리보기·Unity/내보내기 독(오른쪽), 접을 수 있는 파일 브라우저(맨 왼쪽), 상태 표시줄.
/// .gpixel 프로젝트 열기/저장(+시트 내보내기), 단축키, 설정 저장을 맡는다.
/// </summary>
public sealed partial class MainForm : Window, Ai.IAiHost
{
    private enum KeyScope
    {
        /// <summary>어디에 포커스가 있어도 동작.</summary>
        Global,
        /// <summary>글자 입력란에 포커스가 있으면 그 입력란이 키를 받는다.</summary>
        NotInText,
        /// <summary>캔버스에 포커스가 있을 때만.</summary>
        Canvas,
    }

    private static readonly (ToolKind Tool, string Id, string Name, string Key, string Hint)[] ToolDefs =
    [
        (ToolKind.Pencil, "연필", MainStrings.Tool_Pencil_Name, "B", MainStrings.Tool_Pencil_Hint),
        (ToolKind.Eraser, "지우개", MainStrings.Tool_Eraser_Name, "E", MainStrings.Tool_Eraser_Hint),
        (ToolKind.Fill, "채우기", MainStrings.Tool_Fill_Name, "G", MainStrings.Tool_Fill_Hint),
        (ToolKind.Gradient, "그라데이션", MainStrings.Tool_Gradient_Name, "F", MainStrings.Tool_Gradient_Hint),
        (ToolKind.Picker, "스포이드", MainStrings.Tool_Picker_Name, "I", MainStrings.Tool_Picker_Hint),
        (ToolKind.Shape, "도형", MainStrings.Tool_Shape_Name, "R", MainStrings.Tool_Shape_Hint),
        (ToolKind.Select, "사각 선택", MainStrings.Tool_Select_Name, "", MainStrings.Tool_Select_Hint),
        (ToolKind.EllipseSelect, "원 선택", MainStrings.Tool_EllipseSelect_Name, "", MainStrings.Tool_EllipseSelect_Hint),
        (ToolKind.Lasso, "자유 선택", MainStrings.Tool_Lasso_Name, "Q", MainStrings.Tool_Lasso_Hint),
        (ToolKind.MagicWand, "스마트 선택", MainStrings.Tool_MagicWand_Name, "W", MainStrings.Tool_MagicWand_Hint),
        (ToolKind.Move, "이동", MainStrings.Tool_Move_Name, "V", MainStrings.Tool_Move_Hint),
        (ToolKind.Dodge, "밝게/어둡게", MainStrings.Tool_Dodge_Name, "D", MainStrings.Tool_Dodge_Hint),
        (ToolKind.Dither, "디더링", MainStrings.Tool_Dither_Name, "T", MainStrings.Tool_Dither_Hint),
        (ToolKind.ReplaceBrush, "색 바꾸기 브러시", MainStrings.Tool_ReplaceBrush_Name, "C", MainStrings.Tool_ReplaceBrush_Hint),
        (ToolKind.Pivot, "피벗", MainStrings.Tool_Pivot_Name, "K", MainStrings.Tool_Pivot_Hint),
        (ToolKind.Slice, "9-slice", "9-slice", "S", MainStrings.Ui_035),
    ];

    /// <summary>
    /// 도구 막대 칸(위에서부터). 한 칸의 도구는 서로 바꿔 쓰는 변형이다: 채우기(채우기·그라데이션), 선택(사각·원·자유·스마트).
    /// 칸은 마지막으로 고른 도구를 보이고, 길게 누르거나 오른쪽 클릭하면 옆에 펼쳐 바꾼다(<see cref="ToolSlotButton"/>).
    /// </summary>
    private static readonly ToolKind[][] ToolSlots =
    [
        [ToolKind.Pencil], [ToolKind.Eraser], [ToolKind.Fill, ToolKind.Gradient], [ToolKind.Picker],
        [ToolKind.Shape],
        [ToolKind.Select, ToolKind.EllipseSelect, ToolKind.Lasso, ToolKind.MagicWand],
        [ToolKind.Move], [ToolKind.Dodge], [ToolKind.Dither], [ToolKind.ReplaceBrush], [ToolKind.Pivot], [ToolKind.Slice],
    ];

    /// <summary>도구 이름(예: "연필").</summary>
    internal static string ToolName(ToolKind tool) => ToolDefs.First(d => d.Tool == tool).Name;

    /// <summary>단축키를 바꿀 수 있는 명령들(메뉴를 만들 때 등록, <see cref="ApplyShortcuts"/>가 키 표를 만든다).</summary>
    private readonly List<CommandEntry> _commands = new();
    /// <summary>지금 만드는 위 메뉴 이름(명령 분류).</summary>
    private string _cmdCategory = "";
    private string _cmdCategoryDisplay = "";
    private readonly Dictionary<(Key Key, KeyModifiers Mods), (KeyScope Scope, Func<bool> Action)> _keys = new();

    /// <summary>단축키의 "Ctrl" 자리(macOS = ⌘).</summary>
    private static readonly KeyModifiers Ctrl = Platform.CommandModifier;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Alt = KeyModifiers.Alt;
    private const KeyModifiers NoMods = KeyModifiers.None;

    private const int SplitterSize = 3;
    private const int MinPanelSize = 25;

    private readonly AppSettings _settings;
    /// <summary>파일을 닫아도 남는 실행취소 기록.</summary>
    private readonly UndoHistoryKeeper _history;
    /// <summary>그림 편집과 팔레트 변경을 한 줄로 세운 실행취소 순서.</summary>
    private readonly UndoTimeline _undoOrder = new();
    private readonly StartupArgs _args;
    private readonly EditorContext _ctx = new();

    private readonly PixelCanvas _canvas;
    private readonly StartPanel _start;
    private readonly FileBrowserPanel _browser;
    private readonly ColorPanel _colors;
    private readonly ToolOptionsPanel _toolOptions;
    private readonly TimelinePanel _timeline;
    private readonly PreviewPanel _preview;

    // 나눔 막대(Grid + GridSplitter): 고정되는 쪽의 열/행만 픽셀 크기, 나머지는 *.
    private readonly Grid _splitFiles, _splitPalette, _splitRight, _splitTimeline, _splitRightDock;
    private readonly ColumnDefinition _colFiles, _colPalette, _colRight;
    private readonly RowDefinition _rowBottom, _rowPreview;
    private readonly GridSplitter _filesSplitter;

    private readonly List<ToolSlotButton> _toolSlots = new();
    private ToggleButton _tbFiles = null!;
    /// <summary>도구 막대 아래 끝 저장·되돌리기 단추(<see cref="UpdateSaveButtons"/>가 켜고 끈다).</summary>
    private Button _tbSave = null!, _tbRevert = null!;
    private TextBlock _cbToolName = null!, _cbHint = null!;
    /// <summary>확대율 숫자 상자(%): 클릭 = 직접 입력, 좌우로 끌기 = 바꾸기, 우클릭 = 화면에 맞추기·대표 배율 목록.</summary>
    private NumericUpDown _cbZoom = null!;
    private bool _syncingZoom;
    private ToggleButton _cbGrid = null!, _cbTiles = null!, _cbTiled = null!, _cbPivot = null!, _cbOnion = null!, _cbFit = null!;
    /// <summary>새 버전이 있을 때만 보이는 위 막대 단추(누르면 업데이트 창).</summary>
    private Button _cbUpdate = null!;

    private MenuItem _miUndo = null!, _miRedo = null!, _miRecentFolders = null!, _miRecentFiles = null!;
    private MenuItem _miGrid = null!, _miTiles = null!, _miTiled = null!, _miPivot = null!, _miOnion = null!, _miFiles = null!;
    private MenuItem _miPixelPerfect = null!, _miFilled = null!, _miGlobalFill = null!, _miMirrorX = null!, _miMirrorY = null!,
        _miRightErase = null!, _miWrap = null!;
    private readonly List<MenuItem> _docItems = new();
    private MenuItem _miPlayTest = null!, _miRevert = null!;
    private readonly Dictionary<ToolKind, MenuItem> _toolMenuItems = new();

    private readonly TextBlock _stPos, _stColor, _stSize, _stFrame, _stZoom, _stDirty, _stMessage;
    /// <summary>상태 표시줄 "수정됨" 앞의 점.</summary>
    private readonly IconView _stDirtyDot = new(VectorIcon.Dot, AppTheme.WarningBrush)
    {
        Width = 8,
        Height = 8,
        Filled = true,
        IsVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 4, 0),
    };

    private PlayTestForm? _play;
    private readonly DispatcherTimer _playRefresh = new() { Interval = TimeSpan.FromMilliseconds(400) };
    /// <summary>자동 저장: 편집할 때마다 다시 재는 한 번짜리 타이머(<see cref="RestartAutoSave"/>, <see cref="AutoSaveTick"/>).</summary>
    private readonly DispatcherTimer _autoSave = new();
    private bool _settingsApplied;
    private bool _closeConfirmed, _closePending;
    private Ai.McpServer? _mcp;
    private Ai.ChatWindow? _chat;
    /// <summary>AI 참고 자료 보관소(처음 쓸 때 연다, 폴더 설정을 바꾸면 다시 연다).</summary>
    private Gpixel.Core.References.ReferenceLibrary? _refs;
    private ReferenceManagerWindow? _refsWindow;
    private readonly Updater _updater;
    /// <summary>최대화 전 일반 창 위치·크기(설정 저장용).</summary>
    private PixelPoint _normalPosition;
    private Avalonia.Size _normalSize;

    public MainForm(StartupArgs args, AppSettings settings)
    {
        _args = args;
        _settings = settings;
        ScriptTrustStore.Load(_settings);
        _history = new UndoHistoryKeeper(_settings);
        _history.Failed += m => Dispatcher.UIThread.Post(() => ShowMessage(MainStrings.FormatMessage_336(m: m)));
        _updater = new Updater(_settings, this, ShowMessage, ShowUpdateButton, ConfirmCloseAll, ExitForUpdate);
        Title = "Gpixel";
        Background = AppTheme.BackBrush;
        Foreground = AppTheme.TextBrush;
        FontFamily = AppTheme.FontFamily;
        FontSize = AppTheme.FontSize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 1280;
        Height = 800;
        MinWidth = 1000;
        MinHeight = 600;

        // ── 패널
        _canvas = new PixelCanvas(_ctx);
        _start = new StartPanel();
        _browser = new FileBrowserPanel();
        _colors = new ColorPanel(_ctx);
        _toolOptions = new ToolOptionsPanel(_ctx);
        _preview = new PreviewPanel(_ctx);
        _timeline = new TimelinePanel(_ctx, _preview);

        var canvasHost = BuildPaneArea();
        AttachWindowDrop();

        var s0 = _settings;
        (_splitRightDock, _rowPreview, _) = MakeRowSplit(_preview, _toolOptions, fixedFirst: true, s0.PreviewHeight);
        (_splitTimeline, _rowBottom, _) = MakeRowSplit(canvasHost, _timeline, fixedFirst: false, s0.BottomHeight);
        (_splitRight, _colRight, _) = MakeColumnSplit(_splitTimeline, _splitRightDock, fixedFirst: false, s0.RightWidth);
        (_splitPalette, _colPalette, _) = MakeColumnSplit(_colors, _splitRight, fixedFirst: true, s0.PaletteWidth);
        var work = new DockPanel { Background = AppTheme.BackBrush };
        var toolBar = BuildToolBar();
        DockPanel.SetDock(toolBar, Dock.Left);
        work.Children.Add(toolBar);
        work.Children.Add(_splitPalette);
        (_splitFiles, _colFiles, _filesSplitter) = MakeColumnSplit(_browser, work, fixedFirst: true, s0.LeftWidth);

        // ── 상태 표시줄
        var statusBar = new DockPanel { Height = 22, LastChildFill = true };
        _stPos = StatusLabel(statusBar, 80);
        _stColor = StatusLabel(statusBar, 100);
        _stSize = StatusLabel(statusBar, 90);
        _stFrame = StatusLabel(statusBar, 180);
        _stZoom = StatusLabel(statusBar, 60);
        _stDirty = StatusLabel(statusBar, 80);
        var dirtyCell = (Border)_stDirty.Parent!;
        dirtyCell.Child = null;
        dirtyCell.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _stDirtyDot, _stDirty } };
        _stMessage = new TextBlock
        {
            Foreground = AppTheme.DimTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 6, 0),
        };
        statusBar.Children.Add(_stMessage);
        var status = new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = statusBar,
        };

        var menu = BuildMenu();
        var contextBar = BuildContextBar();
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(contextBar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(contextBar);
        root.Children.Add(status);
        root.Children.Add(_splitFiles);
        Content = root;

        KeyState.Attach(this);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // 숫자·글자 입력란(도구 설정의 크기 등) 밖을 누르면 입력 상태를 끝내고 캔버스로 포커스를 돌린다(공통 모듈).
        TextFocusRelease.SetFallback(this, () => _canvas);
        Opened += (_, _) => OnShown();
        Closing += OnWindowClosing;
        // Windows는 최대화할 때 WindowState가 바뀌기 전에 위치·크기 이벤트가 오므로 상태가 정해진 뒤에 기록한다.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(RememberNormalBounds);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(RememberNormalBounds);

        WireEvents();
        ApplySettingsBeforeShow();
        UpdateAll();
    }

    // ───────────── 구성 ─────────────

    private static GridSplitter Splitter(GridResizeDirection direction)
    {
        var sp = new GridSplitter
        {
            ResizeDirection = direction,
            Background = AppTheme.BorderBrush,
            Focusable = false,
        };
        if (direction == GridResizeDirection.Columns) { sp.Width = SplitterSize; sp.MinWidth = SplitterSize; }
        else { sp.Height = SplitterSize; sp.MinHeight = SplitterSize; }
        return sp;
    }

    /// <summary>좌우로 나눈 칸. <paramref name="fixedFirst"/>면 왼쪽, 아니면 오른쪽이 고정 너비(창 크기가 바뀌어도 그대로).</summary>
    private static (Grid Grid, ColumnDefinition Fixed, GridSplitter Splitter) MakeColumnSplit(Control a, Control b, bool fixedFirst, int size)
    {
        var fixedCol = new ColumnDefinition(new GridLength(size)) { MinWidth = MinPanelSize };
        var starCol = new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinPanelSize };
        var grid = new Grid { Background = AppTheme.PanelBrush };
        grid.ColumnDefinitions.Add(fixedFirst ? fixedCol : starCol);
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(fixedFirst ? starCol : fixedCol);
        var sp = Splitter(GridResizeDirection.Columns);
        Grid.SetColumn(a, 0);
        Grid.SetColumn(sp, 1);
        Grid.SetColumn(b, 2);
        grid.Children.Add(a);
        grid.Children.Add(sp);
        grid.Children.Add(b);
        return (grid, fixedCol, sp);
    }

    /// <summary>위아래로 나눈 칸. <paramref name="fixedFirst"/>면 위, 아니면 아래가 고정 높이.</summary>
    private static (Grid Grid, RowDefinition Fixed, GridSplitter Splitter) MakeRowSplit(Control a, Control b, bool fixedFirst, int size)
    {
        var fixedRow = new RowDefinition(new GridLength(size)) { MinHeight = MinPanelSize };
        var starRow = new RowDefinition(1, GridUnitType.Star) { MinHeight = MinPanelSize };
        var grid = new Grid { Background = AppTheme.PanelBrush };
        grid.RowDefinitions.Add(fixedFirst ? fixedRow : starRow);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(fixedFirst ? starRow : fixedRow);
        var sp = Splitter(GridResizeDirection.Rows);
        Grid.SetRow(a, 0);
        Grid.SetRow(sp, 1);
        Grid.SetRow(b, 2);
        grid.Children.Add(a);
        grid.Children.Add(sp);
        grid.Children.Add(b);
        return (grid, fixedRow, sp);
    }

    /// <summary>고정 칸 크기를 나눔 전체 크기 안으로 맞춰 정한다(아직 배치 전이면 그대로).</summary>
    private static double ClampSplit(double value, double total)
    {
        double max = total - MinPanelSize - SplitterSize;
        return max <= MinPanelSize ? Math.Max(MinPanelSize, value) : Math.Clamp(value, MinPanelSize, max);
    }

    private static void SetSplit(ColumnDefinition col, Grid grid, int width)
        => col.Width = new GridLength(ClampSplit(width, grid.Bounds.Width));

    private static void SetSplit(RowDefinition row, Grid grid, int height)
        => row.Height = new GridLength(ClampSplit(height, grid.Bounds.Height));

    private static TextBlock StatusLabel(DockPanel host, int width)
    {
        var text = new TextBlock
        {
            Foreground = AppTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var cell = new Border
        {
            Width = width,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(4, 0, 4, 0),
            Child = text,
        };
        DockPanel.SetDock(cell, Dock.Left);
        host.Children.Add(cell);
        return text;
    }

    internal static Image IconImage(Bitmap bitmap, double size)
    {
        var img = new Image { Source = bitmap, Width = size, Height = size, Stretch = Stretch.Uniform };
        // 원래 크기 이상은 점 그대로, 메뉴처럼 줄일 때는 부드럽게(점이 빠지지 않게).
        RenderOptions.SetBitmapInterpolationMode(img, size >= bitmap.PixelSize.Width ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        return img;
    }

    /// <summary>왼쪽 끝 세로 도구 막대(점 그림 아이콘, 도움말에 단축키). 아래 끝에는 저장·되돌리기 단추가 붙어 있고 도구 칸만 스크롤된다.</summary>
    private Control BuildToolBar()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 1 };
        _tbFiles = ToolBarButton(Icons.Folder, MainStrings.Ui_036);
        _tbFiles.Margin = new Thickness(1, 1, 1, 6);
        _tbFiles.Click += (_, _) => SetFilesVisible(!_filesShown, animate: true);
        stack.Children.Add(_tbFiles);
        foreach (var slotTools in ToolSlots)
        {
            var slot = new ToolSlotButton(slotTools.Select(t => ToolDefs.First(d => d.Tool == t)).Select(d => (d.Tool, d.Name)).ToList(), ToolKeyText);
            slot.ToolPicked += t => { _ctx.Tool = t; UpdateToolUi(); _canvas.Focus(); };
            _toolSlots.Add(slot);
            stack.Children.Add(slot);
        }
        _tbSave = ToolBarAction(Icons.Save, MainStrings.Ui_037, () => Save());
        _tbRevert = ToolBarAction(Icons.Revert, MainStrings.Ui_038, RevertToSaved);
        var actions = new StackPanel { Orientation = Orientation.Vertical, Spacing = 1, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(_tbSave);
        actions.Children.Add(_tbRevert);
        var bar = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Bottom);
        bar.Children.Add(actions);
        // 32px 아이콘 칸 12개(도형·선택은 묶음) — 창이 낮으면 스크롤된다.
        bar.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(2, 4, 2, 4),
            Width = Icons.Size + 14,
            Child = bar,
        };
    }

    private static ToggleButton ToolBarButton(Bitmap icon, string tip) => new ToggleButton
    {
        Content = IconImage(icon, Icons.Size),
        Padding = new Thickness(2),
        Margin = new Thickness(1),
        MinWidth = 0,
        MinHeight = 0,
        HorizontalAlignment = HorizontalAlignment.Center,
        Focusable = false,
    }.Tip(tip);

    /// <summary>도구 막대 아래 명령 단추(저장·되돌리기). 켜고 끄기는 <see cref="SetToolBarActionEnabled"/>(꺼지면 아이콘이 흐려짐).</summary>
    private static Button ToolBarAction(Bitmap icon, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = IconImage(icon, Icons.Size),
            Padding = new Thickness(2),
            Margin = new Thickness(1),
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Focusable = false,
        }.Tip(tip);
        ToolTip.SetShowOnDisabled(b, true);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void SetToolBarActionEnabled(Button b, bool on)
    {
        b.IsEnabled = on;
        if (b.Content is Image icon) icon.Opacity = on ? 1 : 0.35;
    }

    /// <summary>메뉴 아래 막대: 현재 도구 이름·도움말(세부 설정은 오른쪽 독 "도구 설정") + 보기 옵션·확대율.</summary>
    private Control BuildContextBar()
    {
        var bar = new DockPanel { LastChildFill = true };

        // 오른쪽 정렬: (새 버전 알림) 보기·확대
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        DockPanel.SetDock(right, Dock.Right);
        _cbUpdate = BarButton(right, MainStrings.Ui_039, MainStrings.Ui_040, () =>
        {
            if (_updater.Available is { } release) _updater.ShowUpdateDialog(release);
        });
        _cbUpdate.Background = AppTheme.AccentBrush;
        _cbUpdate.Foreground = Brushes.White;
        _cbUpdate.FontWeight = FontWeight.Bold;
        _cbUpdate.Margin = new Thickness(1, 1, 6, 1);
        _cbUpdate.IsVisible = false;
        _cbGrid = Toggle(right, MainStrings.Ui_041, MainStrings.Ui_042, () => SetPixelGrid(!_canvas.ShowPixelGrid));
        _cbTiles = Toggle(right, MainStrings.Ui_043, MainStrings.Ui_044, () => SetTileGrid(!_canvas.ShowTileGrid));
        _cbTiled = Toggle(right, MainStrings.Ui_045, MainStrings.Ui_046, () => SetTiledMode(!_canvas.TiledMode));
        _cbPivot = Toggle(right, MainStrings.Ui_047, MainStrings.Ui_048, () => SetShowPivot(!_canvas.ShowPivot));
        _cbOnion = Toggle(right, MainStrings.Ui_049, MainStrings.Ui_050, () => _ctx.OnionSkin = !_ctx.OnionSkin);
        // 우클릭 = 그 보기의 설정 팝업(바꾸면 바로 적용, 꺼져 있으면 켬)
        foreach (var (button, flyout) in new (ToggleButton, Func<Flyout>)[] { (_cbGrid, PixelGridFlyout), (_cbTiles, TileGridFlyout), (_cbTiled, TiledModeFlyout), (_cbOnion, OnionFlyout) })
            button.ContextRequested += (_, e) =>
            {
                e.Handled = true;
                PopupMenus.Show(button, flyout());
            };
        right.Children.Add(BarSeparator());
        BarButton(right, BarIcon(VectorIcon.Minus), MainStrings.Ui_051, () => _canvas.ZoomStep(-1));
        right.Children.Add(_cbZoom = ZoomBox());
        BarButton(right, BarIcon(VectorIcon.Plus), MainStrings.Ui_052, () => _canvas.ZoomStep(1));
        _cbFit = Toggle(right, MainStrings.Ui_053, MainStrings.Ui_054, () =>
        {
            _canvas.AutoFit = !_canvas.AutoFit;
            if (_canvas.AutoFit) _canvas.FitToWindow();
        });
        right.Children.Add(BarSeparator());
        var claude = BarButton(right, "", MainStrings.Ui_055, ToggleChat);
        claude.Content = IconImage(Icons.Claude, 16);
        claude.Padding = new Thickness(5, 3);
        bar.Children.Add(right);

        _cbToolName = new TextBlock
        {
            Text = MainStrings.Ui_056,
            FontWeight = FontWeight.Bold,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AddLeft(bar, _cbToolName);
        AddLeft(bar, BarSeparator());
        _cbHint = new TextBlock
        {
            Foreground = AppTheme.DimTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 6, 0),
        };
        bar.Children.Add(_cbHint);

        return new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 2, 6, 2),
            Child = bar,
        };
    }

    /// <summary>
    /// 확대율 숫자 상자(100%~6400%, 캔버스 배율은 정수 배라 가까운 배율로 맞춤): 클릭하면 직접 입력, 좌우로 끌면 한 배씩,
    /// 우클릭하면 "화면에 맞추기"와 대표 배율 목록(공통 숫자 상자 메뉴).
    /// </summary>
    private NumericUpDown ZoomBox()
    {
        var box = Ui.Num(100, 6400, _canvas.Zoom * 100, 70, increment: 100);
        box.Margin = new Thickness(1);
        box.TextConverter = new PercentConverter();
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Tip(MainStrings.Ui_057);
        box.Presets([100, 200, 400, 800, 1600, 3200, 6400],
            [new NumberAction(MainStrings.Ui_058, () => _canvas.FitToWindow(), new KeyGesture(Key.D0, Ctrl))],
            v => $"{v:0}%");
        box.ValueChanged += (_, _) =>
        {
            if (_syncingZoom || box.Value is not { } v) return;
            _canvas.SetZoom((int)Math.Round(v / 100m, MidpointRounding.AwayFromZero));
            // 정수 배가 아니면(예: 150%) 실제 배율로 고쳐 보인다.
            if (v != _canvas.Zoom * 100) Dispatcher.UIThread.Post(UpdateToolUi);
        };
        return box;
    }

    /// <summary>
    /// 숫자 상자 글자 변환(NumericUpDown.TextConverter는 Convert = 글자 → 값, ConvertBack = 값 → 글자): "1600%" ↔ 1600, 입력 때 % 기호는 있어도 없어도 됨.
    /// </summary>
    private sealed class PercentConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => decimal.TryParse((value as string)?.Replace("%", "").Trim(), System.Globalization.NumberStyles.Number, culture, out var d) ? d : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is decimal d ? $"{d:0}%" : "";
    }

    private static void AddLeft(DockPanel bar, Control c)
    {
        DockPanel.SetDock(c, Dock.Left);
        c.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(c);
    }

    private static Border BarSeparator() => new()
    {
        Width = 1,
        Background = AppTheme.BorderBrush,
        Margin = new Thickness(5, 3, 5, 3),
    };

    /// <summary>켜고 끄는 글자 단추. 눌린 상태는 <see cref="UpdateToolUi"/>가 실제 값으로 맞춘다.</summary>
    private ToggleButton Toggle(Panel host, string text, string tip, Action onClick)
    {
        var b = new ToggleButton
        {
            Content = text,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(1),
            MinHeight = 0,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        }.Tip(tip);
        b.Click += (_, _) => { onClick(); UpdateToolUi(); };
        host.Children.Add(b);
        return b;
    }

    /// <summary>위쪽 막대 단추 안 아이콘(글자 단추와 같은 높이).</summary>
    private static IconView BarIcon(VectorIcon icon)
    {
        var v = Ui.Icon(icon, 12);
        v.Margin = new Thickness(0, 2);
        return v;
    }

    private static Button BarButton(Panel host, object content, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(1),
            MinHeight = 0,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        }.Tip(tip);
        b.Click += (_, _) => onClick();
        host.Children.Add(b);
        return b;
    }

    private Menu BuildMenu()
    {
        var menu = new Menu { Background = AppTheme.PanelBrush };

        // 파일
        var file = TopMenu("파일(_F)", MainStrings.Menu_File);
        _miRecentFolders = new MenuItem { Header = MenuHeader(MainStrings.Ui_061, null) };
        _miRecentFiles = new MenuItem { Header = MenuHeader(MainStrings.Ui_062, null) };
        _miRecentFolders.SubmenuOpened += (_, _) => FillRecent(_miRecentFolders, _settings.RecentFolders, SetRootFolder);
        _miRecentFiles.SubmenuOpened += (_, _) => FillRecent(_miRecentFiles, _settings.RecentFiles, p => OpenFile(p));
        _miRecentFolders.Items.Add(EmptyItem());
        _miRecentFiles.Items.Add(EmptyItem());
        AddItems(file,
            Cmd("새 프로젝트…", MainStrings.Menu_File_NewProject_063, () => NewProject(null), "Ctrl+N", KeyScope.Global, (Key.N, Ctrl)),
            Cmd("열기(.gpixel / PNG 가져오기)…", MainStrings.Menu_File_OpenFileDialogCmd_065, OpenFileDialogCmd, "Ctrl+O", KeyScope.Global, (Key.O, Ctrl)),
            Cmd("폴더 열기…", MainStrings.Menu_File_OpenFolder_067, OpenFolder, "Ctrl+Shift+O", KeyScope.Global, (Key.O, Ctrl | Shift)),
            Cmd("Unity 프로젝트 열기…", MainStrings.Menu_File_OpenUnityProject_069, OpenUnityProject, "Ctrl+Shift+U", KeyScope.Global, (Key.U, Ctrl | Shift)),
            _miRecentFolders,
            _miRecentFiles,
            new Separator(),
            DocCmd("저장(+ 시트 내보내기)", MainStrings.Menu_File_Save_071, () => Save(), "Ctrl+S", KeyScope.Global, (Key.S, Ctrl)),
            DocCmd("다른 이름으로 저장…", MainStrings.Menu_File_SaveAs_073, () => SaveAs(), "Ctrl+Shift+S", KeyScope.Global, (Key.S, Ctrl | Shift)),
            _miRevert = Cmd("저장한 내용으로 되돌리기", MainStrings.Menu_File_Command_075, RevertToSaved),
            DocCmd("내보내기…", MainStrings.Menu_File_OpenExportDialog_077, OpenExportDialog, "Ctrl+E", KeyScope.Global, (Key.E, Ctrl)),
            DocCmd("GIF 애니메이션으로 내보내기…", MainStrings.Menu_File_Command_079, ExportGif),
            DocCmd("닫기", MainStrings.Menu_File_CloseProject_081, CloseActiveTab, "Ctrl+W", KeyScope.Global, (Key.W, Ctrl)),
            new Separator(),
            Cmd("시작 화면", MainStrings.Menu_File_Command_083, ShowStart),
            // 끝내기 단축키는 OS가 처리한다(Windows Alt+F4, macOS 앱 메뉴의 ⌘Q).
            Cmd("끝내기", MainStrings.Menu_File_Close_085, Close, Platform.IsMac ? "Ctrl+Q" : "Alt+F4"));

        // 편집
        var edit = TopMenu("편집(_E)", MainStrings.Menu_Edit);
        _miUndo = Cmd("실행취소", MainStrings.Menu_Edit_Undo_089, Undo, "Ctrl+Z", KeyScope.NotInText, (Key.Z, Ctrl));
        _miRedo = Cmd("다시 실행", MainStrings.Menu_Edit_Redo_091, Redo, "Ctrl+Y", KeyScope.NotInText, (Key.Y, Ctrl), (Key.Z, Ctrl | Shift));
        (Key, KeyModifiers)[] deleteKeys = Platform.IsMac ? [(Key.Delete, NoMods), (Key.Back, NoMods)] : [(Key.Delete, NoMods)];
        AddItems(edit,
            _miUndo,
            _miRedo,
            new Separator(),
            DocCmd("잘라내기", MainStrings.Menu_Edit_Cut_093, Cut, "Ctrl+X", KeyScope.NotInText, (Key.X, Ctrl)),
            DocCmd("복사", MainStrings.Menu_Edit_Copy_095, Copy, "Ctrl+C", KeyScope.NotInText, (Key.C, Ctrl)),
            DocCmd("붙여넣기", MainStrings.Menu_Edit_Paste_097, Paste, "Ctrl+V", KeyScope.NotInText, (Key.V, Ctrl)),
            DocCmd("지우기", MainStrings.Menu_Edit_DeleteSelection_099, DeleteSelection, "Delete", KeyScope.Canvas, deleteKeys),
            new Separator(),
            DocCmd("모두 선택", MainStrings.Menu_Edit_SelectAll_101, SelectAll, "Ctrl+A", KeyScope.NotInText, (Key.A, Ctrl)),
            DocCmd("선택 해제", MainStrings.Menu_Edit_Deselect_103, Deselect, "Ctrl+D", KeyScope.NotInText, (Key.D, Ctrl)),
            new Separator(),
            DocCmd("좌우 뒤집기(선택/셀)", MainStrings.Menu_Edit_Flip_105, () => Flip(true), "Shift+H", KeyScope.NotInText, (Key.H, Shift)),
            DocCmd("상하 뒤집기(선택/셀)", MainStrings.Menu_Edit_Flip_107, () => Flip(false), "Shift+V", KeyScope.NotInText, (Key.V, Shift)),
            DocCmd("90° 회전(선택/캔버스)", MainStrings.Menu_Edit_Rotate_109, () => Rotate(true), "Ctrl+R", KeyScope.NotInText, (Key.R, Ctrl)),
            DocCmd("-90° 회전(선택/캔버스)", MainStrings.Menu_Edit_Rotate_111, () => Rotate(false), "Ctrl+Shift+R", KeyScope.NotInText, (Key.R, Ctrl | Shift)),
            DocCmd("변형(회전·크기)…", MainStrings.Menu_Edit_BeginTransform_113, BeginTransform, "Ctrl+T", KeyScope.NotInText, (Key.T, Ctrl)),
            new Separator(),
            DocCmd("외곽선 추가(기본색)", MainStrings.Menu_Edit_AddOutline_115, () => AddOutline(false), "Ctrl+Alt+O", KeyScope.NotInText, (Key.O, Ctrl | Alt)),
            DocCmd("외곽선 추가(대각 포함)", MainStrings.Menu_Edit_AddOutline_117, () => AddOutline(true)),
            DocCmd("색 바꾸기…", MainStrings.Menu_Edit_ReplaceColor_119, ReplaceColor, "Ctrl+Alt+R", KeyScope.NotInText, (Key.R, Ctrl | Alt)),
            new Separator(),
            DocCmd("캔버스 크기…", MainStrings.Menu_Edit_BeginCanvasResize_121, BeginCanvasResize, "Ctrl+Alt+C", KeyScope.Global, (Key.C, Ctrl | Alt)),
            DocCmd("캔버스 전체 좌우 뒤집기(모든 프레임)", MainStrings.Menu_Edit_Structural_123, () => _ctx.Structural(() => _ctx.Project!.FlipCanvasHorizontal())),
            DocCmd("캔버스 전체 상하 뒤집기(모든 프레임)", MainStrings.Menu_Edit_Structural_125, () => _ctx.Structural(() => _ctx.Project!.FlipCanvasVertical())),
            DocCmd("이미지에서 팔레트 만들기", MainStrings.Menu_Edit_Command_127, _colors.PaletteFromImage),
            DocCmd("팔레트 색으로 변환…", MainStrings.Menu_Edit_Command_129, _colors.ConvertImageToActivePalette),
            new Separator(),
            Cmd("환경 설정…", MainStrings.Menu_Edit_ShowPreferences_131, ShowPreferences, "Ctrl+,", KeyScope.Global, (Key.OemComma, Ctrl)));
        Bind("편집", "선택 내려놓기", MainStrings.ShortcutCategory_Edit, MainStrings.Shortcut_CommitSelection, KeyScope.Canvas, () =>
        {
            bool transforming = _ctx.IsTransforming;
            if (_canvas.CommitCurve()) return true;
            _ctx.CommitFloating();
            if (transforming) ShowMessage(MainStrings.Message_133);
            return true;
        }, (Key.Enter, NoMods));
        Bind("편집", "선택 해제(캔버스)", MainStrings.ShortcutCategory_Edit, MainStrings.Shortcut_DeselectCanvas, KeyScope.Canvas, () =>
        {
            if (_canvas.CancelCurve()) return true;
            if (!CancelMode()) Deselect();
            return true;
        }, (Key.Escape, NoMods));
        foreach (var (k, dx, dy, dir, displayDir) in new[] { (Key.Left, -1, 0, "왼쪽으로", MainStrings.Ui_134), (Key.Right, 1, 0, "오른쪽으로", MainStrings.Ui_135), (Key.Up, 0, -1, "위로", MainStrings.Ui_136), (Key.Down, 0, 1, "아래로", MainStrings.Ui_137) })
        {
            Bind("편집", $"1px {dir} 옮기기", MainStrings.ShortcutCategory_Edit, MainStrings.FormatShortcut_NudgeOne(direction: displayDir), KeyScope.Canvas, () => _canvas.Nudge(dx, dy), (k, NoMods), (k, Ctrl | Shift));
            Bind("편집", $"8px {dir} 옮기기", MainStrings.ShortcutCategory_Edit, MainStrings.FormatShortcut_NudgeEight(direction: displayDir), KeyScope.Canvas, () => _canvas.Nudge(dx * 8, dy * 8), (k, Shift));
        }

        // 보기
        var view = TopMenu("보기(_V)", MainStrings.Menu_View);
        _miGrid = Checkable(Cmd("픽셀 격자", MainStrings.Menu_View_SetPixelGrid_140, () => SetPixelGrid(!_canvas.ShowPixelGrid), "Ctrl+G", KeyScope.Global, (Key.G, Ctrl)));
        _miTiles = Checkable(Cmd("타일 격자", MainStrings.Menu_View_SetTileGrid_142, () => SetTileGrid(!_canvas.ShowTileGrid), "Ctrl+Shift+G", KeyScope.Global, (Key.G, Ctrl | Shift)));
        _miTiled = Checkable(Cmd("타일 모드(반복해 보기)", MainStrings.Menu_View_SetTiledMode_144, () => SetTiledMode(!_canvas.TiledMode), "Ctrl+Shift+T", KeyScope.Global, (Key.T, Ctrl | Shift)));
        _miPivot = Checkable(Cmd("피벗 표시", MainStrings.Menu_View_SetShowPivot_146, () => SetShowPivot(!_canvas.ShowPivot)));
        _miOnion = Checkable(DocCmd("양파 껍질", MainStrings.Menu_View_OnionSkin_148, () => _ctx.OnionSkin = !_ctx.OnionSkin, "Ctrl+J", KeyScope.Global, (Key.J, Ctrl)));
        _miFiles = Checkable(Cmd("파일 브라우저", MainStrings.Menu_View_SetFilesVisible_150, () => SetFilesVisible(!_filesShown, animate: true), "F8", KeyScope.Global, (Key.F8, NoMods)));
        AddItems(view,
            Cmd("확대", MainStrings.Menu_View_ZoomStep_152, () => _canvas.ZoomStep(1), "Ctrl+=", KeyScope.NotInText, (Key.OemPlus, Ctrl), (Key.Add, Ctrl)),
            Cmd("축소", MainStrings.Menu_View_ZoomStep_154, () => _canvas.ZoomStep(-1), "Ctrl+-", KeyScope.NotInText, (Key.OemMinus, Ctrl), (Key.Subtract, Ctrl)),
            Cmd("화면에 맞추기", MainStrings.Menu_View_FitToWindow_156, _canvas.FitToWindow, "Ctrl+0", KeyScope.Global, (Key.D0, Ctrl), (Key.NumPad0, Ctrl)),
            Cmd("실제 크기(1배)", MainStrings.Menu_View_SetZoom_158, () => _canvas.SetZoom(1), "Ctrl+1", KeyScope.Global, (Key.D1, Ctrl), (Key.NumPad1, Ctrl)),
            new Separator(),
            _miGrid, _miTiles, _miTiled, _miPivot, _miOnion,
            Cmd("격자 설정…", MainStrings.Menu_View_Command_160, GridSettings),
            new Separator(),
            DocCmd("다음 탭", MainStrings.Tabs_Next, () => CycleTab(1), "Ctrl+Tab", KeyScope.Global, (Key.Tab, Ctrl)),
            DocCmd("이전 탭", MainStrings.Tabs_Previous, () => CycleTab(-1), "Ctrl+Shift+Tab", KeyScope.Global, (Key.Tab, Ctrl | Shift)),
            WithIcon(DocCmd("다른 쪽으로 옮기기", MainStrings.Tabs_MoveToOther, () => { if (_activeTab is { } t) MoveTabToOtherPane(t); }), VectorIcon.Columns2),
            new Separator(),
            _miFiles,
            new Separator(),
            _miPlayTest = DocCmd("움직임 테스트", MainStrings.Menu_Test_ShowPlayTest_224, ShowPlayTest, "F5", KeyScope.Global, (Key.F5, NoMods)));
        // 누르고 있는 동안 이전 프레임 보기(키를 떼면 캔버스가 끈다 — PixelCanvas.PeekKey)
        Bind("보기", "이전 프레임 보기(누르고 있기)", MainStrings.ShortcutCategory_View, MainStrings.Shortcut_PeekPrevious, KeyScope.NotInText, () => { if (_ctx.HasProject) _canvas.PeekPrevious = true; return true; }, (Key.H, NoMods));

        // 프레임·레이어
        var frame = TopMenu("프레임·레이어(_R)", MainStrings.Menu_FrameLayer);
        AddItems(frame,
            DocCmd("재생 / 정지", MainStrings.Menu_FrameLayer_TogglePlay_164, _preview.TogglePlay, "Shift+Enter", KeyScope.Global, (Key.Enter, Shift)),
            DocCmd("이전 프레임", MainStrings.Menu_FrameLayer_StepFrame_166, () => _timeline.StepFrame(-1), ",", KeyScope.NotInText, (Key.OemComma, NoMods)),
            DocCmd("다음 프레임", MainStrings.Menu_FrameLayer_StepFrame_168, () => _timeline.StepFrame(1), ".", KeyScope.NotInText, (Key.OemPeriod, NoMods)),
            DocCmd("첫 프레임", MainStrings.Menu_FrameLayer_SetCurrentFrame_170, () => _ctx.SetCurrentFrame(0), "Home", KeyScope.Canvas, (Key.Home, NoMods)),
            DocCmd("마지막 프레임", MainStrings.Menu_FrameLayer_SetCurrentFrame_172, () => _ctx.SetCurrentFrame(_ctx.FrameCount - 1), "End", KeyScope.Canvas, (Key.End, NoMods)),
            new Separator(),
            DocCmd("새 빈 프레임", MainStrings.Menu_FrameLayer_NewEmptyFrame_174, _timeline.NewEmptyFrame, "Alt+N", KeyScope.Global, (Key.N, Alt)),
            DocCmd("프레임 복제", MainStrings.Menu_FrameLayer_DuplicateFrame_176, _timeline.DuplicateFrame, "Alt+D", KeyScope.Global, (Key.D, Alt)),
            DocCmd("프레임 삭제", MainStrings.Menu_FrameLayer_Command_178, _timeline.DeleteFrame),
            new Separator(),
            DocCmd("새 레이어", MainStrings.Menu_FrameLayer_AddLayer_180, _timeline.AddLayer, "Shift+N", KeyScope.NotInText, (Key.N, Shift)),
            DocCmd("새 그룹", MainStrings.Menu_FrameLayer_AddGroup_182, _timeline.AddGroup, "Ctrl+Shift+N", KeyScope.Global, (Key.N, Ctrl | Shift)),
            DocCmd("새 부위…", MainStrings.Menu_FrameLayer_AddPart_184, _timeline.AddPart, "Shift+P", KeyScope.NotInText, (Key.P, Shift)),
            DocCmd("부위 크기…", MainStrings.Menu_FrameLayer_Command_186, _timeline.ResizePart),
            DocCmd("레이어 이름 바꾸기", MainStrings.Menu_FrameLayer_BeginRenameLayer_188, _timeline.BeginRenameLayer, "F2", KeyScope.NotInText, (Key.F2, NoMods)),
            DocCmd("이전 스킨(현재 그룹)", MainStrings.Menu_FrameLayer_StepSkin_190, () => StepSkin(-1), "Alt+[", KeyScope.NotInText, (Key.OemOpenBrackets, Alt)),
            DocCmd("다음 스킨(현재 그룹)", MainStrings.Menu_FrameLayer_StepSkin_192, () => StepSkin(1), "Alt+]", KeyScope.NotInText, (Key.OemCloseBrackets, Alt)),
            DocCmd("스킨 관리…", MainStrings.Menu_FrameLayer_OpenSkinManager_194, () => _timeline.OpenSkinManager(null)),
            new Separator(),
            DocCmd("새 태그(애니메이션)…", MainStrings.Menu_FrameLayer_Command_196, _timeline.NewTag),
            new Separator(),
            DocCmd("격자로 나눠 프레임 만들기(시트 → 프레임)…", MainStrings.Menu_FrameLayer_Command_198, SplitIntoFrames));

        // 도구
        var tools = TopMenu("도구(_T)", MainStrings.Menu_Tools);
        foreach (var (tool, id, name, key, _) in ToolDefs)
        {
            var t = tool;
            // 이미 고른 도구를 다시 눌러도 메뉴 체크가 풀린 채로 남지 않게 UI를 다시 맞춘다.
            (Key, KeyModifiers)[] keys = key.Length > 0 ? [(Enum.Parse<Key>(key), NoMods)] : [];
            var mi = Checkable(Cmd(id, name, () => { _ctx.Tool = t; UpdateToolUi(); }, null, KeyScope.NotInText, keys));
            mi.Icon = IconImage(Icons.For(tool), 16);
            _toolMenuItems[tool] = mi;
            tools.Items.Add(mi);
        }
        // M = 선택 칸: 칸의 지금 선택 도구, 이미 선택 도구면 다음 선택 도구.
        Bind("도구", SelectCycleName, MainStrings.ShortcutCategory_Tools, MainStrings.Shortcut_CycleSelection, KeyScope.NotInText, () => { CycleSelectionTool(); return true; }, (Key.M, NoMods));
        // L = 도형 도구의 선(예전 선 도구 자리, 같은 명령 이름이라 바꾼 키도 이어짐).
        Bind("도구", "선", MainStrings.ShortcutCategory_Tools, MainStrings.Shortcut_ShapeLine, KeyScope.NotInText,
            () => { _ctx.Tool = ToolKind.Shape; _ctx.ShapeKind = ShapeKind.Line; UpdateToolUi(); return true; }, (Key.L, NoMods));
        _miFilled = Checkable(Cmd("도형 채움", MainStrings.Menu_Tools_ShapeFilled_202, () => _ctx.ShapeFilled = !_ctx.ShapeFilled, "U", KeyScope.NotInText, (Key.U, NoMods)));
        _miGlobalFill = Checkable(Cmd("채우기: 같은 색 전체", MainStrings.Menu_Tools_FillContiguous_204, () => _ctx.FillContiguous = !_ctx.FillContiguous, "Shift+G", KeyScope.NotInText, (Key.G, Shift)));
        _miPixelPerfect = Checkable(Cmd("픽셀 퍼펙트", MainStrings.Menu_Tools_PixelPerfect_206, () => _ctx.PixelPerfect = !_ctx.PixelPerfect, "P", KeyScope.NotInText, (Key.P, NoMods)));
        _miMirrorX = Checkable(Cmd("좌우 대칭 그리기", MainStrings.Menu_Tools_MirrorX_208, () => _ctx.MirrorX = !_ctx.MirrorX, "Shift+X", KeyScope.NotInText, (Key.X, Shift)));
        _miMirrorY = Checkable(Cmd("상하 대칭 그리기", MainStrings.Menu_Tools_MirrorY_210, () => _ctx.MirrorY = !_ctx.MirrorY, "Shift+Y", KeyScope.NotInText, (Key.Y, Shift)));
        _miRightErase = Checkable(Cmd("오른쪽 버튼 = 지우개", MainStrings.Menu_Tools_RightButtonErases_212, () => _ctx.RightButtonErases = !_ctx.RightButtonErases));
        _miWrap = Checkable(Cmd("이동: 순환", MainStrings.Menu_Tools_MoveWrap_214, () => _ctx.MoveWrap = !_ctx.MoveWrap));
        AddItems(tools,
            new Separator(),
            _miFilled, _miGlobalFill, _miPixelPerfect, _miMirrorX, _miMirrorY, _miRightErase, _miWrap,
            new Separator(),
            Cmd("브러시 크게", MainStrings.Menu_Tools_BrushSize_216, () => _ctx.BrushSize++, "]", KeyScope.NotInText, (Key.OemCloseBrackets, NoMods)),
            Cmd("브러시 작게", MainStrings.Menu_Tools_BrushSize_218, () => _ctx.BrushSize--, "[", KeyScope.NotInText, (Key.OemOpenBrackets, NoMods)),
            Cmd("기본색 ↔ 보조색", MainStrings.Menu_Tools_SwapColors_220, _ctx.SwapColors, "X", KeyScope.NotInText, (Key.X, NoMods)));
        // 프리셋 종류가 늘어도 한 창의 탭으로 모은다.
        AddItems(tools, new Separator(), Cmd("프리셋", UiStrings.Presets_Menu, () => PresetsWindow.Open(this)),
            WithIcon(Cmd("에셋 관리자", UiStrings.Assets_Menu, () => AssetManagerWindow.Open(this)), VectorIcon.Package));


        // AI
        var ai = TopMenu("AI(_A)", MainStrings.Menu_Category_Ai);
        var refs = Cmd("참고 자료", MainStrings.Menu_Ai_References, ShowReferences);
        refs.Icon = Ui.Icon(VectorIcon.BookImage, 14);
        AddItems(ai,
            Cmd("AI 채팅", MainStrings.Menu_Ai_ToggleChat_226, ToggleChat, "Ctrl+L", KeyScope.Global, (Key.L, Ctrl)),
            WithIcon(Cmd("AI 상태 열기…", UiStrings.AiStatus_OpenMenu, () => ShowAiStatus(this)), VectorIcon.Info),
            refs,
            new Separator(),
            WithIcon(Cmd("AI 그림 픽셀로 정리…", MainStrings.Menu_Ai_Pixelate, () => ShowPixelate(null, 1)), VectorIcon.Grid3x3),
            WithIcon(DocCmd("현재 레이어를 픽셀로 정리…", MainStrings.Menu_Ai_PixelateLayer, PixelateCurrentLayer), VectorIcon.Grid3x3));

        // 도움말
        var help = TopMenu("도움말(_H)", MainStrings.Menu_Help);
        var website = Cmd("홈페이지", MainStrings.Menu_Help_Website, () => Updater.OpenUrl(this, AppInfo.LocalizedWebsiteUrl));
        website.Icon = Ui.Icon(VectorIcon.Globe, 14);
        var discord = Cmd("Discord에 제보·아이디어", MainStrings.Menu_Help_Discord, () => Updater.OpenUrl(this, AppInfo.DiscordUrl));
        // 로고와 글자를 Discord 색으로(머리글은 단축키를 바꿀 때 다시 만들어지므로 항목 글자색으로 물려준다)
        discord.Foreground = AppTheme.DiscordBrush;
        discord.Icon = new IconView(VectorIcon.Discord, AppTheme.DiscordBrush) { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        AddItems(help,
            Cmd("사용 가이드", MainStrings.Menu_Help_Guide, () => HelpWindow.Open(this), "F1", KeyScope.Global, (Key.F1, NoMods)),
            website,
            Cmd("단축키 목록", MainStrings.Menu_Help_ShowShortcuts_230, ShowShortcuts),
            Cmd("설정 파일 위치 열기", MainStrings.Menu_Help_Command_232, OpenSettingsFolder),
            Cmd("오류 로그 폴더 열기", MainStrings.Menu_Help_Command_234, OpenLogFolder),
            discord,
            new Separator(),
            Cmd("업데이트 확인…", MainStrings.Menu_Help_CheckAsync_236, () => _ = _updater.CheckAsync(manual: true)),
            Cmd("Gpixel 정보…", MainStrings.Menu_Help_AboutDialog_238, () => new AboutDialog().ShowDialog(this)));

        foreach (var top in new[] { file, edit, view, frame, tools, ai, help }) menu.Items.Add(top);
        return menu;
    }

    /// <summary>위 메뉴. 이어서 만드는 명령의 단축키 분류(예: "편집")가 된다.</summary>
    private MenuItem TopMenu(string id, string text)
    {
        _cmdCategory = id[..(id.IndexOf('(') is var i and > 0 ? i : id.Length)];
        _cmdCategoryDisplay = _cmdCategory switch
        {
            "파일" => MainStrings.ShortcutCategory_File,
            "편집" => MainStrings.ShortcutCategory_Edit,
            "보기" => MainStrings.ShortcutCategory_View,
            "프레임·레이어" => MainStrings.ShortcutCategory_FrameLayer,
            "도구" => MainStrings.ShortcutCategory_Tools,
            "테스트" => MainStrings.ShortcutCategory_Test,
            "AI" => MainStrings.ShortcutCategory_Ai,
            "도움말" => MainStrings.ShortcutCategory_Help,
            _ => throw new InvalidOperationException(id),
        };
        return new MenuItem { Header = text };
    }

    private static void AddItems(MenuItem parent, params Control[] items)
    {
        foreach (var it in items) parent.Items.Add(it);
    }

    /// <summary>메뉴 글자 + 오른쪽 단축키 표기(OS 표기로 바꿈). 밑줄(_)이 접근 키로 바뀌지 않게 TextBlock으로 그린다.</summary>
    private static Grid MenuHeader(string text, string? keyText)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        if (keyText is not null)
        {
            var k = new TextBlock
            {
                Text = Ui.Shortcut(keyText),
                Foreground = AppTheme.DimTextBrush,
                Margin = new Thickness(24, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(k, 1);
            g.Children.Add(k);
        }
        return g;
    }

    private static void SetMenuText(MenuItem mi, string text)
    {
        if (mi.Header is Grid g && g.Children.Count > 0 && g.Children[0] is TextBlock t) t.Text = text;
        else mi.Header = text;
    }

    private static MenuItem Checkable(MenuItem mi)
    {
        mi.ToggleType = MenuItemToggleType.CheckBox;
        return mi;
    }

    private static MenuItem EmptyItem() => new() { Header = MenuHeader(MainStrings.Ui_240, null), IsEnabled = false };

    /// <summary>
    /// 메뉴 명령 + 단축키 등록(<see cref="Register"/>). 단축키 표기는 지금 단축키(사용자 설정)로 메뉴에 보이고,
    /// <paramref name="keyText"/>는 등록된 키가 없을 때만 보인다(OS가 처리하는 끝내기 등).
    /// </summary>
    private MenuItem Cmd(string id, string text, Action action, string? keyText = null, KeyScope scope = KeyScope.Global, params (Key, KeyModifiers)[] keys)
    {
        var mi = new MenuItem { Header = MenuHeader(text, keyText) };
        // 메뉴가 닫힌 뒤 실행한다(대화상자를 띄워도 메뉴가 남지 않게).
        mi.Click += (_, _) => Dispatcher.UIThread.Post(action);
        Register(_cmdCategory, id, _cmdCategoryDisplay, text, scope, () => { action(); return true; }, keys, mi, keys.Length == 0 ? keyText : null);
        return mi;
    }

    /// <summary>프로젝트가 있어야 쓸 수 있는 명령(없으면 비활성).</summary>
    private MenuItem DocCmd(string id, string text, Action action, string? keyText = null, KeyScope scope = KeyScope.Global, params (Key, KeyModifiers)[] keys)
    {
        var mi = Cmd(id, text, () => { if (_ctx.HasProject) action(); }, keyText, scope, keys);
        _docItems.Add(mi);
        return mi;
    }

    /// <summary>메뉴 없는 단축키 명령. <paramref name="action"/>이 false면 키를 처리하지 않은 것으로 본다.</summary>
    private void Bind(string category, string name, string categoryDisplay, string nameDisplay, KeyScope scope, Func<bool> action, params (Key, KeyModifiers)[] keys)
        => Register(category, name, categoryDisplay, nameDisplay, scope, action, keys, null, null);

    private void Register(string category, string name, string categoryDisplay, string nameDisplay, KeyScope scope, Func<bool> action, (Key, KeyModifiers)[] keys, MenuItem? item, string? fixedKeyText)
    {
        var id = $"{category}/{name}";
        if (_commands.Any(c => c.Info.Id == id)) throw new InvalidOperationException(MainStrings.FormatShortcut_DuplicateId(id: id));
        var defaults = keys.Select(k => new KeyChord(k.Item1, k.Item2)).ToArray();
        _commands.Add(new CommandEntry(new ShortcutCommand(id, categoryDisplay, nameDisplay, defaults), scope, action, item, fixedKeyText) { Keys = defaults.ToList() });
    }

    /// <summary>단축키를 바꿀 수 있는 명령(등록 순서 = 메뉴 순서).</summary>
    private sealed class CommandEntry(ShortcutCommand info, KeyScope scope, Func<bool> action, MenuItem? item, string? fixedKeyText)
    {
        public ShortcutCommand Info { get; } = info;
        public KeyScope Scope { get; } = scope;
        public Func<bool> Action { get; } = action;
        public MenuItem? Item { get; } = item;
        public string? FixedKeyText { get; } = fixedKeyText;
        public List<KeyChord> Keys { get; set; } = new();
    }

    /// <summary>
    /// 설정의 사용자 단축키(<see cref="AppSettings.Shortcuts"/>, 기본값과 다른 것만)를 적용한다: 키 표를 다시 만들고
    /// 메뉴·도구 막대·이전 프레임 보기 키를 맞춘다. 같은 키가 여럿이면 나중 명령이 이긴다(환경 설정 창은 겹치지 않게 함).
    /// </summary>
    private void ApplyShortcuts()
    {
        _keys.Clear();
        foreach (var c in _commands)
        {
            c.Keys = _settings.Shortcuts is { } map && map.TryGetValue(c.Info.Id, out var custom)
                ? custom.Select(Shortcuts.Parse).OfType<KeyChord>().Distinct().ToList()
                : c.Info.Defaults.ToList();
            foreach (var k in c.Keys) _keys[(k.Key, k.Mods)] = (c.Scope, c.Action);
            if (c.Item is { } mi)
                mi.Header = MenuHeader(MenuText(mi), c.Keys.Count > 0 ? Shortcuts.Format(c.Keys[0]) : c.FixedKeyText);
        }
        _canvas.PeekKey = Command("보기/이전 프레임 보기(누르고 있기)").Keys.Select(k => (Key?)k.Key).FirstOrDefault();
        HelpWindow.Shortcuts = id => _commands.FirstOrDefault(c => c.Info.Id == id)?.Keys;
        HelpWindow.RefreshShortcuts();
        foreach (var slot in _toolSlots) slot.RefreshTip();
        if (_tbFiles is not null) _tbFiles.Tip(KeyHint(MainStrings.Ui_241, "보기/파일 브라우저"));
        ApplyTabTips();
        UpdateToolUi();
    }

    private CommandEntry Command(string id) => _commands.First(c => c.Info.Id == id);

    /// <summary>메뉴 항목의 글자(단축키 표기 제외).</summary>
    private static string MenuText(MenuItem mi) => mi.Header is Grid g && g.Children.Count > 0 && g.Children[0] is TextBlock t ? t.Text ?? "" : mi.Header?.ToString() ?? "";

    /// <summary>명령의 첫 단축키 표기(없으면 빈 문자열).</summary>
    private string KeyText(string id) => _commands.FirstOrDefault(c => c.Info.Id == id)?.Keys is { Count: > 0 } k ? Shortcuts.Format(k[0]) : "";

    /// <summary>"설명 (단축키)" — 단축키가 없으면 설명만.</summary>
    private string KeyHint(string text, string id) => KeyText(id) is { Length: > 0 } k ? $"{text} ({k})" : text;

    private const string SelectCycleName = "선택 칸(다음 선택 도구)";

    /// <summary>도구 단축키 표기. 자기 키가 없는 선택 도구(사각·원)는 선택 칸 키(M).</summary>
    private string ToolKeyText(ToolKind tool)
    {
        var own = KeyText($"도구/{ToolDefs.First(d => d.Tool == tool).Id}");
        if (own.Length > 0 || !EditorContext.IsSelectionTool(tool)) return own;
        return KeyText($"도구/{SelectCycleName}");
    }

    private void FillRecent(MenuItem parent, List<string> list, Action<string> open)
    {
        parent.Items.Clear();
        foreach (var p in list)
        {
            var path = p;
            var mi = new MenuItem { Header = MenuHeader(p, null) };
            mi.Click += (_, _) => Dispatcher.UIThread.Post(() => open(path));
            parent.Items.Add(mi);
        }
        if (list.Count == 0) parent.Items.Add(EmptyItem());
        else
        {
            parent.Items.Add(new Separator());
            var clear = new MenuItem { Header = MenuHeader(MainStrings.Ui_242, null) };
            clear.Click += (_, _) => list.Clear();
            parent.Items.Add(clear);
        }
    }

    // ───────────── 이벤트 연결 ─────────────

    private void WireEvents()
    {
        _ctx.ProjectReplaced += (_, _) =>
        {
            _undoOrder.Track(_ctx.Project?.History);
            _play?.DocumentChanged();
            UpdateAll();
            SyncUnsaved();
        };
        _ctx.DirtyChanged += (_, _) =>
        {
            UpdateTitle();
            UpdateStatus();
        };
        _ctx.ProjectChanged += (_, e) =>
        {
            if ((e.Change & (ProjectChange.Canvas | ProjectChange.Path | ProjectChange.Frames | ProjectChange.Layers)) != 0) { UpdateTitle(); UpdateStatus(); }
            // 이름·경로가 바뀌면(다른 이름으로 저장 등) 탭 제목과 파일 목록 점도.
            if ((e.Change & ProjectChange.Path) != 0) { RefreshPanes(); SyncUnsaved(); }
            // 움직임 테스트 창은 편집이 잠시 멈추면 다시 읽는다(스트로크 중에는 미룸).
            if (_play is not null && !_ctx.Project!.IsStrokeActive)
            {
                _playRefresh.Stop();
                _playRefresh.Start();
            }
        };
        _playRefresh.Tick += (_, _) => { _playRefresh.Stop(); _play?.DocumentChanged(); };
        _ctx.CurrentChanged += (_, _) => { UpdateStatus(); UpdateToolUi(); };
        // 실행취소 기록이 바뀔 때마다(편집·실행취소·다시 실행, 문서 바꿈) 자동 저장 타이머를 처음부터 다시 잰다.
        _ctx.UndoChanged += (_, _) => { UpdateUndoItems(); RestartAutoSave(); };
        _autoSave.Tick += (_, _) => AutoSaveTick();
        _ctx.ToolChanged += (_, _) => UpdateToolUi();
        _ctx.ToolOptionsChanged += (_, _) => UpdateToolUi();
        _ctx.OnionSkinChanged += (_, _) => UpdateToolUi();
        // 캔버스 크기 모드를 적용하면(확인·Enter, 다른 편집 전 자동 적용) 새 크기를 화면에 맞춘다.
        _ctx.CanvasResized += (_, message) =>
        {
            _canvas.FitToWindow();
            ShowMessage(message);
        };

        _canvas.ZoomChanged += (_, _) => { _settings.Zoom = _canvas.Zoom; UpdateStatus(); };
        _canvas.HoverChanged += (_, p) => UpdateHover(p);
        _canvas.Message += (_, m) => ShowMessage(m);
        _timeline.Message += (_, m) => ShowMessage(m);
        // 변형(Ctrl+T)·이동 도구는 타임라인에서 고른 셀들을 함께 바꾼다.
        _ctx.SelectedCels = _timeline.SelectedCelTargets;
        _preview.Message += (_, m) => ShowMessage(m);
        _preview.CopyRequested += (_, img) =>
        {
            _ctx.Clipboard = img;
            PutOnSystemClipboard(img);
        };
        _preview.GifExportRequested += (_, _) => ExportGif();
        _preview.FileSaved += (_, path) => _browser.RefreshFile(path);

        _browser.SpriteDefaults = () => new SpriteSettings { PixelsPerUnit = _settings.LastPpu, Mode = SpriteMode.Multiple };
        _browser.FindOpenDocument = path => TryGetOpenDocument(path, out var doc) ? doc : null;
        _browser.TrashPolicy = () => _settings.TrashPolicy;
        _browser.TrashPolicyChanged += policy => _settings.TrashPolicy = policy;
        _browser.Message += (_, m) => ShowMessage(m);
        // 보관 기간이 지났거나 용량을 넘친 휴지통 항목은 시작할 때 영구 삭제한다.
        _browser.Trash.Purge(_settings.TrashPolicy);
        // 남긴 실행취소 기록도: 원래 파일이 없어졌거나 밖에서 바뀌었거나 오래 안 썼거나 용량을 넘친 것(백그라운드).
        _history.Cleanup();
        _browser.FileActivated += (_, path) =>
        {
            // 목록 선택 이벤트가 끝난 뒤 연다(대화상자·선택 되돌리기를 안전하게).
            Dispatcher.UIThread.Post(() => { if (!OpenFile(path)) _browser.SelectFile(CurrentTilePath); });
        };
        _browser.NewImageRequested += (_, folder) => NewProject(folder);
        _browser.PixelateRequested += (_, path) => PixelateFile(path);
        _browser.FileRenamed += (_, r) => OnFileRenamed(r.OldPath, r.NewPath);
        _browser.FileDeleted += (_, path) => OnFileDeleted(path);
        // 저장·열기 중(자동 저장 포함)에 온 파일 감시 알림은 끝난 뒤(문서의 디스크 시각을 갱신한 뒤)에 본다 — 자기 저장을 "밖에서 바뀜"으로 묻지 않게.
        _browser.FilesChanged += (_, paths) => OnFilesChangedWhenIdle(paths);

        _start.FolderChosen += (_, folder) => SetRootFolder(folder);
        _start.FileChosen += (_, file) => OpenFile(file);
        _start.OpenFolderRequested += (_, _) => OpenFolder();
        _start.NewImageRequested += (_, _) => NewProject(null);

        _colors.PalettesChanged += (_, _) => _settings.Palettes = _colors.Palettes.Select(PaletteSetting.From).ToList();
        _colors.PaletteEdited += (before, after) => _undoOrder.PushPalette(before, after);
        _undoOrder.Changed += (_, _) => UpdateUndoItems();
        _colors.Message += (_, m) => ShowMessage(m);

        // 모든 색 고르기 창: 팔레트·최근 색·스포이드(현재 프레임 합성).
        ColorPickerEditor.Sources = () => new ColorPickSources(
            _colors.Palettes, _ctx.RecentColors.ToList(),
            () => _ctx.Project is { } p ? p.Flatten(_ctx.CurrentFrame) : null,
            _colors.PickerMode, mode => _colors.PickerMode = mode);

        // 앱이 끝나는 예외에서 비상 저장할 수정된 문서(열린 모든 탭의 문서).
        CrashGuard.UnsavedProjects = () => OpenDocs.Where(d => d.Project.IsDirty).Select(d => d.Project);

        // 숫자 상자를 끄는 동안의 값 변화(프로젝트 설정 등)는 실행취소 한 단계로 묶는다. Claude 차례처럼 이미 열린 묶음이 있으면 그 안에 둔다.
        SpriteProject? scrubProject = null;
        NumberScrub.Started += (_, _) =>
        {
            if (_ctx.Project is not { } p || p.History.InGroup) return;
            p.History.BeginGroup(null);
            scrubProject = p;
        };
        NumberScrub.Ended += (_, _) =>
        {
            if (scrubProject is not { } p) return;
            scrubProject = null;
            p.History.EndGroup();
        };
    }

    // ───────────── 설정 ─────────────

    private void ApplySettingsBeforeShow()
    {
        var s = _settings;
        _ctx.UndoCapacity = s.UndoSteps;
        ApplyShortcuts();
        double winW = Math.Max(MinWidth, s.WindowWidth), winH = Math.Max(MinHeight, s.WindowHeight);
        Width = winW;
        Height = winH;
        _normalSize = new Avalonia.Size(winW, winH);
        var bounds = new PixelRect(s.WindowX, s.WindowY, (int)winW, (int)winH);
        if (s.WindowX >= 0 && Screens.All.Any(sc => sc.WorkingArea.Intersects(bounds)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(s.WindowX, s.WindowY);
            _normalPosition = Position;
        }
        if (s.Maximized) WindowState = WindowState.Maximized;

        _canvas.InitZoom(s.Zoom);
        _canvas.ShowPixelGrid = s.ShowPixelGrid;
        _canvas.ShowPivot = s.ShowPivot;
        _canvas.ShowTileGrid = s.ShowTileGrid;
        _canvas.TiledMode = s.TiledMode;
        _canvas.TileSize = new Size(s.TileWidth, s.TileHeight);
        _canvas.PixelGridColor = ColorParse.ParseOr(s.PixelGridColor, new Color32(255, 255, 255, 48)).ToColor();
        _canvas.TileGridColor = ColorParse.ParseOr(s.TileGridColor, new Color32(90, 200, 255, 150)).ToColor();
        _ctx.OnionSkin = s.OnionSkin;
        _canvas.PixelGridMinZoom = s.PixelGridMinZoom;
        _canvas.TileRepeat = s.TileRepeat;
        _canvas.TileDim = s.TileDim;
        _canvas.OnionBefore = s.OnionBefore;
        _canvas.OnionAfter = s.OnionAfter;
        _canvas.OnionOpacity = s.OnionOpacity;
        _canvas.OnionTint = s.OnionTint;
        _ctx.BrushSize = s.BrushSize;
        _ctx.BrushShape = s.BrushShape;
        _ctx.FillTolerance = s.FillTolerance;
        _ctx.FillDiagonal = s.FillDiagonal;
        _ctx.FillSampleVisible = s.FillSampleVisible;
        _ctx.PickerSampleVisible = s.PickerSampleVisible;
        _ctx.DodgeStrength = s.DodgeStrength;
        _ctx.DitherPattern = s.DitherPattern;
        _ctx.PixelPerfect = s.PixelPerfect;
        _ctx.FillContiguous = s.FillContiguous;
        _ctx.ShapeFilled = s.ShapeFilled;
        _ctx.ShapeKind = s.ShapeKind;
        _ctx.ShapeSides = s.ShapeSides;
        _ctx.StarPoints = s.StarPoints;
        _ctx.SpiralTurns = s.SpiralTurns;
        _ctx.ShapeOrthogonal = s.ShapeOrthogonal;
        _ctx.RightButtonErases = s.RightButtonErases;
        _ctx.MoveWrap = s.MoveWrap;
        _ctx.TransformResample = s.TransformResample;
        if (ParseGradientStops(s.GradientStops) is { } stops) _ctx.GradientStops = stops;
        if (ParseGradientAlphaStops(s.GradientAlphaStops) is { } alphaStops) _ctx.GradientAlphaStops = alphaStops;
        _ctx.GradientInterpolation = s.GradientInterpolation;
        _ctx.GradientDither = s.GradientDither;
        _ctx.GradientColorSpace = s.GradientColorSpace ?? GradientColorSpace.Rgb;
        _ctx.Primary = ColorParse.ParseOr(s.PrimaryColor, Color32.Black);
        _ctx.Secondary = ColorParse.ParseOr(s.SecondaryColor, Color32.Transparent);
        foreach (var hex in s.RecentColors.AsEnumerable().Reverse())
            if (Color32.TryParseHex(hex, out var c)) _ctx.PushRecentColor(c);
        _colors.SetRecent(_ctx.RecentColors);
        var palettes = s.Palettes ?? [new PaletteSetting { Name = AppSettings.DefaultPaletteName, Colors = [.. AppSettings.DefaultPalette] }];
        _colors.SetPalettes(palettes.Select(p => p.ToPalette()));
        _colors.PickerMode = s.ColorPickerMode;
        _colors.LospecSort = s.LospecSort ?? LospecPaletteDialog.DefaultSortKey;
        _browser.IncludeSubfolders = s.IncludeSubfolders;
        _canvas.AutoFit = s.AutoFit;
        _preview.ZoomSetting = s.PreviewZoom;
        _preview.FpsSetting = s.PreviewFps;
        _preview.BackgroundSetting = s.PreviewBackground;
        _start.SetRecent(s.RecentFolders, s.RecentFiles);
    }

    /// <summary>일반(최대화·최소화 아닌) 상태의 창 위치·크기를 기억한다(WinForms RestoreBounds 대신).</summary>
    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        _normalPosition = Position;
        _normalSize = ClientSize;
    }

    private void OnShown()
    {
        var s = _settings;
        SetSplit(_colFiles, _splitFiles, s.LeftWidth);
        SetSplit(_colPalette, _splitPalette, s.PaletteWidth);
        SetSplit(_colRight, _splitRight, s.RightWidth);
        SetSplit(_rowBottom, _splitTimeline, s.BottomHeight);
        SetSplit(_rowPreview, _splitRightDock, s.PreviewHeight);
        _browser.TreeHeight = s.BrowserTreeHeight;
        SetFilesVisible(s.FilesVisible);
        _settingsApplied = true;
        RememberNormalBounds();
        _start.LoadHub();
        var folder = _args.OpenFolder ?? s.LastFolder;
        if (folder is not null && Directory.Exists(folder)) SetRootFolder(folder);
        else if (_args.OpenFolder is not null) Ui.Error(this, MainStrings.FormatMessage_337(openFolder: _args.OpenFolder));
        RestoreSession();
        if (_args.OpenFile is not null) OpenFile(_args.OpenFile);
        if (s.ChatOpen) Dispatcher.UIThread.Post(ToggleChat);
        _canvas.Focus();
        _updater.OnStartup();
        // 에셋 저장소를 열어 첫 실행의 예전 프리셋 migration을 끝내고, 손상된 상태 파일이 있으면 한 번 알린다.
        Dispatcher.UIThread.Post(() => AppAssets.ShowProblems(this), DispatcherPriority.Background);
    }

    /// <summary>설정의 그라데이션 정지점("위치:#색")을 읽는다. 없거나 2개보다 적게 읽히면 null(기본값 유지).</summary>
    private static List<GradientStop>? ParseGradientStops(List<string>? texts)
    {
        if (texts is null) return null;
        var stops = new List<GradientStop>();
        foreach (var text in texts)
        {
            int colon = text.IndexOf(':');
            if (colon <= 0) continue;
            if (double.TryParse(text[..colon], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pos)
                && Color32.TryParseHex(text[(colon + 1)..].Trim(), out var color))
                stops.Add(new GradientStop(Math.Clamp(pos, 0, 1), color));
        }
        return stops.Count >= 2 ? stops : null;
    }

    /// <summary>설정의 불투명도 정지점("위치:0~255")을 읽는다. 없거나 2개보다 적게 읽히면 null(기본값 유지).</summary>
    private static List<GradientAlphaStop>? ParseGradientAlphaStops(List<string>? texts)
    {
        if (texts is null) return null;
        var stops = new List<GradientAlphaStop>();
        foreach (var text in texts)
        {
            int colon = text.IndexOf(':');
            if (colon <= 0) continue;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (double.TryParse(text[..colon], System.Globalization.NumberStyles.Float, inv, out var pos)
                && int.TryParse(text[(colon + 1)..].Trim(), System.Globalization.NumberStyles.Integer, inv, out var alpha))
                stops.Add(new GradientAlphaStop(Math.Clamp(pos, 0, 1), (byte)Math.Clamp(alpha, 0, 255)));
        }
        return stops.Count >= 2 ? stops : null;
    }

    private void SaveSettings()
    {
        var s = _settings;
        s.Maximized = WindowState == WindowState.Maximized;
        RememberNormalBounds();
        (s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight) =
            (_normalPosition.X, _normalPosition.Y, (int)Math.Round(_normalSize.Width), (int)Math.Round(_normalSize.Height));
        if (_settingsApplied)
        {
            s.FilesVisible = _filesShown;
            if (s.FilesVisible && _filesAnim is null) s.LeftWidth = (int)Math.Round(_colFiles.ActualWidth);
            s.PaletteWidth = (int)Math.Round(_colPalette.ActualWidth);
            s.RightWidth = (int)Math.Round(_colRight.ActualWidth);
            s.BottomHeight = (int)Math.Round(_rowBottom.ActualHeight);
            s.PreviewHeight = (int)Math.Round(_rowPreview.ActualHeight);
            s.BrowserTreeHeight = _browser.TreeHeight;
        }
        s.Zoom = _canvas.Zoom;
        s.ShowPixelGrid = _canvas.ShowPixelGrid;
        s.ShowPivot = _canvas.ShowPivot;
        s.ShowTileGrid = _canvas.ShowTileGrid;
        s.TiledMode = _canvas.TiledMode;
        s.TileWidth = _canvas.TileSize.Width;
        s.TileHeight = _canvas.TileSize.Height;
        s.PixelGridColor = _canvas.PixelGridColor.ToColor32().ToHex();
        s.TileGridColor = _canvas.TileGridColor.ToColor32().ToHex();
        s.OnionSkin = _ctx.OnionSkin;
        s.PixelGridMinZoom = _canvas.PixelGridMinZoom;
        s.TileRepeat = _canvas.TileRepeat;
        s.TileDim = _canvas.TileDim;
        s.OnionBefore = _canvas.OnionBefore;
        s.OnionAfter = _canvas.OnionAfter;
        s.OnionOpacity = _canvas.OnionOpacity;
        s.OnionTint = _canvas.OnionTint;
        s.BrushSize = _ctx.BrushSize;
        s.BrushShape = _ctx.BrushShape;
        s.FillTolerance = _ctx.FillTolerance;
        s.FillDiagonal = _ctx.FillDiagonal;
        s.FillSampleVisible = _ctx.FillSampleVisible;
        s.PickerSampleVisible = _ctx.PickerSampleVisible;
        s.DodgeStrength = _ctx.DodgeStrength;
        s.DitherPattern = _ctx.DitherPattern;
        s.PixelPerfect = _ctx.PixelPerfect;
        s.FillContiguous = _ctx.FillContiguous;
        s.ShapeFilled = _ctx.ShapeFilled;
        s.ShapeKind = _ctx.ShapeKind;
        s.ShapeSides = _ctx.ShapeSides;
        s.StarPoints = _ctx.StarPoints;
        s.SpiralTurns = _ctx.SpiralTurns;
        s.ShapeOrthogonal = _ctx.ShapeOrthogonal;
        s.RightButtonErases = _ctx.RightButtonErases;
        s.MoveWrap = _ctx.MoveWrap;
        s.TransformResample = _ctx.TransformResample;
        s.GradientStops = _ctx.GradientStops.Select(g => FormattableString.Invariant($"{g.Position:0.####}:{g.Color.ToHex()}")).ToList();
        s.GradientAlphaStops = _ctx.GradientAlphaStops.Select(g => FormattableString.Invariant($"{g.Position:0.####}:{g.Alpha}")).ToList();
        s.GradientInterpolation = _ctx.GradientInterpolation;
        s.GradientDither = _ctx.GradientDither;
        s.GradientColorSpace = _ctx.GradientColorSpace;
        s.PrimaryColor = _ctx.Primary.ToHex();
        s.SecondaryColor = _ctx.Secondary.ToHex();
        s.RecentColors = _ctx.RecentColors.Select(c => c.ToHex()).ToList();
        s.Palettes = _colors.Palettes.Select(PaletteSetting.From).ToList();
        s.ColorPickerMode = _colors.PickerMode;
        s.LospecSort = _colors.LospecSort;
        s.IncludeSubfolders = _browser.IncludeSubfolders;
        s.LastFolder = _browser.RootFolder ?? s.LastFolder;
        s.AutoFit = _canvas.AutoFit;
        s.PreviewZoom = _preview.ZoomSetting;
        s.PreviewFps = _preview.FpsSetting;
        s.PreviewBackground = _preview.BackgroundSetting;
        // 다음에 켤 때 다시 열 탭(프로젝트 경로가 없는 문서 — 저장한 적 없는 새 문서·영상 등에서 만든 문서 — 는 빼고).
        s.OpenTabs = _tabs.ToSession(t => t.Doc.Project.Path is null ? null : t.Doc.DiskPath);
        s.Save();
    }

    /// <summary>
    /// 창 닫기: 대화상자는 Closing 처리 중에 띄울 수 없으므로, 저장 안 한 문서(지금 문서 + 메모리)가 있으면 닫기를 일단 취소하고
    /// 처리가 끝난 뒤 한 창에 모아 물어서(<see cref="ConfirmCloseAll"/>) 계속해도 되면 다시 닫는다.
    /// </summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        // 저장·열기 중에는 닫지 않고 끝난 뒤 다시 닫는다(저장 안 한 문서 확인도 그때).
        if (WorkOverlay.IsActive)
        {
            e.Cancel = true;
            WorkOverlay.CloseWhenIdle(this);
            return;
        }
        // 자동 저장이 쓰는 중이면 끝나고 결과를 반영한 뒤 다시 닫는다(쓰다 만 내보내기 파일을 남기지 않게).
        if (_autoSaving is { } saving)
        {
            e.Cancel = true;
            saving.Task.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                FinishAutoSave();
                Close();
            }), TaskScheduler.Default);
            return;
        }
        _ctx.CommitFloating();
        if (UnsavedCount == 0)
        {
            _play?.Close();
            ShutdownChat();
            ShutdownHistory();
            SaveSettings();
            return;
        }
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _closePending = false;
            if (!ConfirmCloseAll())
            {
                App.RestartOnExit = false;
                return;
            }
            _closeConfirmed = true;
            _play?.Close();
            ShutdownChat();
            ShutdownHistory();
            SaveSettings();
            Close();
        });
    }

    /// <summary>위 막대 "업데이트" 단추: 알릴 새 버전이 있을 때만 보인다.</summary>
    private void ShowUpdateButton(ReleaseInfo? release)
    {
        _cbUpdate.IsVisible = release is not null;
        if (release is not null) _cbUpdate.Content = MainStrings.FormatMessage_338(version: release.Version);
    }

    /// <summary>업데이트 설치 스크립트를 띄운 뒤 앱을 끝낸다(저장 여부는 <see cref="ConfirmCloseAll"/>로 이미 물었다).</summary>
    private void ExitForUpdate()
    {
        _closeConfirmed = true;
        _play?.Close();
        ShutdownChat();
        ShutdownHistory();
        SaveSettings();
        Close();
    }

    /// <summary>앱을 다시 시작한다: 평소처럼 닫고(저장 안 한 문서는 묻고, 취소하면 그대로 둠) 끝난 뒤 새로 띄운다(<see cref="Program"/>).</summary>
    private void RestartApp()
    {
        App.RestartOnExit = true;
        Close();
    }

    /// <summary>끝내기 전에: 열린 문서 중 저장한 그대로인 것의 실행취소 기록을 남기고, 기다리던 기록 쓰기가 끝나기를 잠깐 기다린다.</summary>
    private void ShutdownHistory()
    {
        foreach (var p in OpenProjects) _history.Leaving(p);
        _history.Flush(TimeSpan.FromSeconds(10));
    }

    private void OpenSettingsFolder()
    {
        var dir = Path.GetDirectoryName(AppSettings.FilePath)!;
        SaveSettings();
        try { Platform.OpenFolder(dir); }
        catch (IOException ex)
        {
            Ui.Error(this, ex.Message);
        }
    }

    private void OpenLogFolder()
    {
        try { CrashGuard.OpenLogFolder(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, ex.Message);
        }
    }

    // ───────────── 키 처리 ─────────────

    /// <summary>창 전체 키 미리 받기(터널): 포커스된 컨트롤보다 먼저 단축키 표를 본다.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // 저장·열기 덮개가 떠 있는 동안은 단축키도 막는다(덮개가 키를 먹기 전에 이 처리기가 먼저 돈다).
        if (WorkOverlay.IsActive)
        {
            e.Handled = true;
            return;
        }
        // Ctrl(⌘)을 누르는 동안의 임시 이동 도구는 단축키(⌘T·⌘V 등)를 누르는 순간 먼저 원래 도구로 되돌린다 —
        // 단축키가 시작한 변형·붙여넣기가 ⌘를 뗄 때의 도구 되돌리기에 적용(취소)되지 않게.
        _canvas.EndTempMoveForShortcut(e.Key);
        if (e.Handled) return;
        var mods = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (_keys.TryGetValue((e.Key, mods), out var entry) && CanRun(entry.Scope) && entry.Action())
            e.Handled = true;
    }

    private bool CanRun(KeyScope scope)
    {
        if (scope == KeyScope.Global) return true;
        var focus = FocusManager?.GetFocusedElement();
        if (scope == KeyScope.Canvas) return ReferenceEquals(focus, _canvas);
        // 글자 입력란(숫자 상자·편집 가능한 콤보 상자 안쪽 입력란 포함)이면 그 입력란이 키를 받는다.
        return focus is not TextBox;
    }

    // ───────────── 화면 갱신 ─────────────

    private void UpdateAll()
    {
        bool has = _ctx.HasProject;
        _start.IsVisible = !has;
        if (!has) _start.SetRecent(_settings.RecentFolders, _settings.RecentFiles);
        _canvas.IsVisible = has;
        foreach (var item in _docItems) item.IsEnabled = has;
        _miPlayTest.IsEnabled = has;
        UpdateTitle();
        UpdateStatus();
        UpdateUndoItems();
        UpdateSaveButtons();
        UpdateToolUi();
    }

    private void UpdateTitle()
    {
        var p = _ctx.Project;
        Title = p is null
            ? "Gpixel"
            : $"{p.DisplayName}{(p.IsDirty ? " ●" : "")} — Gpixel" + (p.Path is { } path ? $"  ({Path.GetDirectoryName(path)})" : "");
    }

    private void UpdateStatus()
    {
        var p = _ctx.Project;
        _stSize.Text = p is null ? "" : $"{_ctx.CanvasWidth} × {_ctx.CanvasHeight}";
        _stFrame.Text = p is null ? "" : StatusFrameText(p);
        _stZoom.Text = $"{_canvas.Zoom * 100}%";
        _syncingZoom = true;
        _cbZoom.Value = _canvas.Zoom * 100;
        _syncingZoom = false;
        _stDirtyDot.IsVisible = p?.IsDirty == true;
        if (p is null) _stDirty.Text = "";
        else
        {
            _stDirty.Text = p.IsDirty ? MainStrings.Ui_243 : MainStrings.Ui_244;
            _stDirty.Foreground = p.IsDirty ? AppTheme.WarningBrush : AppTheme.TextBrush;
        }
    }

    /// <summary>상태 표시줄의 "프레임 i/n · L k 이름" (부위면 크기도).</summary>
    private string StatusFrameText(SpriteProject p)
    {
        var text = MainStrings.FormatMessage_339(value: _ctx.CurrentFrame + 1, count: p.Frames.Count, value2: _ctx.CurrentLayer + 1);
        if (_ctx.CurrentLayerObject is { } layer)
        {
            text += $" {layer.Name}";
            if (layer.IsPart) text += $" ({layer.PartSize.Width}×{layer.PartSize.Height})";
            else if (layer.IsGroup) text += MainStrings.Ui_245;
        }
        return text;
    }

    private void UpdateHover(Point? pt)
    {
        if (!_ctx.HasProject || pt is not { } p)
        {
            _stPos.Text = "";
            _stColor.Text = "";
            return;
        }
        _stPos.Text = $"{p.X}, {p.Y}";
        var c = _ctx.CompositeColorAt(p.X, p.Y, exact: false);
        _stColor.Text = c.A == 0 ? MainStrings.Ui_246 : c.ToHex();
    }

    private void UpdateUndoItems()
    {
        var h = _ctx.Project?.History;
        var undo = _undoOrder.NextUndo;
        var redo = _undoOrder.NextRedo;
        string? undoLabel = undo == UndoTimeline.Side.Palette ? UiStrings.Palette_UndoLabel : undo == UndoTimeline.Side.Document ? h?.UndoLabel : null;
        string? redoLabel = redo == UndoTimeline.Side.Palette ? UiStrings.Palette_UndoLabel : redo == UndoTimeline.Side.Document ? h?.RedoLabel : null;
        _miUndo.IsEnabled = undo != UndoTimeline.Side.None;
        _miRedo.IsEnabled = redo != UndoTimeline.Side.None;
        SetMenuText(_miUndo, undoLabel is { } u ? MainStrings.FormatMessage_340(u: u) : MainStrings.Menu_Edit_Undo_089);
        SetMenuText(_miRedo, redoLabel is { } r ? MainStrings.FormatMessage_341(r: r) : MainStrings.Menu_Edit_Redo_091);
    }

    /// <summary>M을 이만큼 안에 다시 눌러야 선택 칸의 다음 도구로 넘어간다.</summary>
    private static readonly TimeSpan SelectCycleWindow = TimeSpan.FromSeconds(10);
    private DateTime _lastSelectCycleKey = DateTime.MinValue;

    /// <summary>
    /// M: 선택 칸의 도구(마지막에 고른 선택 도구)를 고른다. 이미 선택 도구를 쓰는 중이고 직전 M이 10초 안이면 칸 안의 다음 도구로(사각 → 원 → 자유 → 스마트).
    /// 10초가 지났으면 바로 바꾸지 않고, 10초 안에 한 번 더 누르면 바꾼다. 다른 도구를 쓰는 중이면 선택 칸 도구로만 바꾼다.
    /// </summary>
    private void CycleSelectionTool()
    {
        var slot = _toolSlots.First(s => s.Contains(ToolKind.Select));
        var now = DateTime.UtcNow;
        bool recent = now - _lastSelectCycleKey <= SelectCycleWindow;
        _lastSelectCycleKey = now;
        if (!slot.Contains(_ctx.Tool)) _ctx.Tool = slot.Current;
        else if (recent) _ctx.Tool = slot.Next(_ctx.Tool);
        else ShowMessage(Ui.Shortcut(MainStrings.FormatShortcut_CycleAgain(tool: ToolDefs.First(d => d.Tool == _ctx.Tool).Name, shortcut: KeyText($"도구/{SelectCycleName}"))));
        UpdateToolUi();
    }

    private void UpdateToolUi()
    {
        var tool = _ctx.Tool;
        // 단축키·메뉴로 고른 묶음 도구도 그 칸에 보이게 바꾼다.
        foreach (var slot in _toolSlots)
        {
            slot.SetCurrent(tool);
            slot.IsChecked = slot.Current == tool;
        }
        foreach (var (t, mi) in _toolMenuItems) mi.IsChecked = t == tool;
        var def = ToolDefs.First(d => d.Tool == tool);
        _cbToolName.Text = Ui.Shortcut(ToolKeyText(tool) is { Length: > 0 } key ? $"{def.Name} ({key})" : def.Name);
        _cbHint.Text = Ui.Shortcut(def.Hint);
        _miPixelPerfect.IsChecked = _ctx.PixelPerfect;
        _miFilled.IsChecked = _ctx.ShapeFilled;
        _miGlobalFill.IsChecked = !_ctx.FillContiguous;
        _miRightErase.IsChecked = _ctx.RightButtonErases;
        _miWrap.IsChecked = _ctx.MoveWrap;
        _miMirrorX.IsChecked = _ctx.MirrorX;
        _miMirrorY.IsChecked = _ctx.MirrorY;
        _cbGrid.IsChecked = _miGrid.IsChecked = _canvas.ShowPixelGrid;
        _cbTiles.IsChecked = _miTiles.IsChecked = _canvas.ShowTileGrid;
        _cbTiled.IsChecked = _miTiled.IsChecked = _canvas.TiledMode;
        _cbPivot.IsChecked = _miPivot.IsChecked = _canvas.ShowPivot;
        _cbOnion.IsChecked = _miOnion.IsChecked = _ctx.OnionSkin;
        _cbFit.IsChecked = _canvas.AutoFit;
        _tbFiles.IsChecked = _miFiles.IsChecked = _filesShown;
    }

    private void ShowMessage(string text) => _stMessage.Text = text;

    private void SetPixelGrid(bool on) { _canvas.ShowPixelGrid = on; UpdateToolUi(); }
    private void SetTileGrid(bool on) { _canvas.ShowTileGrid = on; UpdateToolUi(); }
    private void SetShowPivot(bool on) { _canvas.ShowPivot = on; UpdateToolUi(); }

    private void SetTiledMode(bool on)
    {
        _canvas.TiledMode = on;
        UpdateToolUi();
        ShowMessage(on ? MainStrings.Message_248 : MainStrings.Message_249);
    }

    private bool _filesShown = true;
    private DispatcherTimer? _filesAnim;

    /// <summary>
    /// 파일 브라우저 칸 보이기/숨기기(숨기면 칸과 나눔 막대를 접는다). <paramref name="animate"/>면 폭이 부드럽게 늘고 준다(약 0.18초);
    /// 도중에 다시 누르면 지금 폭에서 반대로 간다.
    /// </summary>
    private void SetFilesVisible(bool visible, bool animate = false)
    {
        if (!visible && _filesShown && _browser.IsVisible && _settingsApplied && _filesAnim is null)
            _settings.LeftWidth = (int)Math.Round(_colFiles.ActualWidth);
        _filesShown = visible;
        _filesAnim?.Stop();
        _filesAnim = null;
        double target = visible ? ClampSplit(_settings.LeftWidth, _splitFiles.Bounds.Width) : 0;
        void Finish()
        {
            _browser.IsVisible = visible;
            _filesSplitter.IsVisible = visible;
            _colFiles.MinWidth = visible ? MinPanelSize : 0;
            if (!visible) _colFiles.Width = new GridLength(0);
            else if (_settingsApplied || _colFiles.Width.Value == 0) SetSplit(_colFiles, _splitFiles, _settings.LeftWidth);
            UpdateToolUi();
        }
        if (!animate || _splitFiles.Bounds.Width <= 0)
        {
            Finish();
            return;
        }
        // 움직이는 동안은 칸을 보이게 두고 최소 폭을 풀어 0까지 줄 수 있게 한다.
        _browser.IsVisible = true;
        _browser.ClipToBounds = true;
        _filesSplitter.IsVisible = true;
        _colFiles.MinWidth = 0;
        double from = _colFiles.ActualWidth;
        var start = DateTime.UtcNow;
        const double DurationMs = 180;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / DurationMs);
            double eased = 1 - Math.Pow(1 - t, 3);
            _colFiles.Width = new GridLength(Math.Max(0, from + (target - from) * eased));
            if (t < 1) return;
            timer.Stop();
            if (ReferenceEquals(_filesAnim, timer)) _filesAnim = null;
            Finish();
        };
        _filesAnim = timer;
        timer.Start();
        UpdateToolUi();
    }

    private void GridSettings()
    {
        var dlg = new GridSettingsDialog(_canvas.PixelGridColor.ToColor32(), _canvas.TileSize, _canvas.TileGridColor.ToColor32());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _canvas.PixelGridColor = dlg.PixelGridColor.ToColor();
        _canvas.TileGridColor = dlg.TileGridColor.ToColor();
        _canvas.TileSize = dlg.TileSize;
        UpdateToolUi();
    }

    private static Flyout SettingsFlyout(string title, params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 2, Children = { Ui.Label(title, bold: true) } };
        foreach (var r in rows) panel.Children.Add(r);
        return new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = panel };
    }

    /// <summary>"격자" 단추 우클릭 팝업: 픽셀 격자 색(알파 = 진하기)과 보이기 시작하는 배율.</summary>
    private Flyout PixelGridFlyout()
    {
        var color = new ColorField(_canvas.PixelGridColor.ToColor32());
        var zoom = new ComboBox { ItemsSource = new[] { MainStrings.Ui_250, MainStrings.Ui_251, MainStrings.Ui_252, MainStrings.Ui_253, MainStrings.Ui_254, MainStrings.Ui_255 }, MinWidth = 80 };
        int[] zooms = [2, 4, 6, 8, 12, 16];
        zoom.SelectedIndex = Math.Max(0, Array.IndexOf(zooms, _canvas.PixelGridMinZoom) is var zi && zi >= 0 ? zi : 2);
        void Apply()
        {
            _canvas.PixelGridColor = color.Color.ToColor();
            _canvas.PixelGridMinZoom = zooms[Math.Max(0, zoom.SelectedIndex)];
            if (!_canvas.ShowPixelGrid) SetPixelGrid(true);
            UpdateToolUi();
        }
        color.ColorChanged += (_, _) => Apply();
        zoom.SelectionChanged += (_, _) => Apply();
        return SettingsFlyout(MainStrings.Ui_256,
            Ui.Row(Ui.Label(MainStrings.Message_257), color, Ui.InfoBadge(MainStrings.Message_258)),
            Ui.Row(Ui.Label(MainStrings.Message_259), zoom));
    }

    /// <summary>"타일 모드" 단추 우클릭 팝업: 반복 크기(자동·3×3·5×5·7×7)와 주변 어둡게.</summary>
    private Flyout TiledModeFlyout()
    {
        var repeat = new TabStrip(TabStripKind.Segmented, MainStrings.Ui_260, "3×3", "5×5", "7×7") { SelectedIndex = _canvas.TileRepeat };
        repeat.SetTip(0, MainStrings.Ui_261);
        var dim = Ui.Check(MainStrings.Message_262, _canvas.TileDim);
        void Apply()
        {
            _canvas.TileRepeat = Math.Max(0, repeat.SelectedIndex);
            _canvas.TileDim = dim.On();
            if (!_canvas.TiledMode) SetTiledMode(true);
            UpdateToolUi();
        }
        repeat.SelectionChanged += (_, _) => Apply();
        dim.IsCheckedChanged += (_, _) => Apply();
        return SettingsFlyout(MainStrings.Message_263, Ui.Row(Ui.Label(MainStrings.Message_264), repeat), dim);
    }

    /// <summary>"양파" 단추 우클릭 팝업: 앞·뒤 프레임 수, 진하기, 색 물들이기.</summary>
    private Flyout OnionFlyout()
    {
        var before = Ui.Num(0, 5, _canvas.OnionBefore, 56).Presets(0, 1, 2, 3, 4, 5).Unit(MainStrings.Message_265).RecentKey("onion.frames");
        var after = Ui.Num(0, 5, _canvas.OnionAfter, 56).Presets(0, 1, 2, 3, 4, 5).Unit(MainStrings.Message_266).RecentKey("onion.frames");
        var opacity = Ui.Num(5, 90, _canvas.OnionOpacity, 56).Presets(10, 15, 20, 25, 33, 40, 50, 66, 75, 90).Unit("%").RecentKey("onion.opacity");
        var tint = Ui.Check(MainStrings.Message_267, _canvas.OnionTint);
        void Apply()
        {
            _canvas.OnionBefore = (int)before.NumValue();
            _canvas.OnionAfter = (int)after.NumValue();
            _canvas.OnionOpacity = (int)opacity.NumValue();
            _canvas.OnionTint = tint.On();
            if (!_ctx.OnionSkin) _ctx.OnionSkin = true;
            UpdateToolUi();
        }
        before.ValueChanged += (_, _) => Apply();
        after.ValueChanged += (_, _) => Apply();
        opacity.ValueChanged += (_, _) => Apply();
        tint.IsCheckedChanged += (_, _) => Apply();
        return SettingsFlyout(MainStrings.Ui_268,
            Ui.Row(Ui.Label(MainStrings.Message_269), before, Ui.Label(MainStrings.Message_270), after, Ui.Label(MainStrings.Message_271)),
            Ui.Row(Ui.Label(MainStrings.Message_272), opacity, Ui.Label(MainStrings.Message_273)),
            tint);
    }

    /// <summary>"타일 격자" 단추 우클릭 팝업: 타일 가로·세로·색을 바로 바꾼다(꺼져 있으면 켬). 값은 격자 설정 창과 같이 앱 설정에 저장.</summary>
    private Flyout TileGridFlyout()
    {
        var size = _canvas.TileSize;
        var w = Ui.Num(1, 4096, size.Width, 64).Presets(Ui.TileSizes).Unit("px").RecentKey("tile.size");
        var h = Ui.Num(1, 4096, size.Height, 64).Presets(Ui.TileSizes).Unit("px").RecentKey("tile.size");
        var color = new ColorField(_canvas.TileGridColor.ToColor32());
        void Apply()
        {
            _canvas.TileSize = new Size((int)w.NumValue(), (int)h.NumValue());
            _canvas.TileGridColor = color.Color.ToColor();
            if (!_canvas.ShowTileGrid) SetTileGrid(true);
            UpdateToolUi();
        }
        w.ValueChanged += (_, _) => Apply();
        h.ValueChanged += (_, _) => Apply();
        color.ColorChanged += (_, _) => Apply();
        var presets = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (int n in new[] { 8, 16, 32, 64 })
            presets.Children.Add(Ui.Button($"{n}×{n}", (_, _) => { w.Value = n; h.Value = n; }).Tip(MainStrings.FormatMessage_342(n: n, n2: n)));
        return SettingsFlyout(MainStrings.Ui_274,
            Ui.Row(Ui.Label(MainStrings.Message_275), w, Ui.Label(MainStrings.Message_276), h, Ui.Label("px")),
            presets,
            Ui.Row(Ui.Label(MainStrings.Message_277), color, Ui.InfoBadge(MainStrings.Message_278)));
    }

    /// <summary>환경 설정(화면 언어·실행취소 횟수·실행취소 기록 남기기·자동 저장·앱 휴지통 보관 기간·업데이트 자동 확인·단축키·AI 참고 자료).</summary>
    private void ShowPreferences()
    {
        var dlg = new PreferencesDialog(_settings.Language ?? UiLanguage.Current, _settings.UndoSteps, _settings.UndoHistoryEnabled, _settings.UndoHistoryPolicy, _history,
            _settings.AutoSave, _settings.AutoSaveIdleSeconds, _settings.TrashPolicy, _settings.UpdateCheck,
            _commands.Select(c => c.Info).ToList(), _commands.ToDictionary(c => c.Info.Id, c => c.Keys.ToList()),
            _settings.ReferencesFolder, _settings.ReferencesShowExamples, _settings.FfmpegPath, owner => ShowAiStatus(owner));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        // 화면 언어는 다시 시작해야 바뀐다(정적 필드에 담긴 문구도 있음).
        bool languageChanged = !string.Equals(dlg.Language, _settings.Language, StringComparison.OrdinalIgnoreCase);
        _settings.Language = dlg.Language;
        _settings.UndoSteps = dlg.UndoSteps;
        bool turnedOff = _settings.UndoHistoryEnabled && !dlg.UndoHistoryEnabled;
        _settings.UndoHistoryEnabled = dlg.UndoHistoryEnabled;
        _settings.UndoHistoryPolicy = dlg.UndoHistoryPolicy;
        // 끄면 새로 쓰지 않는다. 이미 남긴 기록은 지울지 묻고, 두면 보관 기간·용량 정리가 맡는다.
        if (turnedOff && _history.Usage() is { Files: > 0 } usage
            && Ui.Ask(this, MainStrings.FormatMessage_343(bytes: AppTrash.FormatSize(usage.Bytes), files: usage.Files),
                MainStrings.Ui_280, MessageButtons.YesNo, MessageIcon.Question) == DialogResult.Yes)
            _history.Clear();
        _history.Cleanup();
        _settings.TrashPolicy = dlg.TrashPolicy;
        _settings.UpdateCheck = dlg.UpdateCheck;
        _settings.FfmpegPath = dlg.FfmpegPath;
        // 끄면 기다리던 자동 저장도 바로 멈춘다. 바꾼 시간은 다음 편집부터.
        _settings.AutoSave = dlg.AutoSave;
        _settings.AutoSaveIdleSeconds = dlg.AutoSaveIdleSeconds;
        if (!_settings.AutoSave) _autoSave.Stop();
        _ctx.UndoCapacity = dlg.UndoSteps;
        _browser.Trash.Purge(dlg.TrashPolicy);
        // 참고 자료 폴더를 바꾸면 다음에 쓸 때 새 폴더를 연다(열린 자료 창은 닫음).
        if (!string.Equals(dlg.ReferencesFolder ?? "", _settings.ReferencesFolder ?? "", StringComparison.Ordinal))
        {
            _settings.ReferencesFolder = dlg.ReferencesFolder;
            _refs = null;
            _refsWindow?.Close();
        }
        _settings.ReferencesShowExamples = dlg.ReferencesShowExamples;
        if (_refs is { } refs && refs.ShowBuiltIns != dlg.ReferencesShowExamples)
        {
            refs.ShowBuiltIns = dlg.ReferencesShowExamples;
            _refsWindow?.Reload();
        }
        // 기본값과 다른 명령만 저장한다(나중에 기본 단축키가 바뀌어도 고치지 않은 명령은 따라감).
        var custom = _commands
            .Where(c => dlg.Keys.TryGetValue(c.Info.Id, out var k) && !k.SequenceEqual(c.Info.Defaults))
            .ToDictionary(c => c.Info.Id, c => dlg.Keys[c.Info.Id].Select(Shortcuts.Format).ToList());
        _settings.Shortcuts = custom.Count > 0 ? custom : null;
        ApplyShortcuts();
        _settings.Save();
        if ((languageChanged || dlg.CurrentLanguageImported)
            && Ui.Ask(this, UiStrings.Language_RestartNotice, "Gpixel", MessageButtons.YesNo, MessageIcon.Question,
                UiStrings.Language_RestartNow, UiStrings.Language_RestartLater) == DialogResult.Yes)
            RestartApp();
    }

    /// <summary>시작 화면: 모든 탭을 닫는다(저장 안 한 문서는 한 창에 모아 묻는다 — <see cref="CloseAllTabs"/>). 마지막 탭이 닫히면 시작 화면이 Unity 목록을 새로 읽는다.</summary>
    private void ShowStart()
    {
        bool hadTabs = !_tabs.IsEmpty;
        if (!CloseAllTabs()) return;
        if (!hadTabs) _start.LoadHub();
    }

    private void ShowShortcuts()
    {
        var dlg = new ShortcutHelpDialog();
        dlg.ShowDialog(this);
    }

    /// <summary>움직임 테스트: 프로젝트의 태그를 시트 문서로 만들어 넘긴다(편집하면 잠시 뒤 다시 읽음).</summary>
    private void ShowPlayTest()
    {
        if (_play is { IsClosed: false })
        {
            if (_play.WindowState == WindowState.Minimized) _play.WindowState = WindowState.Normal;
            _play.Activate();
            return;
        }
        var play = new PlayTestForm(() => _ctx.Project is { } p ? ProjectExport.BuildDocument(p, p.BaseName, allFrames: true) : null);
        play.Closed += (_, _) => { if (ReferenceEquals(_play, play)) _play = null; };
        _play = play;
        play.Show();
    }

    // ───────────── 파일 ─────────────

    private static DateTime SafeStamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    // ───────────── 열린 문서(탭) ─────────────

    /// <summary>저장 안 한 변경이 있는 열린 문서 수.</summary>
    private int UnsavedCount => OpenDocs.Count(d => d.Project.IsDirty);

    /// <summary>상태 표시줄 문구에 덧붙일 " · 저장 안 한 파일 N개"(없으면 빈 문자열).</summary>
    private string UnsavedSuffix => UnsavedCount is var n and > 0 ? MainStrings.FormatMessage_344(n: n) : "";

    /// <summary>파일 목록에서 지금 문서를 가리키는 칸: 프로젝트 파일이 있으면 그것, 아니면 연 PNG.</summary>
    private string? CurrentTilePath => CurrentDoc?.DiskPath;

    /// <summary>
    /// 경로(프로젝트 경로나 가져온 PNG)의 열린 문서. 없으면 false.
    /// 돌려준 인스턴스는 편집 중인 문서다(합치기 등은 읽기만).
    /// </summary>
    internal bool TryGetOpenDocument(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SpriteProject? project)
    {
        project = null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        project = FindDoc(full)?.Project;
        return project is not null;
    }

    /// <summary>파일 목록의 빨간 점(저장 안 한 변경)과 메모리 썸네일, 도구 막대의 저장·되돌리기 단추를 맞춘다.</summary>
    private void SyncUnsaved()
    {
        var files = new Dictionary<string, PixelImage?>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in OpenDocs)
            if (doc.Project.IsDirty)
                foreach (var path in doc.Paths) files[path] = doc.Thumbnail;
        _browser.SetUnsaved(files);
        UpdateSaveButtons();
    }

    /// <summary>저장 단추는 지금 문서에 저장 안 한 수정이 있을 때만, 되돌리기(단추·메뉴)는 거기에 더해 디스크에 되돌릴 파일이 있을 때만 켠다.</summary>
    private void UpdateSaveButtons()
    {
        bool revert = RevertPath is not null;
        SetToolBarActionEnabled(_tbSave, _ctx.Project is { IsDirty: true });
        SetToolBarActionEnabled(_tbRevert, revert);
        _miRevert.IsEnabled = revert;
    }

    /// <summary>"저장한 내용으로 되돌리기"로 다시 읽을 파일: 수정된 지금 문서의 프로젝트 파일(아직 없으면 가져온 PNG). 수정이 없거나 디스크에 파일이 없으면 null.</summary>
    private string? RevertPath
        => _ctx.Project is not { IsDirty: true } p ? null
            : p.Path is { } path && File.Exists(path) ? path
            : CurrentDoc?.SourcePath is { } source && source.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(source) ? source
            : null;

    /// <summary>
    /// 저장한 내용으로 되돌리기: 확인한 뒤 지금 문서의 수정·실행취소 기록을 버리고 파일을 다시 읽는다(<see cref="ReloadDocument"/>).
    /// 레이어·프레임·확대율·위치는 그대로 둔다. 다시 읽지 못하면(가져오기 취소·읽기 오류) 수정한 문서를 그대로 둔다.
    /// </summary>
    private void RevertToSaved()
    {
        if (CurrentDoc is not { } doc || RevertPath is not { } path) return;
        var name = Path.GetFileName(path);
        // 묻는 동안 자동 저장이 버릴 수정을 파일에 써 버리지 않게 멈춘다.
        _autoSave.Stop();
        if (Ui.Ask(this, MainStrings.FormatMessage_347(name: name),
                MainStrings.Ui_284, MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes
            || !ReloadDocument(doc, path, interactive: true, out _))
        {
            RestartAutoSave();
            return;
        }
        _browser.SelectFile(CurrentTilePath);
        ShowMessage(MainStrings.FormatMessage_348(name: name, unsavedSuffix: UnsavedSuffix));
    }

    /// <summary>
    /// 끝내기·모든 탭 닫기 전에: 저장 안 한 변경이 있는 열린 문서를 한 창에 모아 묻는다(<see cref="UnsavedChangesDialog"/>).
    /// "선택한 파일 저장"은 체크한 문서를 Ctrl+S와 같이 저장하고(저장한 적 없는 새 문서는 저장 위치를 물음), 하나라도 저장하지 못하면 알리고 멈춘다.
    /// 계속해도 되면 true.
    /// </summary>
    private bool ConfirmCloseAll()
    {
        _ctx.CommitFloating();
        var files = OpenDocs.Where(d => d.Project.IsDirty)
            .Select(d => DescribeUnsaved(d.Project, d.SourcePath, d.Thumbnail ?? d.Project.Flatten(0))).ToList();
        if (files.Count == 0) return true;
        var dlg = new UnsavedChangesDialog(files);
        // 묻는 동안 자동 저장이 "저장 안 함"으로 고를 문서를 써 버리지 않게 멈춘다(취소하면 다시 잰다).
        _autoSave.Stop();
        switch (dlg.ShowDialog(this))
        {
            case DialogResult.No:
                return true;
            case DialogResult.Yes:
                break;
            default:
                RestartAutoSave();
                return false;
        }
        foreach (var p in dlg.Checked)
        {
            bool saved = Save(p);
            if (saved) continue;
            ShowMessage(MainStrings.FormatMessage_349(displayName: p.DisplayName, unsavedSuffix: UnsavedSuffix));
            return false;
        }
        return true;
    }

    private static UnsavedFile DescribeUnsaved(SpriteProject p, string? source, PixelImage thumbnail)
    {
        var shown = source ?? p.Path;
        if (shown is null) return new UnsavedFile(p, MainStrings.Ui_286, null, thumbnail, MainStrings.Ui_287);
        string? note = p.Path is { } path && !string.Equals(path, shown, StringComparison.OrdinalIgnoreCase)
            ? MainStrings.FormatMessage_350(value: Path.GetFileName(path)) : null;
        return new UnsavedFile(p, Path.GetFileName(shown), Path.GetDirectoryName(shown), thumbnail, note);
    }

    private void SetRootFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Ui.Error(this, MainStrings.FormatMessage_351(folder: folder));
            _settings.RecentFolders.Remove(folder);
            _start.SetRecent(_settings.RecentFolders, _settings.RecentFiles);
            return;
        }
        folder = Path.GetFullPath(folder);
        _browser.SetRoot(folder);
        if (!_filesShown) SetFilesVisible(true, animate: true);
        _settings.LastFolder = folder;
        AppSettings.PushRecent(_settings.RecentFolders, folder);
        _start.SetRecent(_settings.RecentFolders, _settings.RecentFiles);
        var unityRoot = UnityProjectLocator.FindProjectRoot(folder);
        ShowMessage(unityRoot is null ? MainStrings.FormatMessage_352(folder: folder) : MainStrings.FormatMessage_353(value: Path.GetFileName(unityRoot)));
    }

    private void OpenFolder()
    {
        var path = Ui.PickFolder(this, MainStrings.Message_289, _browser.RootFolder ?? _settings.LastFolder);
        if (path is not null) SetRootFolder(UnityHubDialog.ResolveAssets(path));
    }

    private void OpenUnityProject()
    {
        var dlg = new UnityHubDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedFolder is { } folder) SetRootFolder(folder);
    }

    /// <summary>.gpixel을 열거나 PNG를 가져온다(같은 이름 .gpixel이 있으면 그것). 다른 그림·움직이는 그림·영상은 프레임으로 푼 새 문서. 취소·실패하면 false.</summary>
    private bool OpenFile(string path) => OpenFile(path, interactive: true, out _);

    /// <summary>
    /// 파일 열기: 새 탭으로 연다. 이미 열린 문서면(프로젝트 파일이나 가져온 PNG가 같으면) 그 탭을 고른다 — 저장 안 한 수정·실행취소 기록 그대로.
    /// <paramref name="interactive"/>가 false면(AI 도구) 대화상자를 띄우지 않는다: 칸 크기가 모두 같은 시트는 애니메이션으로 가져오며,
    /// 결과·오류는 <paramref name="message"/>로 돌려준다.
    /// </summary>
    private bool OpenFile(string path, bool interactive, out string message)
    {
        message = "";
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            message = MainStrings.FormatMessage_354(path: path);
            if (interactive) Ui.Error(this, message);
            return false;
        }
        var target = !full.EndsWith(SpriteProject.FileExtension, StringComparison.OrdinalIgnoreCase) && ProjectImport.FindProjectForImage(full) is { } owner
            ? owner
            : full;
        // 그림(PNG 아닌 것·움직이는 PNG)·영상은 프레임으로 풀어 새 문서로(MainForm.OpenFormats.cs). 프로젝트가 내보낸 파일이면 위에서 그 프로젝트를 고름.
        if (ReferenceEquals(target, full) && IsMediaFile(full)) return OpenMediaFile(full, interactive, out message);
        if (FindDoc(target) is { } open)
        {
            ShowDocument(open);
            AppSettings.PushRecent(_settings.RecentFiles, open.DiskPath ?? target);
            message = MainStrings.FormatMessage_355(value: Path.GetFileName(open.DiskPath ?? target)) + UnsavedSuffix;
            ShowMessage(message);
            return true;
        }
        if (!File.Exists(full))
        {
            message = MainStrings.FormatMessage_357(full: full);
            if (interactive) Ui.Error(this, message);
            _settings.RecentFiles.Remove(path);
            return false;
        }
        if (LoadDocument(full, interactive, out var result, out message, out var historyNote) is not { } doc) return false;
        OpenInNewTab(doc);
        var p = doc.Project;
        AppSettings.PushRecent(_settings.RecentFiles, result!.Imported ? full : p.Path ?? full);
        _browser.SelectFile(p.Path is { } pp && File.Exists(pp) ? pp : full);
        int parts = p.Layers.Count(l => l.IsPart);
        var contents = MainStrings.FormatMessage_359(count: p.Frames.Count, count2: p.Tags.Count) + (parts > 0 ? MainStrings.FormatMessage_360(parts: parts) : "");
        message = result.Imported
            ? MainStrings.FormatMessage_361(value: Path.GetFileName(full), contents: contents, path: Path.GetFileName(p.Path))
            : MainStrings.FormatMessage_362(displayName: p.DisplayName, count: p.Layers.Count, contents: contents);
        message += historyNote + UnsavedSuffix;
        ShowMessage(message);
        if (result.Warning is { } w)
        {
            if (interactive) Ui.Warn(this, w);
            else message += "\n" + w;
        }
        return true;
    }

    /// <summary>
    /// .gpixel을 읽거나 PNG를 가져온다(가져오기 방식을 물을 수 있음 — <paramref name="interactive"/>가 false면 프레임으로). 취소·실패하면 null
    /// (실패 문구는 <paramref name="message"/>, 대화형이면 오류 창도).
    /// </summary>
    private ProjectOpenResult? ReadProject(string full, bool interactive, out string message)
    {
        message = "";
        try
        {
            var defaults = new SpriteSettings { PixelsPerUnit = _settings.LastPpu, Mode = SpriteMode.Multiple };
            var analysis = WorkOverlay.Run(this, UiStrings.Busy_Opening, _ => ProjectImport.Analyze(full, defaults));
            ImportMode? mode = null;
            if (analysis.NeedsModeChoice)
            {
                if (interactive)
                {
                    var size = analysis.Document!.Slices.Select(s => Rectangle.Intersect(s.Rect, analysis.Document.Image.Bounds).Size)
                        .FirstOrDefault(s => s.Width > 0 && s.Height > 0);
                    mode = ImportKindDialog.Ask(this, Path.GetFileName(full), analysis.SliceCount, size);
                    if (mode is null) return null;
                }
                else mode = ImportMode.Frames;
            }
            var result = WorkOverlay.Run(this, UiStrings.Busy_Opening, progress => ProjectImport.Import(analysis, mode, progress));
            // 처음 보는 스크립트 효과는 실행할지 묻고, 실행할 것은 작업 스레드에서 실행기를 미리 띄운다
            ScriptTrustStore.Opened(this, result.Project, interactive);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException
                                       or System.Text.Json.JsonException)
        {
            message = MainStrings.FormatMessage_358(full: full, message: ex.Message);
            if (interactive) Ui.Error(this, message);
            return null;
        }
    }

    /// <summary>
    /// 파일을 읽어 열린 문서를 만든다(탭은 열지 않음 — <see cref="OpenInNewTab"/>). 디스크에서 연 프로젝트 파일이면 남겨 둔 실행취소 기록을 이어 받는다
    /// (<paramref name="historyNote"/> = 알림 글). 취소·실패하면 null.
    /// </summary>
    private OpenDocument? LoadDocument(string full, bool interactive, out ProjectOpenResult? result, out string message, out string? historyNote)
    {
        historyNote = null;
        result = ReadProject(full, interactive, out message);
        if (result is null) return null;
        historyNote = result.Imported ? null : _history.Restore(result.Project);
        var doc = new OpenDocument(result.Project, result.Imported ? result.SourcePngPath : null);
        doc.DiskStamp = doc.DiskPath is { } disk ? SafeStamp(disk) : default;
        return doc;
    }

    /// <summary>새 프로젝트(새 이미지 창)를 새 탭으로 만든다. <paramref name="folder"/>가 있으면 그 폴더에 바로 저장한다(파일 브라우저).</summary>
    private void NewProject(string? folder)
    {
        var defaults = _ctx.HasProject ? (_ctx.CanvasWidth, _ctx.CanvasHeight) : (32, 32);
        var dlg = new NewImageDialog(defaults.Item1, defaults.Item2, folder is null ? null : "new_sprite");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string? path = null;
        if (folder is not null)
        {
            var name = dlg.FileName.EndsWith(SpriteProject.FileExtension, StringComparison.OrdinalIgnoreCase) ? dlg.FileName : dlg.FileName + SpriteProject.FileExtension;
            path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                Ui.Error(this, MainStrings.FormatMessage_363(path: path));
                return;
            }
        }
        var p = BuildProject(dlg.ImageWidth, dlg.ImageHeight, dlg.Transparent ? null : _ctx.Secondary,
            dlg.StartWithParts, dlg.PartName, dlg.PartWidth, dlg.PartHeight);
        OpenInNewTab(new OpenDocument(p, null));
        SelectFirstPart(p);
        if (path is null)
        {
            p.IsDirty = true;
            ShowMessage(Ui.Shortcut(MainStrings.Message_294));
            return;
        }
        if (SaveTo(path)) _browser.RefreshFile(path);
        _browser.SelectFile(path);
    }

    /// <summary>
    /// 새 프로젝트(기록 없음). <paramref name="background"/>가 있으면 첫 레이어 셀을 그 색으로 채운다.
    /// <paramref name="startWithParts"/>면 그룹 + 첫 부위를 만들고 그 부위를 현재 레이어로 고른다(<see cref="SelectFirstPart"/>).
    /// </summary>
    private SpriteProject BuildProject(int width, int height, Color32? background,
        bool startWithParts = false, string? partName = null, int partWidth = 0, int partHeight = 0)
    {
        var p = new SpriteProject(width, height);
        p.Edit(MainStrings.Ui_295, ProjectChange.Settings | ProjectChange.Pixels, () =>
        {
            p.Sprite.PixelsPerUnit = _settings.LastPpu;
            if (background is { } c)
            {
                var bg = new PixelImage(p.Width, p.Height);
                bg.Clear(c);
                p.SetCel(0, 0, bg);
            }
        });
        if (startWithParts)
        {
            var group = p.AddGroup();
            p.AddPart(group, string.IsNullOrWhiteSpace(partName) ? null : partName.Trim(),
                Math.Clamp(partWidth, 1, width), Math.Clamp(partHeight, 1, height));
        }
        p.History.Clear();
        return p;
    }

    /// <summary>새로 연 프로젝트에 부위가 있으면 맨 위 부위를 현재 레이어로 고른다.</summary>
    private void SelectFirstPart(SpriteProject p)
    {
        for (int i = p.Layers.Count - 1; i >= 0; i--)
            if (p.Layers[i].IsPart) { _ctx.SetCurrentLayer(i); return; }
    }

    /// <summary>Ctrl+S: 지금 문서 저장.</summary>
    private bool Save() => _ctx.Project is { } p && Save(p);

    /// <summary>.gpixel 저장 + (켜져 있으면) 시트·메타 내보내기. 저장한 적 없는 문서는 저장 위치를 묻는다. 저장했으면 true.</summary>
    private bool Save(SpriteProject p)
    {
        if (p.Path is null) return SaveAs(p);
        // PNG를 가져온 프로젝트는 아직 .gpixel 파일이 없다 — 제안된 경로에 그대로 저장한다.
        return SaveDocument(p, p.Path, interactive: true, out _);
    }

    /// <summary>자동 저장 타이머를 처음부터 다시 잰다(편집할 때마다 — 편집이 멈춘 지 정한 시간이 지나야 저장). 꺼져 있으면 멈춘다.</summary>
    private void RestartAutoSave()
    {
        _autoSave.Stop();
        if (!_settings.AutoSave || !_ctx.HasProject) return;
        _autoSave.Interval = _settings.AutoSaveDelay;
        _autoSave.Start();
    }

    /// <summary>
    /// 자동 저장: 지금 문서를 Ctrl+S와 같이(내보내기 포함) 묻지 않고 저장한다. 디스크에 프로젝트 파일이 이미 있는 문서만 —
    /// 저장한 적 없는 새 프로젝트·아직 .gpixel이 없는 PNG 가져오기는 저장하지 않는다. 그리는 중·끄는 중·떠 있는 선택·변형·묶음 기록(값 끌기, AI 차례) 중이면
    /// 다시 기다린다. 결과·실패는 상태 표시줄에만 알린다.
    /// 지금 상태의 사본을 작업 스레드에서 쓰므로 저장하는 동안에도 편집을 막지 않는다(<see cref="StartAutoSave"/>). 셀이 아주 크면(사본 메모리가
    /// <see cref="MaxAutoSaveSnapshotBytes"/>를 넘음) 사본 없이 덮개를 띄우고 저장한다.
    /// </summary>
    private void AutoSaveTick()
    {
        _autoSave.Stop();
        if (!_settings.AutoSave || _ctx.Project is not { IsDirty: true, Path: { } path } p || !File.Exists(path)) return;
        if (WorkOverlay.IsActive || _autoSaving is not null || p.IsStrokeActive || p.History.InGroup || _canvas.IsDragging
            || _ctx.Selection.IsFloating || _ctx.IsTransforming || _ctx.IsCanvasResizing)
        {
            _autoSave.Start();
            return;
        }
        if (ProjectIO.CelBytes(p) <= MaxAutoSaveSnapshotBytes)
        {
            StartAutoSave(p, path);
            return;
        }
        ShowMessage(SaveTo(path, interactive: false, out var message) ? AutoSavedMessage(path) : AutoSaveFailedMessage(path, message));
    }

    /// <summary>자동 저장이 사본을 만들어 작업 스레드에서 쓰는 셀 크기 한도(넘으면 사본 대신 덮개를 띄우고 저장 — 사본이 이만큼 메모리를 더 씀).</summary>
    private const long MaxAutoSaveSnapshotBytes = 768L << 20;

    /// <summary>작업 스레드에서 쓰는 중인 자동 저장(원래 프로젝트의 사본을 씀). 없으면 null.</summary>
    private (SpriteProject Project, string Path, ProjectSaveJob Job, DateTime StampBefore, Task Task)? _autoSaving;

    /// <summary>자동 저장 결과를 반영하면 끝나는 작업(그동안 온 파일 감시 알림은 이것을 기다림 — 자기 저장을 "밖에서 바뀜"으로 묻지 않게).</summary>
    private TaskCompletionSource? _autoSaved;

    private string AutoSavedMessage(string path) => MainStrings.FormatMessage_365(name: Path.GetFileName(path), unsavedSuffix: UnsavedSuffix);

    private static string AutoSaveFailedMessage(string path, string message)
        => MainStrings.FormatMessage_366(name: Path.GetFileName(path)) + string.Join(" ", message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>지금 상태의 사본을 만들고(UI 스레드, 셀 복사) 작업 스레드에서 쓴다. 끝나면 UI 스레드에서 결과를 반영한다(<see cref="FinishAutoSave"/>).</summary>
    private void StartAutoSave(SpriteProject p, string path)
    {
        var stampBefore = SafeStamp(path);
        ProjectSaveJob job;
        try
        {
            job = ProjectIO.BeginSave(p, path, snapshot: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or System.Security.SecurityException)
        {
            SaveFailed(p, path, stampBefore, ex, interactive: false, out var message);
            ShowMessage(AutoSaveFailedMessage(path, message));
            return;
        }
        var task = Task.Run(() => job.Run());
        _autoSaving = (p, path, job, stampBefore, task);
        _autoSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        task.ContinueWith(_ => Dispatcher.UIThread.Post(FinishAutoSave), TaskScheduler.Default);
    }

    /// <summary>
    /// 쓰는 중인 자동 저장의 결과를 반영한다. 아직 쓰는 중이면(다른 저장·내보내기·되돌리기·닫기 전) 덮개를 띄우고 끝날 때까지 기다린다.
    /// 자동 저장이 없으면 아무것도 하지 않는다.
    /// </summary>
    private void FinishAutoSave()
    {
        if (_autoSaving is not { } saving) return;
        _autoSaving = null; // 기다리는 동안 끝나 올라온 반영 호출이 두 번 반영하지 않게
        if (!saving.Task.IsCompleted) WorkOverlay.Run(this, UiStrings.Busy_Saving, _ => saving.Task.Wait());
        ShowMessage(CompleteSave(saving.Project, saving.Path, saving.Job, saving.StampBefore, interactive: false, out var message)
            ? AutoSavedMessage(saving.Path)
            : AutoSaveFailedMessage(saving.Path, message));
        var done = _autoSaved;
        _autoSaved = null;
        done?.TrySetResult();
    }

    /// <summary>다른 이름으로 저장(<paramref name="p"/>가 없으면 지금 문서). 저장했으면 true.</summary>
    private bool SaveAs(SpriteProject? p = null)
    {
        p ??= _ctx.Project;
        if (p is null) return false;
        var path = Ui.SaveFile(this, MainStrings.Message_296,
            p.Path is null ? _browser.CurrentFolder ?? _settings.LastFolder : Path.GetDirectoryName(p.Path),
            p.Path is null ? "sprite.gpixel" : Path.GetFileName(p.Path),
            SpriteProject.FileExtension,
            new FileFilter(MainStrings.Ui_297, "*.gpixel"));
        if (path is null) return false;
        return SaveDocument(p, path, interactive: true, out _);
    }

    private bool SaveTo(string path) => SaveTo(path, interactive: true, out _);

    /// <summary>지금 문서 저장(+내보내기). <paramref name="interactive"/>가 false면 실패를 대화상자 대신 <paramref name="message"/>로 돌려준다.</summary>
    private bool SaveTo(string path, bool interactive, out string message) => SaveDocument(_ctx.Project!, path, interactive, out message);

    /// <summary>
    /// 문서 저장(+내보내기, Ctrl+S와 같음). 지금 문서가 아니어도(다른 탭) 화면을 바꾸지 않고 저장한다 —
    /// 내보내기 설정·경로는 프로젝트에 들어 있다. <paramref name="interactive"/>가 false면 실패를 대화상자 대신 <paramref name="message"/>로 돌려준다.
    /// </summary>
    private bool SaveDocument(SpriteProject p, string path, bool interactive, out string message)
    {
        FinishAutoSave(); // 자동 저장이 같은 파일을 쓰는 중이면 끝난 뒤에
        if (ReferenceEquals(p, _ctx.Project)) _ctx.CommitFloating();
        var stampBefore = SafeStamp(path);
        ProjectSaveJob job;
        try
        {
            job = ProjectIO.BeginSave(p, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or System.Security.SecurityException)
        {
            return SaveFailed(p, path, stampBefore, ex, interactive, out message);
        }
        WorkOverlay.Run(this, UiStrings.Busy_Saving, progress => job.Run(progress));
        return CompleteSave(p, path, job, stampBefore, interactive, out message);
    }

    /// <summary>
    /// 쓴 저장의 결과를 프로젝트·화면에 반영한다(<see cref="ProjectSaveJob.Finish"/>, 최근 파일, 실행취소 기록, 파일 목록, 제목).
    /// 쓰는 동안 프로젝트가 바뀌었으면(자동 저장 중 편집) 수정됨이 남고 실행취소 기록은 쓰지 않는다(기록이 파일과 맞지 않으므로).
    /// </summary>
    private bool CompleteSave(SpriteProject p, string path, ProjectSaveJob job, DateTime stampBefore, bool interactive, out string message)
    {
        bool current = ReferenceEquals(p, _ctx.Project);
        var doc = OpenDocs.Find(d => ReferenceEquals(d.Project, p));
        ProjectSaveResult result;
        try
        {
            result = job.Finish();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or System.Security.SecurityException)
        {
            return SaveFailed(p, path, stampBefore, ex, interactive, out message);
        }
        AppSettings.PushRecent(_settings.RecentFiles, result.ProjectPath);
        if (!p.IsDirty) _history.Saved(p);
        message = MainStrings.FormatMessage_369(projectPath: Path.GetFileName(result.ProjectPath)) + ExportSummary(result.Export) + TrashStale(result.Export);
        _browser.RefreshFile(result.ProjectPath);
        if (doc is not null)
        {
            // 가져온 PNG는 이제 프로젝트의 내보내기 결과다 — 문서는 프로젝트 파일로 가리킨다.
            doc.SourcePath = null;
            doc.Thumbnail = null;
            doc.DiskStamp = SafeStamp(result.ProjectPath);
        }
        if (current)
        {
            _browser.SelectFile(result.ProjectPath);
            _play?.DocumentChanged();
            UpdateTitle();
            UpdateStatus();
        }
        RefreshPanes();
        SyncUnsaved();
        ShowMessage(message + UnsavedSuffix);
        return true;
    }

    /// <summary>저장 실패 알림. .gpixel은 저장했고 내보내기(PNG·사이드카·메타)만 실패했으면 파일 감시가 자기 저장을 "밖에서 바뀜"으로 보지 않게 표시를 맞춘다.</summary>
    private bool SaveFailed(SpriteProject p, string path, DateTime stampBefore, Exception ex, bool interactive, out string message)
    {
        bool current = ReferenceEquals(p, _ctx.Project);
        var doc = OpenDocs.Find(d => ReferenceEquals(d.Project, p));
        bool projectSaved = SafeStamp(path) is var stamp && stamp != default && stamp != stampBefore && string.Equals(p.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        if (projectSaved)
        {
            if (!p.IsDirty) _history.Saved(p);
            if (doc is not null) doc.DiskStamp = SafeStamp(p.Path!);
            if (current)
            {
                UpdateTitle();
                UpdateStatus();
            }
            _browser.RefreshFile(p.Path!);
        }
        message = projectSaved
            ? MainStrings.FormatMessage_367(path: path, message: ex.Message)
            : MainStrings.FormatMessage_368(path: path, message: ex.Message);
        if (interactive) Ui.Error(this, message, MainStrings.Message_298);
        return false;
    }

    /// <summary>저장/내보내기 결과 요약: 프레임 시트·부위 아틀라스·Sprite Library 각각(파일이 여러 개면 파일 수와 칸·라벨 합계).</summary>
    private static string ExportSummary(ProjectExportResult? e)
    {
        if (e is null || e.IsEmpty) return MainStrings.Ui_299;
        static string One(string label, IReadOnlyList<ExportResult> files, string? unit = null)
        {
            unit ??= MainStrings.Ui_300;
            var meta = files.Any(r => r.MetaWritten) ? " + .meta" : "";
            return files.Count == 1
                ? MainStrings.FormatMessage_370(label: label, imagePath: Path.GetFileName(files[0].ImagePath), unit: unit, spriteCount: files[0].SpriteCount, meta: meta)
                : MainStrings.FormatMessage_371(label: label, count: files.Count, unit: unit, spriteCount: files.Sum(r => r.SpriteCount), meta: meta);
        }
        var parts = new List<string>(3);
        if (e.Frames.Count > 0) parts.Add(One(MainStrings.Ui_301, e.Frames));
        if (e.Parts.Count > 0) parts.Add(One(MainStrings.Ui_302, e.Parts));
        if (e.Libraries.Count > 0) parts.Add(One("Sprite Library", e.Libraries, MainStrings.Ui_303));
        return " → " + string.Join(" · ", parts);
    }

    /// <summary>
    /// 지난번에 이 프로젝트가 내보냈지만 이번에 만들지 않은 옛 결과(이미지·.meta)를 앱 휴지통으로 옮기고 요약 문구를 돌려준다.
    /// 옮기지 못해도 내보내기는 성공으로 두고 알리기만 한다.
    /// </summary>
    private string TrashStale(ProjectExportResult? e)
    {
        if (e is null || e.Stale.Count == 0) return "";
        var first = Path.GetFileName(e.Stale[0]);
        var label = e.Stale.Count == 1 ? MainStrings.FormatExport_TrashOne(name: first)
            : MainStrings.FormatExport_TrashMany(name: first, count: e.Stale.Count - 1);
        string text;
        try
        {
            var entry = _browser.Trash.Add(e.Stale, label);
            text = MainStrings.FormatMessage_372(value: entry?.Files.Count ?? 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = MainStrings.FormatMessage_373(message: ex.Message);
        }
        return text;
    }

    /// <summary>Ctrl+E: 내보내기 설정 창. 파란 "내보내기"를 누르면 창을 닫고 지금 설정대로 내보낸다.</summary>
    private void OpenExportDialog()
    {
        if (!_ctx.HasProject) return;
        var dlg = new UnityExportDialog(_ctx, _settings, v => _settings.LastPpu = v);
        if (dlg.ShowDialog(this) == DialogResult.OK) ExportNow();
    }

    /// <summary>파일 > GIF 애니메이션으로 내보내기: 범위·배율·반복·배경을 고르는 창(프로젝트는 바꾸지 않음).</summary>
    private void ExportGif()
    {
        if (GifExportDialog.Run(_ctx, _settings, this) is not var (message, path)) return;
        ShowMessage(message);
        _browser.RefreshFile(path);
    }

    private void ExportNow()
    {
        FinishAutoSave(); // 자동 저장이 같은 내보내기 파일을 쓰는 중이면 끝난 뒤에
        var p = _ctx.Project!;
        if (p.Path is null && !SaveAs()) return;
        try
        {
            var plan = ProjectExport.BeginExport(p);
            var e = WorkOverlay.Run(this, UiStrings.Busy_Exporting, progress => ProjectExport.Write(p, plan, progress));
            ShowMessage(MainStrings.Message_304 + ExportSummary(e).TrimStart(' ', '→') + TrashStale(e));
            foreach (var r in e.All) _browser.RefreshFile(r.ImagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Ui.Error(this, MainStrings.FormatMessage_374(message: ex.Message), MainStrings.Export_FailureTitle);
        }
    }

    /// <summary>파일 목록에서 이름을 바꿈: 열린 문서들의 경로(프로젝트 파일 또는 가져온 PNG)를 따라 바꾼다.</summary>
    private void OnFileRenamed(string oldPath, string newPath)
    {
        int i = _settings.RecentFiles.FindIndex(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _settings.RecentFiles[i] = newPath;
        foreach (var doc in OpenDocs)
        {
            var source = doc.SourcePath;
            if (!RemapDocument(doc.Project, ref source, oldPath, newPath)) continue;
            doc.SourcePath = source;
            doc.DiskStamp = doc.DiskPath is { } disk ? SafeStamp(disk) : default;
            if (ReferenceEquals(doc, CurrentDoc)) ShowMessage(MainStrings.FormatMessage_375(value: Path.GetFileName(newPath)));
        }
        RefreshPanes();
        SyncUnsaved();
        _history.Renamed(oldPath, newPath, OpenProjects);
    }

    /// <summary>열린 프로젝트(모든 탭의 문서).</summary>
    private IEnumerable<SpriteProject> OpenProjects => OpenDocs.Select(d => d.Project);

    /// <summary>
    /// 이름이 바뀐 파일이 문서의 프로젝트 파일이면 프로젝트 경로(+내보내기 기록)를, 가져온 PNG면 그 경로와 제안된 프로젝트 경로를 바꾼다. 바꿨으면 true.
    /// </summary>
    private static bool RemapDocument(SpriteProject p, ref string? source, string oldPath, string newPath)
    {
        if (string.Equals(p.Path, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            p.Path = newPath;
            p.ExportedFiles = ProjectIO.RemapExportRecord(p.ExportedFiles, oldPath, newPath);
            return true;
        }
        if (!string.Equals(source, oldPath, StringComparison.OrdinalIgnoreCase)) return false;
        source = newPath;
        if (string.Equals(p.Path, ProjectImport.GetProjectPathForPng(oldPath), StringComparison.OrdinalIgnoreCase))
            p.Path = ProjectImport.GetProjectPathForPng(newPath);
        return true;
    }

    /// <summary>파일 목록에서 휴지통으로 보냄: 열린 문서의 파일이면 수정됨으로 둔다(탭은 그대로 — 저장하면 다시 만듦).</summary>
    private void OnFileDeleted(string path)
    {
        _settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _history.Deleted(path);
        foreach (var doc in OpenDocs.Where(d => d.Matches(path)))
        {
            doc.Project.IsDirty = true;
            ShowMessage(MainStrings.FormatMessage_376(value: Path.GetFileName(path)));
        }
        UpdateSaveButtons();
    }

    /// <summary>저장·열기 덮개나 자동 저장이 끝난 뒤(문서의 디스크 시각을 갱신한 뒤) <see cref="OnFilesChanged"/>를 부른다. 둘 다 없으면 바로.</summary>
    private void OnFilesChangedWhenIdle(IReadOnlyCollection<string> paths)
    {
        if (!WorkOverlay.IsActive && _autoSaved is null)
        {
            OnFilesChanged(paths);
            return;
        }
        Task.WhenAll(WorkOverlay.WhenIdle(), _autoSaved?.Task ?? Task.CompletedTask)
            .ContinueWith(_ => OnFilesChangedWhenIdle(paths), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 파일 감시: 지금 문서의 프로젝트 파일이 밖에서 바뀌었으면 다시 불러올지 묻는다.
    /// 다른 탭의 문서는 저장 안 한 변경이 있으면 그대로 두고 알리기만 하고(저장하면 덮어씀), 없으면 다음에 고를 때 다시 읽는다.
    /// </summary>
    private void OnFilesChanged(IReadOnlyCollection<string> paths)
    {
        var current = CurrentDoc;
        foreach (var doc in OpenDocs)
        {
            if (ReferenceEquals(doc, current)) continue;
            if (doc.DiskPath is not { } disk || !paths.Any(x => string.Equals(x, disk, StringComparison.OrdinalIgnoreCase)) || !File.Exists(disk)) continue;
            var diskStamp = SafeStamp(disk);
            if (diskStamp == doc.DiskStamp) continue;
            doc.DiskStamp = diskStamp;
            if (doc.Project.IsDirty) ShowMessage(MainStrings.FormatMessage_378(value: Path.GetFileName(disk)));
            else doc.ChangedOnDisk = true;
        }
        if (current is null) return;
        var p = current.Project;
        if (p.Path is not { } path || !paths.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase))) return;
        // 파일이 없어졌으면 되돌릴 것도 없다(되돌리기 단추를 끈다).
        if (!File.Exists(path)) { UpdateSaveButtons(); return; }
        var stamp = SafeStamp(path);
        if (stamp == current.DiskStamp) return;
        current.DiskStamp = stamp;
        var message = p.IsDirty
            ? MainStrings.FormatMessage_379(displayName: p.DisplayName)
            : MainStrings.FormatMessage_380(displayName: p.DisplayName);
        // 묻는 동안 자동 저장이 밖에서 바뀐 파일을 덮어쓰지 않게 멈춘다.
        _autoSave.Stop();
        if (Ui.Ask(this, message, MainStrings.Message_308, MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes)
        {
            ShowMessage(MainStrings.Message_309);
            RestartAutoSave();
            return;
        }
        ReloadDocument(current, path, interactive: true, out _);
    }

    // ───────────── 편집 ─────────────

    private void Undo()
    {
        // 파일 브라우저에 포커스가 있으면 마지막 파일 삭제를 되살린다(앱 휴지통).
        if (_browser.IsKeyboardFocusWithin)
        {
            _browser.RestoreLastDeleted();
            return;
        }
        // 휘던 곡선은 버리고, 변형·캔버스 크기 모드면 시작 전으로 되돌린다(Esc와 같음).
        if (_canvas.CancelCurve() || CancelMode()) return;
        switch (_undoOrder.NextUndo)
        {
            case UndoTimeline.Side.Palette:
                _colors.RestorePalettes(_undoOrder.UndoPalette().Before);
                ShowMessage(MainStrings.FormatMessage_381(label: UiStrings.Palette_UndoLabel));
                break;
            case UndoTimeline.Side.Document:
                var label = _ctx.Project?.History.UndoLabel;
                if (_ctx.UndoStep()) ShowMessage(MainStrings.FormatMessage_381(label: label));
                break;
        }
    }

    /// <summary>변형·캔버스 크기 모드면 취소하고 알린다(Esc·Ctrl+Z). 모드가 아니었으면 false.</summary>
    private bool CancelMode()
    {
        if (_ctx.IsTransforming)
        {
            _ctx.CancelTransform();
            ShowMessage(MainStrings.Message_310);
            return true;
        }
        if (!_ctx.IsCanvasResizing) return false;
        _ctx.CancelCanvasResize();
        ShowMessage(MainStrings.Message_311);
        return true;
    }

    private void Redo()
    {
        switch (_undoOrder.NextRedo)
        {
            case UndoTimeline.Side.Palette:
                _colors.RestorePalettes(_undoOrder.RedoPalette().After);
                ShowMessage(MainStrings.FormatMessage_382(label: UiStrings.Palette_UndoLabel));
                break;
            case UndoTimeline.Side.Document:
                var label = _ctx.Project?.History.RedoLabel;
                if (_ctx.RedoStep()) ShowMessage(MainStrings.FormatMessage_382(label: label));
                break;
        }
    }

    private void Copy()
    {
        // 타임라인에 포커스가 있으면 셀/프레임 대상
        if (_timeline.IsGridFocused) { _timeline.CopySelection(false); ShowMessage(MainStrings.Message_312); return; }
        PixelImage? clip;
        if (_ctx.Selection.HasSelection) clip = _ctx.Selection.Copy(_ctx.EditImageOrEmpty);
        else
        {
            // 선택이 없으면 부위는 그 부위 셀, 그 밖에는 현재 프레임 합성 그림 전체를 복사한다.
            clip = _ctx.CurrentPart is not null ? _ctx.EditImageOrEmpty.Clone() : _ctx.Project!.Flatten(_ctx.CurrentFrame);
        }
        if (clip is null) return;
        _ctx.Clipboard = clip;
        PutOnSystemClipboard(clip);
        ShowMessage(MainStrings.FormatMessage_383(width: clip.Width, height: clip.Height));
    }

    private void Cut()
    {
        // 타임라인에 포커스가 있으면 셀/프레임 대상
        if (_timeline.IsGridFocused) { _timeline.CopySelection(true); ShowMessage(MainStrings.Message_313); return; }
        if (!_ctx.Selection.HasSelection)
        {
            ShowMessage(MainStrings.Message_314);
            return;
        }
        if (!_ctx.CanEdit) { ShowMessage(MainStrings.Message_315); return; }
        _ctx.BeginSelectionEdit();
        var clip = _ctx.Selection.Cut(_ctx.EditImage!)!;
        _ctx.NotifyPixels();
        _ctx.CommitFloating();
        _ctx.Clipboard = clip;
        PutOnSystemClipboard(clip);
        ShowMessage(MainStrings.FormatMessage_384(width: clip.Width, height: clip.Height));
    }

    /// <summary>시스템 클립보드에 PNG로 올린다(다른 그림 프로그램에 붙여넣기용). 실패하면 앱 내부 클립보드만 쓴다.</summary>
    private void PutOnSystemClipboard(PixelImage clip) => ImageActions.Copy(this, clip);

    private PixelImage? ReadClipboard()
    {
        if (Clipboard is { } cb)
        {
            try
            {
                if (Ui.Wait(cb.TryGetValueAsync(ImageActions.PngClipboardFormat)) is { Length: > 0 } bytes)
                {
                    using var png = new MemoryStream(bytes, writable: false);
                    return PngIO.Load(png);
                }
                using var bmp = Ui.Wait(cb.TryGetBitmapAsync());
                if (bmp is not null)
                {
                    using var ms = new MemoryStream();
                    bmp.Save(ms);
                    ms.Position = 0;
                    return PngIO.Load(ms);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 읽을 수 없는 클립보드 내용이면 앱 내부 클립보드를 쓴다.
            }
        }
        return _ctx.Clipboard;
    }

    private void Paste()
    {
        // 타임라인에 포커스가 있으면 셀/프레임 대상
        if (_timeline.IsGridFocused && _timeline.PasteSelection()) { ShowMessage(MainStrings.Message_316); return; }
        var clip = ReadClipboard();
        if (clip is null)
        {
            ShowMessage(MainStrings.Message_317);
            return;
        }
        if (!_ctx.CanEdit) { ShowMessage(MainStrings.Message_318); return; }
        // 끌어 옮길 수 있는 선택 도구(사각·원·자유)는 그대로 두고, 그 밖이면 사각 선택으로.
        if (_ctx.Tool is not (ToolKind.Select or ToolKind.EllipseSelect or ToolKind.Lasso)) _ctx.Tool = ToolKind.Select;
        _ctx.CommitFloating();
        _ctx.BeginSelectionEdit();
        _ctx.Selection.Paste(_ctx.EditImage!, clip, _canvas.PasteLocation());
        _ctx.NotifyPixels();
        ShowMessage(MainStrings.FormatMessage_385(width: clip.Width, height: clip.Height));
        _canvas.Focus();
    }

    private void DeleteSelection()
    {
        if (!_ctx.Selection.HasSelection || !_ctx.CanEdit) return;
        _ctx.BeginSelectionEdit();
        _ctx.Selection.Delete(_ctx.EditImage!);
        _ctx.NotifyPixels();
        _ctx.CommitFloating();
    }

    private void SelectAll()
    {
        if (!EditorContext.IsSelectionTool(_ctx.Tool) && _ctx.Tool != ToolKind.Move) _ctx.Tool = ToolKind.Select;
        _ctx.CommitFloating();
        _ctx.Selection.SelectAll(_ctx.EditImageOrEmpty);
    }

    private void Deselect()
    {
        if (!_ctx.HasProject || _canvas.ClearPartSelection()) return;
        _ctx.CommitFloating();
        _ctx.Selection.Deselect(_ctx.EditImageOrEmpty);
    }

    /// <summary>선택이 있으면 선택 내용, 없으면 현재 셀을 뒤집는다.</summary>
    private void Flip(bool horizontal)
    {
        if (!_ctx.CanEdit) { ShowMessage(MainStrings.Message_319); return; }
        string displayLabel = horizontal ? MainStrings.Ui_320 : MainStrings.Ui_321;
        if (_ctx.Selection.HasSelection)
        {
            _ctx.BeginSelectionEdit();
            if (horizontal) _ctx.Selection.FlipHorizontal(_ctx.EditImage!);
            else _ctx.Selection.FlipVertical(_ctx.EditImage!);
            _ctx.NotifyPixels();
            ShowMessage(MainStrings.FormatMessage_386(label: displayLabel));
            return;
        }
        _ctx.CommitFloating();
        _ctx.BeginStroke();
        var img = _ctx.EditImage!;
        img.CopyFrom(horizontal ? ImageTransform.FlipHorizontal(img) : ImageTransform.FlipVertical(img));
        _ctx.NotifyPixels();
        _ctx.CommitStroke(displayLabel);
        ShowMessage(MainStrings.FormatMessage_387(label: displayLabel));
    }

    /// <summary>선택이 있으면 선택 내용, 없으면 캔버스 전체(모든 프레임)를 90° 회전.</summary>
    private void Rotate(bool clockwise)
    {
        if (_ctx.Selection.HasSelection)
        {
            if (!_ctx.CanEdit) { ShowMessage(MainStrings.Message_322); return; }
            _ctx.BeginSelectionEdit();
            _ctx.Selection.Rotate90(_ctx.EditImage!, clockwise);
            _ctx.NotifyPixels();
            ShowMessage(MainStrings.Message_323);
            return;
        }
        _ctx.Structural(() => _ctx.Project!.RotateCanvas90(clockwise));
        _canvas.FitToWindow();
        ShowMessage(MainStrings.Message_324);
    }

    /// <summary>
    /// 변형 모드(Ctrl+T): 선택 영역(없으면 현재 셀의 칠한 픽셀 전체)을 손잡이로 회전·크기·옮기기. Enter 적용, Esc·Ctrl+Z 취소.
    /// 타임라인에서 셀 여러 칸(프레임·레이어 선택 포함)을 골라 두었으면 그 셀들을 모두 같은 변형으로 함께 바꾼다.
    /// </summary>
    private void BeginTransform()
    {
        if (_ctx.BeginTransform() is { } error)
        {
            ShowMessage(error);
            return;
        }
        int cels = _ctx.TransformCelCount, locked = _ctx.TransformLockedSkipped;
        string prefix = cels > 1 ? MainStrings.FormatTransform_Cels(count: cels) : "";
        if (locked > 0) prefix += MainStrings.FormatTransform_LockedSkipped(count: locked);
        ShowMessage(prefix + MainStrings.Message_325);
        _canvas.Focus();
    }

    /// <summary>선택 영역(없으면 현재 셀 전체)의 불투명 픽셀 둘레에 기본색으로 1픽셀 외곽선.</summary>
    private void AddOutline(bool diagonal)
    {
        if (!_ctx.CanEdit) { ShowMessage(MainStrings.Message_326); return; }
        _ctx.CommitFloating();
        PixelMask region = _ctx.Selection.HasSelection ? _ctx.Selection.Region : new Rectangle(0, 0, _ctx.CanvasWidth, _ctx.CanvasHeight);
        _ctx.BeginStroke();
        int n = PixelOps.AddOutline(_ctx.EditImage!, region, _ctx.Primary, diagonal);
        _ctx.NotifyPixels();
        _ctx.CommitStroke(MainStrings.Edit_AddOutline);
        ShowMessage(n > 0 ? MainStrings.FormatMessage_388(n: n) : MainStrings.Message_NoOutlinePixels);
    }

    private void ReplaceColor()
    {
        if (!_ctx.CanEdit) { ShowMessage(MainStrings.Message_327); return; }
        _ctx.CommitFloating();
        var dlg = new ReplaceColorDialog(_ctx.Primary, _ctx.Secondary, _ctx.Selection.HasSelection);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        PixelMask? clip = dlg.SelectionOnly ? _ctx.Selection.Region : null;
        _ctx.BeginStroke();
        int n = Raster.ReplaceColor(_ctx.EditImage!, dlg.From, dlg.To, clip);
        _ctx.NotifyPixels();
        _ctx.CommitStroke(MainStrings.Edit_ReplaceColor);
        ShowMessage(MainStrings.FormatMessage_389(n: n));
    }

    /// <summary>현재 그룹(부위면 그 그룹)의 스킨을 앞뒤로 바꾼다(착장 전환, 실행취소 기록 없음).</summary>
    private void StepSkin(int delta)
    {
        if (_ctx.CurrentGroup is not { } group)
        {
            ShowMessage(MainStrings.Message_328);
            return;
        }
        if (_ctx.StepSkin(group, delta)) ShowMessage(MainStrings.FormatMessage_390(name: group.Name, name2: group.ActiveSkin!.Name));
        else ShowMessage(MainStrings.FormatMessage_391(name: group.Name));
    }

    /// <summary>현재 프레임 그림(시트)을 격자/자동으로 나눠 칸마다 프레임 하나인 프로젝트로 바꾼다.</summary>
    private void SplitIntoFrames()
    {
        var p = _ctx.Project!;
        _ctx.CommitFloating();
        var image = p.Flatten(_ctx.CurrentFrame);
        var dlg = new SplitFramesDialog(image, 16, 16);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var rects = dlg.Rects;
        if (p.Frames.Count > 1 && Ui.Ask(this,
                MainStrings.FormatMessage_392(count: p.Frames.Count, count2: rects.Count),
                MainStrings.Ui_331, MessageButtons.OKCancel, MessageIcon.Warning) != DialogResult.OK) return;
        var doc = new Document(image);
        doc.Sprite.CopyFrom(p.Sprite);
        doc.Sprite.Mode = SpriteMode.Multiple;
        doc.Slices.AddRange(Gpixel.Core.Slicing.NamingRules.CreateSlices(rects,
            new Gpixel.Core.Slicing.NamingOptions { BaseName = p.BaseName }, p.Sprite.DefaultPivot));
        var split = ProjectImport.FromDocument(doc);
        split.Export.CopyFrom(p.Export);
        split.ExportedFiles = p.ExportedFiles;
        split.Path = p.Path;
        split.History.Clear();
        split.IsDirty = true;
        if (CurrentDoc is { } current) ReplaceProject(current, split, keepView: false);
        ShowMessage(MainStrings.FormatMessage_393(count: split.Frames.Count, width: split.Width, height: split.Height));
    }

    /// <summary>
    /// 캔버스 크기 모드(Ctrl+Alt+C): 캔버스 손잡이와 도구 설정 숫자로 새 크기, 그림 자리·크기를 정한다. Enter·확인 = 적용, Esc·Ctrl+Z = 취소.
    /// 부위 크기는 "부위 크기…"로 바꾼다.
    /// </summary>
    private void BeginCanvasResize()
    {
        if (_ctx.BeginCanvasResize() is { } error)
        {
            ShowMessage(error);
            return;
        }
        ShowMessage(MainStrings.Message_332);
        _canvas.Focus();
    }

    // ───────────── AI 채팅(Claude·Codex, AI 도구 호스트) ─────────────

    /// <summary>편집기 안 MCP 서버를 처음 만들고 시작한다. 실패하면 사용자에게 알리고 false.</summary>
    private bool EnsureMcp(Visual owner)
    {
        if (_mcp is not null) return true;
        var server = new Ai.McpServer(new Ai.PixelTools(this));
        try { server.Start(); }
        catch (Exception ex) when (ex is System.Net.HttpListenerException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            server.Dispose();
            Ui.Error(owner, MainStrings.FormatMessage_394(message: ex.Message));
            return false;
        }
        _mcp = server;
        return true;
    }

    /// <summary>AI 설치·버전·로그인·MCP 연결 상태 창을 연다. 채팅 진입이면 확인 단추가 채팅을 이어 열겠다고 표시한다.</summary>
    private DialogResult ShowAiStatus(Window owner, bool continueToAi = false)
    {
        if (!EnsureMcp(owner)) return DialogResult.Cancel;
        var result = new Ai.AiStatusDialog(_mcp!, _settings, continueToAi).ShowDialog(owner);
        SaveSettings();
        return result;
    }

    /// <summary>AI 채팅 창을 연다(이미 열려 있으면 앞으로). 첫 사용·앱/CLI 변경 뒤에는 상태 점검 창을 먼저 보인다.</summary>
    private void ToggleChat()
    {
        if (_chat is not null)
        {
            if (_chat.WindowState == WindowState.Minimized) _chat.WindowState = WindowState.Normal;
            _chat.Activate();
            return;
        }
        if (!EnsureMcp(this)) return;
        if (Ai.AiStatusProbe.ShouldShow(_settings) && ShowAiStatus(this, continueToAi: true) != DialogResult.OK) return;
        var chat = new Ai.ChatWindow(_ctx, _mcp!, _settings);
        chat.Closed += (_, _) => { if (ReferenceEquals(_chat, chat)) _chat = null; };
        _chat = chat;
        chat.Show(this);
    }

    /// <summary>AI 참고 자료 창을 연다(이미 열려 있으면 앞으로). 자료 폴더를 열 수 없으면 알린다.</summary>
    private void ShowReferences()
    {
        if (_refsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        Gpixel.Core.References.ReferenceLibrary lib;
        try { lib = ((Ai.IAiHost)this).References; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, UiStrings.FormatRefs_OpenFailed(folder: AppReferences.FolderOf(_settings), message: ex.Message));
            return;
        }
        var window = new ReferenceManagerWindow(lib, _ctx, CaptionWithAi);
        window.Closed += (_, _) => { if (ReferenceEquals(_refsWindow, window)) _refsWindow = null; };
        _refsWindow = window;
        window.Show(this);
    }

    /// <summary>"AI로 설명 붙이기": 채팅 창을 열고 보이는 탭의 입력란에 부탁을 넣는다(보내지는 않음).</summary>
    private void CaptionWithAi(string request)
    {
        ToggleChat();
        _chat?.PrefillInput(request);
    }

    /// <summary>앱을 닫을 때: 채팅이 열려 있었는지 기억하고 AI 프로세스·MCP 서버를 끝낸다.</summary>
    private void ShutdownChat()
    {
        _settings.ChatOpen = _chat is not null;
        if (_chat is { } chat)
        {
            _chat = null;
            chat.Shutdown();
            chat.Close();
        }
        _mcp?.Dispose();
        _mcp = null;
    }

    EditorContext Ai.IAiHost.Context => _ctx;

    string? Ai.IAiHost.CurrentFolder => _browser.CurrentFolder ?? _browser.RootFolder;

    IReadOnlyList<Color32> Ai.IAiHost.Palette => _colors.ActivePalette;

    void Ai.IAiHost.SetPalette(IReadOnlyList<Color32> colors) => _colors.SetActivePalette(colors);

    bool Ai.IAiHost.CreateProject(int width, int height, string? fileName, Color32? background, out string message)
    {
        if (width < 1 || height < 1 || width > PixelImage.MaxSize || height > PixelImage.MaxSize)
        {
            message = MainStrings.FormatMessage_395(maxSize: PixelImage.MaxSize, width: width, height: height);
            return false;
        }
        string? path = null;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var folder = _browser.CurrentFolder ?? _browser.RootFolder;
            if (folder is null)
            {
                message = MainStrings.Message_333;
                return false;
            }
            var name = Path.GetFileName(fileName.Trim());
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                message = MainStrings.FormatMessage_396(fileName: fileName);
                return false;
            }
            if (!name.EndsWith(SpriteProject.FileExtension, StringComparison.OrdinalIgnoreCase)) name += SpriteProject.FileExtension;
            path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                message = MainStrings.FormatMessage_397(path: path);
                return false;
            }
        }
        var p = BuildProject(width, height, background);
        OpenInNewTab(new OpenDocument(p, null));
        if (path is null)
        {
            p.IsDirty = true;
            message = MainStrings.FormatMessage_398(width: width, height: height);
            ShowMessage(message);
            return true;
        }
        if (!SaveTo(path, interactive: false, out var saveMessage))
        {
            message = MainStrings.FormatMessage_399(saveMessage: saveMessage);
            return true;
        }
        _browser.RefreshFile(path);
        _browser.SelectFile(path);
        message = MainStrings.FormatMessage_400(width: width, height: height, path: path);
        return true;
    }

    bool Ai.IAiHost.SaveProject(out string message)
    {
        if (_ctx.Project is not { } p)
        {
            message = MainStrings.Message_334;
            return false;
        }
        if (p.Path is null)
        {
            message = MainStrings.Message_335;
            return false;
        }
        return SaveTo(p.Path, interactive: false, out message);
    }

    bool Ai.IAiHost.OpenFile(string path, out string message) => OpenFile(path, interactive: false, out message);

    Gpixel.Core.References.ReferenceLibrary Ai.IAiHost.References => _refs ??= AppReferences.Open(_settings);

    AppSettings Ai.IAiHost.Settings => _settings;
}
