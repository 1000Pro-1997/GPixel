using System.Drawing;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>효과가 그림 밖으로 넓히는 여백(px, 왼쪽·위·오른쪽·아래).</summary>
public readonly record struct EffectPadding(int Left, int Top, int Right, int Bottom)
{
    public bool IsNone => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>효과가 캔버스 밖으로 나간 부분을 담도록 캔버스를 늘리는 방식.</summary>
public enum EffectCanvasExpandMode
{
    /// <summary>필요한 방향만 필요한 픽셀 수만큼 늘린다.</summary>
    Minimum,
    /// <summary>필요한 크기를 담는 범위에서 원래 캔버스 가로세로 비율을 유지한다.</summary>
    KeepRatio,
    /// <summary>원래 캔버스의 중심을 유지하며 사방에 같은 여백을 더한다.</summary>
    KeepCenter,
}

/// <summary>효과를 담을 새 캔버스 크기와 그 안에서 원래 캔버스 왼쪽 위가 놓일 자리.</summary>
public readonly record struct EffectCanvasExpansion(int Width, int Height, int OffsetX, int OffsetY, bool RatioPreserved)
{
    public bool IsNone(int oldWidth, int oldHeight)
        => Width == oldWidth && Height == oldHeight && OffsetX == 0 && OffsetY == 0;

    /// <summary>
    /// 원래 <paramref name="width"/>×<paramref name="height"/> 캔버스와 효과 결과 <paramref name="effectBounds"/>를 모두 담는 가장 작은 확대.
    /// 비율 유지 결과가 이미지 최대 크기를 넘으면 효과를 자르지 않는 최소 확대를 돌려주고 <see cref="RatioPreserved"/>는 false다.
    /// </summary>
    public static EffectCanvasExpansion FromBounds(int width, int height, Rectangle effectBounds, EffectCanvasExpandMode mode)
    {
        if (width < 1 || height < 1 || width > PixelImage.MaxSize || height > PixelImage.MaxSize)
            throw new ArgumentOutOfRangeException(nameof(width), Gpixel.Core.Localization.DomainStrings.FormatCanvas_InvalidSize(max: PixelImage.MaxSize, width: width, height: height));
        var required = effectBounds.IsEmpty
            ? new Rectangle(0, 0, width, height)
            : Rectangle.Union(new Rectangle(0, 0, width, height), effectBounds);
        long left = Math.Max(0L, -(long)required.Left), top = Math.Max(0L, -(long)required.Top);
        long right = Math.Max(0L, (long)required.Right - width), bottom = Math.Max(0L, (long)required.Bottom - height);
        long minimumWidth = width + left + right, minimumHeight = height + top + bottom;
        if (minimumWidth > PixelImage.MaxSize || minimumHeight > PixelImage.MaxSize)
            throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.FormatCanvas_EffectTooLarge(width: minimumWidth, height: minimumHeight, max: PixelImage.MaxSize));

        int resultWidth = (int)minimumWidth, resultHeight = (int)minimumHeight;
        bool kept = mode == EffectCanvasExpandMode.KeepRatio;
        if (mode == EffectCanvasExpandMode.KeepCenter)
        {
            long margin = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
            if (width + 2 * margin > PixelImage.MaxSize || height + 2 * margin > PixelImage.MaxSize)
                throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.FormatCanvas_EffectTooLarge(
                    width: width + 2 * margin, height: height + 2 * margin, max: PixelImage.MaxSize));
            left = top = margin;
            resultWidth = width + (int)(2 * margin);
            resultHeight = height + (int)(2 * margin);
        }
        if (kept)
        {
            double scale = Math.Max((double)resultWidth / width, (double)resultHeight / height);
            int ratioWidth = Math.Max(resultWidth, (int)Math.Ceiling(width * scale - 1e-9));
            int ratioHeight = Math.Max(resultHeight, (int)Math.Ceiling(height * scale - 1e-9));
            if (ratioWidth <= PixelImage.MaxSize && ratioHeight <= PixelImage.MaxSize)
            {
                int extraX = ratioWidth - resultWidth, extraY = ratioHeight - resultHeight;
                left += extraX / 2;
                top += extraY / 2;
                resultWidth = ratioWidth;
                resultHeight = ratioHeight;
            }
            else
            {
                kept = false;
            }
        }
        return new EffectCanvasExpansion(resultWidth, resultHeight, (int)left, (int)top, kept);
    }
}

