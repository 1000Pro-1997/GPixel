using System.Globalization;
using Gpixel.Core.Localization;

namespace Gpixel.Core.Assets;

/// <summary>패키지·에셋 검사에서 찾은 문제의 종류(언어와 무관한 구조 — 문구는 <see cref="AssetIssue.Message"/>).</summary>
public enum AssetIssueCode
{
    /// <summary>패키지 파일이 압축 크기 한도를 넘음.</summary>
    PackageTooLarge,
    /// <summary>ZIP으로 열 수 없음.</summary>
    NotZip,
    TooManyFiles,
    /// <summary>파일 하나의 압축 해제 크기가 한도를 넘음.</summary>
    FileTooLarge,
    /// <summary>압축 해제 전체 크기가 한도를 넘음(압축 폭탄).</summary>
    ExpandedTooLarge,
    /// <summary>빈 경로, 역슬래시, 제어 문자, 너무 긴 경로처럼 쓸 수 없는 경로.</summary>
    InvalidPath,
    AbsolutePath,
    /// <summary><c>..</c>이나 <c>.</c> 조각으로 패키지 밖이나 다른 곳을 가리킴.</summary>
    PathEscape,
    DuplicatePath,
    /// <summary>대소문자만 다른 두 경로.</summary>
    CaseConflict,
    /// <summary>symlink 같은 일반 파일이 아닌 항목.</summary>
    SpecialFile,
    /// <summary>허용하지 않는 확장자나 자리.</summary>
    DisallowedFile,
    /// <summary>manifest의 files에 없는 파일.</summary>
    UnlistedFile,
    /// <summary>manifest가 적었지만 패키지에 없는 파일.</summary>
    MissingFile,
    HashMismatch,
    MissingManifest,
    /// <summary>JSON으로 읽을 수 없거나 모양이 틀림.</summary>
    InvalidJson,
    JsonTooDeep,
    StringTooLong,
    /// <summary>지원하는 것보다 새 schema 버전.</summary>
    FutureSchema,
    /// <summary>필수 값이 없거나 형식·범위가 틀림(<see cref="AssetIssue.Detail"/> = 필드 이름).</summary>
    InvalidField,
    /// <summary>패키지가 요구하는 앱 버전보다 지금 앱이 낮음(<see cref="AssetIssue.Detail"/> = 필요한 버전).</summary>
    AppTooOld,
    UnknownKind,
    DuplicateAssetId,
    /// <summary>등록되지 않은 효과 종류 ID(첫 버전 레시피는 앱에 들어 있는 기본 효과만 씀).</summary>
    UnknownEffect,
    /// <summary>효과 설정이 schema에 맞지 않음(모르는 키, 틀린 모양, 범위 밖).</summary>
    InvalidEffectSetting,
    /// <summary>에셋, 효과, 정지점 같은 항목 수가 한도를 넘음.</summary>
    TooManyItems,
    /// <summary>미리보기 PNG를 읽을 수 없음.</summary>
    InvalidPreview,
    /// <summary>미리보기 폭·높이가 한도를 넘음.</summary>
    PreviewTooLarge,
    /// <summary>같은 패키지 ID·버전이 이미 다른 내용으로 설치됨.</summary>
    VersionConflict,
    /// <summary>레시피 매개변수가 레시피에 없는 효과 번호를 가리킴(<see cref="AssetIssue.Detail"/> = 연결 필드 → 효과 번호).</summary>
    RecipeBindingMissingEffect,
    /// <summary>레시피 매개변수가 그 효과 schema에 없는 설정 키를 가리킴.</summary>
    RecipeBindingUnknownKey,
    /// <summary>매개변수 종류와 연결된 설정의 종류가 다르거나 내보낼 수 없는 설정(색 목록 등).</summary>
    RecipeBindingKindMismatch,
    /// <summary>같은 효과 설정이 두 번 연결됨(한 매개변수 안이나 두 매개변수 사이).</summary>
    RecipeBindingDuplicate,
    /// <summary>매개변수 범위·기본값·고를 수 있는 값이 연결된 설정의 schema 밖.</summary>
    RecipeParameterOutOfRange,
    /// <summary>스크립트 원문을 해석할 수 없음: 문법·선언 오류, 맨 위 실행의 한도 초과(<see cref="AssetIssue.Detail"/> = 자리와 줄·칸 붙은 오류).</summary>
    InvalidScript,
    /// <summary>레시피 <c>scripts</c>의 원문 해시가 적힌 해시와 다름(<see cref="AssetIssue.Detail"/> = 필드).</summary>
    ScriptHashMismatch,
    /// <summary>스크립트 효과가 가리키는 원문이 레시피에 없음(<see cref="AssetIssue.Detail"/> = 효과 필드와 해시 앞부분).</summary>
    MissingScript,
}

/// <summary>
/// 검사 문제 하나: 종류, 패키지 안 경로(없으면 null), 자세한 값(필드 이름, 한도, 효과 ID 등 — 번역하지 않는 기술 값).
/// 문구는 <c>DomainStrings</c>의 <c>Asset_Issue_{종류}</c>(한국어 기본 + 영어)에서 만든다.
/// </summary>
public sealed record AssetIssue(AssetIssueCode Code, string? Path = null, string? Detail = null)
{
    /// <summary>지금 UI 언어의 문구(경로가 있으면 앞에 붙임).</summary>
    public string Message
    {
        get
        {
            string text = DomainStrings.GetResourceString("Asset_Issue_" + Code) ?? Code.ToString();
            text = text.Replace("{detail}", Detail ?? "", StringComparison.Ordinal);
            return Path is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, "{0}: {1}", Path, text) : text;
        }
    }

    public override string ToString() => Message;
}

/// <summary>패키지 검사·설치 실패. <see cref="Issues"/>에 찾은 문제가 모두 있다(첫 문제가 <see cref="Exception.Message"/>).</summary>
public sealed class AssetPackageException(IReadOnlyList<AssetIssue> issues)
    : Exception(issues.Count > 0 ? issues[0].Message : nameof(AssetPackageException))
{
    public IReadOnlyList<AssetIssue> Issues { get; } = issues;

    public AssetPackageException(AssetIssue issue) : this([issue]) { }
}
