using System.Drawing;
using System.Text;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.Core.Slicing;

namespace Gpixel.Core.IO;

/// <summary>시트의 칸 하나.</summary>
/// <param name="FrameIndex">프로젝트 프레임 번호.</param>
/// <param name="Name">칸(스프라이트) 이름 — Unity <c>.meta</c>에 그대로 쓰인다.</param>
/// <param name="Rect">시트 안 영역(왼쪽 위 원점).</param>
/// <param name="Pivot">칸 기준 피벗(아래 왼쪽 원점, 잘라낸 경우 0~1 밖일 수 있음).</param>
/// <param name="Tag">이 칸이 속한(이름을 정한) 태그 이름. 없으면 null.</param>
/// <param name="Border">9-slice 테두리(부위 아틀라스).</param>
/// <param name="Part">부위 아틀라스 칸이면 그 부위 레이어, 프레임 시트면 null.</param>
/// <param name="Skin">부위 아틀라스 칸이 그룹의 첫 스킨이 아닌 스킨이면 그 스킨(첫 스킨·프레임 시트면 null).</param>
/// <param name="Variant">프레임 시트 칸이면 합성한 스킨 항목.</param>
public sealed record SheetEntry(int FrameIndex, string Name, Rectangle Rect, Pivot Pivot, string? Tag, SpriteBorder Border = default, Layer? Part = null,
    Skin? Skin = null, FrameVariant? Variant = null);

/// <summary>만들어진 시트 이미지와 칸 목록.</summary>
public sealed record SheetResult(PixelImage Image, IReadOnlyList<SheetEntry> Entries, Size CellSize);

/// <summary>결과 파일 하나의 시트: 파일 이름(확장자 없이)과 시트. 태그·부위·스킨마다 나눴으면 그 태그·부위·스킨(프레임 시트는 스킨 항목).</summary>
public sealed record SheetFile(string Name, SheetResult Sheet, AnimationTag? Tag = null, Layer? Part = null, string? SkinName = null, FrameVariant? Variant = null);

/// <summary>
/// 프레임 시트의 스킨 항목 하나: 지금 착장(<see cref="IsCurrent"/>), 그룹 하나만 그 스킨으로 바꾼 착장(나머지 그룹은 지금 착장), 또는 착장 프리셋.
/// <paramref name="Suffix"/>는 이름 뒤 <c>@…</c>(null = 원래 이름 — 지금 착장·첫 번째로 고른 첫 스킨 항목).
/// </summary>
public sealed record FrameVariant(string? Suffix, Layer? Group, Skin? Skin, Outfit? Outfit)
{
    public bool IsCurrent => Group is null && Outfit is null;

    /// <summary>화면 표시용 이름.</summary>
    public string Label => Outfit is { } o ? o.Name : Group is { } g && Skin is { } s ? $"{g.Name} · {s.Name}" : Gpixel.Core.Localization.DomainStrings.Export_CurrentOutfit;
}

/// <summary>결과 파일 하나(이미지 또는 <c>.spriteLib</c>)를 내보낸 결과. 스프라이트 수는 Sprite Library면 항목(라벨) 수.</summary>
public sealed record ExportResult(string ImagePath, bool MetaWritten, int SpriteCount);

/// <summary>
/// 프로젝트 내보내기 결과: 프레임 시트 파일들, 부위 아틀라스 파일들, Sprite Library(<c>.spriteLib</c>) 파일들(꺼져 있거나 쓸 것이 없으면 빈 목록),
/// 그리고 정리할 옛 결과(<see cref="SpriteProject.ExportedFiles"/>에 있었지만 이번에 만들지 않은, 아직 있는 파일과 딸린 <c>.meta</c>).
/// 옛 결과는 지우지 않고 돌려주기만 한다(앱이 앱 휴지통으로 옮김).
/// </summary>
public sealed record ProjectExportResult(IReadOnlyList<ExportResult> Frames, IReadOnlyList<ExportResult> Parts,
    IReadOnlyList<ExportResult> Libraries, IReadOnlyList<string> Stale)
{
    /// <summary>프레임 시트 → 부위 아틀라스 → Sprite Library 순서의 모든 결과 파일.</summary>
    public IEnumerable<ExportResult> All => Frames.Concat(Parts).Concat(Libraries);

    public bool IsEmpty => Frames.Count == 0 && Parts.Count == 0 && Libraries.Count == 0;
}

/// <summary>
/// 내보내기 계획: 만들 파일 경로(프레임 시트·부위 아틀라스 이미지, Sprite Library), 새 기록(결과 파일 + 쓰거나 이미 있는 <c>.meta</c>), 정리할 옛 결과.
/// </summary>
public sealed record ExportPlan(string ProjectPath, IReadOnlyList<string> FramePaths, IReadOnlyList<string> PartPaths,
    IReadOnlyList<string> LibraryPaths, IReadOnlyList<string> Record, IReadOnlyList<string> Stale);

/// <summary>
/// 프로젝트 → 스프라이트 시트 이미지 + Unity 메타 내보내기. 편집 정보(칸·태그 애니메이션)는 <c>.gpixel</c>에만 있고 이미지 옆에 따로 쓰지 않는다.
/// 프레임 시트와 부위 아틀라스는 같은 시트 설정(<see cref="SheetOptions"/>, <see cref="ExportSettings.Frames"/>·<see cref="ExportSettings.Parts"/>)을 쓴다.
/// <list type="bullet">
/// <item>프레임 시트(<c>{시트}</c>): 프레임마다 합성한 칸. 스킨이 "고른 스킨"이면 스킨 항목(<see cref="FrameVariants"/>)마다 한 벌씩 —
/// 한 이미지에 모두(칸 이름 <c>{이름}@{항목}</c>) 또는 스킨마다 따로 파일(<c>{시트}@{항목}</c>, 칸 이름은 같음). Single 모드는 항목마다 따로 파일.</item>
/// <item>칸 이름: 프레임 시트 = 프레임 이름 재정의 → (태그 기준이면) <c>{태그}_{태그 안 번호}</c> → <c>{시트}_{프레임}</c>;
/// 부위 아틀라스 = 프레임 번호(<c>{부위}</c>, <c>{부위}_{프레임}</c>) 또는 태그 기준(<c>{부위}_{태그}_{태그 안 번호}</c>), 첫 스킨이 아닌 스킨은 <c>@{스킨}</c>.
/// 정리(<see cref="CleanSpriteName"/>) 후 파일 안에서 겹치면 <c>_1</c>, <c>_2</c> …. 배치·정렬·나누기를 바꿔도 이름은 그대로.</item>
/// <item>배치: 격자(정렬 기준·줄바꿈 단위 <see cref="SheetOptions.Order"/> + 칸 수) 또는 자동 배치(<see cref="AtlasPacker"/>, 넣는 순서 = 정렬 기준).</item>
/// <item>따로 파일: 태그마다 <c>_{태그}</c>(태그 없는 프레임 <c>_untagged</c>), 부위마다 <c>_{부위}</c>(부위 아틀라스), 스킨마다 <c>@{스킨}</c> — 조합 가능.</item>
/// <item>부위 아틀라스(<c>{시트}_parts</c> 또는 <see cref="ExportSettings.PartsFileName"/>): 부위 × 내용 있는 프레임 × 내보낼 스킨마다 스프라이트 하나(항상 Multiple).
/// 첫 스킨이 아닌 스킨은 직접 그린 셀만(물려받는 셀은 첫 스킨 스프라이트를 같이 씀, <see cref="SheetOptions.BakeInheritedSkins"/>면 구워 넣음).</item>
/// <item>Sprite Library(<see cref="ExportSettings.WriteSpriteLibrary"/>): 그룹 × 내보낼 스킨마다 <c>{부위 아틀라스}_{그룹}@{스킨}.spriteLib</c>(+ <c>.meta</c>) —
/// <see cref="SpriteLibraryAssetWriter"/> 참고.</item>
/// </list>
/// </summary>
public static class ProjectExport
{
    /// <summary>나눈 파일 중 태그 없는 프레임 파일 이름 끝.</summary>
    public const string UntaggedSuffix = "untagged";

    /// <summary>스킨 이름 앞 구분자(<c>Head@knight</c>, <c>hero@knight.png</c>).</summary>
    public const string SkinSeparator = "@";

    /// <summary>Sprite Library Asset 확장자.</summary>
    public const string SpriteLibraryExtension = ".spriteLib";

    /// <summary>프레임 시트 이미지 경로: (지정 폴더 또는 프로젝트 폴더)/(지정 이름 또는 프로젝트 이름).(형식 확장자). 나누면 이 이름이 앞부분.</summary>
    public static string GetExportImagePath(SpriteProject project, string? projectPath = null)
        => GetExportImagePath(project.Export, RequirePath(project, projectPath));

    /// <inheritdoc cref="GetExportImagePath(SpriteProject, string?)"/>
    public static string GetExportImagePath(ExportSettings export, string projectPath)
    {
        var name = string.IsNullOrWhiteSpace(export.FileName)
            ? Path.GetFileNameWithoutExtension(projectPath)
            : export.FileName.Trim();
        return Path.Combine(GetExportFolder(export, projectPath), name + export.ImageFormat.Extension());
    }

