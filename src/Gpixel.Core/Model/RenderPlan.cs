using System.Drawing;
using Gpixel.Core.Edit;

namespace Gpixel.Core.Model;

/// <summary>
/// 렌더링을 지켜보는 쪽(렌더링 스레드에서 불림). 진행값과, 원할 때 지금까지 그린 중간 그림을 받는다.
/// </summary>
public interface IRenderObserver
{
    /// <summary>끝낸 작업 단위 <paramref name="done"/> / 전체 <paramref name="total"/>.</summary>
    void Progress(int done, int total);

    /// <summary>지금 중간 그림을 받을지(받는 쪽이 간격을 정한다). true면 곧 <see cref="Snapshot"/>이 불린다.</summary>
    bool WantsSnapshot { get; }

    /// <summary>중간 그림: 끝난 레이어 + 지금 계산 중인 레이어(그 단계까지). 받는 쪽이 가진다(다시 쓰이지 않음).</summary>
    void Snapshot(PixelImage image);
}

/// <summary>렌더링 선택: 취소, 효과 결과 캐시, 진행 관찰, 중간 결과를 담을 레이어.</summary>
public sealed class RenderPlanOptions
{
    public static readonly RenderPlanOptions Default = new();

    public CancellationToken Cancellation { get; init; }

    /// <summary>잔상 원본·중간 결과를 다시 쓰는 캐시(없으면 매번 계산).</summary>
    public EffectRenderCache? Cache { get; init; }

    public IRenderObserver? Observer { get; init; }

    /// <summary>
    /// 효과 1~k까지 입힌 중간 결과를 <see cref="Cache"/>에 담을 레이어(효과를 고치는 중인 레이어). null이면 담지 않는다 —
    /// 담는 데 그림 복사가 들므로 값을 바꾸는 레이어에만 쓴다. 담긴 것은 이 값과 관계없이 다시 쓴다.
    /// </summary>
    public Layer? CheckpointLayer { get; init; }
}

/// <summary>
/// 프레임 하나(또는 셀 하나)를 그리는 순서표. 부르는 스레드에서 프로젝트 상태(레이어 순서·보이기·불투명도·효과 목록·셀 이미지 참조·피벗,
/// 잔상이 볼 다른 프레임 원본)를 담아 두므로, 만든 뒤에는 프로젝트를 건드리지 않고 다른 스레드에서 그릴 수 있다.
/// 셀 이미지는 참조로 담으므로 그리는 동안 셀 픽셀이 바뀌면 결과가 섞일 수 있다 — 부르는 쪽이 변경 알림을 보고 그런 결과를 버린다.
/// <see cref="SpriteProject.FlattenInto"/> 같은 동기 그리기도 같은 순서표를 써서 결과가 늘 같다.
/// </summary>
public sealed class RenderPlan
{
    private readonly IReadOnlyList<RenderStep> _steps;
    private CacheKey? _contentKey;

    internal RenderPlan(int width, int height, IReadOnlyList<RenderStep> steps)
    {
        Width = width;
        Height = height;
        _steps = steps;
        int units = 0;
        long work = 0;
        foreach (var s in steps)
        {
            units += s.Units;
            work += s.Work;
            RunsScripts |= s.RunsScripts;
        }
        WorkUnits = units;
        EstimatedWork = work;
    }

    /// <summary>결과 그림 크기.</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>그릴 것이 없다(결과는 투명).</summary>
    public bool IsEmpty => _steps.Count == 0;

    /// <summary>진행 단위 수(레이어 합성 하나·켜진 효과 하나 = 1).</summary>
    public int WorkUnits { get; }

    /// <summary>대략의 계산량(합성·효과를 거치는 픽셀 수의 합) — 동기·비동기 판단용 어림값.</summary>
    public long EstimatedWork { get; }

    /// <summary>
    /// 켜진 스크립트 효과가 있다 — 계산 시간을 미리 알 수 없고 한도까지 걸릴 수 있으므로(13.4) 화면 스레드에서 바로 계산하지 않는다.
    /// </summary>
    public bool RunsScripts { get; }

    /// <summary>순서표 내용 열쇠(같은 셀 참조·같은 효과 목록·같은 자리면 같음) — 캐시용.</summary>
    internal CacheKey ContentKey => _contentKey ??= new CacheKey([Width, Height, .. _steps.Select(s => (object?)s.Key)]);

