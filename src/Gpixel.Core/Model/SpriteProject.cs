using System.Drawing;
using Gpixel.Core.Edit;
using Gpixel.Core.Localization;

namespace Gpixel.Core.Model;

/// <summary>
/// Gpixel 프로젝트(<c>.gpixel</c>): 공통 캔버스 크기 × 레이어 × 프레임의 셀(cel) + 태그(애니메이션) +
/// Unity·내보내기 설정 + 실행취소 기록.
/// <para>
/// 레이어는 아래 → 위 평평한 목록이다. 그룹(합친 레이어)의 부위 레이어들은 그룹 바로 아래에 이어 붙어 있고(<c>g-n … g-1</c>),
/// 그룹·일반 레이어만 최상위이며 그룹은 겹치지 않는다. 부위는 자기 크기의 셀을 프레임마다 갖고, 모든 프레임에 공통인 위치에 합성된다.
/// </para>
/// <para>
/// 그룹은 스킨 슬롯이다: 스킨(<see cref="Skin"/>) 목록을 갖고 그중 하나(<see cref="Layer.ActiveSkin"/>)만 보인다. 부위 셀은 스킨마다 따로이고,
/// 첫 스킨이 아닌 스킨의 빈 셀(없거나 모두 투명)은 첫 스킨 셀을 물려받는다. 셀을 읽는 메서드(<see cref="GetCel(int, int)"/>·합성)는
/// 지금 스킨 기준(물려받기 포함), 그리는 메서드(스트로크·<see cref="SetCel"/>)는 지금 스킨 자신의 셀을 고친다.
/// </para>
/// <para>
/// 모든 구조 변경 메서드(레이어·프레임·태그·캔버스·설정)는 <see cref="History"/>에 기록되고
/// <see cref="Changed"/>를 발생시키며 수정됨 표시를 켠다. 그리기는 <see cref="BeginCelStroke"/> →
/// 픽셀 수정(필요하면 <see cref="NotifyPixelsChanged"/>로 다시 그리기 알림) → <see cref="CommitCelStroke"/>로
/// 스트로크 하나를 기록한다. 레이어·프레임·태그 객체의 속성을 직접 바꾸지 말고 Set… 메서드나
/// <see cref="Edit(string, ProjectChange, Action)"/> 안에서 바꿔야 실행취소된다.
/// </para>
/// </summary>
public sealed partial class SpriteProject
{
    public static string UntitledName => DomainStrings.Project_UntitledName;
    public const string FileExtension = ".gpixel";

    private readonly List<Layer> _layers = new();
    private readonly List<Frame> _frames = new();
    private readonly List<AnimationTag> _tags = new();
    private readonly List<Outfit> _outfits = new();
    /// <summary>
    /// 셀: (레이어, 프레임, 스킨) → 이미지. 일반 레이어는 캔버스 크기, 부위는 부위 크기, 그룹은 셀이 없다.
    /// 스킨 키는 부위의 첫 스킨이 아닌 스킨일 때만 쓰고, 일반 레이어·첫 스킨 셀은 null 키다(그룹을 잃은 부위·가져오기 중인 부위도 그대로 읽힘).
    /// </summary>
    private readonly Dictionary<CelKey, PixelImage> _cels = new();
    private int _width, _height;
    private string? _path;
    private bool _isDirty;
    private int _editDepth;
    private StrokeState? _stroke;

    /// <summary>레이어 1개 · 빈 프레임 1개짜리 새 프로젝트.</summary>
    public SpriteProject(int width, int height) : this(width, height, addDefaults: true)
    {
    }

    private SpriteProject(int width, int height, bool addDefaults)
    {
        ValidateSize(width, height);
        _width = width;
        _height = height;
        if (!addDefaults) return;
        _layers.Add(new Layer(NextLayerName()));
        _frames.Add(new Frame());
    }

    /// <summary>레이어·프레임이 하나도 없는 프로젝트(파일 읽기·가져오기용, 내부 전용).</summary>
    internal static SpriteProject CreateBlank(int width, int height) => new(width, height, addDefaults: false);

    /// <summary>이미지 한 장(레이어 1개 · 프레임 1개)으로 새 프로젝트를 만든다.</summary>
    public static SpriteProject FromImage(PixelImage image, string? layerName = null)
    {
        var p = CreateBlank(image.Width, image.Height);
        var layer = new Layer(layerName ?? p.NextLayerName());
        var frame = new Frame();
        p._layers.Add(layer);
        p._frames.Add(frame);
        if (!image.IsRegionEmpty(image.Bounds)) p._cels[new(layer, frame, null)] = image.Clone();
        return p;
    }

    /// <summary>
    /// 같은 크기 그림 여러 장(움직이는 GIF·영상 프레임)으로 새 프로젝트를 만든다: 레이어 1개, 그림마다 프레임 하나(길이 = <paramref name="durationsMs"/>).
    /// 그림이 없거나 크기가 다르면 <see cref="ArgumentException"/>.
    /// </summary>
    public static SpriteProject FromFrames(IReadOnlyList<PixelImage> images, IReadOnlyList<int> durationsMs, string? layerName = null)
    {
        if (images.Count == 0) throw new ArgumentException(null, nameof(images));
        int w = images[0].Width, h = images[0].Height;
        var p = CreateBlank(w, h);
        var layer = new Layer(layerName ?? p.NextLayerName());
        p._layers.Add(layer);
        for (int i = 0; i < images.Count; i++)
        {
            var image = images[i];
            if (image.Width != w || image.Height != h) throw new ArgumentException(null, nameof(images));
            var frame = new Frame { DurationMs = i < durationsMs.Count ? durationsMs[i] : Frame.DefaultDurationMs };
            p._frames.Add(frame);
            if (!image.IsRegionEmpty(image.Bounds)) p._cels[new(layer, frame, null)] = image;
        }
        return p;
    }

    // ───────────── 상태 ─────────────

    public int Width => _width;
    public int Height => _height;
    public Size Size => new(_width, _height);
    public Rectangle Bounds => new(0, 0, _width, _height);

    /// <summary>레이어(0 = 맨 아래). 그룹의 부위는 그룹 바로 아래에 이어 붙어 있다.</summary>
    public IReadOnlyList<Layer> Layers => _layers;
    public IReadOnlyList<Frame> Frames => _frames;
    public IReadOnlyList<AnimationTag> Tags => _tags;

    /// <summary>착장 프리셋(그룹마다 스킨 + 부위 재정의). 바꿀 때는 착장 메서드로(실행취소됨).</summary>
    public IReadOnlyList<Outfit> Outfits => _outfits;

    /// <summary>Unity 설정(모드·PPU·기본 피벗·Single 테두리·메시·Read/Write). 바꿀 때는 <see cref="Edit(string, ProjectChange, Action)"/> 안에서.</summary>
    public SpriteSettings Sprite { get; } = new() { Mode = SpriteMode.Multiple };

    /// <summary>내보내기 설정. 바꿀 때는 <see cref="Edit(string, ProjectChange, Action)"/> 안에서.</summary>
    public ExportSettings Export { get; } = new();

    /// <summary>실행취소 기록. 스트로크 중일 수 있으므로 실행취소는 <see cref="Undo"/>/<see cref="Redo"/>를 쓴다.</summary>
    public UndoStack History { get; } = new();

    /// <summary><c>.gpixel</c> 파일 경로(저장 전이면 null).</summary>
    public string? Path
    {
        get => _path;
        set
        {
            if (_path == value) return;
            _path = value;
            NotifyChanged(ProjectChange.Path, markDirty: false);
        }
    }

    public string DisplayName => _path is null ? UntitledName : System.IO.Path.GetFileName(_path);

    /// <summary>확장자 뺀 파일 이름(칸 이름 규칙 <c>{파일}</c>). 저장 전이면 "sprite".</summary>
    public string BaseName => _path is null ? "sprite" : System.IO.Path.GetFileNameWithoutExtension(_path);

    /// <summary>
    /// 마지막으로 내보낸 결과 파일(전체 경로: 이미지 + 있던 <c>.meta</c>). null이면 기록 없음(예전 파일·다른 이름으로 저장).
    /// 다음 내보내기에서 여기에 있고 이번에 만들지 않는 파일만 "옛 결과"로 정리한다. 실행취소 대상이 아니다.
    /// </summary>
    public IReadOnlyList<string>? ExportedFiles { get; set; }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (value) ChangeStamp++;
            if (_isDirty == value) return;
            _isDirty = value;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 내용이 바뀔 때마다(<see cref="NotifyChanged"/>, 수정됨 켜기) 늘어나는 수. 작업 스레드에서 저장하는 동안 바뀌었는지 알아볼 때 쓴다
    /// (<see cref="Gpixel.Core.IO.ProjectSaveJob.Finish"/>는 그 사이 바뀌었으면 수정됨을 끄지 않는다).
    /// </summary>
    public long ChangeStamp { get; private set; }

    /// <summary>내용이 바뀔 때마다 발생.</summary>
    public event EventHandler<ProjectChangedEventArgs>? Changed;

    /// <summary><see cref="IsDirty"/>가 바뀔 때 발생.</summary>
    public event EventHandler? DirtyChanged;

    public void NotifyChanged(ProjectChange change, bool markDirty = true)
    {
        ChangeStamp++;
        if (markDirty) IsDirty = true;
        Changed?.Invoke(this, new ProjectChangedEventArgs(change));
    }

    /// <summary>스트로크 중 화면을 다시 그리도록 픽셀 변경을 알린다(수정됨 표시 켬).</summary>
    public void NotifyPixelsChanged() => NotifyChanged(ProjectChange.Pixels);

    public int IndexOf(Layer layer) => _layers.IndexOf(layer);
    public int IndexOf(Frame frame) => _frames.IndexOf(frame);

    public AnimationTag? FindTag(string name) => _tags.Find(t => t.Name == name);

    public bool HasTag(string name) => FindTag(name) is not null;

    /// <summary>프레임이 속한 첫 태그(없으면 null).</summary>
    public AnimationTag? TagOfFrame(int frameIndex) => _tags.Find(t => t.Contains(frameIndex));

    /// <summary>프레임의 실제 피벗(재정의가 없으면 기본 피벗).</summary>
    public Pivot GetFramePivot(int frameIndex) => _frames[frameIndex].Pivot ?? Sprite.DefaultPivot;

    // ───────────── 셀 ─────────────

    /// <summary>셀 키(스킨 = 첫 스킨이 아닌 부위 스킨, 아니면 null).</summary>
    internal readonly record struct CelKey(Layer Layer, Frame Frame, Skin? Skin);

    /// <summary>셀 키의 스킨: 부위이고 그룹의 첫 스킨이 아닌 스킨이면 그 스킨, 아니면 null(첫 스킨·일반 레이어).</summary>
    private static Skin? SkinKey(Layer layer, Skin? skin)
        => layer.IsPart && skin is not null && layer.Parent is { IsGroup: true } g && !ReferenceEquals(g.SkinList[0], skin) ? skin : null;

    /// <summary>지금 보이는 스킨(부위 재정의 → 그룹의 지금 스킨)의 셀 키 스킨.</summary>
    private static Skin? ActiveKey(Layer layer) => SkinKey(layer, layer.ShownSkin);

    private static bool HasContent(PixelImage? image) => image is not null && !image.IsRegionEmpty(image.Bounds);

    /// <summary>스킨 셀(내용이 있으면), 아니면 첫 스킨 셀.</summary>
    private PixelImage? ResolveCel(Layer layer, Frame frame, Skin? key)
    {
        if (key is not null && _cels.TryGetValue(new(layer, frame, key), out var own) && HasContent(own)) return own;
        return _cels.GetValueOrDefault(new(layer, frame, null));
    }

    /// <summary>
    /// 보이는 셀 이미지(빈 셀이면 null): 부위면 지금 보이는 스킨(<see cref="Layer.ShownSkin"/> — 부위 재정의 → 그룹의 지금 스킨)의 셀,
    /// 그 셀이 비면 첫 스킨 셀(물려받기). 부위 셀은 부위 크기, 그룹은 항상 null. 반환값을 직접 고치지 말고 스트로크로 고친다.
    /// </summary>
    public PixelImage? GetCel(int layerIndex, int frameIndex) => GetCel(_layers[layerIndex], _frames[frameIndex]);

    /// <inheritdoc cref="GetCel(int, int)"/>
    public PixelImage? GetCel(Layer layer, Frame frame) => ResolveCel(layer, frame, ActiveKey(layer));

    /// <summary>부위를 <paramref name="skin"/> 스킨으로 볼 때의 셀(그 스킨 셀이 비면 첫 스킨 셀). 일반 레이어면 스킨과 무관하게 그 셀.</summary>
    public PixelImage? GetSkinCel(Layer layer, Frame frame, Skin skin) => ResolveCel(layer, frame, SkinKey(layer, skin));

    /// <summary>
    /// 스킨 자신의 셀(물려받지 않음, 빈 셀이면 null). <paramref name="skin"/>이 null이면 지금 보이는 스킨. 일반 레이어면 그 셀.
    /// </summary>
    public PixelImage? GetOwnCel(Layer layer, Frame frame, Skin? skin = null)
        => _cels.GetValueOrDefault(new(layer, frame, skin is null ? ActiveKey(layer) : SkinKey(layer, skin)));

    /// <summary>지금 스킨 셀이 비어서 첫 스킨 셀을 물려받아 보이는지(타임라인에서 흐리게 그림).</summary>
    public bool IsCelInherited(Layer layer, Frame frame)
    {
        if (ActiveKey(layer) is not { } key) return false;
        return !HasContent(_cels.GetValueOrDefault(new(layer, frame, key))) && HasContent(_cels.GetValueOrDefault(new(layer, frame, null)));
    }

    /// <inheritdoc cref="IsCelInherited(Layer, Frame)"/>
    public bool IsCelInherited(int layerIndex, int frameIndex) => IsCelInherited(_layers[layerIndex], _frames[frameIndex]);

    /// <summary>보이는 셀(<see cref="GetCel(int, int)"/>)이 없거나 모두 투명이면 true.</summary>
    public bool IsCelEmpty(int layerIndex, int frameIndex) => !HasContent(GetCel(layerIndex, frameIndex));

    /// <summary>
    /// 지금 스킨의 셀을 통째로 바꾼다(null = 비우기 — 첫 스킨이 아니면 다시 첫 스킨 셀을 물려받음, 실행취소됨).
    /// 이미지 크기는 캔버스(부위면 부위) 크기와 같아야 한다. 그룹이면 <see cref="InvalidOperationException"/>.
    /// </summary>
    public void SetCel(int layerIndex, int frameIndex, PixelImage? image, string? label = null)
    {
        var layer = _layers[layerIndex];
        if (layer.IsGroup) throw new InvalidOperationException(GroupHasNoCelsMessage);
        var key = new CelKey(layer, _frames[frameIndex], ActiveKey(layer));
        var size = CelSize(layer);
        if (image is not null && image.Size != size)
            throw new ArgumentException(layer.IsPart ? DomainStrings.Project_PartCelSizeMismatch : DomainStrings.Project_CanvasCelSizeMismatch, nameof(image));
        Edit(label ?? DomainStrings.Project_ChangeCel, ProjectChange.Pixels, () =>
        {
            if (image is null) _cels.Remove(key);
            else _cels[key] = image;
        });
    }

