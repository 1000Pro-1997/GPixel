using Gpixel.Core.Edit;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// 레이어 효과(비파괴): 효과 목록(추가·삭제·순서·켜기·영구 적용) + 고른 효과 설정(프레임 범위 포함) + 전/후 미리보기(움직이는 효과는 그 범위를 돌려 봄).
/// 고치는 동안 캔버스에도 바로 보이고(<see cref="SpriteProject.PreviewLayerEffects"/>), 확인이면 대화상자 한 번이 실행취소 한 단계,
/// 취소·창 닫기면 원래대로 돌아간다(<see cref="SpriteProject.EndLayerEffectsPreview"/>).
/// </summary>
public sealed partial class LayerEffectsDialog : DialogBase
{
    private const int PreviewBox = 220;
    /// <summary>오른쪽(미리보기) 칸 폭 — 고정(넉넉히): 설명 글이 바뀌어도 창 폭이 그대로.</summary>
    private const int RightWidth = PreviewBox * 2 + 72;
    /// <summary>가운데(효과 목록·설정) 칸 폭.</summary>
    private const int SettingsWidth = 350;

    private static readonly (LightDirection Dir, string Name)[] Directions =
    [
        (LightDirection.TopLeft, UiStrings.S_LayerEffectsDialog_Directions_38A213C040), (LightDirection.Top, UiStrings.S_LayerEffectsDialog_Directions_53073620AC), (LightDirection.TopRight, UiStrings.S_LayerEffectsDialog_Directions_E2ADEEE3AD),
        (LightDirection.Left, UiStrings.S_LayerEffectsDialog_Directions_87DE11F0BD), (LightDirection.Right, UiStrings.S_LayerEffectsDialog_Directions_A573091255),
        (LightDirection.BottomLeft, UiStrings.S_LayerEffectsDialog_Directions_2D3F13D6AD), (LightDirection.Bottom, UiStrings.S_LayerEffectsDialog_Directions_B1DECE0B86), (LightDirection.BottomRight, UiStrings.S_LayerEffectsDialog_Directions_73A07AC9E0),
    ];

    private static readonly string[] Easings = [UiStrings.S_LayerEffectsDialog_Easings_4FA414FBAC, UiStrings.S_LayerEffectsDialog_Easings_BDEEAEDE46, UiStrings.S_LayerEffectsDialog_Easings_503369007B, UiStrings.S_LayerEffectsDialog_Easings_7665ED231C];
    private static readonly string[] Repeats = [UiStrings.S_LayerEffectsDialog_Repeats_3E39AFFB4A, UiStrings.S_LayerEffectsDialog_Repeats_753C57FE3B, UiStrings.S_LayerEffectsDialog_Repeats_466235A991];
    private static readonly string[] Centers = [UiStrings.S_LayerEffectsDialog_Centers_17A9293CA6, UiStrings.S_LayerEffectsDialog_Centers_7CA53FB898, UiStrings.S_LayerEffectsDialog_Centers_4EEB98A21E, UiStrings.S_LayerEffectsDialog_Centers_8D791DFBBE];
    private static readonly string[] Anchors = [UiStrings.S_LayerEffectsDialog_Anchors_0EC3ED31B9, UiStrings.S_LayerEffectsDialog_Anchors_1F3A9869CE, UiStrings.S_LayerEffectsDialog_Anchors_6466116779];
    private static readonly string[] Blends = [UiStrings.S_LayerEffectsDialog_Blends_69D924C609, UiStrings.S_LayerEffectsDialog_Blends_1725732230, UiStrings.S_LayerEffectsDialog_Blends_5260B3BD7E];
    private static readonly string[] Dithers = [UiStrings.S_LayerEffectsDialog_Dithers_19C7C4899C, "2×2 Bayer", "4×4 Bayer"];

    /// <summary>숫자 칸 종류: 우클릭 예시 값(칸 범위 밖 값은 빠짐), 단위(메뉴 글자), 최근 값 묶음 이름. <paramref name="Format"/>가 있으면 단위 대신 그 글자.</summary>
    private sealed record NumKind(string Key, string Unit, decimal[] Values, Func<decimal, string>? Format = null);

    /// <summary>효과 설정의 숫자 칸 종류(같은 종류끼리 최근 값을 나눔).</summary>
    private static class NumKinds
    {
        /// <summary>지나가는·바뀌는 방향(0~359°).</summary>
        public static readonly NumKind Angle = new("effect.angle", "°", [0, 15, 30, 45, 60, 90, 120, 135, 180, 225, 270, 315]);
        /// <summary>범위 동안 도는 전체 각도(양수 시계 방향, 음수 반시계 방향).</summary>
        public static readonly NumKind Rotation = new("effect.rotation", "°", [-1440, -720, -540, -360, -270, -180, -90, -45, -30, -15, 0, 15, 30, 45, 90, 180, 270, 360, 540, 720, 1080, 1440]);
        /// <summary>색상 돌리기(−180~180°).</summary>
        public static readonly NumKind Hue = new("effect.hue", "°", [-180, -120, -90, -60, -30, 0, 30, 60, 90, 120, 180]);
        /// <summary>세기·양(0~100%).</summary>
        public static readonly NumKind Strength = new("effect.strength", "%", [0, 10, 25, 33, 50, 66, 75, 90, 100]);
        /// <summary>불투명도(0~100%).</summary>
        public static readonly NumKind Opacity = new("effect.opacity", "%", [0, 10, 20, 25, 33, 40, 50, 60, 75, 90, 100]);
        /// <summary>밝기·대비·채도(−100~100%).</summary>
        public static readonly NumKind Adjust = new("effect.adjust", "%", [-100, -75, -50, -25, -10, 0, 10, 25, 50, 75, 100]);
        /// <summary>빈도(프레임 중 몇 %).</summary>
        public static readonly NumKind Rate = new("effect.rate", "%", [0, 5, 10, 15, 20, 25, 33, 50, 75, 100]);
        /// <summary>무작위 변화·불규칙함(0~100%).</summary>
        public static readonly NumKind Random = new("effect.random", "%", [0, 10, 20, 25, 33, 50, 66, 75, 90, 100]);
        /// <summary>진행 중 시작 시점(0~100%).</summary>
        public static readonly NumKind Progress = new("effect.progress", "%", [0, 10, 20, 25, 33, 40, 50, 60, 66, 75, 90]);
        /// <summary>그림 크기의 몇 %(반사 높이).</summary>
        public static readonly NumKind Portion = new("effect.portion", "%", [10, 20, 25, 33, 40, 50, 60, 66, 75, 100]);
        /// <summary>늘었다 줄기 세기(1~50%).</summary>
        public static readonly NumKind Squash = new("effect.squash", "%", [5, 10, 15, 20, 25, 30, 40, 50]);
        /// <summary>작은 픽셀 거리(폭·높이·흔들림·간격·밀림).</summary>
        public static readonly NumKind Px = new("effect.px", "px", [0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32]);
        /// <summary>선·띠·고리 두께.</summary>
        public static readonly NumKind Thickness = new("effect.thickness", "px", [1, 2, 3, 4, 5, 6, 8, 12, 16]);
        /// <summary>그림자 X·Y 옮김(음수 = 왼쪽·위).</summary>
        public static readonly NumKind Offset = new("effect.offset", "px", [-8, -4, -3, -2, -1, 0, 1, 2, 3, 4, 8]);
        /// <summary>날아가는·흘러내리는 거리, 고리 반지름.</summary>
        public static readonly NumKind Distance = new("effect.distance", "px", [0, 4, 8, 12, 16, 24, 32, 48, 64, 96, 128]);
        /// <summary>중력(음수 = 위로).</summary>
        public static readonly NumKind Gravity = new("effect.gravity", "px", [-16, -8, -4, -2, 0, 2, 4, 6, 8, 16, 32]);
        /// <summary>터지기 조각 크기(1~4px).</summary>
        public static readonly NumKind Piece = new("effect.piece", "px", [1, 2, 3, 4]);
        /// <summary>범위 안 횟수(튕김·번쩍임·깜빡임·흔들림·오르내림).</summary>
        public static readonly NumKind Count = new("effect.count", UiStrings.S_LayerEffectsDialog_Count_A61CE50C3B, [1, 2, 3, 4, 5, 6, 8, 10, 12, 16]);
        /// <summary>움직임 반복 횟수.</summary>
        public static readonly NumKind Repeat = new("effect.repeat", UiStrings.S_LayerEffectsDialog_Repeat_ABB1AFE73B, [1, 2, 3, 4, 5, 6, 8]);
        /// <summary>프레임 수(보임·숨김·번쩍임·잔상 간격).</summary>
        public static readonly NumKind Frames = new("effect.frames", UiStrings.S_LayerEffectsDialog_Frames_656538CC7F, [0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 16]);
        /// <summary>잔상 개수.</summary>
        public static readonly NumKind Copies = new("effect.copies", UiStrings.S_LayerEffectsDialog_Copies_54ACB79D5D, [1, 2, 3, 4, 5, 6, 8]);
        /// <summary>주사선 간격(줄).</summary>
        public static readonly NumKind ScanGap = new("effect.scanGap", UiStrings.S_LayerEffectsDialog_ScanGap_5A028BD5A6, [2, 3, 4, 5, 6]);
        /// <summary>무작위 시드.</summary>
        public static readonly NumKind Seed = new("effect.seed", "", [1, 2, 3, 7, 42, 1234]);
        /// <summary>색 허용 차이(R·G·B 0~255).</summary>
        public static readonly NumKind Tolerance = new("effect.tolerance", "", [0, 4, 8, 16, 24, 32, 48, 64, 96, 128]);
    }

