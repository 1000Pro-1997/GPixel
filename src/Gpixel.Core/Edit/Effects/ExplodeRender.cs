using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

internal static class ExplodeRender
{
    /// <summary>가로·세로 거리만큼, 세로는 중력 방향으로 중력만큼 더 넓힌다.</summary>
    public static EffectPadding Padding(ExplodeEffect x, int width, int height)
        => new(x.DistanceX, x.DistanceY + Math.Max(0, -x.Gravity), x.DistanceX, x.DistanceY + Math.Max(0, x.Gravity));

    public static void Apply(PixelImage image, ExplodeEffect e, Rectangle source)
    {
        double u = e.Phase, t = e.Progress;
        int f = e.FlashFrames, step = e.FrameStep;
        int flash = f > 0 && step >= 1 && step <= f ? (255 * (f - step + 1) + f / 2) / f : 0;
        if (u <= 0 && flash == 0) return; // 시작 = 원래 그림 그대로
        double fade = !e.Fade ? 1 : e.FadeStart >= 100 ? (u >= 1 ? 0 : 1) : Math.Clamp((1 - u) / (1 - e.FadeStart / 100.0), 0, 1);
        if (e.DistanceX == 0 && e.DistanceY == 0 && e.Gravity == 0 && flash == 0 && fade == 1 && (!e.Shrink || e.PieceSize == 1))
            return; // 움직임·시각 변화가 없으면 픽셀 배열을 복사하지 않는다
        var bounds = image.GetOpaqueBounds();
        if (bounds.IsEmpty) return;
        var flashColor = flash > 0
            ? EffectGradientSampler.For(e, nameof(ExplodeEffect.FlashColor), bounds, e.FlashColor, source.X, source.Y)
            : default;
        int w = image.Width, h = image.Height, n = e.PieceSize;
        var px = image.Pixels;
        var src = EffectBuffers.RentCopy(px);
        try
        {
            Array.Clear(px);
            // 조각은 차례대로 겹쳐 놓는다(나중 조각이 위) — 순서가 결과를 정하므로 나누지 않는다
            if (fade > 0)
            {
                var (cx, cy) = e.Center switch
                {
                    ExplodeCenter.Middle => (bounds.Left + bounds.Width / 2.0, bounds.Top + bounds.Height / 2.0),
                    ExplodeCenter.BottomCenter => (bounds.Left + bounds.Width / 2.0, (double)bounds.Bottom),
                    ExplodeCenter.Custom => (source.X + e.CenterX + 0.5, source.Y + e.CenterY + 0.5),
                    _ => LayerEffects.PivotIn(e.PivotPoint, source),
                };
                int size = e.Shrink ? Math.Max(1, (int)Math.Ceiling(n * (1 - u) - 1e-9)) : n, inset = (n - size) / 2;
                double spreadX = e.SpreadX / 100.0, spreadY = e.SpreadY / 100.0, fall = e.Gravity * u * u;
                // 바깥 조각일수록 멀리(중심 조각은 거리의 40%) — 그림이 통째로 부풀며 흩어진다
                double reach = Math.Max(1, Math.Max(Math.Max(Math.Abs(bounds.Left - cx), Math.Abs(bounds.Right - cx)), Math.Max(Math.Abs(bounds.Top - cy), Math.Abs(bounds.Bottom - cy))));
                // 조각 격자는 원본 왼쪽 위 기준 — 여백이 달라도(캔버스·부위 아틀라스) 같은 조각·같은 무작위
                int gx0 = LayerEffects.FloorDiv(bounds.Left - source.X, n), gx1 = LayerEffects.FloorDiv(bounds.Right - 1 - source.X, n);
                int gy0 = LayerEffects.FloorDiv(bounds.Top - source.Y, n), gy1 = LayerEffects.FloorDiv(bounds.Bottom - 1 - source.Y, n);
                for (int gy = gy0; gy <= gy1; gy++)
                    for (int gx = gx0; gx <= gx1; gx++)
                    {
                        int x0 = source.X + gx * n, y0 = source.Y + gy * n;
                        double vx = x0 + n / 2.0 - cx, vy = y0 + n / 2.0 - cy;
                        double angle = vx * vx + vy * vy < 1e-9 ? LayerEffects.Random(e.Seed, gx, gy, 0) * 2 * Math.PI : Math.Atan2(vy, vx);
                        double jitter = LayerEffects.Random(e.Seed, gx, gy, 1) * 2 - 1;
                        double radial = 0.4 + 0.6 * Math.Min(1, Math.Sqrt(vx * vx + vy * vy) / reach);
                        double speedRandom = LayerEffects.Random(e.Seed, gx, gy, 2);
                        double angleX = angle + jitter * spreadX * Math.PI / 2;
                        double travelX = e.DistanceX * radial * (1 - spreadX * 0.6 * speedRandom) * t;
                        int dx = e.DistanceX == 0 ? 0 : (int)Math.Round(Math.Cos(angleX) * travelX, MidpointRounding.AwayFromZero);
                        double angleY = spreadX == spreadY ? angleX : angle + jitter * spreadY * Math.PI / 2;
                        double travelY = spreadX == spreadY && e.DistanceX == e.DistanceY ? travelX
                            : e.DistanceY * radial * (1 - spreadY * 0.6 * speedRandom) * t;
                        int dy = e.DistanceY == 0 ? (int)Math.Round(fall, MidpointRounding.AwayFromZero)
                            : (int)Math.Round(Math.Sin(angleY) * travelY + fall, MidpointRounding.AwayFromZero);
                        for (int yy = y0 + inset; yy < y0 + inset + size; yy++)
                        {
                            int ty = yy + dy;
                            if ((uint)yy >= (uint)h || (uint)ty >= (uint)h) continue;
                            for (int xx = x0 + inset; xx < x0 + inset + size; xx++)
                            {
                                int tx = xx + dx;
                                if ((uint)xx >= (uint)w || (uint)tx >= (uint)w) continue;
                                var c = src[yy * w + xx];
                                if (c.A == 0) continue;
                                if (flash > 0)
                                {
                                    var sampled = flashColor.Sample(xx, yy);
                                    c = LayerEffects.Mix(c, sampled, flashColor.HasGradient ? (flash * sampled.A + 127) / 255 : flash);
                                }
                                if (fade < 1)
                                {
                                    byte a = (byte)Math.Round(c.A * fade);
                                    if (a == 0) continue;
                                    c = c.WithAlpha(a);
                                }
                                int i = ty * w + tx;
                                px[i] = Color32.BlendOver(px[i], c);
                            }
                        }
                    }
            }
            if (e.KeepOriginal) KeepUnder(px, src, w, h);
        }
        finally
        {
            EffectBuffers.Return(src);
        }
    }

    /// <summary>원본 남기기: 날아간 조각 위에 원래 그림(<paramref name="original"/>의 앞 w×h칸)을 다시 올린다.</summary>
    private static void KeepUnder(Color32[] px, Color32[] original, int w, int h)
        => EffectParallel.For(0, h, w, y =>
        {
            var kept = original.AsSpan(y * w, w);
            var row = px.AsSpan(y * w, w);
            for (int x = 0; x < row.Length; x++)
                if (kept[x].A != 0) row[x] = Color32.BlendOver(row[x], kept[x]);
        });
}