    /// <summary>새 그림에 그린다.</summary>
    public PixelImage Render(RenderPlanOptions? options = null)
    {
        var target = new PixelImage(Width, Height);
        RenderInto(target, options);
        return target;
    }

    /// <summary><paramref name="target"/>(<see cref="Width"/>×<see cref="Height"/>)을 지우고 그린다.</summary>
    public void RenderInto(PixelImage target, RenderPlanOptions? options = null)
    {
        if (target.Width != Width || target.Height != Height)
            throw new ArgumentException(Gpixel.Core.Localization.DomainStrings.Project_TargetCanvasSizeMismatch, nameof(target));
        target.Clear();
        var run = new RenderRun(target, options ?? RenderPlanOptions.Default, WorkUnits);
        foreach (var step in _steps)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            step.Run(run, target, 0, 0, null);
        }
        run.Finish();
    }
}

/// <summary>렌더링 한 번의 실행 상태(진행·중간 그림·취소·캐시). 한 스레드에서만 쓴다.</summary>
internal sealed class RenderRun
{
    private readonly PixelImage _root;
    private readonly IRenderObserver? _observer;
    private readonly int _total;
    private int _done;
    private (PixelImage Image, int X, int Y, byte Opacity)? _inProgress;

    public RenderRun(PixelImage root, RenderPlanOptions options, int total)
    {
        _root = root;
        _observer = options.Observer;
        _total = total;
        Cancellation = options.Cancellation;
        Cache = options.Cache;
        CheckpointLayer = options.CheckpointLayer;
    }

    public CancellationToken Cancellation { get; }
    public EffectRenderCache? Cache { get; }
    public Layer? CheckpointLayer { get; }

    /// <summary>
    /// 지금 계산 중인 그림(아직 바깥 결과에 합성 전)과 바깥 결과 기준 자리·불투명도. 중간 그림을 만들 때 위에 얹는다. null이면 없음.
    /// </summary>
    public void SetInProgress(PixelImage? image, int x, int y, byte opacity)
        => _inProgress = image is null ? null : (image, x, y, opacity);

    /// <summary>작업 <paramref name="units"/>개를 끝냈다(관찰자에게 센 값 그대로 알림). 관찰자가 원하면 중간 그림을 넘긴다.</summary>
    public void Done(int units)
    {
        _done += units;
        if (_observer is null) return;
        _observer.Progress(_done, _total);
        if (!_observer.WantsSnapshot) return;
        var copy = _root.Clone();
        if (_inProgress is { } p) copy.Composite(p.Image, p.X, p.Y, p.Opacity);
        _observer.Snapshot(copy);
    }

    public void Finish()
    {
        _inProgress = null;
        _observer?.Progress(_total, _total);
    }

    /// <summary>
    /// 효과 실행에 끼울 것(취소·캐시·효과마다 진행 알림 + <paramref name="present"/>로 중간 그림 자리 알림, 효과가 붙은 레이어 <paramref name="owner"/>).
    /// </summary>
    public EffectRunHooks Hooks(Action<PixelImage>? present, object? checkpointScope, Layer owner) => new()
    {
        Cancellation = Cancellation,
        Owner = owner,
        Cache = Cache,
        CheckpointScope = Cache is null ? null : checkpointScope,
        EffectDone = image =>
        {
            present?.Invoke(image);
            Done(1);
        },
    };
}

/// <summary>순서표의 한 단계.</summary>
internal abstract class RenderStep
{
    /// <summary>진행 단위 수.</summary>
    public abstract int Units { get; }

    /// <summary>계산량 어림값.</summary>
    public abstract long Work { get; }

    /// <summary>내용 열쇠(캐시용).</summary>
    public abstract CacheKey Key { get; }

    /// <summary>이 프레임에 켜진 스크립트 효과를 그린다.</summary>
    public abstract bool RunsScripts { get; }

    /// <summary>
    /// <paramref name="target"/>의 (<paramref name="ox"/>, <paramref name="oy"/>)를 원점으로 그린다.
    /// <paramref name="present"/>: 이 단계가 바깥 그룹 안이면 그룹 버퍼를 중간 그림으로 알리는 함수(null = 가장 바깥).
    /// </summary>
    public abstract void Run(RenderRun run, PixelImage target, int ox, int oy, Action? present);

