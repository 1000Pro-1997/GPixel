using Gpixel.Core.Effects;
using Gpixel.Core.IO;

namespace Gpixel.Core.Assets;

/// <summary>에셋이 온 곳.</summary>
public enum AssetOrigin
{
    /// <summary>사용자가 이 컴퓨터에서 만든 개인 에셋(편집 가능).</summary>
    Personal,
    /// <summary>설치한 패키지의 에셋(읽기 전용 원본 — 고치면 개인 에셋으로 복사).</summary>
    Package,
}

/// <summary>
/// 카탈로그의 에셋 하나. <see cref="Data"/>는 값 자체이고, 프로젝트에 넣을 때는 값을 복사한다(계획 2.4).
/// <see cref="Enabled"/>는 새 선택 목록에서 보이는지, <see cref="IsBuiltIn"/>은 삭제 불가 기본 그라데이션인지, <see cref="Favorite"/>는 컴퓨터별 즐겨찾기.
/// </summary>
public sealed record AssetEntry(string Id, string Kind, AssetOrigin Origin, IAssetData Data)
{
    public string Name => Data.Name;
    public string? PackageId { get; init; }
    public string? PackageVersion { get; init; }
    public bool IsBuiltIn { get; init; }
    public string? PackageName { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Favorite { get; init; }
    /// <summary>미리보기 PNG의 디스크 경로(패키지가 미리보기를 넣었을 때만). 보이는 항목만 늦게 decode한다.</summary>
    public string? PreviewPath { get; init; }

    public EffectRecipe? Recipe => Data as EffectRecipe;

    public ScriptAsset? Script => Data as ScriptAsset;

    /// <summary>효과로 넣을 레시피: 효과 레시피는 그대로, 스크립트 에셋은 기본 설정 스크립트 효과 하나(<see cref="ScriptAsset.ToRecipe"/>). 그 밖은 null.</summary>
    public EffectRecipe? EffectTemplate => Recipe ?? Script?.ToRecipe();

    /// <summary>이 에셋이 담은 스크립트 원문 해시(레시피의 원문, 스크립트 에셋 자신). 실행 허락 표시에 쓴다.</summary>
    public IReadOnlyCollection<string> ScriptHashes
        => Recipe is { } recipe ? recipe.Scripts.Keys.ToArray() : Script is { } script ? [script.Hash] : Array.Empty<string>();

    /// <summary>그라데이션 값(<see cref="GradientPreset.Favorite"/>는 이 항목의 즐겨찾기).</summary>
    public GradientPreset? Gradient => Data is GradientAsset g ? g.Gradient with { Favorite = Favorite } : null;
}

/// <summary>설치 상태(installed.json의 패키지 한 줄, 계획 7.2). manifest·에셋 데이터는 패키지 폴더에서 읽는다.</summary>
public sealed record InstalledPackageState
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Enabled { get; init; } = true;
    /// <summary>패키지 안에서 개별로 숨긴 에셋 ID. 설치/업데이트 상태에 함께 보존한다.</summary>
    public IReadOnlyList<string> DisabledAssetIds { get; init; } = [];
    /// <summary>설치 출처: <c>file</c>(로컬 파일 가져오기) — 4단계에서 원격 카탈로그 주소가 더해진다.</summary>
    public string Source { get; init; } = "file";
    public DateTime InstalledUtc { get; init; }
    public bool AutoUpdate { get; init; }
    /// <summary>되돌리기용으로 보관한 이전 버전(최근 것 먼저).</summary>
    public IReadOnlyList<string> PreviousVersions { get; init; } = [];
}

/// <summary>설치된 패키지 하나: 상태, 활성 버전 폴더, 읽은 manifest와 에셋. 읽지 못하면 <see cref="Issues"/>가 있고 에셋은 비어 있다.</summary>
public sealed record InstalledPackage(InstalledPackageState State, string Folder, AssetManifest? Manifest,
    IReadOnlyList<AssetEntry> Entries, IReadOnlyList<AssetIssue> Issues)
{
    public string Id => State.Id;
    public string Version => State.Version;
    public bool Enabled => State.Enabled;
    public string Name => Manifest?.Name ?? State.Id;
    public bool IsBroken => Manifest is null || Issues.Count > 0;
    /// <summary>공식 카탈로그에서 받은 Gpixel 기본 패키지. 삭제 대신 끈다.</summary>
    public bool IsOfficial => State.Source == "remote" && Manifest?.Author.Id == "gpixel"
                              && Id.StartsWith("gpixel.", StringComparison.Ordinal);
}

