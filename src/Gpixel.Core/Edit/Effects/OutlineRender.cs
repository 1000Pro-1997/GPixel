using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

internal static class OutlineRender
{
    /// <summary>바깥 외곽선은 사방으로 두께만큼 넓힌다(안쪽이면 없음).</summary>
    public static EffectPadding Padding(OutlineEffect o, int width, int height)
        => o.Inside ? default : new EffectPadding(o.Thickness, o.Thickness, o.Thickness, o.Thickness);

    public static void Apply(PixelImage image, OutlineEffect o, Rectangle source)
    {
        int w = image.Width, h = image.Height, n = w * h;
        var px = image.Pixels;
        bool shade = o.ColorMode == OutlineColorMode.Shade;
        var bounds = !shade && o.ColorGradients.ContainsKey(nameof(OutlineEffect.Color)) ? image.GetOpaqueBounds() : default;
        var color = EffectGradientSampler.For(o, nameof(OutlineEffect.Color), bounds, o.Color, source.X, source.Y);
        // 명암 색은 같은 색마다 한 번만 계산한다(여러 줄이 함께 찾고 넣는 표 + 줄마다 바로 앞 색)
        var shaded = shade ? new System.Collections.Concurrent.ConcurrentDictionary<Color32, Color32>() : null;
        float shadeAmount = o.ShadeAmount / 100f;
        // 그림 모양과, 고리마다 번갈아 쓰는 깎은·넓힌 모양 둘
        var shape = EffectBuffers.Rent<bool>(n);
        var even = EffectBuffers.Rent<bool>(n);
        var odd = o.Thickness > 1 ? EffectBuffers.Rent<bool>(n) : even;
        Color32[]? baseColor = null;
        try
        {
            LayerEffects.Mask(px, shape, w, h);
            var grown = shape;
            if (o.Inside)
            {
                for (int k = 0; k < o.Thickness; k++)
                {
                    var next = k % 2 == 0 ? even : odd;
                    LayerEffects.Erode(grown, next, w, h, o.Diagonal);
                    grown = next;
                }
                var inner = grown;
                EffectParallel.For(0, h, w, y =>
                {
                    int start = y * w;
                    var row = px.AsSpan(start, w);
                    var inShape = shape.AsSpan(start, w);
                    var kept = inner.AsSpan(start, w);
                    var memo = shade ? new ShadeMemo(shaded!, shadeAmount) : default;
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (!inShape[x]) continue;
                        if (kept[x])
                        {
                            if (o.HideOriginal) row[x] = Color32.Transparent;
                            continue;
                        }
                        if (shade)
                        {
                            row[x] = memo.Of(row[x]);
                            continue;
                        }
                        var c = color.Sample(x, y);
                        row[x] = LayerEffects.Mix(row[x], c, c.A);
                    }
                });
            }
            else
            {
                // 명암: 고리마다 바로 안쪽(이전 고리 또는 그림) 이웃의 기준 색을 이어받아, 두꺼운 선도 맞닿은 부분의 색을 따른다.
                // 새 고리 칸만 쓰고 이전 모양 칸만 읽으므로 줄끼리 겹치지 않는다.
                baseColor = shade ? EffectBuffers.RentCopy(px) : null;
                for (int k = 0; k < o.Thickness; k++)
                {
                    var previous = grown;
                    var next = k % 2 == 0 ? even : odd;
                    LayerEffects.Dilate(previous, next, w, h, o.Diagonal);
                    if (baseColor is { } colors)
                        EffectParallel.For(0, h, w, y =>
                        {
                            int start = y * w;
                            var added = next.AsSpan(start, w);
                            var before = previous.AsSpan(start, w);
                            for (int x = 0; x < added.Length; x++)
                                if (added[x] && !before[x]) colors[start + x] = NeighborColor(colors, previous, w, h, x, y);
                        });
                    grown = next;
                }
                var outer = grown;
                var ringColors = baseColor;
                EffectParallel.For(0, h, w, y =>
                {
                    int start = y * w;
                    var row = px.AsSpan(start, w);
                    var inShape = shape.AsSpan(start, w);
                    var ring = outer.AsSpan(start, w);
                    var memo = ringColors is null ? default : new ShadeMemo(shaded!, shadeAmount);
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (inShape[x])
                        {
                            if (o.HideOriginal) row[x] = Color32.Transparent;
                        }
                        else if (ring[x]) row[x] = ringColors is null ? color.Sample(x, y) : memo.Of(ringColors[start + x]);
                    }
                });
            }
        }
        finally
        {
            EffectBuffers.Return(shape);
            EffectBuffers.Return(even);
            if (!ReferenceEquals(odd, even)) EffectBuffers.Return(odd);
            if (baseColor is not null) EffectBuffers.Return(baseColor);
        }
    }

    /// <summary>명암 색 찾기: 모든 줄이 함께 쓰는 표 앞에서 줄마다 바로 앞에 찾은 색을 기억한다(같은 색이 이어지는 일이 많음).</summary>
    private struct ShadeMemo(System.Collections.Concurrent.ConcurrentDictionary<Color32, Color32> table, float amount)
    {
        private Color32 _color, _shaded;
        private bool _found;

        public Color32 Of(Color32 c)
        {
            if (!_found || c != _color)
            {
                _color = c;
                _shaded = table.GetOrAdd(c, static (c, amount) => Gpixel.Core.Pixelate.OkLab.ShiftLightness(c, amount), amount);
                _found = true;
            }
            return _shaded;
        }
    }

    /// <summary>
    /// 바깥 명암 외곽선 픽셀 (x, y)가 따를 안쪽 이웃 색: 가로·세로 이웃(왼·오른·위·아래 순)이 있으면 그중에서, 없으면 대각 이웃 중에서
    /// 가장 많은 색(같으면 먼저 본 이웃).
    /// </summary>
    private static Color32 NeighborColor(Color32[] colors, bool[] filled, int w, int h, int x, int y)
    {
        Span<Color32> found = stackalloc Color32[4];
        int count = 0;
        for (int pass = 0; pass < 2 && count == 0; pass++)
            for (int d = pass * 4; d < pass * 4 + 4; d++)
            {
                var (dx, dy) = LayerEffects.Square[d];
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)w && (uint)ny < (uint)h && filled[ny * w + nx]) found[count++] = colors[ny * w + nx];
            }
        int best = 0, bestVotes = 0;
        for (int a = 0; a < count; a++)
        {
            int votes = 0;
            for (int b = 0; b < count; b++)
                if (found[b] == found[a]) votes++;
            if (votes > bestVotes) (best, bestVotes) = (a, votes);
        }
        return found[best];
    }
}
