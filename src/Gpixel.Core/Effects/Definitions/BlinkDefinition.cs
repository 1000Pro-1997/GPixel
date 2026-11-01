using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>깜빡이기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class BlinkDefinition
{
    public const string Id = "gpixel.effect.blink";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<BlinkEffect>(Id, "blink", BlinkRender.Apply, s =>
    [
        s.Choice("timing", e => e.Timing, (e, v) => e with { Timing = v }, legacy: ["mode"]),
        s.Int("onFrames", 1, BlinkEffect.MaxFrames, e => e.OnFrames, (e, v) => e with { OnFrames = v }),
        s.Int("offFrames", 1, BlinkEffect.MaxFrames, e => e.OffFrames, (e, v) => e with { OffFrames = v }),
        s.Int("count", 1, BlinkEffect.MaxCount, e => e.Count, (e, v) => e with { Count = v }),
        s.Choice("hide", e => e.Hide, (e, v) => e with { Hide = v }, legacy: ["style"]),
        s.Int("opacity", 0, 100, e => e.Opacity, (e, v) => e with { Opacity = v }),
    ]);
}