/// <summary>되돌릴 수 있게 보관한 삭제 패키지(<c>Deleted/</c>).</summary>
public sealed record DeletedPackage(string Folder, string Id, string Version, string Name, DateTime DeletedUtc);

/// <summary>되돌릴 수 있게 보관한 삭제 개인 에셋.</summary>
public sealed record DeletedPersonalAsset(string Id, string Name, string Kind, DateTime DeletedUtc)
{
    public string Folder { get; init; } = "";
}

/// <summary>
/// 카탈로그(계획 4.5): 기본 효과, 개인 에셋, 설치한 패키지를 합친 읽기 모델. 저장소가 바뀔 때마다 새로 만들어지는 변하지 않는 값이다.
/// 고르는 목록(효과 창, 그라데이션 메뉴, AI 도구)은 <see cref="Available"/> — 켜진 개인 에셋과 패키지만 — 을 쓴다.
/// </summary>
public sealed class AssetCatalog
{
    private readonly HashSet<string> _officialGradientNames;

    public AssetCatalog(IReadOnlyList<AssetEntry> personal, IReadOnlyList<InstalledPackage> packages,
        IReadOnlyCollection<string>? disabledBuiltInEffects = null)
    {
        Personal = personal;
        Packages = packages;
        _officialGradientNames = packages.Where(package => package.IsOfficial)
            .SelectMany(package => package.Entries)
            .Where(entry => entry.Kind == AssetKinds.Gradient)
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        BuiltInEffects = EffectRegistry.BuiltIn.Where(effect => disabledBuiltInEffects?.Contains(effect.TypeId) != true).ToArray();
    }

    public static AssetCatalog Empty { get; } = new([], []);

    /// <summary>새로 고를 수 있는 기본 효과. 비활성화는 저장된 효과의 정의·렌더링에는 영향을 주지 않는다.</summary>
    public IReadOnlyList<EffectDefinition> BuiltInEffects { get; }

    /// <summary>개인 에셋(저장 순서).</summary>
    public IReadOnlyList<AssetEntry> Personal { get; }

    /// <summary>관리자에 표시할 개인 에셋. 설치된 공식 그라데이션과 겹치는 기본 항목만 숨긴다(저장은 유지).</summary>
    public IReadOnlyList<AssetEntry> DisplayPersonal => Personal.Where(entry => !IsShadowedDefault(entry)).ToArray();

    /// <summary>설치한 패키지(꺼진 것과 읽지 못한 것 포함).</summary>
    public IReadOnlyList<InstalledPackage> Packages { get; }

    /// <summary>개인 에셋과 모든 패키지 에셋.</summary>
    public IEnumerable<AssetEntry> All => Personal.Concat(Packages.SelectMany(package => package.Entries));

    /// <summary>새로 고를 수 있는 <paramref name="kind"/> 에셋: 개인 에셋 다음에 켜진 패키지 순서.</summary>
    public IReadOnlyList<AssetEntry> Available(string kind)
        => All.Where(entry => entry.Kind == kind && entry.Enabled && !IsShadowedDefault(entry)).ToArray();

    private bool IsShadowedDefault(AssetEntry entry)
        => entry.Origin == AssetOrigin.Personal && entry.IsBuiltIn && entry.Kind == AssetKinds.Gradient
           && _officialGradientNames.Contains(entry.Name);

    public IReadOnlyList<AssetEntry> Recipes => Available(AssetKinds.EffectRecipe);

    /// <summary>효과 창 커스텀 탭에 놓을 에셋: 효과 레시피와 스크립트 효과(개인 다음 켜진 패키지, 저장·설치 순서).</summary>
    public IReadOnlyList<AssetEntry> EffectTemplates
        => All.Where(entry => entry.Enabled && entry.Kind is AssetKinds.EffectRecipe or AssetKinds.EffectScript).ToArray();

    public IReadOnlyList<AssetEntry> Gradients => Available(AssetKinds.Gradient);

    public AssetEntry? Find(string id) => All.FirstOrDefault(entry => entry.Id == id);

    public InstalledPackage? FindPackage(string id) => Packages.FirstOrDefault(package => package.Id == id);
}
