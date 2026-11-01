using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.IO;

/// <summary>
/// 실행취소 기록 파일(문서 하나 = 폴더 하나). 프로젝트 파일(<c>.gpixel</c>)과 따로 두고, 그 파일에 저장된 상태(= 지금 상태)를 기준으로 읽는다.
/// <list type="bullet">
/// <item><c>history.json.gz</c>: 형식 버전, 기준 상태(<c>anchor</c>), 셀 그림 목록, 실행취소 목록(오래된 것부터), 다시 실행 목록(먼 것부터),
/// 기록의 상태들이 쓰는 스크립트 원문(<c>scripts</c>, 형식 3부터 — 프로젝트 파일은 지금 효과가 쓰는 원문만 저장하므로).
/// 단계는 <c>snapshot</c>(구조 변경 앞뒤 상태), <c>stroke</c>(셀 하나의 픽셀 패치), <c>group</c>(여러 단계를 한 단계로).</item>
/// <item><c>blobs/{해시}</c>: 내용 주소 저장소(zlib 압축, 이름 = 압축 전 내용의 SHA-256 앞 32자). 구조 상태 JSON·픽셀 패치·셀 그림이 들어가며,
/// 같은 내용은 한 번만 저장된다(구조 단계 1000개가 같은 셀을 가리켜도 그림은 하나).</item>
/// </list>
/// 레이어·스킨·프레임·태그·착장·셀 그림은 기록 안에서 번호로 가리킨다. 기준 상태에 있는 것은 읽을 때 방금 연 프로젝트의 같은 자리 객체에 이어 붙이고,
/// 지금 프로젝트에 없는 셀 그림(지운 레이어 등)만 내용을 저장한다. 쓰기는 임시 파일에 쓴 뒤 바꿔치기한다.
/// </summary>
public static class UndoHistoryIO
{
    /// <summary>
    /// 3 = 기록의 상태들이 쓰는 스크립트 원문(<c>scripts</c>: 해시 → 원문)을 함께 담는 판. 2 = 레이어 효과를 프로젝트 형식 4와 같은 JSON
    /// (<see cref="Effects.EffectCodec"/>)으로 쓰는 판. 1·2판도 읽는다(원문 없음 — 프로젝트 표에 없는 원문의 효과는 원문 없음으로 남음).
    /// </summary>
    public const int FormatVersion = 3;
    public const string ManifestName = "history.json.gz";
    public const string BlobFolder = "blobs";

    internal static readonly JsonSerializerOptions Json = new(ProjectIO.Options) { WriteIndented = false };

    /// <summary>
    /// 지금 기록의 사본을 뜬다(UI 스레드에서 — 바로 끝남). 그리는 중이면 null. 실행취소·다시 실행 목록은 각각 <paramref name="maxSteps"/>단계까지
    /// (실행취소는 오래된 것부터, 다시 실행은 먼 것부터 버림). 결과는 <see cref="Write"/>로 아무 스레드에서나 쓸 수 있다.
    /// </summary>
    public static UndoHistoryCapture? Capture(SpriteProject project, int maxSteps) => project.CaptureHistory(maxSteps);

