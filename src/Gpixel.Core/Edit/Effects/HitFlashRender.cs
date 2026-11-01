using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>피격 번쩍임(<see cref="HitFlashEffect"/>) 그리기.</summary>
internal static class HitFlashRender
{
    public static void Apply(PixelImage image, HitFlashEffect e, Rectangle source = default)
    {
        int amount = (int)Math.Round(e.Amount(e.Progress) * 255, MidpointRounding.AwayFromZero);
        if (amount <= 0) return;
        var px = image.Pixels;
        var color = EffectGradientSampler.For(e, nameof(e.Color), image.GetOpaqueBounds(), e.Color, source.X, source.Y);
        int w = image.Width;
        EffectParallel.For(0, image.Height, w, y =>
        {
            var row = px.AsSpan(y * w, w);
            for (int x = 0; x < row.Length; x++)
                if (row[x].A != 0)
                {
                    var sampled = color.Sample(x, y);
                    int strength = color.HasGradient ? (amount * sampled.A + 127) / 255 : amount;
                    row[x] = LayerEffects.Mix(row[x], sampled, strength);
                }
        });
    }
}
