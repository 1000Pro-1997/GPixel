using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>흔들기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ShakeDefinition
{
    public const string Id = "gpixel.effect.shake";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ShakeEffect>(Id, "shake", (image, e, _) => ShakeRender.Apply(image, e), s =>
    [
        s.Choice("axis", e => e.Axis, (e, v) => e with { Axis = v }),
        s.Int("amount", 1, ShakeEffect.MaxAmount, e => e.Amount, (e, v) => e with { Amount = v }),
        s.Int("count", 1, ShakeEffect.MaxCount, e => e.Count, (e, v) => e with { Count = v }),
        s.Bool("decay", e => e.Decay, (e, v) => e with { Decay = v }),
        s.Bool("regular", e => e.Regular, (e, v) => e with { Regular = v }),
        s.Int("seed", 0, ShakeEffect.MaxSeed, e => e.Seed, (e, v) => e with { Seed = v }),
    ], padding: ShakeRender.Padding);
}