    /// <summary>저장된 모든 셀(부위면 모든 스킨 자신의 셀). 읽기 전용으로 쓴다(고치려면 스트로크나 <see cref="MapCels"/>).</summary>
    public IEnumerable<(Layer Layer, Frame Frame, PixelImage Cel)> StoredCels
        => _cels.Select(kv => (kv.Key.Layer, kv.Key.Frame, kv.Value));

    /// <summary>
    /// 여러 셀의 픽셀을 한꺼번에 바꾼다(실행취소 한 단계). <paramref name="include"/>가 참인 레이어·프레임의 저장된 셀마다
    /// (부위면 모든 스킨 자신의 셀) <paramref name="map"/>을 부르고, 새 이미지(같은 크기)를 돌려주면 그것으로 바꿔 넣는다(null = 그대로).
    /// 잠긴 레이어(잠긴 그룹의 부위 포함)는 건너뛴다. 바꾼 셀 수를 돌려준다(0이면 기록 없음).
    /// </summary>
    public int MapCels(string label, Func<Layer, Frame, bool> include, Func<PixelImage, PixelImage?> map)
    {
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        var changes = new List<(CelKey Key, PixelImage Image)>();
        foreach (var (key, cel) in _cels)
        {
            if (key.Layer.Locked || (key.Layer.Parent?.Locked ?? false) || !include(key.Layer, key.Frame)) continue;
            if (map(cel) is not { } image) continue;
            if (image.Size != cel.Size) throw new ArgumentException(DomainStrings.Project_ChangedCelSizeMismatch, nameof(map));
            changes.Add((key, image));
        }
        if (changes.Count == 0) return 0;
        Edit(label, ProjectChange.Pixels, () =>
        {
            foreach (var (key, image) in changes) _cels[key] = image;
        });
        return changes.Count;
    }

    /// <summary>레이어 셀의 크기: 부위면 부위 크기, 아니면 캔버스 크기.</summary>
    public Size CelSize(Layer layer) => layer.IsPart ? layer.PartSize : Size;

    /// <summary>
    /// 프레임의 보이는 레이어를 아래부터 불투명도를 곱해 합성한 새 이미지(그룹은 지금 스킨).
    /// </summary>
    public PixelImage Flatten(int frameIndex, bool includeHidden = false)
    {
        var result = new PixelImage(_width, _height);
        FlattenInto(result, frameIndex, includeHidden);
        return result;
    }

    /// <summary>
    /// <paramref name="target"/>(캔버스 크기)을 지우고 프레임을 합성해 넣는다(메모리 재사용).
    /// 일반 레이어는 (0,0)에, 부위는 <see cref="Layer.PartOffset"/>에 불투명도 그룹×부위로 그린다(그룹의 지금 스킨, 빈 셀은 첫 스킨 셀). 숨긴 그룹은 부위도 숨긴다.
    /// 레이어 효과(<see cref="Layer.Effects"/>, 이 프레임이 범위 안인 것만)는 셀마다 입혀 그리고(밖으로 번진 부분은 캔버스에서 잘림), 이 프레임에 효과가 켜진 그룹은
    /// 부위들을 먼저 합친 그림에 그룹 효과를 입혀 그룹 불투명도로 한 번에 그린다. 순서표는 <see cref="PlanFrame"/>과 같다.
    /// <paramref name="layerFilter"/>가 있으면 그리는 레이어(일반·부위)마다 걸러낸다(보이기 여부는 <paramref name="includeHidden"/>로).
    /// <paramref name="celMap"/>이 있으면 그리기 전에 셀 이미지를 그 결과로 바꿔 그린다(미리보기용, 셀은 그대로 — 효과는 바꾼 그림에 입힘).
    /// </summary>
    public void FlattenInto(PixelImage target, int frameIndex, bool includeHidden = false, Func<Layer, bool>? layerFilter = null,
        Func<Layer, PixelImage, PixelImage>? celMap = null)
    {
        if (target.Width != _width || target.Height != _height)
            throw new ArgumentException(DomainStrings.Project_TargetCanvasSizeMismatch, nameof(target));
        PlanFrame(frameIndex, includeHidden, layerFilter, celMap).RenderInto(target);
    }

    /// <summary>
    /// 그룹 하나의 보이는 부위만 합성한 캔버스 크기 이미지(썸네일용, 그룹 자신의 보이기 여부는 무시, 효과 포함). 부위마다 지금 보이는 스킨으로,
    /// <paramref name="skin"/>을 주면 부위 재정의와 관계없이 모든 부위를 그 스킨으로 입혀 본다(빈 셀은 첫 스킨 셀).
    /// </summary>
    public PixelImage FlattenGroup(Layer group, int frameIndex, Skin? skin = null) => PlanGroup(group, frameIndex, skin).Render();

    /// <summary>
    /// 레이어 효과가 입혀질 그림(효과 미리보기용, 효과 없이): 일반 레이어·부위는 지금 보이는 셀, 그룹은 보이는 부위들을 부위 불투명도·부위 효과로 합친
    /// 캔버스 크기 그림(그룹 효과·그룹 불투명도 없이). 빈 셀(그룹이면 그릴 부위 없음)이면 null.
    /// </summary>
    public PixelImage? EffectSource(Layer layer, int frameIndex)
    {
        if (!layer.IsGroup) return GetCel(layer, _frames[frameIndex]);
        var plan = GroupSourcePlan(layer, frameIndex);
        return plan.IsEmpty ? null : plan.Render();
    }

    /// <summary>
    /// 프레임 <paramref name="frameIndex"/>에 입힐 레이어 효과(<see cref="LayerEffects.At"/>): 범위 밖 효과를 빼고, 움직이는 효과는 그 프레임의 진행과
    /// 피벗(<see cref="EffectPivot"/>)을, 다른 프레임 그림을 쓰는 효과(잔상)가 있으면 다른 프레임 원본(<see cref="EffectFrameSource"/>)도 채운다.
    /// <paramref name="skin"/>을 주면 다른 프레임도 그 스킨 셀로 본다(부위 아틀라스의 스킨별 셀).
    /// </summary>
    public IReadOnlyList<LayerEffect> EffectsAt(Layer layer, int frameIndex, Skin? skin = null)
        => EffectsAt(layer, layer.Effects, frameIndex, skin is null || layer.IsGroup ? null : f => GetSkinCel(layer, _frames[f], skin));

    /// <summary>
    /// 레이어의 효과 목록 <paramref name="effects"/>를 프레임 <paramref name="frameIndex"/>에 맞춘다. 다른 프레임 원본은 <paramref name="cels"/>(프레임 번호 → 셀, null =
    /// <see cref="EffectFrameSource"/>), 잔상이 없으면 넣지 않는다(만들 것 없음).
    /// </summary>
    private IReadOnlyList<LayerEffect> EffectsAt(Layer layer, IReadOnlyList<LayerEffect> effects, int frameIndex, Func<int, PixelImage?>? cels)
    {
        Func<int, PixelImage?>? source = !LayerEffects.UsesOtherFrames(effects) ? null
            : cels is null ? EffectFrameSource(layer)
            : f => (uint)f < (uint)_frames.Count ? cels(f) : null;
        return LayerEffects.At(effects, frameIndex, _frames.Count, EffectPivot(layer, frameIndex), source,
            source is null ? null : f => EffectPivot(layer, f));
    }

    /// <summary>
    /// 효과가 다른 프레임 그림을 볼 때 쓰는 원본(프레임 번호 → <see cref="EffectSource"/> — 셀 그대로, 그룹이면 부위 합성, 범위 밖·빈 셀이면 null).
    /// 원본만 제공하고 잔상은 그 프레임의 다른 효과를 별도로 적용하므로 효과 재귀 없이 그 프레임 모습을 얻는다.
    /// </summary>
    public Func<int, PixelImage?> EffectFrameSource(Layer layer) => f => (uint)f < (uint)_frames.Count ? EffectSource(layer, f) : null;

    /// <summary>효과의 "피벗" 자리(효과 원본 왼쪽 위 기준 px): 부위면 부위 피벗(부위 셀 기준), 그 밖에는 프레임 피벗(캔버스 기준).</summary>
    public PointF EffectPivot(Layer layer, int frameIndex)
    {
        var (x, y) = layer.IsPart
            ? layer.PartPivot.ToLocalPixel(layer.PartSize.Width, layer.PartSize.Height)
            : GetFramePivot(Math.Clamp(frameIndex, 0, _frames.Count - 1)).ToLocalPixel(_width, _height);
        return new PointF(x, y);
    }

    // ───────────── 스트로크(픽셀 편집) ─────────────

    public bool IsStrokeActive => _stroke is not null;

    /// <summary>
    /// 셀에 그리기 시작: 편집할 셀 이미지(없으면 투명 셀을 새로 만듦, 부위면 부위 크기)를 돌려준다. 이 이미지를 직접 고친 뒤
    /// <see cref="CommitCelStroke"/>로 바뀐 픽셀을 한 단계로 기록한다. 부위면 그룹의 지금 스킨 자신의 셀이고, 그 셀이 비어서
    /// 첫 스킨 셀을 물려받아 보이던 중이면 보이던 그림(첫 스킨 셀)을 복사해 놓고 시작한다(바꾼 픽셀이 없으면 기록 없이 원래대로).
    /// 잠긴 레이어(또는 잠긴 그룹의 부위)·그룹이면 <see cref="InvalidOperationException"/>.
    /// </summary>
    public PixelImage BeginCelStroke(int layerIndex, int frameIndex)
    {
        var layer = _layers[layerIndex];
        var frame = _frames[frameIndex];
        if (layer.IsGroup) throw new InvalidOperationException(GroupHasNoCelsMessage);
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        if (IsLayerLocked(layerIndex)) throw new InvalidOperationException(DomainStrings.Project_LockedLayerCannotDraw);
        var key = new CelKey(layer, frame, ActiveKey(layer));
        bool created = !_cels.TryGetValue(key, out var image);
        if (created)
        {
            var size = CelSize(layer);
            image = new PixelImage(size.Width, size.Height);
            _cels[key] = image;
        }
        var before = image!.Clone();
        PixelImage? seed = null;
        if (key.Skin is not null && !HasContent(image) && _cels.GetValueOrDefault(key with { Skin = null }) is { } inherited && HasContent(inherited))
        {
            image.CopyFrom(inherited);
            seed = image.Clone();
        }
        _stroke = new StrokeState(key, image, before, created, seed);
        return image;
    }

    /// <summary>스트로크 확정. 바뀐 픽셀이 없으면(물려받은 그림을 복사해 시작했으면 그 그림에서 바뀐 것이 없으면) 기록하지 않고 false.</summary>
    public bool CommitCelStroke(string label)
    {
        var s = _stroke;
        _stroke = null;
        if (s is null) return false;
        var patch = s.Seed is not null && PixelPatch.Create(s.Seed, s.Image) is null ? null : PixelPatch.Create(s.Before, s.Image);
        if (patch is null)
        {
            s.Image.CopyFrom(s.Before);
            if (s.Created) _cels.Remove(s.Key);
            return false;
        }
        History.Push(new CelStrokeCommand(this, label, s, patch));
        NotifyChanged(ProjectChange.Pixels);
        return true;
    }

    /// <summary>스트로크 취소: 셀을 시작 전으로 되돌린다.</summary>
    public void CancelCelStroke()
    {
        var s = _stroke;
        _stroke = null;
        if (s is null) return;
        s.Image.CopyFrom(s.Before);
        if (s.Created) _cels.Remove(s.Key);
        NotifyChanged(ProjectChange.Pixels, markDirty: false);
    }

    /// <summary>열린 스트로크를 확정한 뒤 실행취소.</summary>
    public bool Undo()
    {
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        return History.Undo();
    }

    /// <summary>열린 스트로크를 확정한 뒤 다시 실행.</summary>
    public bool Redo()
    {
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        return History.Redo();
    }

    // ───────────── 일반 편집(실행취소 기록) ─────────────

    /// <summary>
    /// 실행취소 가능한 편집 하나: 전후 상태(레이어·프레임·태그 목록과 속성, 셀 참조, 캔버스 크기, 설정)를
    /// 기록한다. 안에서 다른 편집 메서드를 불러도 한 단계로 묶인다. 예외가 나면 편집 전으로 되돌린다.
    /// 끝에 자동 배치가 켜진 그룹의 부위 위치를 다시 계산해 같은 단계에 넣는다(<see cref="Layer.AutoLayout"/>).
    /// 셀 이미지는 참조만 기록하므로 <paramref name="action"/> 안에서 기존 셀 픽셀을 직접 고치지 말고
    /// 새 이미지로 바꿔 넣어야 한다(픽셀 편집은 스트로크로).
    /// </summary>
    public void Edit(string label, ProjectChange change, Action action)
    {
        if (_editDepth > 0)
        {
            action();
            return;
        }
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        var before = Snapshot.Capture(this);
        _editDepth++;
        try
        {
            action();
            change |= RelayoutGroups();
        }
        catch
        {
            before.Restore(this);
            throw;
        }
        finally
        {
            _editDepth--;
        }
        History.Push(new SnapshotCommand(this, label, before, Snapshot.Capture(this), change));
        NotifyChanged(change);
    }

    public T Edit<T>(string label, ProjectChange change, Func<T> action)
    {
        T result = default!;
        Edit(label, change, () => { result = action(); });
        return result;
    }

    // ───────────── 레이어 ─────────────

    private static string GroupHasNoCelsMessage => DomainStrings.Project_GroupCannotDraw;

    /// <summary>그룹의 부위들(아래 → 위). 그룹이 아니면 빈 목록.</summary>
    public IReadOnlyList<Layer> ChildrenOf(Layer group)
    {
        int g = _layers.IndexOf(group);
        int n = ChildCount(g);
        return n == 0 ? [] : _layers.GetRange(g - n, n);
    }

    /// <summary>그룹의 부위 수(그룹이 아니면 0).</summary>
    public int ChildCount(Layer group) => ChildCount(_layers.IndexOf(group));

    private int ChildCount(int groupIndex)
    {
        if (groupIndex < 0 || !_layers[groupIndex].IsGroup) return 0;
        var group = _layers[groupIndex];
        int n = 0;
        while (groupIndex - n - 1 >= 0 && ReferenceEquals(_layers[groupIndex - n - 1].Parent, group)) n++;
        return n;
    }

    /// <summary>실제로 보이는지: 자신이 보이고, 부위면 그룹도 보일 때.</summary>
    public bool IsLayerVisible(int index)
    {
        var layer = _layers[index];
        return layer.Visible && (layer.Parent?.Visible ?? true);
    }

    /// <summary>실제로 잠겼는지: 자신이 잠겼거나, 부위면 그룹이 잠겼을 때.</summary>
    public bool IsLayerLocked(int index)
    {
        var layer = _layers[index];
        return layer.Locked || (layer.Parent?.Locked ?? false);
    }

    /// <summary>프레임에 그림(어느 스킨이든 내용 있는 셀)이 있는 잠긴 레이어(<see cref="IsLayerLocked"/>)들, 아래 → 위. 그 프레임을 지우면 이 그림이 사라진다.</summary>
    public IReadOnlyList<Layer> LockedLayersWithContent(int frameIndex)
    {
        var frame = _frames[frameIndex];
        var found = new HashSet<Layer>();
        foreach (var (key, cel) in _cels)
            if (ReferenceEquals(key.Frame, frame) && (key.Layer.Locked || (key.Layer.Parent?.Locked ?? false)) && HasContent(cel))
                found.Add(key.Layer);
        return found.Count == 0 ? [] : _layers.Where(found.Contains).ToList();
    }