/// <summary>
/// 레이어 효과(<see cref="LayerEffect"/>) 그리기. 잔상을 뺀 효과는 목록 순서대로 적용하고, 잔상은 다른 효과가 각 프레임에 적용된 모습을 현재 그림 뒤에 합성한다. 꺼진 효과는 건너뛴다.
/// 모양은 알파가 0이 아닌 픽셀이다. 바깥 외곽선·그림자·반사·터지기·늘었다 줄기는 그림 밖으로 번지므로 <see cref="Render(PixelImage, IReadOnlyList{LayerEffect})"/>는 그만큼(<see cref="Padding"/>)
/// 넓힌 이미지와 원래 그림의 왼쪽 위가 놓인 자리를 돌려준다. 이미지 밖은 투명으로 본다.
/// 그리기 함수는 받은 목록을 프레임 범위(<see cref="LayerEffect.Frames"/>)와 관계없이 그린다 — 프레임 하나를 그릴 때는 부르는 쪽이
/// <see cref="At"/>으로 그 프레임에 입힐 효과만 골라(움직이는 효과는 그 프레임의 진행을 채워) 넘긴다.
/// </summary>
public static class LayerEffects
{
    /// <summary>켜진 효과가 하나라도 있는지(어느 프레임이든).</summary>
    public static bool AnyEnabled(IReadOnlyList<LayerEffect> effects)
    {
        for (int i = 0; i < effects.Count; i++)
            if (effects[i].Enabled) return true;
        return false;
    }

    /// <summary>프레임 <paramref name="frameIndex"/>에 입혀질(켜져 있고 범위 안인) 효과가 하나라도 있는지.</summary>
    public static bool AnyActive(IReadOnlyList<LayerEffect> effects, int frameIndex)
    {
        for (int i = 0; i < effects.Count; i++)
            if (effects[i].Enabled && effects[i].AppliesTo(frameIndex)) return true;
        return false;
    }

    /// <summary>
    /// 켜진 효과 중 다른 프레임 그림을 보는 효과(<see cref="EffectDefinition.UsesOtherFrames"/>, 잔상·<c>ctx.frameSource</c>를 쓰는 스크립트)가 있는지 —
    /// 있을 때만 <see cref="At"/>에 다른 프레임 원본을 넘긴다.
    /// </summary>
    public static bool UsesOtherFrames(IReadOnlyList<LayerEffect> effects)
    {
        for (int i = 0; i < effects.Count; i++)
            if (effects[i].Enabled && EffectRegistry.Of(effects[i]) is { } definition && definition.UsesOtherFrames(effects[i])) return true;
        return false;
    }

    /// <summary>
    /// 프레임 <paramref name="frameIndex"/>(프레임 <paramref name="frameCount"/>개 중)에 그릴 효과: 범위 밖 효과를 빼고(순서 그대로, 꺼진 효과도 남음 — 그리기가 건너뜀),
    /// 움직이는 효과(<see cref="AnimatedEffect"/>)는 그 프레임의 진행과 <paramref name="pivot"/>(효과 원본 왼쪽 위 기준 px), 다른 프레임 원본
    /// <paramref name="frameSource"/>(프레임 번호 → 그 프레임 효과 원본, 잔상용 — <see cref="AnimatedEffect.FrameSource"/>)를 채운 사본으로 바꾼다.
    /// 움직이는 색 그라데이션이 있는 다른 효과도 범위 안 진행을 채운 사본으로 바꾼다.
    /// 바꿀 것이 없으면 <paramref name="effects"/> 그대로(복사 없음). <paramref name="cache"/>가 있으면 잔상용으로 그린 앞 프레임 그림을 다시 쓴다.
    /// </summary>
    public static IReadOnlyList<LayerEffect> At(IReadOnlyList<LayerEffect> effects, int frameIndex, int frameCount, PointF? pivot = null,
        Func<int, PixelImage?>? frameSource = null, Func<int, PointF?>? framePivot = null, EffectRenderCache? cache = null)
        => Resolve(effects, frameIndex, frameCount, pivot, frameSource, inRangeOnly: true, framePivot, cache);

