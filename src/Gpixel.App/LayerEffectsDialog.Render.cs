using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.App;

/// <summary>
/// 효과 창의 프레임별 결과: 효과가 바뀔 때마다 모든 프레임(늘린 뒤 차례)을 작업 스레드에서 한 번씩 그려
/// 프레임 줄 썸네일, 전·후 미리보기, 확인 때 늘릴 캔버스 범위가 같은 결과를 함께 쓴다. 미리보기 프레임부터 그리고,
/// 짧게 끝나면(<see cref="QuickWait"/>) 바로 반영해 작은 그림에서는 지금과 똑같이 보인다. 그리는 동안 창은 멈추지 않고,
/// 아직 안 그린 칸·미리보기는 이전 그림 위에 회색 덮개를 씌운다.
/// </summary>
public sealed partial class LayerEffectsDialog
{
    /// <summary>프레임 줄 썸네일 긴 변 최대 픽셀(칸은 44px — 화면 배율 2배까지 또렷하게).</summary>
    private const int StripThumbMax = 96;

    /// <summary>효과를 바꾼 뒤 결과를 기다려 바로 반영하는 시간(이 안에 끝나면 덮개 없이 바뀐다).</summary>
    private static readonly TimeSpan QuickWait = TimeSpan.FromMilliseconds(25);

    /// <summary>모든 프레임을 다 그리는 데 이보다 오래 걸리는 그림이면 값·프레임 줄을 끄는 동안 다시 그리지 않고 놓을 때 한 번 그린다.</summary>
    private const double HeavyRenderMs = 50;

    /// <summary>재생용 전·후 그림 긴 변 최대 픽셀(미리보기 칸·큰 미리보기 창에 충분 — 더 큰 그림은 최근접으로 줄여 둔다).</summary>
    private const int ShownMax = 1024;

    /// <summary>프레임 하나의 결과: 효과 입힌 그림(모든 효과 여백만큼 넓힘)과 원래 그림 대비 자리, 범위 안 효과가 켜졌는지, 줄인 썸네일.</summary>
    private sealed record FrameResult(PixelImage Image, int X, int Y, bool Active, WriteableBitmap Thumb, Rectangle Opaque);

    /// <summary>효과 목록 한 판의 모든 프레임 결과(작업 스레드가 채움).</summary>
    private sealed class FrameRenders(int count)
    {
        public readonly FrameResult?[] Results = new FrameResult?[count];
        public readonly bool[] HasSource = new bool[count];
        /// <summary>프레임마다 효과 원본(재생용 그림을 만들 때 "전" 그림).</summary>
        public PixelImage?[] Sources = new PixelImage?[count];
        public readonly CancellationTokenSource Cts = new();
        public readonly long Started = Stopwatch.GetTimestamp();
        public int Remaining = count;
        /// <summary>모든 프레임을 다 그리는 데 걸린 시간(ms, 작업 스레드가 끝에 씀). 아직이거나 취소됐으면 -1.</summary>
        public double ElapsedMs = -1;
        public Task Task = Task.CompletedTask;
        /// <summary>끄는 중이라 그리지 않고 놓기를 기다리는 자리표(결과가 오지 않음, 덮개는 바로).</summary>
        public bool Held;
        /// <summary>다 그린 뒤 만든 재생용 전·후 그림(없으면 아직).</summary>
        public ShownFrames? Shown;

        public int Count => Results.Length;
        public bool Complete => Volatile.Read(ref Remaining) == 0;

        /// <summary>프레임 <paramref name="f"/>를 다 그렸는지(원본이 없는 프레임은 그릴 것이 없어 늘 끝남).</summary>
        public bool IsDone(int f) => (uint)f < (uint)Count && (!HasSource[f] || Volatile.Read(ref Results[f]) is not null);

        public double Progress => Count == 0 ? 1 : 1 - (double)Volatile.Read(ref Remaining) / Count;

