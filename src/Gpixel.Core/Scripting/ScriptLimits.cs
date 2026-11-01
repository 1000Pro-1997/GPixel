namespace Gpixel.Core.Scripting;

/// <summary>
/// 스크립트 효과 호출 한 번(맨 위 선언 실행, <c>padding</c>, <c>render</c> 각각)에 거는 한도.
/// 넘으면 <see cref="ScriptEffectException"/>으로 그 호출만 멈추고 프로세스는 그대로 산다.
/// 값이 같으면 작업 스레드의 Jint 실행기를 같이 쓴다(값 비교 record).
/// </summary>
public sealed record ScriptLimits
{
    /// <summary>앱에서 쓰는 한도. 값을 고른 근거는 각 속성 설명에 있다.</summary>
    public static ScriptLimits Default { get; } = new();

    /// <summary>
    /// 호출 한 번의 시간 한도(Jint <c>TimeoutInterval</c>, 정규식 한 번의 한도도 같음). 대표 효과의 목표가 256²에서 50ms이므로
    /// 40배 여유를 둔다. 기계마다 속도가 달라 멈추는 지점이 달라지므로 실제로는 <see cref="MaxStatements"/>가 먼저 걸리게 잡는다.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 호출 한 번에 실행할 수 있는 문장 수(Jint <c>MaxStatements</c>). 기계와 상관없이 같은 지점에서 멈추는 결정론적 한도다.
    /// 빛 지나가기 JS가 256²에서 약 53만 문장(약 100배 여유), 빈 반복이 이 한도에 닿는 데 약 0.8초라 시간 한도보다 먼저 걸린다.
    /// </summary>
    public int MaxStatements { get; init; } = 50_000_000;

    /// <summary>
    /// 호출 한 번에 새로 할당할 수 있는 바이트(Jint <c>LimitMemory</c>). Jint는 살아 있는 메모리가 아니라 그 스레드가 호출 동안
    /// 할당한 양을 세므로 버린 객체도 포함된다. 문자열을 두 배씩 늘리면 한도를 넘긴 뒤 한 번 더 할당하고 멈추므로(부록 B: 256MB 한도에 402MB)
    /// 실제 예산 512MB의 절반으로 잡는다. 픽셀마다 계산하는 스크립트는 256²에서 약 20~35MB를 할당한다(빛 지나가기 24MB).
    /// </summary>
    public long MemoryBytes { get; init; } = 256L << 20;

    /// <summary>JS 함수 호출 깊이(Jint <c>LimitRecursion</c>). .NET 스택이 넘치기 전에 멈추도록 낮게 둔다.</summary>
    public int MaxRecursion { get; init; } = 256;

    /// <summary>
    /// 배열 한 개의 최대 칸 수(Jint <c>MaxArraySize</c>). <c>img.data</c>도 배열이라 그림 크기 상한이 된다: 2048×2048 RGBA.
    /// </summary>
    public uint MaxArrayLength { get; init; } = 1u << 24;
}