    /// <summary>
    /// 프레임 범위와 관계없이 모든 효과를 프레임 <paramref name="frameIndex"/>로 움직인 목록(범위 밖이면 가까운 끝의 모습) — 효과 창 미리보기용.
    /// </summary>
    public static IReadOnlyList<LayerEffect> Animate(IReadOnlyList<LayerEffect> effects, int frameIndex, int frameCount, PointF? pivot = null,
        Func<int, PixelImage?>? frameSource = null, Func<int, PointF?>? framePivot = null, EffectRenderCache? cache = null)
        => Resolve(effects, frameIndex, frameCount, pivot, frameSource, inRangeOnly: false, framePivot, cache);

    private static IReadOnlyList<LayerEffect> Resolve(IReadOnlyList<LayerEffect> effects, int frameIndex, int frameCount, PointF? pivot,
        Func<int, PixelImage?>? frameSource, bool inRangeOnly, Func<int, PointF?>? framePivot = null, EffectRenderCache? cache = null)
    {
        bool preservePositions = inRangeOnly && effects.Any(e => e.ClipToPrevious);
        Func<int, PixelImage, (PixelImage Image, int OffsetX, int OffsetY)>? rendered = null;
        if (frameSource is not null && effects.Any(e => e is AfterimageEffect { Enabled: true } && (!inRangeOnly || e.AppliesTo(frameIndex)))
            && effects.Any(e => e.Enabled && e is not AfterimageEffect))
        {
            var others = effects.Where(e => e is not AfterimageEffect).ToArray();
            rendered = (f, raw) =>
            {
                var previous = At(others, f, frameCount, framePivot?.Invoke(f) ?? pivot);
                var padding = Padding(others, raw.Width, raw.Height);
                return cache is null
                    ? Render(raw, previous, padding)
                    : cache.Ghost(new CacheKey("ghost", raw, f, new EffectListKey(previous), padding), hooks => Render(raw, previous, padding, hooks));
            };
        }
        List<LayerEffect>? list = null;
        for (int i = 0; i < effects.Count; i++)
        {
            var e = effects[i];
            LayerEffect? r = inRangeOnly && !e.AppliesTo(frameIndex)
                ? preservePositions ? e with { Enabled = false } : null
                : e is AfterimageEffect a
                    ? a.AtFrame(frameIndex, frameCount, pivot, frameSource) with
                    {
                        RenderedFrameSource = a.ClipToPrevious && rendered is not null
                            ? ClippedGhostSource(effects, i, frameCount, framePivot, pivot, cache)
                            : rendered,
                    }
                    : e is AnimatedEffect animated ? animated.AtFrame(frameIndex, frameCount, pivot, e is ScriptEffect ? frameSource : null) : e;
            if (r is { Enabled: true } && r is not AnimatedEffect && r.HasAnimatedColorGradient())
            {
                var (from, to) = r.Frames is { } range ? (range.From, range.To) : (0, Math.Max(0, frameCount - 1));
                r = r with { GradientTime = Math.Clamp((double)(frameIndex - from) / Math.Max(1, to - from), 0, 1) };
            }
            if (list is null && !ReferenceEquals(r, e))
            {
                list = new List<LayerEffect>(effects.Count);
                for (int k = 0; k < i; k++) list.Add(effects[k]);
            }
            if (list is not null && r is not null) list.Add(r);
        }
        return list ?? effects;
    }