    /// <summary>프레임 <paramref name="frame"/>에 켜진(범위 안) 효과 수.</summary>
    protected static int ActiveUnits(IReadOnlyList<LayerEffect> effects, int frame)
    {
        int n = 0;
        foreach (var e in effects)
            if (e.Enabled && e.AppliesTo(frame)) n++;
        return n;
    }

    /// <summary>프레임 <paramref name="frame"/>에 켜진(범위 안) 스크립트 효과가 있는지.</summary>
    protected static bool HasScripts(IReadOnlyList<LayerEffect> effects, int frame)
    {
        foreach (var e in effects)
            if (e is ScriptEffect { Enabled: true } && e.AppliesTo(frame)) return true;
        return false;
    }

    /// <summary>효과를 거치는 픽셀 수 어림(잔상은 앞 프레임을 다른 효과로 그리는 몫까지).</summary>
    protected static long EffectWork(IReadOnlyList<LayerEffect> effects, int frame, long pixels)
    {
        int others = 0;
        foreach (var e in effects)
            if (e.Enabled && e.AppliesTo(frame) && e is not AfterimageEffect) others++;
        long work = 0;
        foreach (var e in effects)
        {
            if (!e.Enabled || !e.AppliesTo(frame)) continue;
            work += e is AfterimageEffect a ? pixels * a.Count * (1 + others) : pixels;
        }
        return work;
    }
}

/// <summary>
/// 잔상이 볼 다른 프레임: 프레임마다의 원본(일반 레이어·부위는 셀 참조, 그룹은 부위를 합치는 작은 순서표)과 피벗.
/// 순서표를 만들 때 담아 두므로 그리는 동안 프로젝트를 읽지 않는다.
/// </summary>
internal sealed class FrameSources
{
    private readonly Layer _layer;
    private readonly PixelImage?[]? _cels;
    private readonly RenderPlan?[]? _groupSources;
    private readonly PointF[] _pivots;

    public FrameSources(Layer layer, PixelImage?[]? cels, RenderPlan?[]? groupSources, PointF[] pivots)
    {
        _layer = layer;
        _cels = cels;
        _groupSources = groupSources;
        _pivots = pivots;
    }

    /// <summary>프레임 피벗(범위 밖이면 가까운 끝 — <see cref="SpriteProject.EffectPivot"/>와 같다).</summary>
    public PointF PivotAt(int frame) => _pivots[Math.Clamp(frame, 0, _pivots.Length - 1)];

    /// <summary>프레임 번호 → 그 프레임 원본(범위 밖·빈 셀이면 null). 그룹 원본은 캐시가 있으면 다시 쓴다.</summary>
    public Func<int, PixelImage?> SourceFor(RenderRun run) => f =>
    {
        if ((uint)f >= (uint)_pivots.Length) return null;
        if (_cels is not null) return _cels[f];
        if (_groupSources?[f] is not { IsEmpty: false } plan) return null;
        var options = new RenderPlanOptions { Cancellation = run.Cancellation, Cache = run.Cache };
        return run.Cache is { } cache ? cache.GroupSource(_layer, f, plan, () => plan.Render(options)) : plan.Render(options);
    };
}

