namespace Gpixel.Core.Model;

/// <summary>
/// 이 앱이 모르는 효과(등록되지 않은 종류 ID, 더 새 설정 버전, 읽을 수 없는 설정): 원본 JSON을 그대로 들고 있다가 저장할 때 다시 쓴다.
/// 켜짐·프레임 범위·클리핑은 다른 효과처럼 목록 편집(프레임 넣기·지우기, 클리핑 풀기)을 따르고, 저장할 때 원본 JSON의 그 키만 바꾼다.
/// 그리지 않고(여백도 없음) 설정을 고칠 수 없다. 그 효과를 아는 앱에서 열면 정상 효과로 다시 읽힌다.
/// </summary>
public sealed record MissingEffect : LayerEffect
{
    /// <summary>원본의 효과 종류 ID(없으면 빈 글자).</summary>
    public string TypeId { get; init; } = "";

    /// <summary>원본 효과 JSON(한 줄).</summary>
    public string Json { get; init; } = "{}";

    public override string DisplayName => Gpixel.Core.Localization.DomainStrings.Effect_Missing;
}
