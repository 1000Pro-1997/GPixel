using Gpixel.App.Localization;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;

namespace Gpixel.App;

/// <summary>
/// 그라데이션 프리셋과 에셋 저장소 사이(편집 UI: <see cref="PresetsWindow"/>). 고르는 목록은 개인 그라데이션 + 켜진 패키지의 그라데이션이고,
/// 저장·이름 바꾸기·삭제는 개인 에셋에 한다(설치 패키지의 그라데이션은 고치면 개인 에셋으로 복사). 실패하면 알리고 false/null을 돌려준다.
/// </summary>
public static class GradientPresetUi
{
    /// <summary>새로 고를 수 있는 그라데이션(개인 다음에 켜진 패키지).</summary>
    public static IReadOnlyList<AssetEntry> Available => AppAssets.Catalog.Gradients;

    /// <summary>개인 그라데이션(이름 겹침 검사용).</summary>
    public static IReadOnlyList<AssetEntry> Personal => AppAssets.Catalog.Personal.Where(entry => entry.Kind == AssetKinds.Gradient).ToArray();

    /// <summary>개인 그라데이션을 저장한다(<paramref name="id"/>가 null이면 새로). 즐겨찾기는 <see cref="GradientPreset.Favorite"/>를 따른다.</summary>
    public static AssetEntry? Save(Window owner, string? id, GradientPreset preset)
    {
        AssetEntry? saved = null;
        return AppAssets.Try(owner, () => saved = AppAssets.Store.SavePersonalGradient(id, preset), UiStrings.GradientPreset_SaveError) ? saved : null;
    }

    public static bool Remove(Window owner, string id)
        => AppAssets.Try(owner, () => AppAssets.Store.RemovePersonal(id), UiStrings.GradientPreset_SaveError);

    public static bool SetFavorite(Window owner, string id, bool favorite)
        => AppAssets.Try(owner, () => AppAssets.Store.SetFavorite(id, favorite), UiStrings.GradientPreset_SaveError);

    /// <summary>기본 그라데이션을 처음 값으로 되돌리고 지운 것은 다시 넣는다(직접 만든 것과 즐겨찾기는 그대로).</summary>
    public static bool RestoreBuiltIns(Window owner)
        => AppAssets.Try(owner, AppAssets.Store.RestoreBuiltInGradients, UiStrings.GradientPreset_SaveError);
}