    private static Func<int, PixelImage, (PixelImage Image, int OffsetX, int OffsetY)>? ClippedGhostSource(
        IReadOnlyList<LayerEffect> effects, int index, int frameCount, Func<int, PointF?>? framePivot, PointF? pivot, EffectRenderCache? cache)
    {
        int anchor = index - 1;
        while (anchor >= 0 && effects[anchor].ClipToPrevious) anchor--;
        if (anchor < 0 || effects[anchor] is AfterimageEffect) return null;
        var group = effects.Take(anchor + 1).Where(e => e is not AfterimageEffect).ToArray();
        var before = group[..^1];
        return (frame, raw) =>
        {
            var framePosition = framePivot?.Invoke(frame) ?? pivot;
            var padding = Padding(group, raw.Width, raw.Height);
            var resolved = At(group, frame, frameCount, framePosition);
            var prefix = At(before, frame, frameCount, framePosition);
            (PixelImage Image, int OffsetX, int OffsetY) Ghost(EffectRunHooks? hooks)
            {
                var (image, dx, dy) = Render(raw, resolved, padding, hooks);
                if (ReferenceEquals(image, raw))
                    return (new PixelImage(raw.Width, raw.Height), 0, 0);
                var (prior, _, _) = Render(raw, prefix, padding, hooks);
                for (int i = 0; i < image.Pixels.Length; i++)
                    if (image.Pixels[i] == prior.Pixels[i]) image.Pixels[i] = Color32.Transparent;
                return (image, dx, dy);
            }
            return cache is null
                ? Ghost(null)
                : cache.Ghost(new CacheKey("clipped-ghost", raw, frame, new EffectListKey(resolved), new EffectListKey(prefix), padding), Ghost);
        };
    }

    /// <summary>
    /// 영구 적용할 효과 번호(목록 순서): 고른 효과(꺼져 있어도)와 그보다 위(앞)의 켜진 효과 모두.
    /// 잔상을 고르면 앞뒤의 켜진 효과까지 구워야 각 프레임의 완성된 모습이 잔상에도 남는다. 고른 것이 없으면 빈 목록.
    /// 모르는 효과(<see cref="MissingEffect"/>)는 그리지 않으므로 굽지 않고 목록에 남긴다.
    /// </summary>
    public static IReadOnlyList<int> BakeSet(IReadOnlyList<LayerEffect> effects, IEnumerable<int> selected)
    {
        var chosen = selected.Where(i => i >= 0 && i < effects.Count).ToHashSet();
        if (chosen.Count == 0) return [];
        var group = new HashSet<int>();
        foreach (int selectedIndex in chosen)
        {
            int first = selectedIndex;
            while (first > 0 && effects[first].ClipToPrevious) first--;
            int lastInGroup = first;
            while (lastInGroup + 1 < effects.Count && effects[lastInGroup + 1].ClipToPrevious) lastInGroup++;
            for (int i = first; i <= lastInGroup; i++) group.Add(i);
        }
        int last = chosen.Any(i => effects[i] is AfterimageEffect) ? effects.Count - 1 : group.Max();
        return Enumerable.Range(0, last + 1).Where(i => effects[i] is not MissingEffect && (group.Contains(i) || effects[i].Enabled)).ToList();
    }

