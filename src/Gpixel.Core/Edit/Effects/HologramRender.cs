using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>
/// 홀로그램(<see cref="HologramEffect"/>) 그리기. 차례: 글리치 조각 밀기 → 보이는 픽셀마다 색(명암 그대로) → 밝은 띠 또는 주사선 → RGB 분리(글리치 프레임)
/// → 알파 × 투명도 × 깜빡임(디더면 Bayer 칸만) → 테두리 빛. 주사선·디더 무늬는 원본 왼쪽 위 기준, 띠·글리치 조각은 보이는 그림 범위 기준이라
/// 캔버스·부위 아틀라스에서 같다.
/// </summary>
internal static class HologramRender
{
    /// <summary>글리치 조각이 좌우로 최대 밀림만큼, 테두리 빛이면 사방으로 1px 더(밀린 조각 둘레까지) 넓힌다.</summary>
    public static EffectPadding Padding(HologramEffect g, int width, int height)
    {
        int edge = g.EdgeGlow ? 1 : 0;
        return new EffectPadding(g.GlitchReach + edge, edge, g.GlitchReach + edge, edge);
    }

    public static void Apply(PixelImage image, HologramEffect e, Rectangle source)
    {
        var bounds = image.GetOpaqueBounds();
        if (bounds.IsEmpty) return;
        int w = image.Width, h = image.Height, step = e.FrameStep;
        var px = image.Pixels;
        var colors = EffectGradientSampler.For(e, nameof(e.Color), bounds, e.Color, source.X, source.Y);

        var slices = e.GlitchSlices(step, bounds.Height);
        if (slices.Count > 0) Glitch(px, w, bounds, slices);

        double u = e.Progress, flicker = e.FlickerAt(step);
        int tint = (e.TintStrength * 255 + 50) / 100, band = e.Band ? (e.BandStrength * 255 + 50) / 100 : 0, scan = e.ScanStrength;
        var solidBandLight = LayerEffects.Mix(e.Color, Color32.White, 128);
        int gap = e.ScanGap, offset = e.ScanOffset(u);
        // 띠 위 끝: 진행 0 = 보이는 그림 바로 위(두께만큼), 1 = 바로 아래 — 양 끝 프레임엔 닿는 픽셀이 없다
        int bandTop = (int)Math.Round(bounds.Top - e.BandSize + (bounds.Bottom - bounds.Top + e.BandSize) * u, MidpointRounding.AwayFromZero);
        double opacity = e.Opacity / 100.0 * flicker;
        bool split = e.RgbSplit && slices.Count > 0;

        var shape = EffectBuffers.Rent<bool>(w * h);
        var rgb = EffectBuffers.Rent<Color32>(w * h);
        try
        {
            LayerEffects.Mask(px, shape, w, h);
            // 줄마다: 색·띠·주사선(알파는 그대로)을 줄 전체에 먼저 만들고(RGB 분리가 같은 줄 이웃의 결과를 봄) 알파를 줄인다.
            // 모양은 글리치가 옆으로만 미므로 보이는 그림 범위의 줄 안에 있다.
            EffectParallel.For(bounds.Top, bounds.Bottom, w, y =>
            {
                bool inBand = band > 0 && y >= bandTop && y < bandTop + e.BandSize;
                bool scanRow = !inBand && scan > 0 && ((y - source.Y - offset) % gap + gap) % gap == 0;
                double keep = opacity * (scanRow && e.ScanStyle == HologramScanStyle.Fade ? 1 - scan / 100.0 : 1);
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (!shape[i]) continue;
                    var sampled = colors.Sample(x, y);
                    int tintAmount = colors.HasGradient ? (tint * sampled.A + 127) / 255 : tint;
                    var c = LayerEffects.Mix(px[i], Tint(px[i], sampled), tintAmount);
                    if (inBand)
                    {
                        var bandLight = colors.HasGradient ? LayerEffects.Mix(sampled, Color32.White, 128) : solidBandLight;
                        int bandAmount = colors.HasGradient ? (band * sampled.A + 127) / 255 : band;
                        c = LayerEffects.Screen(c, bandLight, bandAmount);
                    }
                    else if (scanRow && e.ScanStyle == HologramScanStyle.Darken)
                        c = Darken(c, scan);
                    rgb[i] = c;
                }
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (!shape[i]) continue;
                    var c = rgb[i];
                    if (split)
                    {
                        byte r = x > 0 && shape[i - 1] ? rgb[i - 1].R : (byte)0, b = x + 1 < w && shape[i + 1] ? rgb[i + 1].B : (byte)0;
                        c = new Color32(r, c.G, b, c.A);
                    }
                    px[i] = Fade(c, keep, e.Dither, x - source.X, y - source.Y);
                }
            });

