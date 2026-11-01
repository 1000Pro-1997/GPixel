using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>안쪽 빛 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class InnerGlowDefinition
{
    public const string Id = "gpixel.effect.innerglow";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<InnerGlowEffect>(Id, "innerglow", InnerGlowRender.Apply, s =>
    [
        s.Color("color", e => e.Color, (e, v) => e with { Color = v }),
        s.Int("size", 1, InnerGlowEffect.MaxSize, e => e.Size, (e, v) => e with { Size = v }, legacy: ["thickness"]),
        s.Int("strength", 0, 100, e => e.Strength, (e, v) => e with { Strength = v }, legacy: ["amount"]),
        s.Bool("fade", e => e.Fade, (e, v) => e with { Fade = v }),
        s.Choice("blend", e => e.Blend, (e, v) => e with { Blend = v }),
    ]);
}
