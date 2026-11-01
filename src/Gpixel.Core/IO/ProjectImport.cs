using System.Drawing;
using Gpixel.Core.Model;
using Gpixel.Core.Slicing;

namespace Gpixel.Core.IO;

/// <summary>프로젝트 열기 결과.</summary>
/// <param name="Project">열린 프로젝트(수정됨 아님). <see cref="SpriteProject.Path"/>는 <c>.gpixel</c> 경로(가져온 경우 저장할 제안 경로).</param>
/// <param name="Imported">PNG를 가져와 새로 만든 프로젝트면 true(아직 <c>.gpixel</c> 파일 없음).</param>
/// <param name="SourcePngPath">PNG를 골라 열었으면 그 경로.</param>
/// <param name="Warning">사용자에게 알릴 말(손상된 사이드카·셀 등). 없으면 null.</param>
public sealed record ProjectOpenResult(SpriteProject Project, bool Imported, string? SourcePngPath, string? Warning);

/// <summary>PNG(스프라이트 시트) 가져오기 방식.</summary>
public enum ImportMode
{
    /// <summary>칸마다 애니메이션 프레임 하나.</summary>
    Frames,
    /// <summary>칸마다 부위 레이어 하나(그룹 하나에 모음, 칸 크기 그대로).</summary>
    Parts,
}

/// <summary>
/// PNG 열기 전 분석 결과. <see cref="NeedsModeChoice"/>면 사용자에게 부위/애니메이션 프레임 중 고르게 한 뒤
/// <see cref="ProjectImport.Import"/>에 넘긴다.
/// </summary>
/// <param name="SourcePath">고른 파일(.png 또는 .gpixel).</param>
/// <param name="ExistingProjectPath">열게 될 기존 <c>.gpixel</c>(있으면 가져오기 없이 그것을 엶).</param>
/// <param name="Document">가져올 PNG 문서(기존 프로젝트를 열면 null).</param>
/// <param name="SliceCount">유효한 칸 수(Multiple일 때).</param>
/// <param name="SlicesSameSize">칸이 모두 같은 크기인지.</param>
/// <param name="SuggestedMode">추천 방식: 칸 크기가 서로 다르면 부위, 아니면 애니메이션 프레임.</param>
/// <param name="Warning">읽으며 생긴 알림(손상된 사이드카 등).</param>
public sealed record ImportAnalysis(
    string SourcePath, string? ExistingProjectPath, Document? Document, int SliceCount, bool SlicesSameSize,
    ImportMode SuggestedMode, string? Warning)
{
    /// <summary>칸이 여러 개이고 모두 같은 크기라 방식을 물어야 하는지.</summary>
    public bool NeedsModeChoice => ExistingProjectPath is null && SliceCount > 1 && SlicesSameSize;
}

/// <summary>
/// PNG/<c>.gpixel</c> 열기와 PNG(스프라이트 시트) → 프로젝트 가져오기.
/// <para>사용 순서: <see cref="Analyze"/> → (<see cref="ImportAnalysis.NeedsModeChoice"/>면 사용자에게 묻기) →
/// <see cref="Import"/>. 한 번에 하려면 <see cref="Open"/>(방식을 안 주면 추천 방식).</para>
/// <para>애니메이션으로 가져오기:</para>
/// <list type="bullet">
/// <item>Multiple이고 칸이 있으면 칸마다 프레임 1개(프레임 이름 = 칸 이름). 캔버스는 모든 칸의 피벗이 같은 캔버스 지점에
/// 오도록 정렬했을 때 모두 들어가는 가장 작은 크기(피벗이 같으면 = 가장 큰 칸 크기). 반올림으로 피벗이 어긋나는
/// 프레임만 프레임별 피벗을 갖는다.</item>
/// <item>칸이 없으면(Single 등) 이미지 전체가 프레임 1개.</item>
/// <item>사이드카 애니메이션 → 태그. 프레임이 이미 연속(정방향·역방향·왕복 a,b,c,b 꼴)이면 그 범위에 태그.
/// 아니면 쓰인 칸이 서로 다르고 다른 태그에 속하지 않을 때 그 프레임들을 첫 프레임 자리로 모아 순서대로 재배치,
/// 그것도 안 되면(같은 칸 반복·다른 태그와 겹침) 프레임을 복제해 맨 뒤에 붙인다. 프레임 길이는 처음 그 프레임을
/// 쓰는 애니메이션의 ms.</item>
/// <item>내보내기 설정: 같은 폴더·같은 이름 PNG, 칸이 격자 모양이면 같은 열 수·간격.</item>
/// </list>
/// <para>부위로 가져오기: 캔버스 = 시트 크기, 파일 이름의 그룹 하나에 칸마다 부위 1개(첫 칸 = 맨 위 부위, 이름·크기·피벗·테두리 그대로,
/// 위치 = 칸 위치, 원래 칸 기억), 프레임 1개. 칸이 없으면 이미지 전체가 부위 1개. 사이드카 애니메이션은 쓰지 않는다.
/// 내보내기: 부위 아틀라스 = 시트 이름(기존 .meta guid 재사용, "원래 위치 유지"), 프레임 시트 = <c>{시트 이름}_frames</c>.</para>
/// </summary>
public static class ProjectImport
{
    /// <summary>PNG에 대응하는 프로젝트 파일 경로(<c>hero.png</c> → <c>hero.gpixel</c>).</summary>
    public static string GetProjectPathForPng(string pngPath) => Path.ChangeExtension(Path.GetFullPath(pngPath), SpriteProject.FileExtension);