/// <summary>
/// 셀 하나를 그 레이어 효과(이 프레임 범위 안인 것)까지 입혀 (x, y)에 불투명도로 합성한다.
/// <paramref name="clip"/>이면 합성 대신 셀 크기로 잘라 그대로 옮긴다(<see cref="LayerEffects.RenderClipped"/>와 같음 — 셀 썸네일).
/// </summary>
internal sealed class CelStep(Layer layer, PixelImage cel, IReadOnlyList<LayerEffect> layerEffects, int frame, int frameCount, PointF pivot,
    FrameSources? sources, byte opacity, int x, int y, bool clip = false) : RenderStep
{
    public override int Units { get; } = (clip ? 0 : 1) + ActiveUnits(layerEffects, frame);

    public override long Work { get; } = (long)cel.Width * cel.Height + EffectWork(layerEffects, frame, (long)cel.Width * cel.Height);

    public override CacheKey Key { get; } = new(layer, cel, layerEffects, frame, frameCount, pivot, opacity, x, y, clip);

    public override bool RunsScripts { get; } = HasScripts(layerEffects, frame);

    public override void Run(RenderRun run, PixelImage target, int ox, int oy, Action? present)
    {
        var effects = LayerEffects.At(layerEffects, frame, frameCount, pivot, sources?.SourceFor(run),
            sources is null ? null : f => sources.PivotAt(f), run.Cache);
        int px = ox + x, py = oy + y;
        if (clip)
        {
            if (!LayerEffects.AnyEnabled(effects))
            {
                target.Blit(cel, px, py);
                return;
            }
            var (clipped, cx, cy) = LayerEffects.Render(cel, effects, LayerEffects.Padding(effects, cel.Width, cel.Height),
                run.Hooks(null, CheckpointScope(run), layer));
            target.Blit(clipped, new Rectangle(-cx, -cy, cel.Width, cel.Height), px, py);
            return;
        }
        var padding = LayerEffects.UsesOtherFrames(effects)
            ? LayerEffects.Padding(layerEffects, cel.Width, cel.Height)
            : LayerEffects.Padding(effects, cel.Width, cel.Height);
        Action<PixelImage>? show = present is not null
            ? _ => present()
            : image => run.SetInProgress(image, px - padding.Left, py - padding.Top, opacity);
        var (result, dx, dy) = LayerEffects.Render(cel, effects, padding, run.Hooks(show, CheckpointScope(run), layer));
        target.Composite(result, px + dx, py + dy, opacity);
        if (present is null) run.SetInProgress(null, 0, 0, 0);
        else present();
        run.Done(1);
    }

    private object? CheckpointScope(RenderRun run)
        => ReferenceEquals(run.CheckpointLayer, layer) ? new CacheKey("cel", layer, cel, frame) : null;
}

/// <summary>
/// 효과가 켜진 그룹: 부위들을 효과 여백만큼 넓힌 캔버스에 먼저 합친 뒤 그룹 효과(이 프레임 범위 안인 것)를 입혀 그룹 불투명도로 그린다
/// (캔버스 밖은 잘림).
/// </summary>
internal sealed class GroupStep(Layer group, IReadOnlyList<CelStep> parts, IReadOnlyList<LayerEffect> groupEffects, int frame, int frameCount,
    PointF pivot, FrameSources? sources, byte opacity, int width, int height) : RenderStep
{
    public override int Units { get; } = parts.Sum(p => p.Units) + ActiveUnits(groupEffects, frame) + 1;

    public override long Work { get; } = parts.Sum(p => p.Work) + EffectWork(groupEffects, frame, (long)width * height);

    public override CacheKey Key { get; } = new([group, groupEffects, frame, frameCount, pivot, opacity, width, height, .. parts.Select(p => (object?)p.Key)]);

    public override bool RunsScripts { get; } = HasScripts(groupEffects, frame) || parts.Any(p => p.RunsScripts);

    public override void Run(RenderRun run, PixelImage target, int ox, int oy, Action? present)
    {
        var effects = LayerEffects.At(groupEffects, frame, frameCount, pivot, sources?.SourceFor(run),
            sources is null ? null : f => sources.PivotAt(f), run.Cache);
        var pad = LayerEffects.Padding(LayerEffects.UsesOtherFrames(effects) ? groupEffects : effects, width, height);
        var buffer = new PixelImage(width + pad.Left + pad.Right, height + pad.Top + pad.Bottom);
        int bx = ox - pad.Left, by = oy - pad.Top;
        Action show = present ?? (() => run.SetInProgress(buffer, bx, by, opacity));
        foreach (var part in parts)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            part.Run(run, buffer, pad.Left, pad.Top, show);
        }
        var scope = ReferenceEquals(run.CheckpointLayer, group) ? new CacheKey("group", group, frame, new CacheKey([.. parts.Select(p => (object?)p.Key)])) : null;
        LayerEffects.Apply(buffer, effects, new Rectangle(pad.Left, pad.Top, width, height), run.Hooks(_ => show(), scope, group));
        target.Composite(buffer, bx, by, opacity);
        if (present is null) run.SetInProgress(null, 0, 0, 0);
        else present();
        run.Done(1);
    }
}
