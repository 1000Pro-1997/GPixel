using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// 캔버스 보기 상태(화면 크기와 무관): 확대율, 화면 가운데가 가리키는 그림 좌표(캔버스 좌표 — 부위 화면이면 부위 기준), 그림 전체가 화면 안에 보였는지.
/// </summary>
public readonly record struct CanvasView(int Zoom, double CenterX, double CenterY, bool WholeVisible);

/// <summary>
/// 픽셀 캔버스: 현재 프레임 한 장(보이는 레이어 합성)을 정수 배율(1~64)로 보여 주고, 현재 레이어의 셀에 그린다.
/// 체커 배경, 픽셀/타일 격자, 타일 모드(5×5, 큰 그림은 3×3 반복), 대칭 안내선, 피벗, 9-slice 안내선(부위), 선택 영역, 양파 껍질,
/// H 누르는 동안 이전 프레임 보기, 변형 모드(Ctrl+T)의 주황 테두리·손잡이(옮기기·크기·회전·회전 중심 끌기),
/// 캔버스 크기 모드(Ctrl+Alt+C)의 하늘색 캔버스 테두리·손잡이(가장자리, Ctrl = 그림 크기도 함께)와 그림 옮기기.
/// <para>
/// 부위를 고르면 캔버스 = 그 부위(부위 크기, 부위 안 좌표로 그림)이고 프레임의 나머지는 흐리게 둘레에 보인다.
/// 그룹을 고르면 프레임 합성을 보며 부위를 배치한다: 올린 부위 테두리·이름, 끌어서 옮기기(놓으면 실행취소 한 단계),
/// 방향키로 1px(Shift 8px) 옮기기, 더블클릭으로 그 부위 고르기.
/// </para>
/// </summary>
public sealed class PixelCanvas : Control
{
    public static readonly int[] ZoomLevels = [1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64];

    private enum DragMode { None, Pan, Stroke, Shape, Gradient, Fill, Picker, Marquee, Lasso, MoveSelection, MoveLayer, Pivot, Arrange, Slice,
        TransformMove, TransformScale, TransformRotate, TransformPivot, CanvasResizeEdge, CanvasResizeMove }

    /// <summary>변형 모드에서 화면 위치가 가리키는 것.</summary>
    private enum TransformHit { None, Move, Scale, Rotate, Pivot }

    /// <summary>9-slice 도구로 끄는 테두리 선.</summary>
    private enum SliceEdge { None, Left, Top, Right, Bottom }

    private static readonly ImmutableDashStyle Dash = new([4.0, 2.0], 0);
    private static readonly IPen BorderPen = AppTheme.Pen(AppTheme.BorderColor);
    private static readonly IPen MirrorPen = AppTheme.Pen(Color.FromArgb(200, 255, 90, 160), 1.5, Dash);
    private static readonly IPen SliceBorderPen = AppTheme.Pen(Color.FromArgb(220, 90, 220, 120), 1, Dash);
    /// <summary>9-slice 도구: Unity 스프라이트 편집기처럼 초록 실선과 손잡이.</summary>
    private static readonly IPen SliceEditPen = AppTheme.Pen(Color.FromArgb(255, 60, 230, 90), 1);
    private static readonly IPen SliceEditActivePen = AppTheme.Pen(Color.FromArgb(255, 160, 255, 170), 2);
    private static readonly IBrush SliceHandleBrush = AppTheme.Brush(Color.FromArgb(255, 60, 230, 90));
    private static readonly IPen SliceHandlePen = AppTheme.Pen(Color.FromArgb(255, 10, 40, 15), 1);
    private static readonly IPen FrameBoundsPen = AppTheme.Pen(Color.FromArgb(110, 255, 255, 255), 1, Dash);
    private static readonly IPen PartFaintPen = AppTheme.Pen(Color.FromArgb(90, 255, 255, 255), 1, Dash);
    private static readonly IPen PartHoverPen = AppTheme.Pen(AppTheme.AccentColor, 1.5, Dash);
    private static readonly IPen PartSelectedPen = AppTheme.Pen(AppTheme.AccentColor, 2);
    /// <summary>자유 선택: 지금 놓으면 선택될 영역 테두리(파란 점선, 어두운 바탕선 위).</summary>
    private static readonly IPen LassoPreviewPen = AppTheme.Pen(Color.FromRgb(90, 170, 255), 1.5, Dash);
    private static readonly IPen LassoPreviewBackPen = AppTheme.Pen(Color.FromArgb(150, 0, 0, 0), 2.5);
    /// <summary>파일 끌어 오기: 캔버스 둘레 강조(강조색)와 그림이 들어갈 자리(강조색 점선, 옅게 채움).</summary>
    private static readonly IPen DropHighlightPen = AppTheme.Pen(Color.FromArgb(200, AppTheme.AccentColor.R, AppTheme.AccentColor.G, AppTheme.AccentColor.B), 2);
    private static readonly IPen DropGhostPen = AppTheme.Pen(AppTheme.AccentColor, 1.5, Dash);
    private static readonly IBrush DropGhostBrush = AppTheme.Brush(Color.FromArgb(40, AppTheme.AccentColor.R, AppTheme.AccentColor.G, AppTheme.AccentColor.B));
    /// <summary>변형 모드: 변형한 사각형 테두리(주황 점선, 어두운 바탕선 위) — 선택 개미 줄·올가미 미리보기(파랑)와 구별된다.</summary>
    private static readonly IPen TransformPen = AppTheme.Pen(Color.FromRgb(255, 168, 0), 1.5, new ImmutableDashStyle([5.0, 3.0], 0));
    private static readonly IPen TransformBackPen = AppTheme.Pen(Color.FromArgb(170, 0, 0, 0), 3);
    private static readonly IBrush TransformHandleBrush = AppTheme.Brush(Color.FromRgb(255, 168, 0));
    private static readonly IPen TransformHandlePen = AppTheme.Pen(Color.FromArgb(230, 20, 12, 0), 1);
    private static readonly IPen TransformPivotPen = AppTheme.Pen(Color.FromRgb(255, 168, 0), 1.5);
    /// <summary>캔버스 크기 모드: 새 캔버스 테두리(하늘색 점선)와 흰 손잡이 — 변형 모드(주황)와 구별된다.</summary>
    private static readonly IPen CanvasResizePen = AppTheme.Pen(Color.FromRgb(60, 210, 255), 1.5, new ImmutableDashStyle([5.0, 3.0], 0));
    private static readonly IBrush CanvasResizeHandleBrush = AppTheme.Brush(Colors.White);
    private static readonly IPen CanvasResizeHandlePen = AppTheme.Pen(Color.FromRgb(0, 120, 170), 1.5);
    private static readonly IBrush LabelBrush = AppTheme.Brush(Color.FromArgb(200, 0, 0, 0));
    private static readonly IPen PivotActivePen = AppTheme.Pen(Colors.White, 1.5);
    private static readonly IPen PivotPen = AppTheme.Pen(Color.FromArgb(255, 255, 120, 80), 1.5);
    /// <summary>그룹 화면에서 부위마다의 피벗(파란색, 프레임 피벗보다 작게).</summary>
    private static readonly IPen PartPivotPen = AppTheme.Pen(Color.FromRgb(90, 170, 255), 1.5);
    private static readonly IBrush PartPivotBrush = AppTheme.Brush(Color.FromRgb(90, 170, 255));
    private static readonly IPen PivotBackPen = AppTheme.Pen(Color.FromArgb(160, 0, 0, 0), 3.5);
    private static readonly IPen WhitePen = AppTheme.Pen(Colors.White);
    private static readonly IPen CursorWhitePen = AppTheme.Pen(Color.FromArgb(200, 255, 255, 255));
    private static readonly IPen CursorBlackPen = AppTheme.Pen(Color.FromArgb(200, 0, 0, 0));
    private static readonly IPen CursorOutlineBackPen = new ImmutablePen(AppTheme.Brush(Color.FromArgb(200, 0, 0, 0)), 3, null, PenLineCap.Square);
    private static readonly IPen CursorOutlineFrontPen = new ImmutablePen(AppTheme.Brush(Color.FromArgb(220, 255, 255, 255)), 1, null, PenLineCap.Square);
    private static readonly IBrush DimBrush = AppTheme.Brush(Color.FromArgb(70, 0, 0, 0));
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor SizeWeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor SizeNsCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeNwseCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor SizeNeswCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Lazy<Cursor> RotateCursor = new(BuildRotateCursor);
    /// <summary>스포이드 커서(<see cref="BuildPickerCursor"/>) — 픽셀로 정리 창의 "원본에서 색 추가"도 쓴다.</summary>
    internal static readonly Lazy<Cursor> PickerCursor = new(BuildPickerCursor);

    private readonly EditorContext _ctx;
    private WriteableBitmap? _floatingBmp;
    /// <summary>양파 껍질 사본(프레임 거리 −5~+5마다 재사용).</summary>
    private readonly Dictionary<int, WriteableBitmap> _ghosts = new();
    private int _zoom = 8;
    private Point _offset;
    private readonly DispatcherTimer _antsTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    /// <summary>합성 계산 중 로딩 아이콘을 돌리려고 다시 그리는 타이머(덮개가 보일 때만 돈다).</summary>
    private readonly DispatcherTimer _busyTimer = new() { Interval = RenderOverlay.FrameInterval };
    /// <summary>계산 중 덮개 위에 효과 없이 바로 보이는 그리는 중인 셀.</summary>
    private WriteableBitmap? _strokeBmp;
    private int _antsPhase;
    private bool _pendingCenter;
    /// <summary>가운데 놓기 예약 번호: <see cref="RestoreView"/>가 올리면 미뤄 둔 가운데 놓기는 건너뛴다.</summary>
    private int _centerVersion;

    // 드래그 상태
    private DragMode _drag;
    private MouseButton _dragButton;
    private Point _dragStartScreen, _lastScreen;
    private Point _startPx, _lastPx;
    private PixelImage? _strokeBase;
    private PixelPerfectStroke? _ppStroke;
    private Color32 _strokeColor, _strokeColor2, _replaceTarget;
    private bool _strokeLighten;
    private HashSet<Point>? _visited;
    private ToolKind _strokeTool;
    /// <summary>도형 도구 획의 모양(누를 때 정해 끝까지 씀).</summary>
    private ShapeStyle _strokeShape;
    /// <summary>
    /// 곡선: 끌어서 양 끝(A·B)을 정한 뒤 휘는 중(버튼은 떼어 있음, 그림에는 아직 안 칠하고 미리보기만).
    /// 처음 버튼으로 클릭(또는 Enter) = 확정, 반대 버튼·Esc·Ctrl+Z = 취소. 휘는 점 = <see cref="_curveThrough"/>(곡선 가운데가 지남).
    /// </summary>
    private (Point A, Point B, MouseButton Button)? _curve;
    private Point _curveThrough;
    private Pivot _pivotBefore;
    private SliceEdge _sliceEdge;
    /// <summary>9-slice 도구로 끄는 동안의 테두리(놓을 때 실행취소 한 단계로 반영).</summary>
    private SpriteBorder? _sliceDraft;
    /// <summary>끄는 9-slice의 대상(그룹 화면의 부위, 아니면 null = 현재 대상)과 그 이미지 좌표 사각형.</summary>
    private Layer? _slicePart;
    private Rectangle _sliceRect;
    private bool _dragMoved;
    private bool _altUsed;
    private KeyModifiers _mods;
    private Point? _hoverPx;
    private Point? _lastStrokeEnd;
    private Point _mouseScreen;
    /// <summary>자유 선택으로 끄는 중인 올가미 꼭짓점(이미지 좌표, 이어진 점들).</summary>
    private readonly List<Point> _lassoPath = new();
    /// <summary>올가미를 지금 놓으면 선택될 영역의 테두리 선분(이미지 좌표). 점이 늘면 버리고 그릴 때 다시 만든다.</summary>
    private List<(Point A, Point B)>? _lassoPreview;
    /// <summary>모양 선택의 테두리 선분(이미지 좌표, 픽셀 모서리). 선택이 바뀌면 다시 만든다.</summary>
    private List<(Point A, Point B)>? _selectionOutline;
    /// <summary>그라데이션 도구로 끄는 중인 끝점(이미지 좌표, Shift 각도 고정 반영).</summary>
    private Point _gradientEnd;

    // 변형 모드 끌기: 시작할 때의 값, 잡은 손잡이(원본 기준 -1/0/1), 시작점(이미지 좌표), 회전 시작 각(라디안)
    private TransformParams _tfStart;
    private int _tfHx, _tfHy;
    private Avalonia.Point _tfStartImg;
    private double _tfStartAngle;

    // 캔버스 크기 모드 끌기: 잡은 손잡이(-1/0/1), 시작점(이미지 좌표), 시작할 때의 캔버스·그림 사각형
    private int _crHx, _crHy;
    private Avalonia.Point _crStartImg;
    private Rectangle _crStartCanvas, _crStartImage;

    // 그룹 화면(부위 배치)
    private Layer? _arrangePart, _hoverPart;
    /// <summary>그룹 화면에서 클릭해 고른 부위(파란 실선, 방향키로 옮김). 빈 곳을 누르면 풀린다.</summary>
    private Layer? _selectedPart;
    private Point _arrangeStart, _arrangeOffset;

    public PixelCanvas(EditorContext ctx)
    {
        _ctx = ctx;
        Focusable = true;
        ClipToBounds = true;
        _antsTimer.Tick += (_, _) =>
        {
            _antsPhase = (_antsPhase + 1) % 8;
            if (_ctx.Selection.HasSelection) InvalidateVisual();
        };
        _peekTimer.Tick += (_, _) =>
        {
            if (PeekKey is { } key && KeyState.IsDown(key)) return;
            _peekTimer.Stop();
            PeekPrevious = false;
        };
        _busyTimer.Tick += (_, _) => InvalidateVisual();
        ctx.RenderUpdated += (_, _) => InvalidateVisual();

        ctx.ProjectReplaced += (_, _) =>
        {
            UpdateFloatingBitmap();
            _lastStrokeEnd = null;
            _curve = null;
            // 프로젝트가 열리며 캔버스가 막 보이게 된 경우 배치가 끝난 뒤 가운데 놓는다.
            CenterImage();
            PostCenterImage();
            InvalidateVisual();
        };
        ctx.ViewChanged += (_, _) =>
        {
            if (_ctx.HasProject) SyncShownView();
            if (!_ctx.IsGroupSelected) _hoverPart = null;
            InvalidateVisual();
        };
        ctx.Selection.Changed += (_, _) => { _selectionOutline = null; UpdateFloatingBitmap(); InvalidateVisual(); };
        ctx.OnionSkinChanged += (_, _) => InvalidateVisual();
        ctx.ToolChanged += (_, _) => { CommitCurve(); UpdateCursor(); InvalidateVisual(); };
        ctx.ToolOptionsChanged += (_, _) =>
        {
            // 휘는 중에 다른 모양을 고르면 지금 곡선을 확정한다.
            if (_ctx.ShapeKind != ShapeKind.Curve) CommitCurve();
            InvalidateVisual();
        };
        // 다른 셀로 가면 휘던 곡선은 버린다(그릴 셀이 바뀜).
        ctx.CurrentChanged += (_, _) => { if (_curve is null) return; _curve = null; InvalidateVisual(); };
        ctx.TransformChanged += (_, _) =>
        {
            if (_drag == DragMode.None) UpdateCursor();
            InvalidateVisual();
        };
        ctx.CanvasResizeChanged += (_, _) =>
        {
            if (_drag == DragMode.None) UpdateCursor();
            InvalidateVisual();
        };
    }