    /// <summary>최상위 레이어(일반·그룹) 덩어리의 시작 번호(그룹이면 맨 아래 부위).</summary>
    private int BlockStart(int topIndex) => topIndex - ChildCount(topIndex);

    /// <summary>최상위 레이어를 넣을 자리를 덩어리를 쪼개지 않는 경계로 맞춘다(그룹 안이면 그 그룹 바로 위).</summary>
    private int TopLevelInsertIndex(int index)
    {
        int at = Math.Clamp(index, 0, _layers.Count);
        if (at > 0 && _layers[at - 1].Parent is { } group) at = _layers.IndexOf(group) + 1;
        return at;
    }

    private Layer RequirePart(int layerIndex)
    {
        var layer = _layers[layerIndex];
        return layer.IsPart ? layer : throw new ArgumentException(DomainStrings.Project_NotPartLayer, nameof(layerIndex));
    }

    /// <summary>빈 일반 레이어를 <paramref name="index"/>(기본: 맨 위, 그룹 안이면 그 그룹 위)에 넣는다.</summary>
    public Layer AddLayer(int? index = null, string? name = null)
    {
        int at = TopLevelInsertIndex(index ?? _layers.Count);
        var layer = new Layer(string.IsNullOrWhiteSpace(name) ? NextLayerName() : UniqueLayerName(name.Trim()));
        Edit(DomainStrings.Project_AddLayer, ProjectChange.Layers, () => _layers.Insert(at, layer));
        return layer;
    }

    /// <summary>빈 그룹(합친 레이어)을 <paramref name="index"/>(기본: 맨 위, 그룹 안이면 그 그룹 위)에 넣는다.</summary>
    public Layer AddGroup(string? name = null, int? index = null)
    {
        int at = TopLevelInsertIndex(index ?? _layers.Count);
        var group = new Layer(UniqueLayerName(string.IsNullOrWhiteSpace(name) ? DomainStrings.Project_DefaultGroupName : name.Trim())) { Kind = LayerKind.Group };
        Edit(DomainStrings.Project_AddGroup, ProjectChange.Layers, () => _layers.Insert(at, group));
        return group;
    }

    /// <summary>
    /// 그룹의 맨 위 부위로 빈 부위 레이어를 넣는다. 위치를 안 주면 캔버스 가운데. 이름은 겹치지 않게 바뀐다.
    /// 그룹이 아니거나 크기가 잘못되면 <see cref="ArgumentException"/>.
    /// </summary>
    public Layer AddPart(Layer group, string? name, int width, int height, Point? offset = null)
    {
        if (!group.IsGroup || !_layers.Contains(group))
            throw new ArgumentException(DomainStrings.Project_AddPartRequiresGroup, nameof(group));
        ValidatePartSize(width, height);
        var part = new Layer(UniqueLayerName(string.IsNullOrWhiteSpace(name) ? DomainStrings.Project_DefaultPartName : name.Trim()))
        {
            Kind = LayerKind.Part,
            Parent = group,
            PartSize = new Size(width, height),
            PartOffset = offset ?? new Point((_width - width) / 2, (_height - height) / 2),
        };
        Edit(DomainStrings.Project_AddPart, ProjectChange.Layers, () => _layers.Insert(_layers.IndexOf(group), part));
        return part;
    }

    /// <summary>부위 위치(캔버스 안 왼쪽 위, 모든 프레임 공통). 자동 배치 중인 그룹의 부위면 <see cref="InvalidOperationException"/>(먼저 <see cref="SetGroupLayout"/>로 끄기).</summary>
    public void SetPartOffset(int layerIndex, Point offset)
    {
        var part = RequirePart(layerIndex);
        if (part.PartOffset == offset) return;
        if (part.IsAutoLaidOut)
            throw new InvalidOperationException(DomainStrings.FormatProject_AutoLayoutBlocksMove(name: part.Parent!.Name));
        Edit(DomainStrings.Project_MovePart, ProjectChange.Layers | ProjectChange.Pixels, () => part.PartOffset = offset);
    }

    /// <summary>
    /// 그룹 자동 배치 켜기·설정 바꾸기(<paramref name="layout"/>, 범위 밖 값은 맞춤) 또는 끄기(null — 부위는 지금 위치에 그대로). 실행취소 한 단계, 같으면 기록 없음.
    /// 켜면 부위 위치를 바로 다시 계산하고, 그 뒤 편집마다 다시 맞춘다.
    /// </summary>
    public void SetGroupLayout(Layer group, GroupLayout? layout)
    {
        if (!group.IsGroup || !_layers.Contains(group)) throw new ArgumentException(DomainStrings.Project_GroupNotInProject, nameof(group));
        layout = layout?.Normalized();
        if (Equals(group.AutoLayout, layout)) return;
        var label = layout is null ? DomainStrings.Project_DisableAutoLayout : group.AutoLayout is null ? DomainStrings.Project_AutoLayout : DomainStrings.Project_AutoLayoutSettings;
        Edit(label, ProjectChange.Layers | ProjectChange.Pixels, () => group.AutoLayout = layout);
    }

    /// <summary>
    /// <paramref name="layout"/>대로 놓았을 때 그룹 부위들의 위치(넣는 순서, 숨긴 부위를 빼면 그 부위는 없음). 프로젝트는 바꾸지 않는다(미리 보기·다시 배치용).
    /// </summary>
    public IReadOnlyList<(Layer Part, Point Offset)> ComputeGroupLayout(Layer group, GroupLayout layout)
    {
        var parts = ChildrenOf(group).Where(l => layout.IncludeHidden || l.Visible).ToList();
        if (layout.Order == GroupLayoutOrder.TopFirst) parts.Reverse();
        var offsets = layout.Arrange(parts.Select(l => l.PartSize).ToList(), Bounds);
        return parts.Select((l, i) => (l, offsets[i])).ToList();
    }

    /// <summary>자동 배치가 켜진 그룹마다 부위 위치를 다시 맞춘다(편집 안에서만 — <see cref="Edit(string, ProjectChange, Action)"/> 끝). 옮긴 것이 있으면 바뀐 부분.</summary>
    private ProjectChange RelayoutGroups()
    {
        bool moved = false;
        for (int i = 0; i < _layers.Count; i++)
        {
            if (_layers[i] is not { IsGroup: true, AutoLayout: { } layout } group) continue;
            foreach (var (part, offset) in ComputeGroupLayout(group, layout))
            {
                if (part.PartOffset == offset) continue;
                part.PartOffset = offset;
                moved = true;
            }
        }
        return moved ? ProjectChange.Layers | ProjectChange.Pixels : ProjectChange.None;
    }

