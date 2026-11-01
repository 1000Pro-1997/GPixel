using System.Collections.Concurrent;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>색조 보정(<see cref="ColorAdjustEffect"/>) 그리기.</summary>
internal static class ColorAdjustRender
{
    public static void Apply(PixelImage image, ColorAdjustEffect e)
    {
        Func<Color32, Color32>? map = e.Mode switch
        {
            ColorAdjustMode.Grayscale when e.Amount > 0 => c => LayerEffects.Mix(c, Gray(c), Percent(e.Amount)),
            ColorAdjustMode.Sepia when e.Amount > 0 => c => LayerEffects.Mix(c, Sepia(c), Percent(e.Amount)),
            ColorAdjustMode.HueShift when e.Hue % 360 != 0 => c => HueShift(c, e.Hue),
            ColorAdjustMode.BrightnessContrast when e.Brightness != 0 || e.Contrast != 0 => c => BrightnessContrast(c, e.Brightness, e.Contrast),
            ColorAdjustMode.Saturation when e.Saturation != 0 => c => Saturate(c, e.Saturation),
            _ => null,
        };
        if (map is null) return;
        var px = image.Pixels;
        int w = image.Width;
        // 같은 색은 한 번만 계산한다(여러 줄이 함께 찾고 넣음)
        var memo = new ConcurrentDictionary<uint, Color32>();
        EffectParallel.For(0, image.Height, w, y =>
        {
            var row = px.AsSpan(y * w, w);
            for (int x = 0; x < row.Length; x++)
            {
                var c = row[x];
                if (c.A == 0) continue;
                if (!memo.TryGetValue(c.Argb, out var r)) memo[c.Argb] = r = map(c).WithAlpha(c.A);
                row[x] = r;
            }
        });
    }

    private static int Percent(int amount) => (amount * 255 + 50) / 100;

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    private static double Luma(Color32 c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    private static Color32 Gray(Color32 c)
    {
        byte g = Clamp(Luma(c));
        return new Color32(g, g, g);
    }

    private static Color32 Sepia(Color32 c) => new(
        Clamp(0.393 * c.R + 0.769 * c.G + 0.189 * c.B),
        Clamp(0.349 * c.R + 0.686 * c.G + 0.168 * c.B),
        Clamp(0.272 * c.R + 0.534 * c.G + 0.131 * c.B));

    private static Color32 HueShift(Color32 c, int degrees)
    {
        var (h, s, v) = c.ToHsv();
        return Color32.FromHsv(h + degrees, s, v);
    }

    private static Color32 BrightnessContrast(Color32 c, int brightness, int contrast)
    {
        double k = 1 + contrast / 100.0, add = brightness * 255 / 100.0;
        byte F(byte x) => Clamp((x - 128) * k + 128 + add);
        return new Color32(F(c.R), F(c.G), F(c.B));
    }

    private static Color32 Saturate(Color32 c, int saturation)
    {
        double l = Luma(c), k = 1 + saturation / 100.0;
        byte F(byte x) => Clamp(l + (x - l) * k);
        return new Color32(F(c.R), F(c.G), F(c.B));
    }
}