    /// <summary>키·창 비활성 처리기를 단 창(캔버스를 다른 칸으로 옮기면 떼었다 다시 단다 — 탭 묶음 나누기).</summary>
    private TopLevel? _keyTop;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _antsTimer.Start();
        // Alt를 누르고 떼면(캔버스에 포커스가 없어도) 커서를 스포이드로 바꾸거나 되돌린다. Ctrl(⌘)은 누르는 동안 임시 이동 도구.
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            _keyTop = top;
            top.AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            top.AddHandler(KeyUpEvent, OnWindowKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (top is Window window) window.Deactivated += OnWindowDeactivated;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => EndTempMove();

    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt && IsPointerOver)
            Dispatcher.UIThread.Post(UpdateCursor); // KeyState가 먼저 갱신된 뒤에
        // Shift+클릭 직선 미리보기·곡선의 원호(Shift)를 켜고 끈다.
        if (e.Key is Key.LeftShift or Key.RightShift && IsPointerOver && _drag == DragMode.None && (_lastStrokeEnd is not null || _curve is not null))
            Dispatcher.UIThread.Post(InvalidateVisual);
        if (IsCommandKey(e.Key))
        {
            if (e.RoutedEvent == KeyDownEvent) { if (IsPointerOver) BeginTempMove(); }
            else EndTempMove();
        }
    }

    // ───────────── 임시 이동 도구(Ctrl/⌘를 누르는 동안) ─────────────

    /// <summary>Ctrl(⌘)을 누르기 전 도구(임시 이동 중이 아니면 null).</summary>
    private ToolKind? _tempMoveFrom;
    /// <summary>끄는 중에 Ctrl(⌘)을 뗐으면 끌기가 끝난 뒤 되돌린다.</summary>
    private bool _tempMoveEndPending;

    private static bool IsCommandKey(Key key)
        => Platform.IsMac ? key is Key.LWin or Key.RWin : key is Key.LeftCtrl or Key.RightCtrl;

    /// <summary>
    /// Ctrl(⌘)을 누르는 동안 이동 도구로 바꾼다(도구 막대의 이동 단추도 눌린 모습, 커서 = 이동). 떼면 원래 도구로.
    /// 그룹 화면·변형 중·캔버스 크기 모드(Ctrl = 그림 크기도 함께)·글자 입력 중·끄는 중이거나 이미 이동 도구면 바꾸지 않는다.
    /// </summary>
    private void BeginTempMove()
    {
        if (_tempMoveFrom is not null || !_ctx.HasProject || _drag != DragMode.None || _ctx.IsGroupSelected || _ctx.IsTransforming || _ctx.IsCanvasResizing
            || _ctx.Tool == ToolKind.Move || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;
        _tempMoveFrom = _ctx.Tool;
        _ctx.Tool = ToolKind.Move;
    }

    private void EndTempMove()
    {
        if (_tempMoveFrom is not { } previous) return;
        if (_drag != DragMode.None) { _tempMoveEndPending = true; return; }
        _tempMoveFrom = null;
        _tempMoveEndPending = false;
        // 그동안 다른 도구를 골랐거나 변형·캔버스 크기 모드면(도구를 바꾸면 적용돼 버림) 그대로 둔다.
        if (_ctx.Tool == ToolKind.Move && !_ctx.IsTransforming && !_ctx.IsCanvasResizing) _ctx.Tool = previous;
    }

    /// <summary>
    /// Ctrl(⌘)을 누른 채 다른 키를 누름(⌘T·⌘V 같은 단축키): 단축키가 실행되기 전에 임시 이동 도구를 원래 도구로 되돌린다.
    /// 끄는 중이면 그대로 둔다. MainForm의 단축키 처리 맨 앞에서 부른다.
    /// </summary>
    public void EndTempMoveForShortcut(Key key)
    {
        if (_tempMoveFrom is null || _drag != DragMode.None
            || key is Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
            return;
        EndTempMove();
    }

    /// <summary>스포이드 도구 아이콘을 2배로 키우고 어두운 테두리를 두른 커서(끝이 집는 점).</summary>
    private static Cursor BuildPickerCursor()
    {
        PixelImage icon;
        using (var s = AssetLoader.Open(new Uri("avares://Gpixel.App/Assets/Tools/picker.png")))
            icon = Gpixel.Core.IO.PngIO.Load(s);
        const int scale = 2, size = 16 * scale + 2;
        var img = new PixelImage(size, size);
        bool Lit(int x, int y) => icon.Get(x, y).A > 100;
        var outline = new Color32(20, 20, 24, 255);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx = (x - 1) / scale, sy = (y - 1) / scale;
                if (x >= 1 && y >= 1 && Lit(sx, sy)) { img.Set(x, y, new Color32(255, 255, 255, 255)); continue; }
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 1 && ny >= 1 && Lit((nx - 1) / scale, (ny - 1) / scale)) { img.Set(x, y, outline); goto next; }
                    }
                next:;
            }
        // 집는 점 = 아이콘의 가장 아래 왼쪽 픽셀
        var tip = new PixelPoint(0, size - 1);
        for (int y = 15; y >= 0 && tip.X == 0; y--)
            for (int x = 0; x < 16; x++)
                if (Lit(x, y)) { tip = new PixelPoint(1 + x * scale, 1 + y * scale + scale - 1); break; }
        return new Cursor(ImageUtil.ToBitmap(img), tip);
    }

    /// <summary>
    /// 변형 모드의 회전 커서(Avalonia에 없어서 만든다): 위가 트인 둥근 화살표(양 끝 화살촉)를 흰색으로, 어두운 테두리를 두른다.
    /// 가리키는 점 = 가운데.
    /// </summary>
    private static Cursor BuildRotateCursor()
    {
        const int size = 25;
        const double c = 12, inner = 5.4, outer = 7.6, gapFrom = -125, gapTo = -55;
        var lit = new bool[size * size];
        // 화살촉: 호 끝(각 φ)에서 호를 따라 바깥(트인 쪽)으로 향한 삼각형
        var heads = new List<(double X, double Y)[]>();
        foreach (var (deg, dir) in new[] { (gapTo, -1.0), (gapFrom + 360, 1.0) })
        {
            double phi = deg * Math.PI / 180, r = (inner + outer) / 2;
            double ex = c + r * Math.Cos(phi), ey = c + r * Math.Sin(phi);
            double tx = -Math.Sin(phi) * dir, ty = Math.Cos(phi) * dir;    // 호를 따라 바깥으로
            double nx = Math.Cos(phi), ny = Math.Sin(phi);                  // 지름 방향
            heads.Add([(ex + tx * 4.5, ey + ty * 4.5), (ex + nx * 4.2, ey + ny * 4.2), (ex - nx * 4.2, ey - ny * 4.2)]);
        }
        static bool InTriangle((double X, double Y)[] t, double x, double y)
        {
            double Cross((double X, double Y) a, (double X, double Y) b) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            double d1 = Cross(t[0], t[1]), d2 = Cross(t[1], t[2]), d3 = Cross(t[2], t[0]);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double px = x + 0.5 - c, py = y + 0.5 - c;
                double d = Math.Sqrt(px * px + py * py), a = Math.Atan2(py, px) * 180 / Math.PI;
                bool ring = d >= inner && d <= outer && !(a > gapFrom && a < gapTo);
                lit[y * size + x] = ring || heads.Any(h => InTriangle(h, x + 0.5, y + 0.5));
            }
        var img = new PixelImage(size, size);
        var white = new Color32(255, 255, 255, 255);
        var outline = new Color32(20, 20, 24, 255);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (lit[y * size + x]) { img.Set(x, y, white); continue; }
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < size && ny < size && lit[ny * size + nx]) { img.Set(x, y, outline); goto next; }
                    }
                next:;
            }
        // 가운데 점(가리키는 곳)
        img.Set((int)c, (int)c, white);
        foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }) img.Set((int)c + dx, (int)c + dy, outline);
        return new Cursor(ImageUtil.ToBitmap(img), new PixelPoint((int)c, (int)c));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _antsTimer.Stop();
        _peekTimer.Stop();
        if (_keyTop is { } top)
        {
            _keyTop = null;
            top.RemoveHandler(KeyDownEvent, OnWindowKey);
            top.RemoveHandler(KeyUpEvent, OnWindowKey);
            if (top is Window window) window.Deactivated -= OnWindowDeactivated;
        }
    }

    // ───────────── 보기 옵션 ─────────────

    private bool _showPixelGrid = true, _showPivot = true, _showTileGrid, _tiledMode, _peekPrevious;
    private Size _shownFrameSize;
    private Point _shownOrigin;
    private Size _tileSize = new(16, 16);
    private Color _pixelGridColor = Color.FromArgb(64, 255, 255, 255), _tileGridColor = Color.FromArgb(150, 90, 200, 255);

    public bool ShowPixelGrid { get => _showPixelGrid; set { _showPixelGrid = value; InvalidateVisual(); } }
    public bool ShowPivot { get => _showPivot; set { _showPivot = value; InvalidateVisual(); } }
    public bool ShowTileGrid { get => _showTileGrid; set { _showTileGrid = value; InvalidateVisual(); } }
    /// <summary>프로젝트를 열 때마다 화면에 맞춘다(맞추기 토글).</summary>
    public bool AutoFit { get; set; } = true;
    /// <summary>타일 모드: 그림을 반복해 보여 주고(크지 않으면 5×5, 한 변이 <see cref="LargeTileSize"/>px 넘으면 3×3), 가장자리를 넘는 그리기는 반대편으로 이어진다.</summary>
    public bool TiledMode { get => _tiledMode; set { _tiledMode = value; InvalidateVisual(); } }
    public Size TileSize { get => _tileSize; set { _tileSize = new Size(Math.Max(1, value.Width), Math.Max(1, value.Height)); InvalidateVisual(); } }
    public Color PixelGridColor { get => _pixelGridColor; set { _pixelGridColor = value; InvalidateVisual(); } }
    public Color TileGridColor { get => _tileGridColor; set { _tileGridColor = value; InvalidateVisual(); } }
    private int _pixelGridMinZoom = 6, _tileRepeat;
    private bool _tileDim = true;
    /// <summary>픽셀 격자를 이 배율 이상에서만 그린다(2~16).</summary>
    public int PixelGridMinZoom { get => _pixelGridMinZoom; set { _pixelGridMinZoom = Math.Clamp(value, 2, 16); InvalidateVisual(); } }
    /// <summary>타일 모드 반복 겹 수: 0 = 자동(큰 그림 3×3, 그 밖 5×5), 1 = 3×3, 2 = 5×5, 3 = 7×7.</summary>
    public int TileRepeat { get => _tileRepeat; set { _tileRepeat = Math.Clamp(value, 0, 3); InvalidateVisual(); } }
    /// <summary>타일 모드에서 가운데(편집하는 칸) 둘레를 어둡게.</summary>
    public bool TileDim { get => _tileDim; set { _tileDim = value; InvalidateVisual(); } }
    private int _onionBefore = 1, _onionAfter = 1, _onionOpacity = 30;
    private bool _onionTint = true;
    /// <summary>양파 껍질: 앞 프레임 수(0~5).</summary>
    public int OnionBefore { get => _onionBefore; set { _onionBefore = Math.Clamp(value, 0, 5); InvalidateVisual(); } }
    /// <summary>양파 껍질: 뒤 프레임 수(0~5).</summary>
    public int OnionAfter { get => _onionAfter; set { _onionAfter = Math.Clamp(value, 0, 5); InvalidateVisual(); } }
    /// <summary>양파 껍질 불투명도(가장 가까운 프레임, %). 멀어질수록 옅어진다.</summary>
    public int OnionOpacity { get => _onionOpacity; set { _onionOpacity = Math.Clamp(value, 5, 90); InvalidateVisual(); } }
    /// <summary>양파 껍질 색 물들이기(앞 = 빨강, 뒤 = 파랑). 끄면 원래 색.</summary>
    public bool OnionTint { get => _onionTint; set { _onionTint = value; InvalidateVisual(); } }

    /// <summary>"이전 프레임 보기"에 쓰는 키(누르고 있는 동안 보임, 사용자 단축키). 없으면 null.</summary>
    public Key? PeekKey { get; set; } = Key.H;

    /// <summary>H를 누르고 있는 동안 이전 프레임을 보여 준다.</summary>
    public bool PeekPrevious
    {
        get => _peekPrevious;
        set
        {
            if (_peekPrevious == value) return;
            _peekPrevious = value;
            if (value) _peekTimer.Start();
            InvalidateVisual();
        }
    }

    public int Zoom => _zoom;

    /// <summary>확대율이 바뀜.</summary>
    public event EventHandler? ZoomChanged;
    /// <summary>커서 아래 픽셀 좌표가 바뀜(그림 밖이면 null).</summary>
    public event EventHandler<Point?>? HoverChanged;
    /// <summary>상태 표시줄에 보일 짧은 알림.</summary>
    public event EventHandler<string>? Message;

    private int W => _ctx.CanvasWidth;
    private int H => _ctx.CanvasHeight;
    private int ClientWidth => (int)Bounds.Width;
    private int ClientHeight => (int)Bounds.Height;

    // ───────────── 좌표·확대 ─────────────

    private Rectangle ImageScreenRect => new(_offset.X, _offset.Y, W * _zoom, H * _zoom);

    private static Point ToInt(Avalonia.Point p) => new((int)Math.Floor(p.X), (int)Math.Floor(p.Y));

    private static Rect ToRect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    private Point ScreenToPixel(Point p) => new(FloorDiv(p.X - _offset.X, _zoom), FloorDiv(p.Y - _offset.Y, _zoom));

    private Avalonia.Point ScreenToImageF(Point p) => new((p.X - _offset.X) / (double)_zoom, (p.Y - _offset.Y) / (double)_zoom);

    private Avalonia.Point ImageToScreen(double x, double y) => new(_offset.X + x * _zoom, _offset.Y + y * _zoom);

    private Rectangle ImageToScreen(Rectangle r) => new(_offset.X + r.X * _zoom, _offset.Y + r.Y * _zoom, r.Width * _zoom, r.Height * _zoom);

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    /// <summary>타일 모드면 그림 밖 좌표를 안으로 감는다.</summary>
    /// <summary>이보다 큰(한 변이라도) 그림은 타일 모드에서 3×3, 아니면 5×5로 반복한다.</summary>
    private const int LargeTileSize = 128;

    /// <summary>타일 모드에서 가운데 칸 둘레로 몇 겹 반복하는지(꺼짐 0, 큰 그림 1 = 3×3, 그 밖 2 = 5×5).</summary>
    private int TileRadius => !_tiledMode ? 0 : _tileRepeat > 0 ? _tileRepeat : W > LargeTileSize || H > LargeTileSize ? 1 : 2;

    private Point Wrap(Point p) => _tiledMode && W > 0 && H > 0 ? new Point(PixelOps.Mod(p.X, W), PixelOps.Mod(p.Y, H)) : p;

    public void SetZoom(int zoom, Point? anchor = null)
    {
        zoom = Math.Clamp(zoom, 1, 64);
        if (zoom == _zoom) return;
        var a = anchor ?? new Point(ClientWidth / 2, ClientHeight / 2);
        double ix = (a.X - _offset.X) / (double)_zoom, iy = (a.Y - _offset.Y) / (double)_zoom;
        _zoom = zoom;
        _offset = new Point((int)Math.Round(a.X - ix * zoom), (int)Math.Round(a.Y - iy * zoom));
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void ZoomStep(int direction, Point? anchor = null)
    {
        int next = direction > 0
            ? ZoomLevels.FirstOrDefault(z => z > _zoom, 64)
            : ZoomLevels.LastOrDefault(z => z < _zoom, 1);
        SetZoom(next, anchor);
    }

    /// <summary>화면에 맞추기(Ctrl+0).</summary>
    public void FitToWindow()
    {
        if (!_ctx.HasProject) return;
        int n = TileRadius * 2 + 1;
        int fit = (int)Math.Floor(Math.Min((ClientWidth - 24) / (double)(W * n), (ClientHeight - 24) / (double)(H * n)));
        _zoom = Math.Clamp(fit, 1, 64);
        CenterOffset();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>프로젝트를 연 직후: <see cref="AutoFit"/>이면 화면에 맞추고, 아니면 현재 확대율로 가운데 놓되 화면보다 크면 맞춘다.</summary>
    private void CenterImage()
    {
        if (!_ctx.HasProject) return;
        _shownFrameSize = _ctx.Project!.Size;
        _shownOrigin = ShownOrigin;
        _pendingView = null;
        if (ClientWidth <= 0 || ClientHeight <= 0)
        {
            // 아직 배치 전: 크기가 정해지면 가운데 놓는다.
            _pendingCenter = true;
            return;
        }
        _pendingCenter = false;
        if (AutoFit || W * _zoom > ClientWidth - 24 || H * _zoom > ClientHeight - 24)
        {
            FitToWindow();
            return;
        }
        CenterOffset();
        InvalidateVisual();
    }

    private void CenterOffset()
        => _offset = new Point((ClientWidth - W * _zoom) / 2, (ClientHeight - H * _zoom) / 2);

    /// <summary>캔버스 (0,0)이 프레임에서 놓인 곳: 부위 화면이면 부위 위치, 아니면 (0,0).</summary>
    private Point ShownOrigin => _ctx.CurrentPart?.PartOffset ?? Point.Empty;

    /// <summary>
    /// 보이는 대상이 바뀜: 프레임 크기가 바뀌면 가운데 다시 놓고, 부위 ↔ 프레임(또는 부위 위치)이 바뀌면
    /// 프레임 그림이 화면에서 그 자리에 머물도록 옮긴다.
    /// </summary>
    private void SyncShownView()
    {
        if (_ctx.Project!.Size != _shownFrameSize)
        {
            CenterImage();
            return;
        }
        var origin = ShownOrigin;
        if (origin == _shownOrigin) return;
        _offset.Offset((origin.X - _shownOrigin.X) * _zoom, (origin.Y - _shownOrigin.Y) * _zoom);
        _shownOrigin = origin;
    }

    /// <summary>배치가 끝난 뒤 가운데 놓는다(그사이 <see cref="RestoreView"/>로 보기를 되돌렸으면 건너뜀).</summary>
    private void PostCenterImage()
    {
        int version = ++_centerVersion;
        _centerQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _centerVersion) return;
            _centerQueued = false;
            CenterImage();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>가운데 놓기를 미뤄 둔 중(<see cref="PostCenterImage"/>) — 그동안의 보기 상태는 "전체가 보임"으로 본다.</summary>
    private bool _centerQueued;

    /// <summary>
    /// 지금 보기 상태(화면 크기와 무관 — 탭에 기억했다가 <see cref="RestoreView"/>로 되돌림): 확대율, 화면 가운데가 가리키는 그림 좌표, 그림 전체가 보이는지.
    /// 아직 가운데 놓기·보기 되돌리기를 미뤄 둔 중이면 그 결과(가운데 놓기 = 전체가 보임, 되돌릴 보기 = 그 보기)를 돌려준다.
    /// </summary>
    public CanvasView View
        => _pendingView ?? (_pendingCenter || _centerQueued ? new CanvasView(_zoom, W / 2.0, H / 2.0, true) : ViewFor(ClientWidth, ClientHeight));

    private CanvasView ViewFor(double width, double height)
    {
        var r = ImageScreenRect;
        bool whole = width > 0 && height > 0 && r.X >= 0 && r.Y >= 0 && r.Right <= width && r.Bottom <= height;
        return new CanvasView(_zoom, (width / 2 - _offset.X) / _zoom, (height / 2 - _offset.Y) / _zoom, whole);
    }

    /// <summary>마우스로 끄는 중인지(그리기·선택·이동·화면 이동 등 — 자동 저장은 끝날 때까지 미룬다).</summary>
    public bool IsDragging => _drag != DragMode.None;

    /// <summary>아직 크기가 정해지지 않아 미뤄 둔 보기 상태(크기가 정해지면 <see cref="ApplyView"/>).</summary>
    private CanvasView? _pendingView;

    /// <summary>
    /// 보기 상태를 되돌린다(프로젝트를 바꾸고 현재 레이어·프레임을 정한 뒤 부른다). 미뤄 둔 가운데 놓기는 취소한다.
    /// 화면 크기가 달라도 맞게 놓는다(<see cref="ApplyView"/>).
    /// </summary>
    public void RestoreView(CanvasView view)
    {
        if (!_ctx.HasProject) return;
        _centerVersion++;
        _pendingCenter = false;
        _centerQueued = false;
        _shownFrameSize = _ctx.Project!.Size;
        _shownOrigin = ShownOrigin;
        ApplyView(view);
    }

    /// <summary>
    /// 보기 상태를 지금 화면 크기에 맞춰 놓는다: 그림 전체가 보이던 보기면 다시 화면에 맞추고(<see cref="AutoFit"/>가 꺼져 있으면 확대율을 두고
    /// 가운데 놓되 화면보다 크면 맞춤), 아니면 확대율을 두고 가리키던 그림 좌표를 화면 가운데에 둔다. 크기가 아직 없으면 미룬다.
    /// </summary>
    private void ApplyView(CanvasView view)
    {
        _zoom = Math.Clamp(view.Zoom, 1, 64);
        if (ClientWidth <= 0 || ClientHeight <= 0)
        {
            _pendingView = view;
            return;
        }
        _pendingView = null;
        if (view.WholeVisible)
        {
            if (AutoFit || W * _zoom > ClientWidth - 24 || H * _zoom > ClientHeight - 24)
            {
                FitToWindow();
                return;
            }
            CenterOffset();
        }
        else _offset = new Point((int)Math.Round(ClientWidth / 2.0 - view.CenterX * _zoom), (int)Math.Round(ClientHeight / 2.0 - view.CenterY * _zoom));
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>설정에서 읽은 확대율을 그대로 적용한다(이벤트 없음).</summary>
    public void InitZoom(int zoom) => _zoom = Math.Clamp(zoom, 1, 64);

    /// <summary>화면 위치(이 컨트롤 좌표) 아래 픽셀의 프레임 좌표(부위 화면이면 부위 위치만큼 옮김, 타일 모드면 가운데 칸으로 감음).</summary>
    public Point FramePixelAt(Avalonia.Point screen)
    {
        var px = Wrap(ScreenToPixel(ToInt(screen)));
        var origin = ShownOrigin;
        return new Point(px.X + origin.X, px.Y + origin.Y);
    }

    private IReadOnlyList<Rectangle>? _dropHint;

    /// <summary>
    /// 파일을 끌어 오는 중의 표시: 캔버스 둘레 강조 + 놓으면 그림이 들어갈 사각형(프레임 좌표, 비어 있어도 됨). null = 끔.
    /// </summary>
    public void SetDropHint(IReadOnlyList<Rectangle>? ghosts)
    {
        if (_dropHint is null && ghosts is null) return;
        _dropHint = ghosts;
        InvalidateVisual();
    }

    private void DrawDropHint(DrawingContext g)
    {
        if (_dropHint is not { } ghosts) return;
        var origin = ShownOrigin;
        foreach (var r in ghosts)
        {
            var s = ImageToScreen(new Rectangle(r.X - origin.X, r.Y - origin.Y, r.Width, r.Height));
            var rect = new Rect(s.X + 0.5, s.Y + 0.5, Math.Max(0, s.Width - 1), Math.Max(0, s.Height - 1));
            g.DrawRectangle(DropGhostBrush, LassoPreviewBackPen, rect);
            g.DrawRectangle(null, DropGhostPen, rect);
        }
        g.DrawRectangle(null, DropHighlightPen, new Rect(1, 1, Math.Max(0, Bounds.Width - 2), Math.Max(0, Bounds.Height - 2)));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _ctx.HasProject)
            PostCenterImage();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_pendingCenter) CenterImage();
        else if (_pendingView is { } pending) ApplyView(pending);
        // 화면 크기가 바뀌면(창 크기, 좌우 나누기·풀기, 다른 칸으로 옮김) 보던 그림 자리를 새 화면 가운데 기준으로 다시 놓는다.
        else if (_ctx.HasProject && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0 && _drag == DragMode.None)
            ApplyView(ViewFor(e.PreviousSize.Width, e.PreviousSize.Height));
    }

    // ───────────── 그리기 ─────────────

    private void UpdateFloatingBitmap()
    {
        _floatingBmp?.Dispose();
        _floatingBmp = _ctx.Selection.Floating is { } f ? ImageUtil.ToBitmap(f) : null;
    }

    public override void Render(DrawingContext g)
    {
        g.FillRectangle(AppTheme.BackBrush, new Rect(Bounds.Size));
        if (!_ctx.HasProject) return;
        if (_ctx.IsCanvasResizing)
        {
            DrawCanvasResize(g);
            return;
        }
        int frame = _ctx.CurrentFrame;
        var view = _ctx.DisplayView;
        Bitmap? bmp = view.Bitmap;
        bool peek = _peekPrevious && frame > 0;
        if (peek)
        {
            bmp = _ctx.ViewBitmap(frame - 1);
            view = RenderView.None;
        }
        // 그룹 화면에서 부위를 끄는 중: 그 부위를 뺀 합성 위에 옮긴 자리로 그린다.
        var dragged = _drag == DragMode.Arrange && !peek ? _arrangePart : null;
        if (dragged is not null) bmp = _ctx.ContextBitmap(frame, dragged);

        using (ImageUtil.Pixelated(g))
        {
            // 부위 화면: 부위 칸 체커 → 프레임의 나머지(다른 부위·그룹·레이어)를 흐리게 → 부위 그림. 부위 칸 안에서도 주변이 비쳐 맞춰 그릴 수 있다.
            bool partView = _ctx.CurrentPart is not null && !_tiledMode;
            if (partView)
            {
                DrawBitmapAt(g, null, 0, 0, W, H, checker: true);
                DrawPartSurroundings(g, _ctx.CurrentPart!, peek ? frame - 1 : frame);
            }
            if (_tiledMode)
            {
                int r = TileRadius;
                for (int ty = -r; ty <= r; ty++)
                    for (int tx = -r; tx <= r; tx++)
                    {
                        if (tx == 0 && ty == 0) continue;
                        DrawImageAt(g, bmp, tx * W, ty * H);
                    }
                // 가운데(편집하는 칸)가 구분되도록 둘레를 살짝 어둡게(끌 수 있음).
                if (_tileDim)
                {
                    var all = ImageToScreen(new Rectangle(-W * r, -H * r, W * (2 * r + 1), H * (2 * r + 1)));
                    var c = ImageScreenRect;
                    g.FillRectangle(DimBrush, new Rect(all.X, all.Y, all.Width, c.Y - all.Y));
                    g.FillRectangle(DimBrush, new Rect(all.X, c.Bottom, all.Width, all.Bottom - c.Bottom));
                    g.FillRectangle(DimBrush, new Rect(all.X, c.Y, c.X - all.X, c.Height));
                    g.FillRectangle(DimBrush, new Rect(c.Right, c.Y, all.Right - c.Right, c.Height));
                }
            }
            if (partView) DrawBitmapAt(g, bmp, 0, 0, W, H, checker: false);
            else DrawImageAt(g, bmp, 0, 0);
            // 계산 중(작업 스레드)이면 회색 덮개 아래 중간 결과를 보이고, 그리는 중인 셀은 효과 없이 덮개 위에 바로 보인다.
            if (view.Overlay && dragged is null) DrawPendingFrame(g);
            if (dragged is not null) DrawDraggedPart(g, dragged, frame);
            // 숨긴 일반 레이어도 그릴 수 있으므로, 현재 레이어가 숨김이면 그 셀을 반투명하게 위에 얹어 그리는 것이 보이게 한다(합성·내보내기는 그대로 숨김).
            if (!peek && !_tiledMode && _ctx.Project is { } hp && _ctx.CurrentPart is null && hp.Layers[_ctx.CurrentLayer] is { IsGroup: false } cur
                && !hp.IsLayerVisible(_ctx.CurrentLayer) && _ctx.CelBitmap(_ctx.CurrentLayer, frame) is { } hiddenCel)
                using (g.PushOpacity(0.6))
                    DrawBitmapAt(g, hiddenCel, 0, 0, W, H, checker: false);
            if (_floatingBmp is not null && !_peekPrevious)
                g.DrawImage(_floatingBmp, new Rect(0, 0, _floatingBmp.PixelSize.Width, _floatingBmp.PixelSize.Height),
                    ToRect(ImageToScreen(_ctx.Selection.Bounds)));
            if (!_peekPrevious) DrawOnionSkin(g);
        }

        var src = VisibleSourceRect(0, 0);
        if (_showPixelGrid && _zoom >= _pixelGridMinZoom) DrawPixelGrid(g, src);
        if (_showTileGrid) DrawTileGrid(g, src);
        var imgRect = ImageScreenRect;
        g.DrawRectangle(null, BorderPen, new Rect(imgRect.X - 0.5, imgRect.Y - 0.5, imgRect.Width + 1, imgRect.Height + 1));
        DrawSymmetryGuides(g);
        bool slicing = _ctx.Tool == ToolKind.Slice && !_peekPrevious;
        if (!slicing) DrawPartBorder(g);
        if (_ctx.IsGroupSelected && !_peekPrevious) DrawPartOutlines(g);
        if (slicing) DrawSliceEditor(g);
        if ((_showPivot || _ctx.Tool == ToolKind.Pivot) && _ctx.IsGroupSelected && !_peekPrevious) DrawPartPivots(g);
        if (_showPivot || _ctx.Tool == ToolKind.Pivot) DrawPivot(g);
        if (_ctx.IsTransforming) { if (!_peekPrevious) DrawTransform(g); }
        else if (_ctx.Selection.HasSelection) DrawSelection(g);
        if (_drag == DragMode.Lasso) DrawLasso(g);
        if (_drag == DragMode.Gradient) DrawGradientGuide(g);
        DrawStrokeLinePreview(g);
        DrawCurvePreview(g);
        DrawBrushCursor(g);
        DrawDropHint(g);
        if (view.Overlay) RenderOverlay.DrawBusy(g, ToRect(ImageScreenRect).Intersect(new Rect(Bounds.Size)), view.Progress);
        if (view.Overlay != _busyTimer.IsEnabled)
        {
            if (view.Overlay) _busyTimer.Start();
            else _busyTimer.Stop();
        }
        if (_peekPrevious)
            AppTheme.DrawText(g, _ctx.CurrentFrame > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_Render_28FC63DF57,_ctx.CurrentFrame) : UiStrings.S_PixelCanvas_Render_5D74D360AD,
                new Avalonia.Point(8, 8), AppTheme.WarningBrush, bold: true);
        // 캔버스 아래 안내는 그릴 수 없을 때(잠금) 경고만 — 사용법 설명은 도움말·상태 표시줄에.
        if (!_ctx.IsGroupSelected && !_ctx.IsTransforming && !_ctx.CanEdit)
            AppTheme.DrawText(g, UiStrings.S_PixelCanvas_Render_77C8A3EDEF, new Avalonia.Point(8, Bounds.Height - 24), AppTheme.WarningBrush);
    }

    /// <summary>
    /// 지금 프레임 합성을 작업 스레드에서 계산하는 중: 보이는 그림(이전 결과·중간 그림) 위에 회색 덮개를 씌우고,
    /// 스트로크 중이면 그리는 셀을 효과 없이(레이어 불투명도로) 덮개 위에 그려 바로 보이게 한다.
    /// </summary>
    private void DrawPendingFrame(DrawingContext g)
    {
        RenderOverlay.DrawVeil(g, ToRect(ImageScreenRect).Intersect(new Rect(Bounds.Size)));
        if (_ctx.Project is not { IsStrokeActive: true } p || _ctx.CurrentPart is not null || _ctx.EditImage is not { } cel
            || cel.Width != W || cel.Height != H)
            return;
        if (_strokeBmp is null || _strokeBmp.PixelSize.Width != W || _strokeBmp.PixelSize.Height != H)
        {
            _strokeBmp?.Dispose();
            _strokeBmp = ImageUtil.NewBitmap(W, H);
        }
        ImageUtil.CopyToBitmap(cel, _strokeBmp);
        using (g.PushOpacity(p.Layers[_ctx.CurrentLayer].Opacity / 255.0))
            DrawBitmapAt(g, _strokeBmp, 0, 0, W, H, checker: false);
    }

    /// <summary>캔버스 크기의 그림을 (ox,oy) 이미지 좌표만큼 옮긴 위치에 체커 배경과 함께 보이는 부분만 그린다(그림이 없으면 체커만).</summary>
    private void DrawImageAt(DrawingContext g, Bitmap? bmp, int ox, int oy) => DrawBitmapAt(g, bmp, ox, oy, W, H, checker: true);

    /// <summary><paramref name="w"/>×<paramref name="h"/> 그림을 (ox,oy) 이미지 좌표에 보이는 부분만 그린다.</summary>
    private void DrawBitmapAt(DrawingContext g, Bitmap? bmp, int ox, int oy, int w, int h, bool checker)
    {
        var src = VisibleSourceRect(ox, oy, w, h);
        if (src.Width <= 0 || src.Height <= 0) return;
        var dest = ToRect(ImageToScreen(new Rectangle(src.X + ox, src.Y + oy, src.Width, src.Height)));
        if (checker) AppTheme.DrawChecker(g, dest.Intersect(new Rect(Bounds.Size)), 8);
        if (bmp is not null) g.DrawImage(bmp, ToRect(src), dest);
    }

    private Rectangle VisibleSourceRect(int ox, int oy) => VisibleSourceRect(ox, oy, W, H);

    private Rectangle VisibleSourceRect(int ox, int oy, int w, int h)
    {
        var tl = ScreenToPixel(Point.Empty);
        var br = ScreenToPixel(new Point(ClientWidth, ClientHeight));
        return Rectangle.Intersect(Rectangle.FromLTRB(tl.X - ox, tl.Y - oy, br.X + 1 - ox, br.Y + 1 - oy), new Rectangle(0, 0, w, h));
    }

    /// <summary>부위 화면: 프레임의 나머지(이 부위를 뺀 합성)를 부위 위치만큼 옮겨 흐리게, 프레임 경계는 점선으로.</summary>
    private void DrawPartSurroundings(DrawingContext g, Layer part, int frame)
    {
        if (_ctx.Project is not { } p || _ctx.ContextBitmap(frame, part) is not { } around) return;
        int ox = -part.PartOffset.X, oy = -part.PartOffset.Y;
        using (g.PushOpacity(0.35)) DrawBitmapAt(g, around, ox, oy, p.Width, p.Height, checker: false);
        var r = ImageToScreen(new Rectangle(ox, oy, p.Width, p.Height));
        g.DrawRectangle(null, FrameBoundsPen, new Rect(r.X - 0.5, r.Y - 0.5, r.Width + 1, r.Height + 1));
    }

    /// <summary>그룹 화면에서 끄는 부위를 옮긴 자리에 그린다(그룹·부위 불투명도 반영).</summary>
    private void DrawDraggedPart(DrawingContext g, Layer part, int frame)
    {
        if (_ctx.Project is not { } p || _ctx.CelBitmap(p.IndexOf(part), frame) is not { } cel) return;
        double opacity = (part.Parent?.Opacity ?? 255) * part.Opacity / (255.0 * 255.0);
        using (g.PushOpacity(opacity))
            DrawBitmapAt(g, cel, _arrangeOffset.X, _arrangeOffset.Y, part.PartSize.Width, part.PartSize.Height, checker: false);
    }

    /// <summary>그룹 화면: 보이는 부위마다 흐린 테두리, 올리거나 끄는 부위는 밝은 테두리 + 이름·크기(끄는 중이면 위치).</summary>
    private void DrawPartOutlines(DrawingContext g)
    {
        if (_ctx.Project is not { } p || _ctx.CurrentGroup is not { } group) return;
        var selected = SelectedPartIn(p, group);
        var hover = _arrangePart ?? _hoverPart;
        foreach (var part in p.ChildrenOf(group))
        {
            if (ReferenceEquals(part, hover) || ReferenceEquals(part, selected) || !p.IsLayerVisible(p.IndexOf(part))) continue;
            var r = ImageToScreen(part.PartRect);
            g.DrawRectangle(null, PartFaintPen, new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1)));
        }
        Rect ScreenRectOf(Layer part)
        {
            var rect = part.PartRect;
            if (ReferenceEquals(part, _arrangePart)) rect.Location = _arrangeOffset;
            var sr = ImageToScreen(rect);
            return new Rect(sr.X + 0.5, sr.Y + 0.5, Math.Max(0, sr.Width - 1), Math.Max(0, sr.Height - 1));
        }
        // 고른 부위: 파란 실선(늘 보임). 마우스가 올라간 다른 부위: 파란 점선.
        if (selected is not null) g.DrawRectangle(null, PartSelectedPen, ScreenRectOf(selected));
        if (hover is not null && !ReferenceEquals(hover, selected) && ReferenceEquals(hover.Parent, group))
            g.DrawRectangle(null, PartHoverPen, ScreenRectOf(hover));
        var active = hover is not null && ReferenceEquals(hover.Parent, group) ? hover : selected;
        if (active is null) return;
        var rect = active.PartRect;
        if (ReferenceEquals(active, _arrangePart)) rect.Location = _arrangeOffset;
        var s = ImageToScreen(rect);
        var label = $"{active.Name}  {rect.Width}×{rect.Height}" + (ReferenceEquals(active, _arrangePart) || ReferenceEquals(active, selected) ? $"  ({rect.X}, {rect.Y})" : "");
        var size = AppTheme.MeasureText(label, AppTheme.SmallFontSize);
        double ly = s.Y - size.Height - 3 >= 0 ? s.Y - size.Height - 3 : s.Bottom + 2;
        g.FillRectangle(LabelBrush, new Rect(s.X, ly, size.Width + 6, size.Height + 2));
        AppTheme.DrawText(g, label, new Avalonia.Point(s.X + 3, ly + 1), Brushes.White, AppTheme.SmallFontSize);
    }

    private void DrawPixelGrid(DrawingContext g, Rectangle src)
    {
        var c = _pixelGridColor;
        var pen = AppTheme.Pen(_zoom >= 12 ? c : Color.FromArgb((byte)Math.Max(16, c.A * 3 / 4), c.R, c.G, c.B));
        for (int x = src.Left; x <= src.Right; x++)
        {
            double sx = _offset.X + x * _zoom + 0.5;
            g.DrawLine(pen, new Avalonia.Point(sx, _offset.Y + src.Top * _zoom), new Avalonia.Point(sx, _offset.Y + src.Bottom * _zoom));
        }
        for (int y = src.Top; y <= src.Bottom; y++)
        {
            double sy = _offset.Y + y * _zoom + 0.5;
            g.DrawLine(pen, new Avalonia.Point(_offset.X + src.Left * _zoom, sy), new Avalonia.Point(_offset.X + src.Right * _zoom, sy));
        }
    }

    private void DrawTileGrid(DrawingContext g, Rectangle src)
    {
        var pen = AppTheme.Pen(_tileGridColor);
        int tw = _tileSize.Width, th = _tileSize.Height;
        for (int x = src.Left / tw * tw; x <= src.Right; x += tw)
        {
            double sx = _offset.X + x * _zoom + 0.5;
            g.DrawLine(pen, new Avalonia.Point(sx, _offset.Y + src.Top * _zoom), new Avalonia.Point(sx, _offset.Y + src.Bottom * _zoom));
        }
        for (int y = src.Top / th * th; y <= src.Bottom; y += th)
        {
            double sy = _offset.Y + y * _zoom + 0.5;
            g.DrawLine(pen, new Avalonia.Point(_offset.X + src.Left * _zoom, sy), new Avalonia.Point(_offset.X + src.Right * _zoom, sy));
        }
    }

    private void DrawSymmetryGuides(DrawingContext g)
    {
        if (!_ctx.MirrorX && !_ctx.MirrorY) return;
        var r = ImageScreenRect;
        if (_ctx.MirrorX)
        {
            double x = r.X + r.Width / 2.0;
            g.DrawLine(MirrorPen, new Avalonia.Point(x, r.Top - 10), new Avalonia.Point(x, r.Bottom + 10));
        }
        if (_ctx.MirrorY)
        {
            double y = r.Y + r.Height / 2.0;
            g.DrawLine(MirrorPen, new Avalonia.Point(r.Left - 10, y), new Avalonia.Point(r.Right + 10, y));
        }
    }

    /// <summary>부위 화면: 현재 부위의 9-slice 테두리 안내선(점선).</summary>
    private void DrawPartBorder(DrawingContext g)
    {
        if (_ctx.CurrentPart is not { } part || part.PartBorder.IsNone) return;
        var r = ImageScreenRect;
        var b = part.PartBorder;
        void Line(double x1, double y1, double x2, double y2)
            => g.DrawLine(SliceBorderPen, new Avalonia.Point(x1, y1), new Avalonia.Point(x2, y2));
        if (b.Left > 0) Line(r.Left + b.Left * _zoom + 0.5, r.Top, r.Left + b.Left * _zoom + 0.5, r.Bottom);
        if (b.Right > 0) Line(r.Right - b.Right * _zoom + 0.5, r.Top, r.Right - b.Right * _zoom + 0.5, r.Bottom);
        if (b.Top > 0) Line(r.Left, r.Top + b.Top * _zoom + 0.5, r.Right, r.Top + b.Top * _zoom + 0.5);
        if (b.Bottom > 0) Line(r.Left, r.Bottom - b.Bottom * _zoom + 0.5, r.Right, r.Bottom - b.Bottom * _zoom + 0.5);
    }

    /// <summary>9-slice 대상 하나: 그룹 화면의 부위(아니면 null = 현재 대상), 이미지 좌표 사각형, 테두리.</summary>
    private readonly record struct SliceTarget(Layer? Part, Rectangle Rect, SpriteBorder Border);

    /// <summary>9-slice 도구로 보이는 대상들: 그룹 화면이면 보이는 부위 전부(끄는 부위는 옮긴 자리), 아니면 현재 대상(부위 또는 프레임) 하나.</summary>
    private IEnumerable<SliceTarget> SliceTargets()
    {
        if (_ctx.Project is not { } p) yield break;
        if (!_ctx.IsGroupSelected)
        {
            yield return new SliceTarget(null, new Rectangle(0, 0, W, H), _ctx.SliceBorder);
            yield break;
        }
        if (_ctx.CurrentGroup is not { } group) yield break;
        foreach (var part in p.ChildrenOf(group))
        {
            if (!p.IsLayerVisible(p.IndexOf(part))) continue;
            var offset = ReferenceEquals(part, _arrangePart) ? _arrangeOffset : part.PartOffset;
            yield return new SliceTarget(part, new Rectangle(offset, part.PartSize), part.PartBorder);
        }
    }

    private bool IsDraggedSlice(SliceTarget t)
        => _drag == DragMode.Slice && ReferenceEquals(t.Part, _slicePart) && t.Rect == _sliceRect;

    /// <summary>9-slice 도구: 대상마다 네 테두리 선(0이면 가장자리)을 초록 실선으로, 가운데에 끄는 손잡이를 그린다.</summary>
    private void DrawSliceEditor(DrawingContext g)
    {
        foreach (var t in SliceTargets())
        {
            bool dragged = IsDraggedSlice(t);
            var r = ImageToScreen(t.Rect);
            var b = dragged && _sliceDraft is { } d ? d : t.Border;
            double left = r.Left + b.Left * _zoom + 0.5, right = r.Right - b.Right * _zoom - 0.5;
            double top = r.Top + b.Top * _zoom + 0.5, bottom = r.Bottom - b.Bottom * _zoom - 0.5;
            IPen PenFor(SliceEdge edge) => dragged && _sliceEdge == edge ? SliceEditActivePen : SliceEditPen;
            g.DrawLine(PenFor(SliceEdge.Left), new Avalonia.Point(left, r.Top), new Avalonia.Point(left, r.Bottom));
            g.DrawLine(PenFor(SliceEdge.Right), new Avalonia.Point(right, r.Top), new Avalonia.Point(right, r.Bottom));
            g.DrawLine(PenFor(SliceEdge.Top), new Avalonia.Point(r.Left, top), new Avalonia.Point(r.Right, top));
            g.DrawLine(PenFor(SliceEdge.Bottom), new Avalonia.Point(r.Left, bottom), new Avalonia.Point(r.Right, bottom));
            double cx = (r.Left + r.Right) / 2.0, cy = (r.Top + r.Bottom) / 2.0;
            void Handle(double x, double y) => g.DrawRectangle(SliceHandleBrush, SliceHandlePen, new Rect(x - 4, y - 4, 8, 8));
            Handle(left, cy);
            Handle(right, cy);
            Handle(cx, top);
            Handle(cx, bottom);
        }
    }

    /// <summary>화면 위치에서 가장 가까운 9-slice 선(6px 안)과 그 대상. 없으면 null.</summary>
    private (SliceTarget Target, SliceEdge Edge)? SliceHitAt(Point screen)
    {
        const double Grab = 6;
        (SliceTarget Target, SliceEdge Edge)? best = null;
        double bestDistance = Grab;
        foreach (var t in SliceTargets())
        {
            var r = ImageToScreen(t.Rect);
            var b = t.Border;
            bool inX = screen.X >= r.Left - Grab && screen.X <= r.Right + Grab;
            bool inY = screen.Y >= r.Top - Grab && screen.Y <= r.Bottom + Grab;
            void Try(SliceEdge edge, bool inRange, double distance)
            {
                if (!inRange || distance > bestDistance) return;
                bestDistance = distance;
                best = (t, edge);
            }
            Try(SliceEdge.Left, inY, Math.Abs(screen.X - (r.Left + b.Left * _zoom)));
            Try(SliceEdge.Right, inY, Math.Abs(screen.X - (r.Right - b.Right * _zoom)));
            Try(SliceEdge.Top, inX, Math.Abs(screen.Y - (r.Top + b.Top * _zoom)));
            Try(SliceEdge.Bottom, inX, Math.Abs(screen.Y - (r.Bottom - b.Bottom * _zoom)));
        }
        return best;
    }

    /// <summary>9-slice 선을 잡았으면 끌기를 시작하고 true.</summary>
    private bool BeginSliceDrag(Point screen)
    {
        if (SliceHitAt(screen) is not { } hit) return false;
        _slicePart = hit.Target.Part;
        _sliceRect = hit.Target.Rect;
        _sliceEdge = hit.Edge;
        _sliceDraft = hit.Target.Border;
        _drag = DragMode.Slice;
        return true;
    }

    /// <summary>끄는 선을 마우스 위치의 가장 가까운 픽셀 경계로 옮긴다(반대쪽 선을 넘지 않음).</summary>
    private void DragSlice(Point screen)
    {
        if (_sliceDraft is not { } b) return;
        int w = _sliceRect.Width, h = _sliceRect.Height;
        int x = (int)Math.Round((screen.X - _offset.X) / (double)_zoom) - _sliceRect.X;
        int y = (int)Math.Round((screen.Y - _offset.Y) / (double)_zoom) - _sliceRect.Y;
        var next = _sliceEdge switch
        {
            SliceEdge.Left => b with { Left = Math.Clamp(x, 0, w - b.Right) },
            SliceEdge.Right => b with { Right = Math.Clamp(w - x, 0, w - b.Left) },
            SliceEdge.Top => b with { Top = Math.Clamp(y, 0, h - b.Bottom) },
            SliceEdge.Bottom => b with { Bottom = Math.Clamp(h - y, 0, h - b.Top) },
            _ => b,
        };
        if (next == b) return;
        _sliceDraft = next;
        InvalidateVisual();
    }

    /// <summary>그룹 화면: 보이는 부위마다 그 부위의 피벗(부위 아틀라스용)을 파란 표시로(끄는 부위는 옮긴 자리 기준).</summary>
    private void DrawPartPivots(DrawingContext g)
    {
        if (_ctx.Project is not { } p || _ctx.CurrentGroup is not { } group) return;
        foreach (var part in p.ChildrenOf(group))
        {
            if (!p.IsLayerVisible(p.IndexOf(part))) continue;
            var offset = ReferenceEquals(part, _arrangePart) ? _arrangeOffset : part.PartOffset;
            var (lx, ly) = part.PartPivot.ToLocalPixel(part.PartSize.Width, part.PartSize.Height);
            var c = ImageToScreen(offset.X + lx, offset.Y + ly);
            const double r = 3, arm = 3;
            g.DrawEllipse(null, PivotBackPen, c, r, r);
            g.DrawEllipse(PartPivotBrush, null, c, r - 1, r - 1);
            g.DrawLine(PartPivotPen, new Avalonia.Point(c.X - r - arm, c.Y), new Avalonia.Point(c.X - r, c.Y));
            g.DrawLine(PartPivotPen, new Avalonia.Point(c.X + r, c.Y), new Avalonia.Point(c.X + r + arm, c.Y));
            g.DrawLine(PartPivotPen, new Avalonia.Point(c.X, c.Y - r - arm), new Avalonia.Point(c.X, c.Y - r));
            g.DrawLine(PartPivotPen, new Avalonia.Point(c.X, c.Y + r), new Avalonia.Point(c.X, c.Y + r + arm));
        }
    }

    private void DrawPivot(DrawingContext g)
    {
        var (px, py) = _ctx.Pivot.ToLocalPixel(W, H);
        var p = ImageToScreen(px, py);
        var pen = _ctx.Tool == ToolKind.Pivot ? PivotActivePen : PivotPen;
        const double r = 5;
        g.DrawEllipse(null, PivotBackPen, p, r, r);
        g.DrawEllipse(null, pen, p, r, r);
        g.DrawLine(pen, new Avalonia.Point(p.X - r - 4, p.Y), new Avalonia.Point(p.X + r + 4, p.Y));
        g.DrawLine(pen, new Avalonia.Point(p.X, p.Y - r - 4), new Avalonia.Point(p.X, p.Y + r + 4));
    }

    private void DrawMarquee(DrawingContext g, Rectangle r)
    {
        var black = AntsPen();
        var rect = new Rect(r.X + 0.5, r.Y + 0.5, r.Width - 1, r.Height - 1);
        g.DrawRectangle(null, WhitePen, rect);
        g.DrawRectangle(null, black, rect);
    }

    private IPen AntsPen() => AppTheme.Pen(Colors.Black, 1, new ImmutableDashStyle([4.0, 4.0], _antsPhase));

    /// <summary>선택 영역 개미 줄: 사각형이면 테두리, 모양이면 들어간 칸과 안 들어간 칸 사이 모서리.</summary>
    private void DrawSelection(DrawingContext g)
    {
        var sel = _ctx.Selection;
        if (sel.IsRectangle)
        {
            DrawMarquee(g, ImageToScreen(sel.Bounds));
            return;
        }
        _selectionOutline ??= MaskOutline(sel.Region);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
            foreach (var (a, b) in _selectionOutline)
            {
                c.BeginFigure(OutlinePoint(a), false);
                c.LineTo(OutlinePoint(b));
                c.EndFigure(false);
            }
        g.DrawGeometry(null, WhitePen, geo);
        g.DrawGeometry(null, AntsPen(), geo);

        // 픽셀 모서리를 화면 픽셀 가운데로(1px 선이 번지지 않게).
        Avalonia.Point OutlinePoint(Point p) => new(_offset.X + p.X * _zoom + 0.5, _offset.Y + p.Y * _zoom + 0.5);
    }

    /// <summary>영역 테두리: 안팎이 갈리는 픽셀 모서리를 가로·세로로 이어 붙인 선분들(이미지 좌표).</summary>
    private static List<(Point A, Point B)> MaskOutline(PixelMask m)
    {
        var segs = new List<(Point, Point)>();
        var r = m.Bounds;
        for (int y = r.Top; y <= r.Bottom; y++)
        {
            int start = int.MinValue;
            for (int x = r.Left; x <= r.Right; x++)
            {
                bool edge = x < r.Right && m.Contains(x, y - 1) != m.Contains(x, y);
                if (edge && start == int.MinValue) start = x;
                else if (!edge && start != int.MinValue)
                {
                    segs.Add((new Point(start, y), new Point(x, y)));
                    start = int.MinValue;
                }
            }
        }
        for (int x = r.Left; x <= r.Right; x++)
        {
            int start = int.MinValue;
            for (int y = r.Top; y <= r.Bottom; y++)
            {
                bool edge = y < r.Bottom && m.Contains(x - 1, y) != m.Contains(x, y);
                if (edge && start == int.MinValue) start = y;
                else if (!edge && start != int.MinValue)
                {
                    segs.Add((new Point(x, start), new Point(x, y)));
                    start = int.MinValue;
                }
            }
        }
        return segs;
    }

    /// <summary>
    /// 자유 선택으로 끄는 중: 지금 놓으면 실제로 선택될 픽셀 영역(시작점으로 닫은 다각형, <see cref="PixelMask.Polygon"/>)을
    /// 파란 점선으로, 그 위에 끈 자취(픽셀 중심을 이은 개미 줄)를 보인다.
    /// </summary>
    private void DrawLasso(DrawingContext g)
    {
        if (_lassoPath.Count < 2) return;
        _lassoPreview ??= MaskOutline(PixelMask.Polygon(_lassoPath));
        var area = new StreamGeometry();
        using (var c = area.Open())
            foreach (var (a, b) in _lassoPreview)
            {
                c.BeginFigure(new Avalonia.Point(_offset.X + a.X * _zoom + 0.5, _offset.Y + a.Y * _zoom + 0.5), false);
                c.LineTo(new Avalonia.Point(_offset.X + b.X * _zoom + 0.5, _offset.Y + b.Y * _zoom + 0.5));
                c.EndFigure(false);
            }
        g.DrawGeometry(null, LassoPreviewBackPen, area);
        g.DrawGeometry(null, LassoPreviewPen, area);

        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(ImageToScreen(_lassoPath[0].X + 0.5, _lassoPath[0].Y + 0.5), false);
            for (int i = 1; i < _lassoPath.Count; i++) c.LineTo(ImageToScreen(_lassoPath[i].X + 0.5, _lassoPath[i].Y + 0.5));
            c.EndFigure(true);
        }
        g.DrawGeometry(null, WhitePen, geo);
        g.DrawGeometry(null, AntsPen(), geo);
    }

    private static bool UsesBrush(ToolKind t)
        => t is ToolKind.Pencil or ToolKind.Eraser or ToolKind.Shape
            or ToolKind.Dodge or ToolKind.Dither or ToolKind.ReplaceBrush;

    /// <summary>Shift+클릭이 마지막 점에서 직선을 긋는 브러시 도구(연필·지우개·밝게/어둡게·디더링·색 바꾸기).</summary>
    private static bool IsStrokeTool(ToolKind t)
        => t is ToolKind.Pencil or ToolKind.Eraser or ToolKind.Dodge or ToolKind.Dither or ToolKind.ReplaceBrush;

    /// <summary>
    /// 브러시 도구에서 Shift를 누르고 있으면 클릭했을 때 그어질 직선(마지막 획 끝 → 포인터, 브러시 두께·대칭 포함)을
    /// 반투명하게 미리 보인다. 연필은 기본색, 그 밖의 도구는 흰색.
    /// </summary>
    private void DrawStrokeLinePreview(DrawingContext g)
    {
        if (_drag != DragMode.None || _hoverPx is null || _lastStrokeEnd is not { } from || !IsStrokeTool(_ctx.Tool)
            || !(KeyState.IsDown(Key.LeftShift) || KeyState.IsDown(Key.RightShift))
            || _ctx.IsGroupSelected || _ctx.IsTransforming || _ctx.IsCanvasResizing || !_ctx.CanEdit) return;
        var to = ScreenToPixel(_mouseScreen);
        var cells = new HashSet<Point>();
        foreach (var p in Raster.Line(from, to))
            cells.UnionWith(Raster.Brush(p.X, p.Y, _ctx.BrushSize, _ctx.BrushShape));
        var c = _ctx.Tool == ToolKind.Pencil && _ctx.Primary.A != 0 ? _ctx.Primary : Color32.White;
        FillCells(g, Targets(cells), new SolidColorBrush(Color.FromArgb(150, c.R, c.G, c.B)));
    }

    private static bool ShiftDown => KeyState.IsDown(Key.LeftShift) || KeyState.IsDown(Key.RightShift);

    /// <summary>휘는 중인 곡선의 점(Shift = 원호, 선 잇기 설정 따름).</summary>
    private List<Point> CurvePoints((Point A, Point B, MouseButton Button) curve)
        => Shapes.Curve(curve.A, curve.B, _curveThrough, arc: ShiftDown, orthogonal: _ctx.ShapeOrthogonal);

    /// <summary>휘는 중인 곡선을 칠할 색으로 미리 보인다(지우는 색이면 반투명 흰색). 브러시 두께·대칭·선택 영역 포함.</summary>
    private void DrawCurvePreview(DrawingContext g)
    {
        if (_curve is not { } curve) return;
        var c = curve.Button == MouseButton.Right ? _ctx.RightButtonErases ? Color32.Transparent : _ctx.Secondary : _ctx.Primary;
        var brush = new SolidColorBrush(c.A == 0 ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(c.A, c.R, c.G, c.B));
        var clip = _ctx.Selection.Clip;
        FillCells(g, Targets(WithBrush(CurvePoints(curve))).Where(m => clip is null || clip.Value.Contains(m)), brush);
    }

    /// <summary>캔버스 안의 픽셀 칸들을 한 색으로 칠한다(미리보기).</summary>
    private void FillCells(DrawingContext g, IEnumerable<Point> cells, IBrush brush)
    {
        foreach (var m in cells)
        {
            if (m.X < 0 || m.Y < 0 || m.X >= W || m.Y >= H) continue;
            var r = ImageToScreen(new Rectangle(m.X, m.Y, 1, 1));
            g.FillRectangle(brush, new Rect(r.X, r.Y, r.Width, r.Height));
        }
    }

    private void DrawBrushCursor(DrawingContext g)
    {
        if (_hoverPx is not { } p || _drag == DragMode.Pan || !UsesBrush(_ctx.Tool) || _ctx.IsGroupSelected || _ctx.IsTransforming) return;
        var brush = _ctx.Brush;
        int size = brush.Size, start = brush.Start;
        if (brush.Shape == BrushShape.Square || size <= 2)
        {
            var r = ImageToScreen(new Rectangle(p.X - start, p.Y - start, size, size));
            g.DrawRectangle(null, CursorBlackPen, new Rect(r.X - 0.5, r.Y - 0.5, r.Width + 1, r.Height + 1));
            g.DrawRectangle(null, CursorWhitePen, new Rect(r.X + 0.5, r.Y + 0.5, r.Width - 1, r.Height - 1));
            return;
        }
        // 원·마름모: 도장 바깥 경계선(칸 모서리를 따라)을 검은 테두리 위에 흰 선으로.
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            void Seg(double x0, double y0, double x1, double y1)
            {
                c.BeginFigure(new Avalonia.Point(x0, y0), false);
                c.LineTo(new Avalonia.Point(x1, y1));
                c.EndFigure(false);
            }
            foreach (var o in brush.Offsets)
            {
                double x0 = _offset.X + (p.X + o.X) * _zoom + 0.5, y0 = _offset.Y + (p.Y + o.Y) * _zoom + 0.5;
                double x1 = x0 + _zoom, y1 = y0 + _zoom;
                if (!brush.Contains(o.X, o.Y - 1)) Seg(x0, y0, x1, y0);
                if (!brush.Contains(o.X, o.Y + 1)) Seg(x0, y1, x1, y1);
                if (!brush.Contains(o.X - 1, o.Y)) Seg(x0, y0, x0, y1);
                if (!brush.Contains(o.X + 1, o.Y)) Seg(x1, y0, x1, y1);
            }
        }
        g.DrawGeometry(null, CursorOutlineBackPen, geo);
        g.DrawGeometry(null, CursorOutlineFrontPen, geo);
    }

    private void DrawOnionSkin(DrawingContext g)
    {
        if (!_ctx.OnionSkin) return;
        int i = _ctx.CurrentFrame;
        // 먼 프레임부터 그려 가까운 프레임이 위에 오게. 멀어질수록 옅게(가장 먼 프레임 = 가까운 것의 약 40%).
        void Ghost(int d, int count)
        {
            int f = i + d;
            if (f < 0 || f >= _ctx.FrameCount || _ctx.ViewBitmap(f) is not { } bmp) return;
            int k = Math.Abs(d);
            float alpha = _onionOpacity / 100f * (count <= 1 ? 1f : 1f - 0.6f * (k - 1) / (count - 1));
            var (r, gr, b) = !_onionTint ? (1f, 1f, 1f) : d < 0 ? (1f, 0.45f, 0.45f) : (0.45f, 0.75f, 1f);
            DrawGhost(g, bmp, d, r, gr, b, alpha);
        }
        for (int d = _onionBefore; d >= 1; d--) Ghost(-d, _onionBefore);
        for (int d = _onionAfter; d >= 1; d--) Ghost(d, _onionAfter);
    }

    /// <summary>색을 물들이고 <paramref name="alpha"/> 불투명도로 만든 사본을 그린다(사본 비트맵은 프레임 거리마다 재사용).</summary>
    private void DrawGhost(DrawingContext g, WriteableBitmap bmp, int key, float r, float gr, float b, float alpha)
    {
        var src = VisibleSourceRect(0, 0);
        if (src.IsEmpty) return;
        if (!_ghosts.TryGetValue(key, out var ghost) || ghost.PixelSize != bmp.PixelSize)
        {
            ghost?.Dispose();
            ghost = ImageUtil.NewBitmap(bmp.PixelSize.Width, bmp.PixelSize.Height);
            _ghosts[key] = ghost;
        }
        Tint(bmp, ghost, r, gr, b, alpha);
        g.DrawImage(ghost, ToRect(src), ToRect(ImageToScreen(src)));
    }

    private static unsafe void Tint(WriteableBitmap source, WriteableBitmap dest, float r, float gr, float b, float alpha)
    {
        using var s = source.Lock();
        using var d = dest.Lock();
        int w = s.Size.Width, h = s.Size.Height;
        for (int y = 0; y < h; y++)
        {
            byte* sp = (byte*)s.Address + y * s.RowBytes;
            byte* dp = (byte*)d.Address + y * d.RowBytes;
            for (int x = 0; x < w; x++, sp += 4, dp += 4)
            {
                // BGRA(직선 알파)
                dp[0] = (byte)(sp[0] * b);
                dp[1] = (byte)(sp[1] * gr);
                dp[2] = (byte)(sp[2] * r);
                dp[3] = (byte)(sp[3] * alpha);
            }
        }
    }

    // ───────────── 입력 ─────────────

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // Alt를 눌러 임시 스포이드를 쓴 뒤 Alt를 떼면 메뉴가 활성화되지 않도록 삼킨다.
        if (e.Key is Key.LeftAlt or Key.RightAlt && _altUsed)
        {
            _altUsed = false;
            e.Handled = true;
        }
    }

    private double _wheelAccum;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!_ctx.HasProject || e.Delta.Y == 0) return;
        e.Handled = true;
        // 트랙패드(macOS)는 작은 값을 잇달아 보내므로 한 눈금(1.0)이 모일 때마다 한 단계씩 확대한다.
        if (Math.Sign(_wheelAccum) != Math.Sign(e.Delta.Y)) _wheelAccum = 0;
        _wheelAccum += e.Delta.Y;
        if (Math.Abs(_wheelAccum) < 1) return;
        int step = Math.Sign(_wheelAccum);
        _wheelAccum -= step;
        // Ctrl/⌘+휠 = 브러시(펜) 크기, 그냥 휠 = 확대·축소.
        if (Ui.HasCommand(e.KeyModifiers))
        {
            _ctx.BrushSize += step;
            Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_OnPointerWheelChanged_D2CFBB82A7,_ctx.BrushSize));
            InvalidateVisual();
            return;
        }
        ZoomStep(step, ToInt(e.GetPosition(this)));
    }

    private static bool IsPaintTool(ToolKind t)
        => t is ToolKind.Pencil or ToolKind.Eraser or ToolKind.Fill or ToolKind.Gradient or ToolKind.Shape
            or ToolKind.Dodge or ToolKind.Dither or ToolKind.ReplaceBrush;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!_ctx.HasProject) return;
        var pt = e.GetCurrentPoint(this);
        if (_drag != DragMode.None)
        {
            if (TryCancelByOtherButton(pt, e.Pointer)) e.Handled = true;
            return;
        }
        // 곡선을 휘는 중: 처음 버튼 = 확정, 반대 버튼 = 취소(가운데 버튼·Space 끌기는 화면 이동).
        if (_curve is { } curve && pt.Properties.PointerUpdateKind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed
            && !(pt.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed && KeyState.IsDown(Key.Space)))
        {
            bool same = (curve.Button == MouseButton.Left) == (pt.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed);
            if (same) CommitCurve();
            else CancelCurve();
            e.Handled = true;
            return;
        }
        var button = pt.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            _ => MouseButton.None,
        };
        var loc = ToInt(pt.Position);
        _mods = e.KeyModifiers;
        _dragButton = button;
        _dragStartScreen = _lastScreen = _mouseScreen = loc;
        _dragMoved = false;
        var px = ScreenToPixel(loc);
        _startPx = _lastPx = px;
        e.Handled = true;

        if (button == MouseButton.Middle || (button == MouseButton.Left && KeyState.IsDown(Key.Space)))
        {
            _drag = DragMode.Pan;
            Cursor = SizeAllCursor;
            e.Pointer.Capture(this);
            return;
        }
        if (button is not (MouseButton.Left or MouseButton.Right)) return;
        if (_ctx.IsCanvasResizing)
        {
            // 캔버스 크기 모드: 캔버스 손잡이·그림 안쪽만 끈다(그리기는 막음).
            if (button == MouseButton.Left && BeginCanvasResizeDrag(loc)) e.Pointer.Capture(this);
            return;
        }
        if (_ctx.IsTransforming)
        {
            // 변형 모드: 손잡이·안쪽·바깥 띠만 끈다(도구는 쓰지 않음).
            if (button == MouseButton.Left && BeginTransformDrag(loc, e.ClickCount)) e.Pointer.Capture(this);
            return;
        }
        if (_ctx.IsGroupSelected)
        {
            // 그룹 화면: 9-slice 도구로 부위의 초록 선을 잡으면 그 선을, 아니면 어떤 도구든 부위 배치.
            if (button == MouseButton.Left && _ctx.Tool == ToolKind.Slice && BeginSliceDrag(loc)) { }
            else if (button == MouseButton.Left) BeginArrange(px, e.ClickCount);
            if (_drag is DragMode.Arrange or DragMode.Slice) e.Pointer.Capture(this);
            return;
        }

        var mods = e.KeyModifiers;
        // Ctrl(⌘)을 캔버스 밖에서 누르고 들어와 누른 경우도 임시 이동 도구로
        if (Ui.HasCommand(mods)) BeginTempMove();
        var tool = _ctx.Tool;
        bool alt = (mods & KeyModifiers.Alt) != 0;
        if (IsPaintTool(tool) && alt)
        {
            // Alt+클릭: 임시 스포이드
            tool = ToolKind.Picker;
            _altUsed = true;
        }
        if (IsPaintTool(tool) && !_ctx.CanEdit)
        {
            Message?.Invoke(this, UiStrings.S_PixelCanvas_OnPointerPressed_F29C349441);
            return;
        }
        e.Pointer.Capture(this);
        switch (tool)
        {
            case ToolKind.Pencil:
            case ToolKind.Eraser:
            case ToolKind.Dodge:
            case ToolKind.Dither:
            case ToolKind.ReplaceBrush:
                BeginPixelStroke(tool, button, Wrap(px));
                if ((mods & KeyModifiers.Shift) != 0 && _lastStrokeEnd is { } from)
                    foreach (var p in Raster.Line(from, px)) StampAt(p);   // Shift+클릭: 마지막 점에서 직선
                else StampAt(px);
                _ctx.NotifyPixels();
                _drag = DragMode.Stroke;
                break;
            case ToolKind.Shape:
                BeginPixelStroke(tool, button, px);
                DrawShape(px);
                _drag = DragMode.Shape;
                break;
            case ToolKind.Fill:
                BeginPixelStroke(tool, button, px);
                FillAt(Wrap(px));
                _drag = DragMode.Fill;
                break;
            case ToolKind.Gradient:
                if (button != MouseButton.Left) break;
                BeginPixelStroke(tool, button, px);
                _gradientEnd = px;
                _drag = DragMode.Gradient;
                break;
            case ToolKind.Picker:
                PickAt(Wrap(px), button);
                _drag = DragMode.Picker;
                break;
            case ToolKind.Select:
            case ToolKind.EllipseSelect:
                if (button == MouseButton.Right) { DeselectByRightClick(); break; }
                if (button != MouseButton.Left) break;
                if (_ctx.Selection.IsInside(px)) BeginMoveSelection();
                else
                {
                    _ctx.CommitFloating();
                    _strokeTool = tool;
                    _drag = DragMode.Marquee;
                }
                break;
            case ToolKind.Lasso:
                if (button == MouseButton.Right) { DeselectByRightClick(); break; }
                if (button != MouseButton.Left) break;
                if (_ctx.Selection.IsInside(px)) BeginMoveSelection();
                else
                {
                    _ctx.CommitFloating();
                    _lassoPath.Clear();
                    _lassoPreview = null;
                    _lassoPath.Add(px);
                    _drag = DragMode.Lasso;
                }
                break;
            case ToolKind.MagicWand:
                if (button == MouseButton.Right) DeselectByRightClick();
                else if (button == MouseButton.Left) WandAt(Wrap(px));
                break;
            case ToolKind.Move:
                if (button != MouseButton.Left) break;
                if (_ctx.Selection.HasSelection) BeginMoveSelection();
                else BeginMoveLayer();
                break;
            case ToolKind.Slice:
                if (button == MouseButton.Left) BeginSliceDrag(loc);
                break;
            case ToolKind.Pivot:
                if (button != MouseButton.Left) break;
                _pivotBefore = _ctx.Pivot;
                _drag = DragMode.Pivot;
                DragPivot(loc);
                break;
        }
        if (_drag == DragMode.None) e.Pointer.Capture(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // 다른 버튼을 누른 채 또 누르면(화음) 플랫폼에 따라 이동 이벤트로 온다.
        if (TryCancelByOtherButton(e.GetCurrentPoint(this), e.Pointer)) return;
        var loc = ToInt(e.GetPosition(this));
        _mouseScreen = loc;
        _mods = e.KeyModifiers;
        if (!_ctx.HasProject) return;
        var px = ScreenToPixel(loc);
        var wrapped = Wrap(px);
        Point? hover = wrapped.X >= 0 && wrapped.Y >= 0 && wrapped.X < W && wrapped.Y < H ? wrapped : null;
        if (hover != _hoverPx)
        {
            _hoverPx = hover;
            HoverChanged?.Invoke(this, hover);
            if (_drag == DragMode.None) InvalidateVisual();
        }
        if (_curve is not null && _drag == DragMode.None && px != _curveThrough)
        {
            _curveThrough = px;
            InvalidateVisual();
        }
        if (Math.Abs(loc.X - _dragStartScreen.X) + Math.Abs(loc.Y - _dragStartScreen.Y) > 3) _dragMoved = true;

        switch (_drag)
        {
            case DragMode.None:
                if (_ctx.IsGroupSelected) UpdateHoverPart(px);
                UpdateCursor(px);
                return;
            case DragMode.Pan:
                _offset.Offset(loc.X - _lastScreen.X, loc.Y - _lastScreen.Y);
                _lastScreen = loc;
                InvalidateVisual();
                return;
            case DragMode.Stroke:
                if (px == _lastPx) return;
                foreach (var p in Raster.Line(_lastPx, px).Skip(1)) StampAt(p);
                _ctx.NotifyPixels();
                break;
            case DragMode.Shape:
                if (px == _lastPx) return;
                DrawShape(px);
                break;
            case DragMode.Gradient:
                if (px == _lastPx) return;
                DrawGradient(px);
                break;
            case DragMode.Picker:
                PickAt(wrapped, _dragButton);
                break;
            case DragMode.Marquee:
                if (_dragMoved)
                {
                    var r = Raster.RectFromPoints(_startPx, px, (_mods & KeyModifiers.Shift) != 0);
                    _ctx.Selection.Select(_ctx.EditImageOrEmpty, _strokeTool == ToolKind.EllipseSelect ? PixelMask.Ellipse(r) : r);
                }
                break;
            case DragMode.Lasso:
                if (px == _lastPx) return;
                // 빠르게 끌어도 빈틈없이 이어진 점들로.
                foreach (var p in Raster.Line(_lassoPath[^1], px).Skip(1)) _lassoPath.Add(p);
                _lassoPreview = null;
                InvalidateVisual();
                break;
            case DragMode.MoveSelection:
                if (px == _lastPx) return;
                _ctx.Selection.MoveBy(_ctx.EditImageOrEmpty, px.X - _lastPx.X, px.Y - _lastPx.Y);
                _ctx.NotifyPixels();
                break;
            case DragMode.MoveLayer:
                if (px == _lastPx) return;
                PixelOps.Shift(_strokeBase!, _ctx.EditImage!, new Rectangle(0, 0, W, H), px.X - _startPx.X, px.Y - _startPx.Y, _ctx.MoveWrap);
                _ctx.NotifyPixels();
                break;
            case DragMode.Pivot:
                DragPivot(loc);
                break;
            case DragMode.Slice:
                DragSlice(loc);
                break;
            case DragMode.TransformMove:
            case DragMode.TransformScale:
            case DragMode.TransformRotate:
            case DragMode.TransformPivot:
                DragTransform(loc);
                break;
            case DragMode.CanvasResizeEdge:
            case DragMode.CanvasResizeMove:
                DragCanvasResize(loc);
                break;
            case DragMode.Arrange:
            {
                var offset = new Point(_arrangeStart.X + px.X - _startPx.X, _arrangeStart.Y + px.Y - _startPx.Y);
                if (offset != _arrangeOffset)
                {
                    _arrangeOffset = offset;
                    InvalidateVisual();
                }
                break;
            }
        }
        _lastPx = px;
        _lastScreen = loc;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None || e.InitialPressMouseButton != _dragButton) return;
        e.Handled = true;
        EndDrag(ToInt(e.GetPosition(this)));
        e.Pointer.Capture(null);
        // 끄는 중에 Ctrl(⌘)을 뗐으면 이제 원래 도구로
        if (_tempMoveEndPending) EndTempMove();
    }

    private void EndDrag(Point location)
    {
        var mode = _drag;
        _drag = DragMode.None;
        if (!_ctx.HasProject) return;
        switch (mode)
        {
            case DragMode.Pan:
                UpdateCursor(ScreenToPixel(location));
                break;
            case DragMode.Stroke:
                _lastStrokeEnd = Wrap(_lastPx);
                EndPixelStroke();
                break;
            case DragMode.Shape:
                // 곡선: 양 끝을 정했으면 놓은 뒤 휘기로(끌지 않고 뗐으면 점 하나로 확정).
                if (_strokeTool == ToolKind.Shape && _strokeShape.Kind == ShapeKind.Curve && _lastPx != _startPx)
                {
                    BeginCurveBend();
                    break;
                }
                EndPixelStroke();
                break;
            case DragMode.Fill:
                EndPixelStroke();
                break;
            case DragMode.Gradient:
                if (_gradientEnd == _startPx)
                {
                    // 끌지 않고 뗐으면 방향이 없으므로 아무것도 칠하지 않는다.
                    _ctx.CancelStroke();
                    _strokeBase = null;
                    Message?.Invoke(this, UiStrings.S_PixelCanvas_EndDrag_E0D2C0AAD2);
                }
                else EndPixelStroke();
                InvalidateVisual();
                break;
            case DragMode.MoveLayer:
                ShowCelShift(_ctx.CommitCelShift(UiStrings.S_PixelCanvas_EndDrag_685B820F08, _lastPx.X - _startPx.X, _lastPx.Y - _startPx.Y, _ctx.MoveWrap));
                _strokeBase = null;
                break;
            case DragMode.Marquee:
                if (!_dragMoved)
                {
                    _ctx.Selection.Deselect(_ctx.EditImageOrEmpty);
                    Message?.Invoke(this, UiStrings.S_PixelCanvas_EndDrag_DB199DD98F);
                }
                break;
            case DragMode.Lasso:
                if (_dragMoved && _lassoPath.Count > 1) _ctx.Selection.Select(_ctx.EditImageOrEmpty, PixelMask.Polygon(_lassoPath));
                else
                {
                    _ctx.Selection.Deselect(_ctx.EditImageOrEmpty);
                    Message?.Invoke(this, UiStrings.S_PixelCanvas_EndDrag_DB199DD98F);
                }
                _lassoPath.Clear();
                _lassoPreview = null;
                InvalidateVisual();
                break;
            case DragMode.Pivot:
                _ctx.CommitPivot(_pivotBefore);
                break;
            case DragMode.Slice:
                var draft = _sliceDraft;
                var slicePart = _slicePart;
                _sliceDraft = null;
                _slicePart = null;
                _sliceEdge = SliceEdge.None;
                if (draft is { } d)
                {
                    if (slicePart is not null) _ctx.SetPartBorder(slicePart, d);
                    else _ctx.SetSliceBorder(d);
                }
                UpdateCursor(ScreenToPixel(location));
                break;
            case DragMode.Arrange:
                EndArrange();
                break;
            case DragMode.TransformMove:
            case DragMode.TransformScale:
            case DragMode.TransformRotate:
            case DragMode.TransformPivot:
            case DragMode.CanvasResizeEdge:
            case DragMode.CanvasResizeMove:
                UpdateCursor(ScreenToPixel(location));
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverPart is not null && _drag == DragMode.None)
        {
            _hoverPart = null;
            InvalidateVisual();
        }
        if (_hoverPx is null) return;
        _hoverPx = null;
        HoverChanged?.Invoke(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // 드래그 중 캡처를 잃으면 지금까지를 확정한다.
        if (_drag != DragMode.None) EndDrag(_lastScreen);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_drag == DragMode.None) return;
        // 드래그 중 포커스를 잃으면(대화상자 등) 지금까지를 확정한다.
        EndDrag(_lastScreen);
    }

    // ───────────── 그리기 도구 ─────────────

    private void BeginPixelStroke(ToolKind tool, MouseButton button, Point start)
    {
        _ctx.CommitFloating();
        _ctx.BeginStroke();
        var img = _ctx.EditImage!;
        _strokeBase = img.Clone();
        _strokeTool = tool;
        _strokeShape = _ctx.ShapeStyle;
        bool right = button == MouseButton.Right;
        _strokeColor = tool switch
        {
            ToolKind.Eraser => Color32.Transparent,
            _ when right && _ctx.RightButtonErases && tool is ToolKind.Pencil or ToolKind.Shape or ToolKind.Fill
                => Color32.Transparent,
            _ => right ? _ctx.Secondary : _ctx.Primary,
        };
        _strokeColor2 = right ? _ctx.Primary : _ctx.Secondary;
        _strokeLighten = !right;
        _replaceTarget = img.Get(start.X, start.Y);
        _visited = tool == ToolKind.Dodge ? new HashSet<Point>() : null;
        _ppStroke = tool is ToolKind.Pencil or ToolKind.Eraser && _ctx.PixelPerfect && _ctx.BrushSize == 1 ? new PixelPerfectStroke() : null;
    }

    private void EndPixelStroke()
    {
        var label = _strokeTool switch
        {
            ToolKind.Pencil => UiStrings.S_PixelCanvas_EndPixelStroke_CF98AEC3ED,
            ToolKind.Eraser => UiStrings.S_PixelCanvas_EndPixelStroke_8EFE7C05AE,
            ToolKind.Fill => UiStrings.S_PixelCanvas_EndPixelStroke_94045434EF,
            ToolKind.Gradient => UiStrings.S_PixelCanvas_EndPixelStroke_45906D38E1,
            ToolKind.Shape => ToolOptionsPanel.ShapeName(_strokeShape.Kind),
            ToolKind.Dodge => _strokeLighten ? UiStrings.S_PixelCanvas_EndPixelStroke_57BA62C92E : UiStrings.S_PixelCanvas_EndPixelStroke_D019BA28D7,
            ToolKind.Dither => UiStrings.S_PixelCanvas_EndPixelStroke_1C14189718,
            _ => UiStrings.S_PixelCanvas_EndPixelStroke_E40CB15E95,
        };
        _ctx.CommitStroke(label);
        if (_strokeTool is not (ToolKind.Eraser or ToolKind.Dodge or ToolKind.Gradient) && _strokeColor.A != 0) _ctx.PushRecentColor(_strokeColor);
        _strokeBase = null;
        _ppStroke = null;
        _visited = null;
    }

    /// <summary>도형(또는 브러시 획)을 그리는 중에 반대쪽 버튼을 누르면 그 획을 취소한다. 취소했으면 true.</summary>
    private bool TryCancelByOtherButton(PointerPoint pt, IPointer pointer)
    {
        if (_drag is not (DragMode.Shape or DragMode.Stroke or DragMode.Gradient)) return false;
        var kind = pt.Properties.PointerUpdateKind;
        bool other = (_dragButton == MouseButton.Left && kind == PointerUpdateKind.RightButtonPressed)
                     || (_dragButton == MouseButton.Right && kind == PointerUpdateKind.LeftButtonPressed);
        if (!other) return false;
        CancelPixelStroke();
        pointer.Capture(null);
        return true;
    }

    /// <summary>그리던 획을 버린다(셀은 시작 전으로, 실행취소 기록 없음). 누르고 있던 버튼을 떼도 아무 일도 없다.</summary>
    private void CancelPixelStroke()
    {
        _drag = DragMode.None;
        _ctx.CancelStroke();
        _strokeBase = null;
        _ppStroke = null;
        _visited = null;
        Message?.Invoke(this, UiStrings.S_PixelCanvas_CancelPixelStroke_53972D288A);
        InvalidateVisual();
    }

    /// <summary>점들을 타일 모드 감기 → 대칭 순으로 바꾼다.</summary>
    private List<Point> Targets(IEnumerable<Point> pts)
        => Raster.Mirror(pts.Select(Wrap), W, H, _ctx.MirrorX, _ctx.MirrorY);

    private void StampAt(Point p)
    {
        var img = _ctx.EditImage!;
        var clip = _ctx.Selection.Clip;
        switch (_strokeTool)
        {
            case ToolKind.Dither:
                foreach (var m in Targets(Raster.Brush(p.X, p.Y, _ctx.BrushSize, _ctx.BrushShape)))
                    if (clip is null || clip.Value.Contains(m))
                        img.Set(m.X, m.Y, Raster.DitherUsesPrimary(m.X, m.Y, _ctx.DitherPattern) ? _strokeColor : _strokeColor2);
                return;
            case ToolKind.Dodge:
            {
                float amount = _ctx.DodgeStrength / 100f;
                foreach (var m in Targets(Raster.Brush(p.X, p.Y, _ctx.BrushSize, _ctx.BrushShape)))
                    if ((clip is null || clip.Value.Contains(m)) && img.Contains(m.X, m.Y) && _visited!.Add(m))
                        img.Set(m.X, m.Y, PixelOps.Dodge(img.Get(m.X, m.Y), _strokeLighten, amount));
                return;
            }
            case ToolKind.ReplaceBrush:
                foreach (var m in Targets(Raster.Brush(p.X, p.Y, _ctx.BrushSize, _ctx.BrushShape)))
                    if ((clip is null || clip.Value.Contains(m)) && img.Contains(m.X, m.Y)
                        && Color32.SameVisible(_strokeBase!.Get(m.X, m.Y), _replaceTarget))
                        img.Set(m.X, m.Y, _strokeColor);
                return;
        }
        if (_ppStroke is not null)
        {
            var (draw, restore) = _ppStroke.Add(p);
            if (restore is { } r)
                foreach (var m in Targets([r]))
                    if (clip is null || clip.Value.Contains(m)) img.Set(m.X, m.Y, _strokeBase!.Get(m.X, m.Y));
            if (draw is { } d) Raster.Plot(img, Targets([d]), _strokeColor, clip);
            return;
        }
        Raster.Plot(img, Targets(Raster.Brush(p.X, p.Y, _ctx.BrushSize, _ctx.BrushShape)), _strokeColor, clip);
    }

    private void DrawShape(Point end)
    {
        var img = _ctx.EditImage!;
        img.CopyFrom(_strokeBase!);
        bool shift = (_mods & KeyModifiers.Shift) != 0;
        var pts = _ctx.ShapeFilled && Shapes.CanFill(_strokeShape.Kind)
            ? Shapes.Fill(_strokeShape, _startPx, end, shift)
            : WithBrush(Shapes.Outline(_strokeShape, _startPx, end, shift));
        Raster.Plot(img, Targets(pts), _strokeColor, _ctx.Selection.Clip);
        _ctx.NotifyPixels();
    }

    /// <summary>선·곡선의 끝점(놓은 곳, Shift면 15° 단위로 맞춘 곳).</summary>
    private Point ShapeEnd() => (_mods & KeyModifiers.Shift) != 0 ? Shapes.SnapLineEnd(_startPx, _lastPx) : _lastPx;

    /// <summary>곡선의 양 끝을 정한 획을 거두고(셀은 그대로) 휘기를 시작한다. 휘는 점은 처음에 두 끝의 가운데(직선).</summary>
    private void BeginCurveBend()
    {
        var a = _startPx;
        var b = ShapeEnd();
        _ctx.CancelStroke();
        _strokeBase = null;
        _curve = (a, b, _dragButton);
        _curveThrough = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        Message?.Invoke(this, UiStrings.Shape_CurveBend);
        InvalidateVisual();
    }

    /// <summary>휘던 곡선을 칠한다(실행취소 한 단계). 휘는 중이 아니었으면 false.</summary>
    public bool CommitCurve()
    {
        if (_curve is not { } curve) return false;
        _curve = null;
        if (_ctx.CanEdit && _ctx.EditImage is not null)
        {
            BeginPixelStroke(ToolKind.Shape, curve.Button, curve.A);
            _strokeShape = _strokeShape with { Kind = ShapeKind.Curve };
            Raster.Plot(_ctx.EditImage!, Targets(WithBrush(CurvePoints(curve))), _strokeColor, _ctx.Selection.Clip);
            _ctx.NotifyPixels();
            EndPixelStroke();
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>휘던 곡선을 버린다(Esc·Ctrl+Z·반대 버튼). 휘는 중이 아니었으면 false.</summary>
    public bool CancelCurve()
    {
        if (_curve is null) return false;
        _curve = null;
        Message?.Invoke(this, UiStrings.S_PixelCanvas_CancelPixelStroke_53972D288A);
        InvalidateVisual();
        return true;
    }

    /// <summary>선 점들에 브러시(크기·모양)를 입힌다.</summary>
    private List<Point> WithBrush(List<Point> pts)
    {
        if (_ctx.BrushSize <= 1) return pts;
        var set = new HashSet<Point>();
        var list = new List<Point>();
        foreach (var p in pts)
            foreach (var b in Raster.Brush(p.X, p.Y, _ctx.BrushSize, _ctx.BrushShape))
                if (set.Add(b)) list.Add(b);
        return list;
    }

    /// <summary>그라데이션을 시작점 → <paramref name="end"/>(Shift: 45° 단위)로 다시 칠한다(획 시작 전 그림 위에).</summary>
    private void DrawGradient(Point end)
    {
        if ((_mods & KeyModifiers.Shift) != 0) end = Raster.SnapAngle(_startPx, end);
        _gradientEnd = end;
        var img = _ctx.EditImage!;
        img.CopyFrom(_strokeBase!);
        Gradient.Apply(img, _startPx, end, _ctx.GradientStops, _ctx.GradientInterpolation, _ctx.GradientDither, _ctx.Selection.Clip, _ctx.GradientAlphaStops, _ctx.GradientColorSpace);
        _ctx.NotifyPixels();
        InvalidateVisual();
    }

    /// <summary>그라데이션을 끄는 동안 시작점·끝점을 잇는 안내선.</summary>
    private void DrawGradientGuide(DrawingContext g)
    {
        var a = ImageToScreen(_startPx.X + 0.5, _startPx.Y + 0.5);
        var b = ImageToScreen(_gradientEnd.X + 0.5, _gradientEnd.Y + 0.5);
        g.DrawLine(CursorOutlineBackPen, a, b);
        g.DrawLine(CursorOutlineFrontPen, a, b);
        const double r = 3.5;
        g.DrawEllipse(Brushes.White, CursorBlackPen, a, r, r);
        g.DrawEllipse(Brushes.Black, CursorWhitePen, b, r, r);
    }

    private void FillAt(Point p)
    {
        var img = _ctx.EditImage!;
        if (!img.Contains(p.X, p.Y)) return;
        // 보이는 레이어 기준: 칠하기 전 합성으로 영역을 정한다(대칭 점들도 같은 기준).
        var sample = _ctx.FillSampleVisible ? _ctx.VisibleImage() : null;
        if (sample is not null && (sample.Width != img.Width || sample.Height != img.Height)) sample = null;
        int changed = 0;
        foreach (var m in Raster.Mirror([p], W, H, _ctx.MirrorX, _ctx.MirrorY))
            changed += Raster.FloodFill(img, m.X, m.Y, _strokeColor, _ctx.FillContiguous, _ctx.Selection.Clip,
                _ctx.FillToleranceChannels, _ctx.FillDiagonal, sample);
        if (changed > 0) _ctx.NotifyPixels();
    }

    /// <summary>색을 집는다: 보이는 색(합성 결과) 또는 현재 레이어의 색(스포이드 기준 옵션). 떠 있는 선택 내용이 위에 있으면 그 색.</summary>
    private void PickAt(Point p, MouseButton button)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= W || p.Y >= H) return;
        var c = _ctx.PickerSampleVisible ? _ctx.CompositeColorAt(p.X, p.Y)
            : _ctx.EditImage is { } cel ? cel.Get(p.X, p.Y) : Color32.Transparent;
        if (_ctx.Selection.Floating is { } f && _ctx.Selection.Bounds.Contains(p))
        {
            var fc = f.Get(p.X - _ctx.Selection.Bounds.X, p.Y - _ctx.Selection.Bounds.Y);
            if (fc.A != 0) c = fc;
        }
        if (button == MouseButton.Right) _ctx.Secondary = c;
        else _ctx.Primary = c;
    }

    /// <summary>선택 도구에서 오른쪽 클릭: 떠 있는 내용을 내려놓고 선택을 푼다(Ctrl+D와 같음).</summary>
    private void DeselectByRightClick()
    {
        if (!_ctx.Selection.HasSelection) return;
        _ctx.CommitFloating();
        _ctx.Selection.Deselect(_ctx.EditImageOrEmpty);
        Message?.Invoke(this, UiStrings.S_PixelCanvas_DeselectByRightClick_1E4DB6CE1A);
    }

    /// <summary>스마트 선택: 클릭한 곳과 맞는 색 영역을 선택한다(채우기의 허용 오차·이어진 영역·대각선·기준 설정). 맞는 곳이 없으면 선택 해제.</summary>
    private void WandAt(Point p)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= W || p.Y >= H) return;
        _ctx.CommitFloating();
        var source = _ctx.FillSampleVisible ? _ctx.VisibleImage() : _ctx.EditImageOrEmpty;
        if (!source.Contains(p.X, p.Y)) return;
        var region = Raster.FloodRegion(source, p.X, p.Y, _ctx.FillContiguous, null, _ctx.FillToleranceChannels, _ctx.FillDiagonal);
        _ctx.Selection.Select(_ctx.EditImageOrEmpty, region);
        var sel = _ctx.Selection;
        Message?.Invoke(this, sel.HasSelection ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_WandAt_2BA26AADD0,sel.Region.Count,sel.Bounds.Width,sel.Bounds.Height) : UiStrings.S_PixelCanvas_WandAt_7AF3F5F46E);
    }

    // ───────────── 선택 영역·이동 ─────────────

    private void BeginMoveSelection()
    {
        if (!_ctx.CanEdit)
        {
            Message?.Invoke(this, UiStrings.S_PixelCanvas_BeginMoveSelection_EE35204CAB);
            return;
        }
        _ctx.BeginSelectionEdit();
        _drag = DragMode.MoveSelection;
    }

    /// <summary>선택이 없을 때 이동 도구: 현재 셀 전체를 민다(순환 옵션).</summary>
    private void BeginMoveLayer()
    {
        if (!_ctx.CanEdit)
        {
            Message?.Invoke(this, UiStrings.S_PixelCanvas_BeginMoveLayer_95920E31EA);
            return;
        }
        _ctx.CommitFloating();
        _ctx.BeginStroke();
        _strokeBase = _ctx.EditImage!.Clone();
        _drag = DragMode.MoveLayer;
    }

    /// <summary>방향키로 선택 내용(없으면 이동 도구에서 레이어 내용, 그룹 화면이면 부위 위치, 캔버스 크기 모드면 그림 자리)을 옮긴다.</summary>
    public bool Nudge(int dx, int dy)
    {
        if (_ctx.IsCanvasResizing)
        {
            var image = _ctx.CanvasResizeImage;
            image.Offset(dx, dy);
            _ctx.SetCanvasResize(_ctx.CanvasResizeCanvas, image);
            return true;
        }
        if (_ctx.IsGroupSelected) return NudgePart(dx, dy);
        if (_ctx.IsTransforming)
        {
            var p = _ctx.TransformParams;
            _ctx.SetTransformParams(p with { CenterX = p.CenterX + dx, CenterY = p.CenterY + dy });
            return true;
        }
        if (!_ctx.HasProject || !_ctx.CanEdit) return false;
        if (_ctx.Selection.HasSelection && (EditorContext.IsSelectionTool(_ctx.Tool) || _ctx.Tool == ToolKind.Move))
        {
            _ctx.BeginSelectionEdit();
            _ctx.Selection.MoveBy(_ctx.EditImage!, dx, dy);
            _ctx.NotifyPixels();
            return true;
        }
        if (_ctx.Tool != ToolKind.Move) return false;
        _ctx.BeginStroke();
        var img = _ctx.EditImage!;
        var copy = img.Clone();
        PixelOps.Shift(copy, img, new Rectangle(0, 0, W, H), dx, dy, _ctx.MoveWrap);
        _ctx.NotifyPixels();
        ShowCelShift(_ctx.CommitCelShift(UiStrings.S_PixelCanvas_Nudge_A41E3BE80E, dx, dy, _ctx.MoveWrap));
        return true;
    }

    /// <summary>이동 도구가 타임라인에서 함께 고른 다른 셀도 옮겼으면 상태 표시줄에 알린다.</summary>
    private void ShowCelShift(int others)
    {
        if (others > 0) Message?.Invoke(this, UiStrings.FormatMove_OtherCels(count: others));
    }

    /// <summary>붙여넣을 위치: 선택이 있으면 그 자리, 없으면 화면에 보이는 영역의 왼쪽 위.</summary>
    public Point PasteLocation()
    {
        if (!_ctx.HasProject) return Point.Empty;
        if (_ctx.Selection.HasSelection) return _ctx.Selection.Bounds.Location;
        var src = VisibleSourceRect(0, 0);
        return src.IsEmpty ? Point.Empty : src.Location;
    }

    private void DragPivot(Point screen)
    {
        var f = ScreenToImageF(screen);
        // 반 픽셀 단위로 맞춘다.
        float fx = MathF.Round((float)f.X * 2) / 2, fy = MathF.Round((float)f.Y * 2) / 2;
        _ctx.PreviewPivot(Pivot.FromLocalPixel(fx, fy, W, H));
    }

    // ───────────── 그룹 화면: 부위 배치 ─────────────

    /// <summary>
    /// 프레임 좌표 <paramref name="px"/> 아래의 맨 위 보이는 부위(현재 그룹). 그 자리에 칠한 픽셀이 있는 부위를 먼저,
    /// 없으면 사각형만 겹치는 부위를 고른다.
    /// </summary>
    private Layer? PartAt(Point px)
    {
        if (_ctx.Project is not { } p || _ctx.CurrentGroup is not { } group) return null;
        var parts = p.ChildrenOf(group);
        Layer? byRect = null;
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var part = parts[i];
            int index = p.IndexOf(part);
            if (!p.IsLayerVisible(index) || !part.PartRect.Contains(px)) continue;
            if (p.GetCel(index, _ctx.CurrentFrame) is { } cel && cel.Get(px.X - part.PartOffset.X, px.Y - part.PartOffset.Y).A != 0) return part;
            byRect ??= part;
        }
        return byRect;
    }

    /// <summary>고른 부위가 아직 이 그룹의 부위이면 그것, 아니면 null.</summary>
    private Layer? SelectedPartIn(SpriteProject p, Layer group)
        => _selectedPart is { } part && ReferenceEquals(part.Parent, group) && p.Layers.Contains(part) ? part : null;

    /// <summary>그룹 화면에서 고른 부위를 푼다(Esc). 풀었으면 true.</summary>
    public bool ClearPartSelection()
    {
        if (_selectedPart is null || !_ctx.IsGroupSelected) return false;
        _selectedPart = null;
        InvalidateVisual();
        return true;
    }

    private void UpdateHoverPart(Point px)
    {
        var part = PartAt(px);
        if (ReferenceEquals(part, _hoverPart)) return;
        _hoverPart = part;
        InvalidateVisual();
    }

    /// <summary>그룹 화면 누르기: 더블클릭이면 그 부위 레이어를 고르고, 아니면 부위 끌기를 시작한다.</summary>
    private void BeginArrange(Point px, int clickCount)
    {
        if (_ctx.Project is not { } p) return;
        var part = PartAt(px);
        if (part is null)
        {
            if (_selectedPart is not null)
            {
                _selectedPart = null;
                InvalidateVisual();
                Message?.Invoke(this, UiStrings.S_PixelCanvas_BeginArrange_28B8E5E745);
            }
            return;
        }
        _selectedPart = part;
        InvalidateVisual();
        int index = p.IndexOf(part);
        if (clickCount >= 2)
        {
            _hoverPart = null;
            _ctx.SetCurrentLayer(index);
            Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_BeginArrange_914451A106,part.Name));
            return;
        }
        if (p.IsLayerLocked(index))
        {
            Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_BeginArrange_9D0F6205A8,part.Name));
            return;
        }
        if (part.IsAutoLaidOut)
        {
            // 끌기 시작에서 한 번만 묻는다. 대화상자가 마우스 놓기를 가져가므로 끌기는 잇지 않고, 확인하면 자동 배치를 끈다(다시 끌면 옮겨짐).
            var group = part.Parent!;
            Dispatcher.UIThread.Post(() =>
            {
                if (!GroupLayoutDialog.ConfirmManualMove(this, part))
                {
                    Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_BeginArrange_B1F7BBC533,group.Name));
                    return;
                }
                _ctx.SetGroupLayout(group, null);
                Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_BeginArrange_32D63E2C46,group.Name));
            });
            return;
        }
        _ctx.CommitFloating();
        _arrangePart = part;
        _arrangeStart = _arrangeOffset = part.PartOffset;
        _drag = DragMode.Arrange;
        Cursor = SizeAllCursor;
    }

    /// <summary>부위 끌기를 마친다: 위치가 바뀌었으면 실행취소 한 단계로 기록한다.</summary>
    private void EndArrange()
    {
        var part = _arrangePart;
        _arrangePart = null;
        if (part is null || _ctx.Project is not { } p || _arrangeOffset == _arrangeStart || p.IndexOf(part) < 0) return;
        var offset = _arrangeOffset;
        _ctx.MovePart(part, offset);
        Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_EndArrange_A643730044,part.Name,offset.X,offset.Y));
    }

    /// <summary>그룹 화면 방향키: 마지막으로 올리거나 끈 부위(없으면 그룹 맨 위 부위)를 옮긴다.</summary>
    private bool NudgePart(int dx, int dy)
    {
        if (_ctx.Project is not { } p || _ctx.CurrentGroup is not { } group || _drag != DragMode.None) return false;
        if (SelectedPartIn(p, group) is not { } part)
        {
            Message?.Invoke(this, UiStrings.S_PixelCanvas_NudgePart_386C0383DB);
            return true;
        }
        int index = p.IndexOf(part);
        if (p.IsLayerLocked(index))
        {
            Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_NudgePart_20E5A45EA7,part.Name));
            return true;
        }
        var offset = new Point(part.PartOffset.X + dx, part.PartOffset.Y + dy);
        if (!GroupLayoutDialog.ConfirmManualMove(this, part))
        {
            Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_NudgePart_61255F9462,group.Name));
            return true;
        }
        _ctx.MovePart(part, offset);
        Message?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_NudgePart_4EE81EE408,part.Name,offset.X,offset.Y));
        return true;
    }

    // ───────────── 변형 모드(Ctrl+T) ─────────────

    /// <summary>손잡이를 잡는 거리(화면 px, 손잡이 가운데에서 가로·세로).</summary>
    private const double TransformHandleGrab = 6;
    /// <summary>변형한 사각형 바깥 이 거리(화면 px) 안이면 회전.</summary>
    private const double TransformRotateBand = 20;

    /// <summary>손잡이 8개(원본 기준 -1/0/1, 가운데 빼고).</summary>
    private static readonly (int Hx, int Hy)[] TransformHandles =
        [(-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0)];

    /// <summary>원본 기준 손잡이(-1/0/1)의 화면 위치.</summary>
    private Avalonia.Point TransformHandleScreen(FreeTransform t, TransformParams p, int hx, int hy)
    {
        var (cx, cy) = t.SourceCenter;
        var r = t.SourceBounds;
        var (x, y) = t.Map(p, cx + hx * r.Width / 2.0, cy + hy * r.Height / 2.0);
        return ImageToScreen(x, y);
    }

    /// <summary>변형 모드: 변형한 사각형(주황 점선), 손잡이 8개, 회전 중심 표시.</summary>
    private void DrawTransform(DrawingContext g)
    {
        if (_ctx.Transform is not { } t) return;
        var p = _ctx.TransformParams;
        var corners = t.Corners(p).Select(c => ImageToScreen(c.X, c.Y)).ToArray();
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(corners[0], false);
            for (int i = 1; i < corners.Length; i++) c.LineTo(corners[i]);
            c.EndFigure(true);
        }
        g.DrawGeometry(null, TransformBackPen, geo);
        g.DrawGeometry(null, TransformPen, geo);
        foreach (var (hx, hy) in TransformHandles)
        {
            var h = TransformHandleScreen(t, p, hx, hy);
            g.DrawRectangle(TransformHandleBrush, TransformHandlePen, new Rect(Math.Round(h.X) - 3.5, Math.Round(h.Y) - 3.5, 7, 7));
        }
        var (px, py) = _ctx.TransformPivot;
        var pv = ImageToScreen(px, py);
        const double r = 4.5;
        g.DrawEllipse(null, PivotBackPen, pv, r, r);
        g.DrawEllipse(null, TransformPivotPen, pv, r, r);
        g.DrawLine(TransformPivotPen, new Avalonia.Point(pv.X - r - 3, pv.Y), new Avalonia.Point(pv.X + r + 3, pv.Y));
        g.DrawLine(TransformPivotPen, new Avalonia.Point(pv.X, pv.Y - r - 3), new Avalonia.Point(pv.X, pv.Y + r + 3));
    }

    /// <summary>화면 위치가 가리키는 변형 동작: 손잡이(크기) → 회전 중심 → 안쪽(옮기기) → 바깥 띠(회전) 순.</summary>
    private (TransformHit Kind, int Hx, int Hy) TransformHitAt(Point screen)
    {
        if (_ctx.Transform is not { } t) return (TransformHit.None, 0, 0);
        var p = _ctx.TransformParams;
        double best = double.MaxValue;
        (TransformHit, int, int) hit = (TransformHit.None, 0, 0);
        foreach (var (hx, hy) in TransformHandles)
        {
            var h = TransformHandleScreen(t, p, hx, hy);
            double dx = Math.Abs(screen.X - h.X), dy = Math.Abs(screen.Y - h.Y);
            if (dx > TransformHandleGrab || dy > TransformHandleGrab || dx + dy >= best) continue;
            best = dx + dy;
            hit = (TransformHit.Scale, hx, hy);
        }
        if (hit.Item1 != TransformHit.None) return hit;
        var (px, py) = _ctx.TransformPivot;
        var pv = ImageToScreen(px, py);
        if (Math.Abs(screen.X - pv.X) <= TransformHandleGrab && Math.Abs(screen.Y - pv.Y) <= TransformHandleGrab) return (TransformHit.Pivot, 0, 0);
        var img = ScreenToImageF(screen);
        var (u, v) = t.Unmap(p, img.X, img.Y);
        var r = t.SourceBounds;
        if (u >= r.Left && u <= r.Right && v >= r.Top && v <= r.Bottom) return (TransformHit.Move, 0, 0);
        var corners = t.Corners(p).Select(c => ImageToScreen(c.X, c.Y)).ToArray();
        double distance = double.MaxValue;
        for (int i = 0; i < 4; i++) distance = Math.Min(distance, SegmentDistance(screen, corners[i], corners[(i + 1) % 4]));
        return distance <= TransformRotateBand ? (TransformHit.Rotate, 0, 0) : (TransformHit.None, 0, 0);
    }

    /// <summary>점에서 선분까지의 거리(화면 px).</summary>
    private static double SegmentDistance(Point p, Avalonia.Point a, Avalonia.Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, len = vx * vx + vy * vy;
        double k = len <= 0 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len, 0, 1);
        double dx = p.X - (a.X + vx * k), dy = p.Y - (a.Y + vy * k);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>변형 모드 커서: 손잡이는 화면에서의 방향에 맞는 크기 조절 화살표, 안쪽은 옮기기, 바깥 띠는 회전.</summary>
    private Cursor TransformCursor(Point screen)
    {
        var (kind, hx, hy) = TransformHitAt(screen);
        switch (kind)
        {
            case TransformHit.Move:
            case TransformHit.Pivot:
                return SizeAllCursor;
            case TransformHit.Rotate:
                return RotateCursor.Value;
            case TransformHit.Scale:
            {
                var t = _ctx.Transform!;
                var p = _ctx.TransformParams;
                var h = TransformHandleScreen(t, p, hx, hy);
                var c = TransformHandleScreen(t, p, 0, 0);
                double dx = h.X - c.X, dy = h.Y - c.Y;
                if (dx * dx + dy * dy < 1e-6) return SizeAllCursor;
                // 화면 y가 아래: 0° = 오른쪽, 45° = 오른쪽 아래(＼)
                double a = Math.Atan2(dy, dx) * 180 / Math.PI;
                a = ((a % 180) + 180) % 180;
                return a < 22.5 || a >= 157.5 ? SizeWeCursor : a < 67.5 ? SizeNwseCursor : a < 112.5 ? SizeNsCursor : SizeNeswCursor;
            }
            default:
                return ArrowCursor;
        }
    }

    /// <summary>변형 모드에서 왼쪽 버튼 누르기: 가리키는 동작의 끌기를 시작했으면 true.</summary>
    private bool BeginTransformDrag(Point screen, int clickCount)
    {
        var (kind, hx, hy) = TransformHitAt(screen);
        _tfStart = _ctx.TransformParams;
        switch (kind)
        {
            case TransformHit.Scale:
                (_tfHx, _tfHy) = (hx, hy);
                _drag = DragMode.TransformScale;
                return true;
            case TransformHit.Pivot:
                if (clickCount >= 2)
                {
                    _ctx.ResetTransformPivot();
                    Message?.Invoke(this, UiStrings.S_PixelCanvas_BeginTransformDrag_CDA1CFC252);
                    return false;
                }
                _drag = DragMode.TransformPivot;
                return true;
            case TransformHit.Move:
                _tfStartImg = ScreenToImageF(screen);
                _drag = DragMode.TransformMove;
                return true;
            case TransformHit.Rotate:
            {
                var (px, py) = _ctx.TransformPivot;
                var m = ScreenToImageF(screen);
                _tfStartAngle = Math.Atan2(m.Y - py, m.X - px);
                _drag = DragMode.TransformRotate;
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>변형 끌기 중 마우스 이동.</summary>
    private void DragTransform(Point screen)
    {
        if (_ctx.Transform is not { } t) return;
        var m = ScreenToImageF(screen);
        bool shift = (_mods & KeyModifiers.Shift) != 0;
        switch (_drag)
        {
            case DragMode.TransformMove:
            {
                // 정수 픽셀 단위로 옮긴다(무손실).
                double dx = Math.Round(m.X - _tfStartImg.X), dy = Math.Round(m.Y - _tfStartImg.Y);
                _ctx.SetTransformParams(_tfStart with { CenterX = _tfStart.CenterX + dx, CenterY = _tfStart.CenterY + dy });
                break;
            }
            case DragMode.TransformRotate:
            {
                var (px, py) = _ctx.TransformPivot;
                double delta = (Math.Atan2(m.Y - py, m.X - px) - _tfStartAngle) * 180 / Math.PI;
                double angle = _tfStart.Angle + delta;
                if (shift) angle = Math.Round(angle / 15) * 15;
                _ctx.SetTransformParams(_ctx.RotatedAboutPivot(_tfStart, angle));
                break;
            }
            case DragMode.TransformPivot:
                // 반 픽셀 단위로 맞춘다(피벗 도구와 같음).
                _ctx.SetTransformPivot(Math.Round(m.X * 2) / 2, Math.Round(m.Y * 2) / 2);
                break;
            case DragMode.TransformScale:
                DragTransformScale(t, m, shift, (_mods & KeyModifiers.Alt) != 0);
                break;
        }
    }

    /// <summary>
    /// 손잡이로 크기 바꾸기: 반대쪽 변(Alt면 가운데)을 제자리에 두고 잡은 변을 마우스로. 모서리는 가로·세로 둘 다, 변은 한 축만.
    /// Shift = 시작 때의 가로세로 비율 유지. 반대쪽을 넘어가면 뒤집힌다. 크기는 정수 픽셀로 맞춘다.
    /// </summary>
    private void DragTransformScale(FreeTransform t, Avalonia.Point m, bool shift, bool alt)
    {
        if (alt) _altUsed = true;   // Alt를 떼도 메뉴가 열리지 않게
        var p0 = _tfStart;
        var src = t.SourceBounds;
        double hw = src.Width / 2.0, hh = src.Height / 2.0;
        var (cx, cy) = t.SourceCenter;
        int ax = alt ? 0 : -_tfHx, ay = alt ? 0 : -_tfHy;
        var anchor = t.Map(p0, cx + ax * hw, cy + ay * hh);
        var (sin, cos) = p0.SinCos();
        double vx = m.X - anchor.X, vy = m.Y - anchor.Y;
        double lx = vx * cos + vy * sin, ly = -vx * sin + vy * cos;
        double sx = p0.ScaleX, sy = p0.ScaleY;
        if (_tfHx != 0) sx = lx / ((_tfHx - ax) * hw);
        if (_tfHy != 0) sy = ly / ((_tfHy - ay) * hh);
        if (shift)
        {
            double kx = sx / p0.ScaleX, ky = sy / p0.ScaleY;
            if (_tfHx != 0 && _tfHy != 0)
            {
                double k = Math.Abs(kx) >= Math.Abs(ky) ? kx : ky;
                (sx, sy) = (p0.ScaleX * k, p0.ScaleY * k);
            }
            else if (_tfHx != 0) sy = p0.ScaleY * Math.Abs(kx);
            else sx = p0.ScaleX * Math.Abs(ky);
        }
        static double Snap(double s, int size)
        {
            double px = Math.Max(1, Math.Round(Math.Abs(s) * size));
            return (s < 0 ? -px : px) / size;
        }
        var next = p0 with { ScaleX = Snap(sx, src.Width), ScaleY = Snap(sy, src.Height) };
        var moved = t.Map(next, cx + ax * hw, cy + ay * hh);
        _ctx.SetTransformParams(next with { CenterX = next.CenterX + anchor.X - moved.X, CenterY = next.CenterY + anchor.Y - moved.Y });
    }

    // ───────────── 캔버스 크기 모드(Ctrl+Alt+C) ─────────────

    /// <summary>새 캔버스와 그림 사각형(보이는 좌표 — 부위 화면이면 부위 위치만큼 옮긴 프레임 좌표).</summary>
    private (Rectangle Canvas, Rectangle Image) CanvasResizeViewRects()
    {
        var o = ShownOrigin;
        var canvas = _ctx.CanvasResizeCanvas;
        canvas.Offset(-o.X, -o.Y);
        var image = _ctx.CanvasResizeImage;
        image.Offset(canvas.X, canvas.Y);
        return (canvas, image);
    }

    /// <summary>캔버스 손잡이(-1/0/1)의 화면 위치.</summary>
    private Avalonia.Point CanvasResizeHandleScreen(Rectangle canvas, int hx, int hy)
        => ImageToScreen(canvas.X + (hx + 1) * canvas.Width / 2.0, canvas.Y + (hy + 1) * canvas.Height / 2.0);

    /// <summary>
    /// 캔버스 크기 모드: 원래 그림 전체(캔버스 밖으로 잘릴 부분 포함)는 흐리게, 새 캔버스 안은 체커 위에 적용될 모습 그대로.
    /// 그림 자리(흰 점선), 캔버스 테두리(하늘색 점선)·손잡이 8개, 위에 크기 글.
    /// </summary>
    private void DrawCanvasResize(DrawingContext g)
    {
        var (canvas, image) = CanvasResizeViewRects();
        var imageScreen = ImageToScreen(image);
        using (ImageUtil.Pixelated(g))
        {
            if (_ctx.FrameBitmap(_ctx.CurrentFrame) is { } source)
                using (g.PushOpacity(0.35))
                    g.DrawImage(source, new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height), ToRect(imageScreen));
            DrawBitmapAt(g, _ctx.CanvasResizeBitmap, canvas.X, canvas.Y, canvas.Width, canvas.Height, checker: true);
        }
        var visible = VisibleSourceRect(canvas.X, canvas.Y, canvas.Width, canvas.Height);
        if (_showPixelGrid && _zoom >= _pixelGridMinZoom && !visible.IsEmpty)
            DrawPixelGrid(g, new Rectangle(visible.X + canvas.X, visible.Y + canvas.Y, visible.Width, visible.Height));
        g.DrawRectangle(null, FrameBoundsPen, new Rect(imageScreen.X + 0.5, imageScreen.Y + 0.5, Math.Max(0, imageScreen.Width - 1), Math.Max(0, imageScreen.Height - 1)));
        // 9-slice로 늘이면 늘어나지 않는 테두리 자리(그림 가장자리에서 같은 픽셀 수)를 초록 점선으로.
        if (_ctx.CanvasResizeUsesSlice && _ctx.CanvasResizeBorder is { IsNone: false } b)
        {
            if (b.Left > 0) DrawSliceGuide(g, image.X + b.Left, image.Y, image.X + b.Left, image.Bottom);
            if (b.Right > 0) DrawSliceGuide(g, image.Right - b.Right, image.Y, image.Right - b.Right, image.Bottom);
            if (b.Top > 0) DrawSliceGuide(g, image.X, image.Y + b.Top, image.Right, image.Y + b.Top);
            if (b.Bottom > 0) DrawSliceGuide(g, image.X, image.Bottom - b.Bottom, image.Right, image.Bottom - b.Bottom);
        }
        var c = ImageToScreen(canvas);
        var border = new Rect(c.X - 0.5, c.Y - 0.5, c.Width + 1, c.Height + 1);
        g.DrawRectangle(null, TransformBackPen, border);
        g.DrawRectangle(null, CanvasResizePen, border);
        foreach (var (hx, hy) in TransformHandles)
        {
            var h = CanvasResizeHandleScreen(canvas, hx, hy);
            g.DrawRectangle(CanvasResizeHandleBrush, CanvasResizeHandlePen, new Rect(Math.Round(h.X) - 4, Math.Round(h.Y) - 4, 8, 8));
        }
        var label = CanvasResizeLabel(canvas.Size, image.Size, _ctx.CanvasResizeSource);
        var size = AppTheme.MeasureText(label, AppTheme.SmallFontSize);
        double ly = c.Y - size.Height - 10 >= 0 ? c.Y - size.Height - 10 : c.Bottom + 8;
        g.FillRectangle(LabelBrush, new Rect(c.X, ly, size.Width + 6, size.Height + 2));
        AppTheme.DrawText(g, label, new Avalonia.Point(c.X + 3, ly + 1), Brushes.White, AppTheme.SmallFontSize);
    }

    /// <summary>캔버스 크기 모드의 9-slice 선 하나(이미지 좌표 픽셀 경계).</summary>
    private void DrawSliceGuide(DrawingContext g, int x0, int y0, int x1, int y1)
    {
        var a = ImageToScreen(x0, y0);
        var b = ImageToScreen(x1, y1);
        g.DrawLine(SliceBorderPen, new Avalonia.Point(Math.Round(a.X) + 0.5, Math.Round(a.Y) + 0.5), new Avalonia.Point(Math.Round(b.X) + 0.5, Math.Round(b.Y) + 0.5));
    }

    /// <summary>크기 글: <c>24×24 · 그림 32×32 (133%)</c>(가로·세로 배율이 다르면 <c>(133%×50%)</c>, 그림 크기가 그대로면 캔버스 크기만).</summary>
    private static string CanvasResizeLabel(Size canvas, Size image, Size source)
    {
        var text = $"{canvas.Width}×{canvas.Height}";
        if (image == source) return text;
        double sx = Math.Round(image.Width * 100.0 / source.Width, 1), sy = Math.Round(image.Height * 100.0 / source.Height, 1);
        var percent = sx == sy ? $"{sx:0.#}%" : $"{sx:0.#}%×{sy:0.#}%";
        return string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PixelCanvas_CanvasResizeLabel_0D4B41243A,text,image.Width,image.Height,percent);
    }

    /// <summary>화면 위치가 가리키는 캔버스 크기 동작: 손잡이(가장 가까운 것, 가장자리) → 그림 안쪽(옮기기) 순. 없으면 <see cref="DragMode.None"/>.</summary>
    private (DragMode Mode, int Hx, int Hy) CanvasResizeHitAt(Point screen)
    {
        var (canvas, image) = CanvasResizeViewRects();
        double best = double.MaxValue;
        (DragMode, int, int) hit = (DragMode.None, 0, 0);
        foreach (var (hx, hy) in TransformHandles)
        {
            var h = CanvasResizeHandleScreen(canvas, hx, hy);
            double dx = Math.Abs(screen.X - h.X), dy = Math.Abs(screen.Y - h.Y);
            if (dx > TransformHandleGrab || dy > TransformHandleGrab || dx + dy >= best) continue;
            best = dx + dy;
            hit = (DragMode.CanvasResizeEdge, hx, hy);
        }
        if (hit.Item1 != DragMode.None) return hit;
        return ImageToScreen(image).Contains(screen) ? (DragMode.CanvasResizeMove, 0, 0) : hit;
    }

    /// <summary>캔버스 크기 모드 커서: 손잡이는 그 방향의 크기 조절 화살표, 그림 안쪽은 옮기기.</summary>
    private Cursor CanvasResizeCursor(Point screen)
    {
        var (mode, hx, hy) = CanvasResizeHitAt(screen);
        return mode switch
        {
            DragMode.CanvasResizeMove => SizeAllCursor,
            DragMode.CanvasResizeEdge => hy == 0 ? SizeWeCursor : hx == 0 ? SizeNsCursor : hx == hy ? SizeNwseCursor : SizeNeswCursor,
            _ => ArrowCursor,
        };
    }

    /// <summary>캔버스 크기 모드에서 왼쪽 버튼 누르기: 손잡이나 그림 안쪽이면 끌기를 시작하고 true.</summary>
    private bool BeginCanvasResizeDrag(Point screen)
    {
        var (mode, hx, hy) = CanvasResizeHitAt(screen);
        if (mode == DragMode.None) return false;
        (_crHx, _crHy) = (hx, hy);
        _crStartImg = ScreenToImageF(screen);
        _crStartCanvas = _ctx.CanvasResizeCanvas;
        _crStartImage = _ctx.CanvasResizeImage;
        _drag = mode;
        return true;
    }

    /// <summary>
    /// 캔버스 크기 끌기(1px 단위): 그림 안쪽이면 그림을 옮기고, 손잡이면 그 캔버스 가장자리만 옮긴다(그림은 제자리 → 여백이 늘거나 잘림).
    /// Ctrl(⌘)이면 그림 가장자리도 함께 움직여 여백은 그대로 두고 그림 크기를 바꾼다(모서리 + Shift = 시작 때 그림 비율 유지). 크기는 1px 이상.
    /// 9-slice로 늘리기가 켜져 있어도 조작은 같고(<see cref="EditorContext.CanvasResizeUsesSlice"/>는 그림을 늘리는 방법만 바꿈), 그때 그림은 테두리 두 쪽 + 1px보다 작아지지 않는다.
    /// </summary>
    private void DragCanvasResize(Point screen)
    {
        var m = ScreenToImageF(screen);
        int dx = (int)Math.Round(m.X - _crStartImg.X), dy = (int)Math.Round(m.Y - _crStartImg.Y);
        Rectangle c = _crStartCanvas, i = _crStartImage;
        if (_drag == DragMode.CanvasResizeMove)
        {
            _ctx.SetCanvasResize(c, new Rectangle(i.X + dx, i.Y + dy, i.Width, i.Height));
            return;
        }
        bool scale = Ui.HasCommand(_mods);
        var min = scale ? _ctx.CanvasResizeMinImage : new System.Drawing.Size(1, 1);
        int multiplier = _ctx.CanvasResizeFromCenter ? 2 : 1;
        int gx = _crHx * dx * multiplier, gy = _crHy * dy * multiplier; // 중심 기준이면 맞은편도 같은 만큼 움직임
        if (scale && (_mods & KeyModifiers.Shift) != 0 && _crHx != 0 && _crHy != 0)
        {
            double kx = (i.Width + gx) / (double)i.Width, ky = (i.Height + gy) / (double)i.Height;
            double k = Math.Abs(kx - 1) >= Math.Abs(ky - 1) ? kx : ky;
            gx = (int)Math.Round(i.Width * k) - i.Width;
            gy = (int)Math.Round(i.Height * k) - i.Height;
        }
        // 캔버스는 1px ~ 최대 크기, 함께 늘이는 그림은 가장 작은 크기 ~ 최대 크기
        gx = Math.Clamp(gx, scale ? Math.Max(1 - c.Width, min.Width - i.Width) : 1 - c.Width, PixelImage.MaxSize - (scale ? Math.Max(c.Width, i.Width) : c.Width));
        gy = Math.Clamp(gy, scale ? Math.Max(1 - c.Height, min.Height - i.Height) : 1 - c.Height, PixelImage.MaxSize - (scale ? Math.Max(c.Height, i.Height) : c.Height));
        if (_ctx.CanvasResizeFromCenter)
        {
            var center = _ctx.CanvasResizeCenter2;
            int centeredX = _crHx == 0 ? c.X : (center.X - c.Width - gx) / 2;
            int centeredY = _crHy == 0 ? c.Y : (center.Y - c.Height - gy) / 2;
            int centeredImageX = i.X + c.X - centeredX - (scale ? gx / 2 : 0);
            int centeredImageY = i.Y + c.Y - centeredY - (scale ? gy / 2 : 0);
            _ctx.SetCanvasResize(new Rectangle(centeredX, centeredY, c.Width + gx, c.Height + gy),
                new Rectangle(centeredImageX, centeredImageY, i.Width + (scale ? gx : 0), i.Height + (scale ? gy : 0)));
            return;
        }
        var (cx, cw, ix, iw) = GrowAxis(_crHx, gx, c.X, c.Width, i.X, i.Width, scale);
        var (cy, ch, iy, ih) = GrowAxis(_crHy, gy, c.Y, c.Height, i.Y, i.Height, scale);
        _ctx.SetCanvasResize(new Rectangle(cx, cy, cw, ch), new Rectangle(ix, iy, iw, ih));
    }

    /// <summary>
    /// 한 축: 잡은 쪽(-1 = 왼쪽·위, 1 = 오른쪽·아래, 0 = 그대로)으로 캔버스를 <paramref name="grow"/>만큼 늘인다. 그림 자리는 새 캔버스 기준.
    /// 왼쪽(위)을 끌면 캔버스 원점이 움직이므로, 제자리인 그림은 그만큼 자리가 바뀌고 함께 늘이는 그림(<paramref name="scale"/>)은 여백이 그대로다.
    /// </summary>
    private static (int Pos, int Size, int ImagePos, int ImageSize) GrowAxis(int hand, int grow, int pos, int size, int imagePos, int imageSize, bool scale)
    {
        if (hand == 0) return (pos, size, imagePos, imageSize);
        return (hand < 0 ? pos - grow : pos, size + grow, hand < 0 && !scale ? imagePos + grow : imagePos, scale ? imageSize + grow : imageSize);
    }

    // ───────────── 커서 ─────────────

    private void UpdateCursor() => UpdateCursor(ScreenToPixel(_mouseScreen));

    private void UpdateCursor(Point px)
    {
        if (!_ctx.HasProject) { Cursor = ArrowCursor; return; }
        if (KeyState.IsDown(Key.Space)) { Cursor = HandCursor; return; }
        if (_ctx.IsCanvasResizing) { Cursor = CanvasResizeCursor(_mouseScreen); return; }
        if (_ctx.IsTransforming) { Cursor = TransformCursor(_mouseScreen); return; }
        if (_ctx.Tool == ToolKind.Slice && SliceHitAt(_mouseScreen) is { } hit)
        {
            Cursor = hit.Edge is SliceEdge.Left or SliceEdge.Right ? SizeWeCursor : SizeNsCursor;
            return;
        }
        if (_ctx.IsGroupSelected)
        {
            Cursor = PartAt(px) is null ? ArrowCursor : SizeAllCursor;
            return;
        }
        if (_ctx.Tool == ToolKind.Slice)
        {
            Cursor = ArrowCursor;
            return;
        }
        if (_ctx.Tool == ToolKind.Picker
            || IsPaintTool(_ctx.Tool) && (KeyState.IsDown(Key.LeftAlt) || KeyState.IsDown(Key.RightAlt))
               && new Rectangle(0, 0, _ctx.CanvasWidth, _ctx.CanvasHeight).Contains(px))
        {
            Cursor = PickerCursor.Value;
            return;
        }
        Cursor = _ctx.Tool switch
        {
            ToolKind.Move => SizeAllCursor,
            ToolKind.Select or ToolKind.EllipseSelect or ToolKind.Lasso when _ctx.Selection.IsInside(px) => SizeAllCursor,
            ToolKind.Pivot => HandCursor,
            _ => CrossCursor,
        };
    }
}
