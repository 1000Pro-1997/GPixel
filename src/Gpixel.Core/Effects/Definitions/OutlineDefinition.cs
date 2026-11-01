using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>외곽선 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class OutlineDefinition
{
    public const string Id = "gpixel.effect.outline";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<OutlineEffect>(Id, "outline", OutlineRender.Apply, s =>
    [
        s.Color("color", e => e.Color, (e, v) => e with { Color = v }),
        s.Int("thickness", 1, OutlineEffect.MaxThickness, e => e.Thickness, (e, v) => e with { Thickness = v }),
        s.Bool("diagonal", e => e.Diagonal, (e, v) => e with { Diagonal = v }),
        s.Bool("inside", e => e.Inside, (e, v) => e with { Inside = v }),
        s.Bool("hideOriginal", e => e.HideOriginal, (e, v) => e with { HideOriginal = v }),
        s.Choice("colorMode", e => e.ColorMode, (e, v) => e with { ColorMode = v }, legacy: ["mode"]),
        s.Int("shadeAmount", -100, 100, e => e.ShadeAmount, (e, v) => e with { ShadeAmount = v }, legacy: ["amount"]),
    ], padding: OutlineRender.Padding, erasesSource: e => e.HideOriginal);
}
