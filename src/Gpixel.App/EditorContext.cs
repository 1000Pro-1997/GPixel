using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>편집 도구 종류.</summary>
public enum ToolKind
{
    Pencil,
    Eraser,
    Fill,
    /// <summary>선형 그라데이션: 끌어서 시작점 → 끝점(색 정지점·보간·디더링은 도구 설정).</summary>
    Gradient,
    Picker,
    /// <summary>도형: 선·사각형·원·삼각형·정다각형·별·나선·원기둥·주사위(모양과 개수는 도구 설정, <see cref="EditorContext.ShapeStyle"/>).</summary>
    Shape,
    /// <summary>사각 선택.</summary>
    Select,
    /// <summary>원 선택(Shift = 정원).</summary>
    EllipseSelect,
    /// <summary>자유 선택(올가미): 끌어 그린 닫힌 모양.</summary>
    Lasso,
    /// <summary>스마트 선택(마술봉): 클릭한 곳과 비슷한 색 영역(채우기 설정을 같이 씀).</summary>
    MagicWand,
    Move,
    /// <summary>밝게(왼쪽)/어둡게(오른쪽) 브러시.</summary>
    Dodge,
    /// <summary>체커 무늬 디더링 브러시(기본색·보조색 번갈아).</summary>
    Dither,
    /// <summary>처음 닿은 색만 바꾸는 색 바꾸기 브러시.</summary>
    ReplaceBrush,
    /// <summary>피벗 점 끌기.</summary>
    Pivot,
    /// <summary>9-slice 테두리(Unity Sprite Border) 초록 선 끌기.</summary>
    Slice,
}

/// <summary>
/// 편집기 전체가 함께 쓰는 상태: 열린 프로젝트, 현재 레이어·프레임, 선택 영역, 도구·색, 합성 비트맵 캐시.
/// 패널들은 이 객체의 이벤트로 서로 동기화된다.
/// <para>
/// 타임라인의 열은 항상 프레임이다. 현재 레이어가 부위면 캔버스 크기(<see cref="CanvasWidth"/>)·그리기 대상(<see cref="EditImage"/>)·
/// 피벗은 그 부위 기준(부위 안 좌표)이고, 그룹이면 프레임 합성을 보며 부위를 배치한다(그리기 없음).
/// </para>
/// </summary>
public sealed class EditorContext
{
    public const int MaxRecentColors = 16;

    private ToolKind _tool = ToolKind.Pencil;
    private Color32 _primary = Color32.Black;
    private Color32 _secondary = Color32.Transparent;
    private int _brushSize = 1;
    private bool _selectionStrokeOpen;
    private int _layer, _frame;
    private Size _canvasSize;
    private bool _onionSkin;
    private AnimationTag? _selectedTag;
    private Pivot? _pivotPreview;
    private PixelImage? _blank;
    private PixelImage? _strokeImage;
    private WriteableBitmap? _blankBitmap;

    // 프레임 합성·부위 하나 뺀 합성·셀(그룹이면 그룹 합성)·타임라인 썸네일 결과(가벼우면 바로, 무거우면 작업 스레드에서 계산)
    private readonly RenderService _renders = new();
    // 무효화를 레이어 단위로 하려고 기억해 두는 레이어별 그리기 상태와 프레임 차례
    private LayerRenderState[] _layerStates = [];
    private Frame[] _frameOrder = [];

    public SpriteProject? Project { get; private set; }
    public bool HasProject => Project is not null;
    public Selection Selection { get; } = new();

    /// <summary>복사한 픽셀(앱 내부 클립보드).</summary>
    public PixelImage? Clipboard { get; set; }

