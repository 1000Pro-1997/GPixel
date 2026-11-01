using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>잔상(<see cref="AfterimageEffect"/>) 그리기: 앞 프레임 원본들을 옅게 깐 뒤 지금 그림을 그 위에 올린다.</summary>
internal static class AfterimageRender
{
    public static void Apply(PixelImage image, AfterimageEffect e, Rectangle source)
    {
        if (e.FrameSource is not { } frameSource) return;
        int w = image.Width, h = image.Height, n = w * h;
        var tintColor = EffectGradientSampler.For(e, nameof(e.TintColor), source, e.TintColor, source.X, source.Y);
        Color32[]? ghosts = null;
        try
        {
            for (int k = e.Count; k >= 1; k--) // 먼 잔상부터 깔고 가까운 잔상을 위에 — 잔상끼리는 차례대로, 한 잔상 안에서는 줄마다 나눠서
            {
                int back = k * e.Interval;
                if (back > e.FrameStep) continue; // 범위 안 프레임만
                if (frameSource(e.FrameIndex - back) is not { } raw || raw.Width != source.Width || raw.Height != source.Height) continue;
                byte opacity = e.GhostOpacity(k);
                if (opacity == 0) continue;
                if (ghosts is null)
                {
                    ghosts = EffectBuffers.Rent<Color32>(n);
                    Array.Clear(ghosts, 0, n);
                }
                var rendered = e.RenderedFrameSource?.Invoke(e.FrameIndex - back, raw);
                var ghost = rendered?.Image ?? raw;
                int originX = source.X + (rendered?.OffsetX ?? 0), originY = source.Y + (rendered?.OffsetY ?? 0);
                var gp = ghost.Pixels;
                var layer = ghosts;
                EffectParallel.For(0, ghost.Height, ghost.Width, gy =>
                {
                    int y = gy + originY;
                    if ((uint)y >= (uint)h) return;
                    for (int gx = 0; gx < ghost.Width; gx++)
                    {
                        int x = gx + originX;
                        if ((uint)x >= (uint)w) continue;
                        var c = gp[gy * ghost.Width + gx];
                        if (c.A == 0) continue;
                        if (e.Tint)
                        {
                            var tint = tintColor.Sample(x, y);
                            c = LayerEffects.Mix(c, tint, tint.A);
                        }
                        byte a = (byte)((c.A * opacity + 127) / 255);
                        if (a == 0) continue;
                        int i = y * w + x;
                        layer[i] = Color32.BlendOver(layer[i], c.WithAlpha(a));
                    }
                });
            }
            if (ghosts is null) return;
            var px = image.Pixels;
            var under = ghosts;
            EffectParallel.For(0, h, w, y =>
            {
                var ghostRow = under.AsSpan(y * w, w);
                var row = px.AsSpan(y * w, w);
                for (int x = 0; x < row.Length; x++)
                    if (ghostRow[x].A != 0) row[x] = Color32.BlendOver(ghostRow[x], row[x]);
            });
        }
        finally
        {
            if (ghosts is not null) EffectBuffers.Return(ghosts);
        }
    }
}
