using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Gpixel.App.Localization;
using Gpixel.Core.Edit;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.Core.Pixelate;

namespace Gpixel.App;

/// <summary>AI 그림 픽셀로 정리 창의 화면 설정(설정 파일에 기억 — 단계 설정은 <see cref="AppSettings.PixelateSettings"/>).</summary>
public sealed class PixelateOptions
{
    public int CandidateCount { get; set; } = 8;
    /// <summary>바꿔 볼 항목(<see cref="CandidateVary"/> 비트).</summary>
    public int Vary { get; set; } = (int)CandidateVary.Default;
    public bool NewDocument { get; set; }
    public int FrameDurationMs { get; set; } = 100;
    public int Fps { get; set; } = 8;
    public bool SavePalette { get; set; }
    public bool AiTouchUp { get; set; }
    /// <summary>AI 다듬기 부탁 글(null = 화면 언어의 기본 글).</summary>
    public string? AiPrompt { get; set; }
    public string AiProvider { get; set; } = "claude";
    public bool AddReference { get; set; }
    public int AiRankCount { get; set; } = 4;
    /// <summary>
    /// 자동 최적화 프리셋(목록 순서 = 도는 순서·후보 순서, 즐겨찾기가 앞). 자동 최적화는 체크한(<see cref="OptimizePreset.Enabled"/>) 프리셋을 모두 차례로 돌린다.
    /// <see cref="Migrate"/> 뒤에는 비어 있지 않다.
    /// </summary>
    public List<OptimizePreset> OptimizePresets { get; set; } = [];
    /// <summary>원본을 넣으면(열기·끌어 놓기·붙여 넣기·찾기·바꾸기) 첫 후보가 끝난 뒤 자동 최적화를 한 번 돌린다(기본 켬, 모든 프리셋 공통).</summary>
    public bool AutoOptimizeOnLoad { get; set; } = true;
    /// <summary>왼쪽 설정 칸을 펼쳐 두었는지(기본 접힘 — 미리보기·후보가 넓게).</summary>
    public bool SettingsExpanded { get; set; }
    /// <summary>옵션 판(<see cref="Migrate"/>): 0 = 자동 최적화 기본값이 바뀌기 전에 저장한 값.</summary>
    public int Version { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool ShowMask { get; set; }
    public int Width { get; set; } = 1320;
    public int Height { get; set; } = 860;

    /// <summary>
    /// 판 4 전에 저장한 설정 파일에만 있는 낱개 자동 최적화 값(OptimizeParams·OptimizeTrials·OptimizeSeconds·Weight*·ScoreColorDistance·ScoreHueWeight)을
    /// 읽어 두는 자리. <see cref="Migrate"/>가 첫 프리셋으로 옮긴 뒤 비우므로 다시 저장되지 않는다(그 밖의 모르는 값도 함께 버림).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Legacy { get; set; }

    /// <summary>보통 후보를 잴 점수 기준·AI 그리기의 자동 최적화: 체크한 첫 프리셋(없으면 첫 프리셋). <see cref="Migrate"/> 뒤에만 쓴다.</summary>
    [JsonIgnore]
    public OptimizePreset PrimaryPreset => OptimizePresets.FirstOrDefault(p => p.Enabled) ?? OptimizePresets[0];

    /// <summary>지금 옵션 판.</summary>
    public const int CurrentVersion = 5;

    /// <summary>
    /// 예전 판에서 저장한 값을 한 번만 옮기고 프리셋이 하나 이상 있게 한다. 판 1 = 자동 최적화 항목·예산을 새 기본값(외곽선 맞추기 방식만 빼고 모두, 2000번, 120초)으로.
    /// 판 2 = 새 자동 최적화 항목(색 거리·다른 색상 지키기)을 켠다. 판 3 = 명암 계열 우선 항목을 켜고 '불러오면 자동 최적화'를 켠다.
    /// 판 4 = 낱개로 저장하던 항목·예산·점수 기준(<see cref="Legacy"/>)을 '기본' 프리셋 하나로 옮긴다.
    /// 판 5 = 기본 프리셋(<see cref="OptimizePreset.BuiltIns"/>) 중 같은 이름이 없는 것을 뒤에 더한다(한 번만 — 그 뒤 지운 기본 프리셋은 되살리지 않음).
    /// 단계 설정 JSON(마지막 설정·프리셋)은 빠진 값이 기본값이라 예전에 저장한 설정도 새 기본값(OKLCh 색상 가중·다른 색상 지키기·명암 계열 우선 켬)으로 읽힌다.
    /// 새로 만든 옵션은 기본 프리셋 전부로 시작한다.
    /// </summary>
    public void Migrate()
    {
        if (Version < CurrentVersion)
        {
            bool fresh = OptimizePresets.Count == 0 && Legacy is null && Version == 0;
            if (OptimizePresets.Count == 0 && !fresh) OptimizePresets.Add(LegacyPreset());
            if (Version < 3) AutoOptimizeOnLoad = true;
            OptimizePreset.LocalizeBuiltIns(OptimizePresets);
            foreach (var builtIn in OptimizePreset.BuiltIns())
                if (!OptimizePresets.Any(p => p.BuiltIn == builtIn.BuiltIn || string.Equals(p.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase)))
                    OptimizePresets.Add(builtIn);
            Version = CurrentVersion;
        }
        Legacy = null;
        OptimizePresets.RemoveAll(p => p is null);
        if (OptimizePresets.Count == 0) OptimizePresets.AddRange(OptimizePreset.BuiltIns());
        OptimizePreset.LocalizeBuiltIns(OptimizePresets);
    }

    /// <summary>판 4 전의 낱개 값으로 만든 '기본' 프리셋(판 1 전이면 항목·예산은 기본값, 판 2·3에서 더한 항목은 켬).</summary>
    private OptimizePreset LegacyPreset()
    {
        var preset = new OptimizePreset { Name = UiStrings.OptimizePreset_DefaultName };
        if (Legacy is not { } old) return preset;
        double Real(string key, double fallback)
            => old.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : fallback;
        int Whole(string key, int fallback)
            => old.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : fallback;
        var w = preset.Weights;
        preset.Weights = new ScoreWeights
        {
            Fidelity = Real("WeightFidelity", w.Fidelity),
            Orphans = Real("WeightOrphans", w.Orphans),
            OutlineGaps = Real("WeightOutlineGaps", w.OutlineGaps),
            Colors = Real("WeightColors", w.Colors),
            Consistency = Real("WeightConsistency", w.Consistency),
            Grid = Real("WeightGrid", w.Grid),
            OutlineThin = Real("WeightOutlineThin", w.OutlineThin),
            ColorShift = Real("WeightColorShift", w.ColorShift),
            BlockRepetition = Real("WeightBlockRepetition", w.BlockRepetition),
        };
        int distance = Whole("ScoreColorDistance", (int)preset.ScoreColorDistance);
        if (Enum.IsDefined((ColorDistanceMode)distance)) preset.ScoreColorDistance = (ColorDistanceMode)distance;
        preset.ScoreHueWeight = PixelateRanges.HueWeight.Clamp(Real("ScoreHueWeight", preset.ScoreHueWeight));
        if (Version >= 1)
        {
            preset.Params = Whole("OptimizeParams", preset.Params) & (int)OptimizeParam.All;
            preset.Trials = Whole("OptimizeTrials", preset.Trials);
            preset.Seconds = Whole("OptimizeSeconds", preset.Seconds);
        }
        if (Version < 2) preset.Params |= (int)(OptimizeParam.ColorDistance | OptimizeParam.DistinctHues);
        if (Version < 3) preset.Params |= (int)OptimizeParam.ShadeFamily;
        return preset;
    }
}

/// <summary>
/// 자동 최적화 프리셋: 이름, 체크(자동 최적화를 누르면 함께 돌림), 즐겨찾기, 바꿔 볼 항목(<see cref="OptimizeParam"/> 비트)·예산과 고정 점수 기준
/// (점수용 주요 색 변화 거리·항목별 비중). 기본은 외곽선 맞추기 방식만 빼고 모두(<see cref="OptimizeParam.SelectAll"/> — 고른 방식이 1px 외곽선을 지키도록
/// 그 체크는 사용자가 직접 켤 때만), 2000번·120초, 기본 비중, OKLCh 색상 가중 2.
/// </summary>
public sealed class OptimizePreset
{
    public const OptimizeParam DefaultParams = OptimizeParam.SelectAll;
    public const int DefaultTrials = 2000;
    public const int DefaultSeconds = 120;
    /// <summary>시도 수·초 칸의 최소.</summary>
    public const int MinBudget = 5;
    public const int MaxTrials = 10000;
    public const int MaxSeconds = 600;

    public string Name { get; set; } = "";
    /// <summary>
    /// 기본 프리셋의 고정 id(<see cref="BuiltIns"/>) — 있으면 <see cref="Name"/>은 화면 언어의 이름으로 바뀐다(번역이 없는 언어는 영어).
    /// 이름을 바꾸거나 복제하면 null(사용자 프리셋)이 된다.
    /// </summary>
    public string? BuiltIn { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Favorite { get; set; }
    public int Params { get; set; } = (int)DefaultParams;
    public int Trials { get; set; } = DefaultTrials;
    public int Seconds { get; set; } = DefaultSeconds;
    /// <summary>후보 설정과 별도로 고정하는 점수용 주요 색 변화 거리.</summary>
    public ColorDistanceMode ScoreColorDistance { get; set; } = ColorDistanceMode.OkLchHue;
    public double ScoreHueWeight { get; set; } = 2;
    public ScoreWeights Weights { get; set; } = new();

    /// <summary>최적화가 팔레트 색 거리를 바꿔도 모든 후보를 재는 '주요 색 변화'의 고정 기준.</summary>
    [JsonIgnore]
    public ColorMetric ScoreMetric => new(ScoreColorDistance, ScoreHueWeight);

    /// <summary>이 프리셋으로 돌릴 자동 최적화 설정(예산은 칸 범위 안으로).</summary>
    public PixelateOptimizeOptions Options() => new()
    {
        Params = (OptimizeParam)Params,
        MaxTrials = Math.Clamp(Trials, MinBudget, MaxTrials),
        MaxTime = TimeSpan.FromSeconds(Math.Clamp(Seconds, MinBudget, MaxSeconds)),
        Weights = Weights,
        ScoreMetric = ScoreMetric,
    };

    /// <summary>이름만 <paramref name="name"/>인 사본(사용자 프리셋 — 기본 프리셋 id 없음).</summary>
    public OptimizePreset Clone(string name)
    {
        var copy = (OptimizePreset)MemberwiseClone();
        copy.Name = name;
        copy.BuiltIn = null;
        return copy;
    }

    /// <summary>기본 프리셋 id → 이름 문구 키(null = 언어와 무관한 이름 그대로).</summary>
    private static readonly (string Id, string? Key, string Fixed)[] BuiltInIds =
    [
        ("oklch1000", null, "OKLCH1000"),
        ("oklab", null, "OKLab"),
        ("quick", nameof(UiStrings.OptimizePreset_BuiltInQuick), ""),
        ("faithful", nameof(UiStrings.OptimizePreset_BuiltInFaithful), ""),
        ("clean", nameof(UiStrings.OptimizePreset_BuiltInClean), ""),
        ("shade", nameof(UiStrings.OptimizePreset_BuiltInShade), ""),
    ];

    /// <summary>화면 언어의 기본 프리셋 이름(번역이 없으면 영어 — <see cref="UiStrings"/>의 대체 규칙).</summary>
    private static string BuiltInName(string id)
    {
        var entry = BuiltInIds.First(e => e.Id == id);
        return entry.Key is null ? entry.Fixed : UiStrings.GetResourceString(entry.Key) ?? entry.Fixed;
    }

    /// <summary>내장 언어(한국어·영어)와 지금 화면 언어에서 그 기본 프리셋이 가질 수 있는 이름들.</summary>
    private static IEnumerable<string> KnownNames(string id)
    {
        var entry = BuiltInIds.First(e => e.Id == id);
        if (entry.Key is null) return [entry.Fixed];
        return new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en") }
            .Select(c => UiStrings.ResourceManager.GetString(entry.Key, c))
            .Append(BuiltInName(id))
            .OfType<string>();
    }

    /// <summary>
    /// id가 없는 예전 기본 프리셋을 이름으로 찾아 id를 붙이고(같은 id는 하나에만), id가 있는 프리셋의 이름을 지금 화면 언어로 바꾼다.
    /// </summary>
    public static void LocalizeBuiltIns(List<OptimizePreset> presets)
    {
        foreach (var (id, _, _) in BuiltInIds)
        {
            if (presets.Any(p => p.BuiltIn == id)) continue;
            var names = KnownNames(id).ToList();
            if (presets.FirstOrDefault(p => p.BuiltIn is null && names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) is { } match)
                match.BuiltIn = id;
        }
        foreach (var preset in presets)
            if (preset.BuiltIn is { } id && BuiltInIds.Any(e => e.Id == id)) preset.Name = BuiltInName(id);
    }

    /// <summary>
    /// 기본 자동 최적화 프리셋(코드에 고정 — 목록에서 지워도 '기본값으로 초기화'로 되돌림). 빠른 확인만 체크 없이 넣어 두고 나머지는 체크해 함께 돈다.
    /// 빠른 확인 외에는 10000번·120초.
    /// </summary>
    public static List<OptimizePreset> BuiltIns()
    {
        List<OptimizePreset> presets =
        [
            new() { BuiltIn = "oklch1000", Params = (int)OptimizeParam.All, Trials = 10000, ScoreColorDistance = ColorDistanceMode.OkLchHue, ScoreHueWeight = 1000 },
            new() { BuiltIn = "oklab", Trials = 10000, ScoreColorDistance = ColorDistanceMode.OkLab },
            new() { BuiltIn = "quick", Enabled = false, Params = (int)OptimizeParam.Default, Trials = 1000, Seconds = 30 },
            new() { BuiltIn = "faithful", Trials = 10000, Weights = new ScoreWeights { Fidelity = 8, ColorShift = 4 } },
            new() { BuiltIn = "clean", Trials = 10000, Weights = new ScoreWeights { Fidelity = 3, Orphans = 3, OutlineGaps = 2, OutlineThin = 6 } },
            new() { BuiltIn = "shade", Trials = 10000, ScoreColorDistance = ColorDistanceMode.OkLchHue, ScoreHueWeight = 20 },
        ];
        foreach (var preset in presets) preset.Name = BuiltInName(preset.BuiltIn!);
        return presets;
    }

    /// <summary>
    /// 기본 프리셋을 처음 값(체크 포함)으로 되돌리고 지운 것은 뒤에 다시 넣는다. 직접 만든 프리셋과 즐겨찾기 표시는 그대로 둔다.
    /// </summary>
    public static void RestoreBuiltIns(List<OptimizePreset> presets)
    {
        LocalizeBuiltIns(presets);
        foreach (var builtIn in BuiltIns())
        {
            int index = presets.FindIndex(p => p.BuiltIn == builtIn.BuiltIn);
            if (index < 0) presets.Add(builtIn);
            else
            {
                builtIn.Favorite = presets[index].Favorite;
                presets[index] = builtIn;
            }
        }
    }
}

/// <summary>정리할 그림: 픽셀, 이름(레이어·팔레트 이름에 씀), 파일 경로(파일에서 왔으면).</summary>
public sealed record PixelateInput(PixelImage Image, string Name, string? Path);

/// <summary>적용: 고른 결과와 넣는 방법·AI 다듬기 설정.</summary>
public sealed record PixelateOutcome(
    PixelateInput Input,
    PixelateResult Result,
    bool NewDocument,
    int FrameDurationMs,
    bool SavePalette,
    bool AiTouchUp,
    string AiPrompt,
    string AiProvider,
    bool AddReference);

/// <summary>
/// AI 그림 픽셀로 정리: 왼쪽은 단계별 설정(배경 → 프레임 → 격자 → 크기 → 칸 색 → 팔레트 → 정리 → 프레임 맞추기, 바꾸면 0.3초 뒤 다시 돌림 —
/// 기본은 얇은 막대로 접혀 있고 눌러 펼침), 오른쪽은 원본(확대·이동, 찾은 격자·배경 지운 모습), 고른 후보의 큰 미리보기(프레임 재생 + 같은 프레임 원본), 점수 순 후보 목록.
/// 후보는 배경에서 여러 스레드로 만들고 끝나는 대로 보이며, 가장 높은 점수를 미리 고른다. 창은 모달이 아니다(채팅에 후보 평가를 부탁하며 열어 둘 수 있음).
/// 원본 없이 열 수 있고(AI 메뉴), 창에 그림 파일 끌어 놓기·Ctrl+V 붙여 넣기·파일 찾기로 원본을 넣거나 바꾼다(<see cref="SetSource"/>).
/// </summary>
public sealed class PixelateDialog : DialogBase
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    private readonly AppSettings _settings;
    private readonly PixelateOptions _opt;
    /// <summary>원본(null = 아직 없음 — 빈 화면에서 끌어 놓기·붙여 넣기·파일 찾기를 기다림).</summary>
    private PixelateInput? _input;
    private readonly bool _canInsertLayer;
    private readonly Action<PixelateOutcome> _apply;
    /// <summary>"새 탭에 추가": 고른 결과 크기의 새 문서를 새 탭에(넣기 설정과 관계없이) 만들고 닫는다.</summary>
    private readonly Button _newTab;
    private readonly Action<string, string, IReadOnlyList<PixelImage>> _askAi;
    /// <summary>파일 찾기의 처음 폴더(주 창 파일 브라우저의 폴더, 없으면 마지막 폴더).</summary>
    private readonly Func<string?> _browseFolder;
    /// <summary>클립보드 그림 읽기(주 창의 붙여넣기와 같은 길 — PNG·비트맵, 없으면 앱 안 클립보드).</summary>
    private readonly Func<PixelImage?> _readClipboardImage;
    /// <summary>그림 복사(주 창의 복사와 같은 길 — 앱 안 클립보드와 시스템 클립보드에 PNG로).</summary>
    private readonly Action<PixelImage> _copyImage;
    /// <summary>레이어 여러 프레임을 이은 원본이면 기억한 프레임 나누기 설정(닫거나 원본을 바꿀 때 되돌림).</summary>
    private FrameSettings? _restoreFrames;
    /// <summary>처음 설정을 받아 열었음(채팅의 "변환 설정 보기"): 닫을 때 단계 설정을 기억하지 않는다(사용자가 기억한 설정 그대로).</summary>
    private readonly bool _presetGiven;
    private readonly List<(string Name, IReadOnlyList<Color32> Colors)> _palettes;
    private readonly string _presetsPath;
    private List<PixelatePreset> _userPresets;

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _play = new();
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _loading;
    private PixelateSource? _prepared;
    private readonly List<PixelateCandidate> _candidates = new();
    private PixelateCandidate? _selected;
    private bool _userPicked;
    private readonly Stopwatch _runClock = new();

    // 미리보기
    private readonly ZoomImageView _sourceView;
    private readonly FramePreview _preview;
    /// <summary>크게 보기 겹판(<see cref="OpenZoom"/>).</summary>
    private readonly ZoomOverlay _zoom;
    private readonly WrapPanel _tiles;
    private readonly TextBlock _status, _selectedInfo, _breakdown, _largeWarning;
    private readonly CheckBox _showGrid, _showMask;
    /// <summary>
    /// 원본 아래 막대의 결과 크기: 자동(= 4단계 방식이 고정 크기가 아님) · 가로×세로 · 비율 묶기.
    /// 4단계 칸과 같은 설정을 함께 쓴다 — 막대를 바꾸면 4단계 칸에 적고, 4단계 칸(불러오기 포함)이 바뀌면 <see cref="SyncBarSize"/>가 막대를 맞춘다.
    /// </summary>
    private readonly CheckBox _barAuto;
    private readonly NumericUpDown _barW, _barH;
    private readonly ToggleButton _barAspect;
    /// <summary>막대를 설정에 맞추는 중(막대 쪽 이벤트 무시).</summary>
    private bool _barSyncing;
    /// <summary>자동을 다시 켤 때 돌아갈 방식(마지막으로 쓴, 고정 크기가 아닌 방식).</summary>
    private OutputSizeMode _barAutoMode = OutputSizeMode.FromScale;
    private readonly NumericUpDown _fps;
    /// <summary>프레임 띠(큰 미리보기·크게 보기 — 썸네일 <see cref="_thumbs"/>를 함께 씀).</summary>
    private readonly FrameStrip _strip, _zoomStrip;
    /// <summary>크게 보기의 프레임 띠 스크롤(프레임이 하나면 숨김).</summary>
    private readonly ScrollViewer _zoomStripScroll;
    private List<WriteableBitmap> _thumbs = [];
    private readonly Button _playButton;
    /// <summary>색 칸 묶음(큰 미리보기·크게 보기 — 같은 뺀 색·고정 색으로 함께 채움, <see cref="ShowColors"/>).</summary>
    private readonly ColorsBlock _colors, _zoomColors;
    /// <summary>
    /// 뺀 색(<see cref="PaletteSettings.ExcludedColors"/> — 왼쪽 칸에 없는 설정이라 불러오기·읽기로 함께 다닌다). 지금 결과의 뒤처리라 바꿔도 다시 돌리지 않고
    /// 후보마다 다시 뺀다(<see cref="ApplyExclusions"/>).
    /// </summary>
    private IReadOnlyList<string> _excluded = [];
    /// <summary>고정 색(<see cref="PaletteSettings.PinnedColors"/> — 원본 칸에서 집음, 바꾸면 다시 돌린다).</summary>
    private IReadOnlyList<string> _pinned = [];
    /// <summary>원본에서 색 추가(스포이드) 중: 원본 보기들에서 칸을 누르면 그 칸 색을 고정 색으로(<see cref="PickCell"/>).</summary>
    private bool _picking;
    /// <summary>재생을 사용자가 멈췄는지(후보를 바꿔도 멈춘 채).</summary>
    private bool _paused;

    // 프리셋
    private readonly ComboBox _preset;

    // 1. 배경
    private readonly ComboBox _bgMode;
    private readonly Button _bgColor;
    private Color32 _bgColorValue = Color32.Black;
    private readonly NumericUpDown _bgTol, _bgAlpha, _bgSpecks;
    private readonly CheckBox _bgAutoTol, _bgConnected, _bgKeepAlpha, _bgSticker, _bgKeep;
    private readonly TextBlock _bgInfo;
    // 2. 프레임
    private readonly ComboBox _frMode, _frAlign;
    private readonly NumericUpDown _frCols, _frRows, _frMerge, _frMergeDist;
    private readonly CheckBox _frCommon;
    private readonly TextBlock _frInfo;
    // 3. 격자
    private readonly ComboBox _grMode;
    private readonly NumericUpDown _grScale, _grOffX, _grOffY, _grJitter, _grAdjust;
    private readonly CheckBox _grPerFrame, _grLocal;
    /// <summary>칸 경계 지역 보정: 최대 이동(칸 %)·띠 너비(칸)·최소 평균 칸 크기(px).</summary>
    private readonly NumericUpDown _grLocalShift, _grLocalBand, _grLocalMinCell;
    private readonly ComboBox _grSource;
    private readonly TextBlock _grInfo;
    private GridFit? _detected;
    // 4. 크기
    private readonly ComboBox _szMode;
    private readonly NumericUpDown _szW, _szH, _szPad, _szFactor;
    private readonly CheckBox _szAspect;
    private readonly TextBlock _szInfo;
    // 5. 칸 색
    private readonly ComboBox _dsMode;
    private readonly NumericUpDown _dsBorder, _dsAlpha, _dsSnap;
    // 6. 팔레트
    private readonly ComboBox _palMethod, _palWhich, _palDither, _palSource, _palDistance;
    private readonly NumericUpDown _palColors, _palDark, _palEdge, _palHueWeight, _palHueChroma, _palHueMin, _palHueArea;
    private readonly CheckBox _palAuto, _palShared, _palSnap, _palDistinct, _palShade;
    private readonly TextBlock _palInfo;
    // 7. 정리
    private readonly CheckBox _clOrphans, _clHoles, _clJaggies;
    private readonly ComboBox _clFit;
    /// <summary>지금 후보들을 만든 설정의 외곽선 맞추기(미리 고르는 후보는 이 방식 중 최고점).</summary>
    private OutlineFit _runFit;
    private readonly NumericUpDown _clContrast, _clVecTol, _clVecBlob;
    private readonly ComboBox _clOutline;
    // 8. 프레임 맞추기
    private readonly CheckBox _coTemporal;
    private readonly NumericUpDown _coContrast;
    // 후보·고급
    private readonly NumericUpDown _candCount;
    private readonly CheckBox _vScale, _vPalette, _vDownsample, _vCleanup, _vDither, _vFit;
    // 넣기·AI
    private readonly RadioButton _outLayer, _outDoc;
    private readonly NumericUpDown _outDuration;
    private readonly CheckBox _outPalette;
    private readonly CheckBox _aiTouch, _aiReference;
    private readonly ComboBox _aiProvider;
    private readonly TextBox _aiPrompt;
    private readonly NumericUpDown _aiRankCount;
    // 자동 최적화
    /// <summary>최적화 설정 창(프리셋 목록·편집, '불러오면 자동 최적화', 시도 기록 — 열려 있을 때만).</summary>
    private Window? _optSettingsWindow;
    /// <summary>설정 창의 프리셋 목록·편집(창이 열려 있을 때만).</summary>
    private OptimizePresetsPanel? _optPresets;
    /// <summary>"자동 최적화"(누르면 체크한 프리셋을 모두 차례로 돌림) ↔ 도는 동안 "취소".</summary>
    private readonly Button _optRun;
    /// <summary>최적화 전 설정으로 되돌리기(한 번 돌린 뒤 그래프 옆).</summary>
    private readonly Button _optRevert;
    /// <summary>그래프 옆 한 줄: 도는 동안 진행(프리셋 이름·k/n·돌림 번호), 끝나면 "프리셋 k/n개 · t초"(풍선에 프리셋마다 결과 한 줄).</summary>
    private readonly TextBlock _optText;
    /// <summary>지금 도는 프리셋의 시도마다 최고 점수(프리셋이 바뀌면 새로).</summary>
    private readonly OptimizeGraph _optGraph;
    /// <summary>설정 창에서 고른 프리셋의 마지막 시도 기록 표.</summary>
    private readonly Grid _optTable;
    private CancellationTokenSource? _optCts;
    private int _optGeneration;
    /// <summary>지금 도는 프리셋의 끝난 시도 수(진행 알림은 순서가 섞일 수 있어 가장 큰 값).</summary>
    private int _optDone;
    /// <summary>지금 도는 프리셋: 이름, 번호(1부터), 돌리는 프리셋 수.</summary>
    private (string Name, int Index, int Count) _optRunning;
    /// <summary>마지막 최적화에서 프리셋마다 결과와 그때 바꿔 본 항목(시도 기록 표).</summary>
    private readonly Dictionary<OptimizePreset, (PixelateOptimizeResult Result, OptimizeParam Params)> _optResults = new(ReferenceEqualityComparer.Instance);
    /// <summary>'불러오면 자동 최적화'(<see cref="PixelateOptions.AutoOptimizeOnLoad"/> — 설정 창에 올림).</summary>
    private readonly CheckBox _optAuto;
    /// <summary>원본을 넣었고 첫 후보가 끝나면 자동 최적화를 돌릴 차례(<see cref="OnFinished"/>에서 한 번만).</summary>
    private bool _autoOptimizePending;
    /// <summary>최적화 전 설정(되돌리기).</summary>
    private PixelateSettings? _optRevertTo;
    /// <summary>최적화가 넣은 후보와 그 프리셋·이름·멈춘 까닭(칸 제목 = 프리셋 이름, 아래에 "최적화(점수)"·"시간초과(점수)" 같은 표시, 우클릭 = 이 설정만 다시 돌리기).</summary>
    private readonly Dictionary<PixelateCandidate, OptimizedTile> _optimized = new(ReferenceEqualityComparer.Instance);
    /// <summary>마지막으로 보통 후보를 잰 점수 기준(설정 창에서 바뀌면 다시 돌림).</summary>
    private (ScoreWeights Weights, ColorDistanceMode Distance, double HueWeight) _scoredWith;

    // 원본 넣기·설정 칸 접기
    /// <summary>원본이 없을 때 가운데 안내(끌어 놓기·붙여 넣기·파일 찾기).</summary>
    private readonly Control _emptyState;
    private readonly Button _replaceSource;
    /// <summary>왼쪽 설정 칸(펼친 너비 <see cref="SettingsWidth"/> + 접기 막대 <see cref="SettingsBarWidth"/>).</summary>
    private readonly ColumnDefinition _settingsColumn;
    private readonly Control _settingsHost;
    private readonly Button _settingsToggle;
    private readonly IconView _settingsChevron;
    private readonly DispatcherTimer _settingsAnim = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Stopwatch _settingsClock = new();
    private double _settingsFrom, _settingsTo;
    private bool _settingsExpanded;
    private const double SettingsWidth = 380, SettingsBarWidth = 24, SettingsAnimMs = 200;

