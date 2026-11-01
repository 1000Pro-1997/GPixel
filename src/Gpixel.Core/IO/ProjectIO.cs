using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.Core.Edit;
using Gpixel.Core.Scripting;

namespace Gpixel.Core.IO;

/// <summary>저장 결과: 프로젝트 파일 경로와 (켜져 있었으면) 내보내기 결과.</summary>
public sealed record ProjectSaveResult(string ProjectPath, ProjectExportResult? Export);

/// <summary>
/// <c>.gpixel</c> 프로젝트 파일 읽기/쓰기. 형식은 ZIP:
/// <c>project.json</c>(버전 4, 캔버스·레이어(종류·그룹·스킨·부위 크기·위치·피벗·테두리·효과)·프레임·태그·Unity·내보내기 설정,
/// 스크립트 효과가 있으면 스크립트 표 <c>scripts</c>: 원문 해시 → 원문) +
/// <c>cels/L{레이어 번호}_F{프레임 번호}.png</c>(셀, 부위 셀은 부위 크기 — 부위는 그룹의 첫 스킨 셀) +
/// <c>cels/L{레이어}_F{프레임}_S{스킨 번호}.png</c>(부위의 첫 스킨이 아닌 스킨 셀, 스킨 번호 = 그룹 스킨 목록 번호 1~).
/// 빈 셀은 생략, 번호는 0부터, 레이어 0 = 맨 아래.
/// <para>
/// 버전 2 이하 파일은 그대로 읽는다(그룹마다 스킨 "기본" 하나). 버전 1 아틀라스 파일(<c>kind: Atlas</c>, <c>atlas/S{스프라이트}_L{레이어}.png</c>)은
/// 그룹 "부위" + 스프라이트마다 부위 하나로 바꿔 읽는다(부위 아틀라스 이름은 예전 내보내기 이름이라 Unity guid·spriteID가 유지됨).
/// </para>
/// </summary>
public static partial class ProjectIO
{
    /// <summary>4 = 레이어 효과를 안정된 종류 ID와 설정 객체로 쓰는 판(<see cref="EffectCodec"/>). 3 이하의 짧은 효과 ID는 읽을 때 바꾼다.</summary>
    public const int CurrentVersion = 4;
    public const string ProjectEntryName = "project.json";

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 줄바꿈은 OS와 관계없이 LF — Windows·macOS에서 같은 문서를 저장하면 같은 바이트(버전 관리에서 줄바꿈만 바뀌지 않게)
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^cels/L(\d+)_F(\d+)(?:_S(\d+))?\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex CelEntry();

