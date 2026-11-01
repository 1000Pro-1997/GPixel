using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>회전 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class RotateDefinition
{
    public const string Id = "gpixel.effect.rotate";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<RotateEffect>(Id, "rotate", RotateRender.Apply, s =>
    [
        s.Int("angle", -RotateEffect.MaxAngle, RotateEffect.MaxAngle, e => e.Angle, (e, v) => e with { Angle = v }),
        s.Choice("anchor", e => e.Anchor, (e, v) => e with { Anchor = v }),
    ], padding: RotateRender.Padding,
        // 예전 파일의 방향(clockwise: false = 반시계) — 이제는 각도의 부호
        legacyFixup: (flat, e) => EffectJson.Bool(flat["clockwise"]) == false ? e with { Angle = -Math.Abs(e.Angle) } : e);
}
