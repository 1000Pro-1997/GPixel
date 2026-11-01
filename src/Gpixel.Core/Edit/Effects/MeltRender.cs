using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>녹아내리기(<see cref="MeltEffect"/>) 그리기.</summary>
internal static class MeltRender
{
    /// <summary>아래로 거리만큼 넓힌다.</summary>
    public static EffectPadding Padding(MeltEffect m, int width, int height) => new(0, 0, 0, m.Distance);

    private const double FadeFrom = 0.65;

    public static void Apply(PixelImage image, MeltEffect e, Rectangle source)
    {
        double u = e.Progress;
        if (u <= 0) return;
        var b = image.GetOpaqueBounds();
        if (b.IsEmpty) return;
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        var src = EffectBuffers.RentCopy(px);
        try
        {
            Array.Clear(px);
            double irregular = e.Irregularity / 100.0;
            // 열마다 제 열에만 흘러내리므로 열끼리 나눠 돈다
            EffectParallel.For(b.Left, b.Right, b.Height, x =>
            {
                int column = x - source.X; // 원본 기준 열 — 여백이 달라도 같은 무작위
                double delay = irregular * 0.5 * LayerEffects.Random(e.Seed, column, 0, 21);
                double speed = 1 - irregular * 0.5 * LayerEffects.Random(e.Seed, column, 0, 22);
                double q = Math.Clamp((u - delay) / (1 - delay), 0, 1);
                double fade = e.Fade ? Math.Clamp((1 - q) / (1 - FadeFrom), 0, 1) : 1;
                if (fade <= 0) return;
                double fall = e.Distance * speed * q * q;
                int top = -1, bottom = -1;
                for (int y = b.Top; y < b.Bottom; y++)
                    if (src[y * w + x].A != 0)
                    {
                        if (top < 0) top = y;
                        bottom = y;
                    }
                if (top < 0) return;
                int span = bottom - top;
                int Target(int y) => y + (int)Math.Round(fall * (e.Stretch && span > 0 ? 0.4 + 0.6 * (y - top) / span : 1), MidpointRounding.AwayFromZero);
                for (int y = top; y <= bottom; y++)
                {
                    var c = src[y * w + x];
                    if (c.A == 0) continue;
                    if (fade < 1)
                    {
                        byte a = (byte)Math.Round(c.A * fade, MidpointRounding.AwayFromZero);
                        if (a == 0) continue;
                        c = c.WithAlpha(a);
                    }
                    int ty = Target(y);
                    // 늘어짐: 바로 아래 픽셀도 그림이면 그 픽셀이 내려간 자리까지 이 색으로 이어 흐른 자국을 남긴다
                    int end = e.Stretch && y < bottom && src[(y + 1) * w + x].A != 0 ? Target(y + 1) : ty + 1;
                    for (int yy = ty; yy < end; yy++)
                        if ((uint)yy < (uint)h) px[yy * w + x] = c;
                }
            });
        }
        finally
        {
            EffectBuffers.Return(src);
        }
    }
}