    [GeneratedRegex(@"^atlas/S(\d+)_L(\d+)\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex AtlasEntry();

    /// <param name="skinIndex">부위의 스킨 번호(0 = 첫 스킨·일반 레이어).</param>
    public static string CelEntryName(int layerIndex, int frameIndex, int skinIndex = 0)
        => skinIndex == 0 ? $"cels/L{layerIndex}_F{frameIndex}.png" : $"cels/L{layerIndex}_F{frameIndex}_S{skinIndex}.png";

    // ───────────── 저장 ─────────────

    /// <summary>
    /// 프로젝트를 저장한다(임시 파일에 쓴 뒤 바꿔치기). 성공하면 <see cref="SpriteProject.Path"/>를 갱신하고
    /// 수정됨 표시를 끈다. 실패하면 예외를 던지고 상태는 그대로다.
    /// </summary>
    public static void Save(SpriteProject project, string path)
    {
        var full = Path.GetFullPath(path);
        WriteFile(project, full, null);
        project.Path = full;
        project.IsDirty = false;
    }

    /// <summary>
    /// 프로젝트를 <paramref name="full"/>에 쓴다(임시 파일에 쓴 뒤 바꿔치기). 프로젝트는 읽기만 하고 경로·수정됨은 바꾸지 않는다 —
    /// 프로젝트를 아무도 고치지 않는 동안이면 작업 스레드에서 불러도 된다.
    /// </summary>
    /// <param name="stage">진행(단위: 셀 픽셀, 모두 <see cref="CelUnits"/>).</param>
    internal static void WriteFile(SpriteProject project, string full, WorkStage? stage)
    {
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = full + ".gpixel-tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                Save(project, fs, full, stage);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>ZIP 내용을 스트림에 쓴다(경로·수정 여부는 바꾸지 않음). 경로를 모르므로 내보내기 기록(<see cref="SpriteProject.ExportedFiles"/>)은 쓰지 않는다.</summary>
    public static void Save(SpriteProject project, Stream stream) => Save(project, stream, null, null);

    /// <param name="projectPath">저장할 프로젝트 전체 경로(내보내기 기록을 이 파일 기준 상대 경로로 씀). null이면 기록을 쓰지 않는다.</param>
    private static void Save(SpriteProject project, Stream stream, string? projectPath, WorkStage? stage)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteProjectJson(zip, ToDto(project, projectPath));

        // 셀 PNG는 여러 코어에서 묶음씩 만들고 ZIP에는 정한 차례대로 쓴다(파일 내용은 차례대로 만든 것과 같다). 묶음만큼만 메모리에 둔다.
        var cels = CelsToWrite(project);
        int batch = Math.Max(1, Environment.ProcessorCount * 2);
        var encoded = new byte[]?[Math.Min(batch, cels.Count)];
        for (int start = 0; start < cels.Count; start += batch)
        {
            int count = Math.Min(batch, cels.Count - start), first = start;
            Parallel.For(0, count, i => encoded[i] = EncodeCel(cels[first + i].Cel, stage));
            for (int i = 0; i < count; i++)
            {
                if (encoded[i] is { } png)
                {
                    // PNG는 이미 압축되어 있으므로 ZIP 압축은 하지 않는다.
                    var entry = zip.CreateEntry(cels[first + i].Name, CompressionLevel.NoCompression);
                    using var es = entry.Open();
                    es.Write(png);
                }
                encoded[i] = null;
            }
        }
    }

    /// <summary>저장할 셀(이름, 그림) — 레이어 → 프레임 → 스킨 차례. 빈 셀도 들어 있다(<see cref="EncodeCel"/>이 거른다).</summary>
    private static List<(string Name, PixelImage Cel)> CelsToWrite(SpriteProject project)
    {
        var list = new List<(string, PixelImage)>();
        for (int li = 0; li < project.Layers.Count; li++)
        {
            var layer = project.Layers[li];
            if (layer.IsGroup) continue;
            var skins = layer is { IsPart: true, Parent: { IsGroup: true } g } ? g.AllSkins : [];
            for (int fi = 0; fi < project.Frames.Count; fi++)
            {
                var frame = project.Frames[fi];
                if (project.GetCelRaw(layer, frame, null) is { } cel) list.Add((CelEntryName(li, fi), cel));
                for (int si = 1; si < skins.Count; si++)
                    if (project.GetCelRaw(layer, frame, skins[si]) is { } skinCel) list.Add((CelEntryName(li, fi, si), skinCel));
            }
        }
        return list;
    }

    /// <summary>저장 진행의 전체 단위: 저장할 셀의 픽셀 수.</summary>
    internal static long CelUnits(SpriteProject project) => CelsToWrite(project).Sum(c => (long)c.Cel.Width * c.Cel.Height);

    private static void WriteProjectJson(ZipArchive zip, ProjectDto dto)
    {
        var json = JsonSerializer.Serialize(dto, Options);
        var entry = zip.CreateEntry(ProjectEntryName, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
        w.Write(json);
    }

    /// <summary>셀 PNG 바이트. 빈 셀은 null(저장하지 않음).</summary>
    private static byte[]? EncodeCel(PixelImage cel, WorkStage? stage)
    {
        if (cel.IsRegionEmpty(cel.Bounds))
        {
            stage?.Step((long)cel.Width * cel.Height);
            return null;
        }
        using var ms = new MemoryStream();
        PngIO.Save(cel, ms, stage);
        return ms.ToArray();
    }

    /// <summary>
    /// 저장(Ctrl+S): 프로젝트를 저장하고, 내보내기가 켜져 있으면 프레임 시트·부위 아틀라스 이미지 + (필요하면) Unity 메타도 쓴다.
    /// 내보낼 결과 목록(기록)은 저장 전에 정해 <c>.gpixel</c>에 함께 저장한다. 지난 기록에 있고 이번에 만들지 않는 옛 결과는
    /// <see cref="ProjectExportResult.Stale"/>로 돌려준다(지우지 않음). 다른 경로로 저장하면 지난 기록은 원래 프로젝트 것이라 넘기지 않는다.
    /// 프로젝트 저장 뒤 내보내기가 실패하면 예외가 그대로 올라온다(프로젝트 파일은 이미 저장됨).
    /// 오래 걸리는 쓰기만 작업 스레드에서 하려면 <see cref="BeginSave"/> → <see cref="ProjectSaveJob.Run"/> → <see cref="ProjectSaveJob.Finish"/>로 나눠 부른다.
    /// </summary>
    /// <param name="path">저장 경로. null이면 <see cref="SpriteProject.Path"/>.</param>
    public static ProjectSaveResult SaveWithExport(SpriteProject project, string? path = null)
    {
        var job = BeginSave(project, path);
        job.Run();
        return job.Finish();
    }

    /// <summary>
    /// 저장 준비(UI 스레드): 내보낼 결과 목록을 정해 기록(<see cref="SpriteProject.ExportedFiles"/>)에 넣는다. 그다음 <see cref="ProjectSaveJob.Run"/>은
    /// 프로젝트를 읽기만 하므로 아무도 프로젝트를 고치지 않는 동안 작업 스레드에서 돌려도 되고, 상태 변경(경로·수정됨)과 실패 알림은
    /// <see cref="ProjectSaveJob.Finish"/>가 부르는 스레드에서 한다.
    /// </summary>
    /// <param name="path">저장 경로. null이면 <see cref="SpriteProject.Path"/>.</param>
    /// <param name="snapshot">
    /// true면 지금 상태의 사본(<see cref="SnapshotForSave"/>, 셀 픽셀 복사)을 만들어 그것을 쓴다 — <see cref="ProjectSaveJob.Run"/>이 도는 동안
    /// 원래 프로젝트를 계속 고쳐도 된다(자동 저장). 사본만큼 메모리를 잠시 더 쓴다(<see cref="CelBytes"/>).
    /// </param>
    public static ProjectSaveJob BeginSave(SpriteProject project, string? path = null, bool snapshot = false)
    {
        path ??= project.Path ?? throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.Document_NoSavePath);
        var full = Path.GetFullPath(path);
        var previous = project.ExportedFiles;
        if (project.Path is { } current && !string.Equals(Path.GetFullPath(current), full, StringComparison.OrdinalIgnoreCase))
            project.ExportedFiles = null;
        ExportPlan? plan = null;
        Exception? planError = null;
        if (project.Export.ExportOnSave)
        {
            try
            {
                plan = ProjectExport.Plan(project, full);
                project.ExportedFiles = plan.Record;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException)
            {
                planError = ex; // 프로젝트는 저장하고 내보내기 실패로 알린다
            }
        }
        var source = snapshot ? SnapshotForSave(project, full) : project;
        return new ProjectSaveJob(project, source, full, previous, plan, planError);
    }

    /// <summary>저장할 셀 픽셀의 바이트 수(<see cref="BeginSave"/>의 사본이 더 쓰는 메모리).</summary>
    public static long CelBytes(SpriteProject project) => CelUnits(project) * 4;

    /// <summary>
    /// 저장용 사본: 파일에 들어가는 것(구조·설정·효과·스크립트·내보내기 기록·셀 픽셀)을 옮긴 독립 프로젝트. 셀은 복사하므로 사본을 쓰는 동안
    /// 원래 프로젝트를 고쳐도 된다. 파일을 쓰고 다시 읽는 것과 같은 길(<see cref="ToDto"/> → <see cref="FromDto"/>)이라 사본을 저장한 결과가
    /// 원래 프로젝트를 저장한 결과와 같다.
    /// </summary>
    internal static SpriteProject SnapshotForSave(SpriteProject project, string fullPath)
    {
        var (copy, fileLayers) = FromDto(ToDto(project, fullPath), null);
        var cels = new List<(Layer Layer, Frame Frame, Skin? Skin, PixelImage Source)>();
        for (int li = 0; li < project.Layers.Count; li++)
        {
            var layer = project.Layers[li];
            if (layer.IsGroup) continue;
            var skins = layer is { IsPart: true, Parent: { IsGroup: true } g } ? g.AllSkins : [];
            var copySkins = fileLayers[li] is { IsPart: true, Parent: { IsGroup: true } cg } ? cg.AllSkins : [];
            for (int fi = 0; fi < project.Frames.Count; fi++)
            {
                var frame = project.Frames[fi];
                if (project.GetCelRaw(layer, frame, null) is { } cel) cels.Add((fileLayers[li], copy.Frames[fi], null, cel));
                for (int si = 1; si < skins.Count && si < copySkins.Count; si++)
                    if (project.GetCelRaw(layer, frame, skins[si]) is { } skinCel) cels.Add((fileLayers[li], copy.Frames[fi], copySkins[si], skinCel));
            }
        }
        // 셀 복사는 새 메모리를 처음 건드리는 비용이 커서 여러 코어로 나눈다(부르는 쪽은 UI 스레드)
        var copies = new PixelImage[cels.Count];
        Parallel.For(0, cels.Count, i => copies[i] = cels[i].Source.Clone());
        for (int i = 0; i < cels.Count; i++) copy.SetCelRaw(cels[i].Layer, cels[i].Frame, copies[i], cels[i].Skin);
        copy.NormalizeSkins();
        copy.ExportedFiles = project.ExportedFiles;
        copy.Path = fullPath;
        copy.IsDirty = false;
        return copy;
    }

    /// <summary>
    /// 프로젝트에 등록된 내보내기 결과 경로(파일 존재 여부와 무관): 지금 설정으로 만들 결과(프레임 시트 파일들, 부위가 있으면 부위 아틀라스 파일들,
    /// 켜져 있으면 Sprite Library 파일들) + 마지막으로 내보낸 기록의 이미지·<c>.spriteLib</c>. project.json만 읽어 셀은 풀지 않는다
    /// (부위·스킨은 내용과 무관하게 셈). 읽을 수 없으면 빈 목록.
    /// </summary>
    public static IReadOnlyList<string> GetRegisteredExportPaths(string projectPath)
    {
        ProjectDto dto;
        try
        {
            using var zip = ZipFile.OpenRead(projectPath);
            dto = ReadDto(zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return [];
        }
        return RegisteredPaths(dto, Path.GetFullPath(projectPath));
    }

    private static List<string> RegisteredPaths(ProjectDto dto, string fullProjectPath)
    {
        var paths = new List<string>();
        if (dto.Width >= 1 && dto.Height >= 1 && dto.Width <= PixelImage.MaxSize && dto.Height <= PixelImage.MaxSize)
        {
            var (project, _) = FromDto(dto, null);
            var (frames, parts) = ProjectExport.GetOutputPaths(project, fullProjectPath, assumePartContent: true);
            paths.AddRange(frames);
            paths.AddRange(parts);
            paths.AddRange(ProjectExport.GetLibraryPaths(project, fullProjectPath, assumePartContent: true));
        }
        var outputs = Enum.GetValues<ExportImageFormat>().Select(f => f.Extension()).Append(ProjectExport.SpriteLibraryExtension).ToArray();
        foreach (var file in ResolveRecord(dto.Exported, fullProjectPath) ?? [])
            if (outputs.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && !paths.Contains(file, StringComparer.OrdinalIgnoreCase))
                paths.Add(file);
        return paths;
    }

    /// <summary>
    /// 내보내기 기록 → 전체 경로. 기록한 프로젝트 파일 이름(<c>owner</c>)이 지금 이름과 다르면(밖에서 복사·이름 바꿈) 남의 기록이므로 null.
    /// </summary>
    private static List<string>? ResolveRecord(ExportRecordDto? record, string? fullProjectPath)
    {
        if (record?.Files is null || fullProjectPath is null
            || !string.Equals(record.Owner, Path.GetFileName(fullProjectPath), StringComparison.OrdinalIgnoreCase)) return null;
        var dir = Path.GetDirectoryName(fullProjectPath) ?? "";
        var files = new List<string>(record.Files.Count);
        foreach (var f in record.Files)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            try
            {
                files.Add(Path.GetFullPath(Path.Combine(dir, f)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return files;
    }

    private static ExportRecordDto? ToRecordDto(IReadOnlyList<string>? files, string? fullProjectPath)
    {
        if (files is null || fullProjectPath is null) return null;
        var dir = Path.GetDirectoryName(fullProjectPath) ?? "";
        return new ExportRecordDto
        {
            Owner = Path.GetFileName(fullProjectPath),
            Files = files.Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).ToList(),
        };
    }

    /// <summary>
    /// Returns the existing, default-named exports (frame sheet <c>{project}.png</c> and its per-tag files, parts atlas <c>{project}_parts.png</c>
    /// and its per-part files — from the current settings and the last export record) and their existing companions.
    /// A custom folder or filename is never treated as owned by the project browser.
    /// Read only project.json so browsing a large project does not decode its cels.
    /// </summary>
    public static string[] GetOwnedDefaultExportPaths(string projectPath)
    {
        var owned = new List<string>();
        foreach (var png in GetOwnedDefaultPngs(projectPath))
        {
            owned.Add(png);
            owned.AddRange(DocumentIO.GetCompanionPaths(png).Where(File.Exists));
        }
        return owned.ToArray();
    }

    /// <summary>
    /// Existing default-named export images owned by the project (frame sheets first, then parts atlases, then other recorded files):
    /// in the project folder, named <c>{project}.*</c> or <c>{project}_*</c>, never a custom parts atlas name.
    /// </summary>
    private static List<string> GetOwnedDefaultPngs(string projectPath)
    {
        ProjectDto dto;
        using (var zip = ZipFile.OpenRead(projectPath))
            dto = ReadDto(zip);
        var export = dto.Export;
        if (export is not { OnSave: true } || !string.IsNullOrWhiteSpace(export.Folder)
            || !string.IsNullOrWhiteSpace(export.FileName)) return [];

        var full = Path.GetFullPath(projectPath);
        var dir = Path.GetDirectoryName(full) ?? "";
        var stem = Path.GetFileNameWithoutExtension(full);
        var customParts = string.IsNullOrWhiteSpace(export.PartsFileName) ? null : export.PartsFileName.Trim();
        return RegisteredPaths(dto, full)
            .Where(p => IsDefaultNamed(p, dir, stem) && !(customParts is not null && HasStem(Path.GetFileName(p), customParts)) && File.Exists(p))
            .ToList();
    }

    /// <summary>프로젝트 폴더에 있고 이름이 <c>{stem}.…</c> 또는 <c>{stem}_…</c>인지(기본 이름 내보내기·그 .meta).</summary>
    private static bool IsDefaultNamed(string path, string dir, string stem)
        => string.Equals(Path.GetDirectoryName(path), dir, StringComparison.OrdinalIgnoreCase) && HasStem(Path.GetFileName(path), stem);

    /// <summary>이름이 <c>{stem}.…</c>, <c>{stem}_…</c>, <c>{stem}@…</c>(착장·스킨 파일)인지.</summary>
    private static bool HasStem(string fileName, string stem)
        => fileName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith(stem + ProjectExport.SkinSeparator, StringComparison.OrdinalIgnoreCase);

    /// <summary>기본 이름 파일의 프로젝트 이름 바꾼 뒤 경로(<c>hero_walk.png</c> → <c>knight_walk.png</c>).</summary>
    private static string RenamedDefault(string path, string oldStem, string newDir, string newStem)
        => Path.Combine(newDir, newStem + Path.GetFileName(path)[oldStem.Length..]);

    /// <summary>
    /// 프로젝트 이름을 바꾼 뒤의 내보내기 기록: 프로젝트 폴더의 기본 이름 파일 중 함께 옮겨진 것(옛 자리에 없고 새 이름에 있음)을 새 경로로.
    /// 열린 프로젝트의 메모리 기록에도 쓴다.
    /// </summary>
    public static IReadOnlyList<string>? RemapExportRecord(IReadOnlyList<string>? files, string oldProjectPath, string newProjectPath)
    {
        if (files is null) return null;
        var oldFull = Path.GetFullPath(oldProjectPath);
        var newFull = Path.GetFullPath(newProjectPath);
        var oldDir = Path.GetDirectoryName(oldFull) ?? "";
        var oldStem = Path.GetFileNameWithoutExtension(oldFull);
        var newDir = Path.GetDirectoryName(newFull) ?? "";
        var newStem = Path.GetFileNameWithoutExtension(newFull);
        return files.Select(f =>
        {
            var full = Path.GetFullPath(f);
            if (!IsDefaultNamed(full, oldDir, oldStem)) return full;
            var moved = RenamedDefault(full, oldStem, newDir, newStem);
            return !File.Exists(full) && File.Exists(moved) ? moved : full;
        }).ToList();
    }

    /// <summary>
    /// Renames a project and its owned default exports together, without replacing any destination.
    /// The export record inside the renamed project is updated to the new names.
    /// </summary>
    public static void RenameWithOwnedDefaultExport(string oldProjectPath, string newProjectPath)
    {
        var oldFull = Path.GetFullPath(oldProjectPath);
        var newFull = Path.GetFullPath(newProjectPath);
        var oldStem = Path.GetFileNameWithoutExtension(oldFull);
        var newDir = Path.GetDirectoryName(newFull) ?? "";
        var newStem = Path.GetFileNameWithoutExtension(newFull);
        var paths = new List<(string Source, string Destination)>();
        var newPngs = new List<string>();
        foreach (var oldPng in GetOwnedDefaultPngs(oldProjectPath))
        {
            var newPng = RenamedDefault(oldPng, oldStem, newDir, newStem);
            newPngs.Add(newPng);
            paths.Add((oldPng, newPng));
            foreach (var source in DocumentIO.GetCompanionPaths(oldPng).Where(File.Exists))
                paths.Add((source, newPng + source[oldPng.Length..]));
        }
        paths.Add((oldProjectPath, newProjectPath));

        foreach (var (source, destination) in paths)
            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
                throw new IOException(Gpixel.Core.Localization.DomainStrings.FormatFile_NameExists(name: Path.GetFileName(destination)));

        // An orphan companion at the new name must not become associated with the moved PNG.
        foreach (var newPng in newPngs)
            foreach (var destination in DocumentIO.GetCompanionPaths(newPng))
                if (!paths.Any(p => string.Equals(p.Destination, destination, StringComparison.OrdinalIgnoreCase))
                    && File.Exists(destination))
                    throw new IOException(Gpixel.Core.Localization.DomainStrings.FormatFile_NameExists(name: Path.GetFileName(destination)));

        var moved = new List<(string Source, string Destination)>();
        try
        {
            foreach (var (source, destination) in paths)
            {
                MoveWithoutReplacing(source, destination);
                moved.Add((source, destination));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                for (int i = moved.Count - 1; i >= 0; i--)
                    MoveWithoutReplacing(moved[i].Destination, moved[i].Source);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                throw new IOException(Gpixel.Core.Localization.DomainStrings.Project_RenameRestoreFailed, new AggregateException(ex, rollback));
            }
            throw;
        }
        UpdateRecordAfterRename(oldFull, newFull);
    }

    /// <summary>
    /// 이름을 바꾼 프로젝트 파일 안의 내보내기 기록을 새 이름으로 고쳐 쓴다(project.json만 바꾸고 나머지 항목은 그대로 복사).
    /// 기록이 없거나 고칠 수 없으면 그대로 둔다(기록은 정리·숨김에만 쓰이고, 남의 기록으로 보이면 무시되므로 안전).
    /// </summary>
    private static void UpdateRecordAfterRename(string oldFull, string newFull)
    {
        var temp = newFull + ".gpixel-tmp";
        try
        {
            ProjectDto dto;
            using (var src = ZipFile.OpenRead(newFull))
            {
                dto = ReadDto(src);
                if (ResolveRecord(dto.Exported, oldFull) is not { } files) return;
                dto.Exported = ToRecordDto(RemapExportRecord(files, oldFull, newFull), newFull);
                using var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                using var dst = new ZipArchive(fs, ZipArchiveMode.Create);
                WriteProjectJson(dst, dto);
                foreach (var e in src.Entries)
                {
                    if (string.Equals(e.FullName, ProjectEntryName, StringComparison.Ordinal)) continue;
                    var copy = dst.CreateEntry(e.FullName, CompressionLevel.NoCompression);
                    using var from = e.Open();
                    using var to = copy.Open();
                    from.CopyTo(to);
                }
            }
            File.Move(temp, newFull, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void MoveWithoutReplacing(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.Ordinal)) return;
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            // Windows requires a distinct intermediate name for case-only changes.
            var temp = source + "." + Guid.NewGuid().ToString("N") + ".rename-tmp";
            File.Move(source, temp);
            try { File.Move(temp, destination); }
            catch
            {
                File.Move(temp, source);
                throw;
            }
        }
        else File.Move(source, destination);
    }

    // ───────────── 읽기 ─────────────

    /// <summary>
    /// 프로젝트 파일을 연다(경로 설정, 수정됨 아님). 손상·지원하지 않는 버전이면 <see cref="InvalidDataException"/>.
    /// 읽지 못한 셀은 빈 셀이 되고 <paramref name="warnings"/>에 적힌다. 버전 1 아틀라스 파일은 부위 레이어로 바꿔 읽는다.
    /// </summary>
    /// <param name="progress">진행(셀 PNG 풀기). 셀이 없으면 단계를 잡지 않는다.</param>
    public static SpriteProject Load(string path, ICollection<string>? warnings = null, WorkProgress? progress = null)
    {
        var full = Path.GetFullPath(path);
        SpriteProject project;
        using (var fs = File.OpenRead(full))
            project = Load(fs, warnings, full, progress);
        project.Path = full;
        project.IsDirty = false;
        return project;
    }

    /// <inheritdoc cref="Load(string, ICollection{string}?, WorkProgress?)"/>
    public static SpriteProject Load(Stream stream, ICollection<string>? warnings = null) => Load(stream, warnings, null, null);

    /// <param name="fullPath">프로젝트 파일 경로. 버전 1 아틀라스의 예전 내보내기 이름(파일 이름, null이면 "sprite")과 내보내기 기록에 쓴다.</param>
    private static SpriteProject Load(Stream stream, ICollection<string>? warnings, string? fullPath, WorkProgress? progress)
    {
        var baseName = fullPath is null ? null : Path.GetFileNameWithoutExtension(fullPath);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.FormatProject_InvalidFile(error: ex.Message), ex);
        }

        using (zip)
        {
            var dto = ReadDto(zip);
            if (dto.Width < 1 || dto.Height < 1 || dto.Width > PixelImage.MaxSize || dto.Height > PixelImage.MaxSize)
                throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.FormatProject_InvalidCanvas(width: dto.Width, height: dto.Height));
            if (dto.Kind == V1Kind.Atlas) return ConvertV1Atlas(zip, dto, warnings, baseName ?? "sprite");

            var (project, fileLayers) = FromDto(dto, warnings);
            project.ExportedFiles = ResolveRecord(dto.Exported, fullPath);
            // 셀 PNG는 ZIP에서 차례대로 읽어 여러 코어에서 풀고, 프로젝트·경고에는 파일 차례대로 넣는다(한 번에 차례대로 읽은 것과 같다).
            var slots = new List<(ZipArchiveEntry Entry, Layer? Layer, int Frame, Skin? Skin)>();
            foreach (var e in zip.Entries)
            {
                if (CelEntry().Match(e.FullName.Replace('\\', '/')) is not { Success: true } m) continue;
                if (!TryIndex(m, 1, fileLayers.Count, out var li) || !TryIndex(m, 2, project.Frames.Count, out var fi)
                    || fileLayers[li].IsGroup || !TrySkin(m, fileLayers[li], out var skin))
                    slots.Add((e, null, 0, null));
                else slots.Add((e, fileLayers[li], fi, skin));
            }
            var cels = new (PixelImage? Image, string? Warning)[slots.Count];
            var bytes = new byte[]?[slots.Count];
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].Layer is not null)
                    (bytes[i], cels[i].Warning) = ReadBytes(slots[i].Entry);
            using (var stage = slots.Count == 0 ? null : progress?.Begin(bytes.Sum(b => (long)(b?.Length ?? 0))))
                Parallel.For(0, slots.Count, i =>
                {
                    if (bytes[i] is not { } png) return;
                    cels[i] = DecodeCel(slots[i].Entry.FullName, png, project.CelSize(slots[i].Layer!));
                    stage?.Step(png.Length);
                });
            for (int i = 0; i < slots.Count; i++)
            {
                var (e, layer, fi, skin) = slots[i];
                if (layer is null)
                {
                    warnings?.Add(Gpixel.Core.Localization.DomainStrings.FormatProject_UnusedCell(name: e.FullName));
                    continue;
                }
                if (cels[i].Warning is { } warning) warnings?.Add(warning);
                if (cels[i].Image is { } img) project.SetCelRaw(layer, project.Frames[fi], img, skin);
            }
            project.NormalizeSkins();
            return project;
        }
    }

