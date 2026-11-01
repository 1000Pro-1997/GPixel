using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

internal static class RimLightRender
{
    public static void Apply(PixelImage image, RimLightEffect r, Rectangle source)
    {
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        var shape = EffectBuffers.Rent<bool>(w * h);
        var (vx, vy) = r.Vector;
        var bounds = r.ColorGradients.Count > 0 ? image.GetOpaqueBounds() : default;
        var light = EffectGradientSampler.For(r, nameof(RimLightEffect.Light), bounds, r.Light, source.X, source.Y);
        var shade = EffectGradientSampler.For(r, nameof(RimLightEffect.Shade), bounds, r.Shade, source.X, source.Y);
        bool lit = r.Light.A != 0 || light.HasGradient, dark = r.Shade.A != 0 || shade.HasGradient;
        int size = r.Size;
        try
        {
            LayerEffects.Mask(px, shape, w, h);
            EffectParallel.For(0, h, w, y =>
            {
                var row = px.AsSpan(y * w, w);
                var inShape = shape.AsSpan(y * w, w);
                for (int x = 0; x < row.Length; x++)
                {
                    if (!inShape[x]) continue;
                    if (lit && RimEdge(shape, w, h, size, x, y, vx, vy))
                    {
                        var color = light.Sample(x, y);
                        row[x] = LayerEffects.Mix(row[x], color, color.A);
                    }
                    else if (dark && RimEdge(shape, w, h, size, x, y, -vx, -vy))
                    {
                        var color = shade.Sample(x, y);
                        row[x] = LayerEffects.Mix(row[x], color, color.A);
                    }
                }
            });
        }
        finally
        {
            EffectBuffers.Return(shape);
        }
    }

    /// <summary>(x, y)에서 (dx, dy) 쪽으로 <paramref name="size"/>칸 안에 모양 밖(이미지 밖 포함)이 있는지 — 대각선이면 두 축 중 하나라도.</summary>
    private static bool RimEdge(bool[] shape, int w, int h, int size, int x, int y, int dx, int dy)
    {
        for (int distance = 1; distance <= size; distance++)
        {
            int nx = x + dx * distance, ny = y + dy * distance;
            if (dx != 0 && !((uint)nx < (uint)w && shape[y * w + nx])) return true;
            if (dy != 0 && !((uint)ny < (uint)h && shape[ny * w + x])) return true;
        }
        return false;
    }
}
