using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

internal static class FadeRender
{
    public static void Apply(PixelImage image, FadeEffect effect)
        => Apply(image, effect.FadeOut ? 1 - effect.Progress : effect.Progress);

    internal static void Apply(PixelImage image, double visibility)
    {
        if (visibility >= 1) return;
        var pixels = image.Pixels;
        if (visibility <= 0)
        {
            Array.Clear(pixels);
            return;
        }
        int w = image.Width;
        EffectParallel.For(0, image.Height, w, y =>
        {
            var row = pixels.AsSpan(y * w, w);
            for (int x = 0; x < row.Length; x++)
            {
                var c = row[x];
                if (c.A == 0) continue;
                byte alpha = (byte)Math.Round(c.A * visibility, MidpointRounding.AwayFromZero);
                row[x] = alpha == 0 ? Color32.Transparent : c.WithAlpha(alpha);
            }
        });
    }
}