    /// <summary>셀 파일의 스킨 번호(<c>_S{n}</c>): 없으면 첫 스킨(null). 있으면 그룹에 그 번호(1~)의 스킨이 있는 부위일 때만 유효.</summary>
    private static bool TrySkin(Match m, Layer layer, out Skin? skin)
    {
        skin = null;
        if (!m.Groups[3].Success) return true;
        var skins = layer is { IsPart: true, Parent: { IsGroup: true } g } ? g.AllSkins : [];
        if (!TryIndex(m, 3, skins.Count, out var si) || si == 0) return false;
        skin = skins[si];
        return true;
    }

    /// <summary><c>project.json</c>을 읽고 버전을 확인한다.</summary>
    private static ProjectDto ReadDto(ZipArchive zip)
    {
        var entry = zip.GetEntry(ProjectEntryName)
                    ?? throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.Project_MissingInfo);
        ProjectDto dto;
        try
        {
            using var s = entry.Open();
            dto = JsonSerializer.Deserialize<ProjectDto>(s, Options)
                  ?? throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.Project_EmptyInfo);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.FormatProject_CorruptInfo(error: ex.Message), ex);
        }
        if (dto.Version > CurrentVersion)
            throw new InvalidDataException(
                Gpixel.Core.Localization.DomainStrings.FormatProject_NewerVersion(version: dto.Version, current: CurrentVersion));
        return dto;
    }

    /// <summary>셀 PNG를 읽어 <paramref name="size"/>로 맞춘다(크기가 다르면 왼쪽 위에 맞추고 알림). 읽지 못하면 null.</summary>
    private static PixelImage? ReadCel(ZipArchiveEntry e, Size size, ICollection<string>? warnings)
    {
        var (bytes, readError) = ReadBytes(e);
        var (image, warning) = bytes is null ? (null, readError) : DecodeCel(e.FullName, bytes, size);
        if (warning is not null) warnings?.Add(warning);
        return image;
    }

    /// <summary>ZIP 항목의 바이트. 읽지 못하면 null과 알림.</summary>
    private static (byte[]? Bytes, string? Warning) ReadBytes(ZipArchiveEntry e)
    {
        try
        {
            using var es = e.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            return (ms.ToArray(), null);
        }
        catch (InvalidDataException ex)
        {
            return (null, Gpixel.Core.Localization.DomainStrings.FormatProject_CellReadFailed(name: e.FullName, error: ex.Message));
        }
    }

    /// <summary>셀 PNG를 풀어 <paramref name="size"/>로 맞춘다(크기가 다르면 왼쪽 위에 맞추고 알림). 풀지 못하면 null과 알림. 아무 스레드에서나 불러도 된다.</summary>
    private static (PixelImage? Image, string? Warning) DecodeCel(string name, byte[] png, Size size)
    {
        PixelImage img;
        try
        {
            using var ms = new MemoryStream(png, writable: false);
            img = PngIO.Load(ms);
        }
        catch (InvalidDataException ex)
        {
            return (null, Gpixel.Core.Localization.DomainStrings.FormatProject_CellReadFailed(name: name, error: ex.Message));
        }
        if (img.Size == size) return (img, null);
        var fitted = new PixelImage(size.Width, size.Height);
        fitted.Blit(img, 0, 0);
        return (fitted, Gpixel.Core.Localization.DomainStrings.FormatProject_CellResized(name: name));
    }

    private static bool TryIndex(Match m, int group, int count, out int index)
        => int.TryParse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index < count;

    /// <summary>파일 목록 썸네일: 첫 프레임 합성.</summary>
    public static PixelImage LoadThumbnail(string path) => Load(path).Flatten(0);

    // ───────────── 버전 1 아틀라스 변환 ─────────────

    /// <summary>
    /// 버전 1 아틀라스 → 그룹 "부위" + 스프라이트마다 부위 하나(첫 스프라이트 = 맨 위 부위), 프레임 1개.
    /// 부위 셀 = 그 스프라이트의 보이는 레이어 합성. 모든 스프라이트에 원래 칸이 있으면 캔버스 = 원래 시트 크기, 위치 = 원래 칸,
    /// 아니면 왼쪽부터 1px 간격으로 늘어놓는다. 내보내기: 부위 아틀라스 = 예전 이름, 프레임 시트 = <c>{예전 이름}_frames</c>.
    /// </summary>
    private static SpriteProject ConvertV1Atlas(ZipArchive zip, ProjectDto dto, ICollection<string>? warnings, string baseName)
    {
        // 파일의 스프라이트 번호 → 유효한 스프라이트
        var sprites = new List<(SpriteDto Dto, int FileIndex)>();
        var spriteDtos = dto.Sprites ?? [];
        for (int i = 0; i < spriteDtos.Count; i++)
        {
            var sd = spriteDtos[i];
            if (sd.Width < 1 || sd.Height < 1 || sd.Width > PixelImage.MaxSize || sd.Height > PixelImage.MaxSize) continue;
            sprites.Add((sd, i));
        }
        if (sprites.Count == 0) sprites.Add((new SpriteDto { Name = "sprite", Width = dto.Width, Height = dto.Height }, 0));

        var layerDtos = dto.Layers ?? [];
        int layerCount = Math.Max(1, layerDtos.Count);
        var cels = new Dictionary<(int Sprite, int Layer), PixelImage>();
        foreach (var e in zip.Entries)
        {
            if (AtlasEntry().Match(e.FullName.Replace('\\', '/')) is not { Success: true } m) continue;
            int si = -1;
            if (TryIndex(m, 1, spriteDtos.Count == 0 ? 1 : spriteDtos.Count, out var fileSprite))
                si = sprites.FindIndex(s => s.FileIndex == fileSprite);
            if (si < 0 || !TryIndex(m, 2, layerCount, out var li))
            {
                warnings?.Add(Gpixel.Core.Localization.DomainStrings.FormatProject_UnusedCell(name: e.FullName));
                continue;
            }
            var size = new Size(sprites[si].Dto.Width, sprites[si].Dto.Height);
            if (ReadCel(e, size, warnings) is { } img && !img.IsRegionEmpty(img.Bounds)) cels[(si, li)] = img;
        }
        int layersWithContent = cels.Keys.Select(k => k.Layer).Distinct().Count();
        if (layersWithContent > 1)
            warnings?.Add(Gpixel.Core.Localization.DomainStrings.FormatProject_CombinedLayers(count: layersWithContent));

        // 캔버스·위치
        Rectangle? Source(SpriteDto s) => s.Source is { Length: >= 4 } r && r[2] > 0 && r[3] > 0 ? new Rectangle(r[0], r[1], r[2], r[3]) : null;
        var offsets = new List<Point>(sprites.Count);
        int canvasW, canvasH;
        if (sprites.All(s => Source(s.Dto) is not null))
        {
            offsets.AddRange(sprites.Select(s => Source(s.Dto)!.Value.Location));
            bool hasSheet = dto.AtlasSourceWidth is > 0 && dto.AtlasSourceHeight is > 0;
            canvasW = hasSheet ? dto.AtlasSourceWidth!.Value : sprites.Max(s => Source(s.Dto)!.Value.Right);
            canvasH = hasSheet ? dto.AtlasSourceHeight!.Value : sprites.Max(s => Source(s.Dto)!.Value.Bottom);
        }
        else
        {
            int x = 0;
            foreach (var (sd, _) in sprites)
            {
                offsets.Add(new Point(x, 0));
                x += sd.Width + 1;
            }
            canvasW = x - 1;
            canvasH = sprites.Max(s => s.Dto.Height);
        }
        canvasW = Math.Clamp(canvasW, 1, PixelImage.MaxSize);
        canvasH = Math.Clamp(canvasH, 1, PixelImage.MaxSize);

        var project = SpriteProject.CreateBlank(canvasW, canvasH);
        var frame = new Frame();
        project.AddFrameRaw(frame);
        ApplySettings(project, dto);
        var visible = Enumerable.Range(0, layerCount)
            .Select(i => i < layerDtos.Count ? (layerDtos[i].Visible, (byte)Math.Clamp(layerDtos[i].Opacity, 0, 255)) : (true, (byte)255))
            .ToList();

        var parts = new List<Layer>(sprites.Count);
        for (int si = 0; si < sprites.Count; si++)
        {
            var sd = sprites[si].Dto;
            var part = new Layer(project.UniqueLayerName(string.IsNullOrWhiteSpace(sd.Name) ? "sprite" : sd.Name.Trim()))
            {
                Kind = LayerKind.Part,
                PartSize = new Size(sd.Width, sd.Height),
                PartOffset = offsets[si],
                PartPivot = sd.Pivot?.ToPivot() ?? project.Sprite.DefaultPivot,
                PartBorder = sd.Border is { Length: >= 4 } b ? new SpriteBorder(b[0], b[1], b[2], b[3]) : SpriteBorder.None,
                PartSourceRect = Source(sd),
            };
            parts.Add(part);
            project.AddLayerRaw(part); // 이름 중복 검사용(순서는 아래에서 다시)

            var flat = new PixelImage(sd.Width, sd.Height);
            for (int li = 0; li < layerCount; li++)
                if (visible[li].Item1 && cels.TryGetValue((si, li), out var cel)) flat.Composite(cel, 0, 0, visible[li].Item2);
            if (!flat.IsRegionEmpty(flat.Bounds)) project.SetCelRaw(part, frame, flat);
        }
        var group = new Layer(project.UniqueLayerName("부위")) { Kind = LayerKind.Group };
        foreach (var part in parts) part.Parent = group;
        project.ReplaceLayersRaw([.. Enumerable.Reverse(parts), group]);

        var oldName = project.Export.FileName ?? baseName;
        project.Export.ExportParts = true;
        project.Export.PartsFileName = oldName;
        project.Export.ExportFrames = true;
        project.Export.FileName = oldName + "_frames";
        ApplySheets(project, dto.Export ?? new ExportDto(), [], warnings);
        return project;
    }

    // ───────────── 변환 ─────────────

    private static ProjectDto ToDto(SpriteProject p, string? projectPath) => new()
    {
        Version = CurrentVersion,
        Width = p.Width,
        Height = p.Height,
        Layers = p.Layers.Select(l => new LayerDto
        {
            Name = l.Name,
            Visible = l.Visible,
            Locked = l.Locked,
            Opacity = l.Opacity,
            Kind = l.Kind == LayerKind.Normal ? null : l.Kind,
            Parent = l.Parent is { } g ? p.IndexOf(g) : null,
            Collapsed = l.IsGroup && l.Collapsed ? true : null,
            EffectsCollapsed = l.EffectsCollapsed ? true : null,
            PartWidth = l.IsPart ? l.PartSize.Width : null,
            PartHeight = l.IsPart ? l.PartSize.Height : null,
            PartX = l.IsPart ? l.PartOffset.X : null,
            PartY = l.IsPart ? l.PartOffset.Y : null,
            PartPivot = l.IsPart ? PivotDto.From(l.PartPivot) : null,
            PartBorder = l.IsPart && !l.PartBorder.IsNone ? [l.PartBorder.Left, l.PartBorder.Top, l.PartBorder.Right, l.PartBorder.Bottom] : null,
            PartSource = l.IsPart && l.PartSourceRect is { } r ? [r.X, r.Y, r.Width, r.Height] : null,
            Skins = l.IsGroup ? l.AllSkins.Select(s => s.Name).ToList() : null,
            SkinOwners = l.IsGroup && l.AllSkins.Any(s => s.Owner is not null) ? l.AllSkins.Select(s => s.Owner is { } o ? p.IndexOf(o) : -1).ToList() : null,
            ActiveSkin = l.IsGroup && l.SkinList.IndexOf(l.ActiveSkin!) is > 0 and var ai ? ai : null,
            Skin = l is { IsPart: true, SkinOverride: { } so, Parent: { } pg } ? pg.SkinList.IndexOf(so) : null,
            Effects = l.Effects.Count > 0 ? l.Effects.Select(e => (JsonObject?)EffectCodec.Write(e)).ToList() : null,
            AutoLayout = l is { IsGroup: true, AutoLayout: { } al } ? GroupLayoutDto.From(al) : null,
        }).ToList(),
        Frames = p.Frames.Select(f => new FrameDto
        {
            DurationMs = f.DurationMs,
            Pivot = f.Pivot is { } fp ? PivotDto.From(fp) : null,
            Name = f.Name,
        }).ToList(),
        Tags = p.Tags.Select(t => new TagDto
        {
            Name = t.Name, From = t.From, To = t.To, Direction = t.Direction, Loop = t.Loop, Color = t.Color.ToHex(),
        }).ToList(),
        Unity = UnityDto.From(p.Sprite),
        Export = ExportDto.From(p.Export, p),
        Outfits = p.Outfits.Count == 0 ? null : p.Outfits.Select(o => new OutfitDto
        {
            Name = o.Name,
            Slots = o.Slots.Select(s => SkinRef(p, s.Group, s.Group, s.Skin)).OfType<SkinRefDto>().ToList(),
            Parts = o.Parts.Select(s => SkinRef(p, s.Part, s.Part.Parent, s.Skin)).OfType<SkinRefDto>().ToList(),
        }).ToList(),
        Exported = ToRecordDto(p.ExportedFiles, projectPath),
        Scripts = p.ScriptsInUse() is { Count: > 0 } scripts ? scripts : null,
    };

    /// <summary>알림에 보일 해시 앞부분(12자).</summary>
    private static string ShortHash(string? hash) => hash is null ? "" : hash.Length > 12 ? hash[..12] : hash;

    private static List<T>? NullIfEmpty<T>(IEnumerable<T> items) => items.ToList() is { Count: > 0 } list ? list : null;

    private static int IndexOf<T>(IReadOnlyList<T> list, T item) where T : class
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    /// <summary>(레이어 번호, 그 그룹의 스킨 번호). 레이어가 프로젝트에 없거나 스킨이 그룹에 없으면 null(지운 항목은 저장하지 않음).</summary>
    private static SkinRefDto? SkinRef(SpriteProject p, Layer layer, Layer? group, Skin skin)
        => p.IndexOf(layer) is >= 0 and var li && group is { IsGroup: true } g && p.IndexOf(g) >= 0 && IndexOf(g.AllSkins, skin) is >= 0 and var si
            ? new SkinRefDto { Layer = li, Skin = si } : null;

    /// <summary>스킨이 든 그룹 기준 참조(내보내기에서 뺄 스킨).</summary>
    private static SkinRefDto? GroupSkinRef(SpriteProject p, Skin skin)
        => p.Layers.FirstOrDefault(l => l.IsGroup && IndexOf(l.Skins, skin) >= 0) is { } g ? SkinRef(p, g, g, skin) : null;

    /// <summary>JSON → 프로젝트(셀 제외). 파일 안 레이어 순서 목록도 돌려준다(셀 파일 번호용, 구조를 고치면 순서가 바뀔 수 있음).</summary>
    private static (SpriteProject Project, List<Layer> FileLayers) FromDto(ProjectDto dto, ICollection<string>? warnings)
    {
        var p = SpriteProject.CreateBlank(dto.Width, dto.Height);
        // 스크립트 표는 효과보다 먼저 넣는다(효과 설정을 그 schema에 맞춰 읽음). 원문과 해시가 다르면 쓰지 않는다(효과는 남고 그리지 않음).
        foreach (var (hash, source) in dto.Scripts ?? [])
        {
            if (ScriptLibrary.IsHash(hash) && source is not null && ScriptEffectProgram.HashOf(source) == hash) p.AddScript(source);
            else warnings?.Add(Gpixel.Core.Localization.DomainStrings.FormatProject_BadScript(hash: ShortHash(hash)));
        }
        var layerDtos = dto.Layers ?? [];
        var fileLayers = new List<Layer>(layerDtos.Count);
        foreach (var l in layerDtos)
        {
            var kind = l.Kind is { } k && Enum.IsDefined(k) ? k : LayerKind.Normal;
            var layer = new Layer(string.IsNullOrWhiteSpace(l.Name) ? p.NextLayerName() : l.Name)
            {
                Visible = l.Visible,
                Locked = l.Locked,
                Opacity = (byte)Math.Clamp(l.Opacity, 0, 255),
                Kind = kind,
                Collapsed = kind == LayerKind.Group && l.Collapsed == true,
                EffectsCollapsed = l.EffectsCollapsed == true,
            };
            if (kind == LayerKind.Part)
            {
                layer.PartSize = new Size(Math.Clamp(l.PartWidth ?? 1, 1, PixelImage.MaxSize), Math.Clamp(l.PartHeight ?? 1, 1, PixelImage.MaxSize));
                layer.PartOffset = new Point(l.PartX ?? 0, l.PartY ?? 0);
                layer.PartPivot = l.PartPivot?.ToPivot() ?? Pivot.Center;
                layer.PartBorder = l.PartBorder is { Length: >= 4 } b ? new SpriteBorder(b[0], b[1], b[2], b[3]) : SpriteBorder.None;
                layer.PartSourceRect = l.PartSource is { Length: >= 4 } r && r[2] > 0 && r[3] > 0 ? new Rectangle(r[0], r[1], r[2], r[3]) : null;
            }
            else if (kind == LayerKind.Group && l.Skins is { Count: > 0 } names)
            {
                // 스킨 이름은 그룹 안에서 겹치지 않게(손상된 파일 대비). 스킨이 없던 예전 파일은 "기본" 하나.
                var skins = new List<Skin>(names.Count);
                foreach (var n in names)
                {
                    var name = string.IsNullOrWhiteSpace(n) ? "스킨" : n.Trim();
                    var unique = name;
                    for (int i = 2; skins.Any(s => s.Name == unique); i++) unique = $"{name} {i}";
                    skins.Add(new Skin(unique));
                }
                layer.ReplaceSkinsRaw(skins);
                if (l.ActiveSkin is int ai && ai >= 0 && ai < skins.Count) layer.SetActiveSkinRaw(skins[ai]);
            }
            if (kind == LayerKind.Group && l.AutoLayout is { } layoutDto) layer.AutoLayout = layoutDto.ToLayout();
            if (l.Effects is { Count: > 0 } effects)
            {
                var list = new List<LayerEffect>(effects.Count);
                foreach (var e in effects)
                {
                    var effect = e is null ? null : EffectCodec.Read(e);
                    if (effect is not null) list.Add(effect);
                    if (effect is ScriptEffect script && !p.Scripts.ContainsKey(script.ScriptHash))
                        warnings?.Add(Gpixel.Core.Localization.DomainStrings.FormatProject_MissingScript(layer: layer.Name, hash: ShortHash(script.ScriptHash)));
                    // 모르는 효과는 원본 JSON 그대로 남기고(다시 저장하면 그대로 씀) 그리지 않는다는 것만 알린다.
                    if (effect is null or MissingEffect)
                        warnings?.Add(Gpixel.Core.Localization.DomainStrings.FormatProject_UnknownEffect(layer: layer.Name, type: (effect as MissingEffect)?.TypeId));
                }
                layer.Effects = list;
            }
            fileLayers.Add(layer);
            p.AddLayerRaw(layer);
        }
        for (int i = 0; i < fileLayers.Count; i++)
            if (fileLayers[i].IsPart && layerDtos[i].Parent is int gi && gi >= 0 && gi < fileLayers.Count && fileLayers[gi].IsGroup)
                fileLayers[i].Parent = fileLayers[gi];
        if (p.NormalizeHierarchy()) warnings?.Add(Gpixel.Core.Localization.DomainStrings.Project_RepairedHierarchy);
        for (int i = 0; i < fileLayers.Count; i++)
            if (fileLayers[i] is { IsPart: true, Parent: { IsGroup: true } pg } part && layerDtos[i].Skin is int si && si >= 0 && si < pg.AllSkins.Count)
                part.SetSkinOverrideRaw(pg.AllSkins[si]);
        // 부위 전용 스킨의 주인(그 그룹의 부위가 아니면 전체 스킨으로 — 순서 정리는 셀을 읽은 뒤 NormalizeSkins)
        for (int i = 0; i < fileLayers.Count; i++)
            if (fileLayers[i].IsGroup && layerDtos[i].SkinOwners is { } owners)
            {
                var all = fileLayers[i].AllSkins;
                for (int k = 1; k < all.Count && k < owners.Count; k++)
                    if (ResolveLayer(fileLayers, owners[k]) is { IsPart: true } owner && ReferenceEquals(owner.Parent, fileLayers[i])) all[k].Owner = owner;
            }
        if (p.Layers.Count == 0) p.AddLayerRaw(new Layer(p.NextLayerName()));

        foreach (var f in dto.Frames ?? [])
            p.AddFrameRaw(new Frame
            {
                DurationMs = f.DurationMs,
                Pivot = f.Pivot?.ToPivot(),
                Name = string.IsNullOrWhiteSpace(f.Name) ? null : f.Name,
            });
        if (p.Frames.Count == 0) p.AddFrameRaw(new Frame());
        // 효과 프레임 범위는 프레임 수에 맞춘다(손상된 파일 대비 — 밖은 안으로, 모든 프레임을 덮으면 모든 프레임).
        foreach (var layer in p.Layers)
            if (layer.Effects.Any(e => e.Frames is not null))
                layer.Effects = layer.Effects.Select(e => e.Frames is { } r ? e with { Frames = FrameRange.Of(r.From, r.To, p.Frames.Count) } : e).ToArray();

        foreach (var t in dto.Tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            int from = Math.Clamp(t.From, 0, p.Frames.Count - 1);
            int to = Math.Clamp(t.To, 0, p.Frames.Count - 1);
            if (from > to) continue;
            p.AddTagRaw(new AnimationTag(p.UniqueTagName(t.Name), from, to)
            {
                Direction = t.Direction,
                Loop = t.Loop,
                Color = Color32.TryParseHex(t.Color, out var c) ? c : AnimationTag.DefaultColors[p.Tags.Count % AnimationTag.DefaultColors.Count],
            });
        }

        ApplySettings(p, dto);
        ApplyOutfits(p, dto, fileLayers);
        if (dto.Export is { } export) ApplySheets(p, export, fileLayers, warnings);
        return (p, fileLayers);
    }

    /// <summary>파일 안 (레이어 번호, 스킨 번호) → 그룹 스킨. 그룹이 아니거나 번호가 잘못되면 null.</summary>
    private static Skin? ResolveSkin(List<Layer> fileLayers, SkinRefDto r)
        => r.Layer >= 0 && r.Layer < fileLayers.Count && fileLayers[r.Layer] is var l
           && (l.IsGroup ? l : l.IsPart ? l.Parent : null) is { IsGroup: true } g && r.Skin >= 0 && r.Skin < g.AllSkins.Count ? g.AllSkins[r.Skin] : null;

    private static Layer? ResolveLayer(List<Layer> fileLayers, int index) => index >= 0 && index < fileLayers.Count ? fileLayers[index] : null;

    /// <summary>착장 프리셋을 읽는다. 잘못된 항목은 건너뛴다.</summary>
    private static void ApplyOutfits(SpriteProject p, ProjectDto dto, List<Layer> fileLayers)
    {
        foreach (var o in dto.Outfits ?? [])
        {
            var slots = new List<OutfitSlot>();
            foreach (var s in o.Slots ?? [])
                if (ResolveLayer(fileLayers, s.Layer) is { IsGroup: true } g && ResolveSkin(fileLayers, s) is { } skin && !slots.Any(x => x.Group == g))
                    slots.Add(new OutfitSlot(g, skin));
            var parts = new List<OutfitPart>();
            foreach (var s in o.Parts ?? [])
                if (ResolveLayer(fileLayers, s.Layer) is { IsPart: true } part && ResolveSkin(fileLayers, s) is { } skin && !parts.Any(x => x.Part == part))
                    parts.Add(new OutfitPart(part, skin));
            p.AddOutfitRaw(new Outfit(p.UniqueOutfitName(o.Name ?? ""), slots, parts));
        }
    }

    /// <summary>Unity·내보내기 설정(시트 설정 제외 — <see cref="ApplySheets"/>)을 적용한다.</summary>
    private static void ApplySettings(SpriteProject p, ProjectDto dto)
    {
        dto.Unity?.ApplyTo(p.Sprite);
        if (dto.Export is { } e) ApplyExport(p.Export, e);
    }

    internal static void ApplyExport(ExportSettings x, ExportDto e)
    {
        x.ExportOnSave = e.OnSave;
        x.Folder = string.IsNullOrWhiteSpace(e.Folder) ? null : e.Folder;
        x.FileName = string.IsNullOrWhiteSpace(e.FileName) ? null : e.FileName;
        x.ExportFrames = e.ExportFrames;
        x.ExportParts = e.ExportParts;
        x.PartsFileName = string.IsNullOrWhiteSpace(e.PartsFileName) ? null : e.PartsFileName;
        x.ImageFormat = Enum.IsDefined(e.ImageFormat) ? e.ImageFormat : ExportImageFormat.Png;
        x.JpegQuality = e.JpegQuality;
        x.SingleFrameIndex = e.SingleFrame;
        x.WriteMeta = e.WriteMeta;
        x.WriteSpriteLibrary = e.SpriteLibrary == true;
        x.KeepImportedRects = e.KeepImportedRects;
    }

    /// <summary>
    /// 시트 설정: 새 파일은 <c>sheet</c>(공통)·<c>partsSheet</c>(부위 아틀라스 따로). 예전 파일(프레임 시트·부위 아틀라스 설정이 따로 있던 형식)은
    /// 두 설정을 각각 만들어, 부위 아틀라스 결과가 공통(프레임 시트) 설정으로도 같으면(또는 한쪽 결과를 내보내지 않으면) 하나로 합치고,
    /// 다르면 부위 아틀라스를 "따로"(<see cref="ExportSettings.PartsOverride"/>)로 남겨 결과가 조용히 바뀌지 않게 한다.
    /// </summary>
    private static void ApplySheets(SpriteProject p, ExportDto e, List<Layer> fileLayers, ICollection<string>? warnings)
    {
        var x = p.Export;
        if (e.Sheet is { } sheet)
        {
            sheet.ApplyTo(x.Common, p, fileLayers);
            if (e.PartsSheet is { } ps)
            {
                var o = new SheetOptions();
                ps.ApplyTo(o, p, fileLayers);
                x.PartsOverride = o;
            }
            return;
        }

        // ── 예전 형식 → 공통 설정 ──
        var frames = new SheetOptions
        {
            Layout = SheetLayout.Grid,
            Spacing = e.Spacing ?? 0,
            Trim = e.Trim == true,
            MaxWidth = e.AtlasMaxWidth ?? 0,
            PackOrder = e.AtlasOrder ?? AtlasPackOrder.KeepOrder,
            SplitTags = e.SplitFrames == true,
            SplitParts = e.SplitParts == true,
            Naming = SpriteNaming.ByTag,
            ExcludedLayers = (e.FrameExcludedLayers ?? []).Select(i => ResolveLayer(fileLayers, i)).OfType<Layer>().Where(l => !l.IsPart).Distinct().ToArray(),
            BakeInheritedSkins = e.BakeInheritedSkins == true,
        };
        // 예전 프레임 시트: 태그마다 한 줄(줄바꿈 없음, 태그가 없던 파일은 격자와 같았음) / 격자(프레임 순, 칸 수 설정 — 설정이 없던 파일은 열 수 > 0 = 가로 N칸, 0 = 한 줄)
        if (string.Equals(e.Layout, "RowPerTag", StringComparison.OrdinalIgnoreCase) && p.Tags.Count > 0)
        {
            frames.Order = GridOrder.ByPartTag;
            frames.Grid = SheetGrid.Columns;
            frames.GridCount = SheetOptions.MaxGridCount;
        }
        else if (e.FrameGrid is { } fg && Enum.IsDefined(fg))
        {
            frames.Grid = fg;
            frames.GridCount = e.FrameGridCount ?? 1;
        }
        else if (e.Columns is > 0 and var columns)
        {
            frames.Grid = SheetGrid.Columns;
            frames.GridCount = columns;
        }
        else
        {
            frames.Grid = SheetGrid.Rows;
            frames.GridCount = 1;
        }
        var outfits = (e.FrameOutfits ?? []).Where(i => i >= 0 && i < p.Outfits.Count).Select(i => p.Outfits[i]).Distinct().ToArray();
        if (outfits.Length > 0)
        {
            // 예전 "착장마다 따로 파일" → 고른 스킨(착장만) + 스킨마다 따로 파일
            frames.Skins = SkinSource.Selected;
            frames.Outfits = outfits;
            frames.ExcludedSkins = p.Layers.Where(l => l.IsGroup).SelectMany(l => l.Skins).ToArray();
            frames.SplitSkins = true;
            if (e.FrameCurrentOutfit != false)
                warnings?.Add(Gpixel.Core.Localization.DomainStrings.Project_MigratedLegacyOutfit);
        }

        var legacyParts = e.PartsLayout ?? "Packed";
        var parts = new SheetOptions
        {
            Layout = string.Equals(legacyParts, "Packed", StringComparison.OrdinalIgnoreCase) ? SheetLayout.Packed : SheetLayout.Grid,
            Order = legacyParts.ToLowerInvariant() switch
            {
                "rowperframe" => GridOrder.ByFrame,
                "rowperparttag" => GridOrder.ByPartTag,
                _ => GridOrder.ByPart,
            },
            Grid = e.PartsGrid is { } pg && Enum.IsDefined(pg) ? pg : SheetGrid.Auto,
            GridCount = e.PartsGrid is { } pg2 && Enum.IsDefined(pg2) ? e.PartsGridCount ?? 1 : 1,
            Spacing = frames.Spacing,
            PowerOfTwo = e.AtlasPowerOfTwo == true,
            MaxWidth = frames.MaxWidth,
            PackOrder = frames.PackOrder,
            SplitSkins = e.SplitSkins == true,
            SplitParts = e.SplitParts == true,
            // 이름 규칙이 없던 파일은 프레임 번호(이름이 바뀌면 Unity 참조가 끊김).
            Naming = e.PartsNaming is { } pn && Enum.IsDefined(pn) ? pn : SpriteNaming.FrameNumber,
            Skins = SkinSource.Selected,
            ExcludedSkins = (e.ExcludedSkins ?? []).Select(r => ResolveSkin(fileLayers, r)).OfType<Skin>().Distinct().ToArray(),
            Outfits = frames.Outfits,
            BakeInheritedSkins = frames.BakeInheritedSkins,
        };

        bool partsMatter = x.ExportParts && p.Layers.Any(l => l.IsPart);
        if (!x.ExportFrames && partsMatter) x.Common.CopyFrom(parts);
        else
        {
            x.Common.CopyFrom(frames);
            if (partsMatter && !SameForParts(p, frames, parts)) x.PartsOverride = parts;
        }
    }

    /// <summary>부위 아틀라스 결과가 두 설정에서 같은지(이 프로젝트에서 뜻이 있는 항목만 비교 — 예전 파일 이행용).</summary>
    internal static bool SameForParts(SpriteProject p, SheetOptions a, SheetOptions b)
    {
        bool tags = p.Tags.Count > 0;
        bool skins = p.Layers.Any(l => l.IsGroup && l.Skins.Count > 1);
        GridOrder Effective(GridOrder o)
        {
            if (tags || !o.Keys.Contains(GridKey.Tag)) return o;
            int at = o.Keys.ToList().IndexOf(GridKey.Tag);
            return new GridOrder(o.Keys.Where(k => k != GridKey.Tag).ToList(), o.RowBreak - (at < o.RowBreak ? 1 : 0)).Normalized();
        }
        if (a.Layout != b.Layout || a.Spacing != b.Spacing || a.Trim != b.Trim || a.PowerOfTwo != b.PowerOfTwo || a.SplitParts != b.SplitParts) return false;
        if (Effective(a.Order) != Effective(b.Order)) return false;
        if (a.Layout == SheetLayout.Grid && (a.Grid != b.Grid || (a.Grid != SheetGrid.Auto && a.GridCount != b.GridCount))) return false;
        if (a.Layout == SheetLayout.Packed && (a.MaxWidth != b.MaxWidth || a.PackOrder != b.PackOrder)) return false;
        if (tags && (a.SplitTags != b.SplitTags || a.Naming != b.Naming)) return false;
        if (!a.ExcludedLayers.SequenceEqual(b.ExcludedLayers)) return false;
        if (skins)
        {
            if (a.SplitSkins != b.SplitSkins || a.BakeInheritedSkins != b.BakeInheritedSkins || a.Skins != b.Skins) return false;
            if (a.Skins == SkinSource.Selected && !a.ExcludedSkins.ToHashSet().SetEquals(b.ExcludedSkins)) return false;
        }
        return true;
    }

    // ───────────── JSON 형식 ─────────────

    /// <summary>버전 1 프로젝트 종류(읽기 전용, 아틀라스면 부위 레이어로 변환).</summary>
    private enum V1Kind
    {
        Animation,
        Atlas,
    }

    private sealed class ProjectDto
    {
        public int Version { get; set; } = CurrentVersion;
        /// <summary>버전 1 전용(쓰지 않음).</summary>
        public V1Kind? Kind { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>버전 1 아틀라스 전용: 가져온 원래 시트 크기.</summary>
        public int? AtlasSourceWidth { get; set; }
        public int? AtlasSourceHeight { get; set; }
        /// <summary>버전 1 아틀라스 전용: 스프라이트.</summary>
        public List<SpriteDto>? Sprites { get; set; }
        public List<LayerDto>? Layers { get; set; }
        public List<FrameDto>? Frames { get; set; }
        public List<TagDto>? Tags { get; set; }
        public UnityDto? Unity { get; set; }
        public ExportDto? Export { get; set; }
        /// <summary>마지막으로 내보낸 결과 파일(없으면 기록 없음 — 예전 파일).</summary>
        public ExportRecordDto? Exported { get; set; }
        /// <summary>착장 프리셋(없으면 없음 — 예전 파일).</summary>
        public List<OutfitDto>? Outfits { get; set; }
        /// <summary>스크립트 표: 원문 해시 → 원문(스크립트 효과가 쓰는 것만, 없으면 스크립트 효과 없음).</summary>
        public SortedDictionary<string, string>? Scripts { get; set; }
    }

    /// <summary>착장 프리셋: 이름, 그룹마다 스킨(레이어 = 그룹 번호), 부위 재정의(레이어 = 부위 번호, 스킨 = 그 그룹의 스킨 번호).</summary>
    private sealed class OutfitDto
    {
        public string? Name { get; set; }
        public List<SkinRefDto>? Slots { get; set; }
        public List<SkinRefDto>? Parts { get; set; }
    }

    /// <summary>(레이어 번호, 그 레이어(부위면 그 그룹)의 스킨 번호).</summary>
    internal sealed class SkinRefDto
    {
        public int Layer { get; set; } = -1;
        public int Skin { get; set; } = -1;
    }

    /// <summary>내보내기 기록: 기록한 프로젝트 파일 이름과 그 파일 기준 상대 경로(<c>/</c> 구분).</summary>
    private sealed class ExportRecordDto
    {
        public string Owner { get; set; } = "";
        public List<string>? Files { get; set; }
    }

    private sealed class LayerDto
    {
        public string Name { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Locked { get; set; }
        public int Opacity { get; set; } = 255;
        /// <summary>null = 일반.</summary>
        public LayerKind? Kind { get; set; }
        /// <summary>부위: 그룹 레이어 번호.</summary>
        public int? Parent { get; set; }
        /// <summary>그룹: 타임라인에서 접힘.</summary>
        public bool? Collapsed { get; set; }
        /// <summary>타임라인에서 효과 줄을 접음.</summary>
        public bool? EffectsCollapsed { get; set; }
        public int? PartWidth { get; set; }
        public int? PartHeight { get; set; }
        public int? PartX { get; set; }
        public int? PartY { get; set; }
        public PivotDto? PartPivot { get; set; }
        /// <summary>부위 테두리 [왼쪽, 위, 오른쪽, 아래].</summary>
        public int[]? PartBorder { get; set; }
        /// <summary>부위의 가져온 원래 칸 [x, y, w, h](왼쪽 위 원점).</summary>
        public int[]? PartSource { get; set; }
        /// <summary>그룹: 스킨 이름(순서대로, 첫 스킨 = 기본). 없으면(예전 파일) "기본" 하나.</summary>
        public List<string>? Skins { get; set; }
        /// <summary>그룹: 스킨마다 부위 전용 스킨의 주인 부위 번호(−1 = 전체 스킨). 없으면 모두 전체 스킨.</summary>
        public List<int>? SkinOwners { get; set; }
        /// <summary>그룹: 지금 스킨 번호(없으면 0).</summary>
        public int? ActiveSkin { get; set; }
        /// <summary>부위: 그룹 스킨 대신 보일 스킨 번호(재정의, 없으면 그룹을 따름).</summary>
        public int? Skin { get; set; }
        /// <summary>레이어 효과(적용 순서대로). 없으면 효과 없음.</summary>
        public List<JsonObject?>? Effects { get; set; }
        /// <summary>그룹: 자동 배치 설정. 없으면 끔.</summary>
        public GroupLayoutDto? AutoLayout { get; set; }
    }

    /// <summary>그룹 자동 배치(<see cref="GroupLayout"/>). 없는 값은 기본값.</summary>
    internal sealed class GroupLayoutDto
    {
        public GroupLayoutDirection Direction { get; set; } = GroupLayoutDirection.Row;
        public int? Columns { get; set; }
        public LayoutAlign Align { get; set; } = LayoutAlign.Center;
        public LayoutDistribution Distribution { get; set; } = LayoutDistribution.Center;
        public int Gap { get; set; } = 1;
        public int Margin { get; set; }
        public GroupLayoutOrder Order { get; set; } = GroupLayoutOrder.TopFirst;
        public bool IncludeHidden { get; set; } = true;
        /// <summary>배치 영역 [x, y, w, h]. 없으면 캔버스 전체.</summary>
        public int[]? Area { get; set; }

        public static GroupLayoutDto From(GroupLayout l) => new()
        {
            Direction = l.Direction,
            Columns = l.Direction == GroupLayoutDirection.Grid ? l.Columns : null,
            Align = l.Align,
            Distribution = l.Distribution,
            Gap = l.Gap,
            Margin = l.Margin,
            Order = l.Order,
            IncludeHidden = l.IncludeHidden,
            Area = l.Area is { } a ? [a.X, a.Y, a.Width, a.Height] : null,
        };

        public GroupLayout ToLayout() => new GroupLayout
        {
            Direction = Direction,
            Columns = Columns ?? 2,
            Align = Align,
            Distribution = Distribution,
            Gap = Gap,
            Margin = Margin,
            Order = Order,
            IncludeHidden = IncludeHidden,
            Area = Area is { Length: >= 4 } a ? new Rectangle(a[0], a[1], a[2], a[3]) : null,
        }.Normalized();
    }

    /// <summary>색 설정의 그라데이션 저장 형식(기존 단색 속성은 그대로 유지).</summary>
    internal sealed class ColorGradientDto
    {
        public int Angle { get; set; }
        public string? Shape { get; set; }
        public bool Animated { get; set; }
        public bool Reverse { get; set; }
        public int Cycles { get; set; } = 1;
        public string? Interpolation { get; set; }
        public string? Dither { get; set; }
        public string? ColorSpace { get; set; }
        public ColorStopDto[]? Stops { get; set; }
        public AlphaStopDto[]? AlphaStops { get; set; }
        public bool StraightRgba { get; set; }
        /// <summary>Animated speed curve; null = linear.</summary>
        public string? Easing { get; set; }
        /// <summary>Animated forward-and-back; null = restart each cycle.</summary>
        public bool? PingPong { get; set; }

        public static ColorGradientDto From(EffectColorGradient gradient) => new()
        {
            Angle = gradient.Angle,
            Shape = gradient.Shape.ToString(),
            Animated = gradient.Animated,
            Reverse = gradient.Reverse,
            Cycles = gradient.Cycles,
            Interpolation = gradient.Interpolation.ToString(),
            Dither = gradient.Dither.ToString(),
            ColorSpace = gradient.ColorSpace.ToString(),
            StraightRgba = gradient.StraightRgba,
            Easing = gradient.Easing == EffectEasing.Linear ? null : gradient.Easing.ToString(),
            PingPong = gradient.PingPong ? true : null,
            Stops = gradient.Stops.Select(s => new ColorStopDto { Position = s.Position, Color = s.Color.ToHex() }).ToArray(),
            AlphaStops = gradient.AlphaStops.Select(s => new AlphaStopDto { Position = s.Position, Alpha = s.Alpha }).ToArray(),
        };

        public EffectColorGradient ToGradient() => new()
        {
            Angle = Angle,
            Shape = Enum.TryParse<EffectGradientShape>(Shape, true, out var shape) && Enum.IsDefined(shape)
                ? shape : EffectGradientShape.Linear,
            Animated = Animated,
            Reverse = Reverse,
            Cycles = Math.Clamp(Cycles, 1, 16),
            Interpolation = Enum.TryParse<Gpixel.Core.Edit.GradientInterpolation>(Interpolation, true, out var mode) && Enum.IsDefined(mode)
                ? mode : Gpixel.Core.Edit.GradientInterpolation.Smooth,
            Dither = Enum.TryParse<Gpixel.Core.Edit.GradientDither>(Dither, true, out var dither) && Enum.IsDefined(dither)
                ? dither : Gpixel.Core.Edit.GradientDither.None,
            ColorSpace = Enum.TryParse<GradientColorSpace>(ColorSpace, true, out var colorSpace) && Enum.IsDefined(colorSpace)
                ? colorSpace : GradientColorSpace.Rgb,
            StraightRgba = StraightRgba,
            Easing = Enum.TryParse<EffectEasing>(Easing, true, out var easing) && Enum.IsDefined(easing)
                ? easing : EffectEasing.Linear,
            PingPong = PingPong ?? false,
            Stops = Stops?.Where(s => s is not null && Color32.TryParseHex(s.Color, out _))
                .Select(s => new Gpixel.Core.Edit.GradientStop(s.Position, Color32.TryParseHex(s.Color, out var c) ? c : Color32.White)).ToArray()
                ?? [],
            AlphaStops = AlphaStops?.Where(s => s is not null).Select(s => new Gpixel.Core.Edit.GradientAlphaStop(s.Position, s.Alpha)).ToArray() ?? [],
        };
    }

    internal sealed class ColorStopDto
    {
        public double Position { get; set; }
        public string? Color { get; set; }
    }

    internal sealed class AlphaStopDto
    {
        public double Position { get; set; }
        public byte Alpha { get; set; }
    }

    private sealed class FrameDto
    {
        public int DurationMs { get; set; } = Frame.DefaultDurationMs;
        public PivotDto? Pivot { get; set; }
        public string? Name { get; set; }
    }

    private sealed class TagDto
    {
        public string Name { get; set; } = "";
        public int From { get; set; }
        public int To { get; set; }
        public TagDirection Direction { get; set; } = TagDirection.Forward;
        public bool Loop { get; set; } = true;
        public string? Color { get; set; }
    }

    internal sealed class UnityDto
    {
        public SpriteMode SpriteMode { get; set; } = SpriteMode.Multiple;
        public float PixelsPerUnit { get; set; } = SpriteSettings.DefaultPixelsPerUnit;
        public PivotDto? DefaultPivot { get; set; }
        /// <summary>Single 모드 테두리 [왼쪽, 위, 오른쪽, 아래].</summary>
        public int[]? Border { get; set; }
        public bool FullRect { get; set; } = true;
        public bool Readable { get; set; }
        /// <summary>Unity 텍스처 가져오기 설정(없으면 기본값 — 예전 파일).</summary>
        public TextureDto? Texture { get; set; }

        public static UnityDto From(SpriteSettings s) => new()
        {
            SpriteMode = s.Mode,
            PixelsPerUnit = s.PixelsPerUnit,
            DefaultPivot = PivotDto.From(s.DefaultPivot),
            Border = s.Border.IsNone ? null : [s.Border.Left, s.Border.Top, s.Border.Right, s.Border.Bottom],
            FullRect = s.FullRectMesh,
            Readable = s.Readable,
            Texture = TextureDto.From(s),
        };

        public void ApplyTo(SpriteSettings s)
        {
            s.Mode = SpriteMode;
            s.PixelsPerUnit = PixelsPerUnit;
            s.DefaultPivot = DefaultPivot?.ToPivot() ?? Pivot.Center;
            s.Border = Border is { Length: >= 4 } b ? new SpriteBorder(b[0], b[1], b[2], b[3]) : SpriteBorder.None;
            s.FullRectMesh = FullRect;
            s.Readable = Readable;
            Texture?.ApplyTo(s);
        }
    }

    /// <summary>Unity 인스펙터 항목(Extrude·Physics·Advanced·Wrap/Filter·Default 플랫폼).</summary>
    internal sealed class TextureDto
    {
        public int ExtrudeEdges { get; set; } = 1;
        public bool GeneratePhysicsShape { get; set; } = true;
        public bool SRgb { get; set; } = true;
        public SpriteAlphaSource AlphaSource { get; set; } = SpriteAlphaSource.InputTextureAlpha;
        public bool AlphaIsTransparency { get; set; } = true;
        public bool GenerateMipMaps { get; set; }
        public TextureWrap WrapMode { get; set; } = TextureWrap.Clamp;
        public TextureFilter FilterMode { get; set; } = TextureFilter.Point;
        public int AnisoLevel { get; set; } = 1;
        public int MaxTextureSize { get; set; }
        public TextureResize ResizeAlgorithm { get; set; } = TextureResize.Mitchell;
        public TextureFormatChoice Format { get; set; } = TextureFormatChoice.Automatic;
        public TextureCompressionChoice Compression { get; set; } = TextureCompressionChoice.None;
        public bool CrunchCompression { get; set; }
        public int CompressionQuality { get; set; } = 50;

        public static TextureDto From(SpriteSettings s) => new()
        {
            ExtrudeEdges = s.ExtrudeEdges, GeneratePhysicsShape = s.GeneratePhysicsShape, SRgb = s.SRgb, AlphaSource = s.AlphaSource,
            AlphaIsTransparency = s.AlphaIsTransparency, GenerateMipMaps = s.GenerateMipMaps, WrapMode = s.WrapMode, FilterMode = s.FilterMode,
            AnisoLevel = s.AnisoLevel, MaxTextureSize = s.MaxTextureSize, ResizeAlgorithm = s.ResizeAlgorithm, Format = s.Format,
            Compression = s.Compression, CrunchCompression = s.CrunchCompression, CompressionQuality = s.CompressionQuality,
        };

        public void ApplyTo(SpriteSettings s)
        {
            s.ExtrudeEdges = ExtrudeEdges;
            s.GeneratePhysicsShape = GeneratePhysicsShape;
            s.SRgb = SRgb;
            s.AlphaSource = Enum.IsDefined(AlphaSource) ? AlphaSource : SpriteAlphaSource.InputTextureAlpha;
            s.AlphaIsTransparency = AlphaIsTransparency;
            s.GenerateMipMaps = GenerateMipMaps;
            s.WrapMode = Enum.IsDefined(WrapMode) ? WrapMode : TextureWrap.Clamp;
            s.FilterMode = Enum.IsDefined(FilterMode) ? FilterMode : TextureFilter.Point;
            s.AnisoLevel = AnisoLevel;
            s.MaxTextureSize = MaxTextureSize;
            s.ResizeAlgorithm = Enum.IsDefined(ResizeAlgorithm) ? ResizeAlgorithm : TextureResize.Mitchell;
            s.Format = Enum.IsDefined(Format) ? Format : TextureFormatChoice.Automatic;
            s.Compression = Enum.IsDefined(Compression) ? Compression : TextureCompressionChoice.None;
            s.CrunchCompression = CrunchCompression;
            s.CompressionQuality = CompressionQuality;
        }
    }

    internal sealed class ExportDto
    {
        public bool OnSave { get; set; } = true;
        public string? Folder { get; set; }
        public string? FileName { get; set; }
        public bool ExportFrames { get; set; } = true;
        public bool ExportParts { get; set; } = true;
        public ExportImageFormat ImageFormat { get; set; } = ExportImageFormat.Png;
        public int JpegQuality { get; set; } = 90;
        public string? PartsFileName { get; set; }
        public int SingleFrame { get; set; }
        public bool? WriteMeta { get; set; }
        /// <summary>Sprite Library(.spriteLib) 같이 쓰기(없으면 꺼짐).</summary>
        public bool? SpriteLibrary { get; set; }
        public bool KeepImportedRects { get; set; } = true;
        /// <summary>두 결과가 같이 쓰는 시트 설정(없으면 예전 형식 — 아래 읽기 전용 값들에서 옮김).</summary>
        public SheetDto? Sheet { get; set; }
        /// <summary>부위 아틀라스만 다른 시트 설정(없으면 공통).</summary>
        public SheetDto? PartsSheet { get; set; }

        // ── 예전 형식(읽기 전용, 쓰지 않음) ──
        /// <summary>예전 프레임 시트 배치("Columns" / "RowPerTag").</summary>
        public string? Layout { get; set; }
        /// <summary>예전 파일의 프레임 시트 열 수(0 = 한 줄).</summary>
        public int? Columns { get; set; }
        public SheetGrid? FrameGrid { get; set; }
        public int? FrameGridCount { get; set; }
        public bool? SplitFrames { get; set; }
        public int? Spacing { get; set; }
        public bool? Trim { get; set; }
        public int? AtlasMaxWidth { get; set; }
        public bool? AtlasPowerOfTwo { get; set; }
        public AtlasPackOrder? AtlasOrder { get; set; }
        /// <summary>예전 부위 아틀라스 배치("Packed" / "RowPerPart" / "RowPerFrame" / "RowPerPartTag").</summary>
        public string? PartsLayout { get; set; }
        public SheetGrid? PartsGrid { get; set; }
        public int? PartsGridCount { get; set; }
        public bool? SplitParts { get; set; }
        public bool? SplitSkins { get; set; }
        public bool? FrameCurrentOutfit { get; set; }
        public List<int>? FrameOutfits { get; set; }
        public List<int>? FrameExcludedLayers { get; set; }
        public List<SkinRefDto>? ExcludedSkins { get; set; }
        public bool? BakeInheritedSkins { get; set; }
        public SpriteNaming? PartsNaming { get; set; }

        /// <param name="p">시트 설정의 스킨·레이어·착장을 파일 안 번호로 적을 프로젝트. null이면 적지 않는다(실행취소 기록은 따로 적음).</param>
        public static ExportDto From(ExportSettings x, SpriteProject? p) => new()
        {
            OnSave = x.ExportOnSave,
            Folder = x.Folder,
            FileName = x.FileName,
            ExportFrames = x.ExportFrames,
            ExportParts = x.ExportParts,
            PartsFileName = x.PartsFileName,
            ImageFormat = x.ImageFormat,
            JpegQuality = x.JpegQuality,
            SingleFrame = x.SingleFrameIndex,
            WriteMeta = x.WriteMeta,
            SpriteLibrary = x.WriteSpriteLibrary ? true : null,
            KeepImportedRects = x.KeepImportedRects,
            Sheet = SheetDto.From(p, x.Common),
            PartsSheet = x.PartsOverride is { } po ? SheetDto.From(p, po) : null,
        };
    }

    /// <summary>시트 설정 한 벌(<see cref="SheetOptions"/>). 스킨·레이어·착장은 파일 안 번호.</summary>
    internal sealed class SheetDto
    {
        public SheetLayout Layout { get; set; } = SheetLayout.Grid;
        public List<GridKey>? Order { get; set; }
        public int RowBreak { get; set; } = 2;
        public SheetGrid Grid { get; set; } = SheetGrid.Auto;
        public int GridCount { get; set; } = 4;
        public int Spacing { get; set; } = 1;
        public bool Trim { get; set; }
        public bool PowerOfTwo { get; set; }
        public int MaxWidth { get; set; }
        public AtlasPackOrder PackOrder { get; set; } = AtlasPackOrder.KeepOrder;
        public bool SplitTags { get; set; }
        public bool SplitSkins { get; set; }
        public bool SplitParts { get; set; }
        public SpriteNaming Naming { get; set; } = SpriteNaming.ByTag;
        public SkinSource Skins { get; set; } = SkinSource.Current;
        public List<SkinRefDto>? ExcludedSkins { get; set; }
        public List<int>? Outfits { get; set; }
        public List<int>? ExcludedLayers { get; set; }
        public bool? BakeInheritedSkins { get; set; }

        /// <param name="p">스킨·레이어·착장을 파일 안 번호로 적을 프로젝트. null이면 값만 적는다.</param>
        public static SheetDto From(SpriteProject? p, SheetOptions o) => new()
        {
            Layout = o.Layout,
            Order = o.Order.Keys.ToList(),
            RowBreak = o.Order.RowBreak,
            Grid = o.Grid,
            GridCount = o.GridCount,
            Spacing = o.Spacing,
            Trim = o.Trim,
            PowerOfTwo = o.PowerOfTwo,
            MaxWidth = o.MaxWidth,
            PackOrder = o.PackOrder,
            SplitTags = o.SplitTags,
            SplitSkins = o.SplitSkins,
            SplitParts = o.SplitParts,
            Naming = o.Naming,
            Skins = o.Skins,
            ExcludedSkins = p is null ? null : NullIfEmpty(o.ExcludedSkins.Select(s => GroupSkinRef(p, s)).OfType<SkinRefDto>()),
            Outfits = p is null ? null : NullIfEmpty(o.Outfits.Select(x => IndexOf(p.Outfits, x)).Where(i => i >= 0)),
            ExcludedLayers = p is null ? null : NullIfEmpty(o.ExcludedLayers.Select(p.IndexOf).Where(i => i >= 0)),
            BakeInheritedSkins = o.BakeInheritedSkins ? true : null,
        };

        public void ApplyTo(SheetOptions o, SpriteProject p, List<Layer> fileLayers)
        {
            ApplyValues(o);
            o.ExcludedSkins = (ExcludedSkins ?? []).Select(r => ResolveSkin(fileLayers, r)).OfType<Skin>().Distinct().ToArray();
            o.Outfits = (Outfits ?? []).Where(i => i >= 0 && i < p.Outfits.Count).Select(i => p.Outfits[i]).Distinct().ToArray();
            o.ExcludedLayers = (ExcludedLayers ?? []).Select(i => ResolveLayer(fileLayers, i)).OfType<Layer>().Where(l => !l.IsPart).Distinct().ToArray();
        }

        /// <summary>스킨·레이어·착장 목록을 뺀 값만 적용한다.</summary>
        public void ApplyValues(SheetOptions o)
        {
            o.Layout = Enum.IsDefined(Layout) ? Layout : SheetLayout.Grid;
            o.Order = new GridOrder((Order ?? [GridKey.Part, GridKey.Skin, GridKey.Frame]).Where(Enum.IsDefined).ToList(), RowBreak);
            o.Grid = Enum.IsDefined(Grid) ? Grid : SheetGrid.Auto;
            o.GridCount = GridCount;
            o.Spacing = Spacing;
            o.Trim = Trim;
            o.PowerOfTwo = PowerOfTwo;
            o.MaxWidth = MaxWidth;
            o.PackOrder = Enum.IsDefined(PackOrder) ? PackOrder : AtlasPackOrder.KeepOrder;
            o.SplitTags = SplitTags;
            o.SplitSkins = SplitSkins;
            o.SplitParts = SplitParts;
            o.Naming = Enum.IsDefined(Naming) ? Naming : SpriteNaming.ByTag;
            o.Skins = Enum.IsDefined(Skins) ? Skins : SkinSource.Current;
            o.BakeInheritedSkins = BakeInheritedSkins == true;
        }
    }

    /// <summary>버전 1 아틀라스 스프라이트(읽기 전용).</summary>
    private sealed class SpriteDto
    {
        public string Name { get; set; } = "sprite";
        public int Width { get; set; }
        public int Height { get; set; }
        public PivotDto? Pivot { get; set; }
        /// <summary>테두리 [왼쪽, 위, 오른쪽, 아래].</summary>
        public int[]? Border { get; set; }
        /// <summary>가져온 원래 칸 [x, y, w, h](왼쪽 위 원점).</summary>
        public int[]? Source { get; set; }
    }

    internal sealed class PivotDto
    {
        public float X { get; set; } = 0.5f;
        public float Y { get; set; } = 0.5f;
        public static PivotDto From(Pivot p) => new() { X = p.X, Y = p.Y };
        public Pivot ToPivot() => float.IsFinite(X) && float.IsFinite(Y) ? new Pivot(X, Y) : Pivot.Center;
    }
}