    /// <summary>
    /// <paramref name="width"/>×<paramref name="height"/> 그림에 켜진 효과들이 그림 밖으로 넓힐 수 있는 최대 여백(차례대로 쌓임: 외곽선 뒤 그림자면 외곽선까지 그림자가 진다).
    /// 프레임 범위·진행은 보지 않으므로 어느 프레임의 효과 묶음 여백보다도 작지 않다(효과를 빼면 여백은 줄기만 함) — 모든 프레임 같은 크기로 넓힐 때 쓴다.
    /// 효과마다 그 정의의 여백 함수(<see cref="EffectDefinition.Padding"/>)에 그때까지 넓힌 크기를 넘겨 더한다. 클리핑된 효과는 넓히지 않는다
    /// (기준 픽셀을 옮기는 효과 — <see cref="EffectDefinition.ClipsBySource"/> — 는 넓힌다). 모르는 효과는 여백이 없다.
    /// 넓힌 크기가 <see cref="PixelImage.MaxSize"/>를 넘는 축은 여백 없이(그 축은 이미지 끝에서 잘림).
    /// </summary>
    public static EffectPadding Padding(IReadOnlyList<LayerEffect> effects, int width, int height)
    {
        int l = 0, t = 0, r = 0, b = 0;
        foreach (var e in effects)
        {
            if (!e.Enabled || EffectRegistry.Of(e) is not { } definition) continue;
            if (e.ClipToPrevious && !definition.ClipsBySource) continue;
            var add = definition.Padding(e, width + l + r, height + t + b);
            l += add.Left;
            t += add.Top;
            r += add.Right;
            b += add.Bottom;
        }
        var p = new EffectPadding(l, t, r, b);
        if (width + p.Left + p.Right > PixelImage.MaxSize) p = p with { Left = 0, Right = 0 };
        if (height + p.Top + p.Bottom > PixelImage.MaxSize) p = p with { Top = 0, Bottom = 0 };
        return p;
    }

    /// <summary>
    /// 효과를 입힌 그림. 켜진 효과가 없으면 <paramref name="source"/> 그대로(복사 없음, 위치 0,0).
    /// 있으면 여백만큼 넓힌 새 이미지와, 원래 그림 왼쪽 위를 기준으로 그 이미지의 왼쪽 위 위치(여백만큼 음수)를 돌려준다.
    /// </summary>
    public static (PixelImage Image, int OffsetX, int OffsetY) Render(PixelImage source, IReadOnlyList<LayerEffect> effects)
        => Render(source, effects, Padding(effects, source.Width, source.Height));

    /// <summary>
    /// 여백을 정해 두고 효과를 입힌 그림(<paramref name="padding"/>은 <paramref name="effects"/>의 여백 이상 — 프레임마다 효과가 달라도 같은 크기로).
    /// 여백이 없고 켜진 효과도 없으면 <paramref name="source"/> 그대로(복사 없음, 위치 0,0).
    /// </summary>
    public static (PixelImage Image, int OffsetX, int OffsetY) Render(PixelImage source, IReadOnlyList<LayerEffect> effects, EffectPadding padding)
        => Render(source, effects, padding, null);

    /// <summary>
    /// <see cref="Render(PixelImage, IReadOnlyList{LayerEffect}, EffectPadding)"/>를 레이어 <paramref name="owner"/>의 효과로 그린다: 효과마다 <paramref name="cancel"/>을 확인하고
    /// 스크립트 효과 실패를 그 레이어에 기록한다(<see cref="Scripting.ScriptEffectRuntime"/>). 결과 그림은 같다.
    /// </summary>
    public static (PixelImage Image, int OffsetX, int OffsetY) Render(PixelImage source, IReadOnlyList<LayerEffect> effects, EffectPadding padding,
        Layer? owner, CancellationToken cancel)
        => Render(source, effects, padding, new EffectRunHooks { Cancellation = cancel, Owner = owner });

    /// <inheritdoc cref="Render(PixelImage, IReadOnlyList{LayerEffect}, EffectPadding)"/>
    internal static (PixelImage Image, int OffsetX, int OffsetY) Render(PixelImage source, IReadOnlyList<LayerEffect> effects, EffectPadding padding,
        EffectRunHooks? hooks)
    {
        if (padding.IsNone && !AnyEnabled(effects)) return (source, 0, 0);
        var image = new PixelImage(source.Width + padding.Left + padding.Right, source.Height + padding.Top + padding.Bottom);
        image.Blit(source, padding.Left, padding.Top);
        Apply(image, effects, new Rectangle(padding.Left, padding.Top, source.Width, source.Height), hooks);
        return (image, -padding.Left, -padding.Top);
    }

