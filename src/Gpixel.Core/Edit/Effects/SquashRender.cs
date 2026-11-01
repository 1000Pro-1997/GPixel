using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

internal static class SquashRender
{
    /// <summary>
    /// 지금까지 넓힌 크기 × 축별 세기만큼 넓힌다 — 기준점에서 그림 끝까지는 많아야 지금 크기(기준점이 그림 가운데면 절반)다.
    /// </summary>
    public static EffectPadding Padding(SquashEffect q, int width, int height)
    {
        double sx = q.WidthAmount / 100.0, sy = q.HeightAmount / 100.0;
        int half = sx == 0 ? 0 : (int)Math.Ceiling(width * sx / 2) + 1, full = sx == 0 ? 0 : (int)Math.Ceiling(width * sx) + 1;
        int halfH = sy == 0 ? 0 : (int)Math.Ceiling(height * sy / 2) + 1, fullH = sy == 0 ? 0 : (int)Math.Ceiling(height * sy) + 1;
        (int gx, int up, int down) = q.Anchor switch
        {
            SquashAnchor.BottomCenter => (half, fullH, 0),
            SquashAnchor.Middle => (half, halfH, halfH),
            _ => (full, fullH, fullH),
        };
        return new EffectPadding(gx, up, gx, down);
    }

    public static void Apply(PixelImage image, SquashEffect q, Rectangle source)
    {
        double f = q.Wobble(q.Progress);
        if (Math.Abs(f) < 1e-9) return;
        var bounds = image.GetOpaqueBounds();
        if (bounds.IsEmpty) return;
        double sx = 1 + q.WidthAmount / 100.0 * f, sy = 1 - q.HeightAmount / 100.0 * f;
        var (ax, ay) = q.Anchor switch
        {
            SquashAnchor.BottomCenter => (bounds.Left + bounds.Width / 2.0, (double)bounds.Bottom),
            SquashAnchor.Middle => (bounds.Left + bounds.Width / 2.0, bounds.Top + bounds.Height / 2.0),
            _ => LayerEffects.PivotIn(q.PivotPoint, source),
        };
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        var src = EffectBuffers.RentCopy(px);
        try
        {
            Array.Clear(px);
            // 결과가 놓일 수 있는 자리만(기준점에서 그림 끝까지 × 배율, 한 칸 여유)
            int x0 = Math.Max(0, (int)Math.Floor(ax + (bounds.Left - ax) * sx) - 1), x1 = Math.Min(w, (int)Math.Ceiling(ax + (bounds.Right - ax) * sx) + 1);
            int y0 = Math.Max(0, (int)Math.Floor(ay + (bounds.Top - ay) * sy) - 1), y1 = Math.Min(h, (int)Math.Ceiling(ay + (bounds.Bottom - ay) * sy) + 1);
            EffectParallel.For(y0, y1, x1 - x0, y =>
            {
                int iy = (int)Math.Floor(ay + (y + 0.5 - ay) / sy);
                if (iy < bounds.Top || iy >= bounds.Bottom) return;
                var from = src.AsSpan(iy * w, w);
                var row = px.AsSpan(y * w, w);
                for (int x = x0; x < x1; x++)
                {
                    int ix = (int)Math.Floor(ax + (x + 0.5 - ax) / sx);
                    if (ix < bounds.Left || ix >= bounds.Right) continue;
                    row[x] = from[ix];
                }
            });
        }
        finally
        {
            EffectBuffers.Return(src);
        }
    }
}
