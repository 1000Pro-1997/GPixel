using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>나타나기 / 사라지기(<see cref="AppearEffect"/>) 그리기.</summary>
internal static class AppearRender
{
    public static void Apply(PixelImage image, AppearEffect e, Rectangle source)
    {
        double v = e.Visibility(e.Progress);
        if (v >= 1) return;
        var px = image.Pixels;
        if (v <= 0)
        {
            Array.Clear(px);
            return;
        }
        int w = image.Width, h = image.Height;
        switch (e.Style)
        {
            case AppearStyle.Fade:
                FadeRender.Apply(image, v);
                break;
            case AppearStyle.Dither:
                EffectParallel.For(0, h, w, y =>
                {
                    var row = px.AsSpan(y * w, w);
                    int left = source.X, by = y - source.Y;
                    for (int x = 0; x < row.Length; x++)
                        if ((Bayer.Threshold4(x - left, by) + 0.5) / 16 >= v) row[x] = Color32.Transparent;
                });
                break;
            case AppearStyle.Scatter:
                EffectParallel.For(0, h, w, y =>
                {
                    var row = px.AsSpan(y * w, w);
                    int seed = e.Seed, left = source.X, by = y - source.Y;
                    for (int x = 0; x < row.Length; x++)
                        if (row[x].A != 0 && LayerEffects.Random(seed, x - left, by, 31) >= v) row[x] = Color32.Transparent;
                });
                break;
            default:
                Wipe(image, e, v, source);
                break;
        }
    }

    /// <summary>
    /// 쓸기: 보이는 그림 범위에서 시작 쪽부터 잰 자리 p(px, 0~L−1), 선 c = v × (L + 가장자리). p + ½ &lt; c − 가장자리면 보임, 그 뒤 가장자리 안이면 2×2 디더
    /// (가장자리 1px = 50%, 2px = 선 쪽 25%·안쪽 75%), 그 밖은 지움. 사라지기는 반대쪽 끝에서 잰다(시작 쪽부터 지워짐).
    /// </summary>
    private static void Wipe(PixelImage image, AppearEffect e, double v, Rectangle source)
    {
        var b = image.GetOpaqueBounds();
        if (b.IsEmpty) return;
        bool vertical = e.From is WipeDirection.Top or WipeDirection.Bottom;
        int length = vertical ? b.Height : b.Width, edge = e.Edge;
        double c = v * (length + edge);
        int w = image.Width;
        var px = image.Pixels;
        EffectParallel.For(b.Top, b.Bottom, b.Width, y =>
        {
            var row = px.AsSpan(y * w, w);
            for (int x = b.Left; x < b.Right; x++)
            {
                if (row[x].A == 0) continue;
                int p = e.From switch
                {
                    WipeDirection.Top => y - b.Top,
                    WipeDirection.Bottom => b.Bottom - 1 - y,
                    WipeDirection.Left => x - b.Left,
                    _ => b.Right - 1 - x,
                };
                if (e.Disappear) p = length - 1 - p;
                double inside = c - (p + 0.5); // 선에서 안쪽으로 얼마나(px)
                if (inside > edge) continue;
                if (inside > 0)
                {
                    double level = edge == 1 ? 0.5 : inside > 1 ? 0.75 : 0.25;
                    if (Bayer.Threshold2(x - source.X, y - source.Y) + 0.5 < level * 4) continue;
                }
                row[x] = Color32.Transparent;
            }
        });
    }
}