    /// <summary>효과를 입힌 그림을 원래 크기로 자른 것(밖으로 번진 부분은 버림). 켜진 효과가 없으면 <paramref name="source"/> 그대로.</summary>
    public static PixelImage RenderClipped(PixelImage source, IReadOnlyList<LayerEffect> effects)
    {
        if (!AnyEnabled(effects)) return source;
        var (image, x, y) = Render(source, effects);
        return image.Crop(new Rectangle(-x, -y, source.Width, source.Height));
    }

    /// <summary>
    /// 켜진 효과를 차례로 <paramref name="image"/>에 바로 입힌다(크기는 그대로 — 번질 자리는 부르는 쪽이 미리 넓혀 둔다).
    /// <paramref name="source"/> = 효과 원본(셀, 그룹이면 캔버스)이 이 이미지 안에 놓인 자리: 터지기의 조각 격자·무작위와 직접 중심·피벗의 기준이다.
    /// 클리핑된 반사는 기준 효과의 픽셀만 뒤집어 원본 그림 뒤에 놓는다(이동한 픽셀은 기준 영역 밖에도 남는다).
    /// </summary>
    public static void Apply(PixelImage image, IReadOnlyList<LayerEffect> effects, Rectangle source) => Apply(image, effects, source, null);

    /// <summary>
    /// <see cref="Apply(PixelImage, IReadOnlyList{LayerEffect}, Rectangle)"/>에 <paramref name="hooks"/>를 끼운 것: 효과마다 취소를 확인하고,
    /// 켜진 효과(잔상 포함) 하나가 끝날 때마다 알리며, 중간 결과 열쇠가 있으면 효과 1~k까지의 결과를 캐시에서 꺼내 k+1번째부터 계산하고
    /// 새로 계산한 중간 결과를 담는다. 결과 그림은 끼우지 않았을 때와 같다.
    /// </summary>
    internal static void Apply(PixelImage image, IReadOnlyList<LayerEffect> effects, Rectangle source, EffectRunHooks? hooks)
    {
        var cancel = hooks?.Cancellation ?? CancellationToken.None;
        var done = hooks?.EffectDone;
        var checkpoints = EffectCheckpoints.For(image, effects, source, hooks);
        int start = checkpoints?.Restore(image) ?? 0;
        List<AfterimageEffect>? clippedAfterimages = null;
        bool[]? clipMask = null;
        for (int index = start; index < effects.Count; index++)
        {
            cancel.ThrowIfCancellationRequested();
            var effect = effects[index];
            if (effect is AfterimageEffect after)
            {
                if (after.ClipToPrevious && effect.Enabled && clipMask is not null)
                    (clippedAfterimages ??= []).Add(after);
                if (!effect.ClipToPrevious) clipMask = null;
                continue;
            }
            // 꺼진 효과와 모르는 효과(그리지 않음)는 건너뛴다. 정의는 효과마다 한 번 찾는다.
            if (!effect.Enabled || EffectRegistry.Of(effect) is not { } definition)
            {
                if (!effect.ClipToPrevious) clipMask = null;
                continue;
            }
            if (effect.ClipToPrevious)
            {
                if (clipMask is not null)
                {
                    if (definition.ClipsBySource)
                    {
                        var moved = new PixelImage(image.Width, image.Height);
                        for (int i = 0; i < image.Pixels.Length; i++)
                            if (clipMask[i]) moved.Pixels[i] = image.Pixels[i];
                        definition.Render(moved, effect, source, hooks);
                        for (int i = 0; i < image.Pixels.Length; i++)
                            if (!clipMask[i] && moved.Pixels[i].A != 0)
                                image.Pixels[i] = Color32.BlendOver(moved.Pixels[i], image.Pixels[i]);
                    }
                    else
                    {
                        var before = (Color32[])image.Pixels.Clone();
                        definition.Render(image, effect, source, hooks);
                        for (int i = 0; i < before.Length; i++)
                            if (!clipMask[i]) image.Pixels[i] = before[i];
                    }
                }
                checkpoints?.Store(index, image);
                done?.Invoke(image);
                continue;
            }

            bool hasClippedFollower = index + 1 < effects.Count && effects[index + 1].ClipToPrevious;
            Color32[]? anchorBefore = hasClippedFollower ? (Color32[])image.Pixels.Clone() : null;
            definition.Render(image, effect, source, hooks);
            clipMask = anchorBefore is null ? null : ChangedPixels(anchorBefore, image.Pixels);
            // 원본을 지우는 효과(원본 지우기 외곽선): 지워진(투명해진) 원본 자리는 기준 영역이 아니다 — 클리핑된 효과는 남은 픽셀만 보고,
            // 뒤따르는 클리핑되지 않은 효과는 원본이 지워진 결과를 그대로 받는다.
            if (clipMask is not null && definition.ErasesSource(effect))
                for (int i = 0; i < clipMask.Length; i++)
                    if (image.Pixels[i].A == 0) clipMask[i] = false;
            checkpoints?.Store(index, image);
            done?.Invoke(image);
        }
        // 잔상은 다른 효과가 각 프레임에 적용된 모습 뒤에 합성한다. 목록 위치로 현재 프레임의 움직임을 다시 입히지 않는다.
        int ghosts = 0;
        foreach (var effect in effects)
            if (effect is AfterimageEffect { Enabled: true, ClipToPrevious: false } after)
            {
                cancel.ThrowIfCancellationRequested();
                AfterimageRender.Apply(image, after, source);
                ghosts++;
                done?.Invoke(image);
            }
        if (clippedAfterimages is not null)
            foreach (var after in clippedAfterimages)
            {
                cancel.ThrowIfCancellationRequested();
                AfterimageRender.Apply(image, after, source);
                ghosts++;
                done?.Invoke(image);
            }
        if (done is null) return;
        // 기준 효과 영역이 없어 건너뛴 클리핑된 잔상도 끝낸 것으로 센다
        foreach (var effect in effects)
            if (effect is AfterimageEffect { Enabled: true } && --ghosts < 0) done(image);
    }

