namespace Gpixel.Core.Scripting;

/// <summary>스크립트 효과 오류의 종류.</summary>
public enum ScriptErrorKind
{
    /// <summary>JS 문법 오류(해석 단계).</summary>
    Syntax,
    /// <summary><c>effect({...})</c> 선언 형식이 틀림(이름, 영역, 설정 schema, 여백, render 함수).</summary>
    Declaration,
    /// <summary><see cref="ScriptLimits.Timeout"/>을 넘음.</summary>
    Timeout,
    /// <summary><see cref="ScriptLimits.MemoryBytes"/>나 <see cref="ScriptLimits.MaxArrayLength"/>를 넘음.</summary>
    Memory,
    /// <summary><see cref="ScriptLimits.MaxStatements"/>를 넘음.</summary>
    Statements,
    /// <summary><see cref="ScriptLimits.MaxRecursion"/>을 넘음.</summary>
    Recursion,
    /// <summary>스크립트가 던진 오류나 잘못된 결과(<c>img.data</c>, <c>padding</c> 값).</summary>
    Runtime,
}

/// <summary>
/// 스크립트 효과 오류 하나. <see cref="Message"/>는 현재 언어로 만든 문구다.
/// <see cref="Line"/>과 <see cref="Column"/>은 1부터 세고, 위치를 모르면 0이다.
/// </summary>
public sealed record ScriptError(ScriptErrorKind Kind, string Message, int Line, int Column);

/// <summary>
/// 스크립트 실행이 한도나 오류로 멈춤. 그 효과의 그 호출만 실패하고 그림은 바뀌지 않는다.
/// 호출한 쪽의 취소는 다른 효과처럼 <see cref="OperationCanceledException"/>으로 나간다.
/// </summary>
public sealed class ScriptEffectException(ScriptError error) : Exception(error.Message)
{
    public ScriptError Error { get; } = error;
    public ScriptErrorKind Kind => Error.Kind;
    public int Line => Error.Line;
    public int Column => Error.Column;
}