    /// <summary>
    /// 부위 크기 바꾸기: 모든 프레임의 셀을 <paramref name="anchor"/> 기준으로 옮기고, 그림이 캔버스에서 같은 자리에 남도록 위치를,
    /// 같은 그림 지점을 가리키도록 피벗을 보정한다.
    /// </summary>
    public void ResizePart(int layerIndex, int width, int height, Anchor anchor)
    {
        var part = RequirePart(layerIndex);
        ValidatePartSize(width, height);
        var old = part.PartSize;
        if (old.Width == width && old.Height == height) return;
        var (ox, oy) = PixelImage.AnchorOffset(old.Width, old.Height, width, height, anchor);
        var (px, py) = part.PartPivot.ToLocalPixel(old.Width, old.Height);
        var pivot = new Pivot((px + ox) / width, 1f - (py + oy) / height);
        var offset = new Point(part.PartOffset.X - ox, part.PartOffset.Y - oy);
        Edit(DomainStrings.Project_ResizePart, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            foreach (var (key, cel) in CelsOf(part)) _cels[key] = cel.ResizeCanvas(width, height, anchor);
            part.PartSize = new Size(width, height);
            part.PartOffset = offset;
            part.PartPivot = pivot;
        });
    }

    /// <summary>부위 피벗(부위 기준 0~1, 아래 왼쪽 원점). 부위 아틀라스 내보내기에 쓴다.</summary>
    public void SetPartPivot(int layerIndex, Pivot pivot)
    {
        var part = RequirePart(layerIndex);
        Edit(DomainStrings.Project_PartPivot, ProjectChange.Layers, () => part.PartPivot = pivot);
    }

    /// <summary>부위 9-slice 테두리(px). 부위 아틀라스 내보내기에 쓴다.</summary>
    public void SetPartBorder(int layerIndex, SpriteBorder border)
    {
        var part = RequirePart(layerIndex);
        Edit(DomainStrings.Project_PartBorder, ProjectChange.Layers, () => part.PartBorder = border);
    }

    /// <summary>그룹 접기/펼치기(타임라인 표시용, 실행취소·수정됨 표시 없음).</summary>
    public void SetLayerCollapsed(int layerIndex, bool collapsed)
    {
        var group = _layers[layerIndex];
        if (!group.IsGroup) throw new ArgumentException(DomainStrings.Project_NotGroupLayer, nameof(layerIndex));
        if (group.Collapsed == collapsed) return;
        group.Collapsed = collapsed;
        NotifyChanged(ProjectChange.Layers, markDirty: false);
    }

    /// <summary>타임라인 효과 줄 접기/펼치기(표시용, 실행취소·수정됨 표시 없음).</summary>
    public void SetLayerEffectsCollapsed(int layerIndex, bool collapsed)
    {
        var layer = _layers[layerIndex];
        if (layer.EffectsCollapsed == collapsed) return;
        layer.EffectsCollapsed = collapsed;
        NotifyChanged(ProjectChange.Layers, markDirty: false);
    }

    /// <summary>
    /// 레이어를 셀까지 복사해 바로 위에 넣는다. 그룹은 부위·스킨까지 통째로(덩어리 바로 위), 부위는 같은 그룹 안 바로 위(모든 스킨 셀).
    /// </summary>
    public Layer DuplicateLayer(int index)
    {
        var src = _layers[index];
        return Edit(DomainStrings.Project_DuplicateLayer, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            var skinMap = new Dictionary<Skin, Skin>();
            if (src is { IsPart: true, Parent: { IsGroup: true } group })
                // 같은 그룹 안 부위 복제: 원래 부위 전용 스킨을 복사본 전용으로도 하나씩 만든다.
                foreach (var s in src.OwnSkins)
                {
                    var dup = new Skin(UniqueSkinName(group, DomainStrings.FormatProject_CopiedName(name: s.Name)));
                    skinMap[s] = dup;
                    group.SkinList.Add(dup);
                }
            var copy = CopyLayer(src, src.Parent, skinMap);
            _layers.Insert(index + 1, copy);
            var partMap = new Dictionary<Layer, Layer> { [src] = copy };
            if (src.IsGroup)
            {
                var children = ChildrenOf(src).ToList();
                for (int k = 0; k < children.Count; k++)
                {
                    var child = CopyLayer(children[k], copy, skinMap);
                    partMap[children[k]] = child;
                    _layers.Insert(index + 1 + k, child);
                }
            }
            foreach (var (from, to) in skinMap)
                if (from.Owner is { } owner && partMap.TryGetValue(owner, out var newOwner)) to.Owner = newOwner;
            return copy;
        });
    }

    /// <summary>
    /// 레이어 하나를 셀까지 복사한 새 레이어(목록에는 넣지 않음, 원래 칸 기억은 복사하지 않음).
    /// 그룹이면 스킨(부위 전용 포함 — 주인은 부르는 쪽이 새 부위로 바꿈)도 복사하고 원래 스킨 → 새 스킨을 <paramref name="skinMap"/>에 넣는다.
    /// 부위 셀·재정의의 스킨은 <paramref name="skinMap"/>으로 바꾼다(없으면 그대로 — 같은 그룹 안 복제).
    /// </summary>
    private Layer CopyLayer(Layer src, Layer? parent, Dictionary<Skin, Skin> skinMap)
    {
        var copy = new Layer(NextNumberedName(src.Name, n => _layers.Any(l => l.Name == n)))
        {
            Visible = src.Visible,
            Opacity = src.Opacity,
            Kind = src.Kind,
            Parent = parent,
            Collapsed = src.Collapsed,
            EffectsCollapsed = src.EffectsCollapsed,
            AutoLayout = src.AutoLayout,
            PartSize = src.PartSize,
            PartOffset = src.PartOffset,
            PartPivot = src.PartPivot,
            PartBorder = src.PartBorder,
            Effects = src.Effects,
        };
        if (src.IsGroup)
        {
            foreach (var s in src.AllSkins) skinMap[s] = new Skin(s.Name, s.Owner);
            copy.ReplaceSkinsRaw(src.AllSkins.Select(s => skinMap[s]));
            copy.SetActiveSkinRaw(skinMap[src.ActiveSkin!]);
        }
        if (src.SkinOverride is { } over) copy.SetSkinOverrideRaw(skinMap.TryGetValue(over, out var mappedOver) ? mappedOver : over);
        foreach (var (key, cel) in CelsOf(src))
            _cels[new(copy, key.Frame, key.Skin is { } s && skinMap.TryGetValue(s, out var mapped) ? mapped : key.Skin)] = cel.Clone();
        return copy;
    }

    /// <summary>레이어 삭제. 그룹은 부위까지 지운다(부위를 지우면 그 부위 전용 스킨도). 남는 레이어가 없으면 <see cref="InvalidOperationException"/>.</summary>
    public void DeleteLayer(int index)
    {
        var layer = _layers[index];
        int start = layer.IsGroup ? BlockStart(index) : index;
        int count = index - start + 1;
        if (_layers.Count - count < 1) throw new InvalidOperationException(DomainStrings.Project_CannotDeleteLastLayer);
        Edit(DomainStrings.Project_DeleteLayer, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            foreach (var l in _layers.GetRange(start, count))
                foreach (var (key, _) in CelsOf(l)) _cels.Remove(key);
            if (layer is { IsPart: true, Parent: { IsGroup: true } g })
                g.ReplaceSkinsRaw(g.SkinList.Where(s => !ReferenceEquals(s.Owner, layer)).ToList());
            _layers.RemoveRange(start, count);
        });
    }

    /// <summary>
    /// 레이어 순서 바꾸기. 부위는 자기 그룹 안에서만(<paramref name="to"/> = 옮긴 뒤 위치, 그룹 범위로 자름).
    /// 일반 레이어·그룹은 덩어리째 옮기고, <paramref name="to"/>는 덩어리를 뺀 목록에서 덩어리가 들어갈 자리
    /// (그룹 안이면 가까운 그룹 경계로 맞춤). 일반 레이어만 있으면 옮긴 뒤 위치와 같다.
    /// </summary>
    public void MoveLayer(int from, int to)
    {
        var layer = _layers[from];
        if (layer.IsPart)
        {
            int g = _layers.IndexOf(layer.Parent!);
            to = Math.Clamp(to, g - ChildCount(g), g - 1);
            if (from == to) return;
            Edit(DomainStrings.Project_MoveLayer, ProjectChange.Layers, () =>
            {
                _layers.RemoveAt(from);
                _layers.Insert(to, layer);
            });
            return;
        }

        int start = BlockStart(from);
        int count = from - start + 1;
        var rest = new List<Layer>(_layers);
        rest.RemoveRange(start, count);
        int at = Math.Clamp(to, 0, rest.Count);
        if (at > 0 && rest[at - 1].Parent is { } group)
        {
            int top = rest.IndexOf(group);
            int bottom = top - rest.Count(l => ReferenceEquals(l.Parent, group));
            int below = at - bottom, above = top + 1 - at;
            at = below < above || (below == above && at < start) ? bottom : top + 1;
        }
        if (at == start) return;
        var block = _layers.GetRange(start, count);
        Edit(DomainStrings.Project_MoveLayer, ProjectChange.Layers, () =>
        {
            _layers.RemoveRange(start, count);
            _layers.InsertRange(at, block);
        });
    }

    /// <summary>한 칸 위로(▲): 부위는 그룹 안에서 위 부위와, 일반 레이어·그룹은 바로 위 덩어리와 자리를 바꾼다. 맨 위면 그대로.</summary>
    public void MoveLayerUp(int index)
    {
        var layer = _layers[index];
        if (layer.IsPart)
        {
            if (_layers[index + 1].IsPart) MoveLayer(index, index + 1);
            return;
        }
        if (index == _layers.Count - 1) return;
        var next = _layers[index + 1];
        int aboveTop = next.Parent is { } g ? _layers.IndexOf(g) : index + 1;
        MoveLayer(index, BlockStart(index) + (aboveTop - index));
    }

    /// <summary>한 칸 아래로(▼): 부위는 그룹 안에서 아래 부위와, 일반 레이어·그룹은 바로 아래 덩어리와 자리를 바꾼다. 맨 아래면 그대로.</summary>
    public void MoveLayerDown(int index)
    {
        var layer = _layers[index];
        if (layer.IsPart)
        {
            if (index > 0 && ReferenceEquals(_layers[index - 1].Parent, layer.Parent)) MoveLayer(index, index - 1);
            return;
        }
        int start = BlockStart(index);
        if (start == 0) return;
        MoveLayer(index, BlockStart(start - 1));
    }

    /// <summary>
    /// 일반 레이어를 바로 아래 일반 레이어에 병합한다(위 레이어를 효과를 구워 넣고 불투명도대로 합성, 보이기 여부와 관계없이 — 캔버스 밖으로 번진 효과는 잘림).
    /// 위 레이어는 삭제된다. 부위·그룹이 끼면 <see cref="InvalidOperationException"/>.
    /// </summary>
    public void MergeLayerDown(int index)
    {
        if (index <= 0 || index >= _layers.Count) throw new ArgumentOutOfRangeException(nameof(index), DomainStrings.Project_NoLayerBelow);
        var upper = _layers[index];
        var lower = _layers[index - 1];
        if (upper.Kind != LayerKind.Normal || lower.Kind != LayerKind.Normal)
            throw new InvalidOperationException(DomainStrings.Project_CannotMergePartGroup);
        Edit(DomainStrings.Project_MergeDown, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            // 효과(잔상)가 위 레이어의 다른 프레임 셀을 볼 수 있으므로 셀을 바꾸기 전에 모두 그려 둔다.
            (PixelImage Image, int OffsetX, int OffsetY) RenderUpper(CelKey key, PixelImage cel)
            {
                var effects = EffectsAt(upper, _frames.IndexOf(key.Frame));
                return LayerEffects.UsesOtherFrames(effects)
                    ? LayerEffects.Render(cel, effects, LayerEffects.Padding(upper.Effects, cel.Width, cel.Height))
                    : LayerEffects.Render(cel, effects);
            }
            var rendered = CelsOf(upper).Select(c => (c.Key, Render: RenderUpper(c.Key, c.Cel))).ToList();
            foreach (var (key, (image, dx, dy)) in rendered)
            {
                var lowerKey = new CelKey(lower, key.Frame, null);
                var merged = _cels.TryGetValue(lowerKey, out var bottom) ? bottom.Clone() : new PixelImage(_width, _height);
                merged.Composite(image, dx, dy, upper.Opacity);
                _cels[lowerKey] = merged;
                _cels.Remove(key);
            }
            _layers.RemoveAt(index);
        });
    }

    /// <summary>레이어의 모든 셀(모든 스킨, 키와 이미지) 사본 목록.</summary>
    private List<(CelKey Key, PixelImage Cel)> CelsOf(Layer layer)
        => _cels.Where(kv => ReferenceEquals(kv.Key.Layer, layer)).Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>프레임의 모든 셀(모든 레이어·스킨) 사본 목록.</summary>
    private List<(CelKey Key, PixelImage Cel)> CelsOf(Frame frame)
        => _cels.Where(kv => ReferenceEquals(kv.Key.Frame, frame)).Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>레이어 속성 바꾸기(null인 값은 그대로).</summary>
    public void SetLayerProperties(int index, string? name = null, bool? visible = null, bool? locked = null, byte? opacity = null)
    {
        var layer = _layers[index];
        Edit(DomainStrings.Project_LayerProperties, ProjectChange.Layers, () =>
        {
            if (!string.IsNullOrWhiteSpace(name)) layer.Name = name.Trim();
            if (visible is { } v) layer.Visible = v;
            if (locked is { } l) layer.Locked = l;
            if (opacity is { } o) layer.Opacity = o;
        });
    }

    /// <summary>레이어 효과 목록을 통째로 바꾼다(실행취소 한 단계, 같으면 기록 없음). 일반·부위·그룹 모두.</summary>
    public void SetLayerEffects(int index, IReadOnlyList<LayerEffect> effects)
    {
        var layer = _layers[index];
        var list = effects.ToArray();
        if (layer.Effects.SequenceEqual(list)) return;
        Edit(DomainStrings.Project_LayerEffects, ProjectChange.Layers, () => layer.Effects = list);
    }

    /// <summary>
    /// 효과 미리보기(대화상자에서 고치는 동안): 실행취소 기록·수정됨 표시 없이 바꾸고 다시 그리게 한다.
    /// 끝낼 때 <see cref="EndLayerEffectsPreview"/>를 반드시 부른다.
    /// </summary>
    public void PreviewLayerEffects(int index, IReadOnlyList<LayerEffect> effects)
    {
        _layers[index].Effects = effects.ToArray();
        NotifyChanged(ProjectChange.Layers, markDirty: false);
    }

    /// <summary>
    /// 효과 미리보기 끝: 레이어를 미리보기 전 효과(<paramref name="original"/>)로 되돌리고, <paramref name="commit"/>이면 미리보던 효과를
    /// <see cref="SetLayerEffects"/>로 확정한다(실행취소 한 단계 = 대화상자 한 번).
    /// </summary>
    public void EndLayerEffectsPreview(int index, IReadOnlyList<LayerEffect> original, bool commit)
    {
        var layer = _layers[index];
        var previewed = layer.Effects;
        layer.Effects = original.ToArray();
        if (commit) SetLayerEffects(index, previewed);
        if (!commit || layer.Effects.SequenceEqual(original)) NotifyChanged(ProjectChange.Layers, markDirty: false);
    }

    /// <summary>
    /// 효과 영구 적용(실행취소 한 단계): <see cref="LayerEffects.BakeSet"/>의 효과(고른 효과 + 그 위의 켜진 효과)를 그 효과들이 입혀지는 프레임의
    /// 저장된 셀마다(부위면 모든 스킨 자신의 셀 — 물려받아 보이는 스킨 셀은 첫 스킨 셀이 구워지므로 그대로) 보이던 그대로 구워 넣고 목록에서 뺀다.
    /// 아래(뒤) 효과는 효과로 남는다. 일반 레이어는 캔버스 크기로 잘리고, 부위는 번진 그림이 들어가도록 부위 크기·위치를 넓힌다
    /// (모든 프레임·스킨 셀을 같이 옮기고, 피벗·테두리는 같은 그림 지점 — 자동 배치 그룹이면 다시 배치). 효과가 바꾸지 않는 프레임도 그대로 구운 것으로 친다.
    /// 그룹(효과가 여러 부위의 합성에 입혀짐)·잠긴 레이어는 <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <returns>구운 효과(목록 순서)와 그 프레임 범위(합, null = 모든 프레임), 바꾼 셀 수, 부위를 넓혔는지.</returns>
    public (IReadOnlyList<LayerEffect> Baked, FrameRange? Frames, int Cels, bool Grown) BakeLayerEffects(int layerIndex, IEnumerable<int> effectIndices)
        => ApplyBake(PrepareBake(layerIndex, effectIndices));

    /// <summary>
    /// 굽기 계산(<see cref="BakeLayerEffects"/>의 무거운 부분 — 셀마다 효과 입히기). 프로젝트는 읽기만 하므로 아무도 프로젝트를 고치지 않는 동안
    /// 작업 스레드에서 돌려도 된다. 결과는 <see cref="ApplyBake"/>로 같은 상태에 넣는다. 그룹·잠긴 레이어·구울 효과 없음은 여기서 예외.
    /// </summary>
    public BakePlan PrepareBake(int layerIndex, IEnumerable<int> effectIndices)
    {
        var layer = _layers[layerIndex];
        if (layer.IsGroup) throw new InvalidOperationException(DomainStrings.Project_CannotBakeGroupEffects);
        if (layer.Locked || layer.Parent is { Locked: true }) throw new InvalidOperationException(DomainStrings.Project_CannotBakeLockedLayerEffects);
        var order = LayerEffects.BakeSet(layer.Effects, effectIndices);
        if (order.Count == 0) throw new ArgumentException(DomainStrings.Project_NoEffectsToBake, nameof(effectIndices));
        LayerEffect[] baked = [.. order.Select(i => layer.Effects[i] with { Enabled = true })];
        LayerEffect[] rest = [.. layer.Effects.Where((_, i) => !order.Contains(i))];
        FrameRange? range = baked.Any(e => e.Frames is null) ? null
            : FrameRange.Of(baked.Min(e => e.Frames!.Value.From), baked.Max(e => e.Frames!.Value.To), _frames.Count);

        var size = CelSize(layer);
        var pad = layer.IsPart ? LayerEffects.Padding(baked, size.Width, size.Height) : default;
        var keep = new Rectangle(pad.Left, pad.Top, size.Width, size.Height); // 원래 셀 자리(넓힌 그림 기준)
        var rendered = new List<(CelKey Key, PixelImage Image)>();
        var area = keep;
        foreach (var (key, cel) in CelsOf(layer))
        {
            int f = _frames.IndexOf(key.Frame);
            // 잔상은 같은 스킨의 다른 프레임 셀(굽기 전 그대로)을 본다
            var skinKey = key.Skin;
            var effects = EffectsAt(layer, baked, f, other => ResolveCel(layer, _frames[other], skinKey));
            if (effects.Count == 0) continue;
            var (image, _, _) = LayerEffects.Render(cel, effects, pad);
            rendered.Add((key, image));
            if (layer.IsPart && image.GetOpaqueBounds() is { IsEmpty: false } b) area = Rectangle.Union(area, b);
        }
        bool grow = area != keep;
        if (grow) ValidatePartSize(area.Width, area.Height);
        return new BakePlan(layer, baked, rest, range, size, keep, area, rendered);
    }

    /// <summary>
    /// 계산한 굽기를 넣는다(실행취소 한 단계, UI 스레드). <see cref="PrepareBake"/> 바로 뒤, 그 사이 프로젝트를 고치지 않은 채로 부른다.
    /// </summary>
    public (IReadOnlyList<LayerEffect> Baked, FrameRange? Frames, int Cels, bool Grown) ApplyBake(BakePlan plan)
    {
        var layer = plan.Layer;
        var (size, keep, area) = (plan.Size, plan.Keep, plan.Area);
        bool grow = area != keep;
        Edit(DomainStrings.Project_BakeEffects, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            if (grow)
            {
                int ox = keep.X - area.X, oy = keep.Y - area.Y; // 넓힌 부위 안 원래 셀 자리
                foreach (var (key, cel) in CelsOf(layer))
                {
                    var moved = new PixelImage(area.Width, area.Height);
                    moved.Blit(cel, ox, oy);
                    _cels[key] = moved;
                }
                var (px, py) = layer.PartPivot.ToLocalPixel(size.Width, size.Height);
                layer.PartPivot = new Pivot((px + ox) / area.Width, 1f - (py + oy) / area.Height);
                if (!layer.PartBorder.IsNone)
                {
                    var b = layer.PartBorder;
                    layer.PartBorder = new SpriteBorder(b.Left + ox, b.Top + oy, b.Right + area.Right - keep.Right, b.Bottom + area.Bottom - keep.Bottom);
                }
                layer.PartOffset = new Point(layer.PartOffset.X - ox, layer.PartOffset.Y - oy);
                layer.PartSize = area.Size;
            }
            foreach (var (key, image) in plan.Rendered) _cels[key] = image.Crop(area);
            layer.Effects = plan.Rest;
        });
        return (plan.Baked, plan.Frames, plan.Rendered.Count, grow);
    }

    /// <summary><see cref="PrepareBake"/>의 결과(넣기 전): 구울 효과·남길 효과, 셀마다 효과 입힌 그림, 부위를 넓힐 범위.</summary>
    public sealed class BakePlan
    {
        internal BakePlan(Layer layer, IReadOnlyList<LayerEffect> baked, IReadOnlyList<LayerEffect> rest, FrameRange? frames, Size size,
            Rectangle keep, Rectangle area, IReadOnlyList<(CelKey Key, PixelImage Image)> rendered)
        {
            Layer = layer;
            Baked = baked;
            Rest = rest;
            Frames = frames;
            Size = size;
            Keep = keep;
            Area = area;
            Rendered = rendered;
        }

        internal Layer Layer { get; }
        internal IReadOnlyList<LayerEffect> Baked { get; }
        internal IReadOnlyList<LayerEffect> Rest { get; }
        internal FrameRange? Frames { get; }
        internal Size Size { get; }
        internal Rectangle Keep { get; }
        internal Rectangle Area { get; }
        internal IReadOnlyList<(CelKey Key, PixelImage Image)> Rendered { get; }
    }

    // ───────────── 스킨(그룹 = 스킨 슬롯) ─────────────

    private int RequireSkin(Layer group, Skin skin)
    {
        if (!group.IsGroup || !_layers.Contains(group)) throw new ArgumentException(DomainStrings.Project_GroupNotInProject, nameof(group));
        int index = group.SkinList.IndexOf(skin);
        return index >= 0 ? index : throw new ArgumentException(DomainStrings.Project_SkinNotInGroup, nameof(skin));
    }

    private Layer RequireGroupPart(Layer part)
    {
        if (!part.IsPart || part.Parent is not { IsGroup: true } group || !_layers.Contains(part))
            throw new ArgumentException(DomainStrings.Project_PartNotInProjectGroup, nameof(part));
        return group;
    }

    /// <summary>그룹 안에서(부위 전용 스킨까지) 겹치지 않는 스킨 이름(겹치면 " 2", " 3" …). 비면 "스킨".</summary>
    public string UniqueSkinName(Layer group, string baseName, Skin? self = null)
    {
        baseName = baseName.Trim();
        if (baseName.Length == 0) baseName = DomainStrings.Project_DefaultSkinName;
        bool Taken(string n) => group.SkinList.Any(s => !ReferenceEquals(s, self) && s.Name == n);
        if (!Taken(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!Taken($"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// <summary>전체 스킨 수(부위 전용 스킨 앞까지 — 목록에서 전체 스킨은 늘 앞에 모여 있다).</summary>
    private static int GroupSkinCount(Layer group) => group.SkinList.TakeWhile(s => s.Owner is null).Count();

    /// <summary>
    /// 그룹에 전체 스킨을 하나 더 넣고(전체 스킨 끝) 지금 스킨으로 고른다. <paramref name="copyFrom"/>이 있으면 그 스킨 자신의 셀을 복사한다
    /// (첫 스킨을 복사하면 모든 셀, 다른 스킨이면 그 스킨이 직접 그린 셀만 — 나머지는 똑같이 첫 스킨을 물려받음). 이름은 겹치지 않게 바뀐다.
    /// </summary>
    public Skin AddSkin(Layer group, string? name = null, Skin? copyFrom = null)
    {
        if (!group.IsGroup || !_layers.Contains(group)) throw new ArgumentException(DomainStrings.Project_AddSkinRequiresGroup, nameof(group));
        if (copyFrom is not null) RequireSkin(group, copyFrom);
        var skin = new Skin(UniqueSkinName(group, string.IsNullOrWhiteSpace(name) ? (copyFrom is null ? DomainStrings.Project_DefaultSkinName : DomainStrings.FormatProject_CopiedName(name: copyFrom.Name)) : name));
        Edit(copyFrom is null ? DomainStrings.Project_AddSkin : DomainStrings.Project_DuplicateSkin, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            group.SkinList.Insert(GroupSkinCount(group), skin);
            if (copyFrom is not null)
                foreach (var part in ChildrenOf(group))
                    if (copyFrom.Owner is null || ReferenceEquals(copyFrom.Owner, part))
                        CopySkinCels(part, copyFrom, skin);
            group.SetActiveSkinRaw(skin);
        });
        return skin;
    }

    /// <summary>부위의 <paramref name="from"/> 스킨 자신의 셀을 <paramref name="to"/> 스킨 셀로 복사한다(<see cref="Edit(string, ProjectChange, Action)"/> 안에서).</summary>
    private void CopySkinCels(Layer part, Skin from, Skin to)
    {
        foreach (var frame in _frames)
            if (_cels.TryGetValue(new(part, frame, SkinKey(part, from)), out var cel) && HasContent(cel))
                _cels[new(part, frame, to)] = cel.Clone();
    }

    /// <summary>
    /// 부위 하나에만 있는 스킨(부위 전용·개별 스킨, 예: Head에만 "모자")을 만들고 그 부위에 입힌다(재정의). 다른 부위·그룹 목록에는 나타나지 않는다.
    /// <paramref name="copyFrom"/>(그 부위가 고를 수 있는 스킨)이 있으면 그 부위의 그 스킨 셀을 복사한다. 이름은 그룹 안에서 겹치지 않게 바뀐다. 실행취소됨.
    /// </summary>
    public Skin AddPartSkin(Layer part, string? name = null, Skin? copyFrom = null)
    {
        var group = RequireGroupPart(part);
        if (copyFrom is not null && (RequireSkin(group, copyFrom) < 0 || copyFrom.Owner is not null && !ReferenceEquals(copyFrom.Owner, part)))
            throw new ArgumentException(DomainStrings.Project_InvalidPartSkin, nameof(copyFrom));
        var skin = new Skin(UniqueSkinName(group, string.IsNullOrWhiteSpace(name) ? (copyFrom is null ? DomainStrings.FormatProject_PartSkinName(part: part.Name) : DomainStrings.FormatProject_CopiedName(name: copyFrom.Name)) : name), part);
        Edit(copyFrom is null ? DomainStrings.Project_AddPartSkin : DomainStrings.Project_DuplicatePartSkin, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            group.SkinList.Add(skin);
            if (copyFrom is not null) CopySkinCels(part, copyFrom, skin);
        });
        part.SetSkinOverrideRaw(skin);
        NotifyChanged(ProjectChange.Layers | ProjectChange.Pixels);
        return skin;
    }

    /// <summary>부위 전용 스킨을 그룹 전체 스킨으로 올린다(다른 부위에서도 고를 수 있게 — 그 부위들은 빈 칸이라 첫 스킨을 빌림). 실행취소됨.</summary>
    public void PromoteSkin(Layer group, Skin skin)
    {
        RequireSkin(group, skin);
        if (skin.Owner is null) return;
        Edit(DomainStrings.Project_PromoteSkin, ProjectChange.Layers, () =>
        {
            group.SkinList.Remove(skin);
            skin.Owner = null;
            group.SkinList.Insert(GroupSkinCount(group), skin);
        });
    }

    /// <summary>
    /// 전체 스킨을 <paramref name="part"/> 전용 스킨으로 내린다: 다른 부위의 그 스킨 셀은 지우고(실행취소로 되살림), 그룹 지금 스킨이었으면 첫 스킨으로,
    /// 다른 부위의 재정의는 그룹을 따르게 한다. 첫 스킨·마지막 전체 스킨은 내릴 수 없다(<see cref="InvalidOperationException"/>).
    /// </summary>
    public void DemoteSkin(Layer group, Skin skin, Layer part)
    {
        int index = RequireSkin(group, skin);
        if (!ReferenceEquals(RequireGroupPart(part), group)) throw new ArgumentException(DomainStrings.Project_PartNotInGroup, nameof(part));
        if (skin.Owner is not null) return;
        if (index == 0 || GroupSkinCount(group) <= 1) throw new InvalidOperationException(DomainStrings.Project_CannotDemoteBaseSkin);
        Edit(DomainStrings.Project_DemoteSkin, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            foreach (var other in ChildrenOf(group))
            {
                if (ReferenceEquals(other, part)) continue;
                foreach (var (key, _) in CelsOf(other))
                    if (ReferenceEquals(key.Skin, skin)) _cels.Remove(key);
            }
            group.SkinList.Remove(skin);
            skin.Owner = part;
            group.SkinList.Add(skin);
            if (ReferenceEquals(group.ActiveSkinRaw, skin)) group.SetActiveSkinRaw(group.SkinList[0]);
        });
        if (ReferenceEquals(group.ActiveSkinRaw, skin)) group.SetActiveSkinRaw(group.SkinList[0]);
    }

    /// <summary>스킨 이름 바꾸기. 비었거나 그룹 안(부위 전용 스킨 포함)에 같은 이름이 있으면 <see cref="ArgumentException"/>.</summary>
    public void RenameSkin(Layer group, Skin skin, string name)
    {
        RequireSkin(group, skin);
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException(DomainStrings.Project_EmptySkinName, nameof(name));
        if (group.SkinList.Any(s => !ReferenceEquals(s, skin) && s.Name == name))
            throw new ArgumentException(DomainStrings.FormatProject_DuplicateSkinName(name: name), nameof(name));
        if (skin.Name == name) return;
        Edit(DomainStrings.Project_RenameSkin, ProjectChange.Layers, () => skin.Name = name);
    }

    /// <summary>
    /// 스킨 삭제(그 스킨의 셀도 지움). 첫 스킨을 지우면 다음 전체 스킨이 첫 스킨이 된다(그 스킨이 물려받던 빈 셀은 빈 셀이 됨).
    /// 지금 스킨이었으면 바로 앞(없으면 새 첫) 스킨을 고른다. 마지막 전체 스킨은 지울 수 없다(<see cref="InvalidOperationException"/>).
    /// 부위 전용 스킨은 언제든 지울 수 있다.
    /// </summary>
    public void DeleteSkin(Layer group, Skin skin)
    {
        int index = RequireSkin(group, skin);
        if (skin.Owner is null && GroupSkinCount(group) <= 1) throw new InvalidOperationException(DomainStrings.Project_CannotDeleteLastSkin);
        bool wasActive = ReferenceEquals(group.ActiveSkin, skin);
        Edit(DomainStrings.Project_DeleteSkin, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            var skins = group.SkinList;
            foreach (var part in ChildrenOf(group))
            {
                foreach (var (key, _) in CelsOf(part))
                    if (index == 0 ? key.Skin is null : ReferenceEquals(key.Skin, skin)) _cels.Remove(key);
                if (index == 0)
                    foreach (var (key, cel) in CelsOf(part))
                        if (ReferenceEquals(key.Skin, skins[1]))
                        {
                            _cels.Remove(key);
                            _cels[key with { Skin = null }] = cel;
                        }
            }
            skins.RemoveAt(index);
            if (wasActive) group.SetActiveSkinRaw(skins[Math.Max(0, index - 1)]);
        });
    }

    /// <summary>
    /// 전체 스킨 순서 바꾸기(<see cref="Layer.Skins"/> 안 번호, <paramref name="to"/> = 옮긴 뒤 위치). 첫 스킨이 바뀌면 새 첫 스킨이 기본(다른 스킨의 빈 셀이 물려받는 셀)이 된다.
    /// </summary>
    public void MoveSkin(Layer group, int from, int to)
    {
        if (!group.IsGroup || !_layers.Contains(group)) throw new ArgumentException(DomainStrings.Project_GroupNotInProject, nameof(group));
        var skins = group.SkinList;
        int count = GroupSkinCount(group);
        if (from < 0 || from >= count) throw new ArgumentOutOfRangeException(nameof(from));
        to = Math.Clamp(to, 0, count - 1);
        if (from == to) return;
        Edit(DomainStrings.Project_ReorderSkin, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            var oldBase = skins[0];
            var skin = skins[from];
            skins.RemoveAt(from);
            skins.Insert(to, skin);
            var newBase = skins[0];
            if (ReferenceEquals(oldBase, newBase)) return;
            // 첫 스킨 셀은 null 키라 키를 맞바꾼다: 옛 첫 스킨 셀 → 옛 첫 스킨 키, 새 첫 스킨 셀 → null 키.
            foreach (var part in ChildrenOf(group))
            {
                var cels = CelsOf(part);
                foreach (var (key, _) in cels)
                    if (key.Skin is null || ReferenceEquals(key.Skin, newBase)) _cels.Remove(key);
                foreach (var (key, cel) in cels)
                {
                    if (key.Skin is null) _cels[key with { Skin = oldBase }] = cel;
                    else if (ReferenceEquals(key.Skin, newBase)) _cels[key with { Skin = null }] = cel;
                }
            }
        });
    }

    /// <summary>
    /// 스킨 하나를 같은 목록 안에서 앞(−)/뒤(+)로: 전체 스킨은 전체 스킨끼리(<see cref="MoveSkin(Layer, int, int)"/>), 부위 전용 스킨은 같은 부위 전용 스킨끼리.
    /// 끝이면 그대로. 실행취소됨.
    /// </summary>
    public void MoveSkin(Layer group, Skin skin, int delta)
    {
        RequireSkin(group, skin);
        if (skin.Owner is null)
        {
            int i = group.SkinList.IndexOf(skin);
            MoveSkin(group, i, i + delta);
            return;
        }
        var own = group.SkinList.Where(s => ReferenceEquals(s.Owner, skin.Owner)).ToList();
        int at = own.IndexOf(skin), target = Math.Clamp(at + delta, 0, own.Count - 1);
        if (at == target) return;
        Edit(DomainStrings.Project_ReorderSkin, ProjectChange.Layers, () =>
        {
            // 같은 부위 전용 스킨들이 차지한 자리는 그대로 두고, 그 자리 안에서 순서만 옮긴다.
            var skins = group.SkinList;
            var slots = Enumerable.Range(0, skins.Count).Where(i => ReferenceEquals(skins[i].Owner, skin.Owner)).ToList();
            own.RemoveAt(at);
            own.Insert(target, skin);
            for (int k = 0; k < slots.Count; k++) skins[slots[k]] = own[k];
        });
    }

    /// <summary>
    /// 그룹의 지금 스킨(착장) 고르기(전체 스킨만). 재정의가 없는 부위만 따라 바뀐다(재정의한 부위는 그대로 — <see cref="ResetPartSkins"/>로 되돌림).
    /// 보기 전환이라 실행취소 기록에는 남기지 않지만 파일에 저장되고 프레임 시트 결과가 바뀌므로 수정됨 표시를 켠다. 열린 스트로크는 먼저 확정한다.
    /// </summary>
    public void SetActiveSkin(Layer group, Skin skin)
    {
        RequireSkin(group, skin);
        if (skin.Owner is not null) throw new ArgumentException(DomainStrings.Project_PartSkinCannotApplyToGroup, nameof(skin));
        if (ReferenceEquals(group.ActiveSkin, skin)) return;
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        group.SetActiveSkinRaw(skin);
        NotifyChanged(ProjectChange.Layers | ProjectChange.Pixels);
    }

    /// <summary>
    /// 부위 하나만 보일 스킨 고르기(재정의, null = 그룹 스킨을 따름 — 그룹 전체 스킨이나 이 부위 전용 스킨). 그룹 스킨과 같은 스킨을 골라도 재정의로 남는다.
    /// <see cref="SetActiveSkin"/>과 같이 실행취소 기록 없이 수정됨 표시를 켜고 파일에 저장된다. 부위가 아니거나 고를 수 없는 스킨이면 <see cref="ArgumentException"/>.
    /// </summary>
    public void SetPartSkin(Layer part, Skin? skin)
    {
        var group = RequireGroupPart(part);
        if (skin is not null)
        {
            RequireSkin(group, skin);
            if (skin.Owner is not null && !ReferenceEquals(skin.Owner, part))
                throw new ArgumentException(DomainStrings.Project_OtherPartSkin, nameof(skin));
        }
        if (ReferenceEquals(part.SkinOverride, skin)) return;
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        part.SetSkinOverrideRaw(skin);
        NotifyChanged(ProjectChange.Layers | ProjectChange.Pixels);
    }

    /// <summary>그룹의 모든 부위 재정의를 지워 그룹 스킨을 따르게 한다(바뀐 것이 있으면 true, 실행취소 기록 없음).</summary>
    public bool ResetPartSkins(Layer group)
    {
        if (!group.IsGroup || !_layers.Contains(group)) throw new ArgumentException(DomainStrings.Project_GroupNotInProject, nameof(group));
        var parts = ChildrenOf(group).Where(p => p.SkinOverrideRaw is not null).ToList();
        if (parts.Count == 0) return false;
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        bool visible = parts.Any(p => p.SkinOverride is not null);
        foreach (var p in parts) p.SetSkinOverrideRaw(null);
        if (visible) NotifyChanged(ProjectChange.Layers | ProjectChange.Pixels);
        return visible;
    }

    /// <summary>
    /// 파일 읽기용: 그룹마다 스킨 목록을 바로잡는다 — 부위 전용 스킨의 주인이 그 그룹의 부위가 아니면 전체 스킨으로, 전체 스킨을 앞으로 모은다
    /// (첫 스킨은 늘 전체 스킨). 셀은 스킨 객체로 걸려 있어 그대로다(첫 스킨이 바뀌는 순서 문제는 생기지 않도록 첫 스킨은 처음부터 전체 스킨).
    /// </summary>
    internal void NormalizeSkins()
    {
        foreach (var g in _layers.Where(l => l.IsGroup))
        {
            var children = ChildrenOf(g);
            var list = g.SkinList;
            foreach (var s in list)
                if (s.Owner is not null && !children.Contains(s.Owner)) s.Owner = null;
            if (list[0].Owner is not null) list[0].Owner = null;
            var ordered = list.Where(s => s.Owner is null).Concat(list.Where(s => s.Owner is not null)).ToList();
            if (!ordered.SequenceEqual(list)) g.ReplaceSkinsRaw(ordered);
        }
    }

    // ───────────── 착장 프리셋 ─────────────

    private void RequireOutfit(Outfit outfit)
    {
        if (!_outfits.Contains(outfit)) throw new ArgumentException(DomainStrings.Project_OutfitNotInProject, nameof(outfit));
    }

    /// <summary>겹치지 않는 착장 이름(겹치면 " 2", " 3" …). 비면 "착장".</summary>
    public string UniqueOutfitName(string baseName, Outfit? self = null)
    {
        baseName = baseName.Trim();
        if (baseName.Length == 0) baseName = DomainStrings.Project_DefaultOutfitName;
        bool Taken(string n) => _outfits.Any(o => !ReferenceEquals(o, self) && o.Name == n);
        if (!Taken(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!Taken($"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// <summary>지금 착장: 그룹마다(위 그룹부터) 지금 스킨 + 재정의한 부위.</summary>
    private (OutfitSlot[] Slots, OutfitPart[] Parts) CaptureOutfit()
    {
        var slots = new List<OutfitSlot>();
        var parts = new List<OutfitPart>();
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var l = _layers[i];
            if (l.IsGroup) slots.Add(new OutfitSlot(l, l.ActiveSkin!));
            else if (l is { IsPart: true, SkinOverride: { } over }) parts.Add(new OutfitPart(l, over));
        }
        return ([.. slots], [.. parts]);
    }

    /// <summary>지금 착장을 새 착장 프리셋으로 저장한다(맨 끝, 이름은 겹치지 않게, 실행취소됨).</summary>
    public Outfit SaveOutfit(string? name = null)
    {
        var (slots, parts) = CaptureOutfit();
        var outfit = new Outfit(UniqueOutfitName(name ?? ""), slots, parts);
        Edit(DomainStrings.Project_SaveOutfit, ProjectChange.Layers | ProjectChange.Settings, () => _outfits.Add(outfit));
        return outfit;
    }

    /// <summary>착장 프리셋 내용을 지금 착장으로 바꾼다(이름은 그대로, 실행취소됨).</summary>
    public void OverwriteOutfit(Outfit outfit)
    {
        RequireOutfit(outfit);
        var (slots, parts) = CaptureOutfit();
        Edit(DomainStrings.Project_OverwriteOutfit, ProjectChange.Layers | ProjectChange.Settings, () =>
        {
            outfit.Slots = slots;
            outfit.Parts = parts;
        });
    }

    /// <summary>착장 이름 바꾸기. 비었거나 같은 이름이 있으면 <see cref="ArgumentException"/>.</summary>
    public void RenameOutfit(Outfit outfit, string name)
    {
        RequireOutfit(outfit);
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException(DomainStrings.Project_EmptyOutfitName, nameof(name));
        if (_outfits.Any(o => !ReferenceEquals(o, outfit) && o.Name == name))
            throw new ArgumentException(DomainStrings.FormatProject_DuplicateOutfitName(name: name), nameof(name));
        if (outfit.Name == name) return;
        Edit(DomainStrings.Project_RenameOutfit, ProjectChange.Layers | ProjectChange.Settings, () => outfit.Name = name);
    }

    /// <summary>착장 삭제(프레임 시트 내보내기 착장 목록에서도 뺀다, 실행취소됨).</summary>
    public void DeleteOutfit(Outfit outfit)
    {
        RequireOutfit(outfit);
        Edit(DomainStrings.Project_DeleteOutfit, ProjectChange.Layers | ProjectChange.Settings, () =>
        {
            _outfits.Remove(outfit);
            foreach (var o in (SheetOptions?[])[Export.Common, Export.PartsOverride])
                if (o is not null && o.Outfits.Contains(outfit)) o.Outfits = o.Outfits.Where(x => !ReferenceEquals(x, outfit)).ToArray();
        });
    }

    /// <summary>착장 순서 바꾸기(<paramref name="to"/> = 옮긴 뒤 위치, 실행취소됨).</summary>
    public void MoveOutfit(int from, int to)
    {
        var outfit = _outfits[from];
        to = Math.Clamp(to, 0, _outfits.Count - 1);
        if (from == to) return;
        Edit(DomainStrings.Project_ReorderOutfit, ProjectChange.Layers | ProjectChange.Settings, () =>
        {
            _outfits.RemoveAt(from);
            _outfits.Insert(to, outfit);
        });
    }

    /// <summary>착장 항목 중 지금 프로젝트에서 쓸 수 없는 것(지운 그룹·부위·스킨)의 설명. 비면 모두 쓸 수 있다.</summary>
    public IReadOnlyList<string> MissingOutfitEntries(Outfit outfit)
    {
        var missing = new List<string>();
        foreach (var (group, skin) in outfit.Slots)
        {
            if (!_layers.Contains(group)) missing.Add(DomainStrings.FormatProject_MissingOutfitGroup(name: group.Name));
            else if (!group.SkinList.Contains(skin)) missing.Add(DomainStrings.FormatProject_MissingOutfitGroupSkin(group: group.Name, skin: skin.Name));
        }
        foreach (var (part, skin) in outfit.Parts)
        {
            if (!_layers.Contains(part) || part.Parent is not { IsGroup: true } g) missing.Add(DomainStrings.FormatProject_MissingOutfitPart(name: part.Name));
            else if (!g.SkinList.Contains(skin)) missing.Add(DomainStrings.FormatProject_MissingOutfitPartSkin(part: part.Name, skin: skin.Name));
        }
        return missing;
    }

    /// <summary>
    /// 착장 프리셋을 지금 착장으로 입힌다: 착장에 든 그룹마다 스킨을 고르고 그 부위들의 재정의를 착장대로(없으면 그룹 따름) 맞춘다.
    /// 착장에 없는 그룹(나중에 만든 그룹)은 그대로 둔다. 쓸 수 없는 항목(지운 그룹·부위·스킨)은 건너뛰고 그 설명을 돌려준다.
    /// 착장 바꾸기와 같이 실행취소 기록 없이 수정됨 표시를 켠다.
    /// </summary>
    public IReadOnlyList<string> ApplyOutfit(Outfit outfit)
    {
        RequireOutfit(outfit);
        if (_stroke is not null) CommitCelStroke(DomainStrings.Project_Edit);
        bool changed = false;
        foreach (var (group, skin) in outfit.Slots)
        {
            if (!_layers.Contains(group)) continue;
            if (group.SkinList.Contains(skin) && !ReferenceEquals(group.ActiveSkin, skin))
            {
                group.SetActiveSkinRaw(skin);
                changed = true;
            }
            foreach (var part in ChildrenOf(group))
            {
                var over = OutfitOverride(outfit, group, part);
                if (ReferenceEquals(part.SkinOverride, over)) continue;
                part.SetSkinOverrideRaw(over);
                changed = true;
            }
        }
        if (changed) NotifyChanged(ProjectChange.Layers | ProjectChange.Pixels);
        return MissingOutfitEntries(outfit);
    }

    /// <summary>
    /// 착장을 입히면(<see cref="ApplyOutfit"/>) 바뀔 잠긴 레이어들: 지금 스킨이 바뀔 잠긴 그룹, 재정의가 바뀔 잠긴 부위(그룹이 잠긴 부위 포함).
    /// 비면 잠긴 레이어는 그대로다.
    /// </summary>
    public IReadOnlyList<Layer> LockedLayersChangedBy(Outfit outfit)
    {
        RequireOutfit(outfit);
        var changed = new List<Layer>();
        foreach (var (group, skin) in outfit.Slots)
        {
            if (!_layers.Contains(group)) continue;
            if (group.Locked && group.SkinList.Contains(skin) && !ReferenceEquals(group.ActiveSkin, skin)) changed.Add(group);
            foreach (var part in ChildrenOf(group))
                if ((part.Locked || group.Locked) && !ReferenceEquals(part.SkinOverride, OutfitOverride(outfit, group, part)))
                    changed.Add(part);
        }
        return changed;
    }

    /// <summary>착장을 입혔을 때 부위의 재정의: 착장의 부위 항목(그룹 스킨 목록에 있을 때), 없으면 null(그룹 따름).</summary>
    private static Skin? OutfitOverride(Outfit outfit, Layer group, Layer part)
    {
        Skin? over = null;
        foreach (var e in outfit.Parts)
            if (ReferenceEquals(e.Part, part) && group.SkinList.Contains(e.Skin)) over = e.Skin;
        return over;
    }

    /// <summary>
    /// 착장을 입혔을 때 부위가 보일 스킨: 착장의 부위 항목 → 착장의 그룹 항목 → (착장에 없거나 쓸 수 없으면) 지금 보이는 스킨.
    /// <paramref name="outfit"/>이 null이면 지금 보이는 스킨.
    /// </summary>
    public static Skin? SkinUnder(Layer part, Outfit? outfit)
    {
        if (outfit is null || part.Parent is not { IsGroup: true } g) return part.ShownSkin;
        foreach (var e in outfit.Parts)
            if (ReferenceEquals(e.Part, part) && g.SkinList.Contains(e.Skin)) return e.Skin;
        foreach (var s in outfit.Slots)
            if (ReferenceEquals(s.Group, g) && g.SkinList.Contains(s.Skin)) return s.Skin;
        return part.ShownSkin;
    }

    /// <summary>
    /// 프레임을 착장 프리셋으로 입혀 합성한 새 이미지(지금 착장은 바꾸지 않음, <paramref name="outfit"/> null = 지금 착장).
    /// <paramref name="excluded"/>에 든 최상위 레이어(그룹이면 그 부위 전부)는 빼고, 보이기·불투명도는 <see cref="Flatten"/>과 같다.
    /// <paramref name="cache"/>를 주면 잔상 원본 등을 이웃 프레임과 나눠 쓴다(결과는 같다).
    /// </summary>
    public PixelImage FlattenOutfit(int frameIndex, Outfit? outfit, IReadOnlyCollection<Layer>? excluded = null, EffectRenderCache? cache = null)
    {
        var frame = _frames[frameIndex];
        bool Excluded(Layer layer) => excluded is { Count: > 0 } && excluded.Contains(layer);
        PixelImage? PartCel(Layer part) => ResolveCel(part, frame, SkinKey(part, SkinUnder(part, outfit)));
        var steps = new List<RenderStep>();
        foreach (var layer in _layers)
        {
            if (layer.IsGroup)
            {
                if (layer.HasActiveEffectsAt(frameIndex) && layer.Visible && !Excluded(layer)
                    && GroupStepOf(layer, frameIndex, part => part.Visible ? PartCel(part) : null) is { } group)
                    steps.Add(group);
                continue;
            }
            if (layer.Parent is { IsGroup: true } pg && pg.HasActiveEffectsAt(frameIndex)) continue; // 그룹 차례에 함께 그린다
            if (!(layer.Visible && (layer.Parent?.Visible ?? true))) continue;
            if (Excluded(layer) || (layer.Parent is { } g && Excluded(g))) continue;
            if ((layer.IsPart ? PartCel(layer) : GetCel(layer, frame)) is { } cel) steps.Add(CelStepOf(layer, frameIndex, cel, DrawOpacity(layer)));
        }
        return new RenderPlan(_width, _height, steps).Render(cache is null ? null : new RenderPlanOptions { Cache = cache });
    }

    // ───────────── 프레임 ─────────────

    /// <summary>
    /// 빈 프레임을 <paramref name="index"/>(기본: 맨 끝)에 넣는다. 길이는 앞 프레임과 같다.
    /// 태그·효과 프레임 범위 안(시작 뒤 ~ 끝)에 넣으면 그 범위가 늘어나고, 뒤쪽 범위는 밀린다.
    /// </summary>
    public Frame AddFrame(int? index = null)
    {
        int at = Math.Clamp(index ?? _frames.Count, 0, _frames.Count);
        var frame = new Frame();
        if (_frames.Count > 0) frame.DurationMs = _frames[Math.Max(0, at - 1)].DurationMs;
        Edit(DomainStrings.Project_AddFrame, ProjectChange.Frames | ProjectChange.Tags, () =>
        {
            _frames.Insert(at, frame);
            foreach (var t in _tags)
            {
                if (t.From >= at) { t.From++; t.To++; }
                else if (t.To >= at) t.To++;
            }
            MapEffectRanges(r => r.From >= at ? new FrameRange(r.From + 1, r.To + 1) : r.To >= at ? new FrameRange(r.From, r.To + 1) : r);
        });
        return frame;
    }

    /// <summary>프레임을 셀까지 복사해 바로 뒤에 넣는다. 원래 프레임을 포함한 태그·효과 범위는 늘어난다.</summary>
    public Frame DuplicateFrame(int index)
    {
        var src = _frames[index];
        var copy = new Frame { DurationMs = src.DurationMs, Pivot = src.Pivot };
        Edit(DomainStrings.Project_DuplicateFrame, ProjectChange.Frames | ProjectChange.Tags | ProjectChange.Pixels, () =>
        {
            _frames.Insert(index + 1, copy);
            foreach (var (key, cel) in CelsOf(src)) _cels[key with { Frame = copy }] = cel.Clone();
            foreach (var t in _tags)
            {
                if (t.From > index) { t.From++; t.To++; }
                else if (t.To >= index) t.To++;
            }
            MapEffectRanges(r => r.From > index ? new FrameRange(r.From + 1, r.To + 1) : r.To >= index ? new FrameRange(r.From, r.To + 1) : r);
        });
        return copy;
    }

    /// <inheritdoc cref="ExtendFrames(FrameExtension)"/>
    public void ExtendFrames(int from, int to, int count, FrameExtendMode mode) => ExtendFrames(new FrameExtension(from, to, count, mode));

    /// <summary>
    /// 프레임 늘리기(실행취소 한 단계): 프레임 [From, To]를 <see cref="FrameExtension.Count"/>개로 바꾼다 — 자리마다 <see cref="FrameExtension.SourceOf"/>의 원래 프레임,
    /// 원래 프레임이 처음 오는 자리는 그 프레임 그대로, 나머지는 사본(모든 레이어·스킨 셀을 복사, 길이·피벗 같음, 칸 이름은 비움 — 이름 규칙).
    /// 태그·모든 레이어 효과 범위는 <see cref="FrameExtension.MapRange"/>로 바뀐다(범위를 덮으면 늘어나고 뒤쪽은 밀림). 같은 수면 아무것도 하지 않는다.
    /// </summary>
    public void ExtendFrames(FrameExtension extension)
    {
        var x = extension;
        if (x.To >= _frames.Count) throw new ArgumentOutOfRangeException(nameof(extension), DomainStrings.FormatFrames_InvalidRange(from: x.From + 1, to: x.To + 1));
        if (x.Added == 0) return;
        var sources = _frames.GetRange(x.From, x.Length);
        Edit(DomainStrings.Project_ExtendFrames, ProjectChange.Frames | ProjectChange.Tags | ProjectChange.Pixels, () =>
        {
            var cels = new List<(CelKey Key, PixelImage Cel)>?[x.Length];
            var result = new Frame[x.Count];
            var placed = new bool[x.Length];
            for (int k = 0; k < x.Count; k++)
            {
                int s = x.SourceOf(k);
                var src = sources[s];
                if (!placed[s])
                {
                    placed[s] = true;
                    result[k] = src;
                    continue;
                }
                var copy = new Frame { DurationMs = src.DurationMs, Pivot = src.Pivot };
                foreach (var (key, cel) in cels[s] ??= CelsOf(src)) _cels[key with { Frame = copy }] = cel.Clone();
                result[k] = copy;
            }
            _frames.RemoveRange(x.From, x.Length);
            _frames.InsertRange(x.From, result);
            foreach (var t in _tags)
            {
                var r = x.MapRange(new FrameRange(t.From, t.To));
                t.From = r.From;
                t.To = r.To;
            }
            MapEffectRanges(r => x.MapRange(r));
        });
    }

    /// <summary>
    /// 프레임 삭제(마지막 하나는 지울 수 없음). 태그·효과 범위가 줄고, 비게 된 태그와 범위의 프레임이 모두 지워진 효과는 지워진다
    /// (같은 실행취소 단계라 되돌리면 함께 돌아온다).
    /// </summary>
    public void DeleteFrame(int index) => DeleteFrames(index, 1);

    /// <summary>
    /// 이어진 프레임 [<paramref name="start"/>, <paramref name="start"/> + <paramref name="count"/>)를 한 단계로 지운다(<see cref="DeleteFrame"/>와 같은 규칙 —
    /// 태그·효과 범위는 남는 프레임으로 줄고, 남는 프레임이 없으면 그 태그·효과를 지움). 모든 프레임은 지울 수 없다.
    /// </summary>
    public void DeleteFrames(int start, int count)
    {
        if (count <= 0) return;
        if (count >= _frames.Count) throw new InvalidOperationException(DomainStrings.Project_CannotDeleteLastFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + count, _frames.Count);
        int end = start + count; // 지우는 칸 끝(제외)
        // 범위 [a, b]에서 남는 첫·끝 프레임을 지운 뒤 번호로(남는 프레임이 없으면 null)
        (int From, int To)? Remap(int a, int b)
        {
            int first = a >= start && a < end ? end : a;
            int last = b >= start && b < end ? start - 1 : b;
            if (first > b || last < a || first > last) return null;
            return (first >= end ? first - count : first, last >= end ? last - count : last);
        }
        var removed = _frames.GetRange(start, count);
        Edit(count == 1 ? DomainStrings.Project_DeleteFrame : DomainStrings.Project_DeleteFrames, ProjectChange.Frames | ProjectChange.Tags | ProjectChange.Pixels, () =>
        {
            _frames.RemoveRange(start, count);
            foreach (var frame in removed)
                foreach (var (key, _) in CelsOf(frame).ToList()) _cels.Remove(key);
            foreach (var t in _tags)
            {
                if (Remap(t.From, t.To) is { } r) (t.From, t.To) = r;
                else t.To = t.From - 1;
            }
            _tags.RemoveAll(t => t.To < t.From);
            MapEffectRanges(r => Remap(r.From, r.To) is { } m ? new FrameRange(m.From, m.To) : null);
        });
    }

    /// <summary>
    /// 프레임 순서 바꾸기(<paramref name="to"/> = 옮긴 뒤 위치). 태그·효과 범위는 프레임 번호 기준이라 그대로다
    /// (태그 안의 순서를 바꾸거나 다른 태그 칸으로 옮기는 동작, 효과는 그 자리에 온 프레임에 입혀짐).
    /// </summary>
    public void MoveFrame(int from, int to)
    {
        var frame = _frames[from];
        to = Math.Clamp(to, 0, _frames.Count - 1);
        if (from == to) return;
        Edit(DomainStrings.Project_MoveFrame, ProjectChange.Frames, () =>
        {
            _frames.RemoveAt(from);
            _frames.Insert(to, frame);
        });
    }

    /// <summary>
    /// 모든 레이어 효과의 프레임 범위를 <paramref name="map"/>으로 고친다(모든 프레임 효과는 그대로, <paramref name="map"/>이 null이면 그 효과를 지움).
    /// 바뀐 레이어만 새 목록으로 바꾼다(편집 안에서 불러 실행취소 기록에 함께 든다).
    /// </summary>
    private void MapEffectRanges(Func<FrameRange, FrameRange?> map)
    {
        foreach (var layer in _layers)
        {
            List<LayerEffect>? list = null;
            for (int i = 0; i < layer.Effects.Count; i++)
            {
                var e = layer.Effects[i];
                var next = e.Frames is { } r ? map(r) : null;
                bool same = e.Frames is null || next == e.Frames;
                if (same && list is null) continue;
                list ??= [.. layer.Effects.Take(i)];
                if (same) list.Add(e);
                else if (next is not null) list.Add(e with { Frames = next });
            }
            if (list is not null) layer.Effects = list.ToArray();
        }
    }

    public void SetFrameDuration(int index, int durationMs)
    {
        var frame = _frames[index];
        Edit(DomainStrings.Project_FrameDuration, ProjectChange.Frames, () => frame.DurationMs = durationMs);
    }

    /// <summary>프레임 피벗 재정의(null = 기본 피벗 사용).</summary>
    public void SetFramePivot(int index, Pivot? pivot)
    {
        var frame = _frames[index];
        Edit(DomainStrings.Project_FramePivot, ProjectChange.Frames, () => frame.Pivot = pivot);
    }

    /// <summary>내보낼 칸 이름 재정의(null/빈 문자열 = 이름 규칙 사용).</summary>
    public void SetFrameName(int index, string? name)
    {
        var frame = _frames[index];
        Edit(DomainStrings.Project_FrameName, ProjectChange.Frames, () => frame.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    // ───────────── 태그 ─────────────

    /// <summary>태그 추가. 이름이 비었거나 이미 있으면 <see cref="ArgumentException"/>(<see cref="UniqueTagName"/> 사용).</summary>
    public AnimationTag AddTag(string name, int from, int to, TagDirection direction = TagDirection.Forward, bool loop = true)
    {
        name = name.Trim();
        ValidateTag(name, from, to, null);
        var tag = new AnimationTag(name, from, to)
        {
            Direction = direction,
            Loop = loop,
            Color = AnimationTag.DefaultColors[_tags.Count % AnimationTag.DefaultColors.Count],
        };
        Edit(DomainStrings.Project_AddTag, ProjectChange.Tags, () => _tags.Add(tag));
        return tag;
    }

    /// <summary>태그 속성 바꾸기(null인 값은 그대로). 잘못된 값이면 <see cref="ArgumentException"/>.</summary>
    public void SetTag(int index, string? name = null, int? from = null, int? to = null,
        TagDirection? direction = null, bool? loop = null, Color32? color = null)
    {
        var tag = _tags[index];
        var newName = string.IsNullOrWhiteSpace(name) ? tag.Name : name.Trim();
        int newFrom = from ?? tag.From, newTo = to ?? tag.To;
        ValidateTag(newName, newFrom, newTo, tag);
        Edit(DomainStrings.Project_EditTag, ProjectChange.Tags, () =>
        {
            tag.Name = newName;
            tag.From = newFrom;
            tag.To = newTo;
            if (direction is { } d) tag.Direction = d;
            if (loop is { } l) tag.Loop = l;
            if (color is { } c) tag.Color = c;
        });
    }

    public void RemoveTag(int index)
    {
        var tag = _tags[index];
        Edit(DomainStrings.Project_DeleteTag, ProjectChange.Tags, () => _tags.Remove(tag));
    }

    private void ValidateTag(string name, int from, int to, AnimationTag? self)
    {
        if (name.Length == 0) throw new ArgumentException(DomainStrings.Project_EmptyTagName, nameof(name));
        if (_tags.Any(t => !ReferenceEquals(t, self) && t.Name == name))
            throw new ArgumentException(DomainStrings.FormatProject_DuplicateTagName(name: name), nameof(name));
        if (from < 0 || to >= _frames.Count || from > to)
            throw new ArgumentOutOfRangeException(nameof(from), DomainStrings.FormatProject_InvalidTagRange(from: from, to: to));
    }

    // ───────────── 캔버스 ─────────────

    /// <summary>
    /// 캔버스 크기 바꾸기: 일반 레이어 셀을 <paramref name="anchor"/> 기준으로 옮기고, 부위는 위치만 같은 만큼 옮기며(부위 셀은 그대로),
    /// 피벗이 같은 그림 지점을 가리키도록 보정한다(<see cref="ResizeCanvasAndScale"/>의 배율 1).
    /// </summary>
    public void ResizeCanvas(int width, int height, Anchor anchor)
    {
        var (ox, oy) = PixelImage.AnchorOffset(_width, _height, width, height, anchor);
        ResizeCanvasAndScale(width, height, new Rectangle(ox, oy, _width, _height));
    }

    /// <summary>
    /// 캔버스 크기 바꾸기 + 그림 크기 바꾸기(실행취소 한 단계): 새 캔버스 <paramref name="width"/>×<paramref name="height"/>에서 지금 캔버스 전체 그림이
    /// <paramref name="image"/>(새 캔버스 좌표) 자리에 그 크기로 놓인다. 일반 레이어 셀은 최근접으로 늘이거나 줄여 놓고(캔버스 밖은 잘림),
    /// 부위는 위치·크기를 같은 비율로(가장자리마다 반올림, 최소 1) 바꿔 옮기며 모든 스킨의 셀을 새 부위 크기로 최근접, 9-slice 테두리도 비율대로 바꾼다.
    /// 기본·프레임 피벗은 같은 그림 지점을 가리키도록 옮긴다(부위 피벗은 부위 기준이라 그대로). 그림 크기가 같으면 부위는 위치만 옮긴다.
    /// <para>
    /// <paramref name="slice"/>가 있으면 9-slice로 늘인다(Unity Sliced·Tiled처럼): 프레임 칸 테두리(<see cref="SpriteSettings.Border"/>)를 쓸 수 있으면
    /// 일반 레이어 셀을 그 테두리로 늘이고 부위 가장자리·피벗도 같은 대응으로 옮기며, 테두리는 그림 가장자리에서 같은 픽셀 수로 남긴다(여백만큼 더함).
    /// 자기 테두리(<see cref="Layer.PartBorder"/>)가 있는 부위는 그 테두리로 늘이고 테두리 값을 그대로 둔다(새 크기에 다 안 들어가면 비율대로).
    /// </para>
    /// </summary>
    public void ResizeCanvasAndScale(int width, int height, Rectangle image, NineSliceFill? slice = null)
    {
        ValidateSize(width, height);
        if (image.Width < 1 || image.Height < 1 || image.Width > PixelImage.MaxSize || image.Height > PixelImage.MaxSize)
            throw new ArgumentOutOfRangeException(nameof(image), DomainStrings.FormatProject_InvalidImageSize(max: PixelImage.MaxSize, width: image.Width, height: image.Height));
        int oldW = _width, oldH = _height;
        if (width == oldW && height == oldH && image == new Rectangle(0, 0, oldW, oldH)) return;
        var fill = slice ?? NineSliceFill.Stretch;
        var border = slice is not null && NineSlice.Applies(Sprite.Border, oldW, oldH) ? Sprite.Border : SpriteBorder.None;
        bool sliced = !border.IsNone;
        // 원래 캔버스 좌표(연속) → 새 캔버스 좌표: 9-slice면 테두리 안은 그대로·가운데만 비율대로, 아니면 전체를 비율대로.
        double MapXF(double x) => image.X + (sliced ? NineSlice.MapCoordinate(x, oldW, border.Left, border.Right, image.Width) : x * image.Width / oldW);
        double MapYF(double y) => image.Y + (sliced ? NineSlice.MapCoordinate(y, oldH, border.Top, border.Bottom, image.Height) : y * image.Height / oldH);
        Pivot Map(Pivot p)
        {
            var (px, py) = p.ToLocalPixel(oldW, oldH);
            return new Pivot((float)(MapXF(px) / width), 1f - (float)(MapYF(py) / height));
        }
        // 원래 캔버스 좌표 → 새 캔버스 좌표(반올림). 부위 가장자리를 따로 옮겨 맞닿은 부위는 계속 맞닿는다.
        int MapX(int x) => (int)Math.Floor(MapXF(x) + 0.5);
        int MapY(int y) => (int)Math.Floor(MapYF(y) + 0.5);
        var parts = _layers.Where(l => l.IsPart).Select(part =>
        {
            var r = part.PartRect;
            int left = MapX(r.Left), top = MapY(r.Top);
            int w = Math.Clamp(MapX(r.Right) - left, 1, PixelImage.MaxSize), h = Math.Clamp(MapY(r.Bottom) - top, 1, PixelImage.MaxSize);
            return (Part: part, Rect: new Rectangle(left, top, w, h));
        }).ToList();
        Edit(DomainStrings.Project_CanvasSize, ProjectChange.Canvas | ProjectChange.Pixels | ProjectChange.Frames | ProjectChange.Settings | ProjectChange.Layers, () =>
        {
            foreach (var key in _cels.Keys.Where(k => !k.Layer.IsPart).ToList())
                _cels[key] = ImageTransform.ResizeCanvasScaled(_cels[key], width, height, image, border, fill);
            foreach (var (part, rect) in parts)
            {
                var old = part.PartSize;
                part.PartOffset = rect.Location;
                if (rect.Size == old) continue;
                bool partSliced = slice is not null && NineSlice.Applies(part.PartBorder, old.Width, old.Height);
                foreach (var (key, cel) in CelsOf(part))
                    _cels[key] = partSliced ? NineSlice.Scale(cel, rect.Width, rect.Height, part.PartBorder, fill) : ImageTransform.ScaleNearest(cel, rect.Width, rect.Height);
                part.PartSize = rect.Size;
                if (!partSliced || !NineSlice.Applies(part.PartBorder, rect.Width, rect.Height))
                    part.PartBorder = ScaleBorder(part.PartBorder, old, rect.Size);
            }
            _width = width;
            _height = height;
            Sprite.DefaultPivot = Map(Sprite.DefaultPivot);
            foreach (var f in _frames)
                if (f.Pivot is { } fp) f.Pivot = Map(fp);
            // 프레임 칸 테두리: 그림 가장자리에서 같은 픽셀 수(그림 바깥 여백만큼 더하고, 캔버스 밖으로 잘린 만큼 뺌).
            if (sliced)
                Sprite.Border = new SpriteBorder(
                    Math.Max(0, border.Left + image.X), Math.Max(0, border.Top + image.Y),
                    Math.Max(0, border.Right + width - image.Right), Math.Max(0, border.Bottom + height - image.Bottom));
        });
    }

    /// <summary>9-slice 테두리를 부위 크기 비율대로(반올림) 바꾼다. 마주 보는 두 변의 합은 새 크기를 넘지 않는다.</summary>
    private static SpriteBorder ScaleBorder(SpriteBorder b, Size from, Size to)
    {
        if (b.IsNone) return b;
        static int Scale(int v, int from, int to) => (int)Math.Floor((double)v * to / from + 0.5);
        int left = Math.Min(Scale(b.Left, from.Width, to.Width), to.Width);
        int right = Math.Min(Scale(b.Right, from.Width, to.Width), to.Width - left);
        int top = Math.Min(Scale(b.Top, from.Height, to.Height), to.Height);
        int bottom = Math.Min(Scale(b.Bottom, from.Height, to.Height), to.Height - top);
        return new SpriteBorder(left, top, right, bottom);
    }

    /// <summary>모든 셀 좌우 뒤집기(피벗·부위 위치·테두리도 뒤집음).</summary>
    public void FlipCanvasHorizontal()
    {
        int w = _width;
        TransformCanvas(DomainStrings.Project_FlipHorizontal, ImageTransform.FlipHorizontal, p => new Pivot(1f - p.X, p.Y),
            r => new Rectangle(w - r.Right, r.Y, r.Width, r.Height),
            b => new SpriteBorder(b.Right, b.Top, b.Left, b.Bottom));
    }

    /// <summary>모든 셀 상하 뒤집기(피벗·부위 위치·테두리도 뒤집음).</summary>
    public void FlipCanvasVertical()
    {
        int h = _height;
        TransformCanvas(DomainStrings.Project_FlipVertical, ImageTransform.FlipVertical, p => new Pivot(p.X, 1f - p.Y),
            r => new Rectangle(r.X, h - r.Bottom, r.Width, r.Height),
            b => new SpriteBorder(b.Left, b.Bottom, b.Right, b.Top));
    }

    /// <summary>모든 셀 90° 회전(가로·세로가 바뀌고 피벗·부위 위치·크기·테두리도 회전).</summary>
    public void RotateCanvas90(bool clockwise = true)
    {
        int w = _width, h = _height;
        if (clockwise)
            TransformCanvas(DomainStrings.Project_RotateClockwise, img => ImageTransform.Rotate90(img, true), p => new Pivot(p.Y, 1f - p.X),
                r => new Rectangle(h - r.Bottom, r.X, r.Height, r.Width),
                b => new SpriteBorder(b.Bottom, b.Left, b.Top, b.Right));
        else
            TransformCanvas(DomainStrings.Project_RotateCounterclockwise, img => ImageTransform.Rotate90(img, false), p => new Pivot(1f - p.Y, p.X),
                r => new Rectangle(r.Y, w - r.Right, r.Height, r.Width),
                b => new SpriteBorder(b.Top, b.Right, b.Bottom, b.Left));
    }

    /// <param name="transform">셀 이미지 변환(일반·부위 셀 모두).</param>
    /// <param name="pivotMap">0~1 피벗 변환(기본·프레임·부위 피벗).</param>
    /// <param name="partRectMap">변환 전 캔버스 안 부위 영역 → 변환 뒤 영역.</param>
    /// <param name="borderMap">부위 9-slice 테두리 변환.</param>
    private void TransformCanvas(string label, Func<PixelImage, PixelImage> transform, Func<Pivot, Pivot> pivotMap,
        Func<Rectangle, Rectangle> partRectMap, Func<SpriteBorder, SpriteBorder> borderMap)
    {
        var probe = transform(new PixelImage(_width, _height));
        Edit(label, ProjectChange.Canvas | ProjectChange.Pixels | ProjectChange.Frames | ProjectChange.Settings | ProjectChange.Layers, () =>
        {
            foreach (var key in _cels.Keys.ToList()) _cels[key] = transform(_cels[key]);
            foreach (var part in _layers.Where(l => l.IsPart))
            {
                var r = partRectMap(part.PartRect);
                part.PartOffset = r.Location;
                part.PartSize = r.Size;
                part.PartPivot = pivotMap(part.PartPivot);
                part.PartBorder = borderMap(part.PartBorder);
            }
            _width = probe.Width;
            _height = probe.Height;
            Sprite.DefaultPivot = pivotMap(Sprite.DefaultPivot);
            foreach (var f in _frames)
                if (f.Pivot is { } fp) f.Pivot = pivotMap(fp);
        });
    }

    // ───────────── 이름 ─────────────

    public string UniqueTagName(string baseName)
    {
        baseName = baseName.Trim();
        if (baseName.Length == 0) baseName = "tag";
        if (!HasTag(baseName)) return baseName;
        for (int i = 1; ; i++)
            if (!HasTag($"{baseName}_{i}")) return $"{baseName}_{i}";
    }

    public string UniqueLayerName(string baseName)
    {
        if (_layers.All(l => l.Name != baseName)) return baseName;
        for (int i = 2; ; i++)
            if (_layers.All(l => l.Name != $"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// <summary>
    /// 복제 이름: 이름 끝이 숫자면 그 숫자 + 1(자릿수 그대로 — <c>item_009</c> → <c>item_010</c>, <c>a-99</c> → <c>a-100</c>),
    /// 숫자가 없으면 <c>_1</c>을 붙인다. <paramref name="taken"/>인 이름이면 쓰이지 않을 때까지 더 올린다.
    /// </summary>
    public static string NextNumberedName(string name, Func<string, bool> taken)
    {
        int end = name.Length, start = end;
        while (start > 0 && char.IsAsciiDigit(name[start - 1])) start--;
        string prefix, format;
        System.Numerics.BigInteger n;
        if (start == end)
        {
            prefix = name + "_";
            format = "";
            n = 0;
        }
        else
        {
            prefix = name[..start];
            format = new string('0', end - start);
            n = System.Numerics.BigInteger.Parse(name[start..], System.Globalization.CultureInfo.InvariantCulture);
        }
        while (true)
        {
            n++;
            var candidate = prefix + n.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>"레이어 N" 중 쓰이지 않은 다음 이름.</summary>
    public string NextLayerName()
    {
        for (int i = _layers.Count + 1; ; i++)
        {
            var candidate = DomainStrings.FormatProject_DefaultLayerName(number: i);
            if (_layers.All(l => l.Name != candidate)) return candidate;
        }
    }

    // ───────────── 파일 읽기·가져오기용 내부 구성 ─────────────

    internal void AddLayerRaw(Layer layer) => _layers.Add(layer);
    internal void ReplaceLayersRaw(IEnumerable<Layer> layers)
    {
        var list = layers.ToList();
        _layers.Clear();
        _layers.AddRange(list);
    }
    internal void AddFrameRaw(Frame frame) => _frames.Add(frame);
    internal void AddTagRaw(AnimationTag tag) => _tags.Add(tag);
    internal void AddOutfitRaw(Outfit outfit) => _outfits.Add(outfit);
    /// <summary>파일 읽기·가져오기용: 셀 하나를 넣는다. <paramref name="skin"/>이 null이거나 첫 스킨이면 첫 스킨(일반 레이어) 셀.</summary>
    internal void SetCelRaw(Layer layer, Frame frame, PixelImage image, Skin? skin = null) => _cels[new(layer, frame, SkinKey(layer, skin))] = image;

    /// <summary>파일 쓰기용: 스킨 자신의 셀(<paramref name="skin"/> null = 첫 스킨·일반 레이어), 물려받지 않음.</summary>
    internal PixelImage? GetCelRaw(Layer layer, Frame frame, Skin? skin) => _cels.GetValueOrDefault(new(layer, frame, SkinKey(layer, skin)));

    /// <summary>
    /// 파일 읽기용: 그룹이 없는(또는 그룹이 아닌 부모를 가진) 부위는 일반 레이어로 바꾸고, 그룹의 부위를 그룹 바로 아래로 모은다.
    /// 바꾼 것이 있으면 true.
    /// </summary>
    internal bool NormalizeHierarchy()
    {
        bool changed = false;
        foreach (var layer in _layers)
        {
            if (layer.IsPart && (layer.Parent is not { IsGroup: true } parent || !_layers.Contains(parent)))
            {
                layer.Kind = LayerKind.Normal;
                layer.Parent = null;
                changed = true;
            }
            else if (!layer.IsPart && layer.Parent is not null)
            {
                layer.Parent = null;
                changed = true;
            }
        }
        var ordered = new List<Layer>(_layers.Count);
        foreach (var layer in _layers)
        {
            if (layer.IsPart) continue;
            if (layer.IsGroup) ordered.AddRange(_layers.Where(l => ReferenceEquals(l.Parent, layer)));
            ordered.Add(layer);
        }
        if (!ordered.SequenceEqual(_layers))
        {
            _layers.Clear();
            _layers.AddRange(ordered);
            changed = true;
        }
        return changed;
    }

    private static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > PixelImage.MaxSize || height > PixelImage.MaxSize)
            throw new ArgumentOutOfRangeException(nameof(width), DomainStrings.FormatCanvas_InvalidSize(max: PixelImage.MaxSize, width: width, height: height));
    }

    private static void ValidatePartSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > PixelImage.MaxSize || height > PixelImage.MaxSize)
            throw new ArgumentException(DomainStrings.FormatProject_InvalidPartSize(max: PixelImage.MaxSize, width: width, height: height), nameof(width));
    }

    // ───────────── 실행취소 기록 ─────────────

    /// <summary>열린 스트로크. <paramref name="Seed"/>: 물려받은 그림을 복사해 시작했으면 그 시작 그림(바뀐 것이 없는지 보는 데 씀).</summary>
    private sealed record StrokeState(CelKey Key, PixelImage Image, PixelImage Before, bool Created, PixelImage? Seed);

    private sealed class CelStrokeCommand(SpriteProject project, string label, StrokeState stroke, PixelPatch patch) : IUndoCommand
    {
        public string Label { get; } = label;
        public StrokeState Stroke => stroke;
        public PixelPatch Patch => patch;

        public void Undo()
        {
            patch.ApplyBefore(stroke.Image);
            if (stroke.Created) project._cels.Remove(stroke.Key);
            project.NotifyChanged(ProjectChange.Pixels);
        }

        public void Redo()
        {
            project._cels[stroke.Key] = stroke.Image;
            patch.ApplyAfter(stroke.Image);
            project.NotifyChanged(ProjectChange.Pixels);
        }
    }

    private sealed class SnapshotCommand(SpriteProject project, string label, Snapshot before, Snapshot after, ProjectChange change)
        : IUndoCommand
    {
        public string Label { get; } = label;
        public Snapshot Before => before;
        public Snapshot After => after;
        public ProjectChange Change => change;

        public void Undo()
        {
            before.Restore(project);
            project.NotifyChanged(change | before.SizeChange(after));
        }

        public void Redo()
        {
            after.Restore(project);
            project.NotifyChanged(change | before.SizeChange(after));
        }
    }

    private readonly record struct LayerState(
        Layer Layer, string Name, bool Visible, bool Locked, byte Opacity, LayerKind Kind, Layer? Parent,
        Size PartSize, Point PartOffset, Pivot PartPivot, SpriteBorder PartBorder, Rectangle? PartSourceRect,
        (Skin Skin, string Name, Layer? Owner)[]? Skins, Skin? ActiveSkin, IReadOnlyList<LayerEffect> Effects, GroupLayout? AutoLayout)
    {
        public static LayerState Of(Layer l) => new(l, l.Name, l.Visible, l.Locked, l.Opacity, l.Kind, l.Parent,
            l.PartSize, l.PartOffset, l.PartPivot, l.PartBorder, l.PartSourceRect,
            l.IsGroup ? l.SkinList.Select(s => (s, s.Name, s.Owner)).ToArray() : null, l.IsGroup ? l.ActiveSkin : null, l.Effects, l.AutoLayout);

        /// <summary>
        /// 상태를 되돌린다. 지금 스킨(착장)은 실행취소 대상이 아니므로 스킨 목록 구성이 같으면 지금 고른 스킨을 그대로 두고,
        /// 스킨이 생기거나 없어지는 단계를 되돌릴 때만 기록된 스킨을 고른다.
        /// </summary>
        public Layer Apply()
        {
            Layer.Name = Name;
            Layer.Visible = Visible;
            Layer.Locked = Locked;
            Layer.Opacity = Opacity;
            Layer.Kind = Kind;
            Layer.Parent = Parent;
            Layer.PartSize = PartSize;
            Layer.PartOffset = PartOffset;
            Layer.PartPivot = PartPivot;
            Layer.PartBorder = PartBorder;
            Layer.PartSourceRect = PartSourceRect;
            Layer.AutoLayout = AutoLayout;
            Layer.Effects = Effects;
            if (Skins is not null)
            {
                var current = Layer.ActiveSkinRaw;
                var restored = Skins.Select(s => s.Skin).ToList();
                bool sameSet = Layer.SkinList.Count == restored.Count && restored.All(Layer.SkinList.Contains);
                foreach (var (skin, name, owner) in Skins)
                {
                    skin.Name = name;
                    skin.Owner = owner;
                }
                Layer.ReplaceSkinsRaw(restored);
                Layer.SetActiveSkinRaw(sameSet && current is not null && restored.Contains(current) ? current : ActiveSkin);
            }
            return Layer;
        }
    }

    /// <summary>구조 상태 사본(그룹 접힘은 제외). 셀 이미지는 참조만 담는다(구조 편집은 셀을 새 이미지로 바꿔 넣으므로 안전).</summary>
    private sealed partial class Snapshot
    {
        private int _w, _h;
        private LayerState[] _layers = [];
        private (Frame Frame, int Ms, Pivot? Pivot, string? Name)[] _frames = [];
        private (AnimationTag Tag, AnimationTag State)[] _tags = [];
        private (Outfit Outfit, string Name, IReadOnlyList<OutfitSlot> Slots, IReadOnlyList<OutfitPart> Parts)[] _outfits = [];
        private KeyValuePair<CelKey, PixelImage>[] _cels = [];
        private SpriteSettings _sprite = new();
        private ExportSettings _export = new();

        public static Snapshot Capture(SpriteProject p) => new()
        {
            _w = p._width,
            _h = p._height,
            _layers = p._layers.Select(LayerState.Of).ToArray(),
            _frames = p._frames.Select(f => (f, f.DurationMs, f.Pivot, f.Name)).ToArray(),
            _tags = p._tags.Select(t => (t, t.Clone())).ToArray(),
            _outfits = p._outfits.Select(o => (o, o.Name, o.Slots, o.Parts)).ToArray(),
            _cels = p._cels.ToArray(),
            _sprite = p.Sprite.Clone(),
            _export = p.Export.Clone(),
        };

        public void Restore(SpriteProject p)
        {
            p._width = _w;
            p._height = _h;
            p._layers.Clear();
            foreach (var state in _layers) p._layers.Add(state.Apply());
            p._frames.Clear();
            foreach (var (frame, ms, pivot, name) in _frames)
            {
                frame.DurationMs = ms;
                frame.Pivot = pivot;
                frame.Name = name;
                p._frames.Add(frame);
            }
            p._tags.Clear();
            foreach (var (tag, state) in _tags)
            {
                tag.Name = state.Name;
                tag.From = state.From;
                tag.To = state.To;
                tag.Direction = state.Direction;
                tag.Loop = state.Loop;
                tag.Color = state.Color;
                p._tags.Add(tag);
            }
            p._outfits.Clear();
            foreach (var (outfit, name, slots, parts) in _outfits)
            {
                outfit.Name = name;
                outfit.Slots = slots;
                outfit.Parts = parts;
                p._outfits.Add(outfit);
            }
            p._cels.Clear();
            foreach (var kv in _cels) p._cels[kv.Key] = kv.Value;
            p.Sprite.CopyFrom(_sprite);
            p.Export.CopyFrom(_export);
        }

        public ProjectChange SizeChange(Snapshot other)
            => _w != other._w || _h != other._h ? ProjectChange.Canvas : ProjectChange.None;
    }
}
