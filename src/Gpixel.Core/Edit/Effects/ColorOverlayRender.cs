using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

internal static class ColorOverlayRender
{
    public static void Apply(PixelImage image, ColorOverlayEffect c, Rectangle source)
    {
        int amount = (c.Amount * 255 + 50) / 100;
        if (amount == 0) return;
        var bounds = c.ColorGradients.ContainsKey(nameof(ColorOverlayEffect.Color)) ? image.GetOpaqueBounds() : default;
        var color = EffectGradientSampler.For(c, nameof(ColorOverlayEffect.Color), bounds, c.Color, source.X, source.Y);
        var px = image.Pixels;
        int w = image.Width;
        EffectParallel.For(0, image.Height, w, y =>
        {
            var row = px.AsSpan(y * w, w);
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x].A == 0) continue;
                var sampled = color.Sample(x, y);
                int strength = (amount * sampled.A + 127) / 255;
                if (strength != 0) row[x] = LayerEffects.Blend(row[x], sampled, strength, c.Blend);
            }
        });
    }
}