        /// <summary>그리는 중인 지 <see cref="RenderService.OverlayDelay"/>가 지났는지(회색 덮개). 놓기를 기다리면 바로.</summary>
        public bool Overlay => Held || !Complete && Stopwatch.GetElapsedTime(Started) >= RenderService.OverlayDelay;

        /// <summary>계산을 멈춘다(썸네일은 줄에 넘긴 것은 줄이, 넘기지 않은 것은 <see cref="DropUnshown"/>가 버린다).</summary>
        public void Cancel() => Cts.Cancel();

        /// <summary>모든 프레임 결과를 반영했는지(UI 스레드만).</summary>
        public bool Applied;
    }

    /// <summary>
    /// 재생용 전·후 그림 묶음: 모든 프레임을 다 그린 뒤 작업 스레드에서 한 번 미리보기 범위로 잘라 비트맵으로 만들어 둔다 — 재생은 그림을 바꿔 끼우기만 한다
    /// (프레임마다 큰 그림을 자르고 비트맵으로 바꾸지 않게). 사본 칸처럼 원본이 같은 프레임은 "전" 그림을 같이 쓴다.
    /// </summary>
    private sealed class ShownFrames(Rectangle bounds, Rectangle canvas, int count)
    {
        public readonly Rectangle Bounds = bounds, Canvas = canvas;
        private readonly (WriteableBitmap Before, WriteableBitmap After)?[] _frames = new (WriteableBitmap, WriteableBitmap)?[count];
        private readonly List<WriteableBitmap> _owned = [];
        private bool _disposed;

        public (WriteableBitmap Before, WriteableBitmap After)? Get(int f)
        {
            lock (_owned) return (uint)f < (uint)_frames.Length ? _frames[f] : null;
        }

        /// <summary>작업 스레드: 프레임 <paramref name="f"/>의 그림을 넣는다(새로 만든 것은 <paramref name="created"/>). 이미 버렸으면 만든 것을 버린다.</summary>
        public void Put(int f, WriteableBitmap before, WriteableBitmap after, IEnumerable<WriteableBitmap> created)
        {
            lock (_owned)
            {
                if (_disposed)
                {
                    foreach (var b in created) b.Dispose();
                    return;
                }
                _owned.AddRange(created);
                _frames[f] = (before, after);
            }
        }

        public bool Disposed
        {
            get { lock (_owned) return _disposed; }
        }

        /// <summary>그림을 버린다. 미리보기가 보이고 있는 그림은 남겨 미리보기가 가진다(<paramref name="shown"/>).</summary>
        public void Dispose(Func<WriteableBitmap, bool> shown, Action adopt)
        {
            lock (_owned)
            {
                _disposed = true;
                bool kept = false;
                foreach (var b in _owned)
                {
                    if (shown(b)) kept = true;
                    else b.Dispose();
                }
                _owned.Clear();
                if (kept) adopt();
            }
        }
    }

    private FrameRenders? _renders;
    /// <summary>잔상용 앞 프레임 그림을 다른 프레임에서 다시 쓰는 캐시(창이 열린 동안 셀은 그대로라 효과만 바뀌면 그대로 쓸 수 있다).</summary>
    private readonly EffectRenderCache _effectCache = new(256L << 20);
    /// <summary>그리는 동안 회색 덮개·로딩 아이콘을 돌리려고 다시 그리는 타이머.</summary>
    private readonly DispatcherTimer _renderTimer = new() { Interval = RenderOverlay.FrameInterval };

    /// <summary>숫자 상자 값을 끄는 중인지, 프레임 줄을 끄는 중인지(누른 뒤 놓을 때까지).</summary>
    private bool _scrubbing, _stripDragging;
    /// <summary>끄는 동안 프레임 다시 그리기·캔버스 미리보기 효과를 미뤘는지(놓을 때 한 번).</summary>
    private bool _rendersHeld, _canvasHeld;
    /// <summary>지난번 모든 프레임을 다 그리는 데 걸린 시간(ms, -1 = 아직 모름).</summary>
    private double _lastRenderMs = -1;

