using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>그림자 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ShadowDefinition
{
    public const string Id = "gpixel.effect.shadow";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ShadowEffect>(Id, "shadow", ShadowRender.Apply, s =>
    [
        s.Color("color", e => e.Color, (e, v) => e with { Color = v }),
        s.Int("offsetX", -ShadowEffect.MaxOffset, ShadowEffect.MaxOffset, e => e.OffsetX, (e, v) => e with { OffsetX = v }, legacy: ["x"]),
        s.Int("offsetY", -ShadowEffect.MaxOffset, ShadowEffect.MaxOffset, e => e.OffsetY, (e, v) => e with { OffsetY = v }, legacy: ["y"]),
        s.Choice("dither", e => e.Dither, (e, v) => e with { Dither = v }),
    ], padding: ShadowRender.Padding);
}
