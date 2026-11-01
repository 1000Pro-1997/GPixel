using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>깜빡이기(<see cref="BlinkEffect"/>) 그리기.</summary>
internal static class BlinkRender
{
    public static void Apply(PixelImage image, BlinkEffect e, Rectangle source)
    {
        if (!e.Hidden) return;
        var px = image.Pixels;
        int w = image.Width, h = image.Height;
        switch (e.Hide)
        {
            case BlinkHide.Transparent:
                Array.Clear(px);
                break;
            case BlinkHide.Translucent:
                EffectParallel.For(0, h, w, y =>
                {
                    var row = px.AsSpan(y * w, w);
                    int opacity = e.Opacity;
                    for (int x = 0; x < row.Length; x++)
                    {
                        var c = row[x];
                        if (c.A == 0) continue;
                        byte a = (byte)((c.A * opacity + 50) / 100);
                        row[x] = a == 0 ? Color32.Transparent : c.WithAlpha(a);
                    }
                });
                break;
            default:
                EffectParallel.For(0, h, w, y =>
                {
                    var row = px.AsSpan(y * w, w);
                    int offset = y - source.Y - source.X;
                    for (int x = 0; x < row.Length; x++)
                        if (((x + offset) & 1) != 0) row[x] = Color32.Transparent;
                });
                break;
        }
    }
}