    /// <summary>기록을 <paramref name="directory"/>에 쓴다(없으면 만듦). 더 쓰이지 않는 조각 파일은 지운다.</summary>
    public static UndoHistoryWriteResult Write(UndoHistoryCapture capture, string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, BlobFolder));
        var blobs = new HistoryBlobs(directory);
        var manifest = capture.Project.BuildHistoryManifest(capture, blobs);
        WriteAtomic(Path.Combine(directory, ManifestName), Compress(JsonSerializer.SerializeToUtf8Bytes(manifest, Json)));
        blobs.DeleteUnused();
        return new UndoHistoryWriteResult(capture.UndoCount, capture.RedoCount, DirectorySize(directory));
    }

    /// <summary>
    /// <paramref name="directory"/>의 기록을 방금 연 <paramref name="project"/>의 실행취소 기록으로 읽는다(지금 기록은 바뀜).
    /// 실행취소 단계가 <see cref="UndoStack.Capacity"/>보다 많으면 오래된 것부터 버린다. 읽은 단계 수(실행취소 + 다시 실행)를 돌려준다.
    /// 파일이 손상됐거나 프로젝트와 맞지 않으면 <see cref="InvalidDataException"/>(프로젝트·기록은 그대로).
    /// </summary>
    public static int Read(SpriteProject project, string directory)
    {
        HistoryManifestDto manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<HistoryManifestDto>(Decompress(File.ReadAllBytes(Path.Combine(directory, ManifestName))), Json)
                       ?? throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_Empty);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.FormatHistory_CorruptMessage(error: ex.Message), ex);
        }
        if (manifest.Version is < 1 or > FormatVersion)
            throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.FormatHistory_UnsupportedVersion(version: manifest.Version));
        return project.LoadHistory(manifest, new HistoryBlobs(directory));
    }

    /// <summary>폴더 안 파일 크기 합(바이트). 없거나 읽지 못하면 0.</summary>
    public static long DirectorySize(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    internal static byte[] Decompress(byte[] data)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.FormatHistory_CorruptChunkMessage(error: ex.Message), ex);
        }
    }

    /// <summary>임시 파일에 쓴 뒤 바꿔치기한다.</summary>
    internal static void WriteAtomic(string path, byte[] data)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private const int ImageMagic = 0x31495047; // "GPI1"
    private const int PatchMagic = 0x31505047; // "GPP1"

    /// <summary>셀 그림: 머리(형식·가로·세로) + RGBA 픽셀 그대로.</summary>
    internal static byte[] EncodeImage(int width, int height, Color32[] pixels)
    {
        using var ms = new MemoryStream(12 + pixels.Length * 4);
        using (var w = new BinaryWriter(ms))
        {
            w.Write(ImageMagic);
            w.Write(width);
            w.Write(height);
            w.Write(MemoryMarshal.AsBytes(pixels.AsSpan()));
        }
        return ms.ToArray();
    }

    internal static PixelImage DecodeImage(byte[] data, int width, int height)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        try
        {
            if (r.ReadInt32() != ImageMagic || r.ReadInt32() != width || r.ReadInt32() != height || data.Length != 12 + width * height * 4)
                throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_CorruptCellImage);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_CorruptCellImage, ex);
        }
        var pixels = new Color32[width * height];
        data.AsSpan(12).CopyTo(MemoryMarshal.AsBytes(pixels.AsSpan()));
        return new PixelImage(width, height, pixels);
    }

    /// <summary>픽셀 패치: 머리 + 바뀐 픽셀 번호(앞 번호와의 차, 7비트 가변 길이) + 이전 값 + 이후 값.</summary>
    internal static byte[] EncodePatch(PixelPatch patch)
    {
        var (width, height, indices, before, after) = patch.Parts;
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(PatchMagic);
            w.Write(width);
            w.Write(height);
            w.Write(indices.Length);
            int prev = 0;
            foreach (var i in indices)
            {
                w.Write7BitEncodedInt(i - prev);
                prev = i;
            }
            w.Write(MemoryMarshal.AsBytes(before.AsSpan()));
            w.Write(MemoryMarshal.AsBytes(after.AsSpan()));
        }
        return ms.ToArray();
    }

    internal static PixelPatch DecodePatch(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            if (r.ReadInt32() != PatchMagic) throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_CorruptPixelPatch);
            int width = r.ReadInt32(), height = r.ReadInt32(), count = r.ReadInt32();
            if (width < 1 || height < 1 || width > PixelImage.MaxSize || height > PixelImage.MaxSize || count < 1 || count > width * height)
                throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_CorruptPixelPatch);
            var indices = new int[count];
            int prev = 0;
            for (int k = 0; k < count; k++) indices[k] = prev += r.Read7BitEncodedInt();
            var before = new Color32[count];
            var after = new Color32[count];
            if (r.Read(MemoryMarshal.AsBytes(before.AsSpan())) != count * 4 || r.Read(MemoryMarshal.AsBytes(after.AsSpan())) != count * 4)
                throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_CorruptPixelPatch);
            return PixelPatch.FromParts(width, height, indices, before, after);
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException)
        {
            throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_CorruptPixelPatch, ex);
        }
    }
}

