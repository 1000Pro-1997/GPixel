using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>충격파 고리(<see cref="ShockwaveEffect"/>) 그리기.</summary>
internal static class ShockwaveRender
{
    /// <summary>사방으로 최대 반지름만큼(피벗·직접 중심은 +1 — 중심이 그림 끝에 걸쳐도) 넓힌다.</summary>
    public static EffectPadding Padding(ShockwaveEffect w, int width, int height)
    {
        int r = w.Radius + (w.Center is ExplodeCenter.Pivot or ExplodeCenter.Custom ? 1 : 0);
        return new EffectPadding(r, r, r, r);
    }

    public static void Apply(PixelImage image, ShockwaveEffect e, Rectangle source)
    {
        double u = e.Progress;
        int r = e.RadiusAt(u);
        if (r < 1) return;
        // 반지름은 빠르기 곡선대로, 옅어짐은 시간대로(처음 빠르게면 끝 무렵에 이미 보이지 않게 되지 않도록)
        double fade = e.Fade ? 1 - e.Phase : 1;
        if (fade <= 0) return;
        if (!e.ColorGradients.ContainsKey(nameof(e.Color)) && e.Color.A == 0) return;
        var (cx, cy) = Center(image, e, source);
        int w = image.Width, h = image.Height;
        // 고리가 닿는 칸(중심 둘레 반지름 + 1의 정사각형)만 표시해 둔다 — 그림이 커도 고리 둘레만 본다
        int left = cx - r - 1, top = cy - r - 1, side = 2 * r + 3;
        var ring = EffectBuffers.Rent<bool>(side * side);
        try
        {
            Array.Clear(ring, 0, side * side);
            void Plot(int x, int y)
            {
                if ((uint)x < (uint)w && (uint)y < (uint)h) ring[(y - top) * side + x - left] = true;
            }
            if (e.Thickness == 1)
            {
                // 중점 원 그리기: 8분원마다 한 칸씩 — 빈틈·두 겹 없는 픽셀 원
                int x = r, y = 0, err = 1 - r;
                while (x >= y)
                {
                    Plot(cx + x, cy + y); Plot(cx + y, cy + x); Plot(cx - y, cy + x); Plot(cx - x, cy + y);
                    Plot(cx - x, cy - y); Plot(cx - y, cy - x); Plot(cx + y, cy - x); Plot(cx + x, cy - y);
                    y++;
                    if (err < 0) err += 2 * y + 1;
                    else
                    {
                        x--;
                        err += 2 * (y - x) + 1;
                    }
                }
            }
            else
            {
                double outer = (r + 0.5) * (r + 0.5), innerR = r - e.Thickness + 0.5, inner = innerR > 0 ? innerR * innerR : -1;
                for (int y = cy - r - 1; y <= cy + r + 1; y++)
                    for (int x = cx - r - 1; x <= cx + r + 1; x++)
                    {
                        int dx = x - cx, dy = y - cy, d = dx * dx + dy * dy;
                        if (d > inner && d <= outer) Plot(x, y);
                    }
            }
            var color = EffectGradientSampler.For(e, nameof(e.Color),
                Rectangle.FromLTRB(cx - r, cy - r, cx + r + 1, cy + r + 1), e.Color, source.X, source.Y);
            var px = image.Pixels;
            int x0 = Math.Max(0, left), x1 = Math.Min(w, left + side), y0 = Math.Max(0, top), y1 = Math.Min(h, top + side);
            if (x1 <= x0) return;
            EffectParallel.For(y0, y1, x1 - x0, y =>
            {
                int mark = (y - top) * side - left, row = y * w;
                for (int x = x0; x < x1; x++)
                {
                    if (!ring[mark + x]) continue;
                    var sampled = color.Sample(x, y);
                    byte alpha = (byte)Math.Round(sampled.A * fade, MidpointRounding.AwayFromZero);
                    if (alpha == 0) continue;
                    var ringColor = sampled.WithAlpha(alpha);
                    int i = row + x;
                    px[i] = e.Behind ? Color32.BlendOver(ringColor, px[i]) : Color32.BlendOver(px[i], ringColor);
                }
            });
        }
        finally
        {
            EffectBuffers.Return(ring);
        }
    }

    /// <summary>고리 중심 픽셀(이미지 기준): 가운데·아래 가운데는 보이는 그림 범위 기준(비면 원본 가운데).</summary>
    private static (int X, int Y) Center(PixelImage image, ShockwaveEffect e, Rectangle source)
    {
        if (e.Center == ExplodeCenter.Custom) return (source.X + e.CenterX, source.Y + e.CenterY);
        if (e.Center == ExplodeCenter.Pivot)
        {
            var (px, py) = LayerEffects.PivotIn(e.PivotPoint, source);
            return ((int)Math.Floor(px), (int)Math.Floor(py));
        }
        var b = image.GetOpaqueBounds();
        if (b.IsEmpty) b = source;
        return (b.Left + b.Width / 2, e.Center == ExplodeCenter.BottomCenter ? b.Bottom - 1 : b.Top + b.Height / 2);
    }
}
