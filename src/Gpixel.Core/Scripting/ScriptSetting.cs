namespace Gpixel.Core.Scripting;

/// <summary>스크립트 설정 값의 종류. 선언의 <c>type</c> 문자열 <c>int</c>, <c>number</c>, <c>bool</c>, <c>color</c>, <c>choice</c>.</summary>
public enum ScriptSettingType
{
    /// <summary>정수. C# 값은 <see cref="int"/>, JS 값은 number.</summary>
    Int,
    /// <summary>실수. C# 값은 <see cref="double"/>, JS 값은 number.</summary>
    Number,
    /// <summary>켜고 끔. C# 값은 <see cref="bool"/>, JS 값은 boolean.</summary>
    Bool,
    /// <summary>색. C# 값은 <see cref="Model.Color32"/>, JS 값은 고정된 <c>{ r, g, b, a }</c>(0~255).</summary>
    Color,
    /// <summary>목록에서 고르기. C# 값과 JS 값 모두 고른 항목의 <see cref="ScriptChoice.Value"/> 문자열.</summary>
    Choice,
}

/// <summary>고르기 설정의 항목 하나. <see cref="Value"/>는 저장 ID, <see cref="Label"/>은 화면 이름.</summary>
public sealed record ScriptChoice(string Value, string Label);

/// <summary>
/// 스크립트가 선언한 설정 하나(5.4 명시적 설정 schema와 같은 정보). 효과 창의 범용 편집기, 저장과 검증이 이것을 쓴다.
/// <para>
/// <see cref="Key"/>는 영구 저장 키(영문자나 _로 시작). <see cref="Min"/>, <see cref="Max"/>, <see cref="Step"/>은 <see cref="ScriptSettingType.Int"/>,
/// <see cref="ScriptSettingType.Number"/>에만 있고 없으면 null이다. <see cref="Default"/>는 <see cref="Type"/>에 맞는 C# 값이다
/// (<see cref="ScriptSettingType"/> 설명 참고). <see cref="Choices"/>는 <see cref="ScriptSettingType.Choice"/>에만 있고 나머지는 빈 목록이다.
/// </para>
/// </summary>
public sealed record ScriptSetting(
    string Key,
    string Label,
    ScriptSettingType Type,
    double? Min,
    double? Max,
    double? Step,
    object Default,
    IReadOnlyList<ScriptChoice> Choices)
{
    // 목록은 참조가 아니라 항목으로 비교한다(schema 비교·변경 감지)
    public bool Equals(ScriptSetting? other)
        => other is not null && Key == other.Key && Label == other.Label && Type == other.Type && Min == other.Min && Max == other.Max
            && Step == other.Step && Default.Equals(other.Default) && Choices.SequenceEqual(other.Choices);

    public override int GetHashCode() => HashCode.Combine(Key, Label, Type, Min, Max, Step, Default, Choices.Count);
}
