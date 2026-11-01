using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.App;

/// <summary>화면에 그릴 합성 결과의 종류.</summary>
internal enum RenderKind
{
    /// <summary>프레임 전체 합성(보이는 레이어·그룹).</summary>
    Composite,
    /// <summary>프레임 합성에서 부위 하나(<see cref="RenderKey.Layer"/>)를 뺀 것.</summary>
    Context,
    /// <summary>셀 하나(그룹이면 그룹 합성, 일반 레이어면 효과 입힌 셀, 부위면 셀 그대로) — 원본 크기.</summary>
    Cel,
    /// <summary><see cref="Cel"/>과 같은 그림을 타임라인 칸 크기로 줄인 것.</summary>
    Thumb,
}

/// <summary>결과 하나의 열쇠(프레임·레이어는 객체로 — 순서가 바뀌어도 따라감).</summary>
internal readonly record struct RenderKey(RenderKind Kind, Frame Frame, Layer? Layer = null);

/// <summary>계산 차례(작을수록 먼저).</summary>
public enum RenderPriority
{
    /// <summary>캔버스에 보이는 지금 프레임, 재생이 기다리는 다음 프레임.</summary>
    Current = 0,
    /// <summary>곧 볼 프레임(재생 미리 계산, 양파 껍질).</summary>
    Soon = 1,
    /// <summary>타임라인 칸 썸네일·멈춘 미리보기처럼 조금 늦어도 되는 것.</summary>
    Background = 2,
}

/// <summary>
/// 지금 그릴 수 있는 것: 비트맵(최신 결과, 계산 중이면 이전 결과 또는 중간 그림 — 없으면 null), 계산 중인지,
/// 회색 덮개를 씌울지(계산이 <see cref="RenderService.OverlayDelay"/>를 넘김), 진행(0~1), 원래 그림 크기(썸네일은 비트맵보다 큼).
/// </summary>
public readonly record struct RenderView(WriteableBitmap? Bitmap, bool Pending, bool Overlay, double Progress, int SourceWidth, int SourceHeight)
{
    public static readonly RenderView None = default;
}

/// <summary>
/// 합성·효과 결과를 계산해 두는 곳. 가벼운 계산은 부를 때 바로(UI 스레드, 지금과 같음) 하고, 무거운 계산은 작업 스레드에서 한다.
/// 작업 스레드 계산 중에는 이전 결과를 보여 주고, 결과가 오면 <see cref="Updated"/>로 알린다. 바뀐 입력의 결과는 <see cref="Invalidate"/>로
/// 무효로 하고(진행 중 계산은 취소), 다시 부를 때 새로 계산한다. 결과는 바이트 예산 안에서 오래 안 쓴 것부터 버린다.
/// 모든 공개 메서드는 UI 스레드에서만 부른다.
/// </summary>
internal sealed class RenderService
{
    /// <summary>이보다 오래 계산 중이면 회색 덮개를 씌운다(짧은 계산은 깜빡이지 않게).</summary>
    public static readonly TimeSpan OverlayDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>타임라인 썸네일의 긴 변 최대 픽셀.</summary>
    public const int ThumbMaxSize = 128;

    /// <summary>재 본 계산 시간이 이 안이면 바로 계산한다.</summary>
    private const double SyncMs = 10;

    /// <summary>아직 재 보지 않은 계산은 어림값(<see cref="RenderPlan.EstimatedWork"/>)이 이 안이면 바로 계산한다(약 5ms).</summary>
    private const long SyncWork = 3_000_000;

    /// <summary>UI 스레드 한 차례에 바로 계산하는 데 쓸 수 있는 시간(넘으면 나머지는 작업 스레드로).</summary>
    private const double TurnBudgetMs = 16;

    /// <summary>중간 그림을 보내는 최소 간격.</summary>
    private const long SnapshotIntervalMs = 33;

    private readonly Dictionary<RenderKey, Entry> _entries = new();
    private readonly object _queueLock = new();
    private readonly List<Job> _queue = new();
    /// <summary>작업 스레드가 지금 계산 중인 것(<see cref="_queueLock"/>).</summary>
    private readonly List<Job> _running = new();
    private readonly int _maxWorkers = Math.Max(1, Environment.ProcessorCount / 2);
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _workers;
    private long _sequence;
    private double _turnSpentMs;
    private bool _turnResetPosted;

