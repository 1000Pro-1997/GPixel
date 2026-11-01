using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>
/// 효과 계산 결과를 다시 쓰는 캐시(여러 스레드에서 함께 씀, 바이트 예산 안에서 가장 오래 안 쓴 것부터 버림).
/// <list type="bullet">
/// <item>잔상 원본: 그룹의 다른 프레임 원본(부위 합성)과, 앞 프레임에 다른 효과를 입힌 그림 — 이웃 프레임을 그릴 때 다시 쓴다.</item>
/// <item>중간 결과: 효과 1~k까지 입힌 그림 — k번째 효과 값만 바꾸면 k번째부터 다시 계산한다(<see cref="RenderPlanOptions"/>로 레이어를 정할 때만 담음).</item>
/// </list>
/// 열쇠에는 셀 이미지 참조와 효과 값이 들어가므로 효과만 바뀌면 그대로 써도 된다. 셀 픽셀·레이어 구조가 바뀌면 부르는 쪽이 <see cref="Clear"/>한다.
/// 꺼낸 그림은 읽기만 한다.
/// </summary>
public sealed class EffectRenderCache
{
    private readonly object _lock = new();
    private readonly Dictionary<object, LinkedListNode<Item>> _map = new();
    private readonly LinkedList<Item> _lru = new();
    private long _bytes;
    private long _maxBytes;
    /// <summary><see cref="Clear"/>할 때마다 늘어난다. 비우기 전에 시작한 계산의 결과는 담지 않는다(바뀌기 전 셀 픽셀로 만든 그림).</summary>
    private long _generation;

    private sealed record Item(object Key, object Value, long Bytes);

    public EffectRenderCache(long maxBytes) => _maxBytes = Math.Max(0, maxBytes);

    /// <summary>바이트 예산(넘으면 오래 안 쓴 것부터 버림).</summary>
    public long MaxBytes
    {
        get { lock (_lock) return _maxBytes; }
        set
        {
            lock (_lock)
            {
                _maxBytes = Math.Max(0, value);
                Trim();
            }
        }
    }

    /// <summary>지금 담긴 바이트.</summary>
    public long Bytes
    {
        get { lock (_lock) return _bytes; }
    }

    /// <summary>지금 세대 — 계산을 시작할 때 읽어 두었다가 담을 때 넘긴다.</summary>
    internal long Generation
    {
        get { lock (_lock) return _generation; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
            _generation++;
        }
    }

    private bool TryGet<T>(object key, out T value) where T : class
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node) && node.Value.Value is T found)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = found;
                return true;
            }
        }
        value = null!;
        return false;
    }

    /// <param name="generation">계산을 시작할 때의 <see cref="Generation"/>. 그 사이 비웠으면 담지 않는다.</param>
    private void Put(object key, object value, long bytes, long generation)
    {
        lock (_lock)
        {
            if (generation != _generation || bytes > _maxBytes) return;
            if (_map.Remove(key, out var old))
            {
                _lru.Remove(old);
                _bytes -= old.Value.Bytes;
            }
            _map[key] = _lru.AddFirst(new Item(key, value, bytes));
            _bytes += bytes;
            Trim();
        }
    }

    private void Trim()
    {
        while (_bytes > _maxBytes && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
        }
    }

    private static long BytesOf(PixelImage image) => (long)image.Width * image.Height * 4 + 64;

    private sealed class Boxed((PixelImage Image, int OffsetX, int OffsetY) value)
    {
        public (PixelImage Image, int OffsetX, int OffsetY) Value { get; } = value;
    }

    /// <summary>그룹 <paramref name="group"/>의 프레임 <paramref name="frame"/> 원본(부위 합성). <paramref name="plan"/>이 같으면 다시 쓴다.</summary>
    internal PixelImage GroupSource(Layer group, int frame, RenderPlan plan, Func<PixelImage> render)
    {
        var key = new CacheKey("source", group, frame, plan.ContentKey);
        if (TryGet<PixelImage>(key, out var found)) return found;
        long generation = Generation;
        var image = render();
        Put(key, image, BytesOf(image), generation);
        return image;
    }

    /// <summary>
    /// 잔상용으로 다른 효과를 입힌 앞 프레임 그림. <paramref name="render"/>는 받은 끼움으로 그리고, 그 실행에서 스크립트 효과가 실패했으면
    /// (<see cref="EffectRunHooks.ScriptFailed"/>) 결과를 담지 않는다 — 효과 값이 같아도 다음 실행은 다를 수 있으므로.
    /// </summary>
    internal (PixelImage Image, int OffsetX, int OffsetY) Ghost(CacheKey key, Func<EffectRunHooks, (PixelImage Image, int OffsetX, int OffsetY)> render)
    {
        if (TryGet<Boxed>(key, out var found)) return found.Value;
        long generation = Generation;
        var hooks = new EffectRunHooks();
        var value = render(hooks);
        if (!hooks.ScriptFailed) Put(key, new Boxed(value), BytesOf(value.Image), generation);
        return value;
    }

    /// <summary>효과 1~k까지 입힌 중간 결과.</summary>
    internal bool TryCheckpoint(CacheKey key, out PixelImage image) => TryGet(key, out image);

    /// <param name="generation">이 계산을 시작할 때의 <see cref="Generation"/>.</param>
    internal void PutCheckpoint(CacheKey key, PixelImage image, long generation) => Put(key, image, BytesOf(image), generation);
}

