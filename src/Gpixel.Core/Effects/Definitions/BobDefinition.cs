using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>둥실둥실 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class BobDefinition
{
    public const string Id = "gpixel.effect.bob";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<BobEffect>(Id, "bob", (image, e, _) => ShakeRender.Apply(image, e), s =>
    [
        s.Int("height", 1, BobEffect.MaxHeight, e => e.Height, (e, v) => e with { Height = v }),
        s.Int("cycles", 1, BobEffect.MaxCycles, e => e.Cycles, (e, v) => e with { Cycles = v }, legacy: ["count"]),
        s.Choice("axis", e => e.Axis, (e, v) => e with { Axis = v }, choices: [EffectAxis.Vertical, EffectAxis.Horizontal]),
    ], padding: ShakeRender.Padding);
}