    /// <param name="input">원본(null = 빈 창 — 끌어 놓기·붙여 넣기·파일 찾기로 넣음).</param>
    /// <param name="browseFolder">파일 찾기의 처음 폴더.</param>
    /// <param name="readClipboardImage">클립보드 그림(없으면 null).</param>
    /// <param name="preset">처음 단계 설정(AI 그리기 결과를 다시 조정 — 그 설정으로 후보를 만들고 자동 최적화는 다시 돌리지 않음). null = 기억한 설정.</param>
    public PixelateDialog(AppSettings settings, PixelateInput? input, IEnumerable<NamedPalette> palettes, bool canInsertLayer,
        Action<PixelateOutcome> apply, Action<string, string, IReadOnlyList<PixelImage>> askAi,
        Func<string?> browseFolder, Func<PixelImage?> readClipboardImage, Action<PixelImage> copyImage, int sourceFrames = 1, PixelateSettings? preset = null)
        : base(TitleFor(input), UiStrings.Pixelate_Apply)
    {
        _settings = settings;
        _opt = settings.Pixelate ??= new PixelateOptions();
        _opt.Migrate();
        _input = input;
        _canInsertLayer = canInsertLayer;
        _apply = apply;
        _askAi = askAi;
        _browseFolder = browseFolder;
        _readClipboardImage = readClipboardImage;
        _copyImage = copyImage;
        _palettes = BuildPalettes(palettes);
        _presetsPath = Path.Combine(Platform.AppDataDirectory, "Gpixel", "Pixelate", PixelatePresets.FileName);
        _userPresets = LoadUserPresets();

        CanResize = true;
        SizeToContent = SizeToContent.Manual;
        Width = Math.Max(900, _opt.Width);
        Height = Math.Max(600, _opt.Height);
        MinWidth = 900;
        MinHeight = 600;
        ShowInTaskbar = true;

        var initial = preset ?? PixelatePresets.ParseSettings(settings.PixelateSettings) ?? new PixelateSettings();
        _presetGiven = preset is not null;
        if (sourceFrames > 1 && preset is null)
        {
            // 레이어의 여러 프레임을 가로로 이은 그림: 칸마다 한 프레임(기억한 나누기 설정은 닫을 때 되돌림).
            _restoreFrames = initial.Frames;
            initial = initial with { Frames = initial.Frames with { Mode = FrameSplitMode.Grid, Columns = sourceFrames, Rows = 1 } };
        }

        // ── 프리셋
        _preset = Ui.Combo(170);
        _preset.SelectionChanged += (_, _) => OnPresetChosen();
        var savePreset = Ui.IconButton(VectorIcon.Plus, UiStrings.Pixelate_Preset_Save, (_, _) => SavePreset());
        var deletePreset = Ui.IconButton(VectorIcon.Trash, UiStrings.Pixelate_Preset_Delete, (_, _) => DeletePreset());
        FillPresets();

        // ── 1. 배경
        _bgMode = Ui.Combo(170, UiStrings.Pixelate_Bg_None, UiStrings.Pixelate_Bg_Auto, UiStrings.Pixelate_Bg_Pick);
        _bgColor = new Button { Width = 40, Height = 22, Margin = new Thickness(3) }.Tip(UiStrings.Pixelate_Bg_Color);
        _bgColor.Click += (_, _) =>
        {
            if (Ui.PickColor(this, _bgColorValue, UiStrings.Pixelate_Bg_Color) is { } c) { _bgColorValue = c.WithAlpha(255); UpdateColorButton(); Changed(); }
        };
        _bgTol = Num(0, 441, 24, 0);
        _bgAutoTol = Ui.Check(UiStrings.Pixelate_Auto);
        _bgConnected = Ui.Check(UiStrings.Pixelate_Bg_Connected).Tip(UiStrings.Pixelate_Bg_Connected_Tip);
        _bgKeepAlpha = Ui.Check(UiStrings.Pixelate_Bg_KeepAlpha);
        _bgSticker = Ui.Check(UiStrings.Pixelate_Bg_Sticker, true).Tip(UiStrings.Pixelate_Bg_Sticker_Tip);
        _bgKeep = Ui.Check(UiStrings.Pixelate_Bg_Keep).Tip(UiStrings.Pixelate_Bg_Keep_Tip);
        _bgAlpha = Num(1, 255, 128, 0);
        _bgSpecks = Num(0, 10000, 24, 0);
        _bgInfo = Info();
        var bgBody = Ui.Stack(
            Ui.Row(Ui.Label(UiStrings.Pixelate_Mode), _bgMode, _bgColor),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Bg_Tolerance), _bgTol, _bgAutoTol),
            _bgConnected, _bgKeepAlpha, _bgSticker, _bgKeep,
            Ui.Row(Ui.Label(UiStrings.Pixelate_Bg_AlphaThreshold), _bgAlpha, Ui.Label(UiStrings.Pixelate_Bg_Specks), _bgSpecks),
            _bgInfo);

