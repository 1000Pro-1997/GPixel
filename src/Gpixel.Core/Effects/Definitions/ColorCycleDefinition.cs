using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>색 순환 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ColorCycleDefinition
{
    public const string Id = "gpixel.effect.colorcycle";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ColorCycleEffect>(Id, "colorcycle", ColorCycleRender.Apply, s =>
    [
        s.ColorList("colors", e => e.Colors, (e, v) => e with { Colors = v }),
        s.Int("steps", 1, ColorCycleEffect.MaxSteps, e => e.Steps, (e, v) => e with { Steps = v }),
        s.Bool("reverse", e => e.Reverse, (e, v) => e with { Reverse = v }),
    ]);
}