            if (e.EdgeGlow && e.EdgeOpacity > 0) Glow(image, shape, e, opacity * e.EdgeOpacity / 100.0, source, colors);
        }
        finally
        {
            EffectBuffers.Return(shape);
            EffectBuffers.Return(rgb);
        }
    }

    /// <summary>글리치: 조각 줄들을 좌우로 민다(원래 그림에서 읽음 — 겹치면 뒤 조각이 이김, 밀려 나간 자리는 투명).</summary>
    private static void Glitch(Color32[] px, int w, Rectangle bounds, IReadOnlyList<(int Top, int Height, int Shift)> slices)
    {
        // 조각이 닿는 줄만 원래 그림을 따로 둔다
        int first = int.MaxValue, last = int.MinValue;
        foreach (var (top, height, _) in slices)
        {
            first = Math.Min(first, bounds.Top + top);
            last = Math.Max(last, Math.Min(bounds.Bottom, bounds.Top + top + height));
        }
        if (last <= first) return;
        var src = EffectBuffers.Rent<Color32>((last - first) * w);
        try
        {
            Array.Copy(px, first * w, src, 0, (last - first) * w);
            foreach (var (top, height, shift) in slices)
                for (int y = bounds.Top + top; y < Math.Min(bounds.Bottom, bounds.Top + top + height); y++)
                {
                    int row = y * w, from = (y - first) * w;
                    for (int x = 0; x < w; x++)
                    {
                        int sx = x - shift;
                        px[row + x] = (uint)sx < (uint)w ? src[from + sx] : Color32.Transparent;
                    }
                }
        }
        finally
        {
            EffectBuffers.Return(src);
        }
    }

    /// <summary>
    /// 홀로그램 색: 밝기 L(0~255)에서 목표 = 색 × (0.3 + 0.7 × L/192)(L ≤ 192), 그 위는 색에서 흰색 쪽으로 0.6 × (L − 192)/63. 채널마다 L에 단조(명암 순서 유지).
    /// </summary>
    internal static Color32 Tint(Color32 c, Color32 color)
    {
        double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        byte T(byte k) => l <= 192
            ? (byte)Math.Round(k * (0.3 + 0.7 * l / 192), MidpointRounding.AwayFromZero)
            : (byte)Math.Round(k + (255 - k) * 0.6 * (l - 192) / 63, MidpointRounding.AwayFromZero);
        return new Color32(T(color.R), T(color.G), T(color.B), c.A);
    }

    private static Color32 Darken(Color32 c, int strength)
    {
        int keep = 100 - strength;
        byte D(byte v) => (byte)((v * keep + 50) / 100);
        return new Color32(D(c.R), D(c.G), D(c.B), c.A);
    }

    /// <summary>알파 × <paramref name="keep"/>(디더면 4×4 Bayer 칸만 알파 그대로, 나머지는 투명).</summary>
    private static Color32 Fade(Color32 c, double keep, bool dither, int bx, int by)
    {
        if (keep >= 1) return c;
        if (dither) return Bayer.On(EffectDither.Bayer4, keep, bx, by) ? c : Color32.Transparent;
        byte a = (byte)Math.Round(c.A * keep, MidpointRounding.AwayFromZero);
        return a == 0 ? Color32.Transparent : c.WithAlpha(a);
    }

    /// <summary>테두리 빛: 모양 밖 4방향 이웃 1px에 색(알파 255 × <paramref name="keep"/>, 디더면 무늬 칸만 불투명).</summary>
    private static void Glow(PixelImage image, bool[] shape, HologramEffect e, double keep, Rectangle source, EffectGradientSampler colors)
    {
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        var solid = e.Color.WithAlpha(255);
        bool In(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h && shape[y * w + x];
        // 모양(읽기만)의 밖 칸만 쓰므로 줄끼리 겹치지 않는다
        EffectParallel.For(0, h, w, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (shape[i] || !(In(x - 1, y) || In(x + 1, y) || In(x, y - 1) || In(x, y + 1))) continue;
                var sampled = colors.Sample(x, y);
                var c = Fade(colors.HasGradient ? sampled.WithAlpha(255) : solid,
                    keep * (colors.HasGradient ? sampled.A / 255.0 : 1), e.Dither, x - source.X, y - source.Y);
                if (c.A != 0) px[i] = c; // 모양 밖은 투명이라 그대로 놓는다
            }
        });
    }
}
