using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>안쪽 빛(<see cref="InnerGlowEffect"/>) 그리기.</summary>
internal static class InnerGlowRender
{
    public static void Apply(PixelImage image, InnerGlowEffect g, Rectangle source = default)
    {
        int amount = (g.Strength * 255 + 50) / 100;
        if (amount == 0) return;
        int w = image.Width, h = image.Height, n = g.Size;
        var px = image.Pixels;
        var color = EffectGradientSampler.For(g, nameof(g.Color), image.GetOpaqueBounds(), g.Color, source.X, source.Y);
        var outer = EffectBuffers.Rent<bool>(w * h);
        var inner = EffectBuffers.Rent<bool>(w * h);
        try
        {
            LayerEffects.Mask(px, outer, w, h);
            // k번째 껍질 = (k−1)번 깎은 모양에서 k번 깎은 모양을 뺀 것(가장자리에서 k px 안쪽)
            for (int k = 1; k <= n; k++)
            {
                LayerEffects.Erode(outer, inner, w, h, diagonal: true);
                int a = g.Fade ? amount * (n - k + 1) / n : amount;
                bool any = false;
                var shell = outer;
                var rest = inner;
                EffectParallel.For(0, h, w, y =>
                {
                    bool found = false;
                    var row = px.AsSpan(y * w, w);
                    var edge = shell.AsSpan(y * w, w);
                    var deeper = rest.AsSpan(y * w, w);
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (!edge[x]) continue;
                        found = true;
                        if (!deeper[x])
                        {
                            var sampled = color.Sample(x, y);
                            int strength = color.HasGradient ? (a * sampled.A + 127) / 255 : a;
                            row[x] = LayerEffects.Blend(row[x], sampled, strength, g.Blend);
                        }
                    }
                    if (found) any = true; // 여러 줄이 같은 값(true)만 쓴다
                });
                if (!any) break;
                (outer, inner) = (inner, outer);
            }
        }
        finally
        {
            EffectBuffers.Return(outer);
            EffectBuffers.Return(inner);
        }
    }
}
