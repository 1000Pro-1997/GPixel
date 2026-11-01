using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Assets;

/// <summary>
/// 에셋 미리보기 그림: 효과 묶음을 견본(또는 지금 그림)에 입힌 모습, 그라데이션 띠. 효과 창 상자, 에셋 관리자 상세, 내보내기 PNG가 같이 쓴다.
/// </summary>
public static class AssetPreview
{
    /// <summary>움직이는 효과 미리보기의 칸 수(효과 창 상자와 같음).</summary>
    public const int Steps = 12;

    /// <summary>내보내기 PNG의 긴 변(32px 견본 × 배율, 효과 바깥 여백 포함 후 맞춤).</summary>
    public const int PngMaxSize = 128;

    private static readonly PixelImage Chick = LoadChick();

    private static PixelImage LoadChick()
    {
        using var stream = typeof(AssetPreview).Assembly.GetManifestResourceStream("Gpixel.Assets.preview-chick.png")
            ?? throw new InvalidOperationException("Embedded chick preview is missing.");
        return PngIO.Load(stream);
    }

    /// <summary>제공된 32×32 RGBA 픽셀 병아리 견본의 독립적인 사본.</summary>
    public static PixelImage SampleSprite() => Chick.Clone();

    /// <summary>
    /// 효과 묶음을 <paramref name="source"/>에 입힌 그림 — 움직이는 효과는 <see cref="Steps"/>칸 중 <paramref name="step"/>번째.
    /// 다른 프레임을 쓰는 효과(잔상)는 앞 프레임과 달라야 보이므로 원본이 한 칸씩 오른쪽으로 가는 것처럼 만든다.
    /// </summary>
    public static PixelImage Render(IReadOnlyList<LayerEffect> template, PixelImage source, int step)
    {
        var src = source;
        Func<int, PixelImage?>? frames = null;
        if (template.Any(effect => EffectRegistry.Of(effect) is { } definition && definition.UsesOtherFrames(effect)))
        {
            frames = frame => Shifted(source, frame);
            src = Shifted(source, step);
        }
        var animated = LayerEffects.Animate(template, step, Steps, null, frames);
        return LayerEffects.Render(src, animated, LayerEffects.Padding(template, src.Width, src.Height)).Image;
    }

    private static PixelImage Shifted(PixelImage src, int frame)
    {
        var img = new PixelImage(src.Width + Steps, src.Height);
        img.Blit(src, Math.Clamp(frame, 0, Steps - 1), 0);
        return img;
    }

    /// <summary>그라데이션 띠(가로 위치 0→1, 디더는 칸 좌표로).</summary>
    public static PixelImage RenderGradient(GradientPreset gradient, int width = 128, int height = 16)
    {
        var img = new PixelImage(width, height);
        var stops = Gradient.Sorted(gradient.Stops);
        var alpha = Gradient.SortedAlpha(gradient.AlphaStops);
        for (int x = 0; x < width; x++)
        {
            double t = width == 1 ? 0 : x / (double)(width - 1);
            for (int y = 0; y < height; y++)
                img[x, y] = Gradient.Sample(stops, alpha, t, gradient.Interpolation, gradient.Dither, x, y, gradient.ColorSpace);
        }
        return img;
    }

    /// <summary>
    /// 패키지에 넣을 미리보기 PNG: 레시피(스크립트 에셋은 기본 설정 효과 하나)는 견본에 입힌 가운데 칸을 정수 배로 키운 그림, 그라데이션은 띠.
    /// 스크립트는 실행을 허락한 원문만 그린다. 모르는 종류면 null.
    /// </summary>
    public static byte[]? Png(IAssetData data)
    {
        PixelImage? image = data switch
        {
            EffectRecipe recipe => Upscale(Render(recipe.Effects, SampleSprite(), Steps / 2), PngMaxSize),
            ScriptAsset script => Upscale(Render(script.ToRecipe().Effects, SampleSprite(), Steps / 2), PngMaxSize),
            GradientAsset gradient => RenderGradient(gradient.Gradient, PngMaxSize, 16),
            _ => null,
        };
        if (image is null) return null;
        using var stream = new MemoryStream();
        PngIO.Save(image, stream);
        return stream.ToArray();
    }

    /// <summary>긴 변이 <paramref name="max"/>를 넘지 않는 가장 큰 정수 배로 키운다(가장 가까운 칸).</summary>
    public static PixelImage Upscale(PixelImage src, int max)
    {
        int scale = Math.Max(1, max / Math.Max(src.Width, src.Height));
        if (scale == 1) return src;
        var img = new PixelImage(src.Width * scale, src.Height * scale);
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                img[x, y] = src[x / scale, y / scale];
        return img;
    }
}