    private readonly SpriteProject _project;
    private readonly Layer _layer;
    private readonly int _frame;
    private readonly IReadOnlyList<LayerEffect> _original;
    private readonly List<LayerEffect> _effects;
    private readonly Dictionary<int, PixelImage?> _sources = new();

    private readonly ListBox _list;
    private readonly StackPanel _settings;
    private readonly Button _remove, _up, _down, _bake, _play;
    private readonly CheckBox _effectOnly, _expandCanvas, _loop;
    /// <summary>크게 보기 창의 재생 단추·프레임 설명·재생 범위 체크(창이 닫혀 있으면 null).</summary>
    private Button? _largePlay;
    private TextBlock? _largeCaption;
    private CheckBox? _largeEffectOnly, _largeLoop;
    private readonly ComboBox _expandMode;
    private readonly TextBlock _expandNote;
    private readonly TextBlock _caption;
    private readonly PreviewView _preview = new();
    private Window? _previewWindow;
    private PreviewView? _largePreview;
    private readonly DispatcherTimer _timer = new();
    private int _previewFrame;
    /// <summary>재생 범위: true = 고른 효과 범위, false = 늘린 뒤 전체 프레임. 앱을 켜 둔 동안 유지.</summary>
    private static bool s_effectOnly = true;
    /// <summary>효과에 맞춘 캔버스 확대 방식. 앱을 켜 둔 동안 유지.</summary>
    private static EffectCanvasExpandMode s_canvasExpandMode = EffectCanvasExpandMode.KeepCenter;
    /// <summary>확인 단추에서 검증·계산한 캔버스 확대(닫힌 뒤 같은 결과를 적용).</summary>
    private EffectCanvasExpansion? _confirmedCanvasExpansion;
    /// <summary>재생 반복: 끝 프레임 다음 바로 처음으로(끝에서 멈칫하지 않음). 앱을 켜 둔 동안 창을 다시 열어도 유지.</summary>
    private static bool s_seamlessLoop;
    private bool _syncing, _baked;

    /// <summary>확인(프레임을 늘렸을 때)·영구 적용으로 닫혔으면 상태 표시줄에 보일 알림.</summary>
    public string? ResultMessage { get; private set; }

