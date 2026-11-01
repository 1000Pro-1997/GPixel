using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

internal static class ShadowRender
{
    /// <summary>옮긴 쪽으로 옮긴 만큼 넓힌다.</summary>
    public static EffectPadding Padding(ShadowEffect s, int width, int height)
        => new(Math.Max(0, -s.OffsetX), Math.Max(0, -s.OffsetY), Math.Max(0, s.OffsetX), Math.Max(0, s.OffsetY));

    /// <summary>그림자. 디더면 반투명 대신 색(불투명)을 밀도 무늬(4×4 Bayer, 효과 원본 왼쪽 위 기준)의 칸에만 깐다.</summary>
    public static void Apply(PixelImage image, ShadowEffect s, Rectangle source)
    {
        int w = image.Width, h = image.Height, dx = s.OffsetX, dy = s.OffsetY;
        int density = s.Dither switch { ShadowDither.Light25 => 4, ShadowDither.Half50 => 8, ShadowDither.Dense75 => 12, _ => 0 };
        var solid = s.Color.WithAlpha(255);
        var bounds = s.ColorGradients.ContainsKey(nameof(ShadowEffect.Color)) ? image.GetOpaqueBounds() : default;
        var color = EffectGradientSampler.For(s, nameof(ShadowEffect.Color), bounds, s.Color, source.X, source.Y);
        var px = image.Pixels;
        var src = EffectBuffers.RentCopy(px);
        try
        {
            // 그림자가 놓일 수 있는 줄·칸만(옮긴 원본이 이미지 안인 곳)
            int x0 = Math.Max(0, dx), x1 = Math.Min(w, w + dx);
            EffectParallel.For(Math.Max(0, dy), Math.Min(h, h + dy), w, y =>
            {
                var cast = src.AsSpan((y - dy) * w, w);
                var under = src.AsSpan(y * w, w);
                var row = px.AsSpan(y * w, w);
                for (int x = x0; x < x1; x++)
                {
                    byte a = cast[x - dx].A;
                    if (a == 0) continue;
                    Color32 shadow;
                    if (density == 0)
                    {
                        var sampled = color.Sample(x, y);
                        shadow = sampled.WithAlpha((byte)((sampled.A * a + 127) / 255));
                    }
                    else if (Bayer.Threshold4(x - source.X, y - source.Y) < density)
                        shadow = color.HasGradient ? color.Sample(x, y) : solid;
                    else continue;
                    row[x] = Color32.BlendOver(shadow, under[x]);
                }
            });
        }
        finally
        {
            EffectBuffers.Return(src);
        }
    }
}