        // ── 2. 프레임
        _frMode = Ui.Combo(170, UiStrings.Pixelate_Frames_Single, UiStrings.Pixelate_Frames_Auto, UiStrings.Pixelate_Frames_Grid);
        _frCols = Num(1, 64, 4, 0);
        _frRows = Num(1, 64, 1, 0);
        _frMerge = Num(0, 100, 12, 0);
        _frMergeDist = Num(0, 4096, 48, 0);
        _frAlign = Ui.Combo(130, UiStrings.Pixelate_Align_BottomCenter, UiStrings.Pixelate_Align_Center, UiStrings.Pixelate_Align_Keep);
        _frCommon = Ui.Check(UiStrings.Pixelate_Frames_Common);
        _frInfo = Info();
        var frBody = Ui.Stack(
            Ui.Row(Ui.Label(UiStrings.Pixelate_Mode), _frMode),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Frames_Columns), _frCols, Ui.Label(UiStrings.Pixelate_Frames_Rows), _frRows),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Frames_Merge), _frMerge, Ui.Label(UiStrings.Pixelate_Frames_MergeDistance), _frMergeDist),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Frames_Align), _frAlign),
            _frCommon, _frInfo);

        // ── 3. 격자
        _grMode = Ui.Combo(170, UiStrings.Pixelate_Grid_Auto, UiStrings.Pixelate_Grid_Manual);
        _grScale = Num(PixelateRanges.Scale, 8, 2, 0.25m);
        _grOffX = Num(PixelateRanges.Offset, 0, 1, 0.5m);
        _grOffY = Num(PixelateRanges.Offset, 0, 1, 0.5m);
        _grJitter = Num(0, 45, 15, 0);
        _grAdjust = Num(PixelateRanges.ScaleAdjust, 0, 2, 0.25m).Tip(UiStrings.Pixelate_Grid_Adjust_Tip);
        _grPerFrame = Ui.Check(UiStrings.Pixelate_Grid_PerFrame);
        _grLocal = Ui.Check(UiStrings.Pixelate_Grid_Local, true).Tip(UiStrings.Pixelate_Grid_Local_Tip);
        _grLocalShift = Num(PixelateRanges.LocalShift, 35, 0, 5, unit: 100).Tip(UiStrings.Pixelate_Grid_LocalShift_Tip);
        _grLocalBand = Num(PixelateRanges.LocalBand, 2, 0).Tip(UiStrings.Pixelate_Grid_LocalBand_Tip);
        _grLocalMinCell = Num(PixelateRanges.LocalMinCell, 4, 2, 0.25m).Tip(UiStrings.Pixelate_Grid_LocalMinCell_Tip);
        _grSource = Ui.Combo(190, UiStrings.Pixelate_Grid_SourceAuto, UiStrings.Pixelate_Grid_SourceOutline, UiStrings.Pixelate_Grid_SourceEdges, UiStrings.Pixelate_Grid_SourceBlocks)
            .Tip(UiStrings.Pixelate_Grid_Source_Tip);
        _grInfo = Info();
        var useDetected = Ui.Button(UiStrings.Pixelate_Grid_UseDetected, (_, _) => UseDetectedGrid());
        var grBody = Ui.Stack(
            Ui.Row(Ui.Label(UiStrings.Pixelate_Mode), _grMode, useDetected),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Grid_Scale), _grScale, Ui.Label(UiStrings.Pixelate_Grid_Offset), _grOffX, _grOffY),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Grid_Jitter), _grJitter),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Grid_Adjust), _grAdjust),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Grid_Source), _grSource),
            _grPerFrame,
            _grLocal,
            Ui.Row(Ui.Label(UiStrings.Pixelate_Grid_LocalShift), _grLocalShift, Ui.Label(UiStrings.Pixelate_Grid_LocalBand), _grLocalBand),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Grid_LocalMinCell), _grLocalMinCell),
            _grInfo);

        // ── 4. 크기
        _szMode = Ui.Combo(170, UiStrings.Pixelate_Size_FromScale, UiStrings.Pixelate_Size_Fixed, UiStrings.Pixelate_Size_Factor);
        _szW = Num(4, 1024, 32, 0).Presets(16, 24, 32, 48, 64, 96, 128);
        _szH = Num(4, 1024, 32, 0).Presets(16, 24, 32, 48, 64, 96, 128);
        _szAspect = Ui.Check(UiStrings.Pixelate_Size_KeepAspect);
        _szPad = Num(0, 64, 1, 0);
        _szFactor = Num(PixelateRanges.Factor, 0.125m, 3, 0.005m).Presets(0.0625m, 0.1m, 0.125m, 0.25m, 0.5m);
        _szInfo = Info();
        var szBody = Ui.Stack(
            Ui.Row(Ui.Label(UiStrings.Pixelate_Mode), _szMode),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Size_WH), _szW, Ui.Label(UiStrings.Pixelate_Size_By), _szH, _szAspect),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Size_FactorLabel), _szFactor, Ui.Label(UiStrings.Pixelate_Size_Padding), _szPad),
            _szInfo);

        // ── 5. 칸 색
        _dsMode = Ui.Combo(170, UiStrings.Pixelate_Ds_Mode, UiStrings.Pixelate_Ds_Median, UiStrings.Pixelate_Ds_Center,
            UiStrings.Pixelate_Ds_Mean, UiStrings.Pixelate_Ds_CenterWeighted, UiStrings.Pixelate_Ds_ClusterWeighted);
        _dsBorder = Num(PixelateRanges.BorderFraction, 20, 0, unit: 100);
        _dsAlpha = Num(PixelateRanges.AlphaThreshold, 50, 0, unit: 100);
        _dsSnap = Num(PixelateRanges.SnapStep, 20, 0);
        var dsBody = Ui.Stack(
            Ui.Row(Ui.Label(UiStrings.Pixelate_Mode), _dsMode),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Ds_Border), _dsBorder),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Ds_Alpha), _dsAlpha),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Ds_Snap), _dsSnap));

        // ── 6. 팔레트
        _palMethod = Ui.Combo(170, UiStrings.Pixelate_Pal_KMeans, UiStrings.Pixelate_Pal_MedianCut, UiStrings.Pixelate_Pal_Existing, UiStrings.Pixelate_Pal_Keep);
        _palColors = Num(PixelateRanges.Colors, 16, 0).Presets(4, 8, 12, 16, 24, 32, 48, 64);
        _palAuto = Ui.Check(UiStrings.Pixelate_Auto);
        _palWhich = Ui.Combo(220);
        if (_palettes.Count == 0) _palWhich.Items.Add(UiStrings.Pixelate_Pal_NoPalettes);
        foreach (var p in _palettes) _palWhich.Items.Add(p.Name);
        _palWhich.SelectedIndex = 0;
        _palShared = Ui.Check(UiStrings.Pixelate_Pal_Shared);
        _palSource = Ui.Combo(150, UiStrings.Pixelate_Pal_Source_Original, UiStrings.Pixelate_Pal_Source_Mean).Tip(UiStrings.Pixelate_Pal_Source_Tip);
        _palEdge = Num(PixelateRanges.EdgeWeight, 0.25m, 2, 0.05m).Tip(UiStrings.Pixelate_Pal_EdgeWeight_Tip);
        _palDither = Ui.Combo(120, UiStrings.Pixelate_Dither_None, UiStrings.Pixelate_Dither_Bayer2, UiStrings.Pixelate_Dither_Bayer4);
        _palSnap = Ui.Check(UiStrings.Pixelate_Pal_SnapOutline);
        _palDark = Num(PixelateRanges.OutlineDarkness, 0.24m, 2, 0.01m);
        _palDistance = Ui.Combo(170, UiStrings.Pixelate_Pal_Distance_OkLab, UiStrings.Pixelate_Pal_Distance_Ciede2000, UiStrings.Pixelate_Pal_Distance_OkLchHue)
            .Tip(UiStrings.Pixelate_Pal_Distance_Tip);
        _palHueWeight = Num(PixelateRanges.HueWeight, 2m, 2, 0.25m).Tip(UiStrings.Pixelate_Pal_HueWeight_Tip);
        _palShade = Ui.Check(UiStrings.Pixelate_Pal_ShadeFamily).Tip(UiStrings.Pixelate_Pal_ShadeFamily_Tip);
        _palDistinct = Ui.Check(UiStrings.Pixelate_Pal_Distinct).Tip(UiStrings.Pixelate_Pal_Distinct_Tip);
        _palHueChroma = Num(PixelateRanges.DistinctMinChroma, 0.04m, 2, 0.01m).Tip(UiStrings.Pixelate_Pal_DistinctChroma_Tip);
        _palHueMin = Num(PixelateRanges.DistinctMinHue, 20m, 0, 1m).Tip(UiStrings.Pixelate_Pal_DistinctHue_Tip);
        _palHueArea = Num(PixelateRanges.DistinctMinArea, 4m, 0, 1m).Tip(UiStrings.Pixelate_Pal_DistinctArea_Tip);
        _palInfo = Info();
        var palBody = Ui.Stack(
            Ui.Row(Ui.Label(UiStrings.Pixelate_Mode), _palMethod),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Pal_Colors), _palColors, _palAuto),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Pal_Which), _palWhich),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Pal_Source), _palSource),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Pal_EdgeWeight), _palEdge),
            _palShared,
            Ui.Row(Ui.Label(UiStrings.Pixelate_Pal_Dither), _palDither),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Pal_Distance), _palDistance, _palHueWeight),
            _palShade,
            _palDistinct,
            Ui.Row(Ui.Label(UiStrings.Pixelate_Pal_DistinctChroma), _palHueChroma, Ui.Label(UiStrings.Pixelate_Pal_DistinctHue), _palHueMin,
                Ui.Label(UiStrings.Pixelate_Pal_DistinctArea), _palHueArea),
            Ui.Row(_palSnap, _palDark),
            _palInfo);

        // ── 7. 정리
        _clOrphans = Ui.Check(UiStrings.Pixelate_Clean_Orphans);
        _clContrast = Num(0, 1, 0.18m, 2, 0.01m).Tip(UiStrings.Pixelate_Clean_Orphans_Tip);
        _clHoles = Ui.Check(UiStrings.Pixelate_Clean_Holes);
        _clOutline = Ui.Combo(190, UiStrings.Pixelate_Outline_None, UiStrings.Pixelate_Outline_Unify, UiStrings.Pixelate_Outline_Closed);
        _clJaggies = Ui.Check(UiStrings.Pixelate_Clean_Jaggies);
        _clFit = Ui.Combo(170, UiStrings.Pixelate_Fit_Shrink, UiStrings.Pixelate_Fit_Thin, UiStrings.Pixelate_Fit_Off, UiStrings.Pixelate_Fit_Vector).Tip(UiStrings.Pixelate_Fit_Tip);
        _clVecTol = Num(PixelateRanges.VectorTolerance, 0.6m, 2, 0.05m).Tip(UiStrings.Pixelate_Clean_VectorTolerance_Tip);
        _clVecBlob = Num(PixelateRanges.VectorBlobCells, 6m, 0, 1m).Tip(UiStrings.Pixelate_Clean_VectorBlob_Tip);
        var clBody = Ui.Stack(
            Ui.Row(_clOrphans, Ui.Label(UiStrings.Pixelate_Clean_Contrast), _clContrast),
            _clHoles,
            Ui.Row(Ui.Label(UiStrings.Pixelate_Fit), _clFit),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Clean_VectorTolerance), _clVecTol, Ui.Label(UiStrings.Pixelate_Clean_VectorBlob), _clVecBlob),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Clean_Outline), _clOutline),
            _clJaggies);

        // ── 8. 프레임 맞추기
        _coTemporal = Ui.Check(UiStrings.Pixelate_Cons_Temporal);
        _coContrast = Num(0, 1, 0.12m, 2, 0.01m);
        var coBody = Ui.Stack(Ui.Row(_coTemporal, Ui.Label(UiStrings.Pixelate_Clean_Contrast), _coContrast));

        // ── 후보
        _candCount = Num(1, 24, _opt.CandidateCount, 0).Presets(1, 4, 8, 12, 16);
        var vary = (CandidateVary)_opt.Vary;
        _vScale = Ui.Check(UiStrings.Pixelate_Vary_Scale, vary.HasFlag(CandidateVary.Scale));
        _vPalette = Ui.Check(UiStrings.Pixelate_Vary_Palette, vary.HasFlag(CandidateVary.PaletteSize));
        _vDownsample = Ui.Check(UiStrings.Pixelate_Vary_Downsample, vary.HasFlag(CandidateVary.DownsampleMode));
        _vCleanup = Ui.Check(UiStrings.Pixelate_Vary_Cleanup, vary.HasFlag(CandidateVary.Cleanup));
        _vDither = Ui.Check(UiStrings.Pixelate_Vary_Dither, vary.HasFlag(CandidateVary.Dither));
        _vFit = Ui.Check(UiStrings.Pixelate_Vary_OutlineFit, vary.HasFlag(CandidateVary.OutlineFit));
        var candBody = Ui.Stack(
            Ui.Row(Ui.Label(UiStrings.Pixelate_Cand_Count), _candCount),
            Ui.Label(UiStrings.Pixelate_Cand_Vary),
            new WrapPanel { Children = { _vScale, _vPalette, _vDownsample, _vCleanup, _vDither, _vFit } });

        // ── 넣기
        _outLayer = new RadioButton { GroupName = "pixelate-out", Content = UiStrings.Pixelate_Out_NewLayer, Foreground = AppTheme.TextBrush, IsEnabled = canInsertLayer };
        _outDoc = new RadioButton { GroupName = "pixelate-out", Content = UiStrings.Pixelate_Out_NewDocument, Foreground = AppTheme.TextBrush };
        (_opt.NewDocument || !canInsertLayer ? _outDoc : _outLayer).IsChecked = true;
        _outDuration = Num(10, 10000, _opt.FrameDurationMs, 0).Presets(50, 80, 100, 125, 150, 200);
        _outPalette = Ui.Check(UiStrings.Pixelate_Out_SavePalette, _opt.SavePalette);
        var outBody = Ui.Stack(_outLayer, _outDoc, Ui.Row(Ui.Label(UiStrings.Pixelate_Out_Duration), _outDuration), _outPalette);

        // ── AI
        _aiTouch = Ui.Check(UiStrings.Pixelate_Ai_TouchUp, _opt.AiTouchUp);
        _aiProvider = Ui.Combo(110, [.. Ai.AiProvider.All.Select(p => p.Name)]);
        _aiProvider.SelectedIndex = Math.Max(0, Ai.AiProvider.All.ToList().IndexOf(Ai.AiProvider.FromId(_opt.AiProvider)));
        _aiPrompt = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(_opt.AiPrompt) ? UiStrings.Pixelate_Ai_DefaultPrompt : _opt.AiPrompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 96,
            Margin = new Thickness(3),
        };
        _aiReference = Ui.Check(UiStrings.Pixelate_Ai_AddReference, _opt.AddReference);
        _aiRankCount = Num(1, 8, _opt.AiRankCount, 0);
        var rank = Ui.Button(VectorIcon.Sparkles, UiStrings.Pixelate_Ai_Rank, (_, _) => AskAiToRank());
        var resetPrompt = Ui.IconButton(VectorIcon.Refresh, UiStrings.Pixelate_Ai_ResetPrompt, (_, _) => _aiPrompt.Text = UiStrings.Pixelate_Ai_DefaultPrompt);
        var aiBody = Ui.Stack(
            Ui.Row(_aiTouch, Ui.Label(UiStrings.Pixelate_Ai_Model), _aiProvider),
            Ui.Row(Ui.Label(UiStrings.Pixelate_Ai_Prompt), resetPrompt),
            _aiPrompt,
            _aiReference,
            Ui.Row(rank, Ui.Label(UiStrings.Pixelate_Ai_RankCount), _aiRankCount));

        var left = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 8, 0) };
        left.Children.Add(Ui.Row(Ui.Label(UiStrings.Pixelate_Preset, bold: true), _preset, savePreset, deletePreset));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Background, bgBody, true));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Frames, frBody, true));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Grid, grBody, true));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Size, szBody, true));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Downsample, dsBody, false));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Palette, palBody, true));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Cleanup, clBody, true));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Consistency, coBody, false));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Candidates, candBody, true));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Output, outBody, true));
        left.Children.Add(Section(UiStrings.Pixelate_Section_Ai, aiBody, false));
        // 너비를 묶어 두어 접고 펼치는 동안 내용이 다시 줄 바꿈되지 않고 잘려 보이게 한다.
        var leftScroll = new ScrollViewer
        {
            Content = left,
            Width = SettingsWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _settingsHost = new Panel { ClipToBounds = true, Children = { leftScroll } };
        // 접기 막대(설정 칸 오른쪽 세로 단추): 설정 아이콘 + 펼침·접힘 방향 화살표.
        _settingsChevron = Ui.Icon(VectorIcon.ChevronRight, 14);
        _settingsToggle = new Button
        {
            Width = SettingsBarWidth - 6,
            Margin = new Thickness(0, 0, 6, 0),
            Background = AppTheme.Brush(Color.FromRgb(212, 173, 76)),
            BorderBrush = AppTheme.Brush(Color.FromRgb(162, 123, 43)),
            Foreground = AppTheme.Brush(Color.FromRgb(42, 35, 24)),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new StackPanel { Spacing = 8, Children = { Ui.Icon(VectorIcon.SlidersHorizontal, 14), _settingsChevron } },
        };
        _settingsToggle.Click += (_, _) => SetSettingsExpanded(!_settingsExpanded, animate: true);
        _settingsAnim.Tick += (_, _) => StepSettingsAnimation();
        var settingsColumn = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_settingsToggle, Dock.Right);
        settingsColumn.Children.Add(_settingsToggle);
        settingsColumn.Children.Add(_settingsHost);

        // ── 오른쪽: 원본 | 고른 후보(재생 + 같은 프레임 원본, 아래에 프레임 띠·재생 조절)
        _sourceView = new ZoomImageView();
        _preview = new FramePreview();
        _strip = new FrameStrip();
        _zoomStrip = new FrameStrip();
        _showGrid = Ui.Check(UiStrings.Pixelate_ShowGrid, _opt.ShowGrid);
        _showMask = Ui.Check(UiStrings.Pixelate_ShowMask, _opt.ShowMask);
        _showGrid.IsCheckedChanged += (_, _) => { _sourceView.ShowGrid = _showGrid.On(); };
        _showMask.IsCheckedChanged += (_, _) => { _sourceView.ShowAlt = _showMask.On(); };
        _sourceView.ShowGrid = _showGrid.On();
        _sourceView.ShowAlt = _showMask.On();
        _sourceView.HoverChanged += h => _preview.SetHover(h);
        // 크게 보기: 큰 미리보기의 결과·원본 그림을 누르면 열림(대화상자를 덮는 겹판 — 같은 프레임 번호·재생·후보·격자 보기·뺀 색을 함께 씀).
        _zoomColors = new ColorsBlock(this) { Margin = new Thickness(0, 4, 0, 0) };
        _zoomStripScroll = StripScroll(_zoomStrip);
        _zoom = new ZoomOverlay(_preview, TogglePlay, StepFrame, CloseZoom, _zoomColors, _zoomStripScroll);
        _zoomStrip.Picked += i => PickFrame(i, _zoom);
        _zoom.GridButton.IsChecked = _showGrid.On();
        _showGrid.IsCheckedChanged += (_, _) => _zoom.GridButton.IsChecked = _showGrid.IsChecked;
        _zoom.GridButton.IsCheckedChanged += (_, _) => _showGrid.IsChecked = _zoom.GridButton.IsChecked;
        _preview.OpenRequested += OpenZoom;
        // 큰 미리보기(결과·원본) 좌클릭 = 크게 보기, 결과 우클릭 = 그 픽셀 색 빼기/되살리기. 크게 보기의 결과 픽셀 좌클릭 = 쓰인 곳 보기, 우클릭 = 빼기/되살리기,
        // 원본 칸 좌클릭 = 그 칸이 된 결과 색 보기(스포이드 중이면 색 추가). 색 칸도 같다(좌클릭 = 쓰인 곳, 우클릭 = 빼기/되살리기·고정 해제).
        _zoom.View.ResultPixelClicked += OnResultPixel;
        _zoom.View.ResultPixelRightClicked += OnResultPixelRight;
        _preview.ResultPixelRightClicked += OnResultPixelRight;
        _zoom.View.SourceCellClicked += OnSourceCell;
        _pickPulse.Tick += OnPickPulse;
        // 원본에서 색 추가(스포이드): 왼쪽 큰 원본·큰 미리보기의 원본 칸·크게 보기의 원본 칸에서 칸을 누르면 고정 색으로.
        _sourceView.CellPicked += PickCell;
        // 원본 우클릭: 보이는 모습(배경 지운 모습이면 그것) 복사·PNG로 저장. 스포이드 중에는 우클릭이 스포이드 취소라 메뉴 없음.
        PopupMenus.Attach(_sourceView, SourceMenu);
        _preview.CellPicked += PickCell;
        _zoom.View.CellPicked += PickCell;
        _sourceView.PickCancelled += () => SetPicking(false);
        _preview.PickCancelled += () => SetPicking(false);
        _zoom.View.PickCancelled += () => SetPicking(false);
        ToolTip.SetTip(_preview, UiStrings.Pixelate_Zoom_OpenTip);
        _fps = Num(1, 60, _opt.Fps, 0).Presets(4, 6, 8, 10, 12, 15, 24);
        _fps.ValueChanged += (_, _) => { if (_play.IsEnabled) RestartPlayback(); };
        _playButton = Ui.IconButton(VectorIcon.Pause, UiStrings.Pixelate_Play, (_, _) => TogglePlay());
        var prevButton = Ui.IconButton(VectorIcon.ChevronLeft, UiStrings.Pixelate_PrevFrame, (_, _) => StepFrame(-1));
        var nextButton = Ui.IconButton(VectorIcon.ChevronRight, UiStrings.Pixelate_NextFrame, (_, _) => StepFrame(1));
        _play.Tick += (_, _) => _preview.Step();
        _preview.IndexChanged += i => { _strip.Selected = i; _zoomStrip.Selected = i; };
        _preview.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) TogglePlay();
            else if (e.Key == Key.Left) StepFrame(-1);
            else if (e.Key == Key.Right) StepFrame(1);
            else return;
            e.Handled = true;
        };
        _status = new TextBlock { Foreground = AppTheme.DimTextBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _selectedInfo = new TextBlock { Foreground = AppTheme.TextBrush, Margin = new Thickness(4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        _largeWarning = new TextBlock { Foreground = AppTheme.WarningBrush, Margin = new Thickness(4, 0, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
        _breakdown = new TextBlock { Foreground = AppTheme.DimTextBrush, FontSize = AppTheme.SmallFontSize, Margin = new Thickness(4, 0, 4, 2), TextWrapping = TextWrapping.Wrap };

        // 큰 미리보기 패널(같은 어두운 바탕): 위 = 결과·원본 그림, 아래 = 재생 조절 + 프레임 띠(가로 스크롤).
        var playRow = Ui.Row(prevButton, _playButton, nextButton, Ui.Label(UiStrings.Pixelate_Fps), _fps);
        playRow.Margin = new Thickness(4, 2, 4, 0);
        var stripScroll = StripScroll(_strip);
        _strip.Picked += i => PickFrame(i, _preview);
        _colors = new ColorsBlock(this) { Margin = new Thickness(6, 2, 6, 2) };
        var previewPanel = new DockPanel { Background = AppTheme.BackBrush, LastChildFill = true };
        DockPanel.SetDock(stripScroll, Dock.Bottom);
        DockPanel.SetDock(playRow, Dock.Bottom);
        DockPanel.SetDock(_colors, Dock.Bottom);
        previewPanel.Children.Add(stripScroll);
        previewPanel.Children.Add(playRow);
        previewPanel.Children.Add(_colors);
        previewPanel.Children.Add(_preview);

        var views = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*") };
        Grid.SetColumn(previewPanel, 2);
        views.Children.Add(_sourceView);
        views.Children.Add(previewPanel);

        // 원본 바꾸기(원본이 있을 때): 파일 찾기 — 창에 끌어 놓거나 Ctrl+V로 붙여 넣어도 바뀐다.
        _replaceSource = Ui.Button(VectorIcon.FolderOpen, UiStrings.Pixelate_ReplaceSource, (_, _) => BrowseSource()).Tip(UiStrings.Pixelate_ReplaceSourceTip);
        // 결과 크기(4단계와 같은 설정): 자동이면 회색 숫자로 지금 결과 크기, 끄면 적은 가로×세로에 맞춤.
        _barAuto = Ui.Check(UiStrings.Pixelate_Auto, true).Tip(UiStrings.Pixelate_Bar_AutoTip);
        _barW = Ui.Num(4, 1024, 32, 58).Presets(16, 24, 32, 48, 64, 96, 128);
        _barH = Ui.Num(4, 1024, 32, 58).Presets(16, 24, 32, 48, 64, 96, 128);
        var linkIcon = Ui.Icon(VectorIcon.Link, 14);
        linkIcon.Margin = new Thickness(0, 1);
        _barAspect = new ToggleButton
        {
            Content = linkIcon,
            Margin = new Thickness(2, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        }.Tip(UiStrings.Pixelate_Bar_KeepAspectTip);
        _barAuto.IsCheckedChanged += (_, _) => BarAutoChanged();
        _barW.ValueChanged += (_, _) => BarSizeChanged(width: true);
        _barH.ValueChanged += (_, _) => BarSizeChanged(width: false);
        _barAspect.IsCheckedChanged += (_, _) => { if (!_barSyncing) _szAspect.IsChecked = _barAspect.On(); };
        var barLabel = Ui.Label(UiStrings.Pixelate_Bar_Size);
        barLabel.Margin = new Thickness(10, 3, 3, 3);
        var toolbar = Ui.Row(_replaceSource, _showGrid, _showMask, barLabel, _barAuto, _barW, Ui.Label(UiStrings.Pixelate_Size_By), _barH, _barAspect);
        var statusRow = new DockPanel { Margin = new Thickness(0, 2) };
        DockPanel.SetDock(toolbar, Dock.Left);
        statusRow.Children.Add(toolbar);
        statusRow.Children.Add(_status);

        // 원본이 없을 때 가운데 안내: 끌어 놓기·붙여 넣기·파일 찾기.
        var browse = Ui.Button(VectorIcon.FolderOpen, UiStrings.Pixelate_Browse, (_, _) => BrowseSource());
        browse.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyState = new Border
        {
            Background = AppTheme.BackBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new IconView(VectorIcon.Image, AppTheme.DimTextBrush) { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock
                    {
                        Text = Ui.Shortcut(UiStrings.Pixelate_Empty),
                        Foreground = AppTheme.TextBrush,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        MaxWidth = 420,
                    },
                    browse,
                },
            },
        };

        _tiles = new WrapPanel { Orientation = Orientation.Horizontal };
        var tileScroll = new ScrollViewer
        {
            Content = _tiles,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = AppTheme.InputBrush,
        };

        // ── 자동 최적화(후보 위): 실행·진행만 남기고 프리셋(항목·예산·점수 기준)·시도 기록은 별도 창에서 편집한다.
        _optAuto = Ui.Check(UiStrings.Pixelate_Opt_AutoOnLoad, _opt.AutoOptimizeOnLoad).Tip(UiStrings.Pixelate_Opt_AutoOnLoadTip);
        _optRun = Ui.Button(VectorIcon.Sparkles, UiStrings.Pixelate_Opt_Toggle, (_, _) => OnOptimizeClicked()).Tip(UiStrings.Pixelate_Opt_ToggleTip);
        ToolTip.SetShowOnDisabled(_optRun, true);
        var optSettings = Ui.IconButton(VectorIcon.SlidersHorizontal, UiStrings.Pixelate_Opt_Settings, (_, _) => OpenOptimizeSettings());
        _optRevert = Ui.IconButton(VectorIcon.Undo2, UiStrings.Pixelate_Opt_RevertTip, (_, _) => RevertOptimize());
        _optText = new TextBlock { Foreground = AppTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _optGraph = new OptimizeGraph { Width = 180, Height = 26, Margin = new Thickness(3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_optGraph, UiStrings.Pixelate_Opt_GraphTip);
        _optTable = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(2) };
        var optBar = new DockPanel { LastChildFill = true };
        foreach (var c in new Control[] { _optRun, optSettings, _optGraph, _optRevert })
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(c, Dock.Left);
            optBar.Children.Add(c);
        }
        optBar.Children.Add(_optText);
        var optBlock = optBar;

        var right = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto,Auto,200") };
        right.RowDefinitions[0].MinHeight = 200;
        void Put(Control c, int row) { Grid.SetRow(c, row); right.Children.Add(c); }
        Put(views, 0);
        Put(_emptyState, 0);
        Put(statusRow, 1);
        Put(new StackPanel { Children = { _selectedInfo, _largeWarning } }, 2);
        Put(_breakdown, 3);
        Put(optBlock, 4);
        Put(tileScroll, 5);

        _settingsExpanded = _opt.SettingsExpanded;
        _settingsColumn = new ColumnDefinition(SettingsColumnWidth(_settingsExpanded), GridUnitType.Pixel);
        var body = new Grid();
        body.ColumnDefinitions.Add(_settingsColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        Grid.SetColumn(right, 1);
        body.Children.Add(settingsColumn);
        body.Children.Add(right);
        SetSettingsExpanded(_settingsExpanded, animate: false);
        // 접혀 있어도 처음 한 번은 (잘린 채) 배치해 둔다 — 처음 펼칠 때 칸 수백 개의 템플릿을 한꺼번에 만드느라 애니메이션이 끊기지 않게(열린 뒤 숨김).
        _settingsHost.IsVisible = true;
        var root = (DockPanel)Content!;
        root.Children.Remove(Body);
        root.Children.Add(body);
        // 크게 보기 겹판은 대화상자 전체(확인·취소 단추까지)를 덮는다.
        Content = null;
        Content = new Panel { Children = { root, _zoom } };

        if (input is not null) _sourceView.SetImage(input.Image, null);

        // 원본 넣기: 창 어디에나 그림 파일 끌어 놓기, Ctrl+V(글 입력 칸 밖) 붙여 넣기.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnSourceDragOver);
        AddHandler(DragDrop.DragOverEvent, OnSourceDragOver);
        AddHandler(DragDrop.DropEvent, OnSourceDrop);
        KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != Key.V || e.KeyModifiers != Platform.CommandModifier || FocusManager?.GetFocusedElement() is TextBox) return;
            e.Handled = true;
            PasteSource();
        };
        // 스포이드 중 Esc는 스포이드만 끈다(크게 보기 닫기·대화상자 취소보다 먼저).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!_picking || e.Key != Key.Escape) return;
            e.Handled = true;
            SetPicking(false);
        }, RoutingStrategies.Tunnel);

        // 값이 바뀌면(불러오는 중이 아니면) 잠시 뒤 다시 돌린다.
        foreach (var c in new Control[] { _bgMode, _frMode, _frAlign, _grMode, _grSource, _szMode, _dsMode, _palMethod, _palWhich, _palSource, _palDither, _palDistance, _clOutline, _clFit })
            ((ComboBox)c).SelectionChanged += (_, _) => Changed();
        foreach (var n in new[] { _bgTol, _bgAlpha, _bgSpecks, _frCols, _frRows, _frMerge, _frMergeDist, _grScale, _grOffX, _grOffY, _grJitter, _grAdjust, _grLocalShift, _grLocalBand, _grLocalMinCell,
                     _szW, _szH, _szPad, _szFactor, _dsBorder, _dsAlpha, _dsSnap, _palColors, _palEdge, _palDark, _palHueWeight, _palHueChroma, _palHueMin, _palHueArea,
                     _clContrast, _clVecTol, _clVecBlob, _coContrast, _candCount })
            n.ValueChanged += (_, _) => Changed();
        foreach (var c in new[] { _bgAutoTol, _bgConnected, _bgKeepAlpha, _bgSticker, _bgKeep, _frCommon, _grPerFrame, _grLocal, _szAspect, _palAuto, _palShared, _palSnap, _palDistinct, _palShade,
                     _clOrphans, _clHoles, _clJaggies, _coTemporal, _vScale, _vPalette, _vDownsample, _vCleanup, _vDither, _vFit })
            c.IsCheckedChanged += (_, _) => Changed();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rerun(); };

        // 숫자·설정 입력의 Enter가 결과 적용으로 이어지지 않게 한다. 적용은 단추로만 한다.
        OkButton.IsDefault = false;
        OkButton.Click += (_, _) =>
        {
            if (_selected is null) { DialogResult = DialogResult.None; return; }
            Apply(_selected, newDocument: false);
        };
        _newTab = AddButton(UiStrings.Pixelate_AddToNewTab, DialogResult.OK, beforeOk: true);
        _newTab.Width = double.NaN;
        _newTab.Tip(UiStrings.Pixelate_AddToNewTabTip);
        _newTab.Click += (_, _) =>
        {
            if (_selected is null) { DialogResult = DialogResult.None; return; }
            Apply(_selected, newDocument: true);
        };

        LoadSettings(initial);
        UpdateSourceState();
        _autoOptimizePending = input is not null && _optAuto.On() && !_presetGiven;
        Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => { if (!_settingsAnim.IsEnabled) _settingsHost.IsVisible = _settingsExpanded; }, DispatcherPriority.Background);
            Rerun();
        };
        Closed += (_, _) => OnClosedSave();
    }

    private static string TitleFor(PixelateInput? input) => input is null ? UiStrings.Pixelate_Title : $"{UiStrings.Pixelate_Title} — {input.Name}";

    // ───────────── 원본 넣기·바꾸기 ─────────────

    /// <summary>원본이 있는지에 따라 빈 화면 안내·원본 바꾸기·적용·자동 최적화 단추를 맞춘다.</summary>
    private void UpdateSourceState()
    {
        bool has = _input is not null;
        _emptyState.IsVisible = !has;
        _replaceSource.IsVisible = has;
        OkButton.IsEnabled = has;
        _newTab.IsEnabled = has;
        UpdateOptimizeButtons();
    }

    /// <summary>
    /// 원본을 넣거나 바꾼다(끌어 놓기·붙여 넣기·파일 찾기): 돌던 일을 멈추고 원본에서 나온 모든 것(준비한 원본·후보·미리보기·최적화 결과·뺀 색)을 비운 뒤
    /// 다시 돌린다. 레이어 여러 프레임을 이은 원본이었으면 프레임 나누기를 기억한 설정으로 되돌린다(새 원본은 그림 한 장).
    /// </summary>
    private void SetSource(PixelateInput input)
    {
        _cts?.Cancel();
        _generation++;
        _debounce.Stop();
        StopOptimize();
        if (_restoreFrames is { } frames)
        {
            _restoreFrames = null;
            LoadSettings(ReadSettings() with { Frames = frames });
        }
        _input = input;
        _prepared = null;
        _detected = null;
        _excluded = [];
        _pinned = [];
        _optRevertTo = null;
        _optGraph.Clear();
        _optResults.Clear();
        FillOptimizeHistory();
        SetOptText("");
        CloseZoom();
        ClearResults();
        Title = TitleFor(input);
        _sourceView.SetImage(input.Image, null);
        UpdateSourceState();
        _autoOptimizePending = _optAuto.On();
        Rerun();
    }

    /// <summary>후보·고른 후보의 미리보기·단계 안내 글을 비운다(원본을 바꿀 때).</summary>
    private void ClearResults()
    {
        _candidates.Clear();
        _optimized.Clear();
        _tiles.Children.Clear();
        _selected = null;
        _userPicked = false;
        _selectedInfo.Text = _breakdown.Text = "";
        _largeWarning.IsVisible = false;
        _bgInfo.Text = _frInfo.Text = _grInfo.Text = _szInfo.Text = _palInfo.Text = "";
        SyncBarSize();
        _preview.SetFrames([], [], null, [], true);
        var oldThumbs = _thumbs;
        _thumbs = [];
        _strip.SetThumbs(_thumbs);
        _zoomStrip.SetThumbs(_thumbs);
        foreach (var b in oldThumbs) b.Dispose();
        _zoomStripScroll.IsVisible = false;
        SetPicking(false);
        _colors.Show([], _excluded, _pinned);
        _zoomColors.Show([], _excluded, _pinned);
        _sourceView.SetGrid([]);
        _play.Stop();
        UpdatePlayIcon();
    }

    /// <summary>그림 파일을 원본으로(못 읽으면 알림).</summary>
    private void LoadSourceFile(string path)
    {
        if (LoadImage(path) is not { } image)
        {
            Ui.Error(this, UiStrings.FormatPixelate_Unreadable(name: Path.GetFileName(path)));
            return;
        }
        SetSource(new PixelateInput(image, Path.GetFileNameWithoutExtension(path), path));
    }

    /// <summary>파일 찾기(처음 폴더 = 주 창 파일 브라우저의 폴더).</summary>
    private void BrowseSource()
    {
        if (Ui.OpenFile(this, UiStrings.Pixelate_OpenTitle, _browseFolder(),
                new FileFilter(UiStrings.Pixelate_ImageFilter, [.. ImageExtensions.Select(e => "*" + e)])) is { } path)
            LoadSourceFile(path);
    }

    /// <summary>Ctrl+V: 클립보드의 그림 파일(복사한 파일·글로 된 경로)이 먼저, 없으면 그림(PNG·비트맵 — 주 창 붙여넣기와 같은 길).</summary>
    private void PasteSource()
    {
        if (ClipboardImagePath() is { } path) LoadSourceFile(path);
        else if (_readClipboardImage() is { } image) SetSource(new PixelateInput(image, UiStrings.Pixelate_PastedName, null));
        else _status.Text = UiStrings.Pixelate_Paste_Nothing;
    }

    private string? ClipboardImagePath()
    {
        if (Clipboard is not { } cb) return null;
        try
        {
            if (Ui.Wait(cb.TryGetFilesAsync()) is { } files)
                foreach (var f in files)
                    if (f.TryGetLocalPath() is { } p && IsImageFile(p)) return p;
            if (Ui.Wait(cb.TryGetTextAsync())?.Trim().Trim('"') is { Length: > 0 } text && IsImageFile(text) && File.Exists(text)) return text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 읽을 수 없는 클립보드 내용이면 그림으로 읽어 본다.
        }
        return null;
    }

    /// <summary>끌어 오는 내용의 첫 그림 파일(없으면 null).</summary>
    private static string? DraggedImage(IDataTransfer data)
        => data.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault(IsImageFile);

    private void OnSourceDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DraggedImage(e.DataTransfer) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnSourceDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DraggedImage(e.DataTransfer) is not { } path)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        // 끌어 놓기가 끝난 뒤에 읽는다(오류 창이 끌기 도중에 뜨지 않게).
        Dispatcher.UIThread.Post(() => LoadSourceFile(path));
    }

    // ───────────── 설정 칸 접기 ─────────────

    private static double SettingsColumnWidth(bool expanded) => expanded ? SettingsWidth + SettingsBarWidth : SettingsBarWidth;

    /// <summary>왼쪽 설정 칸을 펼치거나 접는다(<paramref name="animate"/>면 열 너비를 0.2초 동안 부드럽게).</summary>
    private void SetSettingsExpanded(bool expanded, bool animate)
    {
        _settingsExpanded = expanded;
        _settingsChevron.Icon = expanded ? VectorIcon.ChevronLeft : VectorIcon.ChevronRight;
        _settingsToggle.Tip(expanded ? UiStrings.Pixelate_Settings_Hide : UiStrings.Pixelate_Settings_Show);
        _settingsTo = SettingsColumnWidth(expanded);
        if (!animate)
        {
            _settingsAnim.Stop();
            _settingsColumn.Width = new GridLength(_settingsTo);
            _settingsHost.IsVisible = expanded;
            return;
        }
        _settingsFrom = _settingsColumn.Width.Value;
        _settingsHost.IsVisible = true;
        _settingsClock.Restart();
        _settingsAnim.Start();
    }

    private void StepSettingsAnimation()
    {
        double t = Math.Min(1, _settingsClock.Elapsed.TotalMilliseconds / SettingsAnimMs);
        double eased = 1 - Math.Pow(1 - t, 3);
        _settingsColumn.Width = new GridLength(_settingsFrom + (_settingsTo - _settingsFrom) * eased);
        if (t < 1) return;
        _settingsAnim.Stop();
        _settingsHost.IsVisible = _settingsExpanded;
    }

    /// <summary>파일 확장자가 넣을 수 있는 그림(PNG·JPG·BMP·GIF·WebP)인지.</summary>
    public static bool IsImageFile(string path) => ImageExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>그림 파일의 첫 프레임(PNG는 PNG 읽기, 나머지는 SkiaSharp). 못 읽으면 null.</summary>
    public static PixelImage? LoadImage(string path)
    {
        try
        {
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return PngIO.Load(path);
            return ImageUtil.DecodeFrames(path) is { Images.Count: > 0 } f ? f.Images[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>쓸 수 있는 팔레트: 팔레트 열의 팔레트와 Lospec 즐겨찾기(이름 앞에 "Lospec:").</summary>
    private static List<(string Name, IReadOnlyList<Color32> Colors)> BuildPalettes(IEnumerable<NamedPalette> palettes)
    {
        var list = palettes.Where(p => p.Colors.Any(c => c.A != 0)).Select(p => (p.Name, p.Colors)).ToList();
        try
        {
            foreach (var f in LospecFavoriteStore.Load(LospecPaletteDialog.FavoritesRoot).Items)
            {
                var colors = f.Colors.Select(h => Color32.TryParseHex(h, out var c) ? c : (Color32?)null).OfType<Color32>().ToList();
                if (colors.Count > 0) list.Add((UiStrings.FormatPixelate_Pal_Lospec(name: f.Title), colors));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 즐겨찾기를 읽지 못해도 팔레트 열의 팔레트는 쓸 수 있다.
        }
        return list;
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals, decimal increment = 1)
        => Ui.Num(min, max, value, 64, decimals, increment);

    /// <summary>허용 범위(<see cref="PixelateRanges"/>)를 최소·최대로 쓰는 숫자 칸(<paramref name="unit"/> = 화면 배수, 예: 100 → %).</summary>
    private static NumericUpDown Num(PixelateRange range, decimal value, int decimals, decimal increment = 1, double unit = 1)
        => Num((decimal)(range.Min * unit), (decimal)(range.Max * unit), value, decimals, increment);

    /// <summary>불러올 값을 허용 범위 안으로(화면 단위).</summary>
    private static decimal In(PixelateRange range, double value, double unit = 1, int decimals = 3)
        => (decimal)Math.Round(range.Clamp(value) * unit, decimals);

    private static TextBlock Info() => new()
    {
        Foreground = AppTheme.AccentBrush,
        FontSize = AppTheme.SmallFontSize,
        Margin = new Thickness(3, 2),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Expander Section(string title, Control body, bool expanded) => new()
    {
        Header = new TextBlock { Text = title, FontWeight = FontWeight.Bold, Foreground = AppTheme.TextBrush },
        Content = body,
        IsExpanded = expanded,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(0, 1),
    };

    private void UpdateColorButton() => _bgColor.Background = new SolidColorBrush(_bgColorValue.ToColor());

    // ───────────── 설정 ↔ 화면 ─────────────

    private static int Index<T>(T value) where T : struct, Enum => Array.IndexOf(Enum.GetValues<T>(), value);

    private static T At<T>(ComboBox c) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        return values[Math.Clamp(c.SelectedIndex, 0, values.Length - 1)];
    }

    private static int DitherIndex(PaletteDither d) => d switch { PaletteDither.Bayer2 => 1, PaletteDither.Bayer4 => 2, _ => 0 };

    private void LoadSettings(PixelateSettings s)
    {
        _loading = true;
        try
        {
            var bg = s.Background;
            _bgMode.SelectedIndex = Index(bg.Mode);
            _bgColorValue = PixelateColor(bg.Color);
            UpdateColorButton();
            _bgTol.Value = (decimal)Math.Clamp(bg.Tolerance, 0, 441);
            _bgAutoTol.IsChecked = bg.AutoTolerance;
            _bgConnected.IsChecked = bg.ConnectedOnly;
            _bgKeepAlpha.IsChecked = bg.KeepExistingAlpha;
            _bgSticker.IsChecked = bg.RemoveStickerBorder;
            _bgKeep.IsChecked = bg.KeepBackground;
            _bgAlpha.Value = Math.Clamp(bg.AlphaThreshold, 1, 255);
            _bgSpecks.Value = Math.Clamp(bg.RemoveSpecks, 0, 10000);

            var fr = s.Frames;
            _frMode.SelectedIndex = Index(fr.Mode);
            _frCols.Value = Math.Clamp(fr.Columns, 1, 64);
            _frRows.Value = Math.Clamp(fr.Rows, 1, 64);
            _frMerge.Value = (decimal)Math.Clamp(fr.MergeSmallFraction * 100, 0, 100);
            _frMergeDist.Value = Math.Clamp(fr.MergeDistance, 0, 4096);
            _frAlign.SelectedIndex = Index(fr.Align);
            _frCommon.IsChecked = fr.CommonCanvas;

            var gr = s.Grid;
            _grMode.SelectedIndex = Index(gr.Mode);
            _grScale.Value = In(PixelateRanges.Scale, gr.Scale, decimals: 2);
            _grOffX.Value = In(PixelateRanges.Offset, gr.OffsetX, decimals: 1);
            _grOffY.Value = In(PixelateRanges.Offset, gr.OffsetY, decimals: 1);
            _grJitter.Value = (decimal)Math.Clamp(gr.Jitter * 100, 0, 45);
            _grAdjust.Value = In(PixelateRanges.ScaleAdjust, gr.ScaleAdjust, decimals: 2);
            _grPerFrame.IsChecked = gr.PerFrame;
            _grLocal.IsChecked = gr.LocalCorrection;
            _grLocalShift.Value = In(PixelateRanges.LocalShift, gr.LocalShift, 100, 0);
            _grLocalBand.Value = In(PixelateRanges.LocalBand, gr.LocalBand, decimals: 0);
            _grLocalMinCell.Value = In(PixelateRanges.LocalMinCell, gr.LocalMinCell, decimals: 2);
            _grSource.SelectedIndex = Index(gr.Source);

            var sz = s.Size;
            _szMode.SelectedIndex = Index(sz.Mode);
            _szW.Value = Math.Clamp(sz.Width, 4, 1024);
            _szH.Value = Math.Clamp(sz.Height, 4, 1024);
            _szAspect.IsChecked = sz.KeepAspect;
            _szPad.Value = Math.Clamp(sz.Padding, 0, 64);
            _szFactor.Value = In(PixelateRanges.Factor, sz.Factor);

            var ds = s.Downsample;
            _dsMode.SelectedIndex = Index(ds.Mode);
            _dsBorder.Value = In(PixelateRanges.BorderFraction, ds.BorderFraction, 100, 0);
            _dsAlpha.Value = In(PixelateRanges.AlphaThreshold, ds.AlphaThreshold, 100, 0);
            _dsSnap.Value = In(PixelateRanges.SnapStep, ds.SnapStep, decimals: 0);

            var pal = s.Palette;
            _palMethod.SelectedIndex = Index(pal.Method);
            _palAuto.IsChecked = pal.Colors <= 0;
            if (pal.Colors > 0) _palColors.Value = In(PixelateRanges.Colors, pal.Colors, decimals: 0);
            int which = pal.ExistingName is { } name ? _palettes.FindIndex(p => p.Name == name) : -1;
            if (which >= 0) _palWhich.SelectedIndex = which;
            _palShared.IsChecked = pal.Shared;
            _palSource.SelectedIndex = Index(pal.ColorSource);
            _palEdge.Value = In(PixelateRanges.EdgeWeight, pal.EdgeWeight, decimals: 2);
            _palDither.SelectedIndex = DitherIndex(pal.Dither);
            _palSnap.IsChecked = pal.SnapOutline;
            _palDark.Value = In(PixelateRanges.OutlineDarkness, pal.OutlineDarkness, decimals: 2);
            _palDistance.SelectedIndex = Index(pal.ColorDistance);
            _palHueWeight.Value = In(PixelateRanges.HueWeight, pal.HueWeight, decimals: 2);
            _palDistinct.IsChecked = pal.KeepDistinctHues;
            _palShade.IsChecked = pal.PreferShadeFamily;
            _palHueChroma.Value = In(PixelateRanges.DistinctMinChroma, pal.DistinctMinChroma, decimals: 2);
            _palHueMin.Value = In(PixelateRanges.DistinctMinHue, pal.DistinctMinHue, decimals: 0);
            _palHueArea.Value = In(PixelateRanges.DistinctMinArea, pal.DistinctMinArea, decimals: 0);
            _excluded = pal.ExcludedColors;
            _pinned = pal.PinnedColors;

            var cl = s.Cleanup;
            _clOrphans.IsChecked = cl.RemoveOrphans;
            _clContrast.Value = (decimal)Math.Clamp(cl.OrphanMaxContrast, 0, 1);
            _clHoles.IsChecked = cl.FillHoles;
            _clOutline.SelectedIndex = Index(cl.Outline);
            _clJaggies.IsChecked = cl.ReduceJaggies;
            _clFit.SelectedIndex = Index(cl.OutlineFit);
            _clVecTol.Value = In(PixelateRanges.VectorTolerance, cl.VectorTolerance, decimals: 2);
            _clVecBlob.Value = In(PixelateRanges.VectorBlobCells, cl.VectorBlobCells, decimals: 0);

            _coTemporal.IsChecked = s.Consistency.TemporalMajority;
            _coContrast.Value = (decimal)Math.Clamp(s.Consistency.MaxContrast, 0, 1);
        }
        finally
        {
            _loading = false;
        }
        UpdateEnabled();
    }

    private static Color32 PixelateColor(string hex) => Color32.TryParseHex(hex, out var c) ? c.WithAlpha(255) : Color32.Black;

    /// <summary>화면 값으로 만든 설정.</summary>
    public PixelateSettings ReadSettings()
    {
        double D(NumericUpDown n) => (double)n.NumValue();
        int I(NumericUpDown n) => (int)Math.Round(n.NumValue());
        bool usePalette = _palettes.Count > 0 && _palWhich.SelectedIndex >= 0 && _palWhich.SelectedIndex < _palettes.Count;
        var chosen = usePalette ? _palettes[_palWhich.SelectedIndex] : default;
        return new PixelateSettings
        {
            Background = new BackgroundSettings
            {
                Mode = At<BackgroundMode>(_bgMode),
                Color = _bgColorValue.ToHex(),
                Tolerance = D(_bgTol),
                AutoTolerance = _bgAutoTol.On(),
                ConnectedOnly = _bgConnected.On(),
                KeepExistingAlpha = _bgKeepAlpha.On(),
                RemoveStickerBorder = _bgSticker.On(),
                KeepBackground = _bgKeep.On(),
                AlphaThreshold = I(_bgAlpha),
                RemoveSpecks = I(_bgSpecks),
            },
            Frames = new FrameSettings
            {
                Mode = At<FrameSplitMode>(_frMode),
                Columns = I(_frCols),
                Rows = I(_frRows),
                MergeSmallFraction = D(_frMerge) / 100,
                MergeDistance = I(_frMergeDist),
                Align = At<FrameAlign>(_frAlign),
                CommonCanvas = _frCommon.On(),
            },
            Grid = new PixelGridSettings
            {
                Mode = At<GridMode>(_grMode),
                Scale = D(_grScale),
                OffsetX = D(_grOffX),
                OffsetY = D(_grOffY),
                Jitter = D(_grJitter) / 100,
                ScaleAdjust = D(_grAdjust),
                PerFrame = _grPerFrame.On(),
                LocalCorrection = _grLocal.On(),
                LocalShift = D(_grLocalShift) / 100,
                LocalBand = (int)D(_grLocalBand),
                LocalMinCell = D(_grLocalMinCell),
                Source = At<ScaleSource>(_grSource),
            },
            Size = new SizeSettings
            {
                Mode = At<OutputSizeMode>(_szMode),
                Width = I(_szW),
                Height = I(_szH),
                KeepAspect = _szAspect.On(),
                Padding = I(_szPad),
                Factor = D(_szFactor),
            },
            Downsample = new DownsampleSettings
            {
                Mode = At<DownsampleMode>(_dsMode),
                BorderFraction = D(_dsBorder) / 100,
                AlphaThreshold = D(_dsAlpha) / 100,
                SnapStep = I(_dsSnap),
            },
            Palette = new PaletteSettings
            {
                Method = At<PaletteMethod>(_palMethod),
                Colors = _palAuto.On() ? 0 : I(_palColors),
                ExistingColors = usePalette ? [.. chosen.Colors.Where(c => c.A != 0).Select(c => c.WithAlpha(255).ToHex())] : [],
                ExistingName = usePalette ? chosen.Name : null,
                Shared = _palShared.On(),
                Dither = _palDither.SelectedIndex switch { 1 => PaletteDither.Bayer2, 2 => PaletteDither.Bayer4, _ => PaletteDither.None },
                SnapOutline = _palSnap.On(),
                ColorSource = At<PaletteColorSource>(_palSource),
                EdgeWeight = D(_palEdge),
                OutlineDarkness = D(_palDark),
                ColorDistance = At<ColorDistanceMode>(_palDistance),
                HueWeight = D(_palHueWeight),
                KeepDistinctHues = _palDistinct.On(),
                PreferShadeFamily = _palShade.On(),
                DistinctMinChroma = D(_palHueChroma),
                DistinctMinHue = D(_palHueMin),
                DistinctMinArea = D(_palHueArea),
                ExcludedColors = _excluded,
                PinnedColors = _pinned,
            },
            Cleanup = new CleanupSettings
            {
                RemoveOrphans = _clOrphans.On(),
                OrphanMaxContrast = D(_clContrast),
                FillHoles = _clHoles.On(),
                Outline = At<OutlineMode>(_clOutline),
                ReduceJaggies = _clJaggies.On(),
                OutlineFit = At<OutlineFit>(_clFit),
                VectorTolerance = D(_clVecTol),
                VectorBlobCells = D(_clVecBlob),
            },
            Consistency = new ConsistencySettings { TemporalMajority = _coTemporal.On(), MaxContrast = D(_coContrast) },
        };
    }

    private CandidateVary ReadVary()
    {
        var v = CandidateVary.None;
        if (_vScale.On()) v |= CandidateVary.Scale;
        if (_vPalette.On()) v |= CandidateVary.PaletteSize;
        if (_vDownsample.On()) v |= CandidateVary.DownsampleMode;
        if (_vCleanup.On()) v |= CandidateVary.Cleanup;
        if (_vDither.On()) v |= CandidateVary.Dither;
        if (_vFit.On()) v |= CandidateVary.OutlineFit;
        return v;
    }

    /// <summary>보통 후보를 잴 점수 기준: 체크한 첫 최적화 프리셋(<see cref="PixelateOptions.PrimaryPreset"/>)의 항목별 비중·점수용 색 거리.</summary>
    private (ScoreWeights Weights, ColorDistanceMode Distance, double HueWeight) ScoringCriteria()
    {
        var p = _opt.PrimaryPreset;
        return (p.Weights, p.ScoreColorDistance, p.ScoreHueWeight);
    }

    /// <summary>방식에 따라 쓰지 않는 칸을 끈다.</summary>
    private void UpdateEnabled()
    {
        var bg = At<BackgroundMode>(_bgMode);
        _bgColor.IsEnabled = bg == BackgroundMode.PickColor;
        _bgTol.IsEnabled = bg != BackgroundMode.None && !_bgAutoTol.On();
        _bgAutoTol.IsEnabled = _bgConnected.IsEnabled = bg != BackgroundMode.None;
        _bgKeepAlpha.IsEnabled = bg == BackgroundMode.Auto;
        _bgSticker.IsEnabled = _bgKeep.IsEnabled = bg != BackgroundMode.None;
        var fr = At<FrameSplitMode>(_frMode);
        _frCols.IsEnabled = _frRows.IsEnabled = fr == FrameSplitMode.Grid;
        _frMerge.IsEnabled = _frMergeDist.IsEnabled = fr == FrameSplitMode.Auto;
        bool manual = At<GridMode>(_grMode) == GridMode.Manual;
        _grScale.IsEnabled = _grOffX.IsEnabled = _grOffY.IsEnabled = manual;
        _grJitter.IsEnabled = _grAdjust.IsEnabled = !manual;
        _grLocalShift.IsEnabled = _grLocalBand.IsEnabled = _grLocalMinCell.IsEnabled = _grLocal.On();
        // 외곽선 맞추기가 "크기를 줄여서"면 칸 크기 기준은 외곽선 두께로 정해진다.
        _grSource.IsEnabled = !manual && At<OutlineFit>(_clFit) != OutlineFit.Shrink;
        var sz = At<OutputSizeMode>(_szMode);
        _szW.IsEnabled = _szH.IsEnabled = _szAspect.IsEnabled = sz == OutputSizeMode.FixedSize;
        _szFactor.IsEnabled = sz == OutputSizeMode.ScaleFactor;
        var pm = At<PaletteMethod>(_palMethod);
        _palAuto.IsEnabled = pm is PaletteMethod.KMeans or PaletteMethod.MedianCut;
        _palColors.IsEnabled = _palAuto.IsEnabled && !_palAuto.On();
        _palWhich.IsEnabled = pm == PaletteMethod.Existing && _palettes.Count > 0;
        _palShared.IsEnabled = pm is PaletteMethod.KMeans or PaletteMethod.MedianCut;
        _palSource.IsEnabled = _palShared.IsEnabled;
        _palEdge.IsEnabled = _palSource.IsEnabled && At<PaletteColorSource>(_palSource) == PaletteColorSource.Original;
        _palDither.IsEnabled = pm != PaletteMethod.KeepOriginal;
        _palDark.IsEnabled = _palSnap.On();
        _palDistance.IsEnabled = pm != PaletteMethod.KeepOriginal;
        _palHueWeight.IsEnabled = _palDistance.IsEnabled && At<ColorDistanceMode>(_palDistance) == ColorDistanceMode.OkLchHue;
        _palShade.IsEnabled = _palDistance.IsEnabled;
        _palDistinct.IsEnabled = _palShared.IsEnabled;
        _palHueChroma.IsEnabled = _palHueMin.IsEnabled = _palHueArea.IsEnabled = _palDistinct.IsEnabled && _palDistinct.On();
        _clContrast.IsEnabled = _clOrphans.On();
        _clVecTol.IsEnabled = _clVecBlob.IsEnabled = At<OutlineFit>(_clFit) == OutlineFit.Vector;
        _coContrast.IsEnabled = _coTemporal.On();
        _optPresets?.RefreshApplicable();
        SyncBarSize();
    }

    /// <summary>원본 아래 막대의 결과 크기를 4단계 칸(설정)과 고른 결과에 맞춘다(막대 이벤트는 무시).</summary>
    private void SyncBarSize()
    {
        var mode = At<OutputSizeMode>(_szMode);
        bool auto = mode != OutputSizeMode.FixedSize;
        if (auto) _barAutoMode = mode;
        _barSyncing = true;
        try
        {
            _barAuto.IsChecked = auto;
            _barAspect.IsChecked = _szAspect.On();
            _barW.IsEnabled = _barH.IsEnabled = _barAspect.IsEnabled = !auto;
            if (auto)
            {
                // 자동: 고른 결과의 실제 크기(4~1024 밖일 수도 있어 범위를 넓힘), 결과가 없으면 비움.
                var size = _selected?.Result.Size;
                _barW.Minimum = _barH.Minimum = 0;
                _barW.Maximum = _barH.Maximum = int.MaxValue;
                _barW.Value = size is { Width: > 0 } s ? s.Width : null;
                _barH.Value = size is { Height: > 0 } t ? t.Height : null;
            }
            else
            {
                _barW.Minimum = _barH.Minimum = _szW.Minimum;
                _barW.Maximum = _barH.Maximum = _szW.Maximum;
                _barW.Value = _szW.Value;
                _barH.Value = _szH.Value;
            }
        }
        finally
        {
            _barSyncing = false;
        }
    }

    /// <summary>막대의 "자동"을 바꿈: 켜면 마지막 자동 방식(칸 크기대로·배율), 끄면 지금 보이는 결과 크기에서 시작하는 고정 크기.</summary>
    private void BarAutoChanged()
    {
        if (_barSyncing) return;
        if (_barAuto.On())
        {
            _szMode.SelectedIndex = Index(_barAutoMode);
            return;
        }
        if (_selected?.Result.Size is { Width: > 0, Height: > 0 } size)
        {
            _loading = true;
            _szW.Value = Math.Clamp(size.Width, 4, 1024);
            _szH.Value = Math.Clamp(size.Height, 4, 1024);
            _loading = false;
        }
        _szMode.SelectedIndex = Index(OutputSizeMode.FixedSize);
    }

    /// <summary>막대의 가로·세로를 바꿈(고정 크기일 때): 4단계 칸에 적고, 비율 묶기면 다른 쪽을 지금 결과(없으면 원본) 비율대로 맞춘다.</summary>
    private void BarSizeChanged(bool width)
    {
        if (_barSyncing || _barAuto.On()) return;
        var (edited, target, other) = width ? (_barW, _szW, _szH) : (_barH, _szH, _szW);
        if (edited.Value is not { } v) return;
        _loading = true;
        target.Value = Math.Clamp(v, target.Minimum, target.Maximum);
        if (_barAspect.On() && AspectBasis() is { } a)
        {
            double ratio = width ? a.Height / (double)a.Width : a.Width / (double)a.Height;
            other.Value = Math.Clamp(Math.Round(v * (decimal)ratio), other.Minimum, other.Maximum);
        }
        _loading = false;
        Changed();
    }

    /// <summary>비율 묶기의 기준 비율: 고른 결과 크기, 없으면 원본 그림 크기.</summary>
    private (int Width, int Height)? AspectBasis()
    {
        if (_selected?.Result.Size is { Width: > 0, Height: > 0 } s) return (s.Width, s.Height);
        if (_input is { Image: { Width: > 0, Height: > 0 } img }) return (img.Width, img.Height);
        return null;
    }

    private void Changed()
    {
        if (_loading) return;
        UpdateEnabled();
        DeselectPreset();
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>설정이 프리셋과 달라졌으면(프리셋을 고르는 중이 아니면) 프리셋 칸을 비운다.</summary>
    private void DeselectPreset()
    {
        if (_preset.SelectedIndex >= 0 && !_presetApplying) { _presetSilent = true; _preset.SelectedIndex = -1; _presetSilent = false; }
    }

    // ───────────── 후보 만들기 ─────────────

    private void Rerun()
    {
        if (_input is not { } input) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int gen = ++_generation;
        var settings = ReadSettings();
        _runFit = settings.Cleanup.OutlineFit;
        var vary = ReadVary();
        int count = (int)_candCount.NumValue();
        _scoredWith = ScoringCriteria();
        var weights = _scoredWith.Weights;
        var scoreMetric = new ColorMetric(_scoredWith.Distance, _scoredWith.HueWeight);
        var prepared = _prepared;
        StopOptimize();
        _candidates.Clear();
        _optimized.Clear();
        _tiles.Children.Clear();
        _userPicked = false;
        _status.Text = UiStrings.FormatPixelate_Status_Running(done: 0, total: count);
        _runClock.Restart();
        var image = input.Image;
        Task.Run(() =>
        {
            try
            {
                var src = prepared is not null && prepared.Matches(settings) ? prepared : PixelateSource.Prepare(image, settings);
                cts.Token.ThrowIfCancellationRequested();
                var auto = settings.Grid with { Mode = GridMode.Auto, ScaleAdjust = 0, Source = Pixelator.ScaleSourceFor(settings) };
                var detected = src.Split.Frames.Count == 0 ? null : Pixelator.DetectGrid(src, auto, settings.Palette.OutlineDarkness);
                var estimate = src.Split.Frames.Count == 0 ? default : src.EstimatedScale(-1, auto.Jitter, auto.Source, settings.Palette.OutlineDarkness);
                var fit = settings.Cleanup.OutlineFit;
                Dispatcher.UIThread.Post(() => { if (gen == _generation) OnPrepared(src, detected, estimate, fit); });
                if (src.Split.Frames.Count == 0)
                {
                    Dispatcher.UIThread.Post(() => { if (gen == _generation) { _autoOptimizePending = false; _status.Text = UiStrings.Pixelate_Status_NoSprite; } });
                    return;
                }
                PixelateCandidates.Run(src, settings, vary, count, weights,
                    c => Dispatcher.UIThread.Post(() => { if (gen == _generation) AddCandidate(c, count); }), cts.Token, scoreMetric);
                Dispatcher.UIThread.Post(() => { if (gen == _generation) OnFinished(); });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or AggregateException)
            {
                var message = ex is AggregateException agg ? agg.Flatten().InnerExceptions[0].Message : ex.Message;
                Dispatcher.UIThread.Post(() => { if (gen == _generation) { _autoOptimizePending = false; _status.Text = UiStrings.FormatPixelate_Status_Error(message: message); } });
            }
        });
    }

    private void OnPrepared(PixelateSource src, GridFit? detected, ScaleEstimate estimate, OutlineFit fit)
    {
        _prepared = src;
        _detected = detected;
        _sourceView.SetImage(_input!.Image, src.Cleaned);
        _bgInfo.Text = src.Background.UsedAlpha
            ? UiStrings.Pixelate_Bg_InfoAlpha
            : UiStrings.FormatPixelate_Bg_InfoColor(color: src.Background.Color?.ToHex() ?? "", tolerance: src.Background.Tolerance.ToString("0.#", CultureInfo.CurrentCulture));
        _frInfo.Text = UiStrings.FormatPixelate_Frames_Info(count: src.Split.Frames.Count);
        _grInfo.Text = detected is null ? "" : UiStrings.FormatPixelate_Grid_Info(
            scale: detected.DetectedScale.ToString("0.##", CultureInfo.CurrentCulture),
            x: detected.Offset.X.ToString("0.#", CultureInfo.CurrentCulture),
            y: detected.Offset.Y.ToString("0.#", CultureInfo.CurrentCulture),
            confidence: Math.Round(detected.Confidence * 100))
            + "\n" + SourcesInfo(estimate)
            + (fit == OutlineFit.Shrink && estimate.Outline > 0 && !estimate.Exact && _grMode.SelectedIndex == Index(GridMode.Auto)
                ? "\n" + (estimate.ThinOutline ? UiStrings.FormatPixelate_Grid_ShrinkThinOutline(
                    outline: estimate.Outline.ToString("0.##", CultureInfo.CurrentCulture),
                    scale: detected.DetectedScale.ToString("0.##", CultureInfo.CurrentCulture)) : UiStrings.FormatPixelate_Grid_ShrinkInfo(
                    outline: estimate.Outline.ToString("0.##", CultureInfo.CurrentCulture),
                    scale: detected.DetectedScale.ToString("0.##", CultureInfo.CurrentCulture)))
                : "")
            + (fit is OutlineFit.Thin or OutlineFit.Vector ? DoubleOutlineHint(estimate) : "");
    }

    /// <summary>
    /// 세 방식의 칸 크기와 경계 일치, 고른 쪽과 까닭: 예) "색 경계 5.75px(26%) · 외곽선 24.75px(33%) · 덩어리 경계 19px(33%) → 외곽선 두께(경계 일치 33%)",
    /// 자동에서 덩어리 간격이 뚜렷해 바로 골랐으면 "→ 덩어리 경계(덩어리 간격이 뚜렷함 95%)". 깨끗한 그림의 정확한 정수 배면 그것을 고른 쪽으로.
    /// </summary>
    private static string SourcesInfo(ScaleEstimate e)
    {
        var ci = CultureInfo.CurrentCulture;
        string Px(double v) => v > 0 ? v.ToString("0.##", ci) : "-";
        string Pct(double v, double scale) => scale > 0 ? Math.Round(v * 100).ToString(ci) : "-";
        string reason;
        if (e.Exact) reason = UiStrings.FormatPixelate_Grid_ChosenExact(scale: Px(e.Scale));
        else
        {
            string chosen = e.Used switch
            {
                ScaleSource.Outline => UiStrings.Pixelate_Grid_SourceOutline,
                ScaleSource.ColorEdges => UiStrings.Pixelate_Grid_SourceEdges,
                ScaleSource.EdgeLines => UiStrings.Pixelate_Grid_SourceBlocks,
                _ => "-",
            };
            reason = e.ClearBlocks
                ? UiStrings.FormatPixelate_Grid_ReasonClear(chosen: chosen, fit: Math.Round(e.BlocksFit * 100).ToString(ci))
                : UiStrings.FormatPixelate_Grid_ReasonMatch(chosen: chosen, match: e.Used == ScaleSource.Auto ? "-" : Math.Round(e.Match * 100).ToString(ci));
        }
        return UiStrings.FormatPixelate_Grid_Sources(
            edges: Px(e.Edges), edgesMatch: Pct(e.EdgesMatch, e.Edges),
            outline: Px(e.Outline), outlineMatch: Pct(e.OutlineMatch, e.Outline),
            blocks: Px(e.Blocks), blocksMatch: Pct(e.BlocksMatch, e.Blocks),
            reason: reason)
            + (e.MaxScale > 0 ? "\n" + UiStrings.FormatPixelate_Grid_Cap(cap: Px(e.MaxScale), cells: GridDetector.MinSpriteCells) : "");
    }

    /// <summary>실패한 결과의 까닭(<see cref="PixelateFailure"/>) — 대화상자와 AI 그리기 도구가 함께 쓴다.</summary>
    internal static string FailureReason(PixelateFailure f) => f switch
    {
        PixelateFailure.Empty => UiStrings.Pixelate_Failure_Empty,
        PixelateFailure.NearlyEmpty => UiStrings.Pixelate_Failure_NearlyEmpty,
        PixelateFailure.OneColor => UiStrings.Pixelate_Failure_OneColor,
        _ => UiStrings.Pixelate_Failure_TooSmall,
    };

    /// <summary>실패한 결과면 까닭과 칸 크기, 다른 어림으로 다시 정리한 결과면 그 안내, 아니면 null.</summary>
    private static string? FailureNote(PixelateResult r)
    {
        var ci = CultureInfo.CurrentCulture;
        if (r.Failed) return UiStrings.FormatPixelate_Result_Failed(reason: FailureReason(r.Failure), scale: r.DetectedScale.ToString("0.##", ci));
        return r.RetriedFrom > 0
            ? UiStrings.FormatPixelate_Result_Retried(from: r.RetriedFrom.ToString("0.##", ci), scale: r.DetectedScale.ToString("0.##", ci))
            : null;
    }

    /// <summary>외곽선 두께가 쓴 칸 크기의 2배 이상(정수배 근처)이면 "외곽선이 n픽셀 두께" 안내(앞에 줄바꿈), 아니면 빈 글.</summary>
    private static string DoubleOutlineHint(ScaleEstimate e)
    {
        if (e.Outline <= 0 || e.Scale <= 0) return "";
        double ratio = e.Outline / e.Scale;
        int n = (int)Math.Round(ratio);
        return n >= 2 && Math.Abs(ratio - n) <= 0.3 ? "\n" + UiStrings.FormatPixelate_Grid_DoubleOutline(thickness: n) : "";
    }

    private void AddCandidate(PixelateCandidate c, int total)
    {
        _candidates.Add(WithExcluded(c));
        _candidates.Sort((a, b) => a.Result.Failed.CompareTo(b.Result.Failed) is var f && f != 0 ? f
            : b.Score.Total.CompareTo(a.Score.Total) is var r && r != 0 ? r : a.Index.CompareTo(b.Index));
        _status.Text = UiStrings.FormatPixelate_Status_Running(done: _candidates.Count, total: Math.Max(total, _candidates.Count));
        RebuildTiles();
        if (!_userPicked) Select(Preferred());
    }

    private void OnFinished()
    {
        _status.Text = UiStrings.FormatPixelate_Status_Done(count: _candidates.Count, ms: _runClock.ElapsedMilliseconds);
        if (!_userPicked && _candidates.Count > 0) Select(Preferred());
        // 모든 후보가 실패했으면(칸 크기 감지가 잘못됨) 상태 줄에 까닭을.
        if (_candidates.Count > 0 && _candidates.All(c => c.Result.Failed)) _status.Text = FailureNote(_candidates[0].Result);
        // 원본을 넣은 뒤 첫 후보가 끝나면 자동 최적화를 한 번(설정을 바꿔 다시 돌린 것은 아님 — 넣을 때만 켜짐).
        if (_autoOptimizePending && _candidates.Count > 0)
        {
            _autoOptimizePending = false;
            StartOptimize();
        }
    }

    /// <summary>미리 고를 후보: 실패하지 않은 후보 중 지금 설정의 외곽선 맞추기 방식인 것의 점수 1위(없으면 실패하지 않은 1위, 모두 실패면 1위).</summary>
    private PixelateCandidate Preferred()
        => _candidates.FirstOrDefault(c => !c.Result.Failed && c.Settings.Cleanup.OutlineFit == _runFit)
           ?? _candidates.FirstOrDefault(c => !c.Result.Failed) ?? _candidates[0];

    private static string FitName(OutlineFit f) => f switch
    {
        OutlineFit.Shrink => UiStrings.Pixelate_Fit_Shrink,
        OutlineFit.Thin => UiStrings.Pixelate_Fit_Thin,
        OutlineFit.Vector => UiStrings.Pixelate_Fit_Vector,
        _ => UiStrings.Pixelate_Fit_Off,
    };

    private void RebuildTiles()
    {
        _tiles.Children.Clear();
        for (int i = 0; i < _candidates.Count; i++)
        {
            var c = _candidates[i];
            var optimized = _optimized.GetValueOrDefault(c);
            var tile = new CandidateTile(c, i + 1, ReferenceEquals(c, _selected), optimized?.Name, optimized is null ? null : OptimizeMarker(optimized.Reason, c.Score.Total));
            int rank = i + 1;
            PopupMenus.Attach(tile, () => CandidateMenu(c, rank, optimized));
            tile.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
                _userPicked = true;
                Select(c);
            };
            tile.DoubleTapped += (_, _) =>
            {
                Select(c);
                Apply(c, newDocument: false);
                Close(DialogResult.OK);
            };
            _tiles.Children.Add(tile);
        }
    }

    /// <summary>
    /// 후보 칸 우클릭 메뉴: 결과 첫 프레임 복사(1×·×4)·PNG로 저장, 최적화 후보면 그 프리셋만 같은 시작 설정에서 다시 돌리기
    /// (도는 중이거나 프리셋을 지웠으면 끔).
    /// </summary>
    private IEnumerable<object> CandidateMenu(PixelateCandidate c, int rank, OptimizedTile? optimized)
    {
        var image = c.Result.Frames[0];
        var name = ProjectExport.CleanFileName($"{SaveBaseName()}_{optimized?.Name ?? rank.ToString(CultureInfo.InvariantCulture)}");
        var items = new List<object>
        {
            MenuCommand(UiStrings.Preview_Menu_CopyImage, VectorIcon.Copy, () => CopyImage(image)),
            MenuCommand(UiStrings.Preview_Menu_CopyEnlarged, VectorIcon.Maximize2, () => CopyImage(ImageTransform.ScaleNearest(image, image.Width * 4, image.Height * 4))),
            MenuCommand(UiStrings.Pixelate_Menu_SavePng, VectorIcon.ImageDown, () => SaveImage(image, name)),
        };
        if (optimized is not null)
        {
            items.Add(new Separator());
            items.Add(MenuCommand(UiStrings.Pixelate_Opt_RerunPreset, VectorIcon.Sparkles, () => StartOptimize(optimized.Preset),
                _optCts is null && _input is not null && _opt.OptimizePresets.Contains(optimized.Preset)));
        }
        return items;
    }

    /// <summary>원본 우클릭 메뉴: 보이는 모습(배경 지운 모습을 켰으면 배경을 지운 원본) 복사·PNG로 저장. 원본이 없거나 스포이드 중이면 없음.</summary>
    private IEnumerable<object>? SourceMenu()
    {
        if (_input is not { } input || _picking) return null;
        bool cleaned = _showMask.On() && _prepared?.Cleaned is not null;
        var image = cleaned ? _prepared!.Cleaned! : input.Image;
        var name = ProjectExport.CleanFileName(cleaned ? $"{SaveBaseName()}_{UiStrings.Pixelate_Save_CleanedSuffix}" : SaveBaseName());
        return
        [
            MenuCommand(UiStrings.Preview_Menu_CopyImage, VectorIcon.Copy, () => CopyImage(image)),
            MenuCommand(UiStrings.Pixelate_Menu_SavePng, VectorIcon.ImageDown, () => SaveImage(image, name)),
        ];
    }

    /// <summary>메뉴 항목: 누르면 메뉴가 닫힌 뒤에 한다(파일 대화상자가 메뉴 위에 뜨지 않게).</summary>
    private static MenuItem MenuCommand(string text, VectorIcon icon, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = text, Icon = Ui.Icon(icon, 14), IsEnabled = enabled };
        item.Click += (_, _) => Dispatcher.UIThread.Post(action);
        return item;
    }

    /// <summary>저장할 파일 이름의 앞부분: 원본 파일 이름(없으면 원본 이름).</summary>
    private string SaveBaseName()
        => _input?.Path is { } path ? Path.GetFileNameWithoutExtension(path) : _input?.Name ?? UiStrings.Pixelate_PastedName;

    private void CopyImage(PixelImage image)
    {
        _copyImage(image.Clone());
        _status.Text = UiStrings.FormatPixelate_Copied(width: image.Width, height: image.Height);
    }

    /// <summary>그림을 PNG로 저장(처음 폴더 = 원본 파일 폴더, 없으면 파일 브라우저 폴더). 실패하면 알린다.</summary>
    private void SaveImage(PixelImage image, string name)
    {
        var folder = _input?.Path is { } p ? Path.GetDirectoryName(p) : _browseFolder();
        var path = Ui.SaveFile(this, UiStrings.Pixelate_Save_Title, folder, name + ".png", ".png", new FileFilter(UiStrings.Preview_PngFilter, "*.png"));
        if (path is null) return;
        try
        {
            PngIO.Save(image, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Ui.Error(this, UiStrings.FormatPixelate_SaveFailed(message: ex.Message), UiStrings.Pixelate_Save_Title);
            return;
        }
        _status.Text = UiStrings.FormatPixelate_Saved(name: Path.GetFileName(path), width: image.Width, height: image.Height);
    }

    /// <summary>최적화 후보 칸의 표시: 멈춘 까닭(나아지지 않아 끝남 = 최적화, 시간·시도 수를 다 씀, 취소)과 괄호 안 점수.</summary>
    private static string OptimizeMarker(OptimizeStop reason, double score)
    {
        var s = score.ToString("0.0", CultureInfo.CurrentCulture);
        return reason switch
        {
            OptimizeStop.Time => UiStrings.FormatPixelate_Opt_Marker_Time(score: s),
            OptimizeStop.Trials => UiStrings.FormatPixelate_Opt_Marker_Trials(score: s),
            OptimizeStop.Cancelled => UiStrings.FormatPixelate_Opt_Marker_Cancelled(score: s),
            _ => UiStrings.FormatPixelate_Opt_Marker_Done(score: s),
        };
    }

    /// <summary>결과의 긴 쪽이 이보다 크면 "결과가 큽니다" 경고(보통 픽셀 그림 스프라이트는 이보다 작다).</summary>
    private const int LargeResultSide = 256;

    private void Select(PixelateCandidate c)
    {
        _selected = c;
        foreach (var t in _tiles.Children.OfType<CandidateTile>()) t.IsSelected = ReferenceEquals(t.Candidate, c);
        var r = c.Result;
        int rank = _candidates.IndexOf(c) + 1;
        _selectedInfo.Text = UiStrings.FormatPixelate_Selected(
            rank: rank,
            score: c.Score.Total.ToString("0.0", CultureInfo.CurrentCulture),
            scale: r.Scale.ToString("0.##", CultureInfo.CurrentCulture),
            colors: r.Palette.Count,
            width: r.Size.Width,
            height: r.Size.Height,
            frames: r.Frames.Count);
        int locallyAdjusted = r.Maps.Count(m => m.Local is not null);
        string localState = !c.Settings.Grid.LocalCorrection ? UiStrings.Pixelate_Grid_LocalOff
            : locallyAdjusted > 0 ? UiStrings.FormatPixelate_Grid_LocalApplied(applied: locallyAdjusted, total: r.Maps.Count)
            : r.Maps.All(m => (m.XCuts[^1] - m.XCuts[0] + m.YCuts[^1] - m.YCuts[0]) /
                (double)(m.XCuts.Length + m.YCuts.Length - 2) < PixelateRanges.LocalMinCell.Clamp(c.Settings.Grid.LocalMinCell))
                ? UiStrings.FormatPixelate_Grid_LocalTooSmall(min: PixelateRanges.LocalMinCell.Clamp(c.Settings.Grid.LocalMinCell).ToString("0.##", CultureInfo.CurrentCulture))
            : UiStrings.Pixelate_Grid_LocalUnchanged;
        _selectedInfo.Text += " · " + localState;
        _breakdown.Text = string.Join("   ", c.Score.Parts.Select(p => UiStrings.FormatPixelate_Breakdown(
            name: TermName(p.Term),
            value: FormatValue(p),
            bad: Math.Round(p.Badness * 100),
            weight: p.Weight.ToString("0.#", CultureInfo.CurrentCulture))));
        _szInfo.Text = UiStrings.FormatPixelate_Size_Info(width: r.Size.Width, height: r.Size.Height);
        SyncBarSize();
        // 실패한 결과(칸 크기 감지가 잘못됨)·다시 정리한 결과면 그 안내, 아니면 결과의 긴 쪽이 클 때 칸 크기가 진짜 칸의 몇 분의 1일 수 있다(아직 확대된 그림).
        var note = FailureNote(r);
        _largeWarning.IsVisible = note is not null || Math.Max(r.Size.Width, r.Size.Height) > LargeResultSide;
        _largeWarning.Text = note ?? (_largeWarning.IsVisible ? UiStrings.FormatPixelate_Result_Large(width: r.Size.Width, height: r.Size.Height) : "");
        _palInfo.Text = UiStrings.FormatPixelate_Pal_Info(count: r.Palette.Count);
        var frames = r.CommonFrames();
        _preview.SetFrames(frames, r.CommonExcludedFrames(), _prepared?.Cleaned, r.Maps, r.Frames.All(f => f.Size == r.Size));
        // 썸네일은 한 번 만들어 두 띠가 함께 쓴다(옛 썸네일은 두 띠가 새것으로 바꾼 뒤 정리).
        var oldThumbs = _thumbs;
        _thumbs = frames.Select(f => ImageUtil.MakeThumbnail(f, FrameStrip.Thumb)).ToList();
        _strip.SetThumbs(_thumbs);
        _zoomStrip.SetThumbs(_thumbs);
        foreach (var b in oldThumbs) b.Dispose();
        _zoomStripScroll.IsVisible = _thumbs.Count > 1;
        _strip.Selected = _zoomStrip.Selected = _preview.Index;
        ShowColors(r);
        _sourceView.SetGrid(r.Maps);
        RestartPlayback();
    }

    private static string TermName(ScoreTerm t) => t switch
    {
        ScoreTerm.Fidelity => UiStrings.Pixelate_Score_Fidelity,
        ScoreTerm.Orphans => UiStrings.Pixelate_Score_Orphans,
        ScoreTerm.OutlineGaps => UiStrings.Pixelate_Score_OutlineGaps,
        ScoreTerm.Colors => UiStrings.Pixelate_Score_Colors,
        ScoreTerm.Consistency => UiStrings.Pixelate_Score_Consistency,
        ScoreTerm.OutlineThin => UiStrings.Pixelate_Score_OutlineThin,
        ScoreTerm.ColorShift => UiStrings.Pixelate_Score_ColorShift,
        ScoreTerm.BlockRepetition => UiStrings.Pixelate_Score_BlockRepetition,
        _ => UiStrings.Pixelate_Score_Grid,
    };

    private static string FormatValue(ScorePart p) => p.Term switch
    {
        ScoreTerm.Orphans or ScoreTerm.Colors => p.Value.ToString("0", CultureInfo.CurrentCulture),
        ScoreTerm.Fidelity or ScoreTerm.ColorShift => p.Value.ToString("0.000", CultureInfo.CurrentCulture),
        _ => (p.Value * 100).ToString("0", CultureInfo.CurrentCulture) + "%",
    };

    private void RestartPlayback()
    {
        _play.Stop();
        _play.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, (double)_fps.NumValue()));
        if (_preview.FrameCount > 1 && !_paused) _play.Start();
        UpdatePlayIcon();
    }

    private void StopPlayback()
    {
        _paused = true;
        _play.Stop();
        UpdatePlayIcon();
    }

    private void TogglePlay()
    {
        if (_play.IsEnabled) StopPlayback();
        else
        {
            _paused = false;
            RestartPlayback();
        }
    }

    /// <summary>이전·다음 프레임(재생은 멈춤).</summary>
    private void StepFrame(int delta)
    {
        StopPlayback();
        _preview.Index += delta;
    }

    private void UpdatePlayIcon()
    {
        var icon = _play.IsEnabled ? VectorIcon.Pause : VectorIcon.Play;
        if (_playButton.Content is IconView a) a.Icon = icon;
        if (_zoom.PlayButton.Content is IconView b) b.Icon = icon;
    }

    /// <summary>
    /// 크게 보기를 연다(큰 미리보기의 그림을 누름). 열린 동안은 Esc가 대화상자 취소로 가지 않게 취소 키를 끈다
    /// (Esc·바탕 누르기·닫기 단추 = 크게 보기만 닫기, Space·←→ = 재생·프레임 — 큰 미리보기와 같음).
    /// </summary>
    private void OpenZoom()
    {
        if (_zoom.IsVisible || _preview.FrameCount == 0) return;
        CancelButtonControl.IsCancel = false;
        OkButton.IsDefault = false;
        _zoom.Open();
    }

    /// <summary>크게 보기를 닫고 키보드를 큰 미리보기로 돌려준다.</summary>
    private void CloseZoom()
    {
        if (!_zoom.IsVisible) return;
        _zoom.IsVisible = false;
        CancelButtonControl.IsCancel = true;
        OkButton.IsDefault = false;
        _preview.Focus();
    }

    /// <summary>프레임 띠를 가로 스크롤에 넣는다.</summary>
    private static ScrollViewer StripScroll(FrameStrip strip) => new()
    {
        Content = strip,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Margin = new Thickness(0, 0, 0, 2),
    };

    /// <summary>프레임 띠에서 누른 프레임으로 가고 재생을 멈춘 뒤 <paramref name="focus"/>가 키보드 초점(Space·←→)을 받는다.</summary>
    private void PickFrame(int index, InputElement focus)
    {
        StopPlayback();
        _preview.Index = index;
        focus.Focus();
    }

    /// <summary>
    /// 고른 후보가 쓴 색(많이 쓴 색부터)과 고정 색·뺀 색을 두 색 칸 묶음(큰 미리보기·크게 보기)에 함께 보인다 — 어느 쪽에서 바꿔도 둘 다 바뀐다.
    /// </summary>
    private void ShowColors(PixelateResult r)
    {
        var counts = new Dictionary<uint, int>();
        foreach (var f in r.Frames)
            foreach (var c in f.Pixels)
                if (c.A != 0) counts[c.Argb] = counts.GetValueOrDefault(c.Argb) + 1;
        var used = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => (new Color32(kv.Key), kv.Value)).ToList();
        _colors.Show(used, _excluded, _pinned);
        _zoomColors.Show(used, _excluded, _pinned);
    }

    /// <summary>
    /// 색 빼기 켜고 끄기(뺀 색이면 되살리기) — 다시 돌리지 않고 지금 후보들의 결과에서만 뺀다(<see cref="ApplyExclusions"/>).
    /// 남는 색이 없게 되면(마지막 한 색) 빼지 않고 상태 줄에 알린다.
    /// </summary>
    private void ToggleExcluded(Color32 color)
    {
        bool excluded = PaletteEdits.MatchesAny(_excluded, color);
        if (!excluded && _selected is { } sel
            && !sel.Result.Palette.Any(c => !PaletteEdits.Matches(c, color) && !PaletteEdits.MatchesAny(_excluded, c)))
        {
            _status.Text = UiStrings.Pixelate_Exclude_LastColor;
            return;
        }
        _excluded = PaletteEdits.Toggle(_excluded, color);
        ApplyExclusions();
    }

    private void RestoreAllColors()
    {
        if (_excluded.Count == 0) return;
        _excluded = [];
        ApplyExclusions();
    }

    /// <summary>
    /// 뺀 색이 바뀜: 후보마다 색 빼기 전 결과에서 다시 뺀다(<see cref="Pixelator.WithExclusions"/> — 다시 돌리지 않으므로 순위·점수·고른 후보 그대로,
    /// 결과 색 수는 뺀 만큼만 준다). 돌고 있는 후보·최적화 결과는 들어올 때 같은 뒤처리를 받는다(<see cref="WithExcluded"/>).
    /// </summary>
    private void ApplyExclusions()
    {
        DeselectPreset();
        for (int i = 0; i < _candidates.Count; i++)
        {
            var c = _candidates[i];
            var n = WithExcluded(c);
            if (ReferenceEquals(n, c)) continue;
            _candidates[i] = n;
            if (_optimized.Remove(c, out var preset)) _optimized[n] = preset;
            if (ReferenceEquals(_selected, c)) _selected = n;
        }
        RebuildTiles();
        if (_selected is { } s) Select(s);
    }

    /// <summary>후보에 지금 뺀 색을 적용한 후보(이미 같으면 그대로).</summary>
    private PixelateCandidate WithExcluded(PixelateCandidate c)
        => c.Settings.Palette.ExcludedColors.SequenceEqual(_excluded) ? c : c with
        {
            Settings = c.Settings with { Palette = c.Settings.Palette with { ExcludedColors = _excluded } },
            Result = Pixelator.WithExclusions(c.Result, _excluded),
        };

    /// <summary>고정 색 풀기(다시 돌림).</summary>
    private void Unpin(Color32 color)
    {
        _pinned = PaletteEdits.Toggle(_pinned, color);
        if (_selected is { } s) ShowColors(s.Result);
        Changed();
    }

    private void ClearPins()
    {
        if (_pinned.Count == 0) return;
        _pinned = [];
        if (_selected is { } s) ShowColors(s.Result);
        Changed();
    }

    /// <summary>
    /// 원본에서 색 추가(스포이드) 켜고 끄기: 켜면 원본 보기들이 스포이드 커서가 되고 왼쪽 클릭이 끌기 대신 칸 고르기(<see cref="PickCell"/>),
    /// 보이는 원본 그림 둘레가 강조색으로 천천히(<see cref="PickPulsePeriod"/>초 주기) 밝아졌다 어두워진다("여기서 고르기"). 끄면 바로 사라진다.
    /// </summary>
    private void SetPicking(bool on)
    {
        if (on && _selected is null) on = false;
        if (_picking == on) return;
        _picking = on;
        _sourceView.Picking = _preview.Picking = _zoom.View.Picking = on;
        _colors.Picking = _zoomColors.Picking = on;
        if (on)
        {
            _status.Text = UiStrings.Pixelate_Pin_PickHint;
            _pickClock.Restart();
            OnPickPulse(null, EventArgs.Empty);
            _pickPulse.Start();
        }
        else
        {
            _pickPulse.Stop();
            _sourceView.PickPulse = _preview.PickPulse = _zoom.View.PickPulse = null;
        }
    }

    /// <summary>스포이드 중 원본 둘레 강조의 밝기 주기(초).</summary>
    private const double PickPulsePeriod = 1.2;
    private readonly DispatcherTimer _pickPulse = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _pickClock = new();

    /// <summary>스포이드 중 원본 둘레 밝기: 0~1을 부드럽게(코사인) 오르내림. 대화상자가 숨으면 스포이드를 끈다.</summary>
    private void OnPickPulse(object? sender, EventArgs e)
    {
        if (!IsVisible) { SetPicking(false); return; }
        double level = 0.5 - 0.5 * Math.Cos(2 * Math.PI * _pickClock.Elapsed.TotalSeconds / PickPulsePeriod);
        _sourceView.PickPulse = _preview.PickPulse = level;
        _zoom.View.PickPulse = _zoom.IsVisible ? level : null;
    }

    /// <summary>
    /// 결과 픽셀을 좌클릭(크게 보기): 쓰인 곳 보기 — 그 색의 색 칸을 두 색 칸 묶음에서 강조(굵은 테두리·맥박·보이게 굴림), 누른 픽셀 표시와
    /// 같은 색 영역 둘레(색 칸 좌클릭과 같음)를 1.2초, 상태 줄에 HEX와 결과 좌표. 투명 픽셀이면 아무것도 하지 않는다.
    /// </summary>
    private void OnResultPixel(int frame, int x, int y)
    {
        if (ShowResultColor(frame, x, y) is { } c) _status.Text = $"{c.WithAlpha(255).ToHex()} · ({x}, {y})";
    }

    /// <summary>
    /// 결과 픽셀을 우클릭(큰 미리보기·크게 보기): 지금 그 자리에 보이는 색(뺀 색 대신 들어간 색이면 그 색)을 색 칸 우클릭처럼 <see cref="ToggleColor"/>.
    /// 투명 픽셀이거나 보이는 프레임이 아니면 아무것도 하지 않는다.
    /// </summary>
    private void OnResultPixelRight(int frame, int x, int y)
    {
        if (_preview.Index != frame || _preview.FrameImage is not { } img || !img.Contains(x, y)) return;
        var c = img[x, y];
        if (c.A == 0) return;
        _status.Text = $"{c.WithAlpha(255).ToHex()} · ({x}, {y})";
        ToggleColor(c);
    }

    /// <summary>색 칸 좌클릭: 쓰인 곳 보기 — 결과에서 그 색 픽셀(뺀 색이면 그 색이 있던 자리)을 잠깐 강조하고 두 색 칸 묶음에서 그 칸을 강조.</summary>
    private void ShowColorUse(Color32 color)
    {
        _preview.Flash(color);
        _colors.Highlight(color);
        _zoomColors.Highlight(color);
    }

    /// <summary>색 칸·결과 픽셀 우클릭: 고정 색이면 고정 해제, 아니면 이 색 빼기/되살리기(<see cref="ToggleExcluded"/> — 마지막 남은 색은 빼지 않음).</summary>
    private void ToggleColor(Color32 color)
    {
        if (PaletteEdits.MatchesAny(_pinned, color)) Unpin(color);
        else ToggleExcluded(color);
    }

    /// <summary>
    /// 크게 보기의 원본 칸을 누름(스포이드가 아닐 때): 그 칸이 된 결과 픽셀을 <see cref="OnResultPixel"/>처럼 강조하고, 상태 줄에
    /// "원본 칸 #대표색 → 결과 #결과색"(대표색 = 그 칸의 팔레트 전 색, <see cref="PixelateResult.SourceColors"/>). 결과가 투명이면 아무것도 하지 않는다.
    /// </summary>
    private void OnSourceCell(CellHover h)
    {
        if (_selected is not { } sel || h.Frame >= sel.Result.Maps.Count || h.Frame >= sel.Result.SourceColors.Count) return;
        var map = sel.Result.Maps[h.Frame];
        int x = h.Column + map.OffsetX, y = h.Row + map.OffsetY;
        if (ShowResultColor(h.Frame, x, y) is not { } c) return;
        var rep = sel.Result.SourceColors[h.Frame].Get(x, y);
        _status.Text = UiStrings.FormatPixelate_SourceCellResult(source: rep.A == 0 ? "—" : rep.WithAlpha(255).ToHex(), result: c.WithAlpha(255).ToHex());
    }

    /// <summary>보이는 결과 프레임의 픽셀 색을 강조(색 칸·같은 색 영역·누른 픽셀 표시)하고 그 색을 돌려준다(프레임이 다르거나 밖·투명이면 null).</summary>
    private Color32? ShowResultColor(int frame, int x, int y)
    {
        if (_preview.Index != frame || _preview.FrameImage is not { } img || !img.Contains(x, y)) return null;
        var c = img[x, y];
        if (c.A == 0) return null;
        _preview.Flash(c);
        _preview.Mark(frame, x, y);
        _colors.Highlight(c);
        _zoomColors.Highlight(c);
        return c;
    }

    /// <summary>
    /// 스포이드로 원본 칸을 누름: 고른 후보의 칸 경계·칸 색 고르기 방식으로 그 칸의 대표 색(<see cref="Pixelator.CellColor"/>)을 고정 색에 더하고 다시 돌린다.
    /// 한 번 넣으면 스포이드를 끈다(<paramref name="keep"/> = Shift+클릭이면 그대로). 그 색과 맞는 뺀 색은 지운다(고정 색은 빠지지 않음).
    /// </summary>
    private void PickCell(CellHover h, bool keep)
    {
        if (_selected is not { } sel || _prepared is not { } src || !src.Matches(sel.Result.Settings) || h.Frame >= sel.Result.Maps.Count) return;
        var color = Pixelator.CellColor(src, h.Frame, sel.Result.Maps[h.Frame].Layout(), h.Column, h.Row, sel.Result.Settings.Downsample);
        if (color.A == 0)
        {
            _status.Text = UiStrings.Pixelate_Pin_EmptyCell;
            return;
        }
        if (!keep) SetPicking(false);
        if (PaletteEdits.MatchesAny(_pinned, color))
        {
            _status.Text = UiStrings.Pixelate_Pin_Already;
            return;
        }
        _pinned = [.. _pinned, color.WithAlpha(255).ToHex()];
        _excluded = [.. _excluded.Where(hex => !(Color32.TryParseHex(hex, out var e) && PaletteEdits.Matches(e, color)))];
        ShowColors(sel.Result);
        Changed();
    }

    private void UseDetectedGrid()
    {
        if (_detected is not { } g) return;
        _loading = true;
        _grScale.Value = (decimal)Math.Round(g.Scale, 2);
        _grOffX.Value = (decimal)Math.Round(g.Offset.X, 1);
        _grOffY.Value = (decimal)Math.Round(g.Offset.Y, 1);
        _grMode.SelectedIndex = Index(GridMode.Manual);
        _loading = false;
        Changed();
    }

    // ───────────── 자동 최적화 ─────────────

    /// <summary>끝난(취소면 그때까지 돈) 프리셋 하나의 최적화: 프리셋, 돌릴 때의 이름, 바꿔 본 항목, 결과.</summary>
    private sealed record OptimizeRun(OptimizePreset Preset, string Name, OptimizeParam Params, PixelateOptimizeResult Result);

    /// <summary>최적화 후보 칸이 가리키는 프리셋, 돌릴 때의 이름, 멈춘 까닭.</summary>
    private sealed record OptimizedTile(OptimizePreset Preset, string Name, OptimizeStop Reason);

    /// <summary>
    /// 최적화 설정 창: 왼쪽은 프리셋 목록(체크 = 함께 돌림), 오른쪽은 고른 프리셋의 항목·예산·점수 기준(<see cref="OptimizePresetsPanel"/> — 지금 설정에서
    /// 쓰지 않는 항목은 흐림), 아래는 고른 프리셋의 마지막 시도 기록과 '불러오면 자동 최적화'. 도는 동안은 편집할 수 없다. 닫으면 설정 파일에 저장한다.
    /// </summary>
    private void OpenOptimizeSettings()
    {
        if (_optSettingsWindow is { } open)
        {
            open.Activate();
            return;
        }
        var popup = new Window
        {
            Title = UiStrings.Pixelate_Opt_SettingsTitle,
            Width = 1000,
            Height = 680,
            MinWidth = 700,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = AppTheme.BackBrush,
        };
        var panel = new OptimizePresetsPanel(_opt, ReadSettings);
        panel.Changed += (_, _) => OnOptimizePresetsChanged();
        panel.SelectionChanged += (_, _) => FillOptimizeHistory();
        var historyScroll = new ScrollViewer { Content = _optTable, MaxHeight = 170, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var history = new Expander
        {
            Header = new TextBlock { Text = UiStrings.Pixelate_Opt_History, Foreground = AppTheme.TextBrush },
            Content = historyScroll,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(8, 0),
        };
        var close = Ui.Button(UiStrings.Pixelate_Opt_SettingsClose, (_, _) => popup.Close());
        var bottom = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 4, 8, 8) };
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        _optAuto.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_optAuto);
        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(history, Dock.Bottom);
        content.Children.Add(bottom);
        content.Children.Add(history);
        content.Children.Add(panel);
        popup.Content = content;
        popup.Closed += (_, _) =>
        {
            popup.Content = null;
            bottom.Children.Remove(_optAuto);
            historyScroll.Content = null;
            _optPresets = null;
            _optSettingsWindow = null;
            _settings.Save();
        };
        _optPresets = panel;
        _optSettingsWindow = popup;
        UpdateOptimizeButtons();
        FillOptimizeHistory();
        popup.Show(this);
    }

    /// <summary>설정 창에서 프리셋을 바꾸면: 자동 최적화 단추를 맞추고, 보통 후보를 잰 점수 기준(체크한 첫 프리셋)이 바뀌었으면 잠시 뒤 다시 돌린다.</summary>
    private void OnOptimizePresetsChanged()
    {
        UpdateOptimizeButtons();
        if (_input is null || ScoringCriteria() == _scoredWith) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// 자동 최적화 단추: 도는 동안 "취소", 아니면 "자동 최적화"(원본이 없거나 체크한 프리셋이 없으면 꺼짐 — 풍선에 까닭). 그래프·결과 한 줄은 한 번 돌린 뒤부터,
    /// 되돌리기는 끝난 뒤에만. 도는 동안 설정 창의 프리셋 편집은 끈다.
    /// </summary>
    private void UpdateOptimizeButtons()
    {
        bool running = _optCts is not null;
        bool any = _opt.OptimizePresets.Any(p => p.Enabled);
        _optRun.Content = Ui.IconLabel(running ? VectorIcon.X : VectorIcon.Sparkles, running ? UiStrings.Pixelate_Opt_Cancel : UiStrings.Pixelate_Opt_Toggle);
        _optRun.IsEnabled = _input is not null && (running || any);
        _optRun.Tip(running || any ? UiStrings.Pixelate_Opt_ToggleTip : UiStrings.Pixelate_Opt_NoPresetTip);
        _optGraph.IsVisible = running || _optGraph.HasData;
        _optRevert.IsVisible = !running && _optRevertTo is not null;
        if (_optPresets is not null) _optPresets.IsEnabled = !running;
        _optAuto.IsEnabled = !running;
    }

    private void OnOptimizeClicked()
    {
        if (_optCts is null)
        {
            StartOptimize();
            return;
        }
        // 취소해도 그때까지 돈 프리셋의 결과를 보인다(끝나면 단추가 다시 켜짐).
        _optCts.Cancel();
        _optRun.IsEnabled = false;
    }

    /// <summary>그래프 옆 한 줄과 그 풍선(<paramref name="tip"/>이 없으면 같은 글).</summary>
    private void SetOptText(string text, string? tip = null)
    {
        _optText.Text = text;
        ToolTip.SetTip(_optText, string.IsNullOrEmpty(text) ? null : tip ?? text);
    }

    /// <summary>
    /// 지금 설정(왼쪽)에서 체크한 최적화 프리셋을 목록 순서대로 배경에서 하나씩 돌린다(모두 같은 준비된 원본·시작 설정, 프리셋마다 자기 항목·예산·점수 기준).
    /// 돌던 후보 만들기는 멈추고(결과가 섞이지 않게), 프리셋 하나가 끝날 때마다 후보와 미리보기를 먼저 보인 뒤 다음 프리셋을 시작한다.
    /// 취소하면 남은 프리셋은 건너뛰고 그때까지 돈 프리셋(도는 중이던 프리셋은 그때까지 찾은 1위)을 보인다.
    /// <paramref name="only"/>면(최적화 후보 칸 우클릭) 그 프리셋 하나만 지난 최적화와 같은 시작 설정에서 다시 돌려 그 칸만 바꾼다.
    /// </summary>
    private void StartOptimize(OptimizePreset? only = null)
    {
        if (_optCts is not null || _input is not { } input) return;
        var runs = (only is null ? _opt.OptimizePresets.Where(p => p.Enabled) : _opt.OptimizePresets.Where(p => ReferenceEquals(p, only)))
            .Select(p => (Preset: p, p.Name, Options: p.Options())).ToList();
        if (runs.Count == 0) return;
        _debounce.Stop();
        _cts?.Cancel();
        _generation++;
        var settings = only is not null && _optRevertTo is { } start ? start : ReadSettings();
        var prepared = _prepared;
        var image = input.Image;
        var cts = _optCts = new CancellationTokenSource();
        int gen = ++_optGeneration;
        _optRevertTo = settings;
        _optGraph.Clear();
        _optDone = 0;
        if (only is null) _optResults.Clear();
        else _optResults.Remove(only);
        FillOptimizeHistory();
        SetOptText(UiStrings.Pixelate_Opt_Preparing);
        _status.Text = UiStrings.Pixelate_Opt_Preparing;
        UpdateOptimizeButtons();
        var clock = Stopwatch.StartNew();
        Task.Run(async () =>
        {
            var done = new List<OptimizeRun>();
            PixelateSource? src = null;
            try
            {
                src = prepared is not null && prepared.Matches(settings) ? prepared : PixelateSource.Prepare(image, settings);
                cts.Token.ThrowIfCancellationRequested();
                if (src.Split.Frames.Count == 0)
                {
                    Dispatcher.UIThread.Post(() => { if (gen == _optGeneration) EndOptimize(UiStrings.Pixelate_Status_NoSprite); });
                    return;
                }
                for (int k = 0; k < runs.Count && !cts.IsCancellationRequested; k++)
                {
                    var run = runs[k];
                    var running = (run.Name, k + 1, runs.Count);
                    bool published = false;
                    await Dispatcher.UIThread.InvokeAsync(() => { if (gen == _optGeneration) OnOptimizePresetStarted(running); });
                    var result = PixelateOptimizer.Run(src, settings, run.Options,
                        p => Dispatcher.UIThread.Post(() => { if (gen == _optGeneration && !published) OnOptimizeProgress(p); }), cts.Token);
                    var completed = new OptimizeRun(run.Preset, run.Name, run.Options.Params, result);
                    done.Add(completed);
                    // UI에 이번 후보를 넣고 미리보기를 고른 뒤에야 다음 프리셋을 돌린다.
                    await Dispatcher.UIThread.InvokeAsync(() => { if (gen == _optGeneration) { published = true; OnOptimizeResult(src, completed, done.Count == 1, only is not null); } });
                }
                Dispatcher.UIThread.Post(() => { if (gen == _optGeneration) OnOptimizeFinished(done, runs.Count, clock.Elapsed, only is not null); });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _optGeneration) return;
                    OnOptimizeFinished(done, runs.Count, clock.Elapsed, only is not null);
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or AggregateException)
            {
                var message = ex is AggregateException agg ? agg.Flatten().InnerExceptions[0].Message : ex.Message;
                Dispatcher.UIThread.Post(() => { if (gen == _optGeneration) EndOptimize(UiStrings.FormatPixelate_Status_Error(message: message)); });
            }
        });
    }

    /// <summary>설정 변경·창 닫기에서 진행 중인 최적화를 중단한다. 설정 변경은 이어서 후보를 다시 만든다.</summary>
    private void StopOptimize()
    {
        if (_optCts is null) return;
        _optCts.Cancel();
        _optGeneration++;
        EndOptimize(UiStrings.Pixelate_Opt_Stop_Cancelled);
    }

    private void EndOptimize(string message, string? tip = null)
    {
        _optCts?.Dispose();
        _optCts = null;
        SetOptText(message, tip);
        UpdateOptimizeButtons();
    }

    /// <summary>프리셋 하나를 돌리기 시작함: 그래프·시도 수를 새로(프리셋마다 점수 기준이 달라 이어 그리지 않음).</summary>
    private void OnOptimizePresetStarted((string Name, int Index, int Count) running)
    {
        _optRunning = running;
        _optDone = 0;
        _optGraph.Clear();
        var text = UiStrings.FormatPixelate_Opt_PresetLine(name: running.Name, summary: UiStrings.Pixelate_Opt_Preparing);
        SetOptText(text);
        _status.Text = text;
    }

    private void OnOptimizeProgress(PixelateOptimizeProgress p)
    {
        _optDone = Math.Max(_optDone, p.Done);
        _optGraph.Add(p.BestScore, p.Pass);
        var text = UiStrings.FormatPixelate_Opt_Progress(
            name: _optRunning.Name,
            index: _optRunning.Index,
            count: _optRunning.Count,
            pass: _optGraph.Pass,
            done: _optDone,
            total: p.Budget,
            score: _optGraph.Best.ToString("0.0", CultureInfo.CurrentCulture));
        SetOptText(text);
        _status.Text = text;
    }

    /// <summary>한 프리셋의 1위를 즉시 후보에 넣어 보여 준다. 사용자가 다른 후보를 고른 동안에는 그 선택을 유지한다.</summary>
    private void OnOptimizeResult(PixelateSource src, OptimizeRun run, bool first, bool rerun)
    {
        if (!ReferenceEquals(_prepared, src))
        {
            _prepared = src;
            _sourceView.SetImage(_input!.Image, src.Cleaned);
        }
        if (first && !rerun)
        {
            _candidates.Clear();
            _optimized.Clear();
            _selected = null;
            _userPicked = false;
        }
        var c = WithExcluded(run.Result.Best);
        int at = rerun ? _candidates.FindIndex(x => _optimized.TryGetValue(x, out var o) && ReferenceEquals(o.Preset, run.Preset)) : -1;
        bool replacingSelected = at >= 0 && ReferenceEquals(_selected, _candidates[at]);
        if (at >= 0)
        {
            _optimized.Remove(_candidates[at]);
            _candidates[at] = c;
        }
        else _candidates.Add(c);
        _optimized[c] = new OptimizedTile(run.Preset, run.Name, run.Result.StoppedReason);
        _optResults[run.Preset] = (run.Result, run.Params);
        RebuildTiles();
        if (!_userPicked || replacingSelected) Select(c);
        var text = UiStrings.FormatPixelate_Opt_PresetLine(name: run.Name, summary: OptimizeDone(run.Result));
        SetOptText(text);
        _status.Text = c.Result.Failed ? text + " · " + FailureNote(c.Result) : text;
        FillOptimizeHistory();
    }

    /// <summary>이미 화면에 넣은 후보는 그대로 두고, 돈 프리셋 수·걸린 시간과 프리셋별 결과를 표시한다.</summary>
    private void OnOptimizeFinished(IReadOnlyList<OptimizeRun> runs, int count, TimeSpan elapsed, bool rerun)
    {
        if (runs.Count == 0)
        {
            EndOptimize(UiStrings.Pixelate_Opt_Stop_Cancelled);
            return;
        }
        // 최종 선택의 설정을 왼쪽에 불러온다. 그동안 결과를 미리 봐도 다음 프리셋은 시작할 때의 같은 설정에서 돈다.
        var chosen = _selected!;
        _runFit = chosen.Settings.Cleanup.OutlineFit;
        LoadSettings(chosen.Settings);
        _presetSilent = true;
        _preset.SelectedIndex = -1;
        _presetSilent = false;
        _debounce.Stop();
        var lines = runs.Select(run => UiStrings.FormatPixelate_Opt_PresetLine(name: run.Name, summary: OptimizeDone(run.Result))).ToList();
        var summary = rerun ? lines[0] : UiStrings.FormatPixelate_Opt_PresetsDone(
            done: runs.Count,
            count: count,
            seconds: elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture));
        if (runs.Count < count || runs[^1].Result.StoppedReason == OptimizeStop.Cancelled) summary += " · " + UiStrings.Pixelate_Opt_Stop_Cancelled;
        EndOptimize(summary, string.Join("\n", lines));
        _status.Text = chosen.Result.Failed ? summary + " · " + FailureNote(chosen.Result) : summary;
    }

    /// <summary>프리셋 하나의 결과 한 줄: "돌림 k번 · a점 → b점 · 시도 n번 · t초 · 까닭".</summary>
    private static string OptimizeDone(PixelateOptimizeResult result)
    {
        string reason = result.StoppedReason switch
        {
            OptimizeStop.Trials => UiStrings.Pixelate_Opt_Stop_Trials,
            OptimizeStop.Time => UiStrings.Pixelate_Opt_Stop_Time,
            OptimizeStop.Cancelled => UiStrings.Pixelate_Opt_Stop_Cancelled,
            _ => UiStrings.Pixelate_Opt_Stop_Done,
        };
        return UiStrings.FormatPixelate_Opt_Done(
            passes: result.Passes.Count,
            start: result.Start.Score.Total.ToString("0.0", CultureInfo.CurrentCulture),
            best: result.Best.Score.Total.ToString("0.0", CultureInfo.CurrentCulture),
            trials: result.Trials.Count,
            seconds: result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture),
            reason: reason);
    }

    /// <summary>
    /// 시도 기록 표: 설정 창에서 고른 프리셋의 마지막 결과 한 줄과 점수 순 상위 50개(순위·점수·바꾼 값). 그 프리셋을 이번에 돌리지 않았으면 그 안내만.
    /// </summary>
    private void FillOptimizeHistory()
    {
        _optTable.Children.Clear();
        _optTable.RowDefinitions.Clear();
        if (_optPresets?.SelectedPreset is not { } preset || _optResults.Count == 0) return;
        void Cell(string text, int row, int col, bool bold, int span = 1)
        {
            var t = new TextBlock
            {
                Text = text,
                Foreground = bold ? AppTheme.TextBrush : AppTheme.DimTextBrush,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                FontSize = AppTheme.SmallFontSize,
                Margin = new Thickness(4, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            Grid.SetColumnSpan(t, span);
            _optTable.Children.Add(t);
        }
        _optTable.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (!_optResults.TryGetValue(preset, out var last))
        {
            Cell(UiStrings.Pixelate_Opt_HistoryEmpty, 0, 0, false, 3);
            return;
        }
        Cell(OptimizeDone(last.Result), 0, 0, false, 3);
        _optTable.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(UiStrings.Pixelate_Opt_ColRank, 1, 0, true);
        Cell(UiStrings.Pixelate_Opt_ColScore, 1, 1, true);
        Cell(UiStrings.Pixelate_Opt_ColSettings, 1, 2, true);
        var rows = last.Result.Top(50);
        for (int i = 0; i < rows.Count; i++)
        {
            var t = rows[i];
            int row = i + 2;
            _optTable.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell((i + 1).ToString(CultureInfo.CurrentCulture), row, 0, false);
            Cell(t.Score.Total.ToString("0.0", CultureInfo.CurrentCulture), row, 1, false);
            Cell(OptimizeSummary(t, last.Params) + (t.Candidate.Index == 0 ? " · " + UiStrings.Pixelate_Opt_StartRow : ""), row, 2, false);
        }
    }

    /// <summary>시도 하나에서 고른 항목의 값(칸 크기는 실제로 쓴 크기).</summary>
    private static string OptimizeSummary(PixelateTrial t, OptimizeParam p)
    {
        var ci = CultureInfo.CurrentCulture;
        var s = t.Settings;
        var parts = new List<string>();
        string OnOff(bool on) => on ? UiStrings.Pixelate_Opt_On : UiStrings.Pixelate_Opt_Off;
        if (p.HasFlag(OptimizeParam.Scale)) parts.Add(UiStrings.FormatPixelate_Opt_SumScale(scale: t.Result.Scale.ToString("0.##", ci)));
        if ((p & (OptimizeParam.OffsetX | OptimizeParam.OffsetY)) != 0 && s.Grid.Mode == GridMode.Manual)
            parts.Add($"{UiStrings.Pixelate_Grid_Offset} {s.Grid.OffsetX.ToString("0.##", ci)}, {s.Grid.OffsetY.ToString("0.##", ci)}");
        if (p.HasFlag(OptimizeParam.Colors)) parts.Add(UiStrings.FormatPixelate_Opt_SumColors(count: t.Result.ResolvedColors));
        if (p.HasFlag(OptimizeParam.DownsampleMode))
            parts.Add(s.Downsample.Mode switch
            {
                DownsampleMode.Median => UiStrings.Pixelate_Ds_Median,
                DownsampleMode.Center => UiStrings.Pixelate_Ds_Center,
                DownsampleMode.Mean => UiStrings.Pixelate_Ds_Mean,
                DownsampleMode.CenterWeighted => UiStrings.Pixelate_Ds_CenterWeighted,
                DownsampleMode.ClusterWeighted => UiStrings.Pixelate_Ds_ClusterWeighted,
                _ => UiStrings.Pixelate_Ds_Mode,
            });
        if (p.HasFlag(OptimizeParam.BorderFraction)) parts.Add($"{UiStrings.Pixelate_Opt_Border} {(s.Downsample.BorderFraction * 100).ToString("0", ci)}%");
        if (p.HasFlag(OptimizeParam.AlphaThreshold)) parts.Add($"{UiStrings.Pixelate_Opt_Alpha} {(s.Downsample.AlphaThreshold * 100).ToString("0", ci)}%");
        if (p.HasFlag(OptimizeParam.SnapStep)) parts.Add($"{UiStrings.Pixelate_Opt_Snap} {s.Downsample.SnapStep.ToString(ci)}");
        if (p.HasFlag(OptimizeParam.OutlineDarkness)) parts.Add($"{UiStrings.Pixelate_Opt_Darkness} {s.Palette.OutlineDarkness.ToString("0.00", ci)}");
        if (p.HasFlag(OptimizeParam.Cleanup))
            parts.Add($"{UiStrings.Pixelate_Opt_SumOrphans} {OnOff(s.Cleanup.RemoveOrphans)}, {UiStrings.Pixelate_Opt_SumHoles} {OnOff(s.Cleanup.FillHoles)}, {UiStrings.Pixelate_Opt_SumJaggies} {OnOff(s.Cleanup.ReduceJaggies)}");
        if (p.HasFlag(OptimizeParam.Dither))
            parts.Add(s.Palette.Dither switch
            {
                PaletteDither.Bayer2 => UiStrings.Pixelate_Dither_Bayer2,
                PaletteDither.Bayer4 => UiStrings.Pixelate_Dither_Bayer4,
                _ => UiStrings.Pixelate_Dither_None,
            });
        if (p.HasFlag(OptimizeParam.OutlineFit)) parts.Add(FitName(s.Cleanup.OutlineFit));
        if (s.Palette.Method is PaletteMethod.KMeans or PaletteMethod.MedianCut)
        {
            if (p.HasFlag(OptimizeParam.PaletteMethod))
                parts.Add(s.Palette.Method == PaletteMethod.MedianCut ? UiStrings.Pixelate_Pal_MedianCut : UiStrings.Pixelate_Pal_KMeans);
            if (p.HasFlag(OptimizeParam.PaletteColorSource))
                parts.Add(s.Palette.ColorSource == PaletteColorSource.Mean ? UiStrings.Pixelate_Pal_Source_Mean : UiStrings.Pixelate_Pal_Source_Original);
            if (p.HasFlag(OptimizeParam.EdgeWeight) && s.Palette.ColorSource == PaletteColorSource.Original)
                parts.Add($"{UiStrings.Pixelate_Opt_EdgeWeight} {s.Palette.EdgeWeight.ToString("0.00", ci)}");
            if (p.HasFlag(OptimizeParam.DistinctHues))
                parts.Add(s.Palette.KeepDistinctHues
                    ? $"{UiStrings.Pixelate_Pal_Distinct} {s.Palette.DistinctMinHue.ToString("0", ci)}°"
                    : $"{UiStrings.Pixelate_Pal_Distinct} {OnOff(false)}");
        }
        if (p.HasFlag(OptimizeParam.ColorDistance) && s.Palette.Method != PaletteMethod.KeepOriginal)
        {
            parts.Add(s.Palette.ColorDistance switch
            {
                ColorDistanceMode.OkLab => UiStrings.Pixelate_Pal_Distance_OkLab,
                ColorDistanceMode.Ciede2000 => UiStrings.Pixelate_Pal_Distance_Ciede2000,
                _ => $"{UiStrings.Pixelate_Pal_Distance_OkLchHue} {s.Palette.HueWeight.ToString("0.##", ci)}",
            });
        }
        if (p.HasFlag(OptimizeParam.ShadeFamily) && s.Palette.Method != PaletteMethod.KeepOriginal)
            parts.Add($"{UiStrings.Pixelate_Pal_ShadeFamily} {OnOff(s.Palette.PreferShadeFamily)}");
        if (p.HasFlag(OptimizeParam.SnapOutline)) parts.Add($"{UiStrings.Pixelate_Opt_SnapOutline} {OnOff(s.Palette.SnapOutline)}");
        if (p.HasFlag(OptimizeParam.StickerBorder)) parts.Add($"{UiStrings.Pixelate_Bg_Sticker} {OnOff(s.Background.RemoveStickerBorder)}");
        if (s.Cleanup.OutlineFit == OutlineFit.Vector)
        {
            if (p.HasFlag(OptimizeParam.VectorTolerance)) parts.Add($"{UiStrings.Pixelate_Clean_VectorTolerance} {s.Cleanup.VectorTolerance.ToString("0.00", ci)}");
            if (p.HasFlag(OptimizeParam.VectorBlob)) parts.Add($"{UiStrings.Pixelate_Clean_VectorBlob} {s.Cleanup.VectorBlobCells.ToString("0", ci)}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>최적화 전 설정으로 되돌린다(보통처럼 다시 돌림).</summary>
    private void RevertOptimize()
    {
        if (_optRevertTo is not { } s) return;
        StopOptimize();
        _optRevertTo = null;
        LoadSettings(s);
        UpdateOptimizeButtons();
        Changed();
    }

    // ───────────── 적용·AI ─────────────

    /// <summary>고른 후보를 넣는다. <paramref name="newDocument"/>면 넣기 설정과 관계없이 새 탭의 새 문서로(결과 크기·프레임·길이 그대로).</summary>
    private void Apply(PixelateCandidate c, bool newDocument)
    {
        if (_input is not { } input) return;
        SaveOptions();
        _apply(new PixelateOutcome(input, c.Result, newDocument || _outDoc.On() || !_canInsertLayer, (int)_outDuration.NumValue(), _outPalette.On(),
            _aiTouch.On(), _aiPrompt.Text ?? "", ProviderId, _aiReference.On()));
    }

    private string ProviderId => Ai.AiProvider.All[Math.Clamp(_aiProvider.SelectedIndex, 0, Ai.AiProvider.All.Count - 1)].Id;

    private void AskAiToRank()
    {
        if (_input is not { } input || _candidates.Count == 0) return;
        var top = _candidates.Take((int)_aiRankCount.NumValue()).ToList();
        var list = string.Join(", ", top.Select((c, i) => UiStrings.FormatPixelate_Ai_RankItem(
            index: i + 1,
            score: c.Score.Total.ToString("0.0", CultureInfo.CurrentCulture),
            colors: c.Result.Palette.Count,
            width: c.Result.Size.Width,
            height: c.Result.Size.Height)));
        var images = new List<PixelImage> { input.Image };
        images.AddRange(top.Select(c => Strip(c.Result.CommonFrames())));
        _askAi(ProviderId, UiStrings.FormatPixelate_Ai_RankPrompt(count: top.Count, list: list), images);
    }

    /// <summary>프레임을 1px 띄워 가로로 이은 그림(AI에 후보 하나를 한 장으로 보냄).</summary>
    private static PixelImage Strip(IReadOnlyList<PixelImage> frames)
    {
        int w = frames.Sum(f => f.Width) + Math.Max(0, frames.Count - 1), h = frames.Max(f => f.Height);
        var strip = new PixelImage(Math.Max(1, w), Math.Max(1, h));
        int x = 0;
        foreach (var f in frames)
        {
            strip.Blit(f, x, 0);
            x += f.Width + 1;
        }
        return strip;
    }

    // ───────────── 프리셋 ─────────────

    private bool _presetApplying, _presetSilent;

    private static readonly (PixelateBuiltIn Kind, Func<string> Name)[] BuiltIns =
    [
        (PixelateBuiltIn.Default, () => UiStrings.Pixelate_Preset_Default),
        (PixelateBuiltIn.Small32, () => UiStrings.Pixelate_Preset_Small32),
        (PixelateBuiltIn.Colors16, () => UiStrings.Pixelate_Preset_Colors16),
        (PixelateBuiltIn.KeepColors, () => UiStrings.Pixelate_Preset_KeepColors),
    ];

    private List<PixelatePreset> LoadUserPresets()
    {
        try { return PixelatePresets.Load(_presetsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private void FillPresets()
    {
        _presetSilent = true;
        _preset.Items.Clear();
        foreach (var (_, name) in BuiltIns) _preset.Items.Add(name());
        foreach (var p in _userPresets) _preset.Items.Add(p.Name);
        _preset.SelectedIndex = -1;
        _presetSilent = false;
    }

    private void OnPresetChosen()
    {
        if (_presetSilent || _preset.SelectedIndex < 0) return;
        int i = _preset.SelectedIndex;
        var s = i < BuiltIns.Length ? PixelatePresets.BuiltIn(BuiltIns[i].Kind) : _userPresets[i - BuiltIns.Length].Settings;
        _presetApplying = true;
        LoadSettings(s);
        _presetApplying = false;
        _debounce.Stop();
        _debounce.Start();
    }

    private void SavePreset()
    {
        if (InputDialog.Ask(this, UiStrings.Pixelate_Preset_Save, UiStrings.Pixelate_Preset_SavePrompt, "") is not { } name) return;
        var settings = ReadSettings();
        _userPresets.RemoveAll(p => p.Name == name);
        _userPresets.Add(new PixelatePreset(name, settings));
        if (!WritePresets()) return;
        FillPresets();
        _presetSilent = true;
        _preset.SelectedIndex = BuiltIns.Length + _userPresets.FindIndex(p => p.Name == name);
        _presetSilent = false;
    }

    private void DeletePreset()
    {
        int i = _preset.SelectedIndex - BuiltIns.Length;
        if (i < 0 || i >= _userPresets.Count) return;
        _userPresets.RemoveAt(i);
        if (WritePresets()) FillPresets();
    }

    private bool WritePresets()
    {
        try
        {
            PixelatePresets.Save(_presetsPath, _userPresets);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, UiStrings.FormatPixelate_Preset_SaveFailed(message: ex.Message));
            _userPresets = LoadUserPresets();
            return false;
        }
    }

    // ───────────── 기억 ─────────────

    private void SaveOptions()
    {
        _opt.CandidateCount = (int)_candCount.NumValue();
        _opt.Vary = (int)ReadVary();
        if (_canInsertLayer) _opt.NewDocument = _outDoc.On();
        _opt.FrameDurationMs = (int)_outDuration.NumValue();
        _opt.Fps = (int)_fps.NumValue();
        _opt.SavePalette = _outPalette.On();
        _opt.AiTouchUp = _aiTouch.On();
        _opt.AiPrompt = _aiPrompt.Text == UiStrings.Pixelate_Ai_DefaultPrompt ? null : _aiPrompt.Text;
        _opt.AiProvider = ProviderId;
        _opt.AddReference = _aiReference.On();
        _opt.AiRankCount = (int)_aiRankCount.NumValue();
        _opt.AutoOptimizeOnLoad = _optAuto.On();
        _opt.ShowGrid = _showGrid.On();
        _opt.ShowMask = _showMask.On();
        _opt.SettingsExpanded = _settingsExpanded;
        if (WindowState == WindowState.Normal)
        {
            _opt.Width = (int)Width;
            _opt.Height = (int)Height;
        }
        if (!_presetGiven)
        {
            var s = ReadSettings();
            if (_restoreFrames is { } frames) s = s with { Frames = frames };
            _settings.PixelateSettings = PixelatePresets.SerializeSettings(s, indented: false);
        }
        _settings.Save();
    }

    private void OnClosedSave()
    {
        _cts?.Cancel();
        StopOptimize();
        _debounce.Stop();
        _pickPulse.Stop();
        _play.Stop();
        SaveOptions();
    }

    // ───────────── 보기 ─────────────

    /// <summary>원본에서 고른 칸 하나: 프레임 번호, 칸 열·행(결과 픽셀 = 열 + <see cref="PixelateFrameMap.OffsetX"/>, 행 + <see cref="PixelateFrameMap.OffsetY"/>).</summary>
    private readonly record struct CellHover(int Frame, int Column, int Row);

    /// <summary>
    /// 고른 후보가 실제로 쓴 칸 경계(프레임마다 동적 계획법으로 맞춘 들쭉날쭉한 경계 그대로 — 지역 보정이면 띠마다 꺾인 경계, 칸 사각형마다 왼쪽·위 변)를
    /// 원본 위에 그린다. 어두운 테두리 + 밝은 반투명 선이라 배경·그림 위 모두 보이고, 칸이 화면에서 3px 미만이면 숨기고 3~8px이면 흐리게.
    /// 크게 보기의 결과 칸에는 같은 모양으로 결과 픽셀마다 한 칸인 격자(<see cref="DrawPixels"/>)를 그린다.
    /// </summary>
    private static class CutOverlay
    {
        /// <summary>칸 선 펜(어두운 밑선 + 밝은 선): 칸이 화면에서 <paramref name="hide"/>px 미만이면 null, 그 위 5px 동안은 흐리게.</summary>
        private static (IPen Under, IPen Over)? Pens(double cell, double hide)
        {
            if (cell < hide) return null;
            double k = Math.Clamp((cell - hide) / 5, 0.25, 1);
            return (AppTheme.Pen(Color.FromArgb((byte)(110 * k), 0, 0, 0), 3), AppTheme.Pen(Color.FromArgb((byte)(170 * k), 235, 245, 255)));
        }

        /// <summary>
        /// 결과 픽셀마다 한 칸인 격자(<paramref name="dest"/> = 결과 그림의 화면 사각형, 픽셀 <paramref name="width"/>×<paramref name="height"/>) —
        /// 원본 칸 선과 같은 모양, 픽셀이 화면에서 4px 미만이면 숨긴다. 보이는 자리(<paramref name="clip"/>)의 선만 그린다.
        /// </summary>
        public static void DrawPixels(DrawingContext g, Rect dest, int width, int height, Rect clip)
        {
            if (width <= 0 || height <= 0) return;
            double z = dest.Width / width;
            if (Pens(z, 4) is not { } pens) return;
            var (under, over) = pens;
            double top = Math.Max(clip.Top, dest.Top), bottom = Math.Min(clip.Bottom, dest.Bottom);
            double left = Math.Max(clip.Left, dest.Left), right = Math.Min(clip.Right, dest.Right);
            if (top >= bottom || left >= right) return;
            int x0 = Math.Max(0, (int)Math.Floor((left - dest.X) / z)), x1 = Math.Min(width, (int)Math.Ceiling((right - dest.X) / z));
            int y0 = Math.Max(0, (int)Math.Floor((top - dest.Y) / z)), y1 = Math.Min(height, (int)Math.Ceiling((bottom - dest.Y) / z));
            foreach (var pen in new[] { under, over })
            {
                for (int x = x0; x <= x1; x++)
                {
                    double px = Math.Round(dest.X + x * z) + 0.5;
                    if (px >= clip.Left && px <= clip.Right) g.DrawLine(pen, new Avalonia.Point(px, top), new Avalonia.Point(px, bottom));
                }
                for (int y = y0; y <= y1; y++)
                {
                    double py = Math.Round(dest.Y + y * z) + 0.5;
                    if (py >= clip.Top && py <= clip.Bottom) g.DrawLine(pen, new Avalonia.Point(left, py), new Avalonia.Point(right, py));
                }
            }
        }

        public static void Draw(DrawingContext g, PixelateFrameMap m, Func<double, double> sx, Func<double, double> sy, double zoom, Rect clip)
        {
            double cell = m.XCuts.Length > 1 ? (m.XCuts[^1] - m.XCuts[0]) / (double)(m.XCuts.Length - 1) * zoom : 0;
            if (Pens(cell, 3) is not { } pens) return;
            var (under, over) = pens;
            double top = Math.Max(clip.Top, sy(m.YCuts[0])), bottom = Math.Min(clip.Bottom, sy(m.YCuts[^1]));
            double left = Math.Max(clip.Left, sx(m.XCuts[0])), right = Math.Min(clip.Right, sx(m.XCuts[^1]));
            if (top >= bottom || left >= right) return;
            if (m.Local is not null)
            {
                var geometry = LocalGeometry(m.Layout(), sx, sy, clip);
                g.DrawGeometry(null, under, geometry);
                g.DrawGeometry(null, over, geometry);
                return;
            }
            foreach (var pen in new[] { under, over })
            {
                foreach (int x in m.XCuts)
                {
                    double px = Math.Round(sx(x)) + 0.5;
                    if (px >= clip.Left && px <= clip.Right) g.DrawLine(pen, new Avalonia.Point(px, top), new Avalonia.Point(px, bottom));
                }
                foreach (int y in m.YCuts)
                {
                    double py = Math.Round(sy(y)) + 0.5;
                    if (py >= clip.Top && py <= clip.Bottom) g.DrawLine(pen, new Avalonia.Point(left, py), new Avalonia.Point(right, py));
                }
            }
        }

        /// <summary>지역 보정한 칸: 칸 사각형마다 왼쪽·위 변(끝 열·행은 오른쪽·아래 변도) — 화면 밖 칸은 뺌.</summary>
        private static StreamGeometry LocalGeometry(CellLayout layout, Func<double, double> sx, Func<double, double> sy, Rect clip)
        {
            var geometry = new StreamGeometry();
            using var ctx = geometry.Open();
            void Segment(double x0, double y0, double x1, double y1)
            {
                ctx.BeginFigure(new Avalonia.Point(x0, y0), false);
                ctx.LineTo(new Avalonia.Point(x1, y1));
                ctx.EndFigure(false);
            }
            for (int r = 0; r < layout.Rows; r++)
                for (int c = 0; c < layout.Columns; c++)
                {
                    var (cx0, cy0, cx1, cy1) = layout.Cell(c, r);
                    double l = Math.Round(sx(cx0)) + 0.5, t = Math.Round(sy(cy0)) + 0.5, rr = Math.Round(sx(cx1)) + 0.5, b = Math.Round(sy(cy1)) + 0.5;
                    if (rr < clip.Left || l > clip.Right || b < clip.Top || t > clip.Bottom) continue;
                    Segment(l, t, l, b);
                    Segment(l, t, rr, t);
                    if (c == layout.Columns - 1) Segment(rr, t, rr, b);
                    if (r == layout.Rows - 1) Segment(l, b, rr, b);
                }
            return geometry;
        }

        /// <summary>
        /// 원본 좌표의 칸(프레임 범위·칸 경계 밖이면 null). 지역 보정이면 그 자리의 꺾인 경계로 — 띠가 바뀌는 모서리의 작은 틈도 정리와 같은 칸
        /// (<see cref="CellLayout.Locate"/>)으로 가서 결과의 같은 픽셀이 강조된다.
        /// </summary>
        public static CellHover? Hit(IReadOnlyList<PixelateFrameMap> maps, double x, double y)
        {
            for (int f = 0; f < maps.Count; f++)
                if (HitFrame(maps[f], f, x, y) is { } h) return h;
            return null;
        }

        /// <summary>프레임 <paramref name="f"/>(칸 대응 <paramref name="m"/>)만 보고 <see cref="Hit"/>(그 프레임의 범위·칸 경계 밖이면 null).</summary>
        public static CellHover? HitFrame(PixelateFrameMap m, int f, double x, double y)
        {
            if (!m.SourceBounds.Contains((int)Math.Floor(x), (int)Math.Floor(y))) return null;
            if (x < m.XCuts[0] || x >= m.XCuts[^1] || y < m.YCuts[0] || y >= m.YCuts[^1]) return null;
            m.Layout().Locate(x, y, out int c, out int r);
            return new CellHover(f, c, r);
        }

        /// <summary>원본 보기들의 스포이드 커서(그림 편집의 스포이드 도구와 같음).</summary>
        public static Cursor PickerCursor => PixelCanvas.PickerCursor.Value;

        /// <summary>칸 (c, r)의 원본 사각형(지역 보정이면 그 칸의 띠 경계).</summary>
        public static (int X0, int Y0, int X1, int Y1) CellRect(PixelateFrameMap m, int c, int r) => m.Layout().Cell(c, r);

        public static Pen Highlight { get; } = new(new SolidColorBrush(Color.FromRgb(255, 210, 60)), 2);

        /// <summary>
        /// 스포이드 중 원본 그림 둘레: 강조색 테두리(3px)와 안쪽 흰 선(1px) — <paramref name="level"/>(0~1, 대화상자가 약 1.2초 주기로 오르내림)만큼 밝고 진하게.
        /// </summary>
        public static void DrawPickPulse(DrawingContext g, Rect r, double level)
        {
            if (r.Width <= 2 || r.Height <= 2) return;
            var accent = AppTheme.AccentColor;
            byte a = (byte)Math.Round(70 + 185 * Math.Clamp(level, 0, 1));
            g.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(a, accent.R, accent.G, accent.B)), 3), r.Deflate(1.5));
            g.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a / 2), 255, 255, 255)), 1), r.Deflate(3.5));
        }

        private static readonly Pen MarkUnder = new(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 4);
        private static readonly Pen MarkOver = new(AppTheme.AccentBrush, 2);

        /// <summary>누른 결과 픽셀 표시: 픽셀 둘레(어두운 밑선 위 강조색) — 픽셀이 작으면 적어도 8px 사각형.</summary>
        public static void DrawMark(DrawingContext g, Rect cell)
        {
            double grow = Math.Max(0, (8 - cell.Width) / 2);
            var r = cell.Inflate(grow + 1);
            g.DrawRectangle(null, MarkUnder, r);
            g.DrawRectangle(null, MarkOver, r);
        }
    }

    /// <summary>
    /// 원본 보기: 휠로 확대·축소(마우스 자리 기준), 끌어서 이동. 고른 후보가 쓴 칸 경계와 배경 지운 모습을 겹쳐 볼 수 있고,
    /// 마우스를 올린 칸을 강조해 알린다(<see cref="HoverChanged"/> — 큰 미리보기가 결과의 같은 픽셀을 강조).
    /// 스포이드 중(<see cref="Picking"/>)이면 스포이드 커서에 칸 경계를 켜지 않아도 올린 칸을 강조하고, 왼쪽 클릭 = 그 칸 고르기(<see cref="CellPicked"/>, 끌기 대신),
    /// 오른쪽 클릭 = 그만(<see cref="PickCancelled"/>). 휠·가운데 끌기는 그대로.
    /// </summary>
    private sealed class ZoomImageView : Control
    {
        /// <summary>제목에 붙이는 그림 크기(원본 px 또는 결과 px).</summary>
        public static string SizeText(double width, double height) => $"{width:0}×{height:0}px";
        private WriteableBitmap? _image, _alt;
        private PixelImage? _imageData, _altData;
        private Size _size;
        private IReadOnlyList<PixelateFrameMap> _maps = [];
        private double _zoom = 1;
        private Vector _offset;
        private bool _fitted;
        private Avalonia.Point? _drag;
        private CellHover? _hover;

        public ZoomImageView()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        public event Action<CellHover?>? HoverChanged;
        /// <summary>스포이드 중 칸을 왼쪽 클릭(두 번째 값 = Shift — 스포이드를 계속).</summary>
        public event Action<CellHover, bool>? CellPicked;
        /// <summary>스포이드 중 오른쪽 클릭.</summary>
        public event Action? PickCancelled;

        public bool Picking
        {
            get => field;
            set
            {
                field = value;
                Cursor = value ? CutOverlay.PickerCursor : null;
                if (!value && !ShowGrid) SetHover(null);
                InvalidateVisual();
            }
        }

        public bool ShowGrid { get => field; set { field = value; InvalidateVisual(); } }
        public bool ShowAlt { get => field; set { field = value; InvalidateVisual(); } }
        /// <summary>스포이드 중 원본 둘레 강조 밝기(0~1, null = 그리지 않음 — <see cref="CutOverlay.DrawPickPulse"/>).</summary>
        public double? PickPulse { get => field; set { field = value; InvalidateVisual(); } }

        /// <summary>지금 보이는 그림(배경 지운 모습을 켰으면 그것, 아니면 원본). 비었으면 null.</summary>
        public PixelImage? ShownImage => ShowAlt && _altData is not null ? _altData : _imageData;

        public void SetImage(PixelImage image, PixelImage? alt)
        {
            _imageData = image;
            _altData = alt;
            if (_image is null || _size != image.Size)
            {
                _image?.Dispose();
                _image = ImageUtil.ToBitmap(image);
                _size = image.Size;
                _fitted = false;
            }
            _alt?.Dispose();
            _alt = alt is null ? null : ImageUtil.ToBitmap(alt);
            InvalidateVisual();
        }

        public void SetGrid(IReadOnlyList<PixelateFrameMap> maps)
        {
            _maps = maps;
            SetHover(null);
            InvalidateVisual();
        }

        private void SetHover(CellHover? h)
        {
            if (h == _hover) return;
            _hover = h;
            InvalidateVisual();
            HoverChanged?.Invoke(h);
        }

        private void Fit()
        {
            if (_size.Width <= 0 || Bounds.Width <= 0) return;
            _zoom = Math.Min((Bounds.Width - 8) / _size.Width, (Bounds.Height - 26) / _size.Height);
            _offset = new Vector((Bounds.Width - _size.Width * _zoom) / 2, 20 + (Bounds.Height - 20 - _size.Height * _zoom) / 2);
            _fitted = true;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            var p = e.GetPosition(this);
            double k = e.Delta.Y > 0 ? 1.25 : 1 / 1.25;
            double z = Math.Clamp(_zoom * k, 0.05, 64);
            _offset = new Vector(p.X - (p.X - _offset.X) * z / _zoom, p.Y - (p.Y - _offset.Y) * z / _zoom);
            _zoom = z;
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            if (Picking && (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed))
            {
                e.Handled = true;
                if (point.Properties.IsRightButtonPressed) { PickCancelled?.Invoke(); return; }
                var p = point.Position;
                if (CutOverlay.Hit(_maps, (p.X - _offset.X) / _zoom, (p.Y - _offset.Y) / _zoom) is { } h)
                    CellPicked?.Invoke(h, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                return;
            }
            // 오른쪽 버튼은 끌기가 아니라 우클릭 메뉴(이미지 저장·복사).
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return;
            _drag = e.GetPosition(this);
            e.Pointer.Capture(this);
            if (e.ClickCount == 2) { Fit(); InvalidateVisual(); }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var p = e.GetPosition(this);
            if (_drag is { } start)
            {
                _offset += p - start;
                _drag = p;
                InvalidateVisual();
                return;
            }
            SetHover(ShowGrid || Picking ? CutOverlay.Hit(_maps, (p.X - _offset.X) / _zoom, (p.Y - _offset.Y) / _zoom) : null);
        }

        protected override void OnPointerExited(PointerEventArgs e) => SetHover(null);

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            _drag = null;
            e.Pointer.Capture(null);
        }

        public override void Render(DrawingContext g)
        {
            var bounds = new Rect(Bounds.Size);
            g.FillRectangle(AppTheme.BackBrush, bounds);
            if (!_fitted) Fit();
            var bmp = ShowAlt && _alt is not null ? _alt : _image;
            if (bmp is not null)
            {
                var dest = new Rect(_offset.X, _offset.Y, _size.Width * _zoom, _size.Height * _zoom);
                AppTheme.DrawChecker(g, dest.Intersect(bounds), 8);
                using (ImageUtil.Pixelated(g))
                    g.DrawImage(bmp, new Rect(0, 0, _size.Width, _size.Height), dest);
                if (ShowGrid) DrawGrid(g, bounds);
                DrawHover(g);
                if (PickPulse is { } level) CutOverlay.DrawPickPulse(g, dest.Intersect(new Rect(0, 18, bounds.Width, Math.Max(0, bounds.Height - 18))), level);
            }
            var caption = new Rect(0, 0, bounds.Width, 18);
            g.FillRectangle(AppTheme.BackBrush, caption);
            var text = UiStrings.FormatPixelate_Source(size: _image is null ? "" : SizeText(_size.Width, _size.Height));
            AppTheme.DrawTextIn(g, text, new Rect(4, 0, bounds.Width - 8, 18), AppTheme.DimTextBrush, AppTheme.SmallFontSize);
        }

        private void DrawGrid(DrawingContext g, Rect bounds)
        {
            var box = AppTheme.Pen(Color.FromArgb(220, 255, 200, 70));
            double X(double x) => _offset.X + x * _zoom;
            double Y(double y) => _offset.Y + y * _zoom;
            foreach (var m in _maps)
            {
                var b = m.SourceBounds;
                g.DrawRectangle(null, box, new Rect(X(b.Left), Y(b.Top), b.Width * _zoom, b.Height * _zoom));
                CutOverlay.Draw(g, m, X, Y, _zoom, bounds);
            }
        }

        private void DrawHover(DrawingContext g)
        {
            if (_hover is not { } h || h.Frame >= _maps.Count) return;
            var (x0, y0, x1, y1) = CutOverlay.CellRect(_maps[h.Frame], h.Column, h.Row);
            g.DrawRectangle(null, CutOverlay.Highlight, new Rect(_offset.X + x0 * _zoom, _offset.Y + y0 * _zoom, (x1 - x0) * _zoom, (y1 - y0) * _zoom));
        }
    }

    /// <summary>
    /// 색 칸 하나(14×14): 색 + 테두리. 뺀 색이면 빨간 테두리와 왼쪽 위 → 오른쪽 아래 빗금(선으로 그림),
    /// 고정 색이면 파란 테두리와 오른쪽 위 모서리의 파란 삼각형(선·도형으로 그림).
    /// </summary>
    private sealed class ColorSwatch : Control
    {
        private static readonly IPen ExcludedPen = AppTheme.Pen(AppTheme.UnsavedColor, 1.5);
        private static readonly IPen ExcludedUnder = AppTheme.Pen(Color.FromArgb(200, 255, 255, 255), 3);
        private static readonly IPen NormalPen = AppTheme.Pen(AppTheme.BorderColor);
        private static readonly IPen PinnedPen = AppTheme.Pen(AppTheme.AccentColor, 1.5);
        private static readonly IPen CornerPen = AppTheme.Pen(Color.FromArgb(220, 20, 20, 24));
        private readonly IBrush _fill;
        private readonly bool _excluded, _pinned;
        private static readonly IPen HighlightUnder = AppTheme.Pen(Color.FromArgb(230, 255, 255, 255), 5);
        private static readonly IPen HighlightPen = AppTheme.Pen(AppTheme.AccentColor, 3);

        public ColorSwatch(Color32 color, bool excluded, bool pinned)
        {
            Value = color;
            _fill = new SolidColorBrush(color.WithAlpha(255).ToColor());
            _excluded = excluded;
            _pinned = pinned;
            Width = Height = 14;
        }

        public Color32 Value { get; }

        /// <summary>결과에서 누른 픽셀의 색: 굵은 강조색 테두리.</summary>
        public bool Highlighted { get => field; set { field = value; InvalidateVisual(); } }

        public override void Render(DrawingContext g)
        {
            DrawBody(g);
            if (!Highlighted) return;
            var r = new Rect(Bounds.Size);
            g.DrawRectangle(null, HighlightUnder, r.Inflate(1));
            g.DrawRectangle(null, HighlightPen, r.Inflate(1));
        }

        private void DrawBody(DrawingContext g)
        {
            var r = new Rect(Bounds.Size);
            g.FillRectangle(_fill, r);
            if (_pinned)
            {
                g.DrawRectangle(null, PinnedPen, r.Deflate(0.75));
                var corner = new StreamGeometry();
                using (var ctx = corner.Open())
                {
                    ctx.BeginFigure(new Avalonia.Point(r.Right - 7, r.Top), true);
                    ctx.LineTo(new Avalonia.Point(r.Right, r.Top));
                    ctx.LineTo(new Avalonia.Point(r.Right, r.Top + 7));
                    ctx.EndFigure(true);
                }
                g.DrawGeometry(AppTheme.AccentBrush, CornerPen, corner);
                return;
            }
            if (!_excluded)
            {
                g.DrawRectangle(null, NormalPen, r.Deflate(0.5));
                return;
            }
            using (g.PushClip(r))
            {
                g.DrawLine(ExcludedUnder, r.TopLeft, r.BottomRight);
                g.DrawLine(ExcludedPen, r.TopLeft, r.BottomRight);
            }
            g.DrawRectangle(null, ExcludedPen, r.Deflate(0.75));
        }
    }

    /// <summary>색 칸 줄 끝의 '+' 칸(색 칸과 같은 14×14): 원본에서 색 추가(스포이드). 스포이드 중에는 눌린 모양(파란 바탕).</summary>
    private sealed class PinAddCell : Control
    {
        private static readonly IPen BorderPen = AppTheme.Pen(AppTheme.BorderColor);
        private static readonly IPen PressedPen = AppTheme.Pen(AppTheme.AccentColor);

        public PinAddCell()
        {
            Width = Height = 14;
        }

        public bool Pressed { get => field; set { field = value; InvalidateVisual(); } }

        public override void Render(DrawingContext g)
        {
            var r = new Rect(Bounds.Size);
            g.FillRectangle(Pressed ? AppTheme.AccentBrush : AppTheme.PanelBrush, r);
            g.DrawRectangle(null, Pressed ? PressedPen : BorderPen, r.Deflate(0.5));
            VectorIcons.Draw(g, VectorIcon.Plus, r, 12, Pressed ? Brushes.White : AppTheme.TextBrush, 2.5);
        }
    }

    /// <summary>
    /// 색 칸 묶음: "색 N개"(결과가 실제로 쓴 색 수) + 고른 후보가 쓴 색 칸(많이 쓴 색부터), 결과에 없는 고정 색(흐리게), 결과에 없는 뺀 색(되살릴 수 있게 끝에 —
    /// 빗금·빨간 테두리, 흐리게)과 줄 끝의 '+' 칸(<see cref="PinAddCell"/> — 원본에서 색 추가). 풍선 = HEX·픽셀 수.
    /// 아래에 뺀 색이 있으면 "색 빼기 N개 적용 중 · 모두 되살리기", 고정 색이 있으면 "고정 색 N개 · 모두 해제".
    /// 좌클릭 = 쓰인 곳 보기(<see cref="PixelateDialog.ShowColorUse"/> — 뺀 색이면 있던 자리), 우클릭 = 고정 색이면 고정 해제, 아니면 이 색 빼기/되살리기(<see cref="PixelateDialog.ToggleColor"/>).
    /// 큰 미리보기와 크게 보기가 하나씩 두고, 대화상자가 같은 색·뺀 색·고정 색으로 함께 채운다(<see cref="PixelateDialog.ShowColors"/>).
    /// </summary>
    private sealed class ColorsBlock : StackPanel
    {
        /// <summary>결과에 없는 색 칸(뺀 색·쓰이지 않은 고정 색)의 불투명도.</summary>
        private const double AbsentOpacity = 0.5;
        private readonly PixelateDialog _owner;
        private readonly TextBlock _label, _excludeText, _pinText;
        private readonly WrapPanel _swatches;
        private readonly StackPanel _notice, _pinNotice;
        private readonly PinAddCell _add;

        public ColorsBlock(PixelateDialog owner)
        {
            _owner = owner;
            _label = new TextBlock { Foreground = AppTheme.TextBrush, FontSize = AppTheme.SmallFontSize, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 2) };
            ToolTip.SetTip(_label, UiStrings.Pixelate_Colors_Tip);
            _swatches = new WrapPanel { Orientation = Orientation.Horizontal };
            _add = new PinAddCell { Margin = new Thickness(0, 0, 1, 1), Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(_add, UiStrings.Pixelate_Pin_Add);
            _add.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(_add).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                owner.SetPicking(!owner._picking);
            };
            _excludeText = new TextBlock { Foreground = AppTheme.WarningBrush, FontSize = AppTheme.SmallFontSize, VerticalAlignment = VerticalAlignment.Center };
            _notice = Notice(_excludeText, UiStrings.Pixelate_Exclude_RestoreAll, owner.RestoreAllColors);
            _pinText = new TextBlock { Foreground = AppTheme.AccentBrush, FontSize = AppTheme.SmallFontSize, VerticalAlignment = VerticalAlignment.Center };
            _pinNotice = Notice(_pinText, UiStrings.Pixelate_Pin_ClearAll, owner.ClearPins);
            Children.Add(_label);
            Children.Add(_swatches);
            Children.Add(_notice);
            Children.Add(_pinNotice);
            _swatches.Children.Add(_add);
        }

        /// <summary>안내 한 줄: 글 · 밑줄 친 링크 단추(<paramref name="link"/>).</summary>
        private static StackPanel Notice(TextBlock text, string link, Action click)
        {
            var button = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new TextBlock
                {
                    Text = link,
                    Foreground = AppTheme.AccentBrush,
                    FontSize = AppTheme.SmallFontSize,
                    TextDecorations = TextDecorations.Underline,
                },
            };
            button.Click += (_, _) => click();
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 2, 0, 0),
                IsVisible = false,
                Children = { text, new TextBlock { Text = "·", Foreground = AppTheme.DimTextBrush, FontSize = AppTheme.SmallFontSize, VerticalAlignment = VerticalAlignment.Center }, button },
            };
        }

        /// <summary>스포이드 중('+' 칸이 눌린 모양).</summary>
        public bool Picking { set => _add.Pressed = value; }

        /// <param name="used">결과가 쓴 색과 픽셀 수(보일 순서대로).</param>
        /// <param name="excluded">뺀 색(HEX).</param>
        /// <param name="pinned">고정 색(HEX).</param>
        public void Show(IReadOnlyList<(Color32 Color, int Count)> used, IReadOnlyList<string> excluded, IReadOnlyList<string> pinned)
        {
            _label.Text = UiStrings.FormatPixelate_Pal_Info(count: used.Count);
            EndHighlight();
            _swatches.Children.Clear();
            foreach (var (color, n) in used)
            {
                bool pin = PaletteEdits.MatchesAny(pinned, color);
                Add(color, !pin && PaletteEdits.MatchesAny(excluded, color), pin, false,
                    pin ? UiStrings.FormatPixelate_ColorTip_Pinned(hex: color.ToHex(), count: n) : UiStrings.FormatPixelate_ColorTip(hex: color.ToHex(), count: n));
            }
            foreach (var pin in PaletteEdits.Colors(pinned))
                if (!used.Any(u => PaletteEdits.Matches(u.Color, pin)))
                    Add(pin, false, true, true, UiStrings.FormatPixelate_ColorTip_PinnedUnused(hex: pin.ToHex()));
            foreach (var gone in PaletteEdits.Colors(excluded))
                if (!used.Any(u => PaletteEdits.Matches(u.Color, gone)) && !PaletteEdits.MatchesAny(pinned, gone))
                    Add(gone, true, false, true, UiStrings.FormatPixelate_ColorTip_Excluded(hex: gone.ToHex()));
            _swatches.Children.Add(_add);
            _excludeText.Text = UiStrings.FormatPixelate_Exclude_Notice(count: excluded.Count);
            _notice.IsVisible = excluded.Count > 0;
            _pinText.Text = UiStrings.FormatPixelate_Pin_Notice(count: pinned.Count);
            _pinNotice.IsVisible = pinned.Count > 0;
        }

        private ColorSwatch? _highlighted;
        private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly Stopwatch _pulseClock = new();
        /// <summary>강조 테두리를 두는 시간과 크기 맥박(커졌다 돌아옴) 시간.</summary>
        private const double HighlightSeconds = 2.4, PulseSeconds = 0.5;

        /// <summary>
        /// 결과에서 누른 픽셀의 색 칸 강조: 굵은 강조색 테두리(<see cref="HighlightSeconds"/>초) + 잠깐 커졌다 돌아오는 맥박, 목록이 넘치면 그 칸이 보이게 굴림.
        /// 같은 색 칸이 없으면(뺀 색 등) 아무것도 하지 않는다.
        /// </summary>
        public void Highlight(Color32 color)
        {
            EndHighlight();
            var swatch = _swatches.Children.OfType<ColorSwatch>().FirstOrDefault(s => s.Value.WithAlpha(255) == color.WithAlpha(255));
            if (swatch is null) return;
            _highlighted = swatch;
            swatch.Highlighted = true;
            swatch.ZIndex = 1;
            swatch.RenderTransform = new ScaleTransform(1, 1);
            swatch.BringIntoView();
            _pulseClock.Restart();
            _pulse.Tick -= OnPulse;
            _pulse.Tick += OnPulse;
            _pulse.Start();
        }

        private void OnPulse(object? sender, EventArgs e)
        {
            double t = _pulseClock.Elapsed.TotalSeconds;
            if (_highlighted is not { } s || t >= HighlightSeconds) { EndHighlight(); return; }
            double k = t < PulseSeconds ? 1 + 0.6 * Math.Sin(Math.PI * t / PulseSeconds) : 1;
            if (s.RenderTransform is ScaleTransform st && st.ScaleX != k) { st.ScaleX = k; st.ScaleY = k; }
        }

        private void EndHighlight()
        {
            _pulse.Stop();
            if (_highlighted is not { } s) return;
            s.Highlighted = false;
            s.ZIndex = 0;
            s.RenderTransform = null;
            _highlighted = null;
        }

        private void Add(Color32 color, bool excluded, bool pinned, bool absent, string tip)
        {
            var swatch = new ColorSwatch(color, excluded, pinned)
            {
                Margin = new Thickness(0, 0, 1, 1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Opacity = absent ? AbsentOpacity : 1,
            };
            ToolTip.SetTip(swatch, tip + "\n" + (pinned ? UiStrings.Pixelate_ColorClick_Pinned : UiStrings.Pixelate_ColorClick));
            swatch.PointerPressed += (_, e) =>
            {
                var props = e.GetCurrentPoint(swatch).Properties;
                if (props.IsLeftButtonPressed) _owner.ShowColorUse(color);
                else if (props.IsRightButtonPressed) _owner.ToggleColor(color);
                else return;
                e.Handled = true;
            };
            _swatches.Children.Add(swatch);
        }
    }

    /// <summary>
    /// 크게 보기 겹판: 대화상자 전체를 덮는 어두운 바탕 위의 판 — 위 줄(제목, 이전·재생/멈춤·다음·격자 보기, 도움말, 닫기 ✕), 가운데 <see cref="ZoomPairView"/>(결과 | 원본),
    /// 아래 색 칸 묶음(<see cref="ColorsBlock"/>)과 프레임 띠(프레임이 하나면 숨김).
    /// 프레임 번호·재생·FPS·격자 보기·뺀 색은 큰 미리보기(<see cref="FramePreview"/>)와 같이 쓰고, 고른 후보(색 빼기 포함)가 바뀌면 함께 바뀐다.
    /// 판 밖(어두운 바탕)을 누르거나 Esc·닫기 단추로 닫고, Space = 재생/멈춤, ←→ = 이전·다음 프레임.
    /// </summary>
    private sealed class ZoomOverlay : Grid
    {
        private readonly ZoomPairView _view;
        private readonly Action _close;

        public ZoomOverlay(FramePreview preview, Action togglePlay, Action<int> step, Action close, Control colors, Control strip)
        {
            _close = close;
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
            IsVisible = false;
            Focusable = true;
            _view = new ZoomPairView(preview);
            PlayButton = Ui.IconButton(VectorIcon.Pause, UiStrings.Pixelate_Play, (_, _) => togglePlay());
            var prev = Ui.IconButton(VectorIcon.ChevronLeft, UiStrings.Pixelate_PrevFrame, (_, _) => step(-1));
            var next = Ui.IconButton(VectorIcon.ChevronRight, UiStrings.Pixelate_NextFrame, (_, _) => step(1));
            var gridIcon = Ui.Icon(VectorIcon.Grid3x3, 14);
            gridIcon.Margin = new Thickness(0, 1);
            GridButton = new ToggleButton
            {
                Content = gridIcon,
                Margin = new Thickness(2),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            }.Tip(UiStrings.Pixelate_ShowGrid);
            GridButton.IsCheckedChanged += (_, _) => _view.ShowGrid = GridButton.On();
            var closeButton = Ui.IconButton(VectorIcon.X, UiStrings.Pixelate_Zoom_Close, (_, _) => close());
            var title = new TextBlock { Text = UiStrings.Pixelate_Zoom_Title, Foreground = AppTheme.TextBrush, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            var hint = new TextBlock
            {
                Text = UiStrings.Pixelate_Zoom_Hint,
                Foreground = AppTheme.DimTextBrush,
                FontSize = AppTheme.SmallFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0),
            };
            var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(closeButton, Dock.Right);
            var controls = Ui.Row(title, prev, PlayButton, next, GridButton);
            DockPanel.SetDock(controls, Dock.Left);
            bar.Children.Add(closeButton);
            bar.Children.Add(controls);
            bar.Children.Add(hint);
            // 아래 = 색 칸 묶음 + 프레임 띠(큰 미리보기와 같은 상태), 남는 자리 = 두 칸 그림.
            var content = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(strip, Dock.Bottom);
            DockPanel.SetDock(colors, Dock.Bottom);
            content.Children.Add(bar);
            content.Children.Add(strip);
            content.Children.Add(colors);
            content.Children.Add(_view);
            Children.Add(new Border
            {
                Child = content,
                Background = AppTheme.PanelBrush,
                BorderBrush = AppTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                Margin = new Thickness(28),
            });
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) close();
                else if (e.Key == Key.Space) togglePlay();
                else if (e.Key == Key.Left) step(-1);
                else if (e.Key == Key.Right) step(1);
                else return;
                e.Handled = true;
            };
        }

        /// <summary>두 칸 그림(스포이드 — 오른쪽 원본 칸에서 칸 고르기).</summary>
        public ZoomPairView View => _view;

        /// <summary>재생/멈춤 단추(아이콘은 대화상자가 큰 미리보기 단추와 함께 맞춤).</summary>
        public Button PlayButton { get; }

        /// <summary>격자 보기 단추(눌림 = 원본 칸 경계 + 결과 픽셀 격자 — 대화상자가 "격자 보기" 체크와 서로 맞춤).</summary>
        public ToggleButton GridButton { get; }

        /// <summary>보이고 결과를 가장 큰 정수 배율로 맞춘 뒤 키보드 초점을 받는다.</summary>
        public void Open()
        {
            IsVisible = true;
            _view.Refit();
            Focus();
        }

        /// <summary>판 밖(어두운 바탕)을 누르면 닫는다.</summary>
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!ReferenceEquals(e.Source, this)) return;
            e.Handled = true;
            _close();
        }
    }

    /// <summary>
    /// 크게 보기의 두 칸: 왼쪽 = 지금 프레임 결과(처음엔 칸에 들어가는 가장 큰 정수 배율, 체커), 오른쪽 = 같은 프레임의 원본(배경 지운 모습)을 결과와 같은 화면 크기로
    /// — 결과 픽셀 하나 자리에 그 칸의 원본이 온다. 격자 보기면 결과엔 픽셀마다 한 칸인 격자, 원본엔 실제 칸 경계. 색 칸을 좌클릭하면 결과 칸에도 그 색이 쓰인 곳을 강조.
    /// 결과 픽셀 좌클릭 = 쓰인 곳 보기(<see cref="ResultPixelClicked"/>), 우클릭 = 그 색 빼기/되살리기(<see cref="ResultPixelRightClicked"/>), 원본 칸 좌클릭 = <see cref="SourceCellClicked"/>.
    /// 휠 = 포인터 자리를 축으로 확대·축소(1배 이상은 정수 배율), 끌기 = 이동, 두 번 누르기 = 다시 맞춤 — 두 칸이 함께 움직인다.
    /// 스포이드 중(<see cref="Picking"/>)이면 오른쪽 원본 칸이 스포이드 커서·올린 칸 강조, 왼쪽 클릭 = 그 칸 고르기(<see cref="CellPicked"/>), 오른쪽 클릭 = 그만.
    /// </summary>
    private sealed class ZoomPairView : Control
    {
        private const double Gap = 8, CaptionHeight = 20;
        private readonly FramePreview _preview;
        private double _zoom = 1;
        private Vector _offset;
        private bool _fitted;
        private PixelSize _fittedSize;
        private Avalonia.Point? _drag;

        public ZoomPairView(FramePreview preview)
        {
            _preview = preview;
            ClipToBounds = true;
            Focusable = true;
            preview.IndexChanged += _ => InvalidateVisual();
            preview.FlashChanged += InvalidateVisual;
        }

        public bool ShowGrid { get => field; set { field = value; InvalidateVisual(); } }

        /// <summary>스포이드 중 원본 둘레 강조 밝기(0~1, null = 그리지 않음).</summary>
        public double? PickPulse { get => field; set { field = value; InvalidateVisual(); } }

        /// <summary>왼쪽 결과 칸의 픽셀을 끌지 않고 좌클릭(프레임, 결과 x, y).</summary>
        public event Action<int, int, int>? ResultPixelClicked;
        /// <summary>왼쪽 결과 칸의 픽셀을 끌지 않고 우클릭(프레임, 결과 x, y — 스포이드 중에는 <see cref="PickCancelled"/>).</summary>
        public event Action<int, int, int>? ResultPixelRightClicked;
        /// <summary>오른쪽 원본 칸의 칸을 끌지 않고 좌클릭(스포이드가 아닐 때 — 스포이드 중에는 <see cref="CellPicked"/>). 원본 칸 우클릭은 아무것도 하지 않는다.</summary>
        public event Action<CellHover>? SourceCellClicked;
        private Avalonia.Point? _click;
        /// <summary>누른 단추가 오른쪽인지(<see cref="_click"/>).</summary>
        private bool _clickRight;

        /// <summary>스포이드 중 오른쪽 원본 칸에서 칸을 왼쪽 클릭(두 번째 값 = Shift — 스포이드를 계속).</summary>
        public event Action<CellHover, bool>? CellPicked;
        /// <summary>스포이드 중 오른쪽 클릭.</summary>
        public event Action? PickCancelled;
        private CellHover? _pickHover;

        public bool Picking
        {
            get => field;
            set
            {
                field = value;
                if (!value) { _pickHover = null; Cursor = null; }
                InvalidateVisual();
            }
        }

        /// <summary>오른쪽 원본 칸의 배치: 화면 사각형(결과와 같은 자리)·원본 시작점·배율(화면 px / 원본 px). 그림이 없으면 null.</summary>
        private (Rect Dest, double Sx0, double Sy0, double Kx, double Ky)? SourceMapping()
        {
            var bmp = _preview.FrameBitmap;
            var map = _preview.Map;
            if (bmp is null || map is null) return null;
            var size = bmp.PixelSize;
            var left = LeftPane;
            var rdest = new Rect(RightPane.X + _offset.X, left.Y + _offset.Y, size.Width * _zoom, size.Height * _zoom);
            // 원본에서 결과 캔버스가 덮는 자리: 칸 (0,0) = 캔버스 (OffsetX, OffsetY), 칸 크기 = 평균 칸 크기(경계가 흔들리면 조금 어긋남).
            // 결과가 칸 대응과 다른 캔버스면(프레임 크기가 다름) 원본 범위를 같은 자리에 비율대로 맞춘다.
            double cw, ch, sx0, sy0;
            if (_preview.SameCanvas && map.XCuts.Length > 1 && map.YCuts.Length > 1)
            {
                cw = (map.XCuts[^1] - map.XCuts[0]) / (double)(map.XCuts.Length - 1);
                ch = (map.YCuts[^1] - map.YCuts[0]) / (double)(map.YCuts.Length - 1);
                sx0 = map.XCuts[0] - map.OffsetX * cw;
                sy0 = map.YCuts[0] - map.OffsetY * ch;
            }
            else
            {
                var b = map.SourceBounds;
                double k = Math.Max(b.Width / (double)size.Width, b.Height / (double)size.Height);
                cw = ch = Math.Max(k, 1e-6);
                sx0 = b.X - (size.Width * k - b.Width) / 2;
                sy0 = b.Y - (size.Height * k - b.Height) / 2;
            }
            return (rdest, sx0, sy0, rdest.Width / (size.Width * cw), rdest.Height / (size.Height * ch));
        }

        /// <summary>화면 점이 가리키는 지금 프레임의 원본 칸(오른쪽 칸 밖이거나 칸 경계 밖이면 null).</summary>
        private CellHover? SourceCellAt(Avalonia.Point p)
        {
            if (!RightPane.Contains(p) || SourceMapping() is not { } m || _preview.Map is not { } map) return null;
            return CutOverlay.HitFrame(map, _preview.Index, m.Sx0 + (p.X - m.Dest.X) / m.Kx, m.Sy0 + (p.Y - m.Dest.Y) / m.Ky);
        }

        public void Refit()
        {
            _fitted = false;
            InvalidateVisual();
        }

        private Rect LeftPane => new(0, CaptionHeight, Math.Max(0, (Bounds.Width - Gap) / 2), Math.Max(0, Bounds.Height - CaptionHeight));
        private Rect RightPane => new(LeftPane.Right + Gap, CaptionHeight, LeftPane.Width, LeftPane.Height);

        private void Fit(PixelSize size)
        {
            var pane = LeftPane;
            if (size.Width <= 0 || size.Height <= 0 || pane.Width <= 0 || pane.Height <= 0) return;
            double fit = Math.Min((pane.Width - 8) / size.Width, (pane.Height - 8) / size.Height);
            _zoom = fit >= 1 ? Math.Floor(fit) : Math.Max(0.05, fit);
            _offset = new Vector((pane.Width - size.Width * _zoom) / 2, (pane.Height - size.Height * _zoom) / 2);
            _fitted = true;
            _fittedSize = size;
        }

        /// <summary>포인터가 있는 칸 안 좌표(두 칸이 같은 배치).</summary>
        private Avalonia.Point PaneLocal(Avalonia.Point p)
        {
            var pane = p.X >= RightPane.X ? RightPane : LeftPane;
            return new Avalonia.Point(p.X - pane.X, p.Y - pane.Y);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            var p = PaneLocal(e.GetPosition(this));
            bool up = e.Delta.Y > 0;
            // 1배 이상은 정수 배율(결과 픽셀이 고르게), 1배 아래는 0.8배씩.
            double z = up
                ? (_zoom >= 1 ? Math.Min(128, _zoom + Math.Max(1, Math.Floor(_zoom / 4))) : Math.Min(1, _zoom * 1.25))
                : (_zoom > 1 ? Math.Max(1, _zoom - Math.Max(1, Math.Floor(_zoom / 5))) : Math.Max(0.05, _zoom * 0.8));
            _offset = new Vector(p.X - (p.X - _offset.X) * z / _zoom, p.Y - (p.Y - _offset.Y) * z / _zoom);
            _zoom = z;
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            e.Handled = true;
            var point = e.GetCurrentPoint(this);
            if (Picking && point.Properties.IsRightButtonPressed) { PickCancelled?.Invoke(); return; }
            if (Picking && point.Properties.IsLeftButtonPressed && RightPane.Contains(point.Position))
            {
                if (SourceCellAt(point.Position) is { } h) CellPicked?.Invoke(h, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                return;
            }
            _drag = e.GetPosition(this);
            bool left = point.Properties.IsLeftButtonPressed, right = point.Properties.IsRightButtonPressed;
            _clickRight = right;
            // 좌클릭은 결과 칸·원본 칸, 우클릭은 결과 칸에서만(원본 칸 우클릭은 아무것도 하지 않음).
            _click = e.ClickCount == 1 && ((left && (LeftPane.Contains(point.Position) || RightPane.Contains(point.Position))) || (right && LeftPane.Contains(point.Position)))
                ? point.Position : null;
            e.Pointer.Capture(this);
            if (e.ClickCount == 2) Refit();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var p = e.GetPosition(this);
            if (Picking && _drag is null)
            {
                Cursor = RightPane.Contains(p) ? CutOverlay.PickerCursor : null;
                var h = SourceCellAt(p);
                if (h != _pickHover) { _pickHover = h; InvalidateVisual(); }
            }
            if (_drag is not { } start) return;
            _offset += p - start;
            _drag = p;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_pickHover is null) return;
            _pickHover = null;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            _drag = null;
            e.Pointer.Capture(null);
            var p = e.GetPosition(this);
            if (_click is { } start && Math.Abs(p.X - start.X) <= 4 && Math.Abs(p.Y - start.Y) <= 4 && _preview.FrameBitmap is { } bmp)
            {
                var left = LeftPane;
                if (left.Contains(start))
                {
                    int x = (int)Math.Floor((start.X - left.X - _offset.X) / _zoom), y = (int)Math.Floor((start.Y - left.Y - _offset.Y) / _zoom);
                    if (x >= 0 && y >= 0 && x < bmp.PixelSize.Width && y < bmp.PixelSize.Height)
                        (_clickRight ? ResultPixelRightClicked : ResultPixelClicked)?.Invoke(_preview.Index, x, y);
                }
                else if (!_clickRight && SourceCellAt(start) is { } h) SourceCellClicked?.Invoke(h);
            }
            _click = null;
        }

        public override void Render(DrawingContext g)
        {
            var bounds = new Rect(Bounds.Size);
            g.FillRectangle(AppTheme.BackBrush, bounds);
            var bmp = _preview.FrameBitmap;
            if (bmp is null) return;
            var size = bmp.PixelSize;
            if (!_fitted || size != _fittedSize) Fit(size);
            var left = LeftPane;
            var right = RightPane;
            string zoom = _zoom >= 1 ? "×" + _zoom.ToString("0", CultureInfo.CurrentCulture) : "×" + _zoom.ToString("0.##", CultureInfo.CurrentCulture);
            AppTheme.DrawTextIn(g, UiStrings.FormatPixelate_Preview_Result(index: _preview.Index + 1, count: _preview.FrameCount)
                + " · " + ZoomImageView.SizeText(size.Width, size.Height) + "  " + zoom,
                new Rect(4, 0, left.Width - 8, 18), AppTheme.TextBrush, AppTheme.SmallFontSize, bold: true);
            var sourceLabel = _preview.Map is { } labelMap
                ? UiStrings.Pixelate_Preview_Source + " · " + ZoomImageView.SizeText(labelMap.SourceBounds.Width, labelMap.SourceBounds.Height)
                : UiStrings.Pixelate_Preview_Source;
            AppTheme.DrawTextIn(g, sourceLabel, new Rect(right.X + 4, 0, right.Width - 8, 18), AppTheme.TextBrush, AppTheme.SmallFontSize, bold: true);

            var dest = new Rect(left.X + _offset.X, left.Y + _offset.Y, size.Width * _zoom, size.Height * _zoom);
            using (g.PushClip(left))
            {
                AppTheme.DrawChecker(g, dest.Intersect(left), 8);
                using (ImageUtil.Pixelated(g))
                    g.DrawImage(bmp, new Rect(0, 0, size.Width, size.Height), dest);
                if (ShowGrid) CutOverlay.DrawPixels(g, dest, size.Width, size.Height, left);
                if (_preview.FlashMask is { } mask && _preview.FrameImage is { } image)
                    FramePreview.DrawColorRegions(g, image, mask, dest, _zoom);
                if (_preview.Marked is { } mk && mk.Frame == _preview.Index)
                    CutOverlay.DrawMark(g, new Rect(dest.X + mk.X * _zoom, dest.Y + mk.Y * _zoom, _zoom, _zoom));
            }

            var src = _preview.SourceBitmap;
            var map = _preview.Map;
            if (src is null || map is null || SourceMapping() is not { } sm) return;
            var (rdest, sx0, sy0, kx, ky) = sm;
            using (g.PushClip(right))
            {
                double ScreenX(double x) => rdest.X + (x - sx0) * kx;
                double ScreenY(double y) => rdest.Y + (y - sy0) * ky;
                // 원본 그림 밖은 잘라서 그린다(원본 사각형이 그림을 넘으면 늘어나 그려지지 않게).
                var full = new Rect(0, 0, src.PixelSize.Width, src.PixelSize.Height);
                var srcRect = new Rect(sx0, sy0, rdest.Width / kx, rdest.Height / ky).Intersect(full);
                AppTheme.DrawChecker(g, rdest.Intersect(right), 8);
                if (srcRect.Width > 0 && srcRect.Height > 0)
                {
                    var to = new Rect(ScreenX(srcRect.X), ScreenY(srcRect.Y), srcRect.Width * kx, srcRect.Height * ky);
                    using (ImageUtil.Pixelated(g))
                        g.DrawImage(src, srcRect, to);
                }
                if (ShowGrid) CutOverlay.Draw(g, map, ScreenX, ScreenY, kx, right);
                if (_preview.Marked is { } marked && marked.Frame == _preview.Index)
                {
                    int column = marked.X - map.OffsetX, row = marked.Y - map.OffsetY;
                    var layout = map.Layout();
                    if (column >= 0 && column < layout.Columns && row >= 0 && row < layout.Rows)
                    {
                        var (x0, y0, x1, y1) = layout.Cell(column, row);
                        CutOverlay.DrawMark(g, new Rect(ScreenX(x0), ScreenY(y0), (x1 - x0) * kx, (y1 - y0) * ky));
                    }
                }
                if (Picking && _pickHover is { } h && h.Frame == _preview.Index)
                {
                    var (x0, y0, x1, y1) = CutOverlay.CellRect(map, h.Column, h.Row);
                    g.DrawRectangle(null, CutOverlay.Highlight, new Rect(ScreenX(x0), ScreenY(y0), (x1 - x0) * kx, (y1 - y0) * ky));
                }
                if (PickPulse is { } level)
                {
                    var shown = rdest.Intersect(new Rect(ScreenX(0), ScreenY(0), src.PixelSize.Width * kx, src.PixelSize.Height * ky)).Intersect(right);
                    CutOverlay.DrawPickPulse(g, shown, level);
                }
            }
        }
    }

    /// <summary>
    /// 고른 후보 크게 보기: 왼쪽은 지금 프레임(정수 배율, 체커), 오른쪽은 같은 프레임의 원본(배경 지운 모습, 칸 선은 왼쪽 큰 원본 보기에만).
    /// 원본 보기에서 칸에 마우스를 올리면 그 프레임에 멈추고 결과의 같은 픽셀을 강조한다. 프레임은 <see cref="Index"/>로 바꾼다.
    /// 누르면 키보드 초점(Space·←→)을 받고, 결과·원본 그림을 끌지 않고 좌클릭하면 <see cref="OpenRequested"/>(크게 보기 — <see cref="ZoomOverlay"/>),
    /// 결과 그림을 우클릭하면 <see cref="ResultPixelRightClicked"/>(그 픽셀 색 빼기/되살리기).
    /// 스포이드 중(<see cref="Picking"/>)이면 원본 그림이 스포이드 커서·올린 칸 강조(재생 멈춤), 왼쪽 클릭 = 그 칸 고르기(<see cref="CellPicked"/>), 오른쪽 클릭 = 그만.
    /// </summary>
    private sealed class FramePreview : Control
    {
        private List<WriteableBitmap> _frames = [];
        private WriteableBitmap? _source;
        private PixelImage? _sourceImage;
        private IReadOnlyList<PixelateFrameMap> _maps = [];
        private bool _sameCanvas;
        private int _index;
        private CellHover? _hover;

        public int FrameCount => _frames.Count;

        /// <summary>보이는 프레임이 바뀜(재생·띠·키·마우스 올림).</summary>
        public event Action<int>? IndexChanged;

        public FramePreview()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        public int Index
        {
            get => _index;
            set
            {
                int v = _frames.Count == 0 ? 0 : ((value % _frames.Count) + _frames.Count) % _frames.Count;
                if (v == _index) return;
                _index = v;
                InvalidateVisual();
                IndexChanged?.Invoke(v);
            }
        }

        /// <param name="excluded">색 빼기 전 색(<see cref="PixelateResult.CommonExcludedFrames"/>, 프레임과 같은 자리 — 없으면 빈 목록).</param>
        /// <param name="sameCanvas">결과 프레임이 칸 대응(<see cref="PixelateFrameMap"/>)과 같은 캔버스인지(아니면 결과 쪽 강조는 하지 않음).</param>
        public void SetFrames(IReadOnlyList<PixelImage> frames, IReadOnlyList<PixelImage> excluded, PixelImage? source, IReadOnlyList<PixelateFrameMap> maps, bool sameCanvas)
        {
            foreach (var b in _frames) b.Dispose();
            _frames = frames.Select(ImageUtil.ToBitmap).ToList();
            _images = [.. frames];
            _excludedImages = excluded;
            _flashMasks = null;
            if (!ReferenceEquals(source, _sourceImage))
            {
                _source?.Dispose();
                _source = source is null ? null : ImageUtil.ToBitmap(source);
                _sourceImage = source;
            }
            _maps = maps;
            _sameCanvas = sameCanvas;
            _hover = null;
            Marked = null;
            _index = Math.Min(_index, Math.Max(0, _frames.Count - 1));
            InvalidateVisual();
            IndexChanged?.Invoke(_index);
        }

        public void SetHover(CellHover? h)
        {
            _hover = h;
            if (h is { } v && v.Frame < _frames.Count) Index = v.Frame;
            InvalidateVisual();
        }

        /// <summary>강조할 자리(프레임마다 픽셀 순서) — 없으면 강조하지 않음.</summary>
        private bool[][]? _flashMasks;
        private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
        private List<PixelImage> _images = [];
        private IReadOnlyList<PixelImage> _excludedImages = [];

        /// <summary>
        /// 결과에서 이 색을 쓴 픽셀과, 색 빼기 전에 이 색이었던(뺀 색과 맞는) 픽셀을 잠깐(1.2초) 강조한다 —
        /// 뺀 색은 결과에 없으므로 그 색이 있던 자리(지금은 다른 색)가 강조된다.
        /// </summary>
        public void Flash(Color32 color)
        {
            var was = new Dictionary<uint, bool>();
            _flashMasks = new bool[_images.Count][];
            for (int f = 0; f < _images.Count; f++)
            {
                var px = _images[f].Pixels;
                var ex = f < _excludedImages.Count ? _excludedImages[f].Pixels : null;
                var mask = new bool[px.Length];
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i] == color) mask[i] = true;
                    else if (ex is not null && ex[i].A != 0)
                    {
                        if (!was.TryGetValue(ex[i].Argb, out bool m)) was[ex[i].Argb] = m = PaletteEdits.Matches(ex[i], color);
                        mask[i] = m;
                    }
                }
                _flashMasks[f] = mask;
            }
            _flashTimer.Stop();
            _flashTimer.Tick -= EndFlash;
            _flashTimer.Tick += EndFlash;
            _flashTimer.Start();
            InvalidateVisual();
            FlashChanged?.Invoke();
        }

        private void EndFlash(object? sender, EventArgs e)
        {
            _flashTimer.Stop();
            _flashMasks = null;
            Marked = null;
            InvalidateVisual();
            FlashChanged?.Invoke();
        }

        /// <summary>결과에서 누른 픽셀(프레임, x, y) — 색 강조(<see cref="Flash"/>)가 끝나면 지워진다. 크게 보기도 같은 표시를 그린다.</summary>
        public (int Frame, int X, int Y)? Marked { get; private set; }

        /// <summary>누른 결과 픽셀 표시(<see cref="Flash"/> 다음에 — 같은 1.2초 뒤 함께 지워짐).</summary>
        public void Mark(int frame, int x, int y)
        {
            Marked = (frame, x, y);
            InvalidateVisual();
            FlashChanged?.Invoke();
        }

        /// <summary>스포이드 중 원본 둘레 강조 밝기(0~1, null = 그리지 않음).</summary>
        public double? PickPulse { get => field; set { field = value; InvalidateVisual(); } }

        /// <summary>재생 한 걸음(원본 칸에 마우스를 올린 동안은 멈춤).</summary>
        public void Step()
        {
            if (_frames.Count == 0 || _hover is not null || _pickHover is not null) return;
            Index = _index + 1;
        }

        /// <summary>보이는 프레임에서 강조 중인 자리(<see cref="Flash"/>, 없으면 null)와 그 프레임의 픽셀 — 크게 보기도 같은 강조를 그린다.</summary>
        public bool[]? FlashMask => _flashMasks is { } m && _index < m.Length ? m[_index] : null;
        public PixelImage? FrameImage => _index < _images.Count ? _images[_index] : null;
        /// <summary>색 강조가 시작되거나 끝남.</summary>
        public event Action? FlashChanged;

        /// <summary>보이는 프레임 그림(없으면 null) — 크게 보기가 같은 그림을 그린다.</summary>
        public WriteableBitmap? FrameBitmap => _frames.Count == 0 ? null : _frames[_index];
        /// <summary>배경 지운 원본 그림.</summary>
        public WriteableBitmap? SourceBitmap => _source;
        /// <summary>보이는 프레임의 칸 대응(없으면 null).</summary>
        public PixelateFrameMap? Map => _index < _maps.Count ? _maps[_index] : null;
        /// <summary>결과 프레임이 칸 대응과 같은 캔버스인지.</summary>
        public bool SameCanvas => _sameCanvas;

        /// <summary>결과·원본 그림을 (끌지 않고) 좌클릭 — 크게 보기를 연다(쓰인 곳 보기·원본 칸 대응은 크게 보기에서).</summary>
        public event Action? OpenRequested;
        /// <summary>결과 그림의 픽셀을 (끌지 않고) 우클릭(프레임, 결과 x, y — 스포이드 중에는 <see cref="PickCancelled"/>).</summary>
        public event Action<int, int, int>? ResultPixelRightClicked;
        /// <summary>스포이드 중 원본 그림의 칸을 왼쪽 클릭(두 번째 값 = Shift — 스포이드를 계속).</summary>
        public event Action<CellHover, bool>? CellPicked;
        /// <summary>스포이드 중 오른쪽 클릭.</summary>
        public event Action? PickCancelled;

        private Rect _resultRect, _sourceRect;
        private Avalonia.Point? _press, _pressRight;
        /// <summary>스포이드 중 원본 그림에서 마우스를 올린 칸.</summary>
        private CellHover? _pickHover;

        public bool Picking
        {
            get => field;
            set
            {
                field = value;
                if (!value) _pickHover = null;
                InvalidateVisual();
            }
        }

        private bool OnImage(Avalonia.Point p) => _resultRect.Contains(p) || _sourceRect.Contains(p);

        /// <summary>원본 그림 위 화면 점이 가리키는 지금 프레임의 칸(원본 그림 밖·칸 경계 밖이면 null).</summary>
        private CellHover? SourceCellAt(Avalonia.Point p)
        {
            if (!_sourceRect.Contains(p) || _index >= _maps.Count) return null;
            var m = _maps[_index];
            var b = m.SourceBounds;
            if (b.Width <= 0) return null;
            double k = _sourceRect.Width / b.Width;
            return CutOverlay.HitFrame(m, _index, b.X + (p.X - _sourceRect.X) / k, b.Y + (p.Y - _sourceRect.Y) / k);
        }

        /// <summary>
        /// 누르면 늘 키보드 초점(Space·←→)을 받는다. 결과나 원본 그림을 좌클릭하고 제자리에서 떼면 크게 보기(스포이드 중 원본 칸 누르기는 색 추가),
        /// 결과 그림을 우클릭하고 제자리에서 떼면 그 픽셀 색 빼기/되살리기(<see cref="ResultPixelRightClicked"/>).
        /// </summary>
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            var point = e.GetCurrentPoint(this);
            var p = point.Position;
            if (Picking && (point.Properties.IsRightButtonPressed || (point.Properties.IsLeftButtonPressed && _sourceRect.Contains(p))))
            {
                e.Handled = true;
                _press = _pressRight = null;
                if (point.Properties.IsRightButtonPressed) PickCancelled?.Invoke();
                else if (SourceCellAt(p) is { } h) CellPicked?.Invoke(h, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                return;
            }
            _press = point.Properties.IsLeftButtonPressed && OnImage(p) ? p : null;
            _pressRight = point.Properties.IsRightButtonPressed && _resultRect.Contains(p) ? p : null;
            base.OnPointerPressed(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var p = e.GetPosition(this);
            if (_press is { } start && Math.Abs(p.X - start.X) <= 4 && Math.Abs(p.Y - start.Y) <= 4 && _frames.Count > 0) OpenRequested?.Invoke();
            if (_pressRight is { } r && Math.Abs(p.X - r.X) <= 4 && Math.Abs(p.Y - r.Y) <= 4 && FrameBitmap is { } bmp && _resultRect.Width > 0 && _resultRect.Height > 0)
            {
                var size = bmp.PixelSize;
                int x = (int)Math.Floor((r.X - _resultRect.X) * size.Width / _resultRect.Width), y = (int)Math.Floor((r.Y - _resultRect.Y) * size.Height / _resultRect.Height);
                if (x >= 0 && y >= 0 && x < size.Width && y < size.Height) ResultPixelRightClicked?.Invoke(_index, x, y);
            }
            _press = _pressRight = null;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            bool picker = Picking && _sourceRect.Contains(p);
            Cursor = picker ? CutOverlay.PickerCursor : OnImage(p) ? HandCursor : null;
            var h = picker ? SourceCellAt(p) : null;
            if (h == _pickHover) return;
            _pickHover = h;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_pickHover is null) return;
            _pickHover = null;
            InvalidateVisual();
        }

        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

        public override void Render(DrawingContext g)
        {
            var bounds = new Rect(Bounds.Size);
            g.FillRectangle(AppTheme.BackBrush, bounds);
            if (_frames.Count == 0) return;
            var bmp = _frames[_index];
            double half = Math.Floor(bounds.Width / 2);
            var size = bmp.PixelSize;
            var label = UiStrings.FormatPixelate_Preview_Result(index: _index + 1, count: _frames.Count) + " · " + ZoomImageView.SizeText(size.Width, size.Height);
            AppTheme.DrawTextIn(g, label, new Rect(4, 0, half - 8, 18), AppTheme.TextBrush, AppTheme.SmallFontSize, bold: true);
            var dest = ImageUtil.DrawFitted(g, bmp, new Rectangle(0, 0, size.Width, size.Height), new Rect(4, 20, half - 8, bounds.Height - 24), checker: true);
            _resultRect = dest;
            _sourceRect = default;
            if (FlashMask is { } mask && dest.Width > 0 && _index < _images.Count)
                DrawColorRegions(g, _images[_index], mask, dest, dest.Width / size.Width);
            var hover = (_pickHover ?? _hover) is { } h && h.Frame == _index && h.Frame < _maps.Count ? h : (CellHover?)null;
            if (hover is { } hv && _sameCanvas && dest.Width > 0)
            {
                var m = _maps[hv.Frame];
                double z = dest.Width / size.Width;
                g.DrawRectangle(null, CutOverlay.Highlight, new Rect(dest.X + (hv.Column + m.OffsetX) * z, dest.Y + (hv.Row + m.OffsetY) * z, z, z));
            }
            if (Marked is { } mk && mk.Frame == _index && dest.Width > 0)
            {
                double z = dest.Width / size.Width;
                CutOverlay.DrawMark(g, new Rect(dest.X + mk.X * z, dest.Y + mk.Y * z, z, z));
            }
            if (_source is null || _index >= _maps.Count) return;
            var sourceBounds = _maps[_index].SourceBounds;
            AppTheme.DrawTextIn(g, UiStrings.Pixelate_Preview_Source + " · " + ZoomImageView.SizeText(sourceBounds.Width, sourceBounds.Height),
                new Rect(half + 4, 0, bounds.Width - half - 8, 18), AppTheme.TextBrush, AppTheme.SmallFontSize, bold: true);
            _sourceRect = ImageUtil.DrawFitted(g, _source, _maps[_index].SourceBounds, new Rect(half + 4, 20, bounds.Width - half - 8, bounds.Height - 24), checker: true);
            if (_pickHover is { } ph && ph.Frame == _index && _sourceRect.Width > 0)
            {
                var b = _maps[_index].SourceBounds;
                double k = _sourceRect.Width / b.Width;
                var (x0, y0, x1, y1) = CutOverlay.CellRect(_maps[_index], ph.Column, ph.Row);
                g.DrawRectangle(null, CutOverlay.Highlight, new Rect(_sourceRect.X + (x0 - b.X) * k, _sourceRect.Y + (y0 - b.Y) * k, (x1 - x0) * k, (y1 - y0) * k));
            }
            if (PickPulse is { } level && _sourceRect.Width > 0) CutOverlay.DrawPickPulse(g, _sourceRect.Inflate(3), level);
        }

        /// <summary>
        /// 색 강조: 강조 자리(<paramref name="mask"/>, 픽셀 순서)가 아닌 픽셀은 어둡게 덮고, 강조 자리 덩어리(4방향으로 이어진 영역)는 바깥 경계(구멍 둘레 포함)만 그린다 —
        /// 강조 자리와 아닌 픽셀(또는 그림 밖) 사이의 변만 모아, 바깥 어두운 선(4px) 위에 노란 선(2px)으로.
        /// </summary>
        public static void DrawColorRegions(DrawingContext g, PixelImage img, bool[] mask, Rect dest, double z)
        {
            var dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
            int w = img.Width, h = img.Height;
            bool On(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x];
            var edges = new List<(Avalonia.Point A, Avalonia.Point B)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!mask[y * w + x])
                    {
                        if (img[x, y].A != 0) g.FillRectangle(dim, new Rect(dest.X + x * z, dest.Y + y * z, z, z));
                        continue;
                    }
                    double l = dest.X + x * z, t = dest.Y + y * z, r = l + z, b = t + z;
                    if (!On(x, y - 1)) edges.Add((new(l, t), new(r, t)));
                    if (!On(x, y + 1)) edges.Add((new(l, b), new(r, b)));
                    if (!On(x - 1, y)) edges.Add((new(l, t), new(l, b)));
                    if (!On(x + 1, y)) edges.Add((new(r, t), new(r, b)));
                }
            if (edges.Count == 0) return;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
                foreach (var (a, b) in edges)
                {
                    ctx.BeginFigure(a, false);
                    ctx.LineTo(b);
                    ctx.EndFigure(false);
                }
            g.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 4, lineCap: PenLineCap.Square), geometry);
            g.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 60)), 2, lineCap: PenLineCap.Square), geometry);
        }
    }

    /// <summary>
    /// 결과 프레임 띠: 썸네일(체커, 정수 배율)과 번호, 지금 프레임은 파란 테두리. 누르면 <see cref="Picked"/>.
    /// 썸네일은 대화상자가 만들어 두 띠(큰 미리보기·크게 보기)에 함께 넘기고 정리도 대화상자가 한다.
    /// </summary>
    private sealed class FrameStrip : Control
    {
        public const int Thumb = 56;
        private const int Gap = 4;
        private IReadOnlyList<WriteableBitmap> _thumbs = [];

        public FrameStrip()
        {
            Height = Thumb + 8;
            ClipToBounds = true;
        }

        public int Selected { get => field; set { field = value; InvalidateVisual(); } }

        public event Action<int>? Picked;

        /// <summary>프레임 썸네일(<see cref="Thumb"/> 크기, <see cref="ImageUtil.MakeThumbnail"/>) — 띠가 가지지 않는다.</summary>
        public void SetThumbs(IReadOnlyList<WriteableBitmap> thumbs)
        {
            _thumbs = thumbs;
            Width = Math.Max(1, _thumbs.Count * (Thumb + Gap) + Gap);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            int i = (int)((e.GetPosition(this).X - Gap / 2.0) / (Thumb + Gap));
            if (i >= 0 && i < _thumbs.Count) Picked?.Invoke(i);
        }

        public override void Render(DrawingContext g)
        {
            g.FillRectangle(AppTheme.BackBrush, new Rect(Bounds.Size));
            for (int i = 0; i < _thumbs.Count; i++)
            {
                var r = new Rect(Gap + i * (Thumb + Gap), 4, Thumb, Thumb);
                g.DrawImage(_thumbs[i], r);
                AppTheme.DrawTextIn(g, (i + 1).ToString(CultureInfo.CurrentCulture), new Rect(r.X + 3, r.Y + 1, Thumb - 6, 14), AppTheme.TextBrush, AppTheme.SmallFontSize, bold: true);
                g.DrawRectangle(null, i == Selected ? AppTheme.Pen(AppTheme.AccentColor, 2) : AppTheme.Pen(AppTheme.BorderColor), r.Inflate(i == Selected ? 1 : 0.5));
            }
        }
    }

    /// <summary>
    /// 최적화 진행 그래프: 시도마다 지금까지의 최고 점수를 꺾은선으로(오른쪽 아래에 최고 점수 — 끝난 뒤에도 남음). 새 찾기 돌림이 시작된 곳에 옅은 세로 선.
    /// </summary>
    private sealed class OptimizeGraph : Control
    {
        private static readonly IPen PassPen = AppTheme.Pen(Color.FromArgb(110, AppTheme.DimTextColor.R, AppTheme.DimTextColor.G, AppTheme.DimTextColor.B));
        private readonly List<double> _best = [];
        /// <summary>새 돌림의 첫 점 번호(<see cref="_best"/> 안 — 첫 돌림은 빼고).</summary>
        private readonly List<int> _passStarts = [];

        public double Best => _best.Count == 0 ? 0 : _best[^1];

        /// <summary>지금까지 본 가장 큰 돌림 번호.</summary>
        public int Pass { get; private set; } = 1;

        public bool HasData => _best.Count > 0;

        public void Clear()
        {
            _best.Clear();
            _passStarts.Clear();
            Pass = 1;
            InvalidateVisual();
        }

        public void Add(double score, int pass)
        {
            if (pass > Pass)
            {
                if (_best.Count > 0) _passStarts.Add(_best.Count);
                Pass = pass;
            }
            _best.Add(_best.Count == 0 ? score : Math.Max(_best[^1], score));
            InvalidateVisual();
        }

        public override void Render(DrawingContext g)
        {
            var bounds = new Rect(Bounds.Size);
            g.FillRectangle(AppTheme.BackBrush, bounds);
            g.DrawRectangle(null, AppTheme.Pen(AppTheme.BorderColor), bounds.Deflate(0.5));
            if (_best.Count == 0) return;
            double lo = _best[0], hi = _best[^1];
            var label = new Rect(bounds.Width - 40, bounds.Height - 16, 36, 14);
            if (_best.Count == 1)
            {
                AppTheme.DrawTextIn(g, hi.ToString("0.0", CultureInfo.CurrentCulture), label, AppTheme.TextBrush, AppTheme.SmallFontSize);
                return;
            }
            if (hi - lo < 0.5) lo = hi - 0.5;
            double w = bounds.Width - 6, h = bounds.Height - 6;
            foreach (int start in _passStarts)
            {
                double x = Math.Round(3 + w * start / (_best.Count - 1)) + 0.5;
                g.DrawLine(PassPen, new Avalonia.Point(x, 1), new Avalonia.Point(x, bounds.Height - 1));
            }
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < _best.Count; i++)
                {
                    var pt = new Avalonia.Point(3 + w * i / (_best.Count - 1), 3 + h * (1 - (_best[i] - lo) / (hi - lo)));
                    if (i == 0) ctx.BeginFigure(pt, false);
                    else ctx.LineTo(pt);
                }
                ctx.EndFigure(false);
            }
            g.DrawGeometry(null, AppTheme.Pen(AppTheme.AccentColor, 1.5), geo);
            AppTheme.DrawTextIn(g, hi.ToString("0.0", CultureInfo.CurrentCulture), label, AppTheme.TextBrush, AppTheme.SmallFontSize);
        }
    }

    /// <summary>
    /// 후보 칸: 첫 프레임 썸네일(체커, 정수 배율), 제목(보통 후보 = 순위·점수, 최적화 후보 = 프리셋 이름), 색 수·크기, 외곽선 맞추기 방식,
    /// 최적화 후보면 아래에 <paramref name="marker"/>("최적화(87.6)"·"시간초과(60.4)" 같은 멈춘 까닭과 점수). 풍선은 점수 항목별 내역. 누르면 고르고, 두 번 누르면 적용.
    /// </summary>
    private sealed class CandidateTile : Border
    {
        public CandidateTile(PixelateCandidate c, int rank, bool selected, string? presetName, string? marker)
        {
            Candidate = c;
            var r = c.Result;
            var thumb = new Image { Source = ImageUtil.MakeThumbnail(r.Frames[0], 112), Width = 112, Height = 112 };
            RenderOptions.SetBitmapInterpolationMode(thumb, BitmapInterpolationMode.None);
            var title = new TextBlock
            {
                Text = presetName ?? UiStrings.FormatPixelate_Tile(rank: rank, score: c.Score.Total.ToString("0.0", CultureInfo.CurrentCulture)),
                MaxWidth = 112,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = AppTheme.TextBrush,
                FontWeight = FontWeight.Bold,
            };
            var info = new TextBlock
            {
                Text = UiStrings.FormatPixelate_TileInfo(colors: r.Palette.Count, width: r.Size.Width, height: r.Size.Height, frames: r.Frames.Count),
                Foreground = AppTheme.DimTextBrush,
                FontSize = AppTheme.SmallFontSize,
            };
            var fit = new TextBlock { Text = FitName(c.Settings.Cleanup.OutlineFit), Foreground = AppTheme.DimTextBrush, FontSize = AppTheme.SmallFontSize };
            var lines = new StackPanel { Spacing = 2, Children = { thumb, title, info, fit } };
            if (marker is not null)
                lines.Children.Add(new TextBlock { Text = marker, Foreground = AppTheme.AccentBrush, FontSize = AppTheme.SmallFontSize, FontWeight = FontWeight.Bold });
            Child = lines;
            Padding = new Thickness(4);
            Margin = new Thickness(3);
            BorderThickness = new Thickness(2);
            CornerRadius = new CornerRadius(3);
            Background = AppTheme.PanelBrush;
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, string.Join("\n", c.Score.Parts.Select(p => UiStrings.FormatPixelate_Breakdown(
                name: TermName(p.Term), value: FormatValue(p), bad: Math.Round(p.Badness * 100), weight: p.Weight.ToString("0.#", CultureInfo.CurrentCulture)))));
            IsSelected = selected;
        }

        public PixelateCandidate Candidate { get; }

        public bool IsSelected
        {
            get => BorderBrush == AppTheme.AccentBrush;
            set => BorderBrush = value ? AppTheme.AccentBrush : AppTheme.BorderBrush;
        }
    }
}