    /// <summary>
    /// 이미지를 만든 프로젝트: 같은 이름의 <c>.gpixel</c>, 없으면 같은 폴더에서 이 이미지를 내보내기로 등록한 <c>.gpixel</c>. 없으면 null.
    /// </summary>
    public static string? FindProjectForImage(string imagePath)
    {
        var full = Path.GetFullPath(imagePath);
        var sameName = GetProjectPathForPng(full);
        if (File.Exists(sameName)) return sameName;
        var dir = Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir)) return null;
        try
        {
            foreach (var project in Directory.EnumerateFiles(dir, "*" + SpriteProject.FileExtension))
                if (ProjectIO.GetRegisteredExportPaths(project).Contains(full, StringComparer.OrdinalIgnoreCase)) return project;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>
    /// 파일을 분석한다: <c>.gpixel</c>이거나 이 이미지를 만든 <c>.gpixel</c>(<see cref="FindProjectForImage"/>)이 있으면 그것을 열 것이라고 알려 주고,
    /// 아니면 PNG(+메타/사이드카)를 읽어 칸 정보를 알려 준다. 읽을 수 없으면 예외.
    /// </summary>
    /// <param name="defaults">사이드카·메타가 없는 PNG를 가져올 때 쓸 Unity 설정(마지막 PPU 등).</param>
    public static ImportAnalysis Analyze(string path, SpriteSettings? defaults = null)
    {
        var full = Path.GetFullPath(path);
        if (full.EndsWith(SpriteProject.FileExtension, StringComparison.OrdinalIgnoreCase))
            return new ImportAnalysis(full, full, null, 0, false, ImportMode.Frames, null);
        if (FindProjectForImage(full) is { } projectPath)
            return new ImportAnalysis(full, projectPath, null, 0, false, ImportMode.Frames, null);

        var load = DocumentIO.Load(full, defaults);
        var rects = ValidSlices(load.Document).Select(s => s.Rect).ToList();
        bool same = rects.Select(r => r.Size).Distinct().Count() <= 1;
        return new ImportAnalysis(full, null, load.Document, rects.Count, same, SuggestMode(load.Document), load.Warning);
    }

    /// <summary>
    /// 분석 결과로 프로젝트를 연다(기존 <c>.gpixel</c>이면 그것을, 아니면 <paramref name="mode"/>(null = 추천)로 가져옴).
    /// 가져온 프로젝트의 <see cref="SpriteProject.Path"/>는 PNG 옆 <c>.gpixel</c> 제안 경로이고 수정됨이 아니다.
    /// </summary>
    /// <param name="progress">진행(<c>.gpixel</c>을 열 때 셀 풀기).</param>
    public static ProjectOpenResult Import(ImportAnalysis analysis, ImportMode? mode = null, WorkProgress? progress = null)
    {
        var warnings = new List<string>();
        if (analysis.Warning is not null) warnings.Add(analysis.Warning);
        bool pickedPng = !analysis.SourcePath.EndsWith(SpriteProject.FileExtension, StringComparison.OrdinalIgnoreCase);
        if (analysis.ExistingProjectPath is { } existing)
        {
            var project = ProjectIO.Load(existing, warnings, progress);
            return new ProjectOpenResult(project, false, pickedPng ? analysis.SourcePath : null, Join(warnings));
        }
        var doc = analysis.Document ?? throw new ArgumentException(Gpixel.Core.Localization.DomainStrings.Import_NoDocument, nameof(analysis));
        var imported = FromDocument(doc, mode ?? analysis.SuggestedMode);
        imported.Path = GetProjectPathForPng(analysis.SourcePath);
        imported.IsDirty = false;
        return new ProjectOpenResult(imported, true, analysis.SourcePath, Join(warnings));
    }

    /// <summary>
    /// <c>.gpixel</c>이나 PNG를 한 번에 연다(<see cref="Analyze"/> + <see cref="Import"/>). PNG와 같은 이름의 <c>.gpixel</c>이
    /// 있으면 그것을 연다. 읽을 수 없으면 예외(<see cref="InvalidDataException"/>, <see cref="IOException"/> 등).
    /// </summary>
    /// <param name="mode">가져올 방식. null이면 추천 방식(칸 크기가 다르면 부위).</param>
    public static ProjectOpenResult Open(string path, SpriteSettings? defaults = null, ImportMode? mode = null)
        => Import(Analyze(path, defaults), mode);

    /// <summary>칸 크기가 서로 다르면 부위, 아니면 애니메이션 프레임.</summary>
    public static ImportMode SuggestMode(Document doc)
        => ValidSlices(doc).Select(s => s.Rect.Size).Distinct().Count() > 1 ? ImportMode.Parts : ImportMode.Frames;

    /// <summary>Multiple 문서의 이미지 안으로 자른 유효한 칸들.</summary>
    private static List<(Slice Slice, Rectangle Rect)> ValidSlices(Document doc)
        => doc.Sprite.Mode == SpriteMode.Multiple
            ? doc.Slices
                .Select(s => (Slice: s, Rect: Rectangle.Intersect(s.Rect, doc.Image.Bounds)))
                .Where(s => s.Rect.Width > 0 && s.Rect.Height > 0)
                .ToList()
            : [];

    private static string? Join(List<string> warnings) => warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);

    /// <summary>문서(시트 이미지 + 칸 + 애니메이션)를 프로젝트로 바꾼다. 규칙은 <see cref="ProjectImport"/> 참고.</summary>
    public static SpriteProject FromDocument(Document doc, ImportMode mode = ImportMode.Frames)
    {
        var slices = ValidSlices(doc);
        if (mode == ImportMode.Parts) return PartsFromDocument(doc, slices);

        if (slices.Count == 0)
        {
            var single = SpriteProject.FromImage(doc.Image);
            single.Sprite.CopyFrom(doc.Sprite);
            single.Sprite.Mode = doc.Sprite.Mode;
            return single;
        }

        // 피벗 정렬: 칸 i의 피벗 픽셀 p_i를 공통 점 P에 두는 오프셋 o_i = round(P - p_i).
        var local = slices.Select(s => s.Slice.Pivot.ToLocalPixel(s.Rect.Width, s.Rect.Height)).ToList();
        float px = local.Max(l => l.X), py = local.Max(l => l.Y);
        var offsets = local.Select(l => ((int)MathF.Floor(px - l.X + 0.5f), (int)MathF.Floor(py - l.Y + 0.5f))).ToList();
        int width = Math.Max(1, slices.Select((s, i) => offsets[i].Item1 + s.Rect.Width).Max());
        int height = Math.Max(1, slices.Select((s, i) => offsets[i].Item2 + s.Rect.Height).Max());

        var project = SpriteProject.CreateBlank(width, height);
        var layer = new Layer(project.NextLayerName());
        project.AddLayerRaw(layer);
        project.Sprite.CopyFrom(doc.Sprite);
        project.Sprite.Mode = SpriteMode.Multiple;
        var defaultPivot = new Pivot(px / width, 1f - py / height);
        project.Sprite.DefaultPivot = defaultPivot;

        // 칸 → 프레임(작업 목록은 프레임 객체 순서)
        var work = new List<Frame>();
        var celOf = new Dictionary<Frame, PixelImage>();
        var home = new Dictionary<string, Frame>(StringComparer.Ordinal);
        for (int i = 0; i < slices.Count; i++)
        {
            var (slice, rect) = slices[i];
            var (ox, oy) = offsets[i];
            var cel = new PixelImage(width, height);
            cel.Blit(doc.Image, rect, ox, oy);
            var actual = new Pivot((ox + local[i].X) / width, 1f - (oy + local[i].Y) / height);
            var frame = new Frame
            {
                Name = slice.Name,
                Pivot = MathF.Abs(actual.X - defaultPivot.X) > 1e-4f || MathF.Abs(actual.Y - defaultPivot.Y) > 1e-4f ? actual : null,
            };
            work.Add(frame);
            celOf[frame] = cel;
            home.TryAdd(slice.Name, frame);
        }

        var tags = BuildTags(doc.Animations, work, celOf, home);

        foreach (var frame in work)
        {
            project.AddFrameRaw(frame);
            if (!celOf[frame].IsRegionEmpty(celOf[frame].Bounds)) project.SetCelRaw(layer, frame, celOf[frame]);
        }
        foreach (var t in tags)
        {
            project.AddTagRaw(new AnimationTag(project.UniqueTagName(t.Name), work.IndexOf(t.First), work.IndexOf(t.Last))
            {
                Direction = t.Direction,
                Loop = t.Loop,
                Color = AnimationTag.DefaultColors[project.Tags.Count % AnimationTag.DefaultColors.Count],
            });
        }

        DetectLayout(project.Export, slices.Select(s => s.Rect).ToList());
        return project;
    }

    /// <summary>시트 크기 캔버스 + 파일 이름의 그룹 하나에 칸마다 부위 1개(칸이 없으면 이미지 전체 1개)인 프로젝트.</summary>
    private static SpriteProject PartsFromDocument(Document doc, List<(Slice Slice, Rectangle Rect)> slices)
    {
        var cells = slices.Count > 0
            ? slices.Select(s => (s.Slice.Name, s.Rect, s.Slice.Pivot, s.Slice.Border)).ToList()
            : [(doc.BaseName, doc.Image.Bounds, doc.Sprite.DefaultPivot, doc.Sprite.Border)];
        var project = SpriteProject.CreateBlank(doc.Image.Width, doc.Image.Height);
        var frame = new Frame();
        project.AddFrameRaw(frame);
        project.Sprite.CopyFrom(doc.Sprite);
        project.Sprite.Mode = SpriteMode.Multiple;

        // 부위 이름이 내보내기 칸 이름(=Unity guid·spriteID 기준)이므로 그룹보다 먼저 정한다.
        var parts = new List<Layer>(cells.Count);
        foreach (var (name, rect, pivot, border) in cells)
        {
            var part = new Layer(project.UniqueLayerName(string.IsNullOrWhiteSpace(name) ? "sprite" : name.Trim()))
            {
                Kind = LayerKind.Part,
                PartSize = rect.Size,
                PartOffset = rect.Location,
                PartPivot = pivot,
                PartBorder = border,
                PartSourceRect = rect,
            };
            parts.Add(part);
            project.AddLayerRaw(part);
            var cel = doc.Image.Crop(rect);
            if (!cel.IsRegionEmpty(cel.Bounds)) project.SetCelRaw(part, frame, cel);
        }
        var group = new Layer(project.UniqueLayerName(doc.BaseName)) { Kind = LayerKind.Group };
        foreach (var part in parts) part.Parent = group;
        project.ReplaceLayersRaw([.. Enumerable.Reverse(parts), group]);

        // 가져온 아틀라스 하나만 다시 쓴다. 프레임 시트는 같은 그림의 사본이라 기본으로 끄고, 켜면 겹치지 않는 이름을 쓴다.
        project.Export.ExportParts = true;
        // 부위 이름 = 가져온 칸 이름(Unity guid·spriteID 기준)이므로 태그를 붙여도 이름이 바뀌지 않게 프레임 번호 규칙.
        project.Export.Common.Naming = SpriteNaming.FrameNumber;
        // 가져온 칸 위치를 지키는 자동 배치(원래 위치 유지).
        project.Export.Common.Layout = SheetLayout.Packed;
        project.Export.PartsFileName = doc.BaseName;
        project.Export.ExportFrames = false;
        project.Export.FileName = doc.BaseName + "_frames";
        return project;
    }

    private sealed record WorkTag(string Name, Frame First, Frame Last, TagDirection Direction, bool Loop);

    private static List<WorkTag> BuildTags(IEnumerable<AnimationClip> animations, List<Frame> work,
        Dictionary<Frame, PixelImage> celOf, Dictionary<string, Frame> home)
    {
        var tags = new List<WorkTag>();
        var timed = new HashSet<Frame>();

        void SetDurations(IReadOnlyList<Frame> frames, IReadOnlyList<int> ms)
        {
            for (int i = 0; i < frames.Count; i++)
                if (timed.Add(frames[i])) frames[i].DurationMs = ms[i];
        }

        bool Covered(Frame f)
        {
            int i = work.IndexOf(f);
            return tags.Any(t => i >= work.IndexOf(t.First) && i <= work.IndexOf(t.Last));
        }

        foreach (var anim in animations)
        {
            var steps = anim.Frames.Where(f => home.ContainsKey(f.Slice)).ToList();
            if (steps.Count == 0 || string.IsNullOrWhiteSpace(anim.Name)) continue;
            var frames = steps.Select(f => home[f.Slice]).ToList();
            var ms = steps.Select(f => f.Ms).ToList();
            var idx = frames.Select(f => work.IndexOf(f)).ToList();
            int n = frames.Count;

            if (IsRun(idx, 0, n, +1))
            {
                tags.Add(new WorkTag(anim.Name, frames[0], frames[^1], TagDirection.Forward, anim.Loop));
                SetDurations(frames, ms);
                continue;
            }
            if (IsRun(idx, 0, n, -1))
            {
                tags.Add(new WorkTag(anim.Name, frames[^1], frames[0], TagDirection.Reverse, anim.Loop));
                SetDurations(frames, ms);
                continue;
            }
            if (n >= 4 && n % 2 == 0 && IsRun(idx, 0, n / 2 + 1, +1)
                && Enumerable.Range(1, n / 2 - 1).All(i => frames[n / 2 + i] == frames[n / 2 - i]))
            {
                int k = n / 2;
                tags.Add(new WorkTag(anim.Name, frames[0], frames[k], TagDirection.PingPong, anim.Loop));
                SetDurations(frames.Take(k + 1).ToList(), ms.Take(k + 1).ToList());
                continue;
            }
            if (frames.Distinct().Count() == n && !frames.Any(Covered))
            {
                // 다른 태그와 겹치지 않는 서로 다른 프레임들 → 첫 프레임 자리로 모아 재배치
                int first = idx.Min();
                int insertAt = work.Take(first).Count(f => !frames.Contains(f));
                work.RemoveAll(frames.Contains);
                work.InsertRange(insertAt, frames);
                tags.Add(new WorkTag(anim.Name, frames[0], frames[^1], TagDirection.Forward, anim.Loop));
                SetDurations(frames, ms);
                continue;
            }
            // 같은 칸 반복·겹침 → 복제 프레임을 맨 뒤에
            var copies = new List<Frame>();
            for (int i = 0; i < n; i++)
            {
                var copy = new Frame { DurationMs = ms[i], Pivot = frames[i].Pivot };
                celOf[copy] = celOf[frames[i]].Clone();
                timed.Add(copy);
                copies.Add(copy);
            }
            work.AddRange(copies);
            tags.Add(new WorkTag(anim.Name, copies[0], copies[^1], TagDirection.Forward, anim.Loop));
        }
        return tags;
    }

    /// <summary>idx[start..start+count)가 1씩(step) 이어지는지.</summary>
    private static bool IsRun(List<int> idx, int start, int count, int step)
    {
        for (int i = start + 1; i < start + count; i++)
            if (idx[i] != idx[i - 1] + step) return false;
        return true;
    }

    /// <summary>칸이 격자 모양(모든 행의 칸 수가 같음)이면 같은 열 수·간격으로 내보내도록 설정한다(한 줄이면 세로 1칸).</summary>
    private static void DetectLayout(ExportSettings export, List<Rectangle> rects)
    {
        var o = export.Common;
        var rows = SliceOrder.GroupRows(rects);
        int cols = rows[0].Count;
        if (rows.Count == 1)
        {
            o.Grid = SheetGrid.Rows;
            o.GridCount = 1;
        }
        else
        {
            o.Grid = SheetGrid.Columns;
            o.GridCount = rows.All(r => r.Count == cols) ? cols : rows.Max(r => r.Count);
        }
        if (cols >= 2)
        {
            int gap = rects[rows[0][1]].X - rects[rows[0][0]].Right;
            // 원래 시트와 같은 모양으로 다시 내보내도록 간격을 그대로(붙어 있었으면 0 — 새 프로젝트 기본 1px을 쓰지 않음).
            o.Spacing = Math.Max(0, gap);
        }
        else o.Spacing = 0;
    }
}