    /// <summary>
    /// 끄는 중이고 무거운 그림이면(지난번 다 그리기가 <see cref="HeavyRenderMs"/>를 넘었거나 지금 그리는 것이 그만큼 걸리는 중) 바뀔 때마다 모든 프레임을
    /// 다시 그리지 않는다 — 끄는 동안 한 칸마다 모든 프레임을 처음부터 다시 그리기 시작해 작업 스레드가 쌓이고 창이 끊겼다. 가벼운 그림은 끄는 동안에도 바로 바뀐다.
    /// </summary>
    private bool HoldRenders => (_scrubbing || _stripDragging)
        && (_rendersHeld || _canvasHeld || _lastRenderMs > HeavyRenderMs
            || _renders is { Held: false, Complete: false } running && Stopwatch.GetElapsedTime(running.Started).TotalMilliseconds > HeavyRenderMs);

    /// <summary>끄기를 놓음: 미뤘던 캔버스 미리보기 효과와 모든 프레임 다시 그리기를 한 번 한다.</summary>
    private void ReleaseHold()
    {
        if (_scrubbing || _stripDragging) return;
        if (_canvasHeld)
        {
            _canvasHeld = false;
            int index = _project.IndexOf(_layer);
            if (index >= 0) _project.PreviewLayerEffects(index, _effects);
        }
        if (!_rendersHeld) return;
        _rendersHeld = false;
        RestartRenders();
    }

    /// <summary>끄는 동안: 이전 계산을 멈추고 놓기를 기다리는 자리표를 둔다(칸 수는 바로 맞추고, 그림은 이전 것 위에 덮개).</summary>
    private void HoldForRelease()
    {
        var previous = _renders;
        previous?.Cancel();
        int count = VirtualCount;
        var job = new FrameRenders(count) { Held = true };
        for (int f = 0; f < count; f++) job.HasSource[f] = VirtualSourceAt(f) is not null;
        _renders = job;
        _rendersHeld = true;
        SyncStripFrames();
        DropUnshown(previous);
        _strip.Pending = true;
        RefreshPreview();
    }