/// <summary>쓴 기록: 실행취소·다시 실행 단계 수와 폴더 전체 크기(바이트).</summary>
public readonly record struct UndoHistoryWriteResult(int UndoSteps, int RedoSteps, long Bytes)
{
    public int Steps => UndoSteps + RedoSteps;
}

/// <summary><see cref="UndoHistoryIO.Capture"/>가 뜬 기록 사본(UI 스레드 밖에서 써도 안전 — 셀 그림은 지금 내용을 복사해 둠).</summary>
public sealed class UndoHistoryCapture
{
    internal UndoHistoryCapture(SpriteProject project, List<IUndoCommand> undo, List<IUndoCommand> redo, object anchor,
        List<(PixelImage Image, Color32[]? Pixels)> images, IReadOnlyDictionary<string, string> scripts)
    {
        Scripts = scripts;
        Project = project;
        Undo = undo;
        Redo = redo;
        Anchor = anchor;
        Images = images;
    }

    public SpriteProject Project { get; }
    internal List<IUndoCommand> Undo { get; }
    internal List<IUndoCommand> Redo { get; }
    /// <summary>지금 상태(프로젝트 파일에 저장된 상태) 사본.</summary>
    internal object Anchor { get; }
    /// <summary>기록이 가리키는 셀 그림: 지금 프로젝트에 있는 것(내용은 프로젝트 파일에 있음, null)과 없는 것(지금 내용 사본).</summary>
    internal List<(PixelImage Image, Color32[]? Pixels)> Images { get; }
    /// <summary>기록의 상태(기준 상태 포함)가 쓰는 스크립트 원문(해시 → 원문, 뜰 때 원문을 아는 것).</summary>
    internal IReadOnlyDictionary<string, string> Scripts { get; }

    public int UndoCount => Undo.Count;
    public int RedoCount => Redo.Count;
    public int StepCount => Undo.Count + Redo.Count;
}

