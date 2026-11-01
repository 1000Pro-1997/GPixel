using System.Collections.Concurrent;
using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>색 바꾸기(<see cref="PaletteSwapEffect"/>) 그리기.</summary>
internal static class PaletteSwapRender
{
    public static void Apply(PixelImage image, PaletteSwapEffect p, Rectangle source = default)
    {
        var pairs = p.Pairs;
        if (pairs.Count == 0) return;
        int tol = p.Tolerance;
        var px = image.Pixels;
        int w = image.Width, h = image.Height;
        if (p.ColorGradients.Count == 0)
        {
            // Solid colors: cache the first matching pair for each original pixel color (shared by rows running together).
            var memo = new ConcurrentDictionary<uint, Color32>();
            EffectParallel.For(0, h, w, y =>
            {
                var row = px.AsSpan(y * w, w);
                for (int x = 0; x < row.Length; x++)
                {
                    var c = row[x];
                    if (c.A == 0) continue;
                    if (!memo.TryGetValue(c.Argb, out var result))
                    {
                        result = c;
                        foreach (var pair in pairs)
                        {
                            var f = pair.From;
                            if (Math.Abs(c.R - f.R) > tol || Math.Abs(c.G - f.G) > tol || Math.Abs(c.B - f.B) > tol) continue;
                            result = pair.To.WithAlpha((byte)((c.A * pair.To.A + 127) / 255));
                            break;
                        }
                        memo[c.Argb] = result;
                    }
                    row[x] = result;
                }
            });
            return;
        }

        var bounds = image.GetOpaqueBounds();
        var from = new EffectGradientSampler[pairs.Count];
        var to = new EffectGradientSampler[pairs.Count];
        for (int j = 0; j < pairs.Count; j++)
        {
            from[j] = EffectGradientSampler.For(p, $"Pairs[{j}].From", bounds, pairs[j].From, source.X, source.Y);
            to[j] = EffectGradientSampler.For(p, $"Pairs[{j}].To", bounds, pairs[j].To, source.X, source.Y);
        }
        EffectParallel.For(0, h, w, y =>
        {
            var row = px.AsSpan(y * w, w);
            for (int x = 0; x < row.Length; x++)
            {
                var c = row[x];
                if (c.A == 0) continue;
                for (int j = 0; j < pairs.Count; j++)
                {
                    var f = from[j].Sample(x, y);
                    if (from[j].HasGradient && f.A == 0) continue;
                    if (Math.Abs(c.R - f.R) > tol || Math.Abs(c.G - f.G) > tol || Math.Abs(c.B - f.B) > tol) continue;
                    var target = to[j].Sample(x, y);
                    row[x] = to[j].HasGradient ? LayerEffects.Mix(c, target, target.A)
                        : target.WithAlpha((byte)((c.A * target.A + 127) / 255));
                    break;
                }
            }
        });
    }
}
