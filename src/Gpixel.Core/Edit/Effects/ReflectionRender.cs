using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>반사(<see cref="ReflectionEffect"/>) 그리기.</summary>
internal static class ReflectionRender
{
    /// <summary>
    /// 비치는 쪽으로 간격 + 지금까지 넓힌 높이(왼쪽·오른쪽이면 너비)의 길이%, 물결 폭만큼 양옆(왼쪽·오른쪽이면 위아래)으로 넓힌다 —
    /// 보이는 영역이 아무리 커도 그 안이다.
    /// </summary>
    public static EffectPadding Padding(ReflectionEffect m, int width, int height)
    {
        int reach = m.Gap + RowsFor(m.Horizontal ? width : height, m.Height);
        int l = 0, t = 0, r = 0, b = 0;
        switch (m.Direction)
        {
            case ReflectionDirection.Up: t = reach; break;
            case ReflectionDirection.Left: l = reach; break;
            case ReflectionDirection.Right: r = reach; break;
            default: b = reach; break;
        }
        if (m.Horizontal)
        {
            t += m.Wave;
            b += m.Wave;
        }
        else
        {
            l += m.Wave;
            r += m.Wave;
        }
        return new EffectPadding(l, t, r, b);
    }

    /// <summary>길이 <paramref name="height"/>px(아래·위면 높이, 왼쪽·오른쪽이면 너비) 그림에서 비칠 줄 수(<paramref name="percent"/>%, 올림, 1줄 이상).</summary>
    public static int RowsFor(int height, int percent) => height <= 0 ? 0 : Math.Max(1, (height * percent + 99) / 100);

    /// <summary>그림 쪽부터 <paramref name="row"/>번째 줄(왼쪽·오른쪽이면 열)의 흔들림(px) — 줄은 가로로, 열은 세로로 민다.</summary>
    public static int WaveAt(int wave, int row)
        => wave == 0 ? 0 : (int)Math.Round(wave * Math.Sin(2 * Math.PI * row / ReflectionEffect.WavePeriod), MidpointRounding.AwayFromZero);

    public static void Apply(PixelImage image, ReflectionEffect m, Rectangle source)
    {
        var bounds = image.GetOpaqueBounds();
        if (bounds.IsEmpty) return;
        bool horizontal = m.Horizontal;
        int w = image.Width, h = image.Height, rows = RowsFor(horizontal ? bounds.Width : bounds.Height, m.Height);
        var tintBounds = m.Direction switch
        {
            ReflectionDirection.Up => Rectangle.FromLTRB(bounds.Left, bounds.Top - m.Gap - rows, bounds.Right, bounds.Bottom),
            ReflectionDirection.Left => Rectangle.FromLTRB(bounds.Left - m.Gap - rows, bounds.Top, bounds.Right, bounds.Bottom),
            ReflectionDirection.Right => Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right + m.Gap + rows, bounds.Bottom),
            _ => Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom + m.Gap + rows),
        };
        var tintColor = EffectGradientSampler.For(m, nameof(m.Tint), tintBounds, m.Tint, source.X, source.Y);
        var px = image.Pixels;
        // 줄(왼쪽·오른쪽이면 열) j: 그림 끝에서 j번째 줄을 간격 너머 j번째 줄에 비춘다 — 비친 줄은 모두 보이는 영역 밖이라 읽는 줄과 겹치지 않고
        // 줄마다 다른 줄에 쓰므로 줄끼리 나눠 돈다. 줄을 따라가는 k는 아래·위면 x, 왼쪽·오른쪽이면 y이고 물결은 k 방향으로 민다.
        int alongStart = horizontal ? bounds.Top : bounds.Left, alongEnd = horizontal ? bounds.Bottom : bounds.Right;
        EffectParallel.For(0, rows, alongEnd - alongStart, j =>
        {
            var (from, to) = m.Direction switch
            {
                ReflectionDirection.Up => (bounds.Top + j, bounds.Top - 1 - m.Gap - j),
                ReflectionDirection.Left => (bounds.Left + j, bounds.Left - 1 - m.Gap - j),
                ReflectionDirection.Right => (bounds.Right - 1 - j, bounds.Right + m.Gap + j),
                _ => (bounds.Bottom - 1 - j, bounds.Bottom + m.Gap + j),
            };
            if ((uint)to >= (uint)(horizontal ? w : h)) return;
            double opacity = (rows == 1 ? m.OpacityStart : m.OpacityStart + (m.OpacityEnd - m.OpacityStart) * (double)j / (rows - 1)) / 100;
            if (opacity <= 0) return;
            int shift = WaveAt(m.Wave, j);
            for (int k = alongStart; k < alongEnd; k++)
            {
                var c = horizontal ? px[k * w + from] : px[from * w + k];
                if (c.A == 0) continue;
                int tx = horizontal ? to : k + shift, ty = horizontal ? k + shift : to;
                if ((uint)tx >= (uint)w || (uint)ty >= (uint)h) continue;
                if (m.DitherFade)
                {
                    if (!Bayer.On(EffectDither.Bayer4, opacity, tx - source.X, ty - source.Y)) continue;
                }
                else
                {
                    byte a = (byte)Math.Round(c.A * opacity, MidpointRounding.AwayFromZero);
                    if (a == 0) continue;
                    c = c.WithAlpha(a);
                }
                var tint = tintColor.Sample(tx, ty);
                if (tint.A != 0) c = LayerEffects.Mix(c, tint, tint.A);
                int i = ty * w + tx;
                px[i] = Color32.BlendOver(c, px[i]);
            }
        });
    }
}