/// <summary>기록 폴더의 내용 주소 저장소(<c>blobs/</c>). 이번 쓰기에서 쓰인 조각을 기억해 두었다가 나머지를 지운다.</summary>
internal sealed class HistoryBlobs(string directory)
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private string Folder { get; } = Path.Combine(directory, UndoHistoryIO.BlobFolder);

    private static string HashOf(ReadOnlySpan<byte> payload) => Convert.ToHexStringLower(SHA256.HashData(payload))[..32];

    private static bool IsHash(string? s) => s is { Length: 32 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string PathOf(string hash) => Path.Combine(Folder, hash);

    /// <summary>이미 저장된 조각이면 쓰임으로 표시하고 true.</summary>
    public bool TryUse(string hash)
    {
        if (!File.Exists(PathOf(hash))) return false;
        _used.Add(hash);
        return true;
    }

    /// <summary>조각을 저장하고(이미 있으면 그대로) 해시를 돌려준다.</summary>
    public string Put(byte[] payload)
    {
        var hash = HashOf(payload);
        if (TryUse(hash)) return hash;
        UndoHistoryIO.WriteAtomic(PathOf(hash), UndoHistoryIO.Compress(payload));
        _used.Add(hash);
        return hash;
    }

    /// <summary>조각 내용(내용이 해시와 다르면 손상으로 봄).</summary>
    public byte[] Get(string? hash)
    {
        if (!IsHash(hash)) throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_CorruptChunkName);
        byte[] payload;
        try
        {
            payload = UndoHistoryIO.Decompress(File.ReadAllBytes(PathOf(hash!)));
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_MissingChunkFile, ex);
        }
        if (HashOf(payload) != hash) throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.History_CorruptChunk);
        return payload;
    }

    /// <summary>이번 쓰기에서 쓰이지 않은 조각(과 남은 임시 파일)을 지운다. 지우지 못한 것은 다음에.</summary>
    public void DeleteUnused()
    {
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            if (_used.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

// ───────────── 기록 파일 JSON ─────────────

internal sealed class HistoryManifestDto
{
    public int Version { get; set; }
    /// <summary>지금 상태(프로젝트 파일에 저장된 상태) 조각.</summary>
    public string? Anchor { get; set; }
    public List<HistoryImageDto> Images { get; set; } = [];
    public List<HistoryStepDto> Undo { get; set; } = [];
    public List<HistoryStepDto> Redo { get; set; } = [];
    /// <summary>스크립트 원문(해시 → 원문, 형식 3부터, 없으면 null).</summary>
    public SortedDictionary<string, string>? Scripts { get; set; }
}

/// <summary>셀 그림: 번호·크기. <see cref="Blob"/>이 없으면 지금 프로젝트(기준 상태)의 셀.</summary>
internal sealed class HistoryImageDto
{
    public int Id { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string? Blob { get; set; }
}

internal sealed class HistoryStepDto
{
    public string Label { get; set; } = "";
    /// <summary><c>snapshot</c> · <c>stroke</c> · <c>group</c>.</summary>
    public string Type { get; set; } = "";
    // snapshot
    public string? Before { get; set; }
    public string? After { get; set; }
    public int Change { get; set; }
    // stroke(셀 키 = 레이어·프레임·스킨 번호, 스킨 0 = 첫 스킨·일반 레이어)
    public int Layer { get; set; }
    public int Frame { get; set; }
    public int Skin { get; set; }
    public int Image { get; set; }
    public bool Created { get; set; }
    public string? Patch { get; set; }
    // group
    public List<HistoryStepDto>? Items { get; set; }
}

/// <summary>구조 상태 하나(번호 0 = 없음).</summary>
internal sealed class HistorySnapshotDto
{
    public int W { get; set; }
    public int H { get; set; }
    public List<HistoryLayerDto> Layers { get; set; } = [];
    public List<HistoryFrameDto> Frames { get; set; } = [];
    public List<HistoryTagDto> Tags { get; set; } = [];
    public List<HistoryOutfitDto> Outfits { get; set; } = [];
    /// <summary>셀: [레이어, 프레임, 스킨(0 = 없음), 그림].</summary>
    public List<int[]> Cels { get; set; } = [];
    public ProjectIO.UnityDto Sprite { get; set; } = new();
    /// <summary>내보내기 설정(시트의 스킨·착장·레이어 목록은 <see cref="CommonRefs"/>·<see cref="PartsRefs"/>).</summary>
    public ProjectIO.ExportDto Export { get; set; } = new();
    public HistorySheetRefsDto? CommonRefs { get; set; }
    public HistorySheetRefsDto? PartsRefs { get; set; }
}

internal sealed class HistoryLayerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Visible { get; set; }
    public bool Locked { get; set; }
    public byte Opacity { get; set; }
    public LayerKind Kind { get; set; }
    public int Parent { get; set; }
    public int[] Part { get; set; } = [];
    public ProjectIO.PivotDto? PartPivot { get; set; }
    public int[] PartBorder { get; set; } = [];
    public int[]? PartSource { get; set; }
    /// <summary>그룹만(아니면 null).</summary>
    public List<HistorySkinDto>? Skins { get; set; }
    public int ActiveSkin { get; set; }
    public List<JsonObject>? Effects { get; set; }
    public ProjectIO.GroupLayoutDto? AutoLayout { get; set; }
}

internal sealed class HistorySkinDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Owner { get; set; }
}

internal sealed class HistoryFrameDto
{
    public int Id { get; set; }
    public int DurationMs { get; set; }
    public ProjectIO.PivotDto? Pivot { get; set; }
    public string? Name { get; set; }
}

internal sealed class HistoryTagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int From { get; set; }
    public int To { get; set; }
    public TagDirection Direction { get; set; }
    public bool Loop { get; set; }
    public string? Color { get; set; }
}

internal sealed class HistoryOutfitDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>[그룹, 스킨].</summary>
    public List<int[]> Slots { get; set; } = [];
    /// <summary>[부위, 스킨].</summary>
    public List<int[]> Parts { get; set; } = [];
}

internal sealed class HistorySheetRefsDto
{
    public List<int>? ExcludedSkins { get; set; }
    public List<int>? Outfits { get; set; }
    public List<int>? ExcludedLayers { get; set; }
}
