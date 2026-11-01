using Gpixel.Core.Scripting;

namespace Gpixel.Core.Model;

/// <summary>
/// 스크립트 효과(13.2, 13.5): 사용자가 JS로 쓴 효과. 원문은 프로젝트 스크립트 표(<see cref="SpriteProject.Scripts"/>)에 해시로 한 번만 두고,
/// 효과는 원문 해시 <see cref="ScriptHash"/>와 설정값 <see cref="Settings"/>만 가진다. 원문을 고치면 해시가 바뀌므로 다른 효과 값이 된다.
/// <para>
/// 움직이는 효과처럼 프레임 범위·빠르기 곡선·반복을 가진다(선언이 <c>animated: false</c>면 진행을 쓰지 않을 뿐). 원문을 모르거나 실행을 허락하지 않았거나
/// 실행이 한도에 걸리면 그 효과만 그리지 않고 데이터는 그대로 둔다(<see cref="ScriptEffectRuntime"/>).
/// </para>
/// </summary>
public sealed record ScriptEffect : AnimatedEffect
{
    /// <summary>원문 해시(<see cref="ScriptEffectProgram.ContentHash"/>).</summary>
    public string ScriptHash { get; init; } = "";

    /// <summary>스크립트 설정값(값 비교, 저장 JSON 모양). 원문을 알면 그 schema에 맞춘 값이다.</summary>
    public ScriptSettingValues Settings { get; init; } = ScriptSettingValues.Empty;

    public override string DisplayName => ScriptLibrary.NameOf(ScriptHash) ?? Gpixel.Core.Localization.DomainStrings.Effect_Script;
}