    /// <summary>부위 아틀라스 이미지 경로: 프레임 시트와 같은 폴더의 (지정 이름 또는 <c>{프레임 시트 이름}_parts</c>).(형식 확장자). 나누면 이 이름이 앞부분.</summary>
    public static string GetPartsImagePath(SpriteProject project, string? projectPath = null)
        => GetPartsImagePath(project.Export, RequirePath(project, projectPath));

    /// <inheritdoc cref="GetPartsImagePath(SpriteProject, string?)"/>
    public static string GetPartsImagePath(ExportSettings export, string projectPath)
    {
        var name = string.IsNullOrWhiteSpace(export.PartsFileName)
            ? Path.GetFileNameWithoutExtension(GetExportImagePath(export, projectPath)) + "_parts"
            : export.PartsFileName.Trim();
        return Path.Combine(GetExportFolder(export, projectPath), name + export.ImageFormat.Extension());
    }

    private static string RequirePath(SpriteProject project, string? projectPath)
        => projectPath ?? project.Path ?? throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.Export_SaveProjectFirst);

    private static string GetExportFolder(ExportSettings export, string projectPath)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "";
        return string.IsNullOrWhiteSpace(export.Folder)
            ? projectDir
            : Path.GetFullPath(Path.Combine(projectDir, export.Folder));
    }

    /// <summary>
    /// 켜진 결과 이미지 경로(존재 여부와 무관): 프레임 시트 파일들, 부위 아틀라스 파일들(내용 있는 부위가 있을 때). 나누면 여러 개.
    /// Sprite Library 경로는 <see cref="GetLibraryPaths(SpriteProject, string, bool)"/>.
    /// </summary>
    public static (IReadOnlyList<string> Frames, IReadOnlyList<string> Parts) GetOutputPaths(SpriteProject project, string? projectPath = null)
        => GetOutputPaths(project, RequirePath(project, projectPath), assumePartContent: false);

    /// <param name="assumePartContent">true면 셀 내용을 보지 않고 모든 부위 레이어·스킨을 쓴다(셀을 풀지 않은 프로젝트 정보·자주 부르는 화면 표시용).</param>
    public static (IReadOnlyList<string> Frames, IReadOnlyList<string> Parts) GetOutputPaths(SpriteProject project, string projectPath, bool assumePartContent)
    {
        var export = project.Export;
        var ext = export.ImageFormat.Extension();
        var folder = GetExportFolder(export, projectPath);
        var frames = new List<string>();
        var parts = new List<string>();
        if (export.ExportFrames)
        {
            var baseName = Path.GetFileNameWithoutExtension(GetExportImagePath(export, projectPath));
            frames.AddRange(FrameOutputs(project, baseName).Select(o => Path.Combine(folder, o.Name + ext)));
        }
        if (export.ExportParts)
        {
            var keys = assumePartContent ? AllPartFileKeys(project) : PartCels(project).Select(c => FileKey(project, c)).ToList();
            if (keys.Count > 0)
            {
                var baseName = Path.GetFileNameWithoutExtension(GetPartsImagePath(export, projectPath));
                parts.AddRange(PartsOutputs(project, baseName, keys).Select(o => Path.Combine(folder, o.Name + ext)));
            }
        }
        return (frames, parts);
    }

    /// <summary>
    /// Sprite Library(<c>.spriteLib</c>) 경로: 부위 아틀라스와 Sprite Library가 켜져 있을 때 내용 있는 부위가 있는 그룹 × 내보낼 스킨마다
    /// <c>{부위 아틀라스}_{그룹}@{스킨}.spriteLib</c>(부위 아틀라스와 같은 폴더).
    /// </summary>
    /// <param name="assumePartContent">true면 셀 내용을 보지 않고 부위가 있는 모든 그룹을 쓴다(화면 표시용).</param>
    public static IReadOnlyList<string> GetLibraryPaths(SpriteProject project, string projectPath, bool assumePartContent)
    {
        var export = project.Export;
        if (!export.ExportParts || !export.WriteSpriteLibrary) return [];
        var groups = assumePartContent
            ? PartLayers(project).Select(p => p.Parent!).Distinct().ToList()
            : PartCels(project).Select(c => c.Part.Parent!).Distinct().ToList();
        if (groups.Count == 0) return [];
        var folder = GetExportFolder(export, projectPath);
        var baseName = Path.GetFileNameWithoutExtension(GetPartsImagePath(export, projectPath));
        return LibraryOutputs(project, baseName, groups).Select(o => Path.Combine(folder, o.Name + SpriteLibraryExtension)).ToList();
    }

    /// <summary>메타를 쓸지: 설정값, 자동이면 Unity Assets 안이거나 이미 .meta가 있을 때.</summary>
    public static bool ShouldWriteMeta(SpriteProject project, string pngPath)
        => project.Export.WriteMeta
           ?? (UnityProjectLocator.IsInUnityAssets(pngPath) || File.Exists(UnityMeta.GetMetaPath(pngPath)));

    /// <summary>부위 아틀라스 메타를 쓸지: Sprite Library를 쓰면 스프라이트를 가리켜야 하므로 항상, 아니면 <see cref="ShouldWriteMeta"/>.</summary>
    public static bool ShouldWritePartsMeta(SpriteProject project, string pngPath)
        => project.Export.WriteSpriteLibrary || ShouldWriteMeta(project, pngPath);

    // ───────────── 이름 ─────────────

    /// <summary>Unity 스프라이트 이름으로 정리: 제어 문자를 빼고 경로 구분자(<c>/</c>, <c>\</c>)는 <c>_</c>로, 앞뒤 공백을 없앤다. 비면 "sprite".</summary>
    public static string CleanSpriteName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            if (!char.IsControl(ch)) sb.Append(ch is '/' or '\\' ? '_' : ch);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "sprite" : s;
    }

    private static readonly char[] BadFileChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>파일 이름 조각으로 정리: 어느 OS에서도 못 쓰는 문자(<c>&lt;&gt;:"/\|?*</c>, 제어 문자)는 <c>_</c>, 앞뒤 공백·끝 점은 없앤다. 비면 "_".</summary>
    public static string CleanFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsControl(ch) || Array.IndexOf(BadFileChars, ch) >= 0 || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>겹치지 않게 <c>_1</c>, <c>_2</c> …를 붙여 <paramref name="used"/>에 넣는다.</summary>
    private static string Unique(HashSet<string> used, string name)
    {
        var unique = name;
        for (int n = 1; !used.Add(unique); n++) unique = $"{name}_{n}";
        return unique;
    }

    private static int IndexOfTag(SpriteProject project, AnimationTag? tag)
    {
        if (tag is null) return project.Tags.Count;
        for (int i = 0; i < project.Tags.Count; i++)
            if (ReferenceEquals(project.Tags[i], tag)) return i;
        return project.Tags.Count;
    }

    /// <summary>프레임 칸 열쇠: 태그 기준이고 태그가 있으면 <c>{태그}_{태그 안 번호}</c>, 아니면 프레임 번호(Sprite Library 라벨에도 씀).</summary>
    internal static string FrameKey(SpriteProject project, int frameIndex, bool byTag)
        => byTag && project.TagOfFrame(frameIndex) is { } tag ? $"{tag.Name}_{frameIndex - tag.From}" : frameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ───────────── 배치 엔진(프레임 시트·부위 아틀라스 공통) ─────────────

    /// <summary>값 배열의 사전순 비교.</summary>
    private sealed class KeyComparer : IComparer<int[]>, IEqualityComparer<int[]>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(int[]? a, int[]? b)
        {
            for (int i = 0; i < a!.Length; i++)
            {
                int c = a[i].CompareTo(b![i]);
                if (c != 0) return c;
            }
            return 0;
        }

        public bool Equals(int[]? a, int[]? b) => Compare(a, b) == 0;

        public int GetHashCode(int[] obj)
        {
            var h = new HashCode();
            foreach (var v in obj) h.Add(v);
            return h.ToHashCode();
        }
    }

    /// <summary>칸들을 정렬 기준 값의 사전순(같으면 원래 순서)으로 늘어놓은 번호.</summary>
    private static int[] SortedOrder(IReadOnlyList<int[]> values)
        => Enumerable.Range(0, values.Count).OrderBy(i => values[i], KeyComparer.Instance).ToArray();

    /// <summary>
    /// 칸 배치: 격자면 앞쪽 <paramref name="rowBreak"/>개 기준 값이 같은 칸끼리 묶음(새 줄에서 시작), 묶음 안 자리는 나머지 기준으로
    /// (프레임 기준 = 값 그대로라 같은 프레임은 같은 열, 그 밖의 연속한 기준 = 전체에서 같은 값 묶음의 순번), 칸 수 설정대로 줄바꿈.
    /// 자동 배치면 정렬 순서대로 <see cref="AtlasPacker"/>. 2의 거듭제곱 설정을 맞춘다.
    /// </summary>
    /// <param name="slotSizes">격자 자리 크기(프레임 시트는 모든 칸이 가장 큰 칸 크기).</param>
    /// <param name="sizes">실제 칸 크기(자동 배치·결과 사각형).</param>
    /// <param name="fixedRects">자동 배치에서 원래 위치를 지킬 칸(없으면 null).</param>
    private static (Rectangle[] Rects, Size Size) Arrange(SheetOptions o, IReadOnlyList<GridKey> keys, int rowBreak, IReadOnlyList<int[]> values,
        IReadOnlyList<Size> slotSizes, IReadOnlyList<Size> sizes, IReadOnlyList<Rectangle?>? fixedRects = null, Size minSize = default)
    {
        int n = values.Count;
        var rects = new Rectangle[n];
        if (o.Layout == SheetLayout.Packed)
        {
            var order = SortedOrder(values);
            var pack = AtlasPacker.Pack(order.Select(i => sizes[i]).ToList(),
                new AtlasPackOptions(o.MaxWidth, o.Spacing, o.PowerOfTwo, o.PackOrder, minSize),
                fixedRects is null ? null : order.Select(i => fixedRects[i]).ToList());
            for (int k = 0; k < n; k++) rects[order[k]] = pack.Rects[k];
            return (rects, pack.Size);
        }

        // 묶음(줄) 번호
        rowBreak = Math.Clamp(rowBreak, 0, keys.Count);
        var groupKeys = values.Select(v => v[..rowBreak]).ToList();
        var distinctGroups = groupKeys.Distinct(KeyComparer.Instance).Order(KeyComparer.Instance).ToList();
        var groupIndex = new Dictionary<int[], int>(KeyComparer.Instance);
        for (int g = 0; g < distinctGroups.Count; g++) groupIndex[distinctGroups[g]] = g;

        // 자리 번호: 줄바꿈 뒤 기준들을 바깥 → 안쪽으로 섞은 진법
        var slots = new long[n];
        int d = rowBreak;
        while (d < keys.Count)
        {
            if (keys[d] == GridKey.Frame)
            {
                int radix = values.Max(v => v[d]) + 1;
                for (int i = 0; i < n; i++) slots[i] = slots[i] * radix + values[i][d];
                d++;
                continue;
            }
            int end = d;
            while (end < keys.Count && keys[end] != GridKey.Frame) end++;
            int from = d, to = end;
            var subs = values.Select(v => v[from..to]).ToList();
            var distinct = subs.Distinct(KeyComparer.Instance).Order(KeyComparer.Instance).ToList();
            var rank = new Dictionary<int[], int>(KeyComparer.Instance);
            for (int r = 0; r < distinct.Count; r++) rank[distinct[r]] = r;
            for (int i = 0; i < n; i++) slots[i] = slots[i] * distinct.Count + rank[subs[i]];
            d = end;
        }
        var items = new List<GridItem>(n);
        for (int i = 0; i < n; i++) items.Add(new GridItem(groupIndex[groupKeys[i]], (int)Math.Min(slots[i], int.MaxValue / 2)));
        bool uniform = keys.Count > 0 && keys[^1] == GridKey.Frame;
        int maxSlots = items.Max(i => i.Slot) + 1;
        int groupCount = distinctGroups.Count;
        int cols = ChooseColumns(o.Grid, o.GridCount, maxSlots,
            c => { var r = PlaceGrid(items, slotSizes, groupCount, c, o.Spacing, uniform); return (r.Size, r.Rows); });
        var (placed, size, _) = PlaceGrid(items, slotSizes, groupCount, cols, o.Spacing, uniform);
        for (int i = 0; i < n; i++) rects[i] = new Rectangle(placed[i].Location, sizes[i]);
        int w = size.Width, h = size.Height;
        if (o.PowerOfTwo)
        {
            w = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, w));
            h = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, h));
        }
        return (rects, new Size(w, h));
    }

    /// <summary>격자에 놓을 칸: 그룹(새 줄에서 시작)과 그룹 안 자리 번호.</summary>
    private readonly record struct GridItem(int Group, int Slot);

    /// <summary>
    /// 격자 가로 칸 수. 자동: 시트의 긴 변이 가장 작게 → 넓이가 가장 작게 → 가로로 넓게.
    /// 가로 N칸: N(자리 수보다 크면 자리 수). 세로 N칸: 줄 수가 N 이하인 가장 적은 칸 수(없으면 줄바꿈 없음 = 자리 수).
    /// </summary>
    private static int ChooseColumns(SheetGrid mode, int count, int maxSlots, Func<int, (Size Size, int Rows)> measure)
    {
        maxSlots = Math.Max(1, maxSlots);
        switch (mode)
        {
            case SheetGrid.Columns:
                return Math.Clamp(count, 1, maxSlots);
            case SheetGrid.Rows:
                for (int c = 1; c < maxSlots; c++)
                    if (measure(c).Rows <= count) return c;
                return maxSlots;
            default:
                int best = maxSlots;
                long bestSide = long.MaxValue, bestArea = long.MaxValue;
                for (int c = maxSlots; c >= 1; c--) // 넓은 것부터 보고 같으면 먼저 본 것(넓은 것)을 남긴다
                {
                    var s = measure(c).Size;
                    long side = Math.Max(s.Width, s.Height), area = (long)s.Width * s.Height;
                    if (side < bestSide || (side == bestSide && area < bestArea))
                    {
                        best = c;
                        bestSide = side;
                        bestArea = area;
                    }
                }
                return best;
        }
    }

    /// <summary>
    /// 격자 배치: 그룹마다 새 줄에서 시작하고 자리 번호를 <paramref name="cols"/>칸마다 줄바꿈(빈 자리는 비움 — 열이 맞음).
    /// <paramref name="uniformColumns"/>면 열 폭 = 가장 넓은 칸, 줄 높이 = 그 그룹에서 가장 높은 칸,
    /// 아니면 열 폭 = 그 열에서 가장 넓은 칸, 줄 높이 = 가장 높은 칸. 칸은 자리의 왼쪽 위에 붙인다.
    /// </summary>
    private static (Rectangle[] Rects, Size Size, int Rows) PlaceGrid(IReadOnlyList<GridItem> items, IReadOnlyList<Size> sizes,
        int groupCount, int cols, int spacing, bool uniformColumns)
    {
        var maxSlot = new int[groupCount];
        Array.Fill(maxSlot, -1);
        foreach (var it in items) maxSlot[it.Group] = Math.Max(maxSlot[it.Group], it.Slot);
        var rowStart = new int[groupCount];
        int rows = 0;
        for (int g = 0; g < groupCount; g++)
        {
            rowStart[g] = rows;
            if (maxSlot[g] >= 0) rows += maxSlot[g] / cols + 1;
        }
        int usedCols = Math.Min(cols, maxSlot.Max() + 1);
        var colW = new int[usedCols];
        var rowH = new int[rows];
        if (uniformColumns)
        {
            Array.Fill(colW, sizes.Max(s => s.Width));
            var groupH = new int[groupCount];
            for (int i = 0; i < items.Count; i++) groupH[items[i].Group] = Math.Max(groupH[items[i].Group], sizes[i].Height);
            for (int g = 0; g < groupCount; g++)
                if (maxSlot[g] >= 0)
                    for (int r = 0; r <= maxSlot[g] / cols; r++) rowH[rowStart[g] + r] = groupH[g];
        }
        else
        {
            Array.Fill(rowH, sizes.Max(s => s.Height));
            for (int i = 0; i < items.Count; i++) colW[items[i].Slot % cols] = Math.Max(colW[items[i].Slot % cols], sizes[i].Width);
        }
        var colX = new int[usedCols];
        for (int c = 1; c < usedCols; c++) colX[c] = colX[c - 1] + colW[c - 1] + spacing;
        var rowY = new int[rows];
        for (int r = 1; r < rows; r++) rowY[r] = rowY[r - 1] + rowH[r - 1] + spacing;
        var rects = new Rectangle[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            rects[i] = new Rectangle(colX[it.Slot % cols], rowY[rowStart[it.Group] + it.Slot / cols], sizes[i].Width, sizes[i].Height);
        }
        return (rects, new Size(colX[^1] + colW[^1], rowY[^1] + rowH[^1]), rows);
    }

    /// <summary>
    /// 칸 하나를 (잘라내기면) 투명 테두리를 잘라 낸다: 잘린 이미지, 그 이미지 기준 피벗(같은 지점), 테두리(같은 선, 0 이상). 다 투명하거나 그대로면 원래 것.
    /// </summary>
    private static (PixelImage Image, Pivot Pivot, SpriteBorder Border) TrimCel(PixelImage image, Pivot pivot, SpriteBorder border, bool trim)
    {
        if (!trim) return (image, pivot, border);
        var r = image.GetOpaqueBounds();
        if (r.IsEmpty || r == image.Bounds) return (image, pivot, border);
        var (px, py) = pivot.ToLocalPixel(image.Width, image.Height);
        var b = border.IsNone ? border : new SpriteBorder(
            Math.Max(0, border.Left - r.X), Math.Max(0, border.Top - r.Y),
            Math.Max(0, border.Right - (image.Width - r.Right)), Math.Max(0, border.Bottom - (image.Height - r.Bottom)));
        return (image.Crop(r), new Pivot((px - r.X) / r.Width, 1f - (py - r.Y) / r.Height), b);
    }

    /// <summary>
    /// 부위 아틀라스 스프라이트 하나: 부위 셀에 그 부위 자신의 효과(<see cref="Layer.Effects"/> 중 프레임 <paramref name="frameIndex"/>가 범위 안인 것)를 입힌 그림.
    /// 바깥 외곽선·그림자처럼 밖으로 번지는 효과가 있으면 스프라이트를 효과 여백만큼 넓히고 — 여백은 프레임 범위와 관계없이 켜진 모든 효과 기준이라
    /// 효과가 없는 프레임도 같은 크기(모든 프레임·스킨 같은 크기) — 피벗은 같은 픽셀을, 9-slice 테두리는 같은 선을 가리키도록 옮긴다
    /// (Unity에서 같은 피벗에 놓으면 그림이 캔버스와 같은 자리에 보인다). 그룹 효과는 부위들을 합친 모양에 대한 것이라 부위 스프라이트에는 넣지 않는다
    /// (프레임 시트에만). 효과가 없으면 셀·피벗·테두리 그대로.
    /// </summary>
    private static (PixelImage Image, Pivot Pivot, SpriteBorder Border) PartSprite(SpriteProject project, Layer part, Skin? skin, PixelImage cel, int frameIndex)
    {
        var pad = LayerEffects.Padding(part.Effects, cel.Width, cel.Height);
        // 다른 프레임을 보는 효과(잔상)도 이 칸과 같은 스킨(없으면 첫 스킨)의 셀을 본다
        var effects = project.EffectsAt(part, frameIndex, skin ?? part.Parent?.SkinList.FirstOrDefault());
        var (image, dx, dy) = LayerEffects.Render(cel, effects, pad);
        if (ReferenceEquals(image, cel)) return (cel, part.PartPivot, part.PartBorder);
        int left = -dx, top = -dy, right = image.Width - cel.Width - left, bottom = image.Height - cel.Height - top;
        var (px, py) = part.PartPivot.ToLocalPixel(cel.Width, cel.Height);
        var pivot = new Pivot((px + left) / image.Width, 1f - (py + top) / image.Height);
        var border = part.PartBorder.IsNone ? part.PartBorder
            : new SpriteBorder(part.PartBorder.Left + left, part.PartBorder.Top + top, part.PartBorder.Right + right, part.PartBorder.Bottom + bottom);
        return (image, pivot, border);
    }

    // ───────────── 프레임 시트 ─────────────

    /// <summary>
    /// 프레임 시트 스킨 항목: 스킨이 "지금 착장만"이면 지금 착장 하나. "고른 스킨"이면 부위가 있는(뺀 레이어가 아닌) 그룹마다 체크한 스킨
    /// (그 그룹만 그 스킨, 나머지 그룹은 지금 착장) → 고른 착장 프리셋. 고른 것이 없으면 지금 착장.
    /// 이름 뒤 <c>@…</c>: 첫 번째로 고른 첫 스킨 항목은 없음(원래 이름), 나머지는 스킨·착장 이름 — 스킨 이름이 겹치면 <c>@{그룹}.{스킨}</c>.
    /// </summary>
    public static IReadOnlyList<FrameVariant> FrameVariants(SpriteProject project)
    {
        var o = project.Export.Frames;
        FrameVariant current = new(null, null, null, null);
        if (o.Skins == SkinSource.Current) return [current];
        var items = new List<(Layer? Group, Skin? Skin, Outfit? Outfit)>();
        for (int i = project.Layers.Count - 1; i >= 0; i--)
        {
            var g = project.Layers[i];
            if (!g.IsGroup || project.ChildCount(g) == 0 || o.ExcludedLayers.Contains(g)) continue;
            // 전체 스킨, 그다음 부위 전용 스킨(그 부위만 그 스킨, 나머지는 지금 착장)
            foreach (var s in g.AllSkins)
                if (!o.ExcludedSkins.Contains(s)) items.Add((g, s, null));
        }
        foreach (var outfit in project.Outfits)
            if (o.Outfits.Contains(outfit)) items.Add((null, null, outfit));
        if (items.Count == 0) return [current];

        int plain = items.FindIndex(t => t.Group is { } g && ReferenceEquals(t.Skin, g.BaseSkin));
        var names = items.Select(t => t.Outfit?.Name ?? t.Skin!.Name).ToList();
        var counts = names.Where((_, i) => i != plain).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<FrameVariant>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var (g, s, outfit) = items[i];
            string? suffix = null;
            if (i != plain)
            {
                var name = names[i];
                if (outfit is null && counts[name] > 1) name = $"{g!.Name}.{s!.Name}";
                suffix = Unique(used, name);
            }
            list.Add(new FrameVariant(suffix, g, s, outfit));
        }
        return list;
    }

    /// <summary>항목을 입힐 착장(지금 착장이면 null, 그룹 스킨 항목이면 그 그룹만 그 스킨, 부위 전용 스킨이면 그 부위만 그 스킨인 임시 착장).</summary>
    private static Outfit? Dress(FrameVariant v)
        => v.Outfit ?? (v.Group is { } g && v.Skin is { } s
            ? s.Owner is { } part ? new Outfit("", [], [new OutfitPart(part, s)]) : new Outfit("", [new OutfitSlot(g, s)], [])
            : null);

    /// <summary>프레임 시트 결과 파일: 이름, 이름을 정하는 태그, 프레임들(null = 모든 프레임), 담을 스킨 항목들.</summary>
    private sealed record FrameOutput(string Name, AnimationTag? Tag, IReadOnlyList<int>? Frames, IReadOnlyList<FrameVariant> Variants);

    /// <summary>
    /// 프레임 시트 결과 파일들: 스킨마다 따로 파일이면(또는 Single 모드) 항목마다 <c>{시트}@{항목}</c>(원래 이름 항목은 그대로), 아니면 모든 항목을 한 장에.
    /// 태그마다 따로 파일이면 그 안에서 태그마다 <c>{시트}_{태그}…</c>, 태그 없는 프레임은 <c>{시트}_untagged…</c>.
    /// </summary>
    private static List<FrameOutput> FrameOutputs(SpriteProject project, string baseName)
    {
        var o = project.Export.Frames;
        bool single = project.Sprite.Mode == SpriteMode.Single;
        bool splitTags = o.SplitTags && !single && project.Tags.Count > 0;
        var variants = FrameVariants(project);
        var sets = (o.SplitSkins || single) && variants.Count > 1 ? variants.Select(v => (IReadOnlyList<FrameVariant>)[v]).ToList() : [variants];
        var untagged = splitTags ? Enumerable.Range(0, project.Frames.Count).Where(i => project.TagOfFrame(i) is null).ToList() : [];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<FrameOutput>();
        foreach (var set in sets)
        {
            var suffix = set.Count == 1 && set[0].Suffix is { } s ? SkinSeparator + CleanFileName(s) : "";
            if (!splitTags)
            {
                list.Add(new FrameOutput(Unique(used, baseName + suffix), null, null, set));
                continue;
            }
            foreach (var tag in project.Tags)
                list.Add(new FrameOutput(Unique(used, $"{baseName}_{CleanFileName(tag.Name)}{suffix}"), tag, Enumerable.Range(tag.From, tag.To - tag.From + 1).ToList(), set));
            if (untagged.Count > 0) list.Add(new FrameOutput(Unique(used, $"{baseName}_{UntaggedSuffix}{suffix}"), null, untagged, set));
        }
        return list;
    }

    /// <summary>
    /// 한 장짜리 프레임 시트(지금 착장, 나누기·스킨 항목 설정과 무관): 모든 프레임을 배치·정렬 설정대로. Single 모드이고 <paramref name="allFrames"/>가 false면 프레임 하나짜리 시트.
    /// 움직임 테스트·미리보기용. 뺀 레이어(<see cref="SheetOptions.ExcludedLayers"/>)는 합성하지 않는다.
    /// </summary>
    /// <param name="baseName">이름 규칙의 <c>{시트 이름}</c>(보통 내보낼 이미지 이름).</param>
    public static SheetResult BuildSheet(SpriteProject project, string baseName, bool allFrames = false)
    {
        FrameVariant current = new(null, null, null, null);
        var cache = NewCache();
        if (!allFrames && project.Sprite.Mode == SpriteMode.Single) return SingleSheet(project, baseName, current, null, cache);
        return FrameSheet(project, baseName, null, null, [current], null, cache);
    }

    /// <summary>Single 모드 시트: <see cref="ExportSettings.SingleFrameIndex"/> 프레임 하나를 캔버스 크기 그대로(항목의 착장으로).</summary>
    private static SheetResult SingleSheet(SpriteProject project, string baseName, FrameVariant variant, WorkStage? stage, EffectRenderCache cache)
    {
        int fi = Math.Clamp(project.Export.SingleFrameIndex, 0, project.Frames.Count - 1);
        var image = Composite(project, fi, Dress(variant), cache);
        stage?.Step(CompositeUnits(project));
        var entry = new SheetEntry(fi, CleanSpriteName(baseName), image.Bounds, project.GetFramePivot(fi), null, Variant: variant);
        return new SheetResult(image, [entry], image.Size);
    }

    /// <summary>결과 파일마다의 프레임 시트(스킨 항목·태그마다 나누기 설정대로). 칸 이름은 파일마다 같은 규칙이다.</summary>
    /// <param name="baseName">시트 이름(나누면 파일 이름 앞부분, 칸 이름 <c>{시트 이름}_{프레임}</c>에도 씀).</param>
    public static IReadOnlyList<SheetFile> BuildFrameSheets(SpriteProject project, string baseName) => BuildFrameSheets(project, baseName, null, NewCache());

    private static IReadOnlyList<SheetFile> BuildFrameSheets(SpriteProject project, string baseName, WorkStage? stage, EffectRenderCache cache)
        => FrameOutputs(project, baseName)
            .Select(o => new SheetFile(o.Name,
                project.Sprite.Mode == SpriteMode.Single ? SingleSheet(project, baseName, o.Variants[0], stage, cache) : FrameSheet(project, baseName, o.Frames, o.Tag, o.Variants, stage, cache),
                o.Tag, Variant: o.Variants.Count == 1 ? o.Variants[0] : null))
            .ToList();

    /// <summary>프레임 하나를 착장을 입혀 합성(내보내기에서 뺀 레이어 제외).</summary>
    private static PixelImage Composite(SpriteProject project, int frameIndex, Outfit? outfit, EffectRenderCache? cache)
        => project.FlattenOutfit(frameIndex, outfit, project.Export.Frames.ExcludedLayers, cache);

    /// <summary>
    /// 프레임 하나를 프레임 시트 칸과 똑같이(지금 착장, 레이어 효과, 숨긴·내보내기에서 뺀 레이어 제외) 캔버스 크기 그대로 합성 — GIF 등 애니메이션 내보내기용.
    /// 여러 프레임을 이어 합성하면 같은 <paramref name="cache"/>(<see cref="NewCache"/>)를 넘겨 잔상 원본을 나눠 쓴다.
    /// </summary>
    public static PixelImage CompositeFrame(SpriteProject project, int frameIndex, EffectRenderCache? cache = null) => Composite(project, frameIndex, null, cache);

    /// <summary>
    /// 내보내기 한 번 동안 프레임 합성이 나눠 쓰는 효과 캐시(잔상 원본 등). 예산을 넘으면 오래 안 쓴 것부터 버리므로 크기만 정한다.
    /// </summary>
    public static EffectRenderCache NewCache() => new(ExportCacheBytes);

    private const long ExportCacheBytes = 256L << 20;

    /// <summary>
    /// 진행 단위: 픽셀 하나 압축 = 1. 프레임 합성(효과 포함)은 픽셀당 이만큼으로 친다
    /// (1024², 60프레임, 효과 4개 + 잔상 8개에서 잰 합성 시간 ÷ 시트 압축 시간).
    /// </summary>
    private const int CompositeWeight = 2;

    private static long CompositeUnits(SpriteProject project) => (long)project.Width * project.Height * CompositeWeight;

    /// <summary>
    /// 프레임 시트 한 장: 스킨 항목 × 프레임(태그 기준이 정렬에 있으면 여러 태그에 든 프레임은 태그마다). <paramref name="frames"/>가 null이면 모든 프레임,
    /// 아니면 그 프레임들(이름 태그 = <paramref name="fileTag"/>). 항목이 여럿이면(한 이미지에 모두) 칸 이름 뒤에 <c>@{항목}</c>.
    /// </summary>
    private static SheetResult FrameSheet(SpriteProject project, string baseName, IReadOnlyList<int>? frames, AnimationTag? fileTag, IReadOnlyList<FrameVariant> variants,
        WorkStage? stage, EffectRenderCache cache)
    {
        var o = project.Export.Frames;
        var (keys, rowBreak) = o.Order.For(hasParts: false);
        bool tagRows = keys.Contains(GridKey.Tag) && frames is null && project.Tags.Count > 0;
        bool byTag = o.Naming == SpriteNaming.ByTag;
        bool combined = variants.Count > 1;

        // 1) 칸 목록: (항목, 프레임, 이름 태그, 태그 번호, 프레임 자리 값)
        var cells = new List<(int Variant, int Frame, AnimationTag? Tag, int TagIndex, int FrameValue)>();
        var list = frames ?? Enumerable.Range(0, project.Frames.Count).ToList();
        for (int vi = 0; vi < variants.Count; vi++)
        {
            if (tagRows)
            {
                for (int ti = 0; ti < project.Tags.Count; ti++)
                {
                    var tag = project.Tags[ti];
                    for (int fi = tag.From; fi <= tag.To; fi++) cells.Add((vi, fi, tag, ti, fi - tag.From));
                }
                int u = 0;
                for (int fi = 0; fi < project.Frames.Count; fi++)
                    if (project.TagOfFrame(fi) is null) cells.Add((vi, fi, null, project.Tags.Count, u++));
                continue;
            }
            for (int k = 0; k < list.Count; k++)
            {
                int fi = list[k];
                var tag = fileTag ?? project.TagOfFrame(fi);
                cells.Add((vi, fi, tag, IndexOfTag(project, tag), k));
            }
        }

        // 2) 합성·잘라내기(항목 × 프레임마다 한 번)
        var content = new Dictionary<(int, int), (PixelImage Image, Pivot Pivot)>();
        var dresses = variants.Select(Dress).ToList();
        foreach (var (vi, fi, _, _, _) in cells)
            if (!content.ContainsKey((vi, fi)))
            {
                var (img, pivot, _) = TrimCel(Composite(project, fi, dresses[vi], cache), project.GetFramePivot(fi), default, o.Trim);
                stage?.Step(CompositeUnits(project));
                content[(vi, fi)] = (img, pivot);
            }
        var cell = new Size(content.Values.Max(c => c.Image.Width), content.Values.Max(c => c.Image.Height));

        // 3) 배치
        var values = cells.Select(c => keys.Select(k => k switch
        {
            GridKey.Skin => c.Variant,
            GridKey.Tag => c.TagIndex,
            _ => c.FrameValue,
        }).ToArray()).ToList();
        var sizes = cells.Select(c => content[(c.Variant, c.Frame)].Image.Size).ToList();
        var (rects, size) = Arrange(o, keys, rowBreak, values, Enumerable.Repeat(cell, cells.Count).ToList(), sizes);
        var sheet = new PixelImage(size.Width, size.Height);

        // 4) 이름(만든 순서로 겹침 처리) · 칸
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new string[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            var (vi, fi, tag, _, _) = cells[i];
            var name = project.Frames[fi].Name ?? (byTag && tag is not null ? $"{tag.Name}_{fi - tag.From}" : $"{baseName}_{fi}");
            if (combined && variants[vi].Suffix is { } s) name += SkinSeparator + s;
            names[i] = Unique(used, CleanSpriteName(name));
        }
        var entries = new List<SheetEntry>(cells.Count);
        foreach (var i in SortedOrder(values))
        {
            var (vi, fi, tag, _, _) = cells[i];
            var (img, pivot) = content[(vi, fi)];
            sheet.Blit(img, rects[i].X, rects[i].Y);
            entries.Add(new SheetEntry(fi, names[i], rects[i], pivot, tag?.Name, Variant: variants[vi]));
        }
        return new SheetResult(sheet, entries, cell);
    }

    // ───────────── 부위 아틀라스 ─────────────

    /// <summary>부위 레이어: 그룹 위 → 아래, 그룹 안 위 부위부터.</summary>
    private static List<Layer> PartLayers(SpriteProject project)
    {
        var parts = new List<Layer>();
        for (int g = project.Layers.Count - 1; g >= 0; g--)
        {
            var group = project.Layers[g];
            if (!group.IsGroup || project.Export.Parts.ExcludedLayers.Contains(group)) continue;
            var children = project.ChildrenOf(group);
            for (int c = children.Count - 1; c >= 0; c--) parts.Add(children[c]);
        }
        return parts;
    }

    /// <summary>부위 아틀라스 스프라이트 하나: 부위 × 스킨(null = 그룹의 첫 스킨) × 프레임.</summary>
    private readonly record struct PartCel(Layer Part, Skin? Skin, int Frame);

    /// <summary>부위 아틀라스에 이 스킨을 내보낼지: 지금 착장만이면 부위가 지금 보이는 스킨, 고른 스킨이면 체크한 스킨.</summary>
    private static bool Exported(SheetOptions o, Layer part, Skin skin)
        => o.Skins == SkinSource.Current ? ReferenceEquals(part.ShownSkin, skin) : !o.ExcludedSkins.Contains(skin);

    private static bool HasContent(PixelImage? image) => image is not null && !image.IsRegionEmpty(image.Bounds);

    /// <summary>
    /// 부위 아틀라스에 들어갈 스프라이트 목록(이름을 정하는 기본 순서): <see cref="PartLayers"/> 순서로 부위마다 첫 스킨의 내용 있는 프레임,
    /// 그다음 내보낼 스킨 순서대로 그 스킨이 직접 그린 프레임(<see cref="SheetOptions.BakeInheritedSkins"/>면 물려받아 보이는 프레임도).
    /// 첫 스킨을 내보내지 않아도 남은 스킨이 물려받는 프레임의 첫 스킨 스프라이트는 남긴다(구워 넣으면 남기지 않음).
    /// </summary>
    private static List<PartCel> PartCels(SpriteProject project)
    {
        var o = project.Export.Parts;
        bool bake = o.BakeInheritedSkins;
        var cels = new List<PartCel>();
        foreach (var part in PartLayers(project))
        {
            var group = part.Parent!;
            // 이 부위가 고를 수 있는 스킨(전체 스킨 + 이 부위 전용 스킨) 중 첫 스킨이 아닌 것
            var extras = part.SkinChoices.Skip(1).Where(s => Exported(o, part, s)).ToList();
            bool baseExported = Exported(o, part, group.BaseSkin!);
            for (int fi = 0; fi < project.Frames.Count; fi++)
            {
                var frame = project.Frames[fi];
                if (!HasContent(project.GetCelRaw(part, frame, null))) continue;
                if (baseExported || (!bake && extras.Any(s => !HasContent(project.GetCelRaw(part, frame, s)))))
                    cels.Add(new PartCel(part, null, fi));
            }
            foreach (var skin in extras)
                for (int fi = 0; fi < project.Frames.Count; fi++)
                {
                    var frame = project.Frames[fi];
                    if (HasContent(project.GetCelRaw(part, frame, skin)) || (bake && HasContent(project.GetCelRaw(part, frame, null))))
                        cels.Add(new PartCel(part, skin, fi));
                }
        }
        return cels;
    }

    /// <summary>
    /// 부위 아틀라스 칸 이름(<see cref="SheetOptions.Naming"/>): 프레임 번호 = 프레임 0이 <c>{부위}</c>, 그다음 <c>{부위}_{프레임}</c>;
    /// 태그 기준 = <c>{부위}_{첫 태그}_{태그 안 번호}</c>(태그 없는 프레임은 프레임 번호 규칙). 첫 스킨이 아닌 스킨은 뒤에 <c>@{스킨}</c>.
    /// 정리 후 아틀라스 전체에서 겹치면 <c>_1</c> …(첫 스킨 이름을 먼저 정해 스킨이 없던 때와 같은 이름을 쓴다). 배치·나누기와 관계없이 같다.
    /// </summary>
    private static List<string> PartNames(SpriteProject project, List<PartCel> cels)
    {
        var byTag = project.Export.Parts.Naming == SpriteNaming.ByTag;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new string[cels.Count];
        foreach (bool baseSkin in (bool[])[true, false])
            for (int i = 0; i < cels.Count; i++)
            {
                var (part, skin, fi) = cels[i];
                if ((skin is null) != baseSkin) continue;
                var tag = byTag ? project.TagOfFrame(fi) : null;
                var name = tag is not null ? $"{part.Name}_{tag.Name}_{fi - tag.From}" : fi == 0 ? part.Name : $"{part.Name}_{fi}";
                if (skin is not null) name += SkinSeparator + skin.Name;
                names[i] = Unique(used, CleanSpriteName(name));
            }
        return [.. names];
    }

    /// <summary>부위 아틀라스 스프라이트가 들어갈 파일의 열쇠(나누지 않은 축은 기본값: 부위 null, 태그 −1, 스킨 이름 null).</summary>
    private readonly record struct PartsFileKey(Layer? Part, int Tag, string? SkinName);

    private static bool SplitPartTags(SpriteProject project) => project.Export.Parts.SplitTags && project.Tags.Count > 0;

    private static PartsFileKey FileKey(SpriteProject project, PartCel c)
    {
        var o = project.Export.Parts;
        return new PartsFileKey(o.SplitParts ? c.Part : null, SplitPartTags(project) ? IndexOfTag(project, project.TagOfFrame(c.Frame)) : -1,
            o.SplitSkins ? c.Skin?.Name : null);
    }

    /// <summary>셀 내용을 보지 않은 모든 파일 열쇠(부위 × 태그 묶음 × 첫 스킨·내보낼 스킨).</summary>
    private static List<PartsFileKey> AllPartFileKeys(SpriteProject project)
    {
        var o = project.Export.Parts;
        var tags = SplitPartTags(project)
            ? Enumerable.Range(0, project.Frames.Count).Select(fi => IndexOfTag(project, project.TagOfFrame(fi))).Distinct().Order().ToList()
            : [-1];
        var keys = new List<PartsFileKey>();
        foreach (var part in PartLayers(project))
            foreach (var skin in part.SkinChoices.Skip(1).Where(s => Exported(o, part, s)).Prepend<Skin?>(null))
                foreach (var t in tags)
                    keys.Add(new PartsFileKey(o.SplitParts ? part : null, t, o.SplitSkins ? skin?.Name : null));
        return keys;
    }

    /// <summary>부위 아틀라스 결과 파일: 이름과 열쇠.</summary>
    private sealed record PartsFile(string Name, PartsFileKey Key);

    /// <summary>
    /// 부위 아틀라스 결과 파일들: 나누지 않으면 한 장. <c>{baseName}</c> + 부위마다 <c>_{부위}</c> + 태그마다 <c>_{태그}</c>(태그 없는 프레임 <c>_untagged</c>)
    /// + 스킨마다 <c>@{스킨}</c>(첫 스킨은 없음, 여러 그룹에 같은 이름의 스킨이 있으면 같은 파일). 첫 스킨 파일들이 먼저.
    /// </summary>
    private static List<PartsFile> PartsOutputs(SpriteProject project, string baseName, IReadOnlyList<PartsFileKey> keys)
    {
        var distinct = keys.Where(k => k.SkinName is null).Concat(keys.Where(k => k.SkinName is not null)).Distinct().ToList();
        if (distinct.Count == 0) distinct.Add(new PartsFileKey(null, -1, null));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return distinct.Select(k =>
        {
            var name = baseName
                       + (k.Part is null ? "" : "_" + CleanFileName(k.Part.Name))
                       + (k.Tag < 0 ? "" : "_" + (k.Tag < project.Tags.Count ? CleanFileName(project.Tags[k.Tag].Name) : UntaggedSuffix))
                       + (k.SkinName is null ? "" : SkinSeparator + CleanFileName(k.SkinName));
            return new PartsFile(Unique(used, name), k);
        }).ToList();
    }

    /// <summary>Sprite Library 결과 파일(확장자 없이): 그룹 × 내보낼 스킨마다 <c>{baseName}_{그룹}@{스킨}</c>.</summary>
    private static List<(string Name, Layer Group, Skin Skin)> LibraryOutputs(SpriteProject project, string baseName, IEnumerable<Layer> groups)
    {
        var o = project.Export.Parts;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, Layer, Skin)>();
        foreach (var g in groups)
        {
            var children = project.ChildrenOf(g);
            // 부위 전용 스킨도 라이브러리 하나(그 부위만 그 스킨 스프라이트, 다른 부위는 첫 스킨 스프라이트 — "기본 + 모자"처럼 바꿔 끼움)
            foreach (var s in g.AllSkins)
                if (children.Any(p => (s.Owner is null || ReferenceEquals(s.Owner, p)) && Exported(o, p, s)))
                    list.Add((Unique(used, $"{baseName}_{CleanFileName(g.Name)}{SkinSeparator}{CleanFileName(s.Name)}"), g, s));
        }
        return list;
    }

    /// <summary>
    /// 한 장짜리 부위 아틀라스(나누기 설정과 무관): 부위 × 내용 있는 프레임 × 내보낼 스킨마다 스프라이트 하나. 배치·정렬은 시트 설정대로.
    /// 부위가 없으면 칸 없는 1×1 시트.
    /// </summary>
    /// <param name="baseName">내보낼 부위 아틀라스 이름(칸 이름에는 쓰지 않음, <see cref="BuildSheet"/>와 같은 꼴).</param>
    public static SheetResult BuildPartsSheet(SpriteProject project, string baseName)
    {
        var cels = PartCels(project);
        return cels.Count == 0 ? EmptySheet() : PartsSheet(project, cels, PartNames(project, cels), whole: true, tagFile: false, stage: null);
    }

    /// <summary>결과 파일마다의 부위 아틀라스(부위·태그·스킨마다 나누기 설정대로). 내용 있는 부위가 없으면 빈 시트 한 장.</summary>
    public static IReadOnlyList<SheetFile> BuildPartsSheets(SpriteProject project, string baseName) => BuildPartsSheets(project, baseName, null);

    private static IReadOnlyList<SheetFile> BuildPartsSheets(SpriteProject project, string baseName, WorkStage? stage)
    {
        var cels = PartCels(project);
        if (cels.Count == 0) return [new SheetFile(baseName, EmptySheet())];
        var names = PartNames(project, cels);
        var keys = cels.Select(c => FileKey(project, c)).ToList();
        var files = new List<SheetFile>();
        foreach (var file in PartsOutputs(project, baseName, keys))
        {
            var idx = Enumerable.Range(0, cels.Count).Where(i => keys[i] == file.Key).ToList();
            var tag = file.Key.Tag >= 0 && file.Key.Tag < project.Tags.Count ? project.Tags[file.Key.Tag] : null;
            var sheet = PartsSheet(project, idx.Select(i => cels[i]).ToList(), idx.Select(i => names[i]).ToList(), whole: file.Key.Part is null, tagFile: file.Key.Tag >= 0, stage);
            files.Add(new SheetFile(file.Name, sheet, tag, file.Key.Part, file.Key.SkinName));
        }
        return files;
    }

    private static SheetResult EmptySheet() => new(new PixelImage(1, 1), [], new Size(1, 1));

    /// <param name="whole">부위마다 나누지 않은 파일이면 "원래 위치 유지"를 쓴다(부위마다 나눈 파일에서는 원래 칸 위치가 뜻이 없음).</param>
    /// <param name="tagFile">태그마다 나눈 파일이면 프레임 자리 값은 파일 안 프레임 순번.</param>
    private static SheetResult PartsSheet(SpriteProject project, List<PartCel> cels, IReadOnlyList<string> names, bool whole, bool tagFile, WorkStage? stage)
    {
        var o = project.Export.Parts;
        var (keys, rowBreak) = o.Order.For(hasParts: true);
        bool tagKey = keys.Contains(GridKey.Tag);
        var partIndex = PartLayers(project).Select((p, i) => (p, i)).ToDictionary(t => t.p, t => t.i);
        var untagged = Enumerable.Range(0, project.Frames.Count).Where(i => project.TagOfFrame(i) is null).ToList();
        var fileFrames = cels.Select(c => c.Frame).Distinct().Order().ToList();

        // 그림(구워 넣는 칸이면 물려받은 첫 스킨 그림) + 부위 효과·잘라내기
        var images = new (PixelImage Image, Pivot Pivot, SpriteBorder Border)[cels.Count];
        for (int i = 0; i < cels.Count; i++)
        {
            var (part, skin, fi) = cels[i];
            var cel = skin is null ? project.GetCelRaw(part, project.Frames[fi], null) : project.GetSkinCel(part, project.Frames[fi], skin);
            var (image, pivot, border) = PartSprite(project, part, skin, cel!, fi);
            images[i] = TrimCel(image, pivot, border, o.Trim);
            stage?.Step(PartUnits(cel!));
        }

        var values = cels.Select(c =>
        {
            var tag = project.TagOfFrame(c.Frame);
            int frameValue = tagKey ? (tag is null ? untagged.IndexOf(c.Frame) : c.Frame - tag.From)
                : tagFile ? fileFrames.IndexOf(c.Frame) : c.Frame;
            return keys.Select(k => k switch
            {
                GridKey.Part => partIndex[c.Part],
                GridKey.Skin => c.Skin is null ? 0 : IndexOf(c.Part.Parent!.AllSkins, c.Skin),
                GridKey.Tag => IndexOfTag(project, tag),
                _ => frameValue,
            }).ToArray();
        }).ToList();
        var sizes = images.Select(t => t.Image.Size).ToList();
        IReadOnlyList<Rectangle?>? fixedRects = whole && project.Export.KeepImportedRects
            ? cels.Select((c, i) => c.Frame == 0 && c.Skin is null && c.Part.PartSourceRect is { } r && r.Size == sizes[i] ? r : (Rectangle?)null).ToList()
            : null;
        var minSize = fixedRects is not null && fixedRects.Any(r => r is not null) ? project.Size : Size.Empty;
        var (rects, size) = Arrange(o, keys, rowBreak, values, sizes, sizes, fixedRects, minSize);

        var sheet = new PixelImage(size.Width, size.Height);
        var entries = new List<SheetEntry>(cels.Count);
        foreach (var i in SortedOrder(values))
        {
            var (part, skin, fi) = cels[i];
            sheet.Blit(images[i].Image, rects[i].X, rects[i].Y);
            entries.Add(new SheetEntry(fi, names[i], rects[i], images[i].Pivot, null, images[i].Border, part, skin));
        }
        var cell = new Size(sizes.Max(s => s.Width), sizes.Max(s => s.Height));
        return new SheetResult(sheet, entries, cell);
    }

    private static int IndexOf(IReadOnlyList<Skin> skins, Skin skin)
    {
        for (int i = 0; i < skins.Count; i++)
            if (ReferenceEquals(skins[i], skin)) return i;
        return 0;
    }

    // ───────────── 문서 ─────────────

    /// <summary>부위 아틀라스 문서(항상 Multiple, 칸마다 피벗·테두리, 애니메이션 없음).</summary>
    private static Document PartsDocument(SpriteProject project, SheetResult sheet)
    {
        var doc = new Document(sheet.Image);
        doc.Sprite.CopyFrom(project.Sprite);
        doc.Sprite.Mode = SpriteMode.Multiple;
        int spacing = project.Export.Parts.Spacing;
        doc.Grid = new GridSettings { CellW = sheet.CellSize.Width, CellH = sheet.CellSize.Height, PadX = spacing, PadY = spacing };
        foreach (var e in sheet.Entries)
            doc.Slices.Add(new Slice(e.Name, e.Rect, e.Pivot, e.Border));
        doc.IsDirty = false;
        return doc;
    }

    /// <summary>
    /// 내보낼 내용을 메모리 <see cref="Document"/>로 만든다(한 장짜리 프레임 시트, 지금 착장): 시트 이미지, 칸(이름·피벗), 태그를 방향대로 펼친
    /// 애니메이션(프레임별 ms), 스프라이트 설정. 미리보기·움직임 테스트에도 쓴다.
    /// </summary>
    /// <param name="allFrames">true면 Single 모드여도 모든 프레임 시트(움직임 테스트용).</param>
    public static Document BuildDocument(SpriteProject project, string baseName, bool allFrames = false)
        => FrameDocument(project, BuildSheet(project, baseName, allFrames), allFrames || project.Sprite.Mode == SpriteMode.Multiple);

    /// <summary>프레임 시트 문서. 애니메이션은 모든 프레임이 이 시트에 있는 태그만(한 이미지에 모두면 첫 항목 칸으로).</summary>
    private static Document FrameDocument(SpriteProject project, SheetResult sheet, bool multiple)
    {
        var doc = new Document(sheet.Image);
        doc.Sprite.CopyFrom(project.Sprite);
        int spacing = project.Export.Frames.Spacing;
        doc.Grid = new GridSettings { CellW = sheet.CellSize.Width, CellH = sheet.CellSize.Height, PadX = spacing, PadY = spacing };
        if (!multiple)
        {
            doc.Sprite.Mode = SpriteMode.Single;
            doc.Sprite.DefaultPivot = sheet.Entries[0].Pivot;
            doc.IsDirty = false;
            return doc;
        }

        doc.Sprite.Mode = SpriteMode.Multiple;
        foreach (var e in sheet.Entries)
            doc.Slices.Add(new Slice(e.Name, e.Rect, e.Pivot, e.Border));
        var inSheet = sheet.Entries.Select(e => e.FrameIndex).ToHashSet();
        var first = sheet.Entries.Count > 0 ? sheet.Entries.MinBy(e => e.Variant?.Suffix is null ? 0 : 1)!.Variant : null;
        foreach (var tag in project.Tags)
        {
            var order = tag.GetPlaybackOrder().ToList();
            if (!order.All(inSheet.Contains)) continue;
            var clip = new AnimationClip(tag.Name) { Loop = tag.Loop };
            foreach (var fi in order)
            {
                var entry = sheet.Entries.FirstOrDefault(e => e.FrameIndex == fi && e.Tag == tag.Name && e.Variant == first)
                            ?? sheet.Entries.FirstOrDefault(e => e.FrameIndex == fi && e.Variant == first)
                            ?? sheet.Entries.First(e => e.FrameIndex == fi);
                clip.Frames.Add(new AnimationFrame(entry.Name, project.Frames[fi].DurationMs));
            }
            if (clip.Frames.Count > 0)
                clip.Fps = Math.Clamp((int)Math.Round(1000.0 / clip.Frames.Average(f => f.Ms)), 1, 120);
            doc.Animations.Add(clip);
        }
        doc.IsDirty = false;
        return doc;
    }

    // ───────────── 내보내기 ─────────────

    /// <summary>
    /// 내보내기 계획: 켜진 결과 파일 경로, 새 기록(결과 파일 + 쓰거나 이미 있는 <c>.meta</c> — Sprite Library는 항상 <c>.meta</c>와 함께), 정리할 옛 결과
    /// (<see cref="SpriteProject.ExportedFiles"/>에 있고 새 기록에 없으며 실제로 있는 파일 + 그 파일에 딸린 <c>.meta</c>·사이드카).
    /// 기록이 없으면(예전 파일) 아무것도 정리하지 않는다. 결과 파일 이름이 겹치면 <see cref="InvalidOperationException"/>.
    /// </summary>
    public static ExportPlan Plan(SpriteProject project, string projectPath)
    {
        projectPath = Path.GetFullPath(projectPath);
        var (frames, parts) = GetOutputPaths(project, projectPath, assumePartContent: false);
        var libraries = GetLibraryPaths(project, projectPath, assumePartContent: false);
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in frames.Concat(parts).Concat(libraries))
            if (!images.Add(path))
                throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.FormatExport_DuplicateFile(name: Path.GetFileName(path)));

        var record = new List<string>();
        foreach (var image in frames)
        {
            record.Add(image);
            var meta = UnityMeta.GetMetaPath(image);
            if (ShouldWriteMeta(project, image) || File.Exists(meta)) record.Add(meta);
        }
        foreach (var image in parts)
        {
            record.Add(image);
            var meta = UnityMeta.GetMetaPath(image);
            if (ShouldWritePartsMeta(project, image) || File.Exists(meta)) record.Add(meta);
        }
        foreach (var library in libraries)
        {
            record.Add(library);
            record.Add(UnityMeta.GetMetaPath(library));
        }
        var keep = new HashSet<string>(record, StringComparer.OrdinalIgnoreCase) { projectPath };
        var stale = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (!keep.Contains(path) && File.Exists(path) && seen.Add(path)) stale.Add(path);
        }
        foreach (var old in project.ExportedFiles ?? [])
        {
            var full = Path.GetFullPath(old);
            Add(full);
            if (!keep.Contains(full))
                foreach (var companion in DocumentIO.GetCompanionPaths(full)) Add(companion);
        }
        return new ExportPlan(projectPath, frames, parts, libraries, record, stale);
    }

    /// <summary>
    /// 계획대로 이미지(+ 메타)와 Sprite Library(+ 메타)를 쓴다. 기존 메타의 guid·spriteID·internalID는 파일마다 칸 이름 기준으로 유지되고,
    /// Sprite Library는 방금 쓴 부위 아틀라스 메타의 guid·internalID로 스프라이트를 가리킨다. 기록은 바꾸지 않는다.
    /// </summary>
    /// <param name="progress">진행(합성 → 이미지 쓰기). 시작할 때 <see cref="EstimateWork"/>만큼 단계를 잡는다.</param>
    public static ProjectExportResult Write(SpriteProject project, ExportPlan plan, WorkProgress? progress = null)
    {
        using var stage = progress?.Begin(EstimateWork(project, plan));
        return Write(project, plan, stage);
    }

    internal static ProjectExportResult Write(SpriteProject project, ExportPlan plan, WorkStage? stage)
    {
        var export = project.Export;
        var frames = new List<ExportResult>();
        if (plan.FramePaths.Count > 0)
        {
            bool multiple = project.Sprite.Mode == SpriteMode.Multiple;
            var sheets = BuildFrameSheets(project, Path.GetFileNameWithoutExtension(GetExportImagePath(export, plan.ProjectPath)), stage, NewCache());
            for (int i = 0; i < sheets.Count; i++)
                frames.Add(WriteDocument(plan.FramePaths[i], FrameDocument(project, sheets[i].Sheet, multiple), project, ShouldWriteMeta(project, plan.FramePaths[i]), stage));
        }
        var parts = new List<ExportResult>();
        var sprites = new Dictionary<(Layer Part, int Frame, Skin? Skin), SpriteLibraryAssetWriter.SpriteRef>();
        if (plan.PartPaths.Count > 0)
        {
            var sheets = BuildPartsSheets(project, Path.GetFileNameWithoutExtension(GetPartsImagePath(export, plan.ProjectPath)), stage);
            for (int i = 0; i < sheets.Count; i++)
            {
                var path = plan.PartPaths[i];
                var result = WriteDocument(path, PartsDocument(project, sheets[i].Sheet), project, ShouldWritePartsMeta(project, path), stage);
                parts.Add(result);
                if (plan.LibraryPaths.Count == 0 || !result.MetaWritten || UnityMeta.TryRead(UnityMeta.GetMetaPath(path)) is not { Guid: { } guid } meta) continue;
                var ids = meta.Sprites.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().InternalId);
                foreach (var e in sheets[i].Sheet.Entries)
                    if (e.Part is not null && ids.TryGetValue(e.Name, out var id))
                        sprites[(e.Part, e.FrameIndex, e.Skin)] = new SpriteLibraryAssetWriter.SpriteRef(id, guid);
            }
        }
        var libraries = new List<ExportResult>();
        if (plan.LibraryPaths.Count > 0)
        {
            var baseName = Path.GetFileNameWithoutExtension(GetPartsImagePath(export, plan.ProjectPath));
            var groups = PartCels(project).Select(c => c.Part.Parent!).Distinct();
            var outputs = LibraryOutputs(project, baseName, groups);
            for (int i = 0; i < outputs.Count && i < plan.LibraryPaths.Count; i++)
            {
                var library = BuildLibrary(project, outputs[i].Group, outputs[i].Skin, sprites);
                SpriteLibraryAssetWriter.Write(plan.LibraryPaths[i], library);
                libraries.Add(new ExportResult(plan.LibraryPaths[i], true, library.Sum(c => c.Labels.Count)));
            }
        }
        return new ProjectExportResult(frames, parts, libraries, plan.Stale);
    }

    /// <summary>
    /// 그룹 하나 × 스킨 하나의 Sprite Library 내용: 범주 = 그룹의 부위(위 부위부터, 어느 스킨에든 내용이 있는 부위만),
    /// 라벨 = 그 부위에 어느 스킨이든 내용이 있는 프레임의 칸 열쇠(<see cref="FrameKey"/>). 스프라이트 = 이 스킨이 직접 그린(또는 구운) 스프라이트,
    /// 없으면 첫 스킨 스프라이트(물려받기), 그것도 없으면 빈 항목(그 프레임에 이 스킨은 아무것도 안 보임). 같은 그룹의 모든 스킨 라이브러리는
    /// 범주·라벨이 같아서 Sprite Library 에셋만 바꿔 끼우면 Sprite Resolver 키프레임을 그대로 쓴 채 스킨이 바뀐다.
    /// </summary>
    private static List<SpriteLibraryAssetWriter.Category> BuildLibrary(SpriteProject project, Layer group, Skin skin,
        IReadOnlyDictionary<(Layer Part, int Frame, Skin? Skin), SpriteLibraryAssetWriter.SpriteRef> sprites)
    {
        bool byTag = project.Export.Parts.Naming == SpriteNaming.ByTag;
        Skin? key = ReferenceEquals(skin, group.BaseSkin) ? null : skin;
        var skins = group.AllSkins.Skip(1).Prepend<Skin?>(null).ToList();
        var categories = new List<SpriteLibraryAssetWriter.Category>();
        var usedCategories = new HashSet<string>(StringComparer.Ordinal);
        var children = project.ChildrenOf(group);
        for (int c = children.Count - 1; c >= 0; c--)
        {
            var part = children[c];
            var frames = Enumerable.Range(0, project.Frames.Count).Where(fi => skins.Any(s => sprites.ContainsKey((part, fi, s)))).ToList();
            if (frames.Count == 0) continue;
            var usedLabels = new HashSet<string>(StringComparer.Ordinal);
            var labels = frames.Select(fi => new SpriteLibraryAssetWriter.Label(
                Unique(usedLabels, FrameKey(project, fi, byTag)),
                key is not null && sprites.TryGetValue((part, fi, key), out var own) ? own
                : sprites.TryGetValue((part, fi, null), out var inherited) ? inherited : null)).ToList();
            categories.Add(new SpriteLibraryAssetWriter.Category(Unique(usedCategories, CleanSpriteName(part.Name)), labels));
        }
        return categories;
    }

    /// <summary>
    /// 내보내기 진행의 전체 단위(<see cref="Write"/>가 잡는 단계): 합성할 프레임·부위 셀마다 합성 몫 + 결과 이미지에 들어갈 만큼의 압축 몫.
    /// 잘라내기·여백으로 실제 시트 크기와는 다르다 — 남는 몫은 단계 끝에 채운다.
    /// </summary>
    public static long EstimateWork(SpriteProject project, ExportPlan plan)
    {
        long px = (long)project.Width * project.Height, units = 0;
        if (plan.FramePaths.Count > 0)
        {
            bool single = project.Sprite.Mode == SpriteMode.Single;
            foreach (var o in FrameOutputs(project, "sprite"))
                units += (single ? 1 : (long)(o.Frames?.Count ?? project.Frames.Count) * o.Variants.Count) * (CompositeUnits(project) + px);
        }
        if (plan.PartPaths.Count > 0)
            foreach (var (part, _, _) in PartCels(project))
            {
                var size = project.CelSize(part);
                long cel = (long)size.Width * size.Height;
                units += cel * CompositeWeight + cel;
            }
        return Math.Max(1, units);
    }

    /// <summary>부위 셀 하나(효과 포함)를 그리는 몫.</summary>
    private static long PartUnits(PixelImage cel) => (long)cel.Width * cel.Height * CompositeWeight;

    /// <summary>
    /// 켜진 것을 지금 경로 기준으로 내보낸다(<see cref="BeginExport"/> → <see cref="Write"/>). 기록이 바뀌면 프로젝트를 수정됨으로 둔다
    /// (다음 저장 때 <c>.gpixel</c>에 들어감). 옛 결과는 <see cref="ProjectExportResult.Stale"/>로 돌려준다(지우지 않음).
    /// </summary>
    public static ProjectExportResult Export(SpriteProject project) => Write(project, BeginExport(project));

    /// <summary>
    /// 내보내기 준비(UI 스레드): 지금 경로 기준 계획을 세우고 기록을 갱신한다(바뀌면 수정됨). 그다음 <see cref="Write"/>는 프로젝트를 읽기만 하므로
    /// 아무도 프로젝트를 고치지 않는 동안 작업 스레드에서 돌려도 된다.
    /// </summary>
    public static ExportPlan BeginExport(SpriteProject project)
    {
        var plan = Plan(project, RequirePath(project, null));
        if (!SameRecord(project.ExportedFiles, plan.Record)) project.IsDirty = true;
        project.ExportedFiles = plan.Record;
        return plan;
    }

    private static bool SameRecord(IReadOnlyList<string>? a, IReadOnlyList<string> b)
        => a is not null && a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    private static ExportResult WriteDocument(string path, Document doc, SpriteProject project, bool writeMeta, WorkStage? stage)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (var write = stage?.Sub((long)doc.Image.Width * doc.Image.Height))
            DocumentIO.Save(doc, path, writeMeta, project.Export.ImageFormat, project.Export.JpegQuality, write);
        return new ExportResult(path, writeMeta, Math.Max(1, doc.Slices.Count));
    }
}
