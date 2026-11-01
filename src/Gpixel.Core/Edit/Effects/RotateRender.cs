using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>
/// 회전(<see cref="RotateEffect"/>) 그리기: RotSprite 방식 — 보이는 그림을 Scale2x(EPX)로 k번 키우고(한 변 128px 이하 3번 = 8배, 256px 이하 2번, 512px 이하 1번, 그보다 크면 0번)
/// 결과 픽셀마다 가운데 점을 거꾸로 돌려 키운 그림에서 최근접으로 집는다(새 색·반투명 없음). 90°의 배수는 키우지 않고 최근접으로(손실 없음).
/// </summary>
internal static class RotateRender
{
    /// <summary>
    /// 지금까지 넓힌 크기 <paramref name="width"/>×<paramref name="height"/>를 기준점을 중심으로 돌린 그림이 닿을 수 있는 만큼 넓힌다
    /// (가운데: 가로 ⌈높이/2⌉·세로 ⌈너비/2⌉, 아래 가운데: 가로 높이·위 ⌈너비/2⌉·아래 √((너비/2)² + 높이²), 피벗: 사방 대각선 길이 — 각 +1 반올림 몫).
    /// 회전 각이 0이면 없음.
    /// </summary>
    public static EffectPadding Padding(RotateEffect q, int width, int height)
    {
        if (q.Angle == 0) return default;
        double w2 = width / 2.0;
        switch (q.Anchor)
        {
            case SquashAnchor.Middle:
            {
                int x = (int)Math.Ceiling(height / 2.0) + 1, y = (int)Math.Ceiling(w2) + 1;
                return new EffectPadding(x, y, x, y);
            }
            case SquashAnchor.BottomCenter:
            {
                int x = height + 1;
                return new EffectPadding(x, (int)Math.Ceiling(w2) + 1, x, (int)Math.Ceiling(Math.Sqrt(w2 * w2 + (double)height * height)) + 1);
            }
            default:
            {
                int d = (int)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height)) + 1;
                return new EffectPadding(d, d, d, d);
            }
        }
    }

    public static void Apply(PixelImage image, RotateEffect e, Rectangle source)
    {
        double deg = e.DegreesAt(e.Progress);
        double turn = deg % 360;
        if (Math.Abs(turn) < 1e-9) return; // 0° · 한 바퀴 = 원래 그림
        var b = image.GetOpaqueBounds();
        if (b.IsEmpty) return;
        var (ax, ay) = e.Anchor switch
        {
            SquashAnchor.BottomCenter => (b.Left + b.Width / 2.0, (double)b.Bottom),
            SquashAnchor.Middle => (b.Left + b.Width / 2.0, b.Top + b.Height / 2.0),
            _ => LayerEffects.PivotIn(e.PivotPoint, source),
        };
        double quarter = turn / 90;
        bool exact = Math.Abs(quarter - Math.Round(quarter)) < 1e-9;
        double cos, sin;
        if (exact)
        {
            int q = (((int)Math.Round(quarter) % 4) + 4) % 4;
            (cos, sin) = q switch { 0 => (1.0, 0.0), 1 => (0.0, 1.0), 2 => (-1.0, 0.0), _ => (0.0, -1.0) };
        }
        else
        {
            double r = deg * Math.PI / 180;
            (cos, sin) = (Math.Cos(r), Math.Sin(r));
        }

        // 원본 그림 둘레 1px(Scale2x 이웃 투명) 잘라 키운다 — 잘라 낸 그림과 키우는 단계마다 번갈아 쓰는 두 칸에
        var crop = Rectangle.Inflate(b, 1, 1);
        int levels = exact ? 0 : Math.Max(b.Width, b.Height) switch { <= 128 => 3, <= 256 => 2, <= 512 => 1, _ => 0 };
        int scale = 1 << levels;
        int bw = crop.Width * scale, bh = crop.Height * scale;
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        var big = EffectBuffers.Rent<Color32>(bw * bh);
        var spare = levels > 0 ? EffectBuffers.Rent<Color32>(bw * bh) : null;
        try
        {
            CropNormalized(px, w, h, crop, big);
            for (int k = 0, cw = crop.Width, ch = crop.Height; k < levels; k++, cw *= 2, ch *= 2)
            {
                Scale2x(big, cw, ch, spare!);
                (big, spare) = (spare!, big);
            }

            Array.Clear(px);
            // 돌린 그림이 닿는 자리(원본 범위 네 모서리를 돌린 사각형, 한 칸 여유)
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (cx, cy) in new[] { (crop.Left, crop.Top), (crop.Right, crop.Top), (crop.Left, crop.Bottom), (crop.Right, crop.Bottom) })
            {
                double dx = cx - ax, dy = cy - ay;
                double rx = ax + cos * dx - sin * dy, ry = ay + sin * dx + cos * dy;
                minX = Math.Min(minX, rx);
                maxX = Math.Max(maxX, rx);
                minY = Math.Min(minY, ry);
                maxY = Math.Max(maxY, ry);
            }
            int x0 = Math.Max(0, (int)Math.Floor(minX) - 1), x1 = Math.Min(w, (int)Math.Ceiling(maxX) + 1);
            int y0 = Math.Max(0, (int)Math.Floor(minY) - 1), y1 = Math.Min(h, (int)Math.Ceiling(maxY) + 1);
            var bp = big;
            EffectParallel.For(y0, y1, x1 - x0, y =>
            {
                double dy = y + 0.5 - ay;
                var row = px.AsSpan(y * w, w);
                for (int x = x0; x < x1; x++)
                {
                    double dx = x + 0.5 - ax;
                    // 거꾸로 돌린 자리(원본 좌표) → 키운 그림 칸
                    double sx = ax + cos * dx + sin * dy, sy = ay - sin * dx + cos * dy;
                    int ix = (int)Math.Floor((sx - crop.X) * scale), iy = (int)Math.Floor((sy - crop.Y) * scale);
                    if ((uint)ix >= (uint)bw || (uint)iy >= (uint)bh) continue;
                    row[x] = bp[iy * bw + ix];
                }
            });
        }
        finally
        {
            EffectBuffers.Return(big);
            if (spare is not null) EffectBuffers.Return(spare);
        }
    }

    /// <summary>
    /// <paramref name="crop"/> 영역(이미지 밖은 투명)을 <paramref name="dst"/>의 앞 칸에 옮긴다. 알파 0인 픽셀은 하나의 투명으로
    /// (Scale2x가 RGB만 다른 투명을 다른 색으로 보지 않게).
    /// </summary>
    private static void CropNormalized(Color32[] px, int w, int h, Rectangle crop, Color32[] dst)
        => EffectParallel.For(0, crop.Height, crop.Width, cy =>
        {
            int y = crop.Y + cy, row = cy * crop.Width;
            for (int cx = 0; cx < crop.Width; cx++)
            {
                int x = crop.X + cx;
                var c = (uint)x < (uint)w && (uint)y < (uint)h ? px[y * w + x] : Color32.Transparent;
                dst[row + cx] = c.A == 0 ? Color32.Transparent : c;
            }
        });

    /// <summary>
    /// Scale2x(EPX): <paramref name="src"/> 앞 <paramref name="w"/>×<paramref name="h"/>칸의 픽셀 하나를 <paramref name="dst"/>(다른 배열)의 2×2로,
    /// 이웃 모양을 따라 모서리를 이웃 색으로(새 색 없음, 대각선이 계단 대신 이어짐). 원본 줄마다 결과 두 줄에만 쓴다.
    /// </summary>
    internal static void Scale2x(Color32[] src, int w, int h, Color32[] dst)
    {
        int dw = w * 2;
        Color32 At(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? src[y * w + x] : Color32.Transparent;
        EffectParallel.For(0, h, w, y =>
        {
            for (int x = 0; x < w; x++)
            {
                var e = src[y * w + x];
                Color32 e0 = e, e1 = e, e2 = e, e3 = e;
                var b = At(x, y - 1);
                var d = At(x - 1, y);
                var f = At(x + 1, y);
                var hh = At(x, y + 1);
                if (!b.Equals(hh) && !d.Equals(f))
                {
                    if (d.Equals(b)) e0 = d;
                    if (b.Equals(f)) e1 = f;
                    if (d.Equals(hh)) e2 = d;
                    if (hh.Equals(f)) e3 = f;
                }
                int i = y * 2 * dw + x * 2;
                dst[i] = e0;
                dst[i + 1] = e1;
                dst[i + dw] = e2;
                dst[i + dw + 1] = e3;
            }
        });
    }
}