/// <summary>
/// 캐시 열쇠: 항목들을 차례대로 비교한다(값 형식·record는 값으로, 이미지·레이어 같은 객체는 참조로).
/// 효과 목록은 <see cref="EffectListKey"/>로 감싸 넣는다.
/// </summary>
internal sealed class CacheKey : IEquatable<CacheKey>
{
    private readonly object?[] _items;
    private readonly int _hash;

    public CacheKey(params object?[] items)
    {
        _items = items;
        var h = new HashCode();
        foreach (var item in items) h.Add(item);
        _hash = h.ToHashCode();
    }

    public bool Equals(CacheKey? other)
    {
        if (other is null || other._hash != _hash || other._items.Length != _items.Length) return false;
        for (int i = 0; i < _items.Length; i++)
            if (!Equals(_items[i], other._items[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CacheKey k && Equals(k);
    public override int GetHashCode() => _hash;
}

/// <summary>
/// 효과 목록을 값으로 비교하는 열쇠. 그릴 때만 채우는 다른 프레임 원본 함수(<see cref="AnimatedEffect.FrameSource"/>·
/// <see cref="AnimatedEffect.RenderedFrameSource"/>)는 빼고 비교한다 — 그 함수가 보는 셀이 바뀌면 캐시를 비우므로.
/// </summary>
internal sealed class EffectListKey : IEquatable<EffectListKey>
{
    private readonly LayerEffect[] _effects;
    private readonly int _hash;

    public EffectListKey(IReadOnlyList<LayerEffect> effects, int count)
    {
        _effects = new LayerEffect[count];
        var h = new HashCode();
        for (int i = 0; i < count; i++)
        {
            var e = effects[i] is AnimatedEffect { FrameSource: not null } or AnimatedEffect { RenderedFrameSource: not null }
                ? ((AnimatedEffect)effects[i]) with { FrameSource = null, RenderedFrameSource = null }
                : effects[i];
            _effects[i] = e;
            h.Add(e);
        }
        _hash = h.ToHashCode();
    }

    public EffectListKey(IReadOnlyList<LayerEffect> effects) : this(effects, effects.Count)
    {
    }

    public bool Equals(EffectListKey? other)
        => other is not null && other._hash == _hash && _effects.AsSpan().SequenceEqual(other._effects);

    public override bool Equals(object? obj) => obj is EffectListKey k && Equals(k);
    public override int GetHashCode() => _hash;
}

/// <summary>효과 한 번 실행에 끼우는 것: 취소, 그리는 레이어, 캐시, 효과 하나가 끝날 때 알림, 중간 결과 열쇠.</summary>
internal sealed class EffectRunHooks
{
    public CancellationToken Cancellation { get; init; }

    /// <summary>효과가 붙은 레이어(그룹이면 그 그룹) — 스크립트 효과 실패를 (레이어, 효과)별로 기록한다. null이면 기록하지 않는다.</summary>
    public Layer? Owner { get; init; }

    public EffectRenderCache? Cache { get; init; }

    /// <summary>켜진 효과 하나(잔상 포함)를 끝낼 때마다: 지금 그림.</summary>
    public Action<PixelImage>? EffectDone { get; init; }

    /// <summary>중간 결과를 담고 꺼낼 열쇠의 앞부분(이 레이어·프레임·셀·여백). null이면 담지 않는다.</summary>
    public object? CheckpointScope { get; init; }

    private int _scriptFailed;

    /// <summary>이 끼움으로 그린 실행에서 스크립트 효과가 실패했다(그 효과를 건너뛴 그림 — 효과 값으로 열쇠를 만든 캐시에 담지 않는다).</summary>
    public bool ScriptFailed => Volatile.Read(ref _scriptFailed) != 0;

    /// <summary>스크립트 효과 렌더러가 실패를 알린다(아무 스레드에서나).</summary>
    public void ReportScriptFailure() => Volatile.Write(ref _scriptFailed, 1);
}

/// <summary>
/// 효과 1~k까지 입힌 중간 결과를 캐시에 담고 꺼낸다(<see cref="EffectRunHooks.CheckpointScope"/>가 있을 때만).
/// k 다음 효과가 앞 효과에 맞춰 자르기(클리핑)가 아닌 자리만 담는다 — 그 자리에서는 그림 말고 이어받을 상태가 없다.
/// 클리핑된 잔상이 있는 목록은 담지 않는다(목록 중간의 상태를 끝에서 쓴다).
/// </summary>
internal sealed class EffectCheckpoints
{
    private readonly EffectRenderCache _cache;
    private readonly object _scope;
    private readonly IReadOnlyList<LayerEffect> _effects;
    private readonly System.Drawing.Rectangle _source;
    private readonly int _width, _height;
    private readonly EffectRunHooks _hooks;
    /// <summary>이 실행을 시작할 때의 캐시 세대(그 뒤 비웠으면 담지 않음).</summary>
    private readonly long _generation;

    private EffectCheckpoints(EffectRenderCache cache, object scope, IReadOnlyList<LayerEffect> effects, System.Drawing.Rectangle source,
        int width, int height, EffectRunHooks hooks)
    {
        _cache = cache;
        _scope = scope;
        _effects = effects;
        _source = source;
        _width = width;
        _height = height;
        _hooks = hooks;
        _generation = cache.Generation;
    }

    public static EffectCheckpoints? For(PixelImage image, IReadOnlyList<LayerEffect> effects, System.Drawing.Rectangle source, EffectRunHooks? hooks)
    {
        if (hooks is not { CheckpointScope: { } scope, Cache: { } cache } || effects.Count < 2) return null;
        foreach (var e in effects)
            if (e is AfterimageEffect { Enabled: true, ClipToPrevious: true }) return null;
        return new EffectCheckpoints(cache, scope, effects, source, image.Width, image.Height, hooks);
    }

    private bool Boundary(int index) => index + 1 >= _effects.Count || !_effects[index + 1].ClipToPrevious;

    private CacheKey KeyFor(int index) => new("prefix", _scope, _width, _height, _source, new EffectListKey(_effects, index + 1));

    /// <summary>담긴 가장 긴 중간 결과를 <paramref name="image"/>에 옮기고 이어서 계산할 효과 번호를 돌려준다(없으면 0). 건너뛴 효과도 끝낸 것으로 알린다.</summary>
    public int Restore(PixelImage image)
    {
        for (int i = _effects.Count - 2; i >= 0; i--)
        {
            if (!Boundary(i) || !_cache.TryCheckpoint(KeyFor(i), out var saved)) continue;
            saved.Pixels.AsSpan().CopyTo(image.Pixels);
            if (_hooks.EffectDone is { } done)
                for (int k = 0; k <= i; k++)
                    if (_effects[k] is { Enabled: true } and not AfterimageEffect) done(image);
            return i + 1;
        }
        return 0;
    }

    /// <summary>
    /// 효과 <paramref name="index"/>까지 입힌 그림을 담는다(마지막 효과·이어받을 상태가 있는 자리는 담지 않음).
    /// 이 실행에서 스크립트 효과가 실패했으면 그 뒤로는 담지 않는다(건너뛴 결과가 효과 값 열쇠로 남지 않게).
    /// </summary>
    public void Store(int index, PixelImage image)
    {
        if (index >= _effects.Count - 1 || !Boundary(index) || _hooks.ScriptFailed) return;
        _cache.PutCheckpoint(KeyFor(index), image.Clone(), _generation);
    }
}
