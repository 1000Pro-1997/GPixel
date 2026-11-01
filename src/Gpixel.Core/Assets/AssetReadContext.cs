namespace Gpixel.Core.Assets;

/// <summary>
/// 데이터 패키지 검사 한도(계획 10.1). 기본값은 실제 에셋을 재어 정했다: 효과 32개짜리 레시피 JSON ≈ 11KB(색 설정마다 8정지점 그라데이션을 넣으면 ≈ 52KB),
/// 색·알파 정지점 64개씩인 그라데이션 ≈ 9.5KB, 128px 미리보기 PNG(<see cref="AssetPreview.Png"/>) ≈ 0.15~15KB(효과 하나 ≈ 0.3~0.7KB, 효과 32개 ≈ 7.5~14KB).
/// 한도는 그 수십 배로 두어 정상 패키지는 걸리지 않고 압축 폭탄과 과도한 항목은 쓰기 전에 막는다.
/// </summary>
public sealed record AssetPackageLimits
{
    /// <summary>패키지 파일(압축된 ZIP) 크기.</summary>
    public long MaxPackageBytes { get; init; } = 8L * 1024 * 1024;
    /// <summary>압축 해제한 모든 파일 크기의 합.</summary>
    public long MaxExpandedBytes { get; init; } = 32L * 1024 * 1024;
    public int MaxFiles { get; init; } = 256;
    /// <summary>파일 하나(미리보기·JSON 밖)의 압축 해제 크기.</summary>
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024;
    /// <summary>JSON 파일 하나(manifest, 에셋).</summary>
    public long MaxJsonBytes { get; init; } = 1024 * 1024;
    /// <summary>
    /// 스크립트 원문 하나(UTF-8 바이트): 패키지의 <c>assets/scripts/*.js</c>와 레시피 <c>scripts</c>의 원문. 빛 지나가기 JS ≈ 2.9KB의 20배 남짓.
    /// 레시피 JSON 안 원문 글은 <see cref="MaxStringLength"/> 대신 이 한도를 쓴다.
    /// </summary>
    public int MaxScriptBytes { get; init; } = 64 * 1024;
    public long MaxPreviewBytes { get; init; } = 1024 * 1024;
    /// <summary>미리보기 PNG의 폭·높이.</summary>
    public int MaxPreviewSize { get; init; } = 512;
    public int MaxAssets { get; init; } = 128;
    public int MaxEffectsPerRecipe { get; init; } = 32;
    /// <summary>레시피 하나의 매개변수 수.</summary>
    public int MaxRecipeParameters { get; init; } = 32;
    /// <summary>매개변수 하나에 연결하는 효과 설정 수.</summary>
    public int MaxParameterBindings { get; init; } = 64;
    /// <summary>그라데이션 색·알파 정지점 각각(효과 색 슬롯의 그라데이션도).</summary>
    public int MaxGradientStops { get; init; } = 64;
    /// <summary>색 순환·팔레트 교체 같은 효과 설정 목록의 칸 수.</summary>
    public int MaxListItems { get; init; } = 256;
    public int MaxJsonDepth { get; init; } = 16;
    public int MaxStringLength { get; init; } = 4096;
    /// <summary>패키지 안 경로 길이(글자).</summary>
    public int MaxPathLength { get; init; } = 160;

    public static AssetPackageLimits Default { get; } = new();
}

/// <summary>
/// 에셋 JSON 하나를 읽는 동안의 상태: 패키지 안 경로, 한도, 엄격 모드, 문제 목록.
/// 엄격 모드(설치할 패키지)는 모르는 키, 등록되지 않은 효과, 범위 밖 값을 문제로 남긴다.
/// 느슨한 모드(개인 에셋 파일)는 앱이 쓴 데이터를 읽으며 모르는 효과를 <see cref="Model.MissingEffect"/>로 보존한다.
/// </summary>
public sealed class AssetReadContext(string path, bool strict, AssetPackageLimits? limits = null, List<AssetIssue>? issues = null)
{
    public string Path { get; } = path;
    public bool Strict { get; } = strict;
    public AssetPackageLimits Limits { get; } = limits ?? AssetPackageLimits.Default;
    public List<AssetIssue> Issues { get; } = issues ?? [];

    /// <summary>
    /// manifest(개인 에셋이면 저장 줄)의 에셋 ID. 파일 안에 ID가 없는 종류(<c>effect-script</c>의 JS)는 읽은 값에 이 ID를 붙인다.
    /// </summary>
    public string AssetId { get; init; } = "";

    public void Add(AssetIssueCode code, string? detail = null) => Issues.Add(new AssetIssue(code, Path, detail));

    /// <summary>필드 <paramref name="field"/>가 없거나 틀림.</summary>
    public void Bad(string field) => Add(AssetIssueCode.InvalidField, field);
}
