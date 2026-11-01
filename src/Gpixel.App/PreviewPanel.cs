using System.Diagnostics;
using Avalonia.Controls.Templates;
using Gpixel.Core.Edit;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// Piskel식 미리보기(오른쪽 위): 현재 태그(없으면 전체 프레임)를 실시간으로 재생한다.
/// 자체 FPS(0 = 프레임 길이 사용), 배율 1×/2×/4×/맞춤, 배경색을 고를 수 있고, 멈춰 있으면 현재 프레임을 보여 준다.
/// 그룹(스킨 슬롯)이 있으면 아래쪽 "착장" 줄에서 그룹마다(타임라인처럼 위→아래) 보일 스킨을 고르고, "관리…"로 스킨 관리 창을 연다.
/// 그림을 우클릭하면 보이는 프레임을 복사(1×·×4)·PNG로 저장하거나 GIF 내보내기 창을 연다.
/// </summary>
public sealed class PreviewPanel : UserControl
{
    private static readonly string[] Backgrounds = [UiStrings.S_PreviewPanel_Backgrounds_D9726B3169, UiStrings.S_PreviewPanel_Backgrounds_7DB5254C5D, UiStrings.S_PreviewPanel_Backgrounds_E200745781, UiStrings.S_PreviewPanel_Backgrounds_3785BA5E7F, UiStrings.S_PreviewPanel_Backgrounds_9E0985F1EA, UiStrings.S_PreviewPanel_Backgrounds_0814BC3C7C];
    private static readonly Color?[] PresetColors = [null, Colors.Black, Colors.White, Color.FromRgb(128, 128, 128), Color.FromRgb(60, 140, 70), null];

    /// <summary>배경 목록 항목: 이름 + 색 견본(null = 체커).</summary>
    private sealed record BackgroundOption(string Name, Color? Color);

