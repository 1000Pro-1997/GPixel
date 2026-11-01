using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>색조 보정 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ColorAdjustDefinition
{
    public const string Id = "gpixel.effect.coloradjust";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ColorAdjustEffect>(Id, "adjust", (image, e, _) => ColorAdjustRender.Apply(image, e), s =>
    [
        s.Choice("mode", e => e.Mode, (e, v) => e with { Mode = v }),
        s.Int("amount", 0, 100, e => e.Amount, (e, v) => e with { Amount = v }),
        s.Int("hue", -ColorAdjustEffect.MaxHue, ColorAdjustEffect.MaxHue, e => e.Hue, (e, v) => e with { Hue = v }),
        s.Int("brightness", -100, 100, e => e.Brightness, (e, v) => e with { Brightness = v }),
        s.Int("contrast", -100, 100, e => e.Contrast, (e, v) => e with { Contrast = v }),
        s.Int("saturation", -100, 100, e => e.Saturation, (e, v) => e with { Saturation = v }),
    ]);
}
