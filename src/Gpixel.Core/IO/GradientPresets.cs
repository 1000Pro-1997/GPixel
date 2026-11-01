using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.IO;

/// <summary>A named snapshot of gradient colors, opacity and effect geometry.</summary>
public sealed record GradientPreset
{
    private IReadOnlyList<GradientStop> _stops = Array.AsReadOnly(new[]
    {
        new GradientStop(0, Color32.White), new GradientStop(1, Color32.White),
    });
    private IReadOnlyList<GradientAlphaStop> _alphaStops = Array.AsReadOnly(new[]
    {
        new GradientAlphaStop(0, 255), new GradientAlphaStop(1, 255),
    });

    public string Name { get; init; } = "";
    public IReadOnlyList<GradientStop> Stops
    {
        get => _stops;
        init => _stops = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    public IReadOnlyList<GradientAlphaStop> AlphaStops
    {
        get => _alphaStops;
        init => _alphaStops = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    public GradientInterpolation Interpolation { get; init; } = GradientInterpolation.Smooth;
    public GradientDither Dither { get; init; } = GradientDither.None;
    public GradientColorSpace ColorSpace { get; init; } = GradientColorSpace.OkLch;
    public int Angle { get; init; }
    public EffectGradientShape Shape { get; init; } = EffectGradientShape.Linear;
    public bool Animated { get; init; }
    public bool Reverse { get; init; }
    public int Cycles { get; init; } = 1;
    public EffectEasing Easing { get; init; } = EffectEasing.Linear;
    public bool PingPong { get; init; }
    public bool Favorite { get; init; }

    /// <summary>Copy the editable effect settings; an absent alpha curve is an opaque curve.</summary>
    public static GradientPreset Capture(string name, EffectColorGradient gradient) => new()
    {
        Name = name.Trim(), Stops = gradient.Stops,
        AlphaStops = gradient.AlphaStops.Count == 0
            ? [new GradientAlphaStop(0, 255), new GradientAlphaStop(1, 255)] : gradient.AlphaStops,
        Interpolation = gradient.Interpolation, Dither = gradient.Dither, ColorSpace = gradient.ColorSpace,
        Angle = gradient.Angle, Shape = gradient.Shape, Animated = gradient.Animated,
        Reverse = gradient.Reverse, Cycles = gradient.Cycles, Easing = gradient.Easing, PingPong = gradient.PingPong,
    };

    /// <summary>Make a separate effect value; callers applying to the paint tool use only color settings.</summary>
    public EffectColorGradient ToGradient() => new()
    {
        Stops = Stops, AlphaStops = AlphaStops, Interpolation = Interpolation, Dither = Dither,
        ColorSpace = ColorSpace, Angle = Angle, Shape = Shape, Animated = Animated,
        Reverse = Reverse, Cycles = Cycles, Easing = Easing, PingPong = PingPong,
    };
}

/// <summary>기본 그라데이션. 사용자 그라데이션은 개인 에셋(<see cref="Assets.AssetStore"/>)에 있고, 이 목록은 첫 실행과 '기본값으로 초기화'에서 쓴다.</summary>
public static class GradientPresets
{
    /// <summary>기본 그라데이션(코드에 고정 — 사용자가 지워도 '기본값으로 초기화'로 되돌림).</summary>
    public static List<GradientPreset> BuiltIns()
    {
        var opaque = new[] { new GradientAlphaStop(0, 255), new GradientAlphaStop(1, 255) };
        var rainbow = new[]
        {
            new GradientStop(0, new Color32(255, 0, 0)),
            new GradientStop(0.2, new Color32(255, 140, 0)),
            new GradientStop(0.4, new Color32(255, 255, 0)),
            new GradientStop(0.6, new Color32(0, 190, 50)),
            new GradientStop(0.8, new Color32(0, 80, 255)),
            new GradientStop(1, new Color32(148, 0, 211)),
        };
        return
        [
            new GradientPreset { Name = "Rainbow", Stops = rainbow, AlphaStops = opaque },
            new GradientPreset { Name = "Repeating Rainbow", Stops = [new(0, rainbow[0].Color), new(0.17, rainbow[1].Color), new(0.34, rainbow[2].Color), new(0.51, rainbow[3].Color), new(0.68, rainbow[4].Color), new(0.85, rainbow[5].Color), new(1, rainbow[0].Color)], AlphaStops = opaque },
            new GradientPreset { Name = "Sunset", Stops = [new(0, new Color32(30, 16, 77)), new(0.45, new Color32(219, 54, 97)), new(1, new Color32(255, 204, 90))], AlphaStops = opaque },
            new GradientPreset { Name = "Ocean", Stops = [new(0, new Color32(3, 31, 65)), new(0.5, new Color32(12, 120, 171)), new(1, new Color32(106, 224, 220))], AlphaStops = opaque },
            new GradientPreset { Name = "Transparent Fade", Stops = [new(0, Color32.White), new(1, Color32.White)], AlphaStops = [new(0, 255), new(1, 0)] },
            new GradientPreset { Name = "Fire", Stops = [new(0, new Color32(40, 4, 4)), new(0.35, new Color32(190, 30, 20)), new(0.7, new Color32(255, 140, 20)), new(1, new Color32(255, 240, 150))], AlphaStops = opaque },
            new GradientPreset { Name = "Forest", Stops = [new(0, new Color32(12, 36, 22)), new(0.5, new Color32(46, 120, 60)), new(1, new Color32(180, 220, 110))], AlphaStops = opaque },
            new GradientPreset { Name = "Grayscale", Stops = [new(0, Color32.Black), new(1, Color32.White)], AlphaStops = opaque, ColorSpace = GradientColorSpace.Rgb },
        ];
    }
}
