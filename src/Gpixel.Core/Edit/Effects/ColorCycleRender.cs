using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>색 순환(<see cref="ColorCycleEffect"/>) 그리기.</summary>
internal static class ColorCycleRender
{
    public static void Apply(PixelImage image, ColorCycleEffect e, Rectangle source = default)
    {
        var colors = e.Colors;
        int n = colors.Count, k = e.Shift(e.Progress);
        if (n < ColorCycleEffect.MinColors || k == 0) return;
        var px = image.Pixels;
        int w = image.Width, h = image.Height;
        if (e.ColorGradients.Count == 0)
        {
            var index = new Dictionary<uint, int>(n);
            for (int i = 0; i < n; i++) index.TryAdd(colors[i].Argb & 0x00FFFFFFu, i);
            // 다 만든 표는 읽기만 하므로 여러 줄이 함께 봐도 된다
            EffectParallel.For(0, h, w, y =>
            {
                var row = px.AsSpan(y * w, w);
                for (int x = 0; x < row.Length; x++)
                {
                    var c = row[x];
                    if (c.A != 0 && index.TryGetValue(c.Argb & 0x00FFFFFFu, out int at)) row[x] = colors[(at + k) % n].WithAlpha(c.A);
                }
            });
            return;
        }

        var bounds = image.GetOpaqueBounds();
        var samplers = new EffectGradientSampler[n];
        for (int j = 0; j < n; j++)
            samplers[j] = EffectGradientSampler.For(e, $"Colors[{j}]", bounds, colors[j], source.X, source.Y);
        EffectParallel.For(0, h, w, y =>
        {
            var row = px.AsSpan(y * w, w);
            for (int x = 0; x < row.Length; x++)
            {
                var c = row[x];
                if (c.A == 0) continue;
                for (int j = 0; j < n; j++)
                {
                    var match = samplers[j].Sample(x, y);
                    if (samplers[j].HasGradient && match.A == 0) continue;
                    if (c.R != match.R || c.G != match.G || c.B != match.B) continue;
                    var target = samplers[(j + k) % n].Sample(x, y);
                    row[x] = samplers[(j + k) % n].HasGradient
                        ? LayerEffects.Mix(c, target, target.A)
                        : target.WithAlpha(c.A);
                    break;
                }
            }
        });
    }
}