    public RenderService()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) available = 8L << 30;
        BudgetBytes = Math.Clamp(available / 4, 256L << 20, 2L << 30);
        Effects = new EffectRenderCache(Math.Clamp(available / 8, 128L << 20, 1L << 30));
        _overlayTimer.Tick += (_, _) => OnOverlayTick();
        WorkOverlay.Idle += Kick;
    }

    /// <summary>결과·중간 그림이 왔거나 회색 덮개가 생길 때(다시 그리기).</summary>
    public event Action? Updated;

    /// <summary>잔상 원본·효과 중간 결과 캐시(작업 스레드와 같이 씀).</summary>
    public EffectRenderCache Effects { get; }

    /// <summary>결과 비트맵·그림의 바이트 예산.</summary>
    public long BudgetBytes { get; }

    /// <summary>
    /// 효과 1~k까지의 중간 결과를 담을 레이어(효과를 고치는 중인 레이어 — 지금 프레임 합성에만 씀). null이면 담지 않는다.
    /// </summary>
    public Layer? CheckpointLayer { get; set; }

    private sealed class Entry(RenderKey key)
    {
        public RenderKey Key { get; } = key;
        /// <summary>입력이 바뀔 때마다 늘어남.</summary>
        public int Version;
        /// <summary>지금 가진 결과가 어느 입력 판의 것인지(-1 = 없음). <see cref="Version"/>과 같으면 최신.</summary>
        public int ResultVersion = -1;
        public PixelImage? Image;
        public WriteableBitmap? Bitmap;
        public int SourceWidth, SourceHeight;
        public WriteableBitmap? Snapshot;
        public Job? Job;
        public double CostMs = -1;
        public long LastUse;
        /// <summary>계산 중이 된 시각(Stopwatch 틱, 0 = 계산 중 아님). 편집이 이어지면(결과 전에 다시 무효) 그대로 둔다.</summary>
        public long PendingSince;
        public bool OverlayShown;

        public bool Fresh => ResultVersion == Version;

        public long Bytes => (Image is { } i ? (long)i.Width * i.Height * 4 : 0) + BitmapBytes(Bitmap) + BitmapBytes(Snapshot);

        public void DropSnapshot()
        {
            Snapshot?.Dispose();
            Snapshot = null;
        }
    }

    private static long BitmapBytes(WriteableBitmap? b) => b is null ? 0 : (long)b.PixelSize.Width * b.PixelSize.Height * 4;

    // ───────────── 부르기 ─────────────

    /// <summary>
    /// <paramref name="key"/>의 그릴 것. 최신 결과가 없으면 <paramref name="plan"/>(UI 스레드에서 지금 상태로 만든 순서표, null = 그릴 것 없음)으로
    /// 바로 계산하거나 작업 스레드에 맡긴다. <paramref name="live"/>면 계산 중 중간 그림도 받는다.
    /// </summary>
    public RenderView Get(RenderKey key, RenderPriority priority, Func<RenderPlan?> plan, bool live = false)
    {
        var entry = EntryOf(key);
        if (!entry.Fresh)
        {
            if (entry.Job is { } running) running.Raise(priority);
            else Start(entry, plan(), priority, live);
        }
        return ViewOf(entry, live);
    }

    /// <summary>
    /// <paramref name="key"/>의 최신 결과 그림(프레임 합성처럼 그림을 가지는 종류만). 없으면 지금 이 자리에서 계산한다(계산 중인 것은 기다리지 않고 버림).
    /// 스포이트·채우기처럼 정확한 그림이 바로 필요할 때.
    /// </summary>
    public PixelImage? GetImage(RenderKey key, Func<RenderPlan?> plan)
    {
        var entry = EntryOf(key);
        if (entry.Fresh && entry.Image is not null) return entry.Image;
        CancelJob(entry);
        var built = plan();
        var sw = Stopwatch.StartNew();
        var image = built?.Render(new RenderPlanOptions { Cache = Effects });
        Store(entry, image, image is null ? null : ImageUtil.ToBitmap(image), entry.Version, sw.Elapsed.TotalMilliseconds);
        return image;
    }

    /// <summary>지금 가진 결과 그림(최신이 아닐 수 있음, 없으면 null) — 상태 표시줄처럼 늦어도 되는 곳.</summary>
    public PixelImage? ShownImage(RenderKey key) => _entries.TryGetValue(key, out var e) ? e.Image : null;

    /// <summary>계산 중이거나 다시 계산해야 하는지(결과가 최신이 아님).</summary>
    public bool IsPending(RenderKey key) => _entries.TryGetValue(key, out var e) && !e.Fresh;

    /// <summary>최신 결과가 있는지.</summary>
    public bool IsReady(RenderKey key) => _entries.TryGetValue(key, out var e) && e.Fresh;

    /// <summary>계산 중인 작업이 하나라도 있는지.</summary>
    public bool Busy
    {
        get
        {
            foreach (var e in _entries.Values)
                if (e.Job is not null) return true;
            return false;
        }
    }

    private Entry EntryOf(RenderKey key)
    {
        if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry(key);
        entry.LastUse = Environment.TickCount64;
        return entry;
    }

    private RenderView ViewOf(Entry entry, bool live)
    {
        bool pending = !entry.Fresh;
        bool overlay = pending && entry.PendingSince != 0 && Stopwatch.GetElapsedTime(entry.PendingSince) >= OverlayDelay;
        entry.OverlayShown = overlay;
        // 예산 때문에 비트맵만 버렸으면 가진 그림으로 다시 만든다
        if (entry.Bitmap is null && entry.Image is not null) entry.Bitmap = ImageUtil.ToBitmap(entry.Image);
        var bitmap = live && pending && entry.Snapshot is not null ? entry.Snapshot : entry.Bitmap;
        return new RenderView(bitmap, pending, overlay, entry.Job?.Progress ?? (pending ? 0 : 1), entry.SourceWidth, entry.SourceHeight);
    }

    // ───────────── 계산 ─────────────

    private void Start(Entry entry, RenderPlan? plan, RenderPriority priority, bool live)
    {
        if (plan is null)
        {
            Store(entry, null, null, entry.Version, 0);
            return;
        }
        // 어림값이 크면(잔상처럼 캐시가 맞을 때만 빠른 계산 포함) 재 본 시간이 짧았어도 늘 작업 스레드로 — 효과가 바뀌면 캐시가 안 맞아 오래 걸릴 수 있다.
        // 스크립트 효과는 계산 시간을 미리 알 수 없고 한도(2초)까지 걸릴 수 있으므로 늘 작업 스레드로.
        bool cheap = !plan.RunsScripts && plan.EstimatedWork <= SyncWork && (entry.CostMs < 0 || entry.CostMs <= SyncMs);
        if (cheap && _turnSpentMs < TurnBudgetMs)
        {
            var sw = Stopwatch.StartNew();
            var image = plan.Render(new RenderPlanOptions { Cache = Effects });
            var (keep, bitmap, w, h) = Convert(entry.Key.Kind, image);
            double ms = sw.Elapsed.TotalMilliseconds;
            SpendTurn(ms);
            Store(entry, keep, bitmap, entry.Version, ms, w, h);
            return;
        }
        if (entry.PendingSince == 0) entry.PendingSince = Stopwatch.GetTimestamp();
        var options = new RenderPlanOptions
        {
            Cache = Effects,
            // 효과 1~k 중간 결과는 값을 바꾸며 보는 캔버스 프레임에만 담는다(재생·썸네일까지 담으면 복사 비용과 캐시 자리만 쓴다).
            CheckpointLayer = entry.Key.Kind == RenderKind.Composite && live ? CheckpointLayer : null,
        };
        var job = new Job(this, entry.Key, entry.Version, plan, options, live, priority, Interlocked.Increment(ref _sequence));
        entry.Job = job;
        lock (_queueLock)
        {
            _queue.Add(job);
            // 지금 프레임이 왔으면 돌고 있는 늦어도 되는 계산(썸네일)을 멈춘다 — 효과마다 모든 코어를 쓰므로 함께 돌면 지금 프레임이 그만큼 늦어진다.
            // 멈춘 것은 다음에 그 칸을 그릴 때 다시 맡긴다(Canceled).
            if (priority != RenderPriority.Background)
                foreach (var running in _running)
                    if (running.Priority == RenderPriority.Background) running.Cancel();
        }
        Kick();
        if (!_overlayTimer.IsEnabled) _overlayTimer.Start();
    }

    /// <summary>맡은 계산이 있으면 작업 스레드를 더 띄운다(할 일이 없거나 지금 할 수 없는 스레드는 바로 끝남).</summary>
    private void Kick()
    {
        lock (_queueLock)
        {
            int want = Math.Min(_maxWorkers, _running.Count + _queue.Count);
            while (_workers < want)
            {
                _workers++;
                Task.Run(WorkerLoop);
            }
        }
    }

    private void SpendTurn(double ms)
    {
        _turnSpentMs += ms;
        if (_turnResetPosted) return;
        _turnResetPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _turnSpentMs = 0;
            _turnResetPosted = false;
        }, DispatcherPriority.Background);
    }

    /// <summary>결과 종류에 맞게 보관할 그림·비트맵(썸네일은 줄임). 작업 스레드에서도 부른다.</summary>
    private static (PixelImage? Keep, WriteableBitmap Bitmap, int Width, int Height) Convert(RenderKind kind, PixelImage image)
    {
        switch (kind)
        {
            case RenderKind.Composite:
                return (image, ImageUtil.ToBitmap(image), image.Width, image.Height);
            case RenderKind.Thumb:
                return (null, ImageUtil.ToBitmap(Shrink(image, ThumbMaxSize)), image.Width, image.Height);
            default:
                return (null, ImageUtil.ToBitmap(image), image.Width, image.Height);
        }
    }

    /// <summary>긴 변이 <paramref name="max"/>를 넘으면 최근접으로 줄인다(그대로면 같은 그림).</summary>
    internal static PixelImage Shrink(PixelImage image, int max)
    {
        if (image.Width <= max && image.Height <= max) return image;
        double scale = (double)max / Math.Max(image.Width, image.Height);
        int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var small = new PixelImage(w, h);
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(image.Height - 1, (int)((y + 0.5) * image.Height / h));
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(image.Width - 1, (int)((x + 0.5) * image.Width / w));
                small.Pixels[y * w + x] = image.Pixels[sy * image.Width + sx];
            }
        }
        return small;
    }

    private void Store(Entry entry, PixelImage? image, WriteableBitmap? bitmap, int version, double ms, int width = 0, int height = 0)
    {
        if (!ReferenceEquals(entry.Bitmap, bitmap)) entry.Bitmap?.Dispose();
        entry.Image = image;
        entry.Bitmap = bitmap;
        entry.SourceWidth = width == 0 ? image?.Width ?? 0 : width;
        entry.SourceHeight = height == 0 ? image?.Height ?? 0 : height;
        entry.ResultVersion = version;
        if (ms > 0) entry.CostMs = ms;
        entry.DropSnapshot();
        if (entry.Fresh)
        {
            entry.PendingSince = 0;
            entry.OverlayShown = false;
        }
        Trim();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            Job? job;
            lock (_queueLock)
            {
                job = Pick();
                if (job is null)
                {
                    _workers--;
                    return;
                }
                _queue.Remove(job);
                _running.Add(job);
            }
            job.Execute();
            lock (_queueLock) _running.Remove(job);
            Kick();
        }
    }

    /// <summary>
    /// 다음에 할 계산(<see cref="_queueLock"/> 안에서): 차례가 가장 빠른 것. 늦어도 되는 계산(<see cref="RenderPriority.Background"/>)은
    /// 다른 계산이 돌지 않을 때 한 번에 하나만 한다 — 효과 계산이 안에서 여러 코어를 쓰므로 하나로도 코어가 찬다.
    /// 저장·열기 덮개가 떠 있는 동안(<see cref="WorkOverlay.IsActive"/>)은 새 계산을 시작하지 않는다(코어를 저장에 준다, 끝나면 <see cref="WorkOverlay.Idle"/>에 이어 감).
    /// </summary>
    private Job? Pick()
    {
        if (WorkOverlay.IsActive) return null;
        Job? best = null;
        foreach (var candidate in _queue)
            if (best is null || candidate.Priority < best.Priority || candidate.Priority == best.Priority && candidate.Sequence < best.Sequence)
                best = candidate;
        if (best is not { Priority: RenderPriority.Background }) return best;
        return _running.Count == 0 ? best : null;
    }

    /// <summary>작업이 취소됨(UI 스레드): 무효화로 취소된 것은 이미 떼었고, 지금 프레임에 자리를 내주려고 멈춘 것은 떼어 다음에 다시 맡기게 한다.</summary>
    private void Canceled(Job job)
    {
        if (!_entries.TryGetValue(job.Key, out var entry) || !ReferenceEquals(entry.Job, job)) return;
        entry.Job = null;
        entry.DropSnapshot();
        Updated?.Invoke();
    }

    /// <summary>
    /// 작업이 예외로 끝남(UI 스레드): 작업을 떼고, 이 입력 판은 다시 맡기지 않는다(같은 오류가 그릴 때마다 되풀이되지 않게) —
    /// 가진 그림을 최신으로 치고 덮개를 걷는다. 다음 무효화(편집)에서 다시 계산한다.
    /// </summary>
    private void Failed(Job job)
    {
        if (!_entries.TryGetValue(job.Key, out var entry) || !ReferenceEquals(entry.Job, job)) return;
        entry.Job = null;
        entry.DropSnapshot();
        if (job.Version == entry.Version)
        {
            entry.ResultVersion = job.Version;
            entry.PendingSince = 0;
            entry.OverlayShown = false;
        }
        if (!Busy) _overlayTimer.Stop();
        Updated?.Invoke();
    }

    /// <summary>작업 스레드가 끝낸 결과(UI 스레드).</summary>
    private void Complete(Job job, PixelImage? image, WriteableBitmap? bitmap, int width, int height, double ms)
    {
        if (!_entries.TryGetValue(job.Key, out var entry) || !ReferenceEquals(entry.Job, job))
        {
            bitmap?.Dispose();
            return;
        }
        entry.Job = null;
        if (job.Version == entry.Version || entry.Bitmap is null)
            Store(entry, image, bitmap, job.Version, ms, width, height);
        else
            bitmap?.Dispose();
        if (!Busy) _overlayTimer.Stop();
        Updated?.Invoke();
    }

    /// <summary>작업 스레드가 보낸 중간 그림(UI 스레드).</summary>
    private void Snapshot(Job job, WriteableBitmap bitmap)
    {
        if (!_entries.TryGetValue(job.Key, out var entry) || !ReferenceEquals(entry.Job, job))
        {
            bitmap.Dispose();
            return;
        }
        entry.Snapshot?.Dispose();
        entry.Snapshot = bitmap;
        Updated?.Invoke();
    }

    private void OnOverlayTick()
    {
        bool crossed = false;
        foreach (var e in _entries.Values)
            if (e.Job is not null && !e.OverlayShown && e.PendingSince != 0 && Stopwatch.GetElapsedTime(e.PendingSince) >= OverlayDelay)
                crossed = true;
        if (!Busy) _overlayTimer.Stop();
        if (crossed) Updated?.Invoke();
    }

    private void CancelJob(Entry entry)
    {
        if (entry.Job is not { } job) return;
        entry.Job = null;
        job.Cancel();
        lock (_queueLock) _queue.Remove(job);
        entry.DropSnapshot();
    }

    // ───────────── 무효화·예산 ─────────────

    /// <summary><paramref name="match"/>에 맞는 결과를 무효로 한다(가진 그림은 다시 계산될 때까지 보여 주고, 진행 중 계산은 취소).</summary>
    public void Invalidate(Func<RenderKey, bool> match)
    {
        foreach (var entry in _entries.Values)
        {
            if (!match(entry.Key)) continue;
            entry.Version++;
            CancelJob(entry);
        }
    }

    /// <summary>모든 결과를 무효로 한다(그림은 남김).</summary>
    public void InvalidateAll() => Invalidate(_ => true);

    /// <summary>모든 결과와 효과 캐시를 버린다(다른 프로젝트, 캔버스 크기 변경).</summary>
    public void Reset()
    {
        foreach (var entry in _entries.Values)
        {
            CancelJob(entry);
            entry.Bitmap?.Dispose();
        }
        _entries.Clear();
        Effects.Clear();
        _overlayTimer.Stop();
    }

    /// <summary>예산을 넘으면 오래 안 쓴 결과부터 버린다: 먼저 다시 만들 수 있는 비트맵(그림이 있는 것), 그다음 결과 전체. 방금 쓴 것은 남긴다.</summary>
    private void Trim()
    {
        long total = 0;
        foreach (var e in _entries.Values) total += e.Bytes;
        if (total <= BudgetBytes) return;
        long now = Environment.TickCount64;
        var order = _entries.Values.Where(e => now - e.LastUse > 1000).OrderBy(e => e.LastUse).ToList();
        foreach (var e in order)
        {
            if (total <= BudgetBytes) return;
            if (e.Image is not null && e.Bitmap is not null && e.Key.Kind == RenderKind.Composite)
            {
                total -= BitmapBytes(e.Bitmap);
                e.Bitmap.Dispose();
                e.Bitmap = null;
            }
        }
        foreach (var e in order)
        {
            if (total <= BudgetBytes) return;
            if (e.Job is not null) continue;
            total -= e.Bytes;
            e.Bitmap?.Dispose();
            _entries.Remove(e.Key);
        }
    }

    // ───────────── 작업 ─────────────

    private sealed class Job(RenderService owner, RenderKey key, int version, RenderPlan plan, RenderPlanOptions options, bool live,
        RenderPriority priority, long sequence) : IRenderObserver
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Stopwatch _clock = new();
        private long _lastSnapshot;
        private volatile int _priority = (int)priority;
        private double _progress;

        public RenderKey Key { get; } = key;
        public int Version { get; } = version;
        public long Sequence { get; } = sequence;
        public RenderPriority Priority => (RenderPriority)_priority;
        public double Progress => Volatile.Read(ref _progress);

        public void Raise(RenderPriority p)
        {
            if ((int)p < _priority) _priority = (int)p;
        }

        public void Cancel() => _cts.Cancel();

        public void Execute()
        {
            _clock.Start();
            try
            {
                var image = plan.Render(new RenderPlanOptions
                {
                    Cancellation = _cts.Token,
                    Cache = options.Cache,
                    CheckpointLayer = options.CheckpointLayer,
                    Observer = this,
                });
                var (keep, bitmap, w, h) = RenderService.Convert(Key.Kind, image);
                double ms = _clock.Elapsed.TotalMilliseconds;
                Dispatcher.UIThread.Post(() => owner.Complete(this, keep, bitmap, w, h, ms));
            }
            catch (OperationCanceledException)
            {
                // 무효가 되어 취소됐거나(새 계산이 이어서 옴) 지금 프레임에 자리를 내줌(다시 맡김)
                Dispatcher.UIThread.Post(() => owner.Canceled(this));
            }
            catch (Exception ex)
            {
                // 계산기 오류(메모리 부족 등): 항목을 풀어 덮개가 남지 않게 한 뒤 오류는 그대로 알린다(CrashGuard).
                var info = ExceptionDispatchInfo.Capture(ex);
                Dispatcher.UIThread.Post(() =>
                {
                    owner.Failed(this);
                    info.Throw();
                });
            }
        }

        void IRenderObserver.Progress(int done, int total) => Volatile.Write(ref _progress, total <= 0 ? 1 : Math.Clamp((double)done / total, 0, 1));

        bool IRenderObserver.WantsSnapshot => live && !_cts.IsCancellationRequested && _clock.ElapsedMilliseconds - _lastSnapshot >= SnapshotIntervalMs;

        void IRenderObserver.Snapshot(PixelImage image)
        {
            _lastSnapshot = _clock.ElapsedMilliseconds;
            var bitmap = ImageUtil.ToBitmap(image);
            Dispatcher.UIThread.Post(() => owner.Snapshot(this, bitmap));
        }
    }
}