    /// <summary>
    /// 효과·프레임 늘리기가 바뀜: 지금 효과로 모든 프레임을 다시 그리기 시작한다(이전 계산은 취소, 이전 그림은 새 그림이 올 때까지 보임).
    /// 무거운 그림을 끄는 중이면 놓을 때까지 미룬다(<see cref="HoldRenders"/>).
    /// </summary>
    private void RestartRenders()
    {
        if (HoldRenders)
        {
            HoldForRelease();
            return;
        }
        var previous = _renders;
        previous?.Cancel();
        // 처음(아직 모든 프레임 범위를 모름): 미리보기 자리·배율은 지금 캔버스 — 다 그리면 효과 범위로 맞춘다
        if (_previewBounds.IsEmpty) _previewBounds = _previewCanvas = new Rectangle(0, 0, _project.Width, _project.Height);
        int count = VirtualCount;
        var all = VirtualEffects();
        var sources = new PixelImage?[count];
        var pivots = new System.Drawing.PointF[count];
        for (int f = 0; f < count; f++)
        {
            sources[f] = VirtualSourceAt(f);
            pivots[f] = _project.EffectPivot(_layer, OriginalOf(f));
        }
        bool others = LayerEffects.UsesOtherFrames(all);
        var job = new FrameRenders(count) { Sources = sources };
        for (int f = 0; f < count; f++) job.HasSource[f] = sources[f] is not null;
        int start = Math.Clamp(_previewFrame, 0, Math.Max(0, count - 1));
        var token = job.Cts.Token;
        var cache = _effectCache;
        var layer = _layer;
        job.Task = Task.Run(() =>
        {
            try
            {
                for (int k = 0; k < count; k++)
                {
                    token.ThrowIfCancellationRequested();
                    int f = (start + k) % count;
                    if (sources[f] is { } source)
                    {
                        var effects = LayerEffects.At(all, f, count, pivots[f],
                            others ? i => (uint)i < (uint)count ? sources[i] : null : null,
                            i => pivots[Math.Clamp(i, 0, count - 1)], cache);
                        var (image, x, y) = LayerEffects.Render(source, effects, LayerEffects.Padding(all, source.Width, source.Height), layer, token);
                        var thumb = ImageUtil.ToBitmap(RenderService.Shrink(image, StripThumbMax));
                        Volatile.Write(ref job.Results[f], new FrameResult(image, x, y, LayerEffects.AnyEnabled(effects), thumb, image.GetOpaqueBounds()));
                    }
                    Interlocked.Decrement(ref job.Remaining);
                    Dispatcher.UIThread.Post(() => OnFrameRendered(job, f));
                }
                Volatile.Write(ref job.ElapsedMs, Stopwatch.GetElapsedTime(job.Started).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                // 효과가 또 바뀌어 새 계산이 시작됨
            }
            catch (Exception ex)
            {
                var info = ExceptionDispatchInfo.Capture(ex);
                Dispatcher.UIThread.Post(info.Throw);
            }
        }, token);
        _renders = job;
        SyncStripFrames();
        DropUnshown(previous);
        // 가벼운 계산은 여기서 끝나므로 창이 예전처럼 바로 바뀐다(무거우면 기다리지 않고 이어서 작업 스레드가 그린다).
        // 지난 계산이 이 시간 안에 끝나지 않았으면(무거운 그림 — 값을 끄는 동안 바꿀 때마다 기다리기만 하게 됨) 기다리지 않는다.
        bool slow = previous is not null && Volatile.Read(ref previous.ElapsedMs) is var ms && (ms < 0 || ms > QuickWait.TotalMilliseconds);
        if (!slow)
        {
            try { job.Task.Wait(QuickWait); }
            catch (AggregateException) { }
        }
        ApplyRendered(job);
    }

    /// <summary>지난 계산이 만든 썸네일 중 프레임 줄에 넘기지 않은 것과 재생용 그림을 버린다(계산이 아직 돌면 남은 것은 도착할 때 버린다).</summary>
    private void DropUnshown(FrameRenders? job)
    {
        if (job is null) return;
        job.Cancel();
        foreach (var r in job.Results)
            if (r is not null && !_strip.Shows(r.Thumb)) r.Thumb.Dispose();
        job.Shown?.Dispose(_preview.Shows, _preview.Adopt);
    }

    /// <summary>작업 스레드가 프레임 하나를 끝냄(UI 스레드).</summary>
    private void OnFrameRendered(FrameRenders job, int f)
    {
        if (!ReferenceEquals(job, _renders))
        {
            if (job.Results[f] is { } stale && !_strip.Shows(stale.Thumb)) stale.Thumb.Dispose();
            return;
        }
        if (job.Applied) return; // 빨리 끝나 이미 모두 반영함
        if (job.Results[f] is { } r) _strip.SetThumb(f, r.Thumb, r.Image.Width, r.Image.Height);
        if (f == _previewFrame) RefreshPreview();
        if (job.Complete) OnRendersComplete(job);
    }

    /// <summary>지금까지 끝난 프레임을 모두 반영한다(빨리 끝났을 때).</summary>
    private void ApplyRendered(FrameRenders job)
    {
        for (int f = 0; f < job.Count; f++)
            if (job.Results[f] is { } r) _strip.SetThumb(f, r.Thumb, r.Image.Width, r.Image.Height);
        if (job.Complete) OnRendersComplete(job);
        else
        {
            RefreshPreview();
            if (!_renderTimer.IsEnabled) _renderTimer.Start();
        }
    }

    /// <summary>모든 프레임을 그림: 확인 때 늘릴 캔버스 범위를 정하고 미리보기를 그 자리·배율로 다시 그린 뒤, 재생용 그림을 작업 스레드에서 만든다.</summary>
    private void OnRendersComplete(FrameRenders job)
    {
        job.Applied = true;
        _lastRenderMs = Stopwatch.GetElapsedTime(job.Started).TotalMilliseconds;
        _renderTimer.Stop();
        _strip.Pending = false;
        _preview.SetPending(false, 1);
        SyncCanvasExpansion();
        RefreshPreview();
        RefreshScriptStatus();
        BuildShownFrames(job);
    }

    /// <summary>
    /// 재생용 전·후 그림을 지금 미리보기 범위로 작업 스레드에서 만든다(<see cref="ShownFrames"/>). 범위가 바뀌면(캔버스 늘리기를 켜고 끔) 다시 만든다.
    /// </summary>
    private void BuildShownFrames(FrameRenders job)
    {
        if (!ReferenceEquals(job, _renders) || !job.Complete) return;
        var (bounds, canvas) = (_previewBounds, _previewCanvas);
        if (job.Shown is { } old)
        {
            if (old.Bounds == bounds && old.Canvas == canvas) return;
            old.Dispose(_preview.Shows, _preview.Adopt);
        }
        var shown = job.Shown = new ShownFrames(bounds, canvas, job.Count);
        var offset = PreviewOffset;
        var token = job.Cts.Token;
        _ = Task.Run(() =>
        {
            var befores = new Dictionary<PixelImage, WriteableBitmap>(ReferenceEqualityComparer.Instance);
            for (int f = 0; f < job.Count && !token.IsCancellationRequested && !shown.Disposed; f++)
            {
                if (job.Sources[f] is not { } source || Volatile.Read(ref job.Results[f]) is not { } result) continue;
                var created = new List<WriteableBitmap>(2);
                if (!befores.TryGetValue(source, out var before))
                {
                    before = befores[source] = ImageUtil.ToBitmap(RenderService.Shrink(PreviewBefore(source, bounds, offset), ShownMax));
                    created.Add(before);
                }
                var after = ImageUtil.ToBitmap(RenderService.Shrink(PreviewAfter(result, bounds, offset), ShownMax));
                created.Add(after);
                shown.Put(f, before, after, created);
            }
        }, token);
    }

    /// <summary>회색 덮개가 생기거나 로딩 아이콘이 돌 때 다시 그린다.</summary>
    private void OnRenderTimer()
    {
        if (_renders is not { } job || job.Complete)
        {
            _renderTimer.Stop();
            return;
        }
        _strip.Pending = job.Overlay;
        _preview.SetPending(job.Overlay && !job.IsDone(_previewFrame), job.Progress);
        _largePreview?.InvalidateVisual();
    }

    /// <summary>
    /// 늘린 뒤 모든 프레임에서 이 레이어의 켜진 효과 결과가 차지하는 캔버스 좌표 범위 — 다 그린 결과가 있으면 그것으로, 아직이면 null.
    /// </summary>
    private Rectangle? RenderedOutputBounds()
    {
        if (_renders is not { Complete: true } job) return null;
        var bounds = new Rectangle(0, 0, _project.Width, _project.Height);
        int ox = _layer.IsPart ? _layer.PartOffset.X : 0;
        int oy = _layer.IsPart ? _layer.PartOffset.Y : 0;
        foreach (var r in job.Results)
        {
            if (r is not { Active: true }) continue;
            var opaque = r.Opaque;
            if (opaque.IsEmpty) continue;
            opaque.Offset(ox + r.X, oy + r.Y);
            bounds = Rectangle.Union(bounds, opaque);
        }
        return bounds;
    }

    /// <summary>프레임 <paramref name="f"/>의 결과(아직이면 null).</summary>
    private FrameResult? RenderedFrame(int f) => _renders is { } job && (uint)f < (uint)job.Count ? Volatile.Read(ref job.Results[f]) : null;

    /// <summary>프레임 <paramref name="f"/>를 아직 그리는 중인지.</summary>
    private bool IsRendering(int f) => _renders is { } job && !job.IsDone(f);

    private void DisposeRenders()
    {
        _renderTimer.Stop();
        DropUnshown(_renders);
        _renders = null;
        _effectCache.Clear();
    }
}