    private static bool[] ChangedPixels(Color32[] before, Color32[] after)
    {
        // 크기를 모르므로 한 줄로 보고 같은 길이 토막으로 나눠 돈다
        const int chunk = 4096;
        var changed = new bool[before.Length];
        EffectParallel.For(0, (before.Length + chunk - 1) / chunk, chunk, c =>
        {
            int start = c * chunk, length = Math.Min(chunk, before.Length - start);
            var from = before.AsSpan(start, length);
            var to = after.AsSpan(start, length);
            var mark = changed.AsSpan(start, length);
            for (int i = 0; i < mark.Length; i++) mark[i] = from[i] != to[i];
        });
        return changed;
    }

    /// <summary>피벗 자리(이미지 기준): 없으면 원본 가운데.</summary>
    internal static (double X, double Y) PivotIn(PointF? pivot, Rectangle source)
        => pivot is { } p ? (source.X + p.X, source.Y + p.Y) : (source.X + source.Width / 2.0, source.Y + source.Height / 2.0);

    internal static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>시드·조각 자리·용도로 정해지는 0 이상 1 미만의 수(늘 같음).</summary>
    internal static double Random(int seed, int x, int y, int salt)
    {
        uint h = (uint)seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)y * 0xC2B2AE3Du ^ (uint)(salt + 1) * 0x27D4EB2Fu;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h / 4294967296.0;
    }

    /// <summary>스크린(밝게 더하기)을 <paramref name="amount"/>/255만큼(알파는 그대로).</summary>
    internal static Color32 Screen(Color32 dst, Color32 color, int amount)
    {
        byte S(byte d, byte c)
        {
            int screen = 255 - (255 - d) * (255 - c) / 255;
            return (byte)(d + ((screen - d) * amount + 127) / 255);
        }
        return new Color32(S(dst.R, color.R), S(dst.G, color.G), S(dst.B, color.B), dst.A);
    }

    // ───────────── 도우미 ─────────────

    /// <summary>알파가 0이 아닌 픽셀을 <paramref name="mask"/>의 앞 <paramref name="w"/>×<paramref name="h"/>칸에 쓴다.</summary>
    internal static void Mask(Color32[] px, bool[] mask, int w, int h)
        => EffectParallel.For(0, h, w, y =>
        {
            var row = px.AsSpan(y * w, w);
            var mark = mask.AsSpan(y * w, w);
            for (int x = 0; x < row.Length; x++) mark[x] = row[x].A != 0;
        });

    /// <summary>한 칸 넓히기(4방향 = 십자, 8방향 = 모서리 포함)를 <paramref name="dst"/>(<paramref name="src"/>와 다른 배열)에 쓴다.</summary>
    internal static void Dilate(bool[] src, bool[] dst, int w, int h, bool diagonal)
        => EffectParallel.For(0, h, w, y =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++) dst[i] = src[i] || Any(src, w, h, x, y, diagonal);
        });

    /// <summary>한 칸 깎기(이웃 4/8방향 중 하나라도 비었거나 이미지 밖이면 빠짐)를 <paramref name="dst"/>(<paramref name="src"/>와 다른 배열)에 쓴다.</summary>
    internal static void Erode(bool[] src, bool[] dst, int w, int h, bool diagonal)
        => EffectParallel.For(0, h, w, y =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++) dst[i] = src[i] && All(src, w, h, x, y, diagonal);
        });

    private static readonly (int X, int Y)[] Cross = [(-1, 0), (1, 0), (0, -1), (0, 1)];
    internal static readonly (int X, int Y)[] Square = [(-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)];

    private static bool Any(bool[] m, int w, int h, int x, int y, bool diagonal)
    {
        foreach (var (dx, dy) in diagonal ? Square : Cross)
        {
            int nx = x + dx, ny = y + dy;
            if ((uint)nx < (uint)w && (uint)ny < (uint)h && m[ny * w + nx]) return true;
        }
        return false;
    }

    private static bool All(bool[] m, int w, int h, int x, int y, bool diagonal)
    {
        foreach (var (dx, dy) in diagonal ? Square : Cross)
        {
            int nx = x + dx, ny = y + dy;
            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h || !m[ny * w + nx]) return false;
        }
        return true;
    }

    /// <summary><paramref name="dst"/>의 RGB를 섞기 방법대로 <paramref name="color"/>와 섞은 결과 쪽으로 <paramref name="amount"/>/255만큼(알파는 그대로).</summary>
    internal static Color32 Blend(Color32 dst, Color32 color, int amount, EffectBlend blend)
    {
        switch (blend)
        {
            case EffectBlend.Screen:
                return Screen(dst, color, amount);
            case EffectBlend.Multiply:
                var m = new Color32((byte)((dst.R * color.R + 127) / 255), (byte)((dst.G * color.G + 127) / 255), (byte)((dst.B * color.B + 127) / 255));
                return Mix(dst, m, amount);
            default:
                return Mix(dst, color, amount);
        }
    }

    /// <summary><paramref name="dst"/>의 RGB를 <paramref name="color"/> 쪽으로 <paramref name="amount"/>/255만큼(알파는 그대로).</summary>
    internal static Color32 Mix(Color32 dst, Color32 color, int amount)
    {
        if (amount <= 0) return dst;
        int keep = 255 - amount;
        byte M(byte d, byte c) => (byte)((d * keep + c * amount + 127) / 255);
        return new Color32(M(dst.R, color.R), M(dst.G, color.G), M(dst.B, color.B), dst.A);
    }
}
