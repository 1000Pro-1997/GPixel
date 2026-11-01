using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>빛 지나가기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ShineDefinition
{
    public const string Id = "gpixel.effect.shine";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ShineEffect>(Id, "shine", ShineRender.Apply, s =>
    [
        s.Int("angle", 0, 359, e => e.Angle, (e, v) => e with { Angle = v }),
        s.Int("width", 1, ShineEffect.MaxWidth, e => e.Width, (e, v) => e with { Width = v }),
        s.Int("strength", 0, 100, e => e.Strength, (e, v) => e with { Strength = v }, legacy: ["amount"]),
        s.Color("color", e => e.Color, (e, v) => e with { Color = v }),
        s.Int("softness", 0, ShineEffect.MaxSoftness, e => e.Softness, (e, v) => e with { Softness = v }),
    ]);
}
