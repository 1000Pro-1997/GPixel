using System.Text.Json;
using Gpixel.Core.Model;

namespace Gpixel.Core.IO;

/// <summary>문서 열기 결과.</summary>
/// <param name="Document">열린 문서(수정됨 아님).</param>
/// <param name="Source">스프라이트 정보를 어디서 읽었는지.</param>
/// <param name="Warning">사이드카가 깨져 메타/기본값을 쓴 경우 등 사용자에게 알릴 말. 없으면 null.</param>
public sealed record DocumentLoadResult(Document Document, DocumentInfoSource Source, string? Warning);

/// <summary>스프라이트 설정을 읽어 온 곳.</summary>
public enum DocumentInfoSource
{
    /// <summary>사이드카도 메타도 없어 기본값을 씀.</summary>
    Defaults,
    /// <summary>사이드카(<c>.png.gpixel.json</c>).</summary>
    Sidecar,
    /// <summary>Unity 메타(사이드카 없음) — 기존 Unity 리소스.</summary>
    UnityMeta,
}

/// <summary>
/// 문서 열기/저장 흐름: PNG → 사이드카 → (켜져 있으면) Unity 메타. 이름 바꾸기 시 딸린 파일도 함께 옮긴다.
/// </summary>
public static class DocumentIO
{
    /// <summary>
    /// PNG를 연다. 사이드카가 있으면 그 설정을, 없고 메타가 있으면 메타의 칸·피벗을, 둘 다 없으면
    /// <paramref name="defaults"/>(없으면 기본 설정)를 쓴다. PNG를 읽을 수 없으면 예외.
    /// </summary>
    public static DocumentLoadResult Load(string pngPath, SpriteSettings? defaults = null)
    {
        var full = Path.GetFullPath(pngPath);
        var doc = new Document(PngIO.Load(full), full);
        if (defaults is not null) doc.Sprite = defaults.Clone();
        string? warning = null;

        if (File.Exists(SidecarIO.GetPath(full)))
        {
            try
            {
                SidecarIO.TryLoad(full, doc);
                doc.IsDirty = false;
                return new DocumentLoadResult(doc, DocumentInfoSource.Sidecar, null);
            }
            catch (JsonException ex)
            {
                warning = Gpixel.Core.Localization.DomainStrings.FormatDocument_IgnoreSidecar(error: ex.Message);
                doc.Slices.Clear();
                doc.Animations.Clear();
            }
        }

        var meta = UnityMeta.TryRead(UnityMeta.GetMetaPath(full));
        if (meta is not null && meta.TextureType == 8)
        {
            meta.ApplyTo(doc);
            if (doc.Slices.Count > 0)
            {
                doc.Grid.CellW = doc.Slices[0].Rect.Width;
                doc.Grid.CellH = doc.Slices[0].Rect.Height;
            }
            doc.IsDirty = false;
            return new DocumentLoadResult(doc, DocumentInfoSource.UnityMeta, warning);
        }

        doc.IsDirty = false;
        return new DocumentLoadResult(doc, DocumentInfoSource.Defaults, warning);
    }

    /// <summary>
    /// 문서를 저장한다: 이미지(기본 PNG, <paramref name="format"/>) → <paramref name="writeMeta"/>면 Unity 메타. 같은 이름의 예전 사이드카는 지운다.
    /// 성공하면 경로를 갱신하고 수정됨 표시를 끈다. 실패하면 예외를 그대로 던지고 수정됨 상태를 유지한다.
    /// </summary>
    /// <param name="pngPath">저장 경로. null이면 <see cref="Document.Path"/>.</param>
    public static void Save(Document doc, string? pngPath, bool writeMeta, ExportImageFormat format = ExportImageFormat.Png, int jpegQuality = 90)
        => Save(doc, pngPath, writeMeta, format, jpegQuality, null);

    /// <param name="stage">이미지 쓰기 진행(단위: 픽셀).</param>
    internal static void Save(Document doc, string? pngPath, bool writeMeta, ExportImageFormat format, int jpegQuality, WorkStage? stage)
    {
        var path = pngPath ?? doc.Path ?? throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.Document_NoSavePath);
        path = Path.GetFullPath(path);
        ImageWriter.Save(doc.Image, path, format, jpegQuality, stage);
        // 예전 버전이 쓴 사이드카는 이제 낡은 정보이고 열 때 메타보다 먼저 읽히므로 지운다.
        var sidecar = SidecarIO.GetPath(path);
        if (File.Exists(sidecar)) File.Delete(sidecar);
        doc.Path = path;
        if (writeMeta) UnityMeta.Write(doc, path);
        doc.IsDirty = false;
    }

    /// <summary>PNG에 딸린 파일 경로들(존재 여부와 무관): 메타, 사이드카.</summary>
    public static string[] GetCompanionPaths(string pngPath)
        => [UnityMeta.GetMetaPath(pngPath), SidecarIO.GetPath(pngPath)];

    /// <summary>
    /// PNG 이름을 바꾸며 같은 이름의 <c>.meta</c>와 사이드카도 함께 옮긴다(guid 유지).
    /// 대상 PNG가 이미 있으면 <see cref="IOException"/>.
    /// </summary>
    public static void RenameWithCompanions(string oldPngPath, string newPngPath)
    {
        if (File.Exists(newPngPath)) throw new IOException(Gpixel.Core.Localization.DomainStrings.FormatFile_NameExists(name: Path.GetFileName(newPngPath)));
        File.Move(oldPngPath, newPngPath);
        var oldCompanions = GetCompanionPaths(oldPngPath);
        var newCompanions = GetCompanionPaths(newPngPath);
        for (int i = 0; i < oldCompanions.Length; i++)
            if (File.Exists(oldCompanions[i])) File.Move(oldCompanions[i], newCompanions[i], overwrite: true);
    }
}
