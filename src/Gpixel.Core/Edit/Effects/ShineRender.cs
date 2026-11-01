using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

internal static class ShineRender
{
    public static void Apply(PixelImage image, ShineEffect s, Rectangle source)
    {
        int amount = (s.Strength * 255 + 50) / 100;
        if (amount == 0) return;
        var bounds = image.GetOpaqueBounds();
        if (bounds.IsEmpty) return;
        var color = EffectGradientSampler.For(s, nameof(ShineEffect.Color), bounds, s.Color, source.X, source.Y);
        double angle = s.Angle * Math.PI / 180, dx = Math.Cos(angle), dy = -Math.Sin(angle); // y는 아래로
        double Along(double x, double y) => x * dx + y * dy;
        double l = bounds.Left + 0.5, r = bounds.Right - 0.5, top = bounds.Top + 0.5, bottom = bounds.Bottom - 0.5;
        double s0 = Math.Min(Math.Min(Along(l, top), Along(r, top)), Math.Min(Along(l, bottom), Along(r, bottom)));
        double s1 = Math.Max(Math.Max(Along(l, top), Along(r, top)), Math.Max(Along(l, bottom), Along(r, bottom)));
        double half = s.Width / 2.0, reach = half + s.Softness;
        // 띠 가운데: 진행 0 = 첫 픽셀보다 (반폭 + 부드러움 + 1) 앞, 1 = 마지막 픽셀보다 그만큼 뒤 — 양 끝에서는 닿는 픽셀이 없다
        double from = s0 - reach - 1, to = s1 + reach + 1, c = from + (to - from) * s.Progress;
        int w = image.Width;
        var px = image.Pixels;
        EffectParallel.For(bounds.Top, bounds.Bottom, bounds.Width, y =>
        {
            var row = px.AsSpan(y * w, w);
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (row[x].A == 0) continue;
                double d = Math.Abs(Along(x + 0.5, y + 0.5) - c);
                if (d > reach) continue;
                int k = d <= half ? amount : amount * (s.Softness + 1 - (int)Math.Ceiling(d - half)) / (s.Softness + 1);
                if (k > 0)
                {
                    var sampled = color.Sample(x, y);
                    row[x] = LayerEffects.Screen(row[x], sampled, color.HasGradient ? (k * sampled.A + 127) / 255 : k);
                }
            }
        });
    }
}
