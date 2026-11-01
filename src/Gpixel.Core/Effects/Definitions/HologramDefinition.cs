using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>홀로그램 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class HologramDefinition
{
    public const string Id = "gpixel.effect.hologram";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<HologramEffect>(Id, "hologram", HologramRender.Apply, s =>
    [
        s.Color("color", e => e.Color, (e, v) => e with { Color = v }),
        s.Int("tintStrength", 0, 100, e => e.TintStrength, (e, v) => e with { TintStrength = v }, legacy: ["amount"]),
        s.Int("scanGap", HologramEffect.MinScanGap, HologramEffect.MaxScanGap, e => e.ScanGap, (e, v) => e with { ScanGap = v }, legacy: ["gap"]),
        s.Int("scanStrength", 0, 100, e => e.ScanStrength, (e, v) => e with { ScanStrength = v }),
        s.Choice("scanStyle", e => e.ScanStyle, (e, v) => e with { ScanStyle = v }, legacy: ["style"]),
        s.Bool("scanScroll", e => e.ScanScroll, (e, v) => e with { ScanScroll = v }, legacy: ["scroll"]),
        s.Bool("band", e => e.Band, (e, v) => e with { Band = v }),
        s.Int("bandSize", 1, HologramEffect.MaxBandSize, e => e.BandSize, (e, v) => e with { BandSize = v }, legacy: ["thickness"]),
        s.Int("bandStrength", 0, 100, e => e.BandStrength, (e, v) => e with { BandStrength = v }),
        s.Int("opacity", 0, 100, e => e.Opacity, (e, v) => e with { Opacity = v }),
        s.Bool("dither", e => e.Dither, (e, v) => e with { Dither = v }, legacy: ["ditherFade"]),
        s.Int("flickerRate", 0, 100, e => e.FlickerRate, (e, v) => e with { FlickerRate = v }),
        s.Int("flickerStrength", 0, 100, e => e.FlickerStrength, (e, v) => e with { FlickerStrength = v }),
        s.Int("glitchRate", 0, 100, e => e.GlitchRate, (e, v) => e with { GlitchRate = v }),
        s.Int("glitchShift", 1, HologramEffect.MaxGlitchShift, e => e.GlitchShift, (e, v) => e with { GlitchShift = v }, legacy: ["distance"]),
        s.Bool("rgbSplit", e => e.RgbSplit, (e, v) => e with { RgbSplit = v }),
        s.Bool("edgeGlow", e => e.EdgeGlow, (e, v) => e with { EdgeGlow = v }),
        s.Int("edgeOpacity", 0, 100, e => e.EdgeOpacity, (e, v) => e with { EdgeOpacity = v }),
        s.Int("seed", 0, HologramEffect.MaxSeed, e => e.Seed, (e, v) => e with { Seed = v }),
    ], padding: HologramRender.Padding);
}