    /// <param name="focus">처음 고를 효과 번호(범위 밖이면 첫 효과).</param>
    /// <param name="newRange">"추가 범위"의 처음 값(타임라인에서 칸을 고르고 "효과 추가…"로 열었을 때 그 프레임들). null = 모든 프레임.</param>
    /// <param name="motionRange">추가 범위를 그대로 두고 움직이는 효과를 추가할 때 쓸 범위(한 칸만 골랐으면 태그 끝까지 넓힌 것). null = newRange.</param>
    public LayerEffectsDialog(EditorContext ctx, Layer layer, int focus = -1, FrameRange? newRange = null, FrameRange? motionRange = null) : base(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_7E8F1A60E2,layer.Name))
    {
        // 숫자·색 값 입력에서 Enter는 입력만 확정한다. 효과 적용은 확인 단추로만 한다.
        OkButton.IsDefault = false;
        _motionRange = motionRange ?? newRange;
        _project = ctx.Project ?? throw new InvalidOperationException(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_42089F3C1D);
        _layer = layer;
        _frame = Math.Clamp(ctx.CurrentFrame, 0, _project.Frames.Count - 1);
        _previewFrame = _frame;
        _original = layer.Effects;
        _effects = [.. layer.Effects];

        int n = _project.Frames.Count;
        _addAll = Ui.Check(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_562B8ABA6D, newRange is null);
        _addFrom = WithKind(Ui.Num(1, n, (newRange?.From ?? 0) + 1, 56), FrameNumberKind(end: false));
        _addTo = WithKind(Ui.Num(1, n, (newRange?.To ?? n - 1) + 1, 56), FrameNumberKind(end: true));
        _addFrom.IsEnabled = _addTo.IsEnabled = newRange is not null;
        ToolTip.SetTip(_addAll, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_58CE85AE10);
        _addAll.IsCheckedChanged += (_, _) =>
        {
            if (!_addAll.On() && (int)_addFrom.NumValue() == 1 && (int)_addTo.NumValue() == n)
                _addFrom.Value = _addTo.Value = _frame + 1; // 모든 프레임을 덮는 범위 = "모든 프레임"이라 지금 프레임 하나로 시작
            _addFrom.IsEnabled = _addTo.IsEnabled = !_addAll.On();
            _addRangeTouched = true;
            SyncStripRange();
        };
        _addFrom.ValueChanged += (_, _) => { _addRangeTouched = true; SyncStripRange(); };
        _addTo.ValueChanged += (_, _) => { _addRangeTouched = true; SyncStripRange(); };
        _remove = Ui.Button(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_DEC8D5CC64, (_, _) => Remove(), 60);
        _up = Ui.Button(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_4E920AEA13, (_, _) => Move(-1), 60);
        _down = Ui.Button(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_FE8FEDFCFF, (_, _) => Move(1), 60);
        _bake = Ui.Button(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_769C41A711, (_, _) => Bake(), 80);
        _list = new ListBox { Width = SettingsWidth, Height = 170, Margin = new Thickness(3) };
        ToolTip.SetTip(_list, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_A48FBAD60B);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Delete or Key.Back) || SelectedRow is null) return;
            e.Handled = true;
            Remove();
            _list.Focus(); // 이어서 Delete를 누를 수 있게
        };
        _drag.Moved += MoveUnit;
        _list.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            ShowSettings();
            UpdateButtons();
            SyncPlayback(restart: true);
            SyncStripRange();
        };
        _settings = new StackPanel { Spacing = 2, MinHeight = 120, Width = SettingsWidth };
        Grid.SetIsSharedSizeScope(_settings, true); // 설정 줄마다 이름 열 폭을 맞춘다(SettingsRow)

        _expandCanvas = Ui.Check(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_420801482E, true);
        _expandMode = Ui.Combo(110, [UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_E64B4CB375, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_F7968CE40F, UiStrings.EffectCanvasExpand_KeepCenter]);
        _expandMode.SelectedIndex = (int)s_canvasExpandMode;
        _expandMode.IsEnabled = true;
        _expandNote = Ui.Label("");
        _expandNote.Foreground = AppTheme.DimTextBrush;
        _expandNote.Margin = new Thickness(3, 0, 0, 2);
        _expandNote.TextWrapping = TextWrapping.Wrap;
        _expandNote.MaxWidth = SettingsWidth;
        _expandNote.IsVisible = true;
        ToolTip.SetTip(_expandCanvas, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_98461A52E4);
        ToolTip.SetTip(_expandMode, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_4DF95CBC10);
        _expandCanvas.IsCheckedChanged += (_, _) =>
        {
            _expandMode.IsEnabled = _expandCanvas.On();
            _expandNote.IsVisible = _expandCanvas.On();
            SyncCanvasExpansion();
            RefreshPreview();
        };
        _expandMode.SelectionChanged += (_, _) =>
        {
            if (_expandMode.SelectedIndex < 0) return;
            s_canvasExpandMode = (EffectCanvasExpandMode)_expandMode.SelectedIndex;
            SyncCanvasExpansion();
            RefreshPreview();
        };
        OkButton.Click += (_, _) =>
        {
            _confirmedCanvasExpansion = null;
            if (!_expandCanvas.On()) return;
            try
            {
                _confirmedCanvasExpansion = CanvasExpansion(VirtualEffects());
            }
            catch (InvalidOperationException ex)
            {
                DialogResult = DialogResult.None;
                Ui.Error(this, ex.Message);
            }
        };

        string? motionTip = _motionRange != newRange && _motionRange is { } mr
            ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_F60FBD674C,mr.From + 1,mr.To + 1)
            : null;
        var rangeLabel = Ui.Label(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_CE6B076915);
        if (motionTip is not null) ToolTip.SetTip(rangeLabel, motionTip);
        var canvasExpand = Ui.Stack(Ui.Row(_expandCanvas, _expandMode), _expandNote);
        var shared = Ui.Group(UiStrings.EffectShared_Title, Ui.Stack(canvasExpand, _extendPanel));
        var left = Ui.Stack(
            Ui.Row(rangeLabel, _addAll, Ui.Label(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_4782D0C688), _addFrom, Ui.Label("~"), _addTo),
            _list,
            Ui.Row(_remove, _up, _down, _bake),
            shared,
            Ui.Group(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_4D4CF63BB1, new ScrollViewer { Content = _settings, MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }));
        left.Width = SettingsWidth + 30;

        _preview.Width = PreviewBox * 2 + 12;
        _preview.Height = PreviewBox + 20;
        _preview.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(_preview, UiStrings.EffectPreview_OpenLarge);
        _preview.PointerPressed += (_, e) =>
        {
            if (!_preview.HasImages || !e.GetCurrentPoint(_preview).Properties.IsLeftButtonPressed) return;
            OpenLargePreview();
            e.Handled = true;
        };
        _caption = Ui.Label("");
        _caption.Foreground = AppTheme.DimTextBrush;
        _play = Ui.Button(VectorIcon.Play, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_3DC0585476, (_, _) => TogglePlay(), 80);
        ToolTip.SetTip(_play, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_46C6465E13);
        _effectOnly = Ui.Check(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_BEC5ADB6D1, s_effectOnly);
        _effectOnly.VerticalAlignment = VerticalAlignment.Center;
        _effectOnly.IsCheckedChanged += (_, _) =>
        {
            s_effectOnly = _effectOnly.On();
            if (_largeEffectOnly is { } large) large.IsChecked = s_effectOnly;
            SyncPlayback(restart: true);
        };
        ToolTip.SetTip(_effectOnly, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_2138B1E743);
        _loop = Ui.Check(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_96BC2CE00E, s_seamlessLoop);
        _loop.VerticalAlignment = VerticalAlignment.Center;
        _loop.IsCheckedChanged += (_, _) =>
        {
            s_seamlessLoop = _loop.On();
            if (_largeLoop is { } large) large.IsChecked = s_seamlessLoop;
        };
        ToolTip.SetTip(_loop, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_0500474F59);
        _timer.Tick += (_, _) => NextPreviewFrame();
        var right = Ui.Stack(
            Ui.Row(_play, _effectOnly, _loop, Ui.InfoBadge(Intro(layer))),
            _caption,
            _preview,
            BuildFrameStrip());
        right.Width = RightWidth; // 고정 — 진행 % 등 글 길이에 따라 창 폭이 흔들리지 않게

        var root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        root.Children.Add(BuildPalette(motionTip));
        root.Children.Add(left);
        root.Children.Add(right);
        Body.Children.Add(root);

        RebuildList(_effects.Count == 0 ? -1 : focus >= 0 && focus < _effects.Count ? focus : 0);
        AttachScriptEvents();
        NumberScrub.Started += OnScrubStarted;
        NumberScrub.Ended += OnScrubEnded;
        Closed += (_, _) =>
        {
            NumberScrub.Started -= OnScrubStarted;
            NumberScrub.Ended -= OnScrubEnded;
            _scrubbing = _stripDragging = false;
            _previewWindow?.Close();
            _timer.Stop();
            DisposeRenders();
            DisposePalette();
            _strip.Dispose();
            int index = _project.IndexOf(_layer);
            if (index >= 0 && _canvasHeld && !_baked)
            {
                // 끄다가 닫힘: 미뤘던 효과를 넣고 확정·취소
                _canvasHeld = false;
                _project.PreviewLayerEffects(index, _effects);
            }
            if (index >= 0 && !_baked) EndPreview(index, DialogResult == DialogResult.OK);
            _preview.Dispose();
        };
    }

    /// <summary>이 창(큰 미리보기 창 포함)의 숫자 상자를 끌기 시작·끝.</summary>
    private void OnScrubStarted(object? sender, NumericUpDown box)
    {
        if (TopLevel.GetTopLevel(box) is { } top && (ReferenceEquals(top, this) || ReferenceEquals(top, _previewWindow))) _scrubbing = true;
    }

    private void OnScrubEnded(object? sender, NumericUpDown box)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        ReleaseHold();
    }

    private static string Intro(Layer layer)
        => UiStrings.S_LayerEffectsDialog_Intro_AA476847D8
           + UiStrings.S_LayerEffectsDialog_Intro_3052848546
           + UiStrings.S_LayerEffectsDialog_Intro_C71F0948BE
           + (layer.IsGroup ? UiStrings.S_LayerEffectsDialog_Intro_5F6126CBFA : "")
           + (layer.IsPart ? UiStrings.S_LayerEffectsDialog_Intro_03C1A71EB9 : "");

    // ───────────── 목록 ─────────────

    private readonly CheckBox _addAll;
    private readonly NumericUpDown _addFrom, _addTo;
    /// <summary>추가 범위를 손대지 않았을 때 움직이는 효과에 쓸 범위.</summary>
    private readonly FrameRange? _motionRange;
    private bool _addRangeTouched;


    private void UpdateButtons()
    {
        var block = SelectedBlock;
        _remove.IsEnabled = block is not null;
        _up.IsEnabled = block is { First: > 0 };
        _down.IsEnabled = block is { } b && b.Last < _effects.Count - 1;
        if (_savePreset is not null) _savePreset.IsEnabled = block is not null;
        var (canBake, why) = CanBake(_project, _layer);
        var chosen = SelectedIndices();
        _bake.IsEnabled = canBake && chosen.Any(i => _effects[i] is not MissingEffect);
        int extra = chosen.Count > 0 ? LayerEffects.BakeSet(_effects, chosen).Count - chosen.Count : 0;
        ToolTip.SetTip(_bake, why ?? (UiStrings.S_LayerEffectsDialog_UpdateButtons_ADC8E0220B
                                      + (extra > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_UpdateButtons_DFC3F498B4,extra) : "")));
    }

    /// <summary>고른 줄의 효과 번호들(묶음 머리면 구성 효과 전부).</summary>
    private IReadOnlyList<int> SelectedIndices()
        => SelectedBlock is (var first, var last) ? Enumerable.Range(first, last - first + 1).ToArray() : [];

    /// <summary>
    /// 효과 하나 또는 효과 레시피를 추가 범위로 목록 <paramref name="at"/>번째에 넣고 첫 효과를 고른다.
    /// 레시피(<paramref name="recipe"/>)는 값을 복사해 넣고, 모든 프레임 정책이면 추가 범위 대신 모든 프레임을 쓴다.
    /// </summary>
    private void Add(IReadOnlyList<LayerEffect> templates, int at, EffectRecipe? recipe = null)
    {
        if (templates.Count == 0) return;
        int n = _project.Frames.Count;
        bool animated = templates.Any(effect => effect is AnimatedEffect);
        var range = !_addRangeTouched && animated ? _motionRange
            : _addAll.On() ? null : FrameRange.Of((int)_addFrom.NumValue() - 1, (int)_addTo.NumValue() - 1, n);
        var added = recipe?.Instantiate(_project, range) ?? templates.Select((effect, index) => effect with
        {
            Frames = range,
            ClipToPrevious = index > 0 && effect.ClipToPrevious,
        }).ToArray();
        at = Math.Clamp(at, 0, _effects.Count);
        _effects.InsertRange(at, added);
        RebuildList(at);
    }

    /// <summary>고른 줄을 지운다(묶음 머리면 구성 효과 전부). 기준 효과가 사라지면 바로 뒤의 클리핑 효과는 클리핑을 푼다.</summary>
    private void Remove()
    {
        if (SelectedBlock is not (var first, var last)) return;
        bool anchor = false;
        for (int i = first; i <= last; i++)
        {
            var removed = _effects[i];
            if (ReferenceEquals(removed, _scriptEditorTarget)) _scriptEditor?.Close();
            anchor |= !removed.ClipToPrevious;
            _virtualRangeOverrides.Remove(removed);
            if (ReferenceEquals(removed, _extensionOwner)) SetExtension(null, null);
            if (ReferenceEquals(removed, _preferExistingFor))
            {
                _preferExistingFor = null;
                _preferExisting = null;
            }
        }
        int clippedFollowers = 0;
        if (anchor)
            while (last + clippedFollowers + 1 < _effects.Count && _effects[last + clippedFollowers + 1].ClipToPrevious)
                clippedFollowers++;
        _effects.RemoveRange(first, last - first + 1);
        for (int child = 0; child < clippedFollowers; child++)
            ReplaceEffect(first + child, _effects[first + child] with { ClipToPrevious = false });
        RebuildList(Math.Min(first, _effects.Count - 1));
    }

    private readonly DragReorder _drag = new();

    /// <summary>효과 참조를 새 불변 사본으로 바꾸고, 프레임 늘리기·가상 범위가 그 사본을 따라가게 한다.</summary>
    private void ReplaceEffect(int index, LayerEffect effect)
    {
        var old = _effects[index];
        _effects[index] = effect;
        if (_virtualRangeOverrides.Remove(old, out var virtualRange))
            _virtualRangeOverrides[effect] = virtualRange;
        if (ReferenceEquals(old, _extensionOwner)) _extensionOwner = effect;
        if (ReferenceEquals(old, _preferExistingFor)) _preferExistingFor = effect;
        if (ReferenceEquals(old, _scriptEditorTarget)) _scriptEditorTarget = effect;
    }

    private void DetachClippedFollowers(int anchor)
    {
        for (int i = anchor + 1; i < _effects.Count && _effects[i].ClipToPrevious; i++)
            ReplaceEffect(i, _effects[i] with { ClipToPrevious = false });
    }

    /// <summary>효과 하나를 바꾸고(목록 글자 갱신) 캔버스·미리보기에 반영한다.</summary>
    private void Update(int index, LayerEffect effect)
    {
        if (index < 0 || index >= _effects.Count || _effects[index] == effect) return;
        ReplaceEffect(index, effect);
        RefreshRows();
        Changed();
        SyncPlayback();
    }

    /// <summary>목록이 바뀜: 단추 상태, 캔버스(미리보기 효과)·프레임 줄·전/후 미리보기를 갱신한다(프레임 결과는 작업 스레드에서 다시 그림, 무거운 그림을 끄는 중이면 놓을 때).</summary>
    private void Changed()
    {
        SyncExtendEditor();
        UpdateButtons();
        int index = _project.IndexOf(_layer);
        if (index >= 0)
        {
            if (HoldRenders) _canvasHeld = true;
            else _project.PreviewLayerEffects(index, _effects);
        }
        RestartRenders();
    }

    private string EffectSummary(LayerEffect e)
        => Summary(_virtualRangeOverrides.TryGetValue(e, out var range) ? e with { Frames = range } : e)
           + (e.ClipToPrevious ? UiStrings.EffectPreset_ClippedSuffix : "");

    private void RefreshEffectSummary(int index)
    {
        if (index >= 0 && index < _effects.Count) RefreshRows();
    }

    // ───────────── 영구 적용 ─────────────

    /// <summary>레이어에 효과를 영구 적용할 수 있는지와, 안 되면 그 까닭.</summary>
    internal static (bool Ok, string? Why) CanBake(SpriteProject project, Layer layer)
    {
        if (layer.IsGroup) return (false, UiStrings.S_LayerEffectsDialog_CanBake_F0B223C43A);
        if (project.IsLayerLocked(project.IndexOf(layer))) return (false, UiStrings.S_LayerEffectsDialog_CanBake_235F1CD0DC);
        return (true, null);
    }

    /// <summary>
    /// 효과 영구 적용(<see cref="SpriteProject.BakeLayerEffects"/>)한 결과 알림 — "효과 영구 적용: 외곽선 (프레임 1~4)".
    /// </summary>
    internal static string BakeSummary((IReadOnlyList<LayerEffect> Baked, FrameRange? Frames, int Cels, bool Grown) r, Layer layer)
        => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_BakeSummary_5A9A6172BE,string.Join(", ", r.Baked.Select(e => e.DisplayName)))
           + (r.Frames is { } f ? f.From == f.To ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_BakeSummary_E38062A45D,f.From + 1) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_BakeSummary_1887A237E0,f.From + 1,f.To + 1) : UiStrings.S_LayerEffectsDialog_BakeSummary_5A6EC98D59)
           + string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_BakeSummary_76F41482A1,r.Cels)
           + (r.Grown ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_BakeSummary_E39243C108,layer.PartSize.Width,layer.PartSize.Height) : "")
           + UiStrings.S_LayerEffectsDialog_BakeSummary_49B857E682;

    /// <summary>
    /// 고른 효과(묶음 머리면 구성 효과 전부) 영구 적용: 창에서 고친 효과 목록을 확정하고 곧바로 구워 넣는다(둘을 합쳐 실행취소 한 단계). 창은 닫힌다.
    /// </summary>
    private void Bake()
    {
        int index = _project.IndexOf(_layer);
        var selected = SelectedIndices();
        if (index < 0 || selected.Count == 0) return;
        var list = VirtualEffects().ToList();
        var extension = _extension;
        int trim = Trim, keep = VirtualCount;
        // 지울 프레임과 함께 없어지는 효과는 빼고 확정하므로, 고른 효과 번호도 남는 목록 번호로
        var commitList = CommitEffects();
        var bakeIndices = selected.Where(i => !TrimmedAway(i)).Select(i => i - Enumerable.Range(0, i).Count(TrimmedAway)).ToList();
        EffectCanvasExpansion? expansion = null;
        try
        {
            if (_expandCanvas.On() && CanvasExpansion(list) is { } plan && !plan.IsNone(_project.Width, _project.Height))
                expansion = plan;
        }
        catch (InvalidOperationException ex)
        {
            Ui.Error(this, ex.Message);
            return;
        }
        int oldWidth = _project.Width, oldHeight = _project.Height;
        _project.EndLayerEffectsPreview(index, _original, commit: false);
        try
        {
            (IReadOnlyList<LayerEffect>, FrameRange?, int, bool) result = default;
            var change = ProjectChange.Layers | ProjectChange.Pixels
                         | (extension is null && trim == 0 ? 0 : ProjectChange.Frames | ProjectChange.Tags)
                         | (expansion is null ? 0 : ProjectChange.Canvas | ProjectChange.Frames | ProjectChange.Settings);
            string label = extension is not null && expansion is not null ? UiStrings.S_LayerEffectsDialog_Bake_E014268BBB
                : extension is not null ? UiStrings.S_LayerEffectsDialog_Bake_E079C5BFE1
                : expansion is not null ? UiStrings.S_LayerEffectsDialog_Bake_D0F7807D90 : UiStrings.S_LayerEffectsDialog_Bake_D15392AB9F;
            _project.Edit(label, change, () =>
            {
                if (extension is { } ext) _project.ExtendFrames(ext);
                _project.SetLayerEffects(index, commitList);
                if (trim > 0) _project.DeleteFrames(keep, trim);
                if (expansion is { } grow) ApplyCanvasExpansion(grow);
                // 셀마다 효과를 입히는 계산은 덮개를 띄우고 작업 스레드에서(넣기만 여기서)
                var bake = WorkOverlay.Run(this, UiStrings.Busy_Baking, _ => _project.PrepareBake(index, bakeIndices));
                result = _project.ApplyBake(bake);
            });
            ResultMessage = (extension is { } x ? ExtendSummary(x) + " · " : "")
                            + (trim > 0 ? TrimSummary(trim) + " · " : "")
                            + (expansion is { } grow ? CanvasExpansionSummary(oldWidth, oldHeight, grow) + " · " : "")
                            + BakeSummary(result, _layer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _project.PreviewLayerEffects(index, _effects);
            Ui.Error(this, ex.Message);
            return;
        }
        _baked = true;
        DialogResult = DialogResult.OK;
    }

    // ───────────── 설정 ─────────────

    /// <summary>고른 효과의 설정 칸을 다시 만든다. 설명 글은 칸에 늘어놓지 않고 프레임 범위 줄 끝의 정보 표시(i) 풍선에 모은다.</summary>
    private void ShowSettings()
    {
        _rangeRow = null;
        _extendCount = null;
        _extendModeBox = null;
        _extendTarget = null;
        _extendFor = -1;
        FillSettings();
        ShowSharedExtendSettings();
        var hints = _settings.Children.OfType<TextBlock>().Where(t => t.Classes.Contains(HintClass)).ToList();
        if (hints.Count == 0 || _rangeRow is null) return;
        foreach (var h in hints) _settings.Children.Remove(h);
        _rangeRow.Children.Add(Ui.InfoBadge(string.Join("\n\n", hints.Select(h => h.Text))));
    }

    /// <summary>설정 칸의 프레임 범위 줄(설명 정보 표시를 붙일 곳).</summary>
    private StackPanel? _rangeRow;

    private void FillSettings()
    {
        _settings.Children.Clear();
        _scriptStatus = null;
        if (SelectedRow is { Recipe: { } recipe } header)
        {
            FillRecipeSettings(header, recipe);
            return;
        }
        int i = Selected;
        if (i < 0 || i >= _effects.Count)
        {
            _settings.Children.Add(Ui.Label(_effects.Count == 0 ? UiStrings.S_LayerEffectsDialog_FillSettings_24136B66D0 : UiStrings.S_LayerEffectsDialog_FillSettings_12D0C7E74E));
            return;
        }
        var e = _effects[i];
        if (UiOf(e) is not { } ui)
        {
            // 모르는 효과는 고칠 수 없다 — 설명만 보인다.
            _settings.Children.Add(new TextBlock
            {
                Text = e is MissingEffect missing ? MissingNote(missing) : e.DisplayName,
                TextWrapping = TextWrapping.Wrap, MaxWidth = SettingsWidth - 10, Foreground = AppTheme.DimTextBrush,
            });
            return;
        }
        void Set(Func<LayerEffect, LayerEffect> change) => Update(i, change(_effects[i]));
        AddRangeEditor(i);
        AddClipEditor(i);
        ui.Panel(this, e, Set);
    }

    /// <summary>
    /// 움직이는 효과 공통: 빠르기 곡선, 반복(한 번/반복/왕복 + 횟수), 범위 길이 안내. <paramref name="timing"/>이 false면(진행 곡선을 쓰지 않는 효과 — 깜빡이기 프레임 간격·잔상)
    /// 범위 안내만.
    /// </summary>
    private void AddMotionEditors(int i, Action<Func<LayerEffect, LayerEffect>> set, bool timing = true)
    {
        var a = (AnimatedEffect)_effects[i];
        if (timing) AddTimingEditors(a, set);
        var (from, to) = VRangeOf(a);
        _settings.Children.Add(Hint(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_AddMotionEditors_610C3B2627,to - from + 1,from + 1,to + 1)
                                    + (from == to ? UiStrings.S_LayerEffectsDialog_AddMotionEditors_9BD880B938 : "")));
    }

    private void AddTimingEditors(AnimatedEffect a, Action<Func<LayerEffect, LayerEffect>> set)
    {
        SettingsRow(UiStrings.S_LayerEffectsDialog_AddTimingEditors_E1B19358AF, ChoiceEditor(Easings, (int)a.Easing, k => set(o => (AnimatedEffect)o with { Easing = (EffectEasing)k })));
        var count = NumEditor(1, AnimatedEffect.MaxRepeatCount, a.RepeatCount, NumKinds.Repeat, v => set(o => (AnimatedEffect)o with { RepeatCount = v }));
        count.IsEnabled = a.Repeat != EffectRepeat.Once;
        SettingsRow(UiStrings.S_LayerEffectsDialog_AddTimingEditors_E8E7F0E20B, ChoiceEditor(Repeats, (int)a.Repeat, k =>
        {
            count.IsEnabled = (EffectRepeat)k != EffectRepeat.Once;
            set(o => (AnimatedEffect)o with { Repeat = (EffectRepeat)k });
        }), count, Ui.Label(UiStrings.S_LayerEffectsDialog_AddTimingEditors_433F81EB07));
    }

    /// <summary>
    /// 프레임 범위: "모든 프레임"(나중에 늘어난 프레임까지) 또는 시작~끝 프레임(1부터). 체크를 풀면 현재 프레임 하나로 시작한다.
    /// 타임라인의 효과 줄에서 상자 끝을 끌어도 바뀐다.
    /// </summary>
    private void AddRangeEditor(int i)
    {
        var effect = _effects[i];
        bool virtualRange = UsesVirtualTimeline(effect);
        int n = virtualRange ? VirtualCount : _project.Frames.Count;
        FrameRange? range = virtualRange
            ? _virtualRangeOverrides.TryGetValue(effect, out var direct)
                ? direct
                : effect.Frames is { } stored && _extension is { } extension ? extension.MapRange(stored) : null
            : effect.Frames;
        var all = Ui.Check(UiStrings.S_LayerEffectsDialog_AddRangeEditor_1629B10BDB, range is null);
        var from = WithKind(Ui.Num(1, n, (range?.From ?? 0) + 1, 56), FrameNumberKind(end: false));
        var to = WithKind(Ui.Num(1, n, (range?.To ?? n - 1) + 1, 56), FrameNumberKind(end: true));
        from.IsEnabled = to.IsEnabled = range is not null;
        bool syncing = false;
        void Apply()
        {
            // 선택을 바꾸며 없어진 범위 칸의 지연 이벤트는 이전 효과에도, 새 효과에도 적용하지 않는다.
            if (syncing || Selected != i || _extendFor != i) return;
            var next = all.On() ? null : FrameRange.Of((int)from.NumValue() - 1, (int)to.NumValue() - 1, n);
            if (next is null && !all.On())
            {
                // 모든 프레임을 덮는 범위 = "모든 프레임"
                syncing = true;
                all.IsChecked = true;
                syncing = false;
            }
            from.IsEnabled = to.IsEnabled = next is not null;
            if (virtualRange)
            {
                SetVirtualRange(i, next);
            }
            else
            {
                FollowRange(_effects[i], next);
                Update(i, _effects[i] with { Frames = next });
            }
            SyncExtendEditor();
            if (_effects[i] is AnimatedEffect) ShowSettings(); // 범위 길이 안내 갱신
        }
        all.IsCheckedChanged += (_, _) =>
        {
            if (!all.On() && (int)from.NumValue() == 1 && (int)to.NumValue() == n)
            {
                // 모든 프레임을 덮는 범위는 "모든 프레임"과 같으므로 현재 프레임 하나로 시작한다.
                syncing = true;
                from.Value = to.Value = _frame + 1;
                syncing = false;
            }
            Apply();
        };
        from.ValueChanged += (_, _) => Apply();
        to.ValueChanged += (_, _) => Apply();
        _rangeRow = SettingsRow(UiStrings.S_LayerEffectsDialog_AddRangeEditor_C773F4F9DC, all, from, Ui.Label("~"), to);
    }

    /// <summary>고른 효과를 앞의 비클리핑 효과가 만든 영역에만 적용한다.</summary>
    private void AddClipEditor(int i)
    {
        var effect = _effects[i];
        int anchor = i - 1;
        while (anchor >= 0 && _effects[anchor].ClipToPrevious) anchor--;
        bool canClip = anchor >= 0 && _effects[anchor] is not AfterimageEffect;
        var clip = Ui.Check(UiStrings.EffectPreset_ClipToPrevious, effect.ClipToPrevious);
        clip.IsEnabled = canClip;
        ToolTip.SetTip(clip, canClip ? UiStrings.EffectPreset_ClipTip : UiStrings.EffectPreset_ClipUnavailableTip);
        clip.IsCheckedChanged += (_, _) =>
        {
            if (Selected != i || !canClip || _effects[i].ClipToPrevious == clip.On()) return;
            ReplaceEffect(i, _effects[i] with { ClipToPrevious = clip.On() });
            RebuildList(i);
        };
        SettingsRow(UiStrings.EffectPreset_ApplyTarget, clip);
    }

    /// <summary>설정 칸 이름 열의 크기 공유 묶음 — 모든 줄의 이름 열이 가장 긴 이름에 맞춰 같은 폭이 된다.</summary>
    private const string LabelColumn = "EffectSettingsLabel";
    /// <summary>위 줄에 딸린 줄(<see cref="SettingsSubRow"/>)의 이름 들여쓰기.</summary>
    private const double SubIndent = 12;

    /// <summary>
    /// 설정 칸에 한 줄: 왼쪽 이름 열(모든 줄이 같은 폭, 왼쪽 맞춤)에 <paramref name="label"/>, 오른쪽 열에 컨트롤들(모든 줄이 같은 x에서 시작).
    /// 이름이 ""면 오른쪽 열만. 오른쪽 열 줄을 돌려준다.
    /// </summary>
    private StackPanel SettingsRow(string label, params Control[] controls) => FormRow(label.Length == 0 ? null : Ui.Label(label), 0, controls);

    /// <summary>위 줄에 딸린 줄(위 줄 고름에 따라 쓰이는 값): 이름을 조금 들여 쓴다.</summary>
    private StackPanel SettingsSubRow(string label, params Control[] controls) => FormRow(Ui.Label(label), SubIndent, controls);

    /// <summary>체크 상자가 이름인 줄(켜야 오른쪽 값을 씀): 체크 상자가 이름 열에 들어간다.</summary>
    private StackPanel SettingsCheckRow(CheckBox head, params Control[] controls) => FormRow(head, 0, controls);

    private StackPanel FormRow(Control? head, double indent, Control[] controls)
    {
        if (head is not null)
        {
            head.VerticalAlignment = VerticalAlignment.Center;
            head.Margin = new Thickness(head.Margin.Left + indent, head.Margin.Top, head.Margin.Right + 6, head.Margin.Bottom);
        }
        var row = Ui.Row(controls);
        AddFormRow(head, row);
        return row;
    }

    /// <summary>오른쪽 열에 한 덩어리(줄 바꿈 패널 등 — 열 폭에 맞춰 늘어남). 이름은 첫 줄 높이에 맞춘다.</summary>
    private void SettingsBlock(string label, Control body)
    {
        var head = Ui.Label(label);
        head.VerticalAlignment = VerticalAlignment.Top;
        head.Margin = new Thickness(3, 6, 9, 3);
        AddFormRow(head, body);
    }

    /// <summary>이름 열(<see cref="LabelColumn"/> 공유 폭, 왼쪽 맞춤) + 값 열 한 줄을 설정 칸에 넣는다.</summary>
    private void AddFormRow(Control? head, Control body)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = LabelColumn });
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        if (head is not null)
        {
            head.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(head);
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        _settings.Children.Add(grid);
    }

    private const string HintClass = "effect-hint";

    /// <summary>설명 글. 설정 칸에 넣으면 <see cref="ShowSettings"/>가 정보 표시 풍선으로 옮긴다(펼침 상자 안에서는 그대로 보임).</summary>
    private static TextBlock Hint(string text, double maxWidth = 290)
    {
        var t = Ui.Label(text);
        t.TextWrapping = TextWrapping.Wrap;
        t.MaxWidth = maxWidth;
        t.Foreground = AppTheme.DimTextBrush;
        t.Classes.Add(HintClass);
        return t;
    }

    private static ColorField ColorEditor(Color32 value, Action<Color32> changed)
    {
        var field = new ColorField(value);
        field.ColorChanged += (_, _) => changed(field.Color);
        return field;
    }

    /// <summary>효과 값 숫자 칸: <paramref name="kind"/>의 우클릭 예시 값·단위·최근 값 묶음을 붙인다.</summary>
    private static NumericUpDown NumEditor(int min, int max, int value, NumKind kind, Action<int> changed)
    {
        var n = WithKind(Ui.Num(min, max, value, 56), kind);
        n.ValueChanged += (_, _) => changed((int)n.NumValue());
        return n;
    }

    private static NumericUpDown WithKind(NumericUpDown n, NumKind kind) => n.Presets(kind.Values, [], kind.Format).Unit(kind.Unit).RecentKey(kind.Key);

    /// <summary>
    /// 프레임 번호 칸(1부터, 범위 시작·끝) 예시: 처음·지금·마지막 프레임과 태그 시작(끝) 프레임에 이름을 붙이고, 남는 자리는 고른 간격.
    /// </summary>
    private NumKind FrameNumberKind(bool end)
    {
        int n = _project.Frames.Count, now = _frame + 1;
        var names = new Dictionary<decimal, string>();
        foreach (var t in _project.Tags)
            if ((end ? t.To : t.From) + 1 is var f && f >= 1 && f <= n) names.TryAdd(f, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_FrameNumberKind_D50B47CDF3,t.Name,(end ? UiStrings.S_LayerEffectsDialog_FrameNumberKind_FC90F137FF : UiStrings.S_LayerEffectsDialog_FrameNumberKind_5E664EA7C3)));
        names[now] = names.TryGetValue(now, out var tag) ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_FrameNumberKind_F07F8BC693,tag) : UiStrings.S_LayerEffectsDialog_FrameNumberKind_36C0AD1206;
        var values = new SortedSet<decimal>(names.Keys) { 1, n };
        foreach (var v in Ui.FrameNumbers(n))
        {
            if (values.Count >= 12) break;
            values.Add(v);
        }
        return new(end ? "effect.frame.to" : "effect.frame.from", "", [.. values],
            v => names.TryGetValue(v, out var name) ? $"{v} ({name})" : $"{v}");
    }

    /// <summary>중심 X·Y 칸(원본 왼쪽 위 기준 px) 예시: 0, 그림 가운데·끝(이름 붙임)과 흔한 거리.</summary>
    private NumKind CenterKind(bool vertical)
    {
        var source = SourceAt(_frame);
        int size = source is null ? vertical ? _project.Height : _project.Width : vertical ? source.Height : source.Width;
        decimal mid = size / 2, last = Math.Max(0, size - 1);
        var values = new SortedSet<decimal> { 0, mid, last };
        foreach (var v in new decimal[] { 4, 8, 16, 24, 32, 48, 64 })
            if (v < last && values.Count < 10) values.Add(v);
        string edge = vertical ? UiStrings.S_LayerEffectsDialog_CenterKind_7529E535E3 : UiStrings.S_LayerEffectsDialog_CenterKind_DE4F2D8B9E;
        return new(vertical ? "effect.center.y" : "effect.center.x", "px", [.. values],
            v => v == mid ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_CenterKind_6FC4EF0158,v) : v == last && v > 0 ? $"{v} px ({edge})" : $"{v} px");
    }

    private static ComboBox ChoiceEditor(string[] items, int selected, Action<int> changed)
    {
        var c = Ui.Combo(170, items);
        c.SelectedIndex = Math.Max(0, selected);
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }

    private static CheckBox CheckEditor(string text, bool value, Action<bool> changed)
    {
        var c = Ui.Check(text, value);
        c.IsCheckedChanged += (_, _) => changed(c.On());
        return c;
    }

    // ───────────── 미리보기 ─────────────

    /// <summary>움직이는 효과의 프레임 범위(null = 모든 프레임).</summary>
    private (int From, int To) RangeOf(LayerEffect e)
        => e.Frames is { } r ? (r.From, Math.Min(r.To, _project.Frames.Count - 1)) : (0, _project.Frames.Count - 1);

    /// <summary>재생할 범위: "고른 효과 범위만"이면 고른 효과(묶음이면 구성 효과 범위를 모두 덮는) 범위, 체크를 끄면 늘린 뒤 전체 프레임.</summary>
    private (int From, int To)? PlayRange()
    {
        if (_effectOnly.On())
            return SelectedBlock is (var first, var last) && last < _effects.Count
                ? Enumerable.Range(first, last - first + 1).Select(i => VRangeOf(_effects[i])).Aggregate((a, b) => (Math.Min(a.From, b.From), Math.Max(a.To, b.To)))
                : null;
        return VirtualCount > 0 ? (0, VirtualCount - 1) : null;
    }

    /// <summary>재생 범위를 현재 상태에 맞춘다. 효과를 새로 골랐거나 범위 방식을 바꾸면 처음부터 곧바로 재생한다.</summary>
    private void SyncPlayback(bool restart = false)
    {
        var range = PlayRange();
        _play.IsEnabled = range is not null;
        if (_largePlay is { } largePlay) largePlay.IsEnabled = _play.IsEnabled;
        if (range is null)
        {
            _timer.Stop();
            int frame = Math.Clamp(VirtualOf(_frame), 0, VirtualCount - 1);
            if (_previewFrame != frame)
            {
                _previewFrame = frame;
                RefreshPreview();
            }
        }
        else
        {
            bool moved = restart || _previewFrame < range.Value.From || _previewFrame > range.Value.To;
            if (moved)
            {
                _previewFrame = range.Value.From;
                RefreshPreview();
            }
            if (restart) StartTimer();
        }
        UpdatePlayButtons();
    }

    private void TogglePlay()
    {
        if (_timer.IsEnabled) _timer.Stop();
        else if (PlayRange() is { } range)
        {
            if (_previewFrame < range.From || _previewFrame >= range.To) _previewFrame = range.From;
            StartTimer();
            RefreshPreview();
        }
        UpdatePlayButtons();
    }

    /// <summary>창과 크게 보기 창의 재생 단추 글을 재생 상태에 맞춘다.</summary>
    private void UpdatePlayButtons()
    {
        foreach (var button in new[] { _play, _largePlay })
            if (button is not null)
                button.Content = _timer.IsEnabled ? Ui.IconLabel(VectorIcon.Pause, UiStrings.S_LayerEffectsDialog_SyncPlayback_61EB6DFDA0) : Ui.IconLabel(VectorIcon.Play, UiStrings.S_LayerEffectsDialog_SyncPlayback_1100994439);
    }

    /// <summary>재생을 멈추고 한 프레임 앞(+1)이나 뒤(−1)로 옮긴다(재생 범위 안에서 돌아감, 범위가 없으면 모든 프레임).</summary>
    private void StepPreview(int delta)
    {
        _timer.Stop();
        var (from, to) = PlayRange() ?? (0, VirtualCount - 1);
        if (to < from) return;
        int f = _previewFrame < from || _previewFrame > to ? from : _previewFrame + delta;
        _previewFrame = f > to ? from : f < from ? to : f;
        RefreshPreview();
        UpdatePlayButtons();
    }

    private void StartTimer()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(40, _project.Frames[OriginalOf(_previewFrame)].DurationMs));
        _timer.Start();
    }

    /// <summary>재생 중 다음 프레임(범위 끝 다음은 처음 — "끝에서 바로 처음으로"가 꺼져 있으면 끝 프레임은 조금 더 보여 줌).</summary>
    private void NextPreviewFrame()
    {
        if (PlayRange() is not { } range)
        {
            SyncPlayback();
            return;
        }
        int next = _previewFrame >= range.To || _previewFrame < range.From ? range.From : _previewFrame + 1;
        // 다음 프레임을 아직 그리는 중이면 지금 프레임을 그대로 두고 다음 틱에 다시 본다
        if (IsRendering(next)) return;
        _previewFrame = next;
        int ms = Math.Max(40, _project.Frames[OriginalOf(_previewFrame)].DurationMs);
        _timer.Interval = TimeSpan.FromMilliseconds(_previewFrame == range.To && !s_seamlessLoop ? ms * 3 : ms);
        RefreshPreview();
    }

    /// <summary>효과 원본(그 프레임, 창이 열린 동안 바뀌지 않으므로 프레임마다 한 번만 만든다).</summary>
    private PixelImage? SourceAt(int frame)
    {
        if (!_sources.TryGetValue(frame, out var src)) _sources[frame] = src = _project.EffectSource(_layer, frame);
        return src;
    }

    /// <summary>
    /// 전·후를 모든 프레임에 공통인 확인 후 캔버스 자리와 크기로 그린다. "후"는 작업 스레드가 그린 결과(<see cref="RestartRenders"/>)를 쓰고,
    /// 아직 그리는 중이면 이전 그림을 그대로 두고 오래 걸리면 회색 덮개를 씌운다.
    /// 확장을 끈 경우 캔버스 밖 효과 픽셀은 흐리게 표시한다.
    /// </summary>
    private void RefreshPreview()
    {
        int f = _previewFrame;
        _strip.Current = f;
        var all = VirtualEffects();
        var source = VirtualSourceAt(f);
        string where = _layer.IsGroup ? UiStrings.S_LayerEffectsDialog_RefreshPreview_BDAFE80FE8 : _layer.IsPart ? UiStrings.S_LayerEffectsDialog_RefreshPreview_BD6BE5C32E : "";
        if (_extension is not null)
            where = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_RefreshPreview_6A782EF4D8,VirtualCount,where,OriginalOf(f) + 1,(IsCopyFrame(f) ? UiStrings.S_LayerEffectsDialog_RefreshPreview_E18A9F9C35 : ""));
        var bounds = _previewBounds;
        var canvas = new Rectangle(_previewCanvas.X - bounds.X, _previewCanvas.Y - bounds.Y, _previewCanvas.Width, _previewCanvas.Height);
        if (source is null)
        {
            _caption.Text = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_RefreshPreview_A0B1282CEF,f + 1,where);
            var empty = new PixelImage(bounds.Width, bounds.Height);
            _preview.SetImages(empty, empty, canvas);
            _preview.SetPending(false, 1);
            _largePreview?.InvalidateVisual();
            if (_largeCaption is not null) _largeCaption.Text = _caption.Text;
            return;
        }
        string progress = Selected >= 0 && Selected < all.Count && all[Selected] is AnimatedEffect a && a.AppliesTo(f)
            ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_RefreshPreview_19220A5382,a.AtFrame(f, VirtualCount, _project.EffectPivot(_layer, OriginalOf(f))).Time * 100) : "";
        _caption.Text = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_RefreshPreview_64659703DD,f + 1,where,progress);
        if (_largeCaption is not null) _largeCaption.Text = _caption.Text;
        if (RenderedFrame(f) is not { } result)
        {
            _preview.SetPending(_renders?.Overlay == true, _renders?.Progress ?? 0);
            _largePreview?.InvalidateVisual();
            return;
        }
        // 재생용으로 미리 만든 그림이 있으면 바꿔 끼우기만 한다(재생 중 프레임마다 큰 그림을 자르고 비트맵으로 바꾸지 않게)
        if (_renders?.Shown is { } shown && shown.Bounds == bounds && shown.Canvas == _previewCanvas && shown.Get(f) is { } prebuilt)
            _preview.ShowBitmaps(prebuilt.Before, prebuilt.After, bounds.Size, canvas);
        else
        {
            _preview.SetImages(PreviewBefore(source, bounds, PreviewOffset), PreviewAfter(result, bounds, PreviewOffset), canvas);
            // 캔버스 늘리기를 켜고 꺼 범위가 바뀌었으면 재생용 그림을 다시 만든다
            if (_renders is { Complete: true } job) BuildShownFrames(job);
        }
        _preview.SetPending(false, 1);
        _largePreview?.InvalidateVisual();
    }

    /// <summary>부위면 부위 자리(전·후 그림의 캔버스 좌표 기준).</summary>
    private Point PreviewOffset => _layer.IsPart ? _layer.PartOffset : Point.Empty;

    /// <summary>"전" 미리보기 그림: 원본을 모든 프레임에 공통인 범위 <paramref name="bounds"/>(캔버스 좌표)로 자른다. 어느 스레드에서든.</summary>
    private static PixelImage PreviewBefore(PixelImage source, Rectangle bounds, Point offset)
    {
        if (bounds.X == offset.X && bounds.Y == offset.Y && bounds.Width == source.Width && bounds.Height == source.Height) return source;
        var before = new PixelImage(bounds.Width, bounds.Height);
        before.Blit(source, new Rectangle(bounds.X - offset.X, bounds.Y - offset.Y, bounds.Width, bounds.Height), 0, 0);
        return before;
    }

    /// <summary>"후" 미리보기 그림: 효과 결과를 <paramref name="bounds"/>로 자른다. 어느 스레드에서든.</summary>
    private static PixelImage PreviewAfter(FrameResult result, Rectangle bounds, Point offset)
    {
        var region = new Rectangle(bounds.X - offset.X - result.X, bounds.Y - offset.Y - result.Y, bounds.Width, bounds.Height);
        return region == result.Image.Bounds ? result.Image : result.Image.Crop(region);
    }

    private void OpenLargePreview()
    {
        if (_previewWindow is { } window)
        {
            window.Activate();
            return;
        }
        var view = new PreviewView(_preview) { Margin = new Thickness(12, 12, 12, 4) };
        // 아래 줄: 재생·이전·다음 프레임, 지금 프레임 설명, 재생 범위 체크 두 개(창의 체크와 같이 바뀜).
        var play = Ui.Button(VectorIcon.Play, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_3DC0585476, (_, _) => TogglePlay(), 80);
        ToolTip.SetTip(play, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_46C6465E13);
        var prev = Ui.IconButton(VectorIcon.ChevronLeft, UiStrings.EffectPreview_PrevFrame, (_, _) => StepPreview(-1));
        var next = Ui.IconButton(VectorIcon.ChevronRight, UiStrings.EffectPreview_NextFrame, (_, _) => StepPreview(1));
        var effectOnly = Ui.Check(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_BEC5ADB6D1, _effectOnly.On());
        effectOnly.VerticalAlignment = VerticalAlignment.Center;
        effectOnly.IsCheckedChanged += (_, _) => _effectOnly.IsChecked = effectOnly.On();
        ToolTip.SetTip(effectOnly, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_2138B1E743);
        var loop = Ui.Check(UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_96BC2CE00E, _loop.On());
        loop.VerticalAlignment = VerticalAlignment.Center;
        loop.IsCheckedChanged += (_, _) => _loop.IsChecked = loop.On();
        ToolTip.SetTip(loop, UiStrings.S_LayerEffectsDialog_LayerEffectsDialog_0500474F59);
        var caption = Ui.Label(_caption.Text ?? "");
        caption.Foreground = AppTheme.DimTextBrush;
        caption.VerticalAlignment = VerticalAlignment.Center;
        var bar = Ui.Row(play, prev, next, effectOnly, loop, caption);
        bar.Margin = new Thickness(12, 0, 12, 10);
        DockPanel.SetDock(bar, Dock.Bottom);
        var large = new Window
        {
            Title = Title,
            Width = 900,
            Height = 620,
            MinWidth = 520,
            MinHeight = 300,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppTheme.BackBrush,
            Content = new DockPanel { Children = { bar, view } },
        };
        large.KeyDown += (_, e) =>
        {
            // 단추·체크에 포커스가 있으면 Space는 그 단추를 누른다 — 창에서 또 재생을 바꾸지 않는다.
            if (e.Key == Key.Space && e.Source is Avalonia.Controls.Primitives.ToggleButton or Button) return;
            switch (e.Key)
            {
                case Key.Escape: large.Close(); break;
                case Key.Space: TogglePlay(); break;
                case Key.Left: StepPreview(-1); break;
                case Key.Right: StepPreview(1); break;
                default: return;
            }
            e.Handled = true;
        };
        large.Closed += (_, _) =>
        {
            _previewWindow = null;
            _largePreview = null;
            _largePlay = null;
            _largeCaption = null;
            _largeEffectOnly = null;
            _largeLoop = null;
        };
        _previewWindow = large;
        _largePreview = view;
        _largePlay = play;
        _largeCaption = caption;
        _largeEffectOnly = effectOnly;
        _largeLoop = loop;
        play.IsEnabled = _play.IsEnabled;
        UpdatePlayButtons();
        large.Show(this);
    }

    /// <summary>전·후를 같은 캔버스 위치와 배율로 표시하고, 잘려 나가는 픽셀은 흐리게 표시한다.</summary>
    private sealed class PreviewView : Control, IDisposable
    {
        private readonly PreviewView? _source;
        private WriteableBitmap? _before, _after;
        /// <summary>그림이 나타내는 원래 픽셀 크기(미리 줄여 둔 재생용 그림이면 비트맵보다 큼 — 자리·배율은 이 크기로 정한다).</summary>
        private Size _size;
        /// <summary>지금 그림을 이 칸이 가졌는지(아니면 재생용 그림 묶음 <see cref="ShownFrames"/>이 가짐).</summary>
        private bool _owned;
        private Rectangle _canvas;
        /// <summary>"후" 그림을 다시 그리는 중이 오래 걸림(회색 덮개 + 로딩 아이콘)과 진행.</summary>
        private bool _pending;
        private double _progress;

        public PreviewView() { }
        public PreviewView(PreviewView source) => _source = source;

        public bool HasImages => (_source ?? this)._before is not null;

        /// <summary>"후" 그림이 아직 지금 효과의 결과가 아님(이전 그림 위에 덮개를 씌움).</summary>
        public void SetPending(bool pending, double progress)
        {
            if (_pending == pending && (!pending || Math.Abs(_progress - progress) < 1e-9)) return;
            _pending = pending;
            _progress = progress;
            InvalidateVisual();
        }

        public void SetImages(PixelImage? before, PixelImage? after, Rectangle canvas)
        {
            Dispose();
            _canvas = canvas;
            if (before is not null && after is not null)
            {
                try
                {
                    _before = ImageUtil.ToBitmap(before);
                    _after = ImageUtil.ToBitmap(after);
                    _size = new Size(before.Width, before.Height);
                    _owned = true;
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }
            InvalidateVisual();
        }

        /// <summary>미리 만든 재생용 그림을 보인다(바꿔 끼우기만 — 그림은 묶음이 가진다). <paramref name="size"/> = 원래 픽셀 크기.</summary>
        public void ShowBitmaps(WriteableBitmap before, WriteableBitmap after, Size size, Rectangle canvas)
        {
            Dispose();
            (_before, _after, _size, _canvas, _owned) = (before, after, size, canvas, false);
            InvalidateVisual();
        }

        /// <summary>그 그림을 보이고 있는지.</summary>
        public bool Shows(WriteableBitmap bitmap) => ReferenceEquals(_before, bitmap) || ReferenceEquals(_after, bitmap);

        /// <summary>보이는 재생용 그림의 묶음이 버려짐: 다음 그림으로 바꿀 때 이 칸이 버린다.</summary>
        public void Adopt() => _owned = _before is not null;

        public override void Render(DrawingContext g)
        {
            var b = new Rect(Bounds.Size);
            g.FillRectangle(AppTheme.BackBrush, b);
            var data = _source ?? this;
            if (data._before is not { } before || data._after is not { } after)
            {
                AppTheme.DrawTextIn(g, UiStrings.S_LayerEffectsDialog_Render_BF9D824663, b, AppTheme.DimTextBrush, center: true);
                return;
            }
            double half = (b.Width - 12) / 2, top = 18;
            AppTheme.DrawTextIn(g, UiStrings.S_LayerEffectsDialog_Render_71A8D08947, new Rect(0, 0, half, 16), AppTheme.DimTextBrush, AppTheme.SmallFontSize, center: true);
            AppTheme.DrawTextIn(g, UiStrings.S_LayerEffectsDialog_Render_894FF3899A, new Rect(half + 12, 0, half, 16), AppTheme.DimTextBrush, AppTheme.SmallFontSize, center: true);
            DrawImage(g, before, data._size, new Rect(0, top, half, b.Height - top), data._canvas);
            var shown = DrawImage(g, after, data._size, new Rect(half + 12, top, half, b.Height - top), data._canvas);
            if (data._pending)
            {
                RenderOverlay.DrawVeil(g, shown);
                RenderOverlay.DrawBusy(g, shown, data._progress);
            }
        }

        /// <summary>원래 크기 <paramref name="size"/>의 그림을 정수 배율(작으면 축소)로 그린다 — 비트맵이 미리 줄인 것이어도 자리·크기는 같다.</summary>
        private static Rect DrawImage(DrawingContext g, WriteableBitmap bitmap, Size size, Rect area, Rectangle canvas)
        {
            // The faint spill shows where the effect continues beyond the actual project canvas.
            Rect dest;
            using (g.PushOpacity(0.25))
                dest = ImageUtil.DrawFittedThumb(g, bitmap, size, area);
            // DrawFittedThumb returns the exact pixel-snapped destination, including downscaled images.
            var canvasRect = new Rect(dest.X + canvas.X * dest.Width / size.Width, dest.Y + canvas.Y * dest.Height / size.Height,
                canvas.Width * dest.Width / size.Width, canvas.Height * dest.Height / size.Height);
            using (g.PushClip(canvasRect))
                ImageUtil.DrawFittedThumb(g, bitmap, size, area, checker: true);
            using (g.PushClip(dest))
                g.DrawRectangle(null, AppTheme.Pen(AppTheme.BorderColor), canvasRect);
            return dest;
        }

        public void Dispose()
        {
            if (_owned)
            {
                _before?.Dispose();
                _after?.Dispose();
            }
            _before = _after = null;
            _owned = false;
        }
    }
}