    public EditorContext()
    {
        // 선택 영역 떼기·내려놓기는 셀 픽셀을 직접 바꾼다.
        Selection.Changed += (_, _) => InvalidateCurrent();
        _renders.Updated += () => RenderUpdated?.Invoke(this, EventArgs.Empty);
        // 스크립트 효과: 실행 허락·멈춤 해제로 같은 효과 값의 결과가 달라지면 다시 그리고, 실패 표시만 바뀌면 화면만 다시 그린다(작업 스레드에서도 옴).
        ScriptLibrary.Changed += () => Dispatcher.UIThread.Post(RerenderScripts);
        ScriptEffectRuntime.StatusChanged += () => Dispatcher.UIThread.Post(() => ViewChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>다른 프로젝트가 열리거나 닫힘.</summary>
    public event EventHandler? ProjectReplaced;
    /// <summary>현재 프로젝트 내용이 바뀜.</summary>
    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;
    /// <summary>캔버스·미리보기·타임라인을 다시 그려야 함(내용 변경, 현재 프레임/레이어 변경 등).</summary>
    public event EventHandler? ViewChanged;
    /// <summary>현재 레이어·프레임·선택한 태그가 바뀜.</summary>
    public event EventHandler? CurrentChanged;
    public event EventHandler? DirtyChanged;
    public event EventHandler? ToolChanged;
    public event EventHandler? ToolOptionsChanged;
    public event EventHandler? ColorsChanged;
    public event EventHandler? RecentColorsChanged;
    public event EventHandler? OnionSkinChanged;
    public event EventHandler? UndoChanged;
    /// <summary>작업 스레드에서 계산하던 합성 결과·중간 그림이 왔거나 회색 덮개를 씌울 때(캔버스·타임라인·미리보기 다시 그리기).</summary>
    public event EventHandler? RenderUpdated;

    // ───────────── 도구·색 ─────────────

    public ToolKind Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            // 변형·캔버스 크기 모드면 어떤 도구로 바꾸든 먼저 적용한다.
            if (IsTransforming || IsCanvasResizing || (!IsSelectionTool(value) && value != ToolKind.Move)) CommitFloating();
            _tool = value;
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>선택 영역을 만드는 도구(사각·원·자유·스마트 선택): 떠 있는 선택을 유지하고 안쪽을 끌거나 방향키로 옮긴다.</summary>
    public static bool IsSelectionTool(ToolKind t)
        => t is ToolKind.Select or ToolKind.EllipseSelect or ToolKind.Lasso or ToolKind.MagicWand;

    public Color32 Primary
    {
        get => _primary;
        set { if (_primary == value) return; _primary = value; ColorsChanged?.Invoke(this, EventArgs.Empty); }
    }

    public Color32 Secondary
    {
        get => _secondary;
        set { if (_secondary == value) return; _secondary = value; ColorsChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void SwapColors()
    {
        (_primary, _secondary) = (_secondary, _primary);
        ColorsChanged?.Invoke(this, EventArgs.Empty);
    }

    public List<Color32> RecentColors { get; } = new();

    /// <summary>그리기에 쓴 색을 최근 색 맨 앞에 넣는다.</summary>
    public void PushRecentColor(Color32 c)
    {
        if (RecentColors.Count > 0 && RecentColors[0] == c) return;
        RecentColors.Remove(c);
        RecentColors.Insert(0, c);
        if (RecentColors.Count > MaxRecentColors) RecentColors.RemoveRange(MaxRecentColors, RecentColors.Count - MaxRecentColors);
        RecentColorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>브러시 크기(1~<see cref="BrushFootprint.MaxSize"/>): 연필·지우개·밝게/어둡게·디더링·색 바꾸기 브러시, 선·도형 외곽 두께.</summary>
    public int BrushSize
    {
        get => _brushSize;
        set { value = Math.Clamp(value, 1, BrushFootprint.MaxSize); if (_brushSize == value) return; _brushSize = value; OptionsChanged(); }
    }

    private bool _pixelPerfect, _fillContiguous = true, _shapeFilled, _mirrorX, _mirrorY, _rightErases, _moveWrap = true;
    private bool _fillDiagonal, _fillSampleVisible, _pickerSampleVisible = true;
    private BrushShape _brushShape = BrushShape.Square;
    private int _fillTolerance, _dodgeStrength = DefaultDodgeStrength;
    private DitherPattern _ditherPattern = DitherPattern.Checker;

    public const int DefaultDodgeStrength = 10;
    public const int MaxDodgeStrength = 50;

    /// <summary>브러시 모양(사각·원·마름모). 크기 1은 모양과 관계없이 한 점.</summary>
    public BrushShape BrushShape
    {
        get => _brushShape;
        set { if (!Enum.IsDefined(value)) value = BrushShape.Square; if (_brushShape == value) return; _brushShape = value; OptionsChanged(); }
    }

    /// <summary>현재 브러시 도장(모양·크기).</summary>
    public BrushFootprint Brush => BrushFootprint.Get(_brushShape, _brushSize);

    public bool PixelPerfect { get => _pixelPerfect; set { _pixelPerfect = value; OptionsChanged(); } }
    /// <summary>채우기: true = 이어진 영역만, false = 맞는 색 전체.</summary>
    public bool FillContiguous { get => _fillContiguous; set { _fillContiguous = value; OptionsChanged(); } }
    /// <summary>채우기 허용 오차(0~100%, 0 = 정확히 같은 색만). 채널 차이 최댓값 기준(<see cref="Raster.Matches"/>).</summary>
    public int FillTolerance
    {
        get => _fillTolerance;
        set { value = Math.Clamp(value, 0, 100); if (_fillTolerance == value) return; _fillTolerance = value; OptionsChanged(); }
    }
    /// <summary><see cref="FillTolerance"/>(%)를 채널 단위(0~255)로.</summary>
    public int FillToleranceChannels => (int)Math.Round(_fillTolerance * 255 / 100.0);
    /// <summary>채우기: 이어진 영역을 대각선으로도 잇는다(8방향).</summary>
    public bool FillDiagonal { get => _fillDiagonal; set { _fillDiagonal = value; OptionsChanged(); } }
    /// <summary>채우기 영역을 보이는 레이어 합성으로 정한다(칠하기는 현재 레이어). false = 현재 레이어.</summary>
    public bool FillSampleVisible { get => _fillSampleVisible; set { _fillSampleVisible = value; OptionsChanged(); } }
    /// <summary>스포이드(임시 스포이드 포함): 보이는 레이어 합성에서 집는다. false = 현재 레이어에서.</summary>
    public bool PickerSampleVisible { get => _pickerSampleVisible; set { _pickerSampleVisible = value; OptionsChanged(); } }
    /// <summary>밝게/어둡게 한 번의 명도 변화(1~50%).</summary>
    public int DodgeStrength
    {
        get => _dodgeStrength;
        set { value = Math.Clamp(value, 1, MaxDodgeStrength); if (_dodgeStrength == value) return; _dodgeStrength = value; OptionsChanged(); }
    }
    /// <summary>디더링 무늬.</summary>
    public DitherPattern DitherPattern
    {
        get => _ditherPattern;
        set { if (!Enum.IsDefined(value)) value = DitherPattern.Checker; if (_ditherPattern == value) return; _ditherPattern = value; OptionsChanged(); }
    }
    /// <summary>도형: 채움(true) / 외곽(false). 나선은 늘 선(<see cref="Shapes.CanFill"/>).</summary>
    public bool ShapeFilled { get => _shapeFilled; set { _shapeFilled = value; OptionsChanged(); } }

    private ShapeKind _shapeKind = ShapeKind.Rectangle;
    private int _shapeSides = 5, _starPoints = 5, _spiralTurns = 3;

    /// <summary>도형 도구가 그리는 모양.</summary>
    public ShapeKind ShapeKind
    {
        get => _shapeKind;
        set { if (!Enum.IsDefined(value)) value = ShapeKind.Rectangle; if (_shapeKind == value) return; _shapeKind = value; OptionsChanged(); }
    }
    /// <summary>정다각형 꼭짓점 수(<see cref="Shapes.MinSides"/>~<see cref="Shapes.MaxSides"/>).</summary>
    public int ShapeSides
    {
        get => _shapeSides;
        set { value = Math.Clamp(value, Shapes.MinSides, Shapes.MaxSides); if (_shapeSides == value) return; _shapeSides = value; OptionsChanged(); }
    }
    /// <summary>별 뿔 수(<see cref="Shapes.MinSides"/>~<see cref="Shapes.MaxSides"/>).</summary>
    public int StarPoints
    {
        get => _starPoints;
        set { value = Math.Clamp(value, Shapes.MinSides, Shapes.MaxSides); if (_starPoints == value) return; _starPoints = value; OptionsChanged(); }
    }
    /// <summary>나선 감은 수(<see cref="Shapes.MinTurns"/>~<see cref="Shapes.MaxTurns"/>).</summary>
    public int SpiralTurns
    {
        get => _spiralTurns;
        set { value = Math.Clamp(value, Shapes.MinTurns, Shapes.MaxTurns); if (_spiralTurns == value) return; _spiralTurns = value; OptionsChanged(); }
    }
    private bool _shapeOrthogonal;
    /// <summary>선·곡선 픽셀 잇기: true = ㄱ자(4방향, 모든 픽셀이 변으로 붙음), false = 대각선으로도 이음.</summary>
    public bool ShapeOrthogonal { get => _shapeOrthogonal; set { if (_shapeOrthogonal == value) return; _shapeOrthogonal = value; OptionsChanged(); } }
    /// <summary>지금 도형 설정(모양·개수·선 잇기).</summary>
    public ShapeStyle ShapeStyle => new(_shapeKind, _shapeSides, _starPoints, _spiralTurns, _shapeOrthogonal);
    /// <summary>대칭 그리기: 좌우(세로 축 기준 반전).</summary>
    public bool MirrorX { get => _mirrorX; set { _mirrorX = value; OptionsChanged(); } }
    /// <summary>대칭 그리기: 상하.</summary>
    public bool MirrorY { get => _mirrorY; set { _mirrorY = value; OptionsChanged(); } }
    /// <summary>오른쪽 버튼으로 보조색 대신 지운다(연필·도형·채우기).</summary>
    public bool RightButtonErases { get => _rightErases; set { _rightErases = value; OptionsChanged(); } }
    /// <summary>이동 도구로 셀 내용을 옮길 때 밖으로 나간 픽셀을 반대편으로 돌린다.</summary>
    public bool MoveWrap { get => _moveWrap; set { _moveWrap = value; OptionsChanged(); } }

    /// <summary>그라데이션 기본 정지점(검정 → 흰색).</summary>
    public static readonly IReadOnlyList<GradientStop> DefaultGradientStops = [new(0, Color32.Black), new(1, Color32.White)];
    private IReadOnlyList<GradientStop> _gradientStops = DefaultGradientStops;
    private GradientInterpolation _gradientInterpolation = GradientInterpolation.Smooth;
    private GradientDither _gradientDither = GradientDither.None;
    private GradientColorSpace _gradientColorSpace = GradientColorSpace.OkLch;

    /// <summary>그라데이션 색 정지점(2개 이상, 위치 0~1). 편집 순서를 그대로 두고 칠할 때 위치 순으로 쓴다.</summary>
    public IReadOnlyList<GradientStop> GradientStops
    {
        get => _gradientStops;
        set
        {
            if (value.Count < 2) throw new ArgumentException(UiStrings.S_EditorContext_GradientStops_B89D363377, nameof(value));
            _gradientStops = value.Select(s => s with { Position = Math.Clamp(s.Position, 0, 1) }).ToArray();
            OptionsChanged();
        }
    }

    /// <summary>그라데이션 기본 불투명도 정지점(처음·끝 모두 100%).</summary>
    public static readonly IReadOnlyList<GradientAlphaStop> DefaultGradientAlphaStops = [new(0, 255), new(1, 255)];
    private IReadOnlyList<GradientAlphaStop> _gradientAlphaStops = DefaultGradientAlphaStops;

    /// <summary>그라데이션 불투명도 정지점(2개 이상, 위치 0~1, 색 정지점과 따로). 칠할 때 색 알파에 곱한다.</summary>
    public IReadOnlyList<GradientAlphaStop> GradientAlphaStops
    {
        get => _gradientAlphaStops;
        set
        {
            if (value.Count < 2) throw new ArgumentException(UiStrings.S_EditorContext_GradientAlphaStops_AB19024421, nameof(value));
            _gradientAlphaStops = value.Select(s => s with { Position = Math.Clamp(s.Position, 0, 1) }).ToArray();
            OptionsChanged();
        }
    }
    /// <summary>그라데이션 보간: 부드럽게 / 단계(정지점 색 띠).</summary>
    public GradientInterpolation GradientInterpolation
    {
        get => _gradientInterpolation;
        set { if (!Enum.IsDefined(value)) value = GradientInterpolation.Smooth; if (_gradientInterpolation == value) return; _gradientInterpolation = value; OptionsChanged(); }
    }
    /// <summary>그라데이션 색 정지점 사이의 보간 색 공간.</summary>
    public GradientColorSpace GradientColorSpace
    {
        get => _gradientColorSpace;
        set { if (!Enum.IsDefined(value)) value = GradientColorSpace.OkLch; if (_gradientColorSpace == value) return; _gradientColorSpace = value; OptionsChanged(); }
    }
    /// <summary>그라데이션 순서 디더링(부드럽게일 때만 씀).</summary>
    public GradientDither GradientDither
    {
        get => _gradientDither;
        set { if (!Enum.IsDefined(value)) value = GradientDither.None; if (_gradientDither == value) return; _gradientDither = value; OptionsChanged(); }
    }

    private void OptionsChanged() => ToolOptionsChanged?.Invoke(this, EventArgs.Empty);

    public bool OnionSkin
    {
        get => _onionSkin;
        set { if (_onionSkin == value) return; _onionSkin = value; OnionSkinChanged?.Invoke(this, EventArgs.Empty); }
    }

    // ───────────── 프로젝트 ─────────────

    /// <summary>캔버스(그리는 그림) 가로: 부위를 고르면 그 부위 크기, 아니면 프로젝트 캔버스.</summary>
    public int CanvasWidth => CurrentPart?.PartSize.Width ?? Project?.Width ?? 0;
    public int CanvasHeight => CurrentPart?.PartSize.Height ?? Project?.Height ?? 0;
    public int FrameCount => Project?.Frames.Count ?? 0;
    public int LayerCount => Project?.Layers.Count ?? 0;

    /// <summary>현재 레이어 번호(0 = 맨 아래).</summary>
    public int CurrentLayer => _layer;
    public int CurrentFrame => _frame;

    /// <summary>현재 레이어 객체(프로젝트가 없으면 null).</summary>
    public Layer? CurrentLayerObject
        => Project is { } p && p.Layers.Count > 0 ? p.Layers[Math.Clamp(_layer, 0, p.Layers.Count - 1)] : null;

    /// <summary>현재 레이어가 부위면 그 부위, 아니면 null.</summary>
    public Layer? CurrentPart => CurrentLayerObject is { IsPart: true } l ? l : null;

    /// <summary>현재 레이어가 그룹인지(캔버스는 부위 배치 모드).</summary>
    public bool IsGroupSelected => CurrentLayerObject?.IsGroup == true;

    /// <summary>고른 그룹, 또는 고른 부위가 속한 그룹(없으면 null).</summary>
    public Layer? CurrentGroup => CurrentLayerObject switch
    {
        { IsGroup: true } g => g,
        { IsPart: true } part => part.Parent,
        _ => null,
    };

    private Size CurrentCanvasSize => new(CanvasWidth, CanvasHeight);

    /// <summary>실행취소 단계 수(설정). 바꾸면 열린 프로젝트에도 바로 적용한다.</summary>
    public int UndoCapacity
    {
        get => _undoCapacity;
        set
        {
            _undoCapacity = Math.Clamp(value, 1, UndoStack.MaxCapacity);
            if (Project is not null) Project.History.Capacity = _undoCapacity;
        }
    }
    private int _undoCapacity = UndoStack.DefaultCapacity;

    public void SetProject(SpriteProject? project)
    {
        if (Project is not null)
        {
            CommitFloating();
            Project.Changed -= OnProjectChanged;
            Project.DirtyChanged -= OnProjectDirty;
            Project.History.Changed -= OnUndoChanged;
        }
        Selection.Deselect(_blank ?? new PixelImage(1, 1));
        _selectionStrokeOpen = false;
        _strokeImage = null;
        _renders.Reset();
        _renders.CheckpointLayer = null;
        Project = project;
        RememberRenderState(project);
        _layer = project is null ? 0 : project.Layers.Count - 1;
        _frame = 0;
        _canvasSize = CurrentCanvasSize;
        _selectedTag = null;
        _pivotPreview = null;
        if (project is not null)
        {
            project.History.Capacity = UndoCapacity;
            project.Changed += OnProjectChanged;
            project.DirtyChanged += OnProjectDirty;
            project.History.Changed += OnUndoChanged;
        }
        ProjectReplaced?.Invoke(this, EventArgs.Empty);
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        UndoChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnUndoChanged(object? sender, EventArgs e) => UndoChanged?.Invoke(this, EventArgs.Empty);

    private void OnProjectDirty(object? sender, EventArgs e) => DirtyChanged?.Invoke(this, EventArgs.Empty);

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        var p = Project!;
        // 스트로크 중에는 현재 셀만 바뀐다. 그 밖의 변경은 바뀐 것을 보고 결과를 무효로 한다(효과만 바뀐 레이어면 그 레이어가 들어간 결과만).
        // 결과 키(레이어, 프레임)는 그룹의 지금 스킨 기준이므로 스킨이 바뀌면(Layers | Pixels) 모두 무효로 한다.
        if (e.Change == ProjectChange.Pixels && p.IsStrokeActive) InvalidateCurrent(stroking: true);
        else if ((e.Change & ~ProjectChange.Path & ~ProjectChange.Settings) != 0)
        {
            InvalidateRenders(p, e.Change);
            SyncCanvasResize();
        }
        int layer = Math.Clamp(_layer, 0, p.Layers.Count - 1), frame = Math.Clamp(_frame, 0, p.Frames.Count - 1);
        bool currentChanged = layer != _layer || frame != _frame;
        _layer = layer;
        _frame = frame;
        if (_selectedTag is not null && !p.Tags.Contains(_selectedTag)) { _selectedTag = null; currentChanged = true; }
        var size = CurrentCanvasSize;
        if ((e.Change & ProjectChange.Canvas) != 0 || size != _canvasSize)
        {
            _canvasSize = size;
            Selection.Deselect(EditImageOrEmpty);
            _selectionStrokeOpen = false;
        }
        ProjectChanged?.Invoke(this, e);
        if (currentChanged) CurrentChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 현재 레이어·프레임을 바꾼다(떠 있는 선택은 먼저 내려놓음). 캔버스 크기가 다른 레이어(부위 ↔ 프레임, 크기가 다른 부위)로
    /// 바뀌면 선택 영역은 해제된다.
    /// </summary>
    public void SetCurrent(int layer, int frame)
    {
        if (Project is not { } p) return;
        layer = Math.Clamp(layer, 0, p.Layers.Count - 1);
        frame = Math.Clamp(frame, 0, p.Frames.Count - 1);
        if (layer == _layer && frame == _frame) return;
        CommitFloating();
        if (layer != _layer)
        {
            var next = p.Layers[layer];
            var size = next.IsPart ? next.PartSize : p.Size;
            if (size != _canvasSize)
            {
                Selection.Deselect(EditImageOrEmpty);
                _selectionStrokeOpen = false;
            }
            _pivotPreview = null;
        }
        _layer = layer;
        _frame = frame;
        _canvasSize = CurrentCanvasSize;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCurrentFrame(int frame) => SetCurrent(_layer, frame);

    public void SetCurrentLayer(int layer) => SetCurrent(layer, _frame);

    /// <summary>타임라인에서 고른 태그(재생·미리보기 대상).</summary>
    public AnimationTag? SelectedTag
    {
        get => _selectedTag;
        set
        {
            if (ReferenceEquals(_selectedTag, value)) return;
            _selectedTag = value;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>재생할 태그: 고른 태그가 현재 프레임을 포함하면 그것, 아니면 현재 프레임이 속한 첫 태그.</summary>
    public AnimationTag? ActiveTag
        => Project is null ? null
            : _selectedTag is { } t && Project.Tags.Contains(t) && t.Contains(_frame) ? t : Project.TagOfFrame(_frame);

    /// <summary>재생 순서(태그가 있으면 그 방향대로, 없으면 모든 프레임).</summary>
    public List<int> PlaybackOrder() => ActiveTag?.GetPlaybackOrder() ?? Enumerable.Range(0, FrameCount).ToList();

    public bool PlaybackLoops => ActiveTag?.Loop ?? true;

    public int FrameDuration(int frame) => Project is { } p && frame >= 0 && frame < p.Frames.Count ? p.Frames[frame].DurationMs : 100;

    // ───────────── 그리기 대상 ─────────────

    /// <summary>현재 레이어가 그룹이 아니고 잠기지 않았으면(그룹 포함) true. 숨긴 레이어에도 그릴 수 있다.</summary>
    public bool CanEdit
        => Project is { } p && !p.Layers[_layer].IsGroup && !p.IsLayerLocked(_layer);

    /// <summary>현재 셀 이미지(스트로크 중이면 그 이미지, 부위면 부위 크기, 그룹·빈 셀이면 null).</summary>
    public PixelImage? EditImage => _strokeImage ?? Project?.GetCel(_layer, _frame);

    /// <summary>선택 영역 계산용: 현재 셀이 없으면 캔버스 크기의 빈 이미지.</summary>
    public PixelImage EditImageOrEmpty
    {
        get
        {
            if (EditImage is { } img) return img;
            if (_blank is null || _blank.Width != CanvasWidth || _blank.Height != CanvasHeight)
                _blank = new PixelImage(Math.Max(1, CanvasWidth), Math.Max(1, CanvasHeight));
            return _blank;
        }
    }

    /// <summary>현재 셀에 그리기 시작(빈 셀이면 새로 만듦). 그룹에는 그릴 수 없다.</summary>
    public void BeginStroke()
    {
        if (Project is not { } p || p.Layers[_layer].IsGroup) return;
        _strokeImage = p.BeginCelStroke(_layer, _frame);
    }
    /// <summary>스트로크 확정(바뀐 것이 없으면 기록 안 함).</summary>
    public void CommitStroke(string label)
    {
        if (Project is null) return;
        _strokeImage = null;
        Project.CommitCelStroke(label);
    }

    /// <summary>스트로크 취소: 셀을 시작 전으로 되돌리고 기록하지 않는다.</summary>
    public void CancelStroke()
    {
        if (Project is null) return;
        _strokeImage = null;
        Project.CancelCelStroke();
        InvalidateCurrent();
    }

    /// <summary>
    /// 타임라인에서 고른 셀들(레이어·프레임 번호) — 변형(Ctrl+T)·이동 도구가 지금 셀과 함께 바꿀 셀. 창이 타임라인 선택에 잇는다(null이면 지금 셀만).
    /// </summary>
    public Func<IReadOnlyList<(int Layer, int Frame)>>? SelectedCels { get; set; }

    /// <summary>
    /// 이동 도구로 지금 셀 전체를 (<paramref name="dx"/>, <paramref name="dy"/>) 민 스트로크를 확정한다. 타임라인에서 다른 셀도 함께 골라 두었으면
    /// 그 셀들도 같은 만큼 밀어 실행취소 한 단계로 묶는다(<paramref name="wrap"/>이면 밖으로 나간 픽셀이 반대쪽으로; 그룹·빈 셀·잠긴 레이어는 건너뜀).
    /// 함께 민 다른 셀 수를 돌려준다.
    /// </summary>
    public int CommitCelShift(string label, int dx, int dy, bool wrap)
    {
        if (Project is not { } p) return 0;
        var others = (SelectedCels?.Invoke() ?? []).Where(c => c.Layer != _layer || c.Frame != _frame).Distinct().ToList();
        if (others.Count == 0 || (dx == 0 && dy == 0))
        {
            CommitStroke(label);
            return 0;
        }
        int layer = _layer, frame = _frame;
        var after = TakeStrokeResult(p);
        var moved = new List<(int Layer, int Frame, PixelImage Image)>();
        foreach (var (l, f) in others)
        {
            if (l < 0 || l >= p.Layers.Count || f < 0 || f >= p.Frames.Count || p.Layers[l].IsGroup || p.IsLayerLocked(l) || p.IsCelEmpty(l, f)) continue;
            var cel = p.GetCel(l, f)!;
            var image = new PixelImage(cel.Width, cel.Height);
            PixelOps.Shift(cel, image, cel.Bounds, dx, dy, wrap);
            moved.Add((l, f, image));
        }
        if (after is null && moved.Count == 0) return 0;
        p.Edit(label, ProjectChange.Pixels, () =>
        {
            if (after is not null) p.SetCel(layer, frame, after);
            foreach (var (l, f, image) in moved) p.SetCel(l, f, image);
        });
        return moved.Count;
    }

    /// <summary>스트로크 중 픽셀이 바뀌었음을 알린다(다시 그리기).</summary>
    public void NotifyPixels()
    {
        if (Project is null) return;
        InvalidateCurrent(stroking: Project.IsStrokeActive);
        Project.NotifyPixelsChanged();
    }

    // ───────────── 선택 영역 편집(실행취소 묶음) ─────────────

    /// <summary>
    /// 선택 영역을 바꾸는 동작 전에 부른다: 아직 열린 기록이 없으면 현재 셀 스트로크를 시작한다.
    /// 변형·캔버스 크기 모드면 다른 편집이므로 먼저 적용한다.
    /// </summary>
    public void BeginSelectionEdit()
    {
        if (Project is null) return;
        if (IsTransforming || IsCanvasResizing) CommitFloating();
        if (_selectionStrokeOpen) return;
        BeginStroke();
        _selectionStrokeOpen = true;
    }

    /// <summary>
    /// 떠 있는 픽셀을 내려놓고 열린 선택 편집 기록을 확정한다. 변형 중이면 변형을 적용한 뒤 내려놓고(실행취소 한 단계),
    /// 캔버스 크기 모드면 그것을 적용한다(실행취소 한 단계).
    /// </summary>
    public void CommitFloating()
    {
        if (Project is not { } p) return;
        ApplyCanvasResize();
        var transform = _transform;
        _transform = null;
        if (Selection.IsFloating && _strokeImage is { } img) Selection.Anchor(img);
        if (transform?.Multi is { } multi) CommitMultiTransform(p, transform, multi);
        else if (_selectionStrokeOpen)
        {
            _selectionStrokeOpen = false;
            CommitStroke(transform is not null ? UiStrings.S_EditorContext_CommitFloating_19E799222E : UiStrings.S_EditorContext_CommitFloating_9E9E894C6C);
        }
        if (transform is not null) TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 여러 셀 변형 적용: 지금 셀(선택 편집 기록 안에서 내려놓은 결과)과 다른 셀들을 실행취소 한 단계로 — 열린 기록은 되돌리고
    /// 지금 셀의 결과 그림을 다른 셀들과 같은 편집 안에서 넣는다.
    /// </summary>
    private void CommitMultiTransform(SpriteProject p, TransformSession t, MultiCelTransform multi)
    {
        int layer = _layer, frame = _frame;
        PixelImage? after = null;
        if (_selectionStrokeOpen)
        {
            _selectionStrokeOpen = false;
            after = TakeStrokeResult(p);
        }
        multi.Commit(UiStrings.S_EditorContext_CommitFloating_19E799222E, t.Source, t.Params, _transformResample,
            after is null ? null : () => p.SetCel(layer, frame, after));
    }

    /// <summary>
    /// 열린 스트로크를 기록 없이 되돌리고 지금 셀이 스트로크로 바뀐 그림(바뀐 것이 없으면 null)을 돌려준다 — 다른 셀들과 한 편집(실행취소 한 단계)으로
    /// 묶어 <see cref="SpriteProject.SetCel"/>로 다시 넣을 때.
    /// </summary>
    private PixelImage? TakeStrokeResult(SpriteProject p)
    {
        var after = _strokeImage?.Clone();
        _strokeImage = null;
        p.CancelCelStroke();
        if (after is null) return null;
        var before = p.GetCel(_layer, _frame);
        return (before is null ? after.GetOpaqueBounds().IsEmpty : after.ContentEquals(before)) ? null : after;
    }

    // ───────────── 자유 변형 ─────────────

    /// <summary>
    /// 변형 중인 상태: 손잡이 기준 원본(<paramref name="source"/>), 지금 셀에서 떼어 낸 원본(<paramref name="lifted"/> — 여러 셀이 아니면 손잡이 기준과 같음,
    /// 지금 셀이 비었으면 null), 함께 변형할 다른 셀들(<paramref name="multi"/>), 지금 값, 시작 전 선택 상태(취소용).
    /// </summary>
    private sealed class TransformSession(FreeTransform source, FreeTransform? lifted, MultiCelTransform? multi, SelectionState before, PixelImage? celBefore)
    {
        /// <summary>손잡이·값의 기준 원본: 한 셀이면 떼어 낸 픽셀, 여러 셀이면 모든 셀의 원본을 감싸는 사각형.</summary>
        public FreeTransform Source { get; } = source;
        /// <summary>지금 셀에서 떼어 낸 원본(캔버스에 떠 있는 그림). 지금 셀에 뗄 것이 없으면 null.</summary>
        public FreeTransform? Lifted { get; } = lifted;
        /// <summary>타임라인에서 함께 고른 다른 셀들(같은 행렬로 적용 때 한꺼번에 바뀜). 한 셀이면 null.</summary>
        public MultiCelTransform? Multi { get; } = multi;
        /// <summary>시작 전 선택 상태(영역·모양·떠 있는 픽셀).</summary>
        public SelectionState Before { get; } = before;
        /// <summary>이미 열린 선택 편집 기록 안에서 시작했으면 그때의 셀 픽셀(취소 때 되돌림). 변형이 기록을 열었으면 null(기록째 취소).</summary>
        public PixelImage? CelBefore { get; } = celBefore;
        public TransformParams Params { get; set; } = source.Identity;
        /// <summary>회전 중심(원본 좌표 — 변형을 따라 움직인다).</summary>
        public (double X, double Y) PivotSource { get; set; } = source.SourceCenter;
    }

    private TransformSession? _transform;
    private ResampleMode _transformResample = ResampleMode.RotSprite;

    /// <summary>변형 모드에 들어가거나 나옴, 변형 값·회전 중심·리샘플링이 바뀜.</summary>
    public event EventHandler? TransformChanged;

    /// <summary>변형 모드(Ctrl+T)인지.</summary>
    public bool IsTransforming => _transform is not null;

    /// <summary>변형 원본(변형 중이 아니면 null).</summary>
    public FreeTransform? Transform => _transform?.Source;

    /// <summary>지금 변형 값(변형 중이 아니면 기본값).</summary>
    public TransformParams TransformParams => _transform?.Params ?? default;

    /// <summary>변형 리샘플링(기본 RotSprite). 변형 중에 바꾸면 바로 다시 그린다.</summary>
    public ResampleMode TransformResample
    {
        get => _transformResample;
        set
        {
            if (!Enum.IsDefined(value)) value = ResampleMode.RotSprite;
            if (_transformResample == value) return;
            _transformResample = value;
            if (_transform is not null) RenderTransform();
            else TransformChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>회전 중심(캔버스 연속 좌표). 변형 중이 아니면 (0,0).</summary>
    public (double X, double Y) TransformPivot
        => _transform is { } t ? t.Source.Map(t.Params, t.PivotSource.X, t.PivotSource.Y) : (0, 0);

    /// <summary>
    /// 변형 모드에 들어간다: 선택 영역(없으면 현재 셀의 칠한 픽셀 전체)을 떼어 원본으로 삼는다. 회전 중심은 처음에 대상의 피벗 자리.
    /// <paramref name="cels"/>(함께 바꿀 셀들, 레이어·프레임 번호 — null이면 <see cref="SelectedCels"/>)에 지금 셀 말고 다른 셀이 있으면 그 셀들도 같은 영역(선택이 없으면 셀마다 칠한 픽셀 전체)을
    /// 떼어 함께 변형한다: 손잡이는 모든 원본을 감싸는 사각형, 캔버스에는 지금 셀이 떠서 움직이고, 적용하면 모든 셀이 같은 행렬로 바뀐다(실행취소 한 단계).
    /// 빈 셀·잠긴 레이어·그룹은 건너뛴다(<see cref="TransformLockedSkipped"/>).
    /// 못 들어가면 상태 표시줄에 보일 이유, 들어갔으면(이미 변형 중이어도) null.
    /// </summary>
    public string? BeginTransform(IReadOnlyList<(int Layer, int Frame)>? cels = null)
    {
        if (Project is not { } p) return UiStrings.S_EditorContext_BeginTransform_E708BAFBC9;
        if (_transform is not null) return null;
        if (IsGroupSelected) return UiStrings.S_EditorContext_BeginTransform_10405489AE;
        // 캔버스 크기 모드면 먼저 적용한다(캔버스가 바뀌면 선택이 풀리므로 시작 전 선택 상태를 저장하기 전에).
        ApplyCanvasResize();
        if (!CanEdit) return UiStrings.S_EditorContext_BeginTransform_C545365EE1;
        bool hadStroke = _selectionStrokeOpen;
        var celBefore = hadStroke ? _strokeImage?.Clone() : null;
        var before = Selection.Save();
        BeginSelectionEdit();
        var img = EditImage!;
        // 함께 고른 다른 셀들: 지금 셀의 선택 영역(없으면 셀마다 칠한 픽셀 전체)을 같은 자리에서 뗀다(셀은 적용할 때까지 그대로).
        var others = (cels ?? SelectedCels?.Invoke())?.Where(c => c.Layer != _layer || c.Frame != _frame).ToList();
        var multi = others is { Count: > 0 } ? MultiCelTransform.Create(p, _layer, others, Selection.HasSelection ? Selection.Region : null) : null;
        if (!Selection.HasSelection && img.GetOpaqueBounds() is { IsEmpty: false } content) Selection.Select(img, content);
        FreeTransform? lifted = null;
        if (Selection.HasSelection)
        {
            Selection.Lift(img);
            lifted = new FreeTransform(Selection.Floating!, Selection.Region);
        }
        var othersBounds = multi?.Bounds ?? Rectangle.Empty;
        if (lifted is null && othersBounds.IsEmpty)
        {
            if (!hadStroke)
            {
                _selectionStrokeOpen = false;
                CancelStroke();
            }
            return multi is null ? UiStrings.S_EditorContext_BeginTransform_433795A813 : UiStrings.Transform_NoPixelsInCels;
        }
        var source = lifted;
        if (!othersBounds.IsEmpty)
        {
            // 손잡이는 모든 셀의 원본을 감싸는 사각형(움직임 전체가 보이게).
            var union = lifted is null ? othersBounds : Rectangle.Union(othersBounds, lifted.SourceBounds);
            source = new FreeTransform(new PixelImage(union.Width, union.Height), new PixelMask(union));
        }
        _transform = new TransformSession(source!, lifted, multi, before, celBefore);
        // 회전 중심은 처음에 지금 대상의 피벗(부위면 부위 피벗, 아니면 프레임/기본 피벗 — 캔버스의 피벗 표시) 자리.
        var (px, py) = Pivot.ToLocalPixel(CanvasWidth, CanvasHeight);
        var pivotSource = source!.Unmap(_transform.Params, px, py);
        if (double.IsFinite(pivotSource.X) && double.IsFinite(pivotSource.Y)) _transform.PivotSource = pivotSource;
        NotifyPixels();
        TransformChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>변형 중인 셀 수(지금 셀에 뗄 것이 있으면 그것 포함, 함께 고른 다른 셀들). 변형 중이 아니면 0.</summary>
    public int TransformCelCount => _transform is { } t ? (t.Lifted is null ? 0 : 1) + (t.Multi?.Targets.Count ?? 0) : 0;

    /// <summary>함께 고른 셀 중 레이어가 잠겨 건너뛴 셀 수(그림이 있는 것만). 변형 중이 아니면 0.</summary>
    public int TransformLockedSkipped => _transform?.Multi?.LockedSkipped ?? 0;

    /// <summary>
    /// 방금 지금 셀에 넣은 그림(<paramref name="image"/>를 <paramref name="location"/>에 — 캔버스 밖은 잘려 들어감)을 잘리기 전 그대로 떠 있는 상태로 들고
    /// 변형 모드에 들어간다: 셀에서 넣은 픽셀을 지우고 원본 전체(투명 테두리는 뺌)를 띄운다. 적용(Enter)하면 그때 자리에서 캔버스 밖이 잘려
    /// 내려앉고(실행취소 한 단계 — 넣기와 따로), 취소(Esc)하면 넣은 그대로(잘린 그림)로 돌아간다.
    /// 못 들어가면 상태 표시줄에 보일 이유, 들어갔으면 null.
    /// </summary>
    public string? BeginPlacedTransform(PixelImage image, Point location)
    {
        if (Project is null) return UiStrings.S_EditorContext_BeginTransform_E708BAFBC9;
        CommitFloating();
        if (IsGroupSelected) return UiStrings.S_EditorContext_BeginTransform_10405489AE;
        if (!CanEdit) return UiStrings.S_EditorContext_BeginTransform_C545365EE1;
        var content = image.GetOpaqueBounds();
        if (content.IsEmpty) return UiStrings.S_EditorContext_BeginTransform_433795A813;
        var floating = image.Crop(content);
        var region = new PixelMask(new Rectangle(location.X + content.X, location.Y + content.Y, content.Width, content.Height));
        var before = Selection.Save();
        BeginSelectionEdit();
        var cel = EditImage!;
        // 넣으며 셀에 그려진(잘린) 픽셀을 지운다 — 떠 있는 원본이 그 자리를 대신한다.
        var r = region.Bounds;
        for (int y = 0; y < floating.Height; y++)
            for (int x = 0; x < floating.Width; x++)
                if (floating.Pixels[y * floating.Width + x].A != 0) cel.Set(r.X + x, r.Y + y, Color32.Transparent);
        Selection.SetFloating(floating, region);
        var placed = new FreeTransform(floating, region);
        _transform = new TransformSession(placed, placed, null, before, null);
        NotifyPixels();
        TransformChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>변형 값을 바꾸고 원본에서 다시 그린다(끄는 동안의 미리보기도 이것). 배율 0은 막는다.</summary>
    public void SetTransformParams(TransformParams p)
    {
        if (_transform is not { } t) return;
        if (!double.IsFinite(p.CenterX) || !double.IsFinite(p.CenterY) || !double.IsFinite(p.Angle)
            || !double.IsFinite(p.ScaleX) || !double.IsFinite(p.ScaleY) || p.ScaleX == 0 || p.ScaleY == 0) return;
        p = p with { Angle = TransformParams.NormalizeAngle(p.Angle) };
        if (p == t.Params) return;
        t.Params = p;
        RenderTransform();
    }

    /// <summary>회전 중심을 캔버스 좌표로 옮긴다(그림은 그대로).</summary>
    public void SetTransformPivot(double x, double y)
    {
        if (_transform is not { } t) return;
        var src = t.Source.Unmap(t.Params, x, y);
        if (!double.IsFinite(src.X) || !double.IsFinite(src.Y)) return;
        t.PivotSource = src;
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>회전 중심을 원본 가운데로 되돌린다.</summary>
    public void ResetTransformPivot()
    {
        if (_transform is not { } t) return;
        t.PivotSource = t.Source.SourceCenter;
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 회전 중심을 기준으로 <paramref name="angle"/>(도)로 돌린 값: 회전 중심은 화면에서 제자리에 있다.
    /// </summary>
    public TransformParams RotatedAboutPivot(TransformParams start, double angle)
    {
        if (_transform is not { } t) return start;
        var (px, py) = t.Source.Map(start, t.PivotSource.X, t.PivotSource.Y);
        double rad = (angle - start.Angle) * Math.PI / 180;
        double s = Math.Sin(rad), c = Math.Cos(rad);
        double dx = start.CenterX - px, dy = start.CenterY - py;
        return start with { Angle = angle, CenterX = px + dx * c - dy * s, CenterY = py + dx * s + dy * c };
    }

    /// <summary>변형을 적용한다: 결과를 내려놓고 실행취소 한 단계로 기록한다(결과 모양은 선택으로 남음).</summary>
    public void CommitTransform() => CommitFloating();

    /// <summary>변형을 취소한다: 셀 픽셀과 선택(떠 있었는지 포함)을 변형 전 그대로 되돌린다. 기록은 남지 않는다.</summary>
    public void CancelTransform()
    {
        if (_transform is not { } t || Project is null) return;
        _transform = null;
        if (t.CelBefore is { } cel && _strokeImage is { } img)
        {
            // 이미 열려 있던 선택 편집 기록은 그대로 두고 그 안의 변형 전 상태로.
            img.CopyFrom(cel);
            Selection.Restore(t.Before);
            NotifyPixels();
        }
        else
        {
            _selectionStrokeOpen = false;
            CancelStroke();
            Selection.Restore(t.Before);
        }
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderTransform()
    {
        if (_transform is not { } t) return;
        if (t.Lifted is { } lifted)
        {
            // 여러 셀이면 손잡이 기준(모든 원본을 감싸는 사각형)과 같은 행렬로 지금 셀 원본을 그린다.
            var r = lifted.Render(t.Source.ParamsFor(lifted, t.Params), _transformResample);
            Selection.SetFloating(r.Image, r.Region);
        }
        NotifyPixels();
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────── 캔버스 크기 모드 ─────────────

    /// <summary>캔버스 크기 모드 상태: 원래 그림(현재 프레임 합성), 새 캔버스 자리, 원래 그림이 놓일 사각형, 미리보기.</summary>
    private sealed class CanvasResizeSession(PixelImage source)
    {
        /// <summary>현재 프레임 합성(미리보기 원본 — 늘 이것에서 다시 그려 누적되지 않음).</summary>
        public PixelImage Source { get; set; } = source;
        /// <summary>새 캔버스(원래 프레임 좌표: 가장자리를 끄는 동안 화면에서 제자리에 보이게).</summary>
        public Rectangle Canvas { get; set; } = new(0, 0, source.Width, source.Height);
        /// <summary>원래 그림이 놓일 사각형(새 캔버스 좌표, 크기 = 그림 크기).</summary>
        public Rectangle Image { get; set; } = new(0, 0, source.Width, source.Height);
        /// <summary>중심 기준 크기 조정의 고정된 캔버스 중심(픽셀 두 배 좌표).</summary>
        public Point Center2 { get; set; }
        public bool ResizeFromCenter { get; set; }
        /// <summary>결과 미리보기(새 캔버스 크기). 값이 바뀌면 버리고 그릴 때 다시 만든다.</summary>
        public WriteableBitmap? Preview { get; set; }
    }

    private CanvasResizeSession? _canvasResize;

    /// <summary>캔버스 크기 모드에 들어가거나 나옴, 값이 바뀜.</summary>
    public event EventHandler? CanvasResizeChanged;

    /// <summary>캔버스 크기 모드를 적용해 프로젝트가 바뀜(상태 표시줄 알림 글).</summary>
    public event EventHandler<string>? CanvasResized;

    /// <summary>캔버스 크기 모드(Ctrl+Alt+C)인지.</summary>
    public bool IsCanvasResizing => _canvasResize is not null;

    /// <summary>원래 캔버스(프레임) 크기.</summary>
    public Size CanvasResizeSource => _canvasResize?.Source.Size ?? Size.Empty;

    /// <summary>새 캔버스(원래 프레임 좌표, 크기 = 새 캔버스 크기). 모드가 아니면 빈 사각형.</summary>
    public Rectangle CanvasResizeCanvas => _canvasResize?.Canvas ?? Rectangle.Empty;

    /// <summary>원래 그림이 놓일 사각형(새 캔버스 좌표). 모드가 아니면 빈 사각형.</summary>
    public Rectangle CanvasResizeImage => _canvasResize?.Image ?? Rectangle.Empty;
    /// <summary>크기 변경 시 캔버스 중심을 고정하고 양쪽 가장자리를 함께 움직이는지.</summary>
    public bool CanvasResizeFromCenter
    {
        get => _canvasResize?.ResizeFromCenter ?? false;
        set
        {
            if (_canvasResize is not { } s || s.ResizeFromCenter == value) return;
            s.ResizeFromCenter = value;
            if (value) s.Center2 = new Point(2 * s.Canvas.X + s.Canvas.Width, 2 * s.Canvas.Y + s.Canvas.Height);
            CanvasResizeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>중심 기준을 켠 순간의 캔버스 중심(픽셀 두 배 좌표).</summary>
    public Point CanvasResizeCenter2 => _canvasResize?.Center2 ?? Point.Empty;

    private bool _canvasResizeNineSlice = true;
    private NineSliceFill _canvasResizeSliceFill = NineSliceFill.Stretch;

    /// <summary>캔버스 크기 모드에서 9-slice로 늘릴지(기본 켬, 앱을 켜 둔 동안 기억). 쓸 테두리가 없으면 켜 있어도 쓰지 않는다(<see cref="CanvasResizeUsesSlice"/>).</summary>
    public bool CanvasResizeNineSlice
    {
        get => _canvasResizeNineSlice;
        set
        {
            if (_canvasResizeNineSlice == value) return;
            _canvasResizeNineSlice = value;
            OnCanvasResizeSliceChanged();
        }
    }

    /// <summary>9-slice로 늘릴 때 가운데를 채우는 방법(늘이기·되풀이).</summary>
    public NineSliceFill CanvasResizeSliceFill
    {
        get => _canvasResizeSliceFill;
        set
        {
            if (_canvasResizeSliceFill == value) return;
            _canvasResizeSliceFill = value;
            OnCanvasResizeSliceChanged();
        }
    }

    /// <summary>프레임 칸 9-slice 테두리(캔버스 크기 모드가 늘일 테두리 — 쓸 수 없으면 없음).</summary>
    public SpriteBorder CanvasResizeBorder
        => Project is { } p && NineSlice.Applies(p.Sprite.Border, p.Width, p.Height) ? p.Sprite.Border : SpriteBorder.None;

    /// <summary>9-slice로 늘릴 테두리가 있는지: 프레임 칸 테두리 또는 자기 테두리가 있는 부위.</summary>
    public bool CanvasResizeSliceAvailable
        => Project is { } p && (!CanvasResizeBorder.IsNone || p.Layers.Any(l => l.IsPart && NineSlice.Applies(l.PartBorder, l.PartSize.Width, l.PartSize.Height)));

    /// <summary>캔버스 크기 모드가 그림 크기를 9-slice로 바꾸는지(켰고 쓸 테두리가 있음). 조작(Ctrl+끌기·그림 가로/세로)은 같고 늘리는 방법만 바뀐다.</summary>
    public bool CanvasResizeUsesSlice => _canvasResizeNineSlice && CanvasResizeSliceAvailable;

    /// <summary>9-slice로 늘릴 때 그림의 가장 작은 크기: 프레임 칸 테두리 두 쪽 + 가운데 1px(테두리가 없으면 1).</summary>
    public Size CanvasResizeMinImage
    {
        get
        {
            var b = CanvasResizeUsesSlice ? CanvasResizeBorder : SpriteBorder.None;
            return b.IsNone ? new Size(1, 1) : new Size(b.Left + b.Right + 1, b.Top + b.Bottom + 1);
        }
    }

    private void OnCanvasResizeSliceChanged()
    {
        if (_canvasResize is not { } s) return;
        var min = CanvasResizeMinImage;
        s.Image = s.Image with { Width = Math.Max(s.Image.Width, min.Width), Height = Math.Max(s.Image.Height, min.Height) };
        DropCanvasResizePreview(s);
        CanvasResizeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>적용하면 될 모습(새 캔버스 크기, 현재 프레임 합성을 최근접 — 9-slice면 프레임 칸 테두리로). 모드가 아니면 null. 호출자는 Dispose하지 않는다.</summary>
    public WriteableBitmap? CanvasResizeBitmap
        => _canvasResize is not { } s ? null
            : s.Preview ??= ImageUtil.ToBitmap(ImageTransform.ResizeCanvasScaled(s.Source, s.Canvas.Width, s.Canvas.Height, s.Image,
                CanvasResizeUsesSlice ? CanvasResizeBorder : SpriteBorder.None, _canvasResizeSliceFill));

    /// <summary>
    /// 캔버스 크기 모드에 들어간다: 떠 있는 선택·변형을 먼저 적용하고, 현재 프레임 합성 전체(부위를 골랐어도)를 미리보기로 보인다.
    /// 못 들어가면 상태 표시줄에 보일 이유, 들어갔으면(이미 모드여도) null.
    /// </summary>
    public string? BeginCanvasResize()
    {
        if (Project is null) return UiStrings.S_EditorContext_BeginCanvasResize_9D67D0C8F0;
        if (_canvasResize is not null) return null;
        CommitFloating();
        if (FrameImage(_frame) is not { } image) return UiStrings.S_EditorContext_BeginCanvasResize_1378D58776;
        _canvasResize = new CanvasResizeSession(image);
        CanvasResizeChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// 새 캔버스(원래 프레임 좌표)와 그림 사각형(새 캔버스 좌표)을 바꾼다. 크기는 1~<see cref="PixelImage.MaxSize"/>로 맞추고,
    /// 9-slice로 늘이면 그림은 테두리 두 쪽 + 1px보다 작아지지 않는다(<see cref="CanvasResizeMinImage"/>).
    /// </summary>
    public void SetCanvasResize(Rectangle canvas, Rectangle image)
    {
        if (_canvasResize is not { } s) return;
        static int Clamp(int v, int min = 1) => Math.Clamp(v, min, PixelImage.MaxSize);
        var min = CanvasResizeMinImage;
        canvas.Size = new Size(Clamp(canvas.Width), Clamp(canvas.Height));
        image.Size = new Size(Clamp(image.Width, min.Width), Clamp(image.Height, min.Height));
        if (canvas == s.Canvas && image == s.Image) return;
        s.Canvas = canvas;
        s.Image = image;
        DropCanvasResizePreview(s);
        CanvasResizeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>캔버스 크기 모드를 적용한다(바뀐 것이 있으면 실행취소 한 단계).</summary>
    public void CommitCanvasResize() => CommitFloating();

    /// <summary>캔버스 크기 모드를 취소한다(프로젝트는 그대로).</summary>
    public void CancelCanvasResize()
    {
        if (_canvasResize is not { } s) return;
        _canvasResize = null;
        DropCanvasResizePreview(s);
        CanvasResizeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCanvasResize()
    {
        if (_canvasResize is not { } s || Project is not { } p) return;
        _canvasResize = null;
        DropCanvasResizePreview(s);
        var (canvas, image) = (s.Canvas.Size, s.Image);
        bool changed = canvas != p.Size || image != new Rectangle(Point.Empty, p.Size);
        if (changed) p.ResizeCanvasAndScale(canvas.Width, canvas.Height, image, CanvasResizeUsesSlice ? _canvasResizeSliceFill : null);
        CanvasResizeChanged?.Invoke(this, EventArgs.Empty);
        if (!changed) return;
        bool scaled = image.Size != s.Source.Size;
        CanvasResized?.Invoke(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_EditorContext_ApplyCanvasResize_B3AC8F3521,canvas.Width,canvas.Height) + (scaled ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_EditorContext_ApplyCanvasResize_2F84F43E48,image.Width,image.Height) : ""));
    }

    /// <summary>모드 중에 프로젝트가 바뀜(레이어 보이기 등): 캔버스 크기가 그대로면 미리보기 원본을 새 합성으로, 바뀌었으면 모드를 취소한다.</summary>
    private void SyncCanvasResize()
    {
        if (_canvasResize is not { } s || Project is not { } p) return;
        if (p.Size != s.Source.Size || FrameImage(_frame) is not { } image)
        {
            CancelCanvasResize();
            return;
        }
        s.Source = image;
        DropCanvasResizePreview(s);
        CanvasResizeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void DropCanvasResizePreview(CanvasResizeSession s)
    {
        s.Preview?.Dispose();
        s.Preview = null;
    }

    /// <summary>구조 편집(레이어·프레임·태그·설정) 전에 열린 그리기를 마무리한다.</summary>
    /// <summary>9-slice 대상의 테두리: 부위를 골랐으면 그 부위, 아니면 프레임 시트 모든 칸(프로젝트 설정).</summary>
    public SpriteBorder SliceBorder => CurrentPart?.PartBorder ?? Project?.Sprite.Border ?? SpriteBorder.None;

    /// <summary>9-slice 대상의 테두리를 바꾼다(실행취소 한 단계). 같으면 아무것도 하지 않는다.</summary>
    public void SetSliceBorder(SpriteBorder border)
    {
        if (Project is not { } p || border == SliceBorder) return;
        if (CurrentPart is { } part) SetPartBorder(part, border);
        else Structural(() => p.Edit(UiStrings.S_EditorContext_SetSliceBorder_738B84377F, ProjectChange.Settings, () => p.Sprite.Border = border));
    }

    /// <summary>부위 하나의 9-slice 테두리를 바꾼다(그룹 화면에서 끌 때, 실행취소 한 단계).</summary>
    public void SetPartBorder(Layer part, SpriteBorder border)
    {
        if (Project is not { } p || part.PartBorder == border) return;
        int index = p.IndexOf(part);
        Structural(() => p.SetPartBorder(index, border));
    }

    /// <summary>
    /// 부위를 옮긴다(실행취소 한 단계). 그룹이 자동 배치 중이면 같은 단계에서 자동 배치를 끈다 — 먼저 <see cref="GroupLayoutDialog.ConfirmManualMove"/>로 물어본 뒤 부른다.
    /// </summary>
    public void MovePart(Layer part, Point offset)
    {
        if (Project is not { } p || p.IndexOf(part) is var index && index < 0 || part.PartOffset == offset) return;
        Structural(() => p.Edit(part.IsAutoLaidOut ? UiStrings.S_EditorContext_MovePart_9205E8CE1C : UiStrings.S_EditorContext_MovePart_B02E0EA2B6, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            if (part.Parent is { AutoLayout: not null } group) p.SetGroupLayout(group, null);
            p.SetPartOffset(index, offset);
        }));
    }

    /// <summary>그룹 자동 배치 켜기·설정(null = 끄기, 부위는 그 자리에), 실행취소 한 단계.</summary>
    public void SetGroupLayout(Layer group, GroupLayout? layout)
    {
        if (Project is not { } p || !p.Layers.Contains(group)) return;
        Structural(() => p.SetGroupLayout(group, layout));
    }

    public void Structural(Action action)
    {
        if (Project is null) return;
        CommitFloating();
        action();
    }

    // ───────────── 스킨(착장) ─────────────

    /// <summary>그룹의 지금 스킨(착장)을 고른다(실행취소 기록 없음, 떠 있는 선택은 먼저 내려놓음).</summary>
    public void SetActiveSkin(Layer group, Skin skin)
    {
        if (Project is not { } p || ReferenceEquals(group.ActiveSkin, skin)) return;
        Structural(() => p.SetActiveSkin(group, skin));
    }

    /// <summary>부위 하나만 보일 스킨을 고른다(null = 그룹 스킨을 따름, 실행취소 기록 없음, 떠 있는 선택은 먼저 내려놓음).</summary>
    public void SetPartSkin(Layer part, Skin? skin)
    {
        if (Project is not { } p || ReferenceEquals(part.SkinOverride, skin)) return;
        Structural(() => p.SetPartSkin(part, skin));
    }

    /// <summary>그룹의 모든 부위를 그룹 스킨으로 되돌린다(재정의 지우기). 바뀐 것이 있으면 true.</summary>
    public bool ResetPartSkins(Layer group)
    {
        if (Project is not { } p) return false;
        bool changed = false;
        Structural(() => changed = p.ResetPartSkins(group));
        return changed;
    }

    /// <summary>그룹의 지금 스킨을 <paramref name="delta"/>칸 앞뒤로 돌린다(끝에서는 반대쪽으로). 스킨이 하나뿐이면 false.</summary>
    public bool StepSkin(Layer group, int delta)
    {
        var skins = group.Skins;
        if (skins.Count < 2 || group.ActiveSkin is not { } active) return false;
        int i = 0;
        while (i < skins.Count && !ReferenceEquals(skins[i], active)) i++;
        SetActiveSkin(group, skins[((i + delta) % skins.Count + skins.Count) % skins.Count]);
        return true;
    }

    public bool UndoStep()
    {
        if (Project is null) return false;
        CommitFloating();
        return Project.Undo();
    }

    public bool RedoStep()
    {
        if (Project is null) return false;
        CommitFloating();
        return Project.Redo();
    }

    // ───────────── 피벗 ─────────────

    /// <summary>현재 피벗(끄는 중이면 미리보기 값): 부위면 그 부위의 피벗, 아니면 현재 프레임 피벗.</summary>
    public Pivot Pivot
        => _pivotPreview ?? CurrentPart?.PartPivot ?? (Project is { } p ? p.GetFramePivot(_frame) : Pivot.Center);

    /// <summary>피벗 끄는 중: 기록 없이 화면만 바꾼다.</summary>
    public void PreviewPivot(Pivot pivot)
    {
        _pivotPreview = pivot;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 끌기를 마친 피벗을 기록한다. 부위면 그 부위의 피벗, 아니면 현재 프레임에 피벗 재정의가 있으면
    /// 그 프레임만, 없으면 프로젝트 기본 피벗을 바꾼다.
    /// </summary>
    public void CommitPivot(Pivot before)
    {
        if (Project is not { } p || _pivotPreview is not { } after) return;
        _pivotPreview = null;
        if (after == before) { ViewChanged?.Invoke(this, EventArgs.Empty); return; }
        if (CurrentPart is not null) p.SetPartPivot(_layer, after);
        else if (p.Frames[_frame].Pivot is not null) p.SetFramePivot(_frame, after);
        else p.Edit(UiStrings.S_EditorContext_CommitPivot_463E10A971, ProjectChange.Settings, () => p.Sprite.DefaultPivot = after);
    }

    // ───────────── 합성 결과(RenderService) ─────────────

    /// <summary>레이어 하나의 그리기에 영향을 주는 상태(이름·잠금 같은 것은 빠짐).</summary>
    private readonly record struct LayerRenderState(Layer Layer, LayerKind Kind, Layer? Parent, bool Visible, byte Opacity,
        IReadOnlyList<LayerEffect> Effects, Point Offset, Size PartSize, Skin? Shown)
    {
        public static LayerRenderState Of(Layer l) => new(l, l.Kind, l.Parent, l.Visible, l.Opacity, l.Effects, l.PartOffset, l.PartSize, l.ShownSkin);

        /// <summary>효과 목록 말고는 같은지.</summary>
        public bool SameExceptEffects(LayerRenderState other) => this with { Effects = other.Effects } == other;

        /// <summary>
        /// 그리기가 같은지: 효과 목록은 내용으로 비교한다(효과 창 확인·타임라인 끌기를 놓으면 미리보던 것과 같은 효과가 새 배열로 들어온다 —
        /// 참조로 비교하면 방금 다 그린 모든 프레임을 다시 계산했다).
        /// </summary>
        public bool SameRender(LayerRenderState other)
            => this == other || SameExceptEffects(other) && Effects.SequenceEqual(other.Effects);
    }

    private void RememberRenderState(SpriteProject? p)
    {
        _layerStates = p is null ? [] : p.Layers.Select(LayerRenderState.Of).ToArray();
        _frameOrder = p is null ? [] : p.Frames.ToArray();
    }

    /// <summary>
    /// 스트로크가 아닌 변경: 캔버스 크기가 바뀌면 모두 버리고, 셀 픽셀·프레임·레이어 구조가 바뀌면 모두 무효로 한다(그림은 다시 계산될 때까지 보임).
    /// 레이어 속성만 바뀌었으면 바뀐 레이어(와 그 그룹)의 셀·썸네일과 프레임 합성만 무효로 하고, 효과 목록만 바뀌었으면 효과 결과 캐시도 그대로 쓴다.
    /// 이름·잠금처럼 그리기와 관계없는 변경은 아무것도 무효로 하지 않는다.
    /// </summary>
    private void InvalidateRenders(SpriteProject p, ProjectChange change)
    {
        var before = _layerStates;
        var frames = _frameOrder;
        RememberRenderState(p);
        if ((change & ProjectChange.Canvas) != 0)
        {
            _renders.Reset();
            return;
        }
        bool sameStructure = before.Length == _layerStates.Length && frames.Length == _frameOrder.Length
            && before.Zip(_layerStates).All(t => ReferenceEquals(t.First.Layer, t.Second.Layer))
            && frames.Zip(_frameOrder).All(t => ReferenceEquals(t.First, t.Second));
        if (!sameStructure || (change & (ProjectChange.Pixels | ProjectChange.Frames)) != 0)
        {
            _renders.Effects.Clear();
            _renders.CheckpointLayer = null;
            _renders.InvalidateAll();
            return;
        }
        var changed = new HashSet<Layer>();
        bool effectsOnly = true;
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i].SameRender(_layerStates[i])) continue;
            changed.Add(before[i].Layer);
            if (before[i].Layer.Parent is { } parent) changed.Add(parent);
            effectsOnly &= before[i].SameExceptEffects(_layerStates[i]);
        }
        if (changed.Count == 0) return;
        if (!effectsOnly) _renders.Effects.Clear();
        // 효과만 바뀐 레이어 하나(효과 창에서 값을 바꾸는 중): 지금 프레임 합성에서 효과 1~k 중간 결과를 담아 다음 변경 때 이어 그린다.
        _renders.CheckpointLayer = effectsOnly ? changed.FirstOrDefault(l => before.Any(s => ReferenceEquals(s.Layer, l) && !ReferenceEquals(s.Effects, l.Effects))) : null;
        _renders.Invalidate(k => k.Kind is RenderKind.Composite or RenderKind.Context || k.Layer is { } l && changed.Contains(l));
    }

    /// <summary>
    /// 스크립트 실행 허락·멈춤 해제: 효과 값은 같아도 결과가 달라지므로 효과 결과 캐시를 비우고 모든 결과를 무효로 한다(그림은 다시 계산될 때까지 보임).
    /// </summary>
    private void RerenderScripts()
    {
        if (Project is null) return;
        _renders.Effects.Clear();
        _renders.CheckpointLayer = null;
        _renders.InvalidateAll();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 현재 셀이 바뀜: 현재 프레임의 합성, 현재 레이어가 들어간 "부위 하나 뺀 합성", 현재 셀과 그 그룹의 셀·썸네일을 무효로 한다.
    /// 셀 픽셀을 그 자리에서 고치므로 그 셀을 담은 효과 결과 캐시도 비운다. 잔상이 있으면 다른 프레임 합성도 이 셀을 보므로 무효로 한다 —
    /// 다만 그리는 중(<paramref name="stroking"/>)에는 다른 프레임은 두고 지금 프레임만 다시 그린다(포인터가 움직일 때마다 모든 프레임을 다시 계산해
    /// 코어를 다 쓰지 않게). 그리기를 마치면(<see cref="SpriteProject.CommitCelStroke"/>) 모두 무효가 된다.
    /// </summary>
    private void InvalidateCurrent(bool stroking = false)
    {
        if (Project is not { } p || _frame < 0 || _frame >= p.Frames.Count || _layer < 0 || _layer >= p.Layers.Count) return;
        var frame = p.Frames[_frame];
        var layer = p.Layers[_layer];
        var group = layer.Parent;
        bool others = !stroking && (LayerEffects.UsesOtherFrames(layer.Effects) || group is not null && LayerEffects.UsesOtherFrames(group.Effects));
        _renders.Effects.Clear();
        _renders.CheckpointLayer = null;
        _renders.Invalidate(k => k.Kind switch
        {
            RenderKind.Composite => others || ReferenceEquals(k.Frame, frame),
            RenderKind.Context => ReferenceEquals(k.Frame, frame) && !ReferenceEquals(k.Layer, layer),
            _ => ReferenceEquals(k.Frame, frame) && (ReferenceEquals(k.Layer, layer) || group is not null && ReferenceEquals(k.Layer, group))
                 || others && (ReferenceEquals(k.Layer, layer) || group is not null && ReferenceEquals(k.Layer, group)),
        });
    }

    /// <summary>
    /// 프레임 전체 합성(보이는 레이어·그룹)의 그릴 것. 무거우면 작업 스레드에서 계산하는 동안 이전 결과(<paramref name="live"/>면 중간 그림)를 준다.
    /// 비트맵은 호출자가 Dispose하지 않는다.
    /// </summary>
    public RenderView FrameView(int frame, RenderPriority priority = RenderPriority.Background, bool live = false)
    {
        if (Project is not { } p || frame < 0 || frame >= p.Frames.Count) return RenderView.None;
        return _renders.Get(new RenderKey(RenderKind.Composite, p.Frames[frame]), priority, () => p.PlanFrame(frame), live);
    }

    /// <summary>프레임 전체 합성 비트맵(계산 중이면 이전 결과). 호출자는 Dispose하지 않는다.</summary>
    public WriteableBitmap? FrameBitmap(int frame, RenderPriority priority = RenderPriority.Background) => FrameView(frame, priority).Bitmap;

    /// <summary>프레임 <paramref name="frame"/>의 최신 합성이 준비되었는지(재생이 기다릴지 정할 때).</summary>
    public bool IsFrameReady(int frame) => Project is { } p && frame >= 0 && frame < p.Frames.Count
        && _renders.IsReady(new RenderKey(RenderKind.Composite, p.Frames[frame]));

    /// <summary>
    /// 프레임 전체 합성 그림(미리보기가 그리는 그대로, 최신 — 없으면 지금 계산). 캐시라서 호출자는 고치지 않는다.
    /// </summary>
    public PixelImage? FrameImage(int frame)
    {
        if (Project is not { } p || frame < 0 || frame >= p.Frames.Count) return null;
        return _renders.GetImage(new RenderKey(RenderKind.Composite, p.Frames[frame]), () => p.PlanFrame(frame));
    }

    /// <summary>
    /// 캔버스에 보이는 그림: 부위를 골랐으면 그 부위의 셀(부위 크기, 빈 셀이면 null), 아니면 프레임 전체 합성.
    /// 이전 프레임 보기·양파 껍질에 쓴다. 호출자는 Dispose하지 않는다.
    /// </summary>
    public WriteableBitmap? ViewBitmap(int frame, RenderPriority priority = RenderPriority.Soon)
        => CurrentPart is not null ? CelView(_layer, frame, priority).Bitmap : FrameView(frame, priority).Bitmap;

    /// <summary>
    /// 현재 캔버스 그림: 부위면 그 부위의 현재 셀, 아니면 현재 프레임 합성(계산 중이면 중간 그림·진행·덮개 여부).
    /// 부위 셀이 비었으면 투명 비트맵.
    /// </summary>
    public RenderView DisplayView
    {
        get
        {
            if (Project is null) return RenderView.None;
            if (CurrentPart is null) return FrameView(_frame, RenderPriority.Current, live: true);
            var view = CelView(_layer, _frame, RenderPriority.Current);
            if (view.Bitmap is not null) return view;
            int w = Math.Max(1, CanvasWidth), h = Math.Max(1, CanvasHeight);
            if (_blankBitmap is null || _blankBitmap.PixelSize.Width != w || _blankBitmap.PixelSize.Height != h)
            {
                _blankBitmap?.Dispose();
                _blankBitmap = ImageUtil.NewBitmap(w, h);
            }
            return view with { Bitmap = _blankBitmap };
        }
    }

    /// <summary>
    /// 프레임 합성에서 부위 하나(<paramref name="exclude"/>)를 뺀 비트맵(프레임 크기). 부위 화면의 흐린 주변,
    /// 그룹 화면에서 부위를 끄는 동안의 배경에 쓴다. 호출자는 Dispose하지 않는다.
    /// </summary>
    public WriteableBitmap? ContextBitmap(int frame, Layer exclude)
    {
        if (Project is not { } p || frame < 0 || frame >= p.Frames.Count) return null;
        return _renders.Get(new RenderKey(RenderKind.Context, p.Frames[frame], exclude), RenderPriority.Current,
            () => p.PlanFrame(frame, layerFilter: l => !ReferenceEquals(l, exclude))).Bitmap;
    }

    /// <summary>
    /// 현재 캔버스에서 보이는 색. 부위 화면이면 부위 셀의 색(투명이면 그 자리의 프레임 합성 색), 아니면 프레임 합성 색.
    /// <paramref name="exact"/>가 false면(상태 표시줄) 계산 중일 때 이전 결과의 색을 쓰고 기다리지 않는다.
    /// </summary>
    public Color32 CompositeColorAt(int x, int y, bool exact = true)
    {
        if (CurrentPart is { } part)
        {
            var c = EditImage is { } cel && cel.Contains(x, y) ? cel.Get(x, y) : Color32.Transparent;
            if (c.A != 0) return c;
            x += part.PartOffset.X;
            y += part.PartOffset.Y;
        }
        var image = exact ? FrameImage(_frame)
            : Project is { } p && _frame < p.Frames.Count ? _renders.ShownImage(new RenderKey(RenderKind.Composite, p.Frames[_frame])) : null;
        return image is not null && image.Contains(x, y) ? image.Get(x, y) : Color32.Transparent;
    }

    /// <summary>
    /// 현재 캔버스에서 보이는 그림 전체(캔버스 크기, 새 이미지): 각 픽셀이 <see cref="CompositeColorAt"/>와 같다.
    /// 채우기의 "보이는 레이어 전체" 기준에 쓴다.
    /// </summary>
    public PixelImage VisibleImage()
    {
        int w = Math.Max(1, CanvasWidth), h = Math.Max(1, CanvasHeight);
        var img = new PixelImage(w, h);
        if (FrameImage(_frame) is not { } frame) return img;
        if (CurrentPart is not { } part)
        {
            img.CopyFrom(frame);
            return img;
        }
        var cel = EditImage;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = cel is not null && cel.Contains(x, y) ? cel.Get(x, y) : Color32.Transparent;
                int fx = x + part.PartOffset.X, fy = y + part.PartOffset.Y;
                if (c.A == 0 && frame.Contains(fx, fy)) c = frame.Get(fx, fy);
                img.Set(x, y, c);
            }
        return img;
    }

    /// <summary>
    /// 셀 하나(레이어 × 프레임)의 그릴 것(원본 크기): 그룹이면 그 그룹의 합성(프레임 크기, 그룹 효과 포함), 일반 레이어면 효과를 입힌 셀(셀 크기로 잘림),
    /// 부위면 셀 그대로(부위 화면에서 고치는 그림이라 효과 없이). 빈 셀(그룹이면 모든 부위가 빈 셀)이면 비트맵이 null.
    /// </summary>
    public RenderView CelView(int layer, int frame, RenderPriority priority = RenderPriority.Background)
    {
        if (Project is not { } p || frame < 0 || frame >= p.Frames.Count || layer < 0 || layer >= p.Layers.Count) return RenderView.None;
        return _renders.Get(new RenderKey(RenderKind.Cel, p.Frames[frame], p.Layers[layer]), priority, () => p.PlanCel(layer, frame));
    }

    /// <summary><see cref="CelView"/>의 비트맵. 호출자는 Dispose하지 않는다.</summary>
    public WriteableBitmap? CelBitmap(int layer, int frame) => CelView(layer, frame).Bitmap;

    /// <summary>
    /// 타임라인 칸 썸네일: <see cref="CelView"/>와 같은 그림을 긴 변 <see cref="RenderService.ThumbMaxSize"/>px 이하로 줄인 것
    /// (<see cref="RenderView.SourceWidth"/>·<see cref="RenderView.SourceHeight"/>는 원래 크기).
    /// </summary>
    public RenderView CelThumbnail(int layer, int frame)
    {
        if (Project is not { } p || frame < 0 || frame >= p.Frames.Count || layer < 0 || layer >= p.Layers.Count) return RenderView.None;
        return _renders.Get(new RenderKey(RenderKind.Thumb, p.Frames[frame], p.Layers[layer]), RenderPriority.Background, () => p.PlanCel(layer, frame));
    }
}