    private readonly EditorContext _ctx;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Stopwatch _clock = new();
    private readonly View _view;
    private readonly Button _play;
    private readonly NumericUpDown _fps;
    private readonly ComboBox _background;
    private readonly Button _scaleButton;
    private static readonly int[] ScaleSteps = [0, 1, 2, 4];
    private int _scale; // 0 = 맞춤
    private Color? _bgColor;
    private Color _customColor = Color.FromRgb(40, 60, 90);
    private bool _suppressBackground;
    private List<int> _order = new();
    private int _orderIndex;
    private double _elapsedInFrame;
    /// <summary>착장 줄: 그룹마다 이름 + 스킨 고르기 상자. 그룹이 없으면 숨김.</summary>
    private readonly WrapPanel _outfit = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 1) };
    private readonly Border _outfitHost;
    /// <summary>착장 줄을 다시 만드는 중(상자 선택 바뀜을 스킨 고르기로 보지 않음).</summary>
    private bool _syncingOutfit;

    /// <summary>재생 중 보이는 프레임이 바뀜(-1 = 재생 멈춤).</summary>
    public event EventHandler<int>? PlayingFrameChanged;

    /// <summary>상태 표시줄에 보일 알림.</summary>
    public event EventHandler<string>? Message;
    /// <summary>"이미지 복사": 보이는 프레임 그림을 클립보드(시스템·앱)에 올려 달라.</summary>
    public event EventHandler<PixelImage>? CopyRequested;
    /// <summary>"GIF로 저장…": GIF 내보내기 창을 열어 달라.</summary>
    public event EventHandler? GifExportRequested;
    /// <summary>파일을 저장함(파일 목록 새로 고침용).</summary>
    public event EventHandler<string>? FileSaved;

    /// <summary>"크게 복사"의 최근접 확대 배율.</summary>
    private const int EnlargedCopyScale = 4;

    public PreviewPanel(EditorContext ctx)
    {
        _ctx = ctx;
        Background = AppTheme.PanelBrush;
        _view = new View(this);
        _play = Ui.IconButton(VectorIcon.Play, UiStrings.S_PreviewPanel_PreviewPanel_0F8DBBD3C8, (_, _) => TogglePlay(), 34, 13);
        _fps = Ui.Num(0, 60, 0, 46)
            .Presets([0, 4, 6, 8, 10, 12, 15, 20, 24, 30, 60], [], v => v == 0 ? UiStrings.S_PreviewPanel_PreviewPanel_DFA250A1B3 : $"{v:0} FPS")
            .RecentKey("preview.fps")
            .Tip(UiStrings.S_PreviewPanel_PreviewPanel_F37E165A04);
        _scaleButton = new Button
        {
            Width = 52,
            Margin = new Thickness(1, 2, 1, 2),
            Padding = new Thickness(0, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        }.Tip(UiStrings.S_PreviewPanel_PreviewPanel_6984708585);
        _scaleButton.Click += (_, _) => StepScale(+1);
        _scaleButton.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            StepScale(-1);
            e.Handled = true;
        };
        _background = new ComboBox
        {
            Margin = new Thickness(1, 2, 1, 2),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            // 펼친 목록은 견본 + 이름, 닫힌 상자는 견본만.
            ItemTemplate = new FuncDataTemplate<BackgroundOption>((o, _) => SwatchRow(o), supportsRecycling: false),
            SelectionBoxItemTemplate = new FuncDataTemplate<BackgroundOption>((o, _) => Swatch(o), supportsRecycling: false),
        };
        for (int i = 0; i < Backgrounds.Length; i++)
            _background.Items.Add(new BackgroundOption(Backgrounds[i], i == Backgrounds.Length - 1 ? _customColor : PresetColors[i]));
        _background.SelectedIndex = 0;
        _background.Tip(UiStrings.S_PreviewPanel_PreviewPanel_83BA43E90E);
        _background.SelectionChanged += (_, _) => { if (!_suppressBackground) OnBackgroundChanged(); };
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Background = AppTheme.PanelBrush };
        DockPanel.SetDock(bar, Dock.Bottom);
        bar.Children.Add(_play);
        var fpsLabel = Ui.Label("FPS");
        fpsLabel.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(fpsLabel);
        bar.Children.Add(_fps);
        bar.Children.Add(_scaleButton);
        bar.Children.Add(_background);
        _outfitHost = new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsVisible = false,
            Child = new ScrollViewer
            {
                Content = _outfit,
                MaxHeight = 92,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            },
        };
        DockPanel.SetDock(_outfitHost, Dock.Bottom);
        var root = new DockPanel();
        root.Children.Add(Ui.Header(UiStrings.S_PreviewPanel_PreviewPanel_015AD8431D));
        root.Children.Add(bar);
        root.Children.Add(_outfitHost);
        root.Children.Add(_view);
        PopupMenus.Attach(_view, ViewMenu);
        Content = root;
        SetScale(0);

        _timer.Tick += (_, _) => Tick();
        ctx.ProjectReplaced += (_, _) =>
        {
            Stop();
            RebuildOutfit();
            _view.InvalidateVisual();
        };
        ctx.ViewChanged += (_, _) => _view.InvalidateVisual();
        ctx.RenderUpdated += (_, _) => _view.InvalidateVisual();
        _busyTimer.Tick += (_, _) => _view.InvalidateVisual();
        // 재생 중에 태그를 만들거나 고르면 그 태그 안에서 반복하도록 순서를 바꾼다.
        ctx.CurrentChanged += (_, _) => RefreshOrder();
        ctx.ProjectChanged += (_, e) =>
        {
            if ((e.Change & (ProjectChange.Tags | ProjectChange.Frames)) != 0) RefreshOrder();
            // 스킨 고르기·추가·삭제·이름·순서와 그룹 추가·삭제·이름은 모두 Layers 변경이다(실행취소 포함).
            if ((e.Change & ProjectChange.Layers) != 0) RebuildOutfit();
        };
        RebuildOutfit();
    }

    // ───────────── 착장(그룹마다 보일 스킨) ─────────────

    /// <summary>착장 줄을 프로젝트에 맞춰 다시 만든다(그룹이 없으면 숨김).</summary>
    private void RebuildOutfit()
    {
        _syncingOutfit = true;
        try
        {
            _outfit.Children.Clear();
            var p = _ctx.Project;
            var groups = p is null ? [] : p.Layers.Where(l => l.IsGroup).Reverse().ToList();
            _outfitHost.IsVisible = groups.Count > 0;
            if (p is null || groups.Count == 0) return;
            var title = Ui.Label(UiStrings.S_PreviewPanel_RebuildOutfit_E5D30D48F5);
            title.Foreground = AppTheme.DimTextBrush;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(2, 0, 4, 0);
            _outfit.Children.Add(title);
            foreach (var group in groups) _outfit.Children.Add(OutfitItem(p, group));
            var manage = new Button
            {
                Content = UiStrings.S_PreviewPanel_RebuildOutfit_090268890B,
                Padding = new Thickness(6, 0),
                MinHeight = 20,
                Margin = new Thickness(2, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            }.Tip(UiStrings.S_PreviewPanel_RebuildOutfit_12AE9802EA);
            manage.Click += (_, _) => SkinManagerWindow.Open(_ctx, this);
            _outfit.Children.Add(manage);
        }
        finally
        {
            _syncingOutfit = false;
        }
    }

    /// <summary>
    /// 그룹 하나: 이름(누르면 스킨 관리 창이 그 그룹으로, 부위만 따로 고른 스킨이 있으면 "*") + 스킨 상자(스킨이 하나뿐이면 끔).
    /// 고르면 그 그룹의 지금 스킨이 바뀐다(부위만 따로 고른 스킨은 그대로).
    /// </summary>
    private Control OutfitItem(SpriteProject p, Layer group)
    {
        int overrides = p.ChildrenOf(group).Count(x => x.SkinOverride is not null);
        var label = new TextBlock
        {
            Text = overrides > 0 ? group.Name + " *" : group.Name,
            Foreground = group.Visible ? AppTheme.TextBrush : AppTheme.DimTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 80,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 2, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        }.Tip(overrides > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PreviewPanel_OutfitItem_210601903B,group.Name,overrides) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PreviewPanel_OutfitItem_0C4037D29A,group.Name));
        label.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Dispatcher.UIThread.Post(() => SkinManagerWindow.Open(_ctx, this, group));
        };
        var combo = new ComboBox
        {
            Width = 92,
            MinWidth = 0,
            Margin = new Thickness(1, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = group.Skins.Count > 1,
        };
        var skins = group.Skins.ToList();
        for (int i = 0; i < skins.Count; i++)
        {
            combo.Items.Add(i == 0 && skins.Count > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PreviewPanel_OutfitItem_CC61BCD084,skins[i].Name) : skins[i].Name);
            if (ReferenceEquals(skins[i], group.ActiveSkin)) combo.SelectedIndex = i;
        }
        combo.Tip(skins.Count > 1
            ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PreviewPanel_OutfitItem_1F81422509,group.Name)
            : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PreviewPanel_OutfitItem_78809E4FDB,group.Name));
        combo.SelectionChanged += (_, _) =>
        {
            if (_syncingOutfit || combo.SelectedIndex < 0 || combo.SelectedIndex >= skins.Count) return;
            var skin = skins[combo.SelectedIndex];
            // 고르기가 착장 줄을 다시 만들므로, 상자가 닫힌 뒤에 바꾼다.
            Dispatcher.UIThread.Post(() =>
            {
                if (_ctx.Project is { } p && p.Layers.Contains(group) && group.Skins.Contains(skin)) _ctx.SetActiveSkin(group, skin);
            });
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { label, combo } };
    }

    private void StepScale(int direction)
    {
        int i = Array.IndexOf(ScaleSteps, _scale);
        SetScale(ScaleSteps[(i + direction + ScaleSteps.Length) % ScaleSteps.Length]);
    }

    private void SetScale(int scale)
    {
        _scale = scale;
        _scaleButton.Content = scale == 0 ? UiStrings.S_PreviewPanel_SetScale_EBF1B9D1AA : $"{scale}×";
        _view.InvalidateVisual();
    }

    /// <summary>배율 설정값(0 = 맞춤).</summary>
    public int ZoomSetting
    {
        get => _scale;
        set => SetScale(value is 1 or 2 or 4 ? value : 0);
    }

    /// <summary>미리보기 FPS(0 = 프레임 길이).</summary>
    public int FpsSetting
    {
        get => (int)_fps.NumValue();
        set => _fps.Value = Math.Clamp(value, 0, 60);
    }

    /// <summary>배경 설정값("checker" 또는 #RRGGBB).</summary>
    public string BackgroundSetting
    {
        get => _bgColor is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "checker";
        set
        {
            if (value != "checker" && Color32.TryParseHex(value, out var c))
            {
                _customColor = c.ToColor();
                _bgColor = _customColor;
                UpdateCustomOption();
                int idx = Array.FindIndex(PresetColors, p => p == _customColor);
                _suppressBackground = true;
                _background.SelectedIndex = idx >= 0 ? idx : Backgrounds.Length - 1;
                _suppressBackground = false;
            }
            _view.InvalidateVisual();
        }
    }

    private void OnBackgroundChanged()
    {
        int i = _background.SelectedIndex;
        if (i < 0) return;
        if (i == Backgrounds.Length - 1)
        {
            if (Ui.PickColor(this, _customColor.ToColor32()) is { } picked) _customColor = picked.ToColor();
            _bgColor = _customColor;
            UpdateCustomOption();
        }
        else _bgColor = PresetColors[i];
        _view.InvalidateVisual();
    }

    /// <summary>"사용자 색…" 항목의 견본을 현재 사용자 색으로 바꾼다(선택은 유지).</summary>
    private void UpdateCustomOption()
    {
        int last = Backgrounds.Length - 1;
        if (_background.Items[last] is BackgroundOption { Color: var c } && c == _customColor) return;
        bool selected = _background.SelectedIndex == last;
        _suppressBackground = true;
        _background.Items[last] = new BackgroundOption(Backgrounds[last], _customColor);
        if (selected) _background.SelectedIndex = last;
        _suppressBackground = false;
    }

    /// <summary>둥근 색 견본 + 이름(닫힌 상자와 펼친 목록 모두).</summary>
    private static Control SwatchRow(BackgroundOption? o) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children = { Swatch(o), new TextBlock { Text = o?.Name ?? "", VerticalAlignment = VerticalAlignment.Center } },
    };

    private static Control Swatch(BackgroundOption? o)
    {
        return new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new SwatchFill(o?.Color),
        };
    }

    /// <summary>견본 안쪽: 색이 없으면 체커.</summary>
    private sealed class SwatchFill(Color? color) : Control
    {
        public override void Render(DrawingContext g)
        {
            var r = new Rect(Bounds.Size);
            if (color is { } c) g.FillRectangle(new SolidColorBrush(c), r);
            else AppTheme.DrawChecker(g, r, 4);
        }
    }

    // ───────────── 우클릭 메뉴 ─────────────

    /// <summary>그림 우클릭 메뉴: 연 순간 보이던 프레임(재생 중이어도)을 대상으로 한다. 문서가 없으면 모두 끔.</summary>
    private IEnumerable<object> ViewMenu()
    {
        bool has = _ctx.HasProject && _ctx.FrameCount > 0;
        int frame = has ? Math.Clamp(ShownFrame, 0, _ctx.FrameCount - 1) : 0;
        return
        [
            Item(UiStrings.Preview_Menu_CopyImage, VectorIcon.Copy, () => CopyFrame(frame, 1), has),
            Item(UiStrings.Preview_Menu_CopyEnlarged, VectorIcon.Maximize2, () => CopyFrame(frame, EnlargedCopyScale), has),
            new Separator(),
            Item(UiStrings.Preview_Menu_SaveFrame, VectorIcon.ImageDown, () => SaveFrame(frame), has),
            Item(UiStrings.Preview_Menu_SaveGif, VectorIcon.Film, () => GifExportRequested?.Invoke(this, EventArgs.Empty), has),
        ];
    }

    private static MenuItem Item(string text, VectorIcon icon, Action action, bool enabled)
    {
        var item = new MenuItem { Header = text, Icon = Ui.Icon(icon, 14), IsEnabled = enabled };
        // 메뉴가 닫힌 뒤에 한다(파일 대화상자가 메뉴 위에 뜨지 않게).
        item.Click += (_, _) => Dispatcher.UIThread.Post(action);
        return item;
    }

    /// <summary>
    /// 미리보기가 그리는 그대로의 프레임 그림(보이는 레이어·효과·지금 착장, 투명 유지, 미리보기 배경 없음)을
    /// <paramref name="scale"/>배 최근접 확대한 새 그림. 프레임이 없으면 null.
    /// </summary>
    private PixelImage? FrameImage(int frame, int scale)
    {
        if (_ctx.FrameImage(frame) is not { } img) return null;
        return scale > 1 ? ImageTransform.ScaleNearest(img, img.Width * scale, img.Height * scale) : img.Clone();
    }

    private void CopyFrame(int frame, int scale)
    {
        if (FrameImage(frame, scale) is not { } img) return;
        CopyRequested?.Invoke(this, img);
        Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.Preview_Copied, frame + 1, img.Width, img.Height));
    }

    /// <summary>"프레임 저장…": 기본 이름 <c>{문서}_{프레임 번호}.png</c>(번호는 미리보기 표시처럼 1부터), 1×.</summary>
    private void SaveFrame(int frame)
    {
        if (_ctx.Project is not { } p) return;
        var name = p.Path is { } pp ? Path.GetFileNameWithoutExtension(pp) : ProjectExport.CleanFileName(p.DisplayName);
        var path = Ui.SaveFile(this, UiStrings.Preview_SaveFrame_Title, p.Path is { } dir ? Path.GetDirectoryName(dir) : null,
            $"{name}_{frame + 1}.png", ".png", new FileFilter(UiStrings.Preview_PngFilter, "*.png"));
        if (path is null) return;
        SaveFrameTo(frame, path);
    }

    /// <summary>프레임 그림을 <paramref name="path"/>에 PNG(1×)로 쓴다. 실패하면 알린다.</summary>
    private void SaveFrameTo(int frame, string path)
    {
        if (FrameImage(frame, 1) is not { } img) return;
        try
        {
            PngIO.Save(img, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.Preview_SaveFailed, ex.Message), UiStrings.Preview_SaveFrame_Title);
            return;
        }
        Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.Preview_Saved, Path.GetFileName(path), img.Width, img.Height));
        FileSaved?.Invoke(this, path);
    }

    // ───────────── 재생 ─────────────

    public bool IsPlaying => _timer.IsEnabled;

    /// <summary>재생 중 보이는 프레임(멈춰 있으면 현재 프레임).</summary>
    public int ShownFrame => IsPlaying && _orderIndex < _order.Count ? _order[_orderIndex] : _ctx.CurrentFrame;

    public void TogglePlay()
    {
        if (IsPlaying) Stop();
        else Play();
    }

    public void Play()
    {
        if (!_ctx.HasProject || _ctx.FrameCount == 0) return;
        _order = _ctx.PlaybackOrder();
        if (_order.Count == 0) return;
        int at = _order.IndexOf(_ctx.CurrentFrame);
        _orderIndex = at < 0 ? 0 : at;
        _elapsedInFrame = 0;
        _clock.Restart();
        _timer.Start();
        ((IconView)_play.Content!).Icon = VectorIcon.Pause;
        PlayingFrameChanged?.Invoke(this, ShownFrame);
        _view.InvalidateVisual();
    }

    public void Stop()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        ((IconView)_play.Content!).Icon = VectorIcon.Play;
        PlayingFrameChanged?.Invoke(this, -1);
        _view.InvalidateVisual();
    }

    /// <summary>재생 중이면 재생 순서(태그 범위)를 지금 상태로 다시 정한다.</summary>
    private void RefreshOrder()
    {
        if (!IsPlaying || !_ctx.HasProject) return;
        var order = _ctx.PlaybackOrder();
        if (order.Count == 0) { Stop(); return; }
        if (order.SequenceEqual(_order)) return;
        int shown = ShownFrame;
        _order = order;
        int at = order.IndexOf(shown);
        _orderIndex = at < 0 ? 0 : at;
        _elapsedInFrame = 0;
    }

    private int FrameMs(int frame)
    {
        decimal fps = _fps.NumValue();
        return fps > 0 ? (int)Math.Round(1000 / (double)fps) : _ctx.FrameDuration(frame);
    }

    private void Tick()
    {
        if (!_ctx.HasProject || _order.Count == 0) { Stop(); return; }
        _elapsedInFrame += _clock.Elapsed.TotalMilliseconds;
        _clock.Restart();
        bool changed = false, waiting = false;
        int guard = 0;
        while (_elapsedInFrame >= FrameMs(_order[_orderIndex]) && guard++ < 1000)
        {
            int next = _orderIndex + 1 < _order.Count ? _orderIndex + 1 : _ctx.PlaybackLoops ? 0 : -1;
            if (next < 0) { Stop(); return; }
            // 다음 프레임 합성이 아직이면(작업 스레드에서 계산 중) 지금 프레임을 그대로 두고 기다린다 — 회색 프레임을 끼우지 않는다.
            if (_ctx.FrameView(_order[next], RenderPriority.Current).Pending)
            {
                _elapsedInFrame = FrameMs(_order[_orderIndex]);
                _waitFrame = _order[next];
                waiting = true;
                break;
            }
            _elapsedInFrame -= FrameMs(_order[_orderIndex]);
            _orderIndex = next;
            changed = true;
        }
        // 곧 보일 프레임을 미리 계산해 둔다
        for (int k = 1; k <= 2 && _order.Count > 1; k++) _ctx.FrameView(_order[(_orderIndex + k) % _order.Count], RenderPriority.Soon);
        if (waiting != _waiting)
        {
            _waiting = waiting;
            _view.InvalidateVisual();
        }
        else if (waiting) _view.InvalidateVisual(); // 로딩 아이콘이 돈다
        if (!changed) return;
        PlayingFrameChanged?.Invoke(this, ShownFrame);
        _view.InvalidateVisual();
    }

    /// <summary>재생 중 다음 프레임 합성을 기다리는 중인지와 그 프레임.</summary>
    private bool _waiting;
    private int _waitFrame;
    /// <summary>멈춰 있을 때 로딩 아이콘을 돌리려고 다시 그리는 타이머.</summary>
    private readonly DispatcherTimer _busyTimer = new() { Interval = RenderOverlay.FrameInterval };

    /// <summary>미리보기를 그리는 영역.</summary>
    private sealed class View : Control
    {
        private readonly PreviewPanel _owner;

        public View(PreviewPanel owner) => _owner = owner;

        public override void Render(DrawingContext g)
        {
            var ctx = _owner._ctx;
            var bounds = new Rect(Bounds.Size);
            if (_owner._bgColor is { } bg) g.FillRectangle(AppTheme.Brush(bg), bounds);
            else
            {
                g.FillRectangle(AppTheme.InputBrush, bounds);
                AppTheme.DrawChecker(g, bounds, 8);
            }
            if (!ctx.HasProject) return;
            var area = new Rect(4, 4, Math.Max(0, bounds.Width - 8), Math.Max(0, bounds.Height - 24));
            int frame = Math.Clamp(_owner.ShownFrame, 0, Math.Max(0, ctx.FrameCount - 1));
            var view = ctx.FrameView(frame);
            if (view.Bitmap is not { } bmp) return;
            var shown = ImageUtil.DrawFitted(g, bmp, new Rectangle(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), area, _owner._scale);
            // 재생 중 다음 프레임을 기다리거나, 보이는 프레임을 다시 계산하는 중이면 로딩 아이콘(그림은 이전 것 그대로)
            var wait = _owner._waiting && _owner._waitFrame < ctx.FrameCount ? ctx.FrameView(_owner._waitFrame) : view;
            if (wait.Overlay) RenderOverlay.DrawBusy(g, shown.Intersect(bounds), wait.Progress);
            // 멈춰 있을 때도 로딩 아이콘이 돌도록(재생 중에는 재생 타이머가 다시 그린다)
            bool spin = wait.Overlay && !_owner.IsPlaying;
            if (spin != _owner._busyTimer.IsEnabled)
            {
                if (spin) _owner._busyTimer.Start();
                else _owner._busyTimer.Stop();
            }
            var tag = ctx.ActiveTag;
            string caption = $"{frame + 1}/{ctx.FrameCount} · {ctx.FrameDuration(frame)}ms" + (tag is null ? "" : $" · {tag.Name}");
            var textBrush = _owner._bgColor is { } c && Lightness(c) > 0.6 ? Brushes.Black : Brushes.White;
            AppTheme.DrawTextIn(g, caption, new Rect(4, bounds.Height - 18, Math.Max(1, bounds.Width - 8), 16), textBrush, AppTheme.SmallFontSize);
        }

        /// <summary>HSL 밝기(System.Drawing.Color.GetBrightness와 같은 값).</summary>
        private static double Lightness(Color c) => (Math.Max(c.R, Math.Max(c.G, c.B)) + Math.Min(c.R, Math.Min(c.G, c.B))) / 510.0;
    }
}
