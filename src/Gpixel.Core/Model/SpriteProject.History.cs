using System.Drawing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;

namespace Gpixel.Core.Model;

// 실행취소 기록을 파일로 남기고 다시 읽기(<see cref="UndoHistoryIO"/>). 기록의 단계는 그대로(같은 SnapshotCommand·CelStrokeCommand) 되살린다.
public sealed partial class SpriteProject
{
    private readonly object _historyLock = new();
    private HistoryIdTable _historyIds = new();

    /// <summary>
    /// 기록 사본(UI 스레드). 구조 단계의 상태 사본은 만든 뒤 바뀌지 않으므로 참조만 담고, 지금 프로젝트에 없는 셀 그림만 내용을 복사한다
    /// (스트로크는 그림을 제자리에서 고치지만 실행취소 순서상 어느 그림이든 지금 내용 = 지금 상태의 내용).
    /// </summary>
    internal UndoHistoryCapture? CaptureHistory(int maxSteps)
    {
        if (_stroke is not null) return null;
        maxSteps = Math.Clamp(maxSteps, 1, UndoStack.MaxCapacity);
        var (undo, redo, open) = History.Contents();
        if (open is not null) undo.Add(open);
        if (undo.Count > maxSteps) undo.RemoveRange(0, undo.Count - maxSteps);
        if (redo.Count > maxSteps) redo.RemoveRange(0, redo.Count - maxSteps);

        var images = new List<(PixelImage Image, Color32[]? Pixels)>();
        var seen = new HashSet<PixelImage>(ReferenceEqualityComparer.Instance);
        var snapshots = new HashSet<Snapshot>(ReferenceEqualityComparer.Instance);
        foreach (var img in _cels.Values)
            if (seen.Add(img)) images.Add((img, null));
        void AddImage(PixelImage img)
        {
            if (seen.Add(img)) images.Add((img, (Color32[])img.Pixels.Clone()));
        }
        void AddSnapshot(Snapshot s)
        {
            if (snapshots.Add(s))
                foreach (var kv in s.Cels) AddImage(kv.Value);
        }
        void Visit(IUndoCommand c)
        {
            switch (c)
            {
                case UndoStack.GroupCommand g:
                    foreach (var item in g.Items) Visit(item);
                    break;
                case CelStrokeCommand s:
                    AddImage(s.Stroke.Image);
                    break;
                case SnapshotCommand s:
                    AddSnapshot(s.Before);
                    AddSnapshot(s.After);
                    break;
                default:
                    throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.FormatHistory_UnsavableStep(type: c.GetType().Name));
            }
        }
        foreach (var c in undo) Visit(c);
        foreach (var c in redo) Visit(c);
        var anchor = Snapshot.Capture(this);
        snapshots.Add(anchor);
        // 기록의 모든 상태가 쓰는 스크립트 원문(UI 스레드에서 표를 읽음): 새 실행에서 예전 원문의 효과로 되돌려도 그리게 기록에 함께 둔다.
        var scripts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var hash in ScriptHashesOf(snapshots.SelectMany(s => s.Effects)))
            if (ScriptSourceOf(hash) is { } source)
                scripts[hash] = source;
        return new UndoHistoryCapture(this, undo, redo, anchor, images, scripts);
    }

    /// <summary>기록 사본 → 기록 파일 내용(조각은 <paramref name="blobs"/>에 씀). 아무 스레드에서나(사본만 읽음).</summary>
    internal HistoryManifestDto BuildHistoryManifest(UndoHistoryCapture capture, HistoryBlobs blobs)
    {
        lock (_historyLock)
        {
            var ids = _historyIds;
            string Snap(Snapshot s)
            {
                if (ids.HashOf(s) is { } known && blobs.TryUse(known)) return known;
                var hash = blobs.Put(JsonSerializer.SerializeToUtf8Bytes(s.ToDto(ids), UndoHistoryIO.Json));
                ids.SetHash(s, hash);
                return hash;
            }
            string Patch(PixelPatch p)
            {
                if (ids.HashOf(p) is { } known && blobs.TryUse(known)) return known;
                var hash = blobs.Put(UndoHistoryIO.EncodePatch(p));
                ids.SetHash(p, hash);
                return hash;
            }
            HistoryStepDto Step(IUndoCommand c) => c switch
            {
                UndoStack.GroupCommand g => new HistoryStepDto { Label = g.Label, Type = "group", Items = g.Items.Select(Step).ToList() },
                CelStrokeCommand s => new HistoryStepDto
                {
                    Label = s.Label,
                    Type = "stroke",
                    Layer = ids.Of(s.Stroke.Key.Layer),
                    Frame = ids.Of(s.Stroke.Key.Frame),
                    Skin = s.Stroke.Key.Skin is { } skin ? ids.Of(skin) : 0,
                    Image = ids.Of(s.Stroke.Image),
                    Created = s.Stroke.Created,
                    Patch = Patch(s.Patch),
                },
                SnapshotCommand s => new HistoryStepDto { Label = s.Label, Type = "snapshot", Before = Snap(s.Before), After = Snap(s.After), Change = (int)s.Change },
                _ => throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.FormatHistory_UnsavableStep(type: c.GetType().Name)),
            };
            return new HistoryManifestDto
            {
                Version = UndoHistoryIO.FormatVersion,
                Anchor = Snap((Snapshot)capture.Anchor),
                Scripts = capture.Scripts.Count > 0 ? new SortedDictionary<string, string>(capture.Scripts.ToDictionary(), StringComparer.Ordinal) : null,
                Undo = capture.Undo.Select(Step).ToList(),
                Redo = capture.Redo.Select(Step).ToList(),
                Images = capture.Images.Select(i => new HistoryImageDto
                {
                    Id = ids.Of(i.Image),
                    W = i.Image.Width,
                    H = i.Image.Height,
                    Blob = i.Pixels is { } px ? blobs.Put(UndoHistoryIO.EncodeImage(i.Image.Width, i.Image.Height, px)) : null,
                }).ToList(),
            };
        }
    }

    /// <summary>
    /// 기록 파일 내용을 실행취소 기록으로 되살린다. 기준 상태는 지금 프로젝트(방금 연 파일)와 같아야 한다(레이어·프레임·태그·착장·스킨 수와 종류, 셀).
    /// 맞지 않거나 손상됐으면 <see cref="InvalidDataException"/>(프로젝트는 그대로).
    /// </summary>
    internal int LoadHistory(HistoryManifestDto m, HistoryBlobs blobs)
    {
        if (_stroke is not null) throw new InvalidOperationException(Gpixel.Core.Localization.DomainStrings.History_CannotReadWhilePainting);
        // 기록이 담은 스크립트 원문(형식 3부터): 해시가 맞는 것만. 상태를 읽기 전에 앱에 알려야 효과 설정을 지금 원문의 schema로 읽는다.
        var scripts = new List<string>();
        foreach (var (hash, source) in m.Scripts ?? new SortedDictionary<string, string>())
        {
            if (!Scripting.ScriptLibrary.IsHash(hash) || source is null || Scripting.ScriptEffectProgram.HashOf(source) != hash) throw Corrupt();
            Scripting.ScriptLibrary.Add(source);
            scripts.Add(source);
        }
        var r = new HistoryResolver();
        var sizes = new Dictionary<int, HistoryImageDto>();
        foreach (var i in m.Images)
            if (i.Id <= 0 || i.W < 1 || i.H < 1 || i.W > PixelImage.MaxSize || i.H > PixelImage.MaxSize || !sizes.TryAdd(i.Id, i))
                throw Corrupt();
        var snapshotDtos = new Dictionary<string, HistorySnapshotDto>(StringComparer.Ordinal);
        HistorySnapshotDto ReadSnap(string? hash)
        {
            if (hash is not null && snapshotDtos.TryGetValue(hash, out var known)) return known;
            HistorySnapshotDto dto;
            try { dto = JsonSerializer.Deserialize<HistorySnapshotDto>(blobs.Get(hash), UndoHistoryIO.Json) ?? throw Corrupt(); }
            catch (JsonException ex) { throw new InvalidDataException(Gpixel.Core.Localization.DomainStrings.FormatHistory_CorruptMessage(error: ex.Message), ex); }
            snapshotDtos[hash!] = dto;
            return dto;
        }

        // 기준 상태 = 지금 프로젝트: 같은 자리 객체에 번호를 붙인다.
        var anchor = ReadSnap(m.Anchor);
        if (anchor.W != _width || anchor.H != _height || anchor.Layers.Count != _layers.Count || anchor.Frames.Count != _frames.Count
            || anchor.Tags.Count != _tags.Count || anchor.Outfits.Count != _outfits.Count)
            throw Mismatch();
        for (int i = 0; i < _layers.Count; i++)
        {
            var (layer, dto) = (_layers[i], anchor.Layers[i]);
            if (dto.Kind != layer.Kind || (dto.Skins is null) != !layer.IsGroup) throw Mismatch();
            r.Bind(dto.Id, layer);
            if (dto.Skins is not { } skins) continue;
            if (skins.Count != layer.SkinList.Count) throw Mismatch();
            for (int k = 0; k < skins.Count; k++) r.Bind(skins[k].Id, layer.SkinList[k]);
        }
        for (int i = 0; i < _frames.Count; i++) r.Bind(anchor.Frames[i].Id, _frames[i]);
        for (int i = 0; i < _tags.Count; i++) r.Bind(anchor.Tags[i].Id, _tags[i]);
        for (int i = 0; i < _outfits.Count; i++) r.Bind(anchor.Outfits[i].Id, _outfits[i]);
        // 기준 상태의 셀: 프로젝트 파일에 있는 그림에 잇고, 파일에 없는 셀(빈 셀은 저장하지 않음)은 빈 그림을 새로 넣는다(아래에서, 모두 읽은 뒤).
        var blanks = new List<(CelKey Key, PixelImage Image)>();
        var anchorKeys = new HashSet<CelKey>();
        foreach (var c in anchor.Cels)
        {
            if (c is not [var li, var fi, var si, var ii]) throw Corrupt();
            var key = new CelKey(r.Bound<Layer>(li), r.Bound<Frame>(fi), si == 0 ? null : r.Bound<Skin>(si));
            if (!anchorKeys.Add(key)) throw Corrupt();
            if (r.Objects.ContainsKey(ii)) continue; // 같은 그림을 두 셀이 같이 쓰던 경우
            if (_cels.TryGetValue(key, out var img)) r.Bind(ii, img);
            else
            {
                if (!sizes.TryGetValue(ii, out var size)) throw Corrupt();
                var blank = new PixelImage(size.W, size.H);
                r.Bind(ii, blank);
                blanks.Add((key, blank));
            }
        }
        if (_cels.Keys.Any(k => !anchorKeys.Contains(k))) throw Mismatch();
        // 지금 프로젝트에 없는 셀 그림: 쓰일 때 조각에서 읽는다.
        r.LoadImage = id => sizes.TryGetValue(id, out var s) && s.Blob is { } blob
            ? UndoHistoryIO.DecodeImage(blobs.Get(blob), s.W, s.H)
            : throw Corrupt();

        var snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        var patches = new Dictionary<string, PixelPatch>(StringComparer.Ordinal);
        Snapshot Snap(string? hash)
        {
            if (hash is not null && snapshots.TryGetValue(hash, out var known)) return known;
            var s = Snapshot.FromDto(ReadSnap(hash), r);
            snapshots[hash!] = s;
            return s;
        }
        PixelPatch Patch(string? hash)
        {
            if (hash is not null && patches.TryGetValue(hash, out var known)) return known;
            var p = UndoHistoryIO.DecodePatch(blobs.Get(hash));
            patches[hash!] = p;
            return p;
        }
        IUndoCommand Step(HistoryStepDto s) => s.Type switch
        {
            "group" when s.Items is { Count: > 0 } items => new UndoStack.GroupCommand(s.Label, items.Select(Step).ToList()),
            "snapshot" => new SnapshotCommand(this, s.Label, Snap(s.Before), Snap(s.After), (ProjectChange)s.Change),
            "stroke" => Stroke(s),
            _ => throw Corrupt(),
        };
        IUndoCommand Stroke(HistoryStepDto s)
        {
            var image = r.Image(s.Image);
            var patch = Patch(s.Patch);
            if (patch.Parts.Width != image.Width || patch.Parts.Height != image.Height) throw Corrupt();
            var key = new CelKey(r.Layer(s.Layer), r.Frame(s.Frame), s.Skin == 0 ? null : r.Skin(s.Skin));
            return new CelStrokeCommand(this, s.Label, new StrokeState(key, image, image, s.Created, null), patch);
        }

        var undoDtos = m.Undo.Count > History.Capacity ? m.Undo.Skip(m.Undo.Count - History.Capacity) : m.Undo;
        var undo = undoDtos.Select(Step).ToList();
        var redo = m.Redo.Select(Step).ToList();

        foreach (var (key, image) in blanks) _cels[key] = image;
        // 열려 있는 동안 표에서 빼지 않으므로 되돌린 효과도 원문을 찾고, 그 상태로 저장하면 원문도 함께 저장된다.
        foreach (var source in scripts) AddScript(source);
        History.Load(undo, redo);
        lock (_historyLock)
        {
            // 다음 쓰기가 같은 번호·조각을 그대로 쓰게 한다(바뀐 단계만 새로 씀).
            var ids = new HistoryIdTable();
            foreach (var (id, obj) in r.Objects) ids.Set(obj, id);
            foreach (var (hash, s) in snapshots) ids.SetHash(s, hash);
            foreach (var (hash, p) in patches) ids.SetHash(p, hash);
            _historyIds = ids;
        }
        return undo.Count + redo.Count;
    }

    private static InvalidDataException Corrupt() => new(Gpixel.Core.Localization.DomainStrings.History_Corrupt);

    private static InvalidDataException Mismatch() => new(Gpixel.Core.Localization.DomainStrings.History_ProjectMismatch);

    private sealed partial class Snapshot
    {
        public KeyValuePair<CelKey, PixelImage>[] Cels => _cels;

        /// <summary>이 상태의 모든 레이어 효과.</summary>
        public IEnumerable<LayerEffect> Effects => _layers.SelectMany(s => s.Effects);

        public HistorySnapshotDto ToDto(HistoryIdTable ids) => new()
        {
            W = _w,
            H = _h,
            Layers = _layers.Select(s => new HistoryLayerDto
            {
                Id = ids.Of(s.Layer),
                Name = s.Name,
                Visible = s.Visible,
                Locked = s.Locked,
                Opacity = s.Opacity,
                Kind = s.Kind,
                Parent = s.Parent is { } parent ? ids.Of(parent) : 0,
                Part = [s.PartSize.Width, s.PartSize.Height, s.PartOffset.X, s.PartOffset.Y],
                PartPivot = ProjectIO.PivotDto.From(s.PartPivot),
                PartBorder = [s.PartBorder.Left, s.PartBorder.Top, s.PartBorder.Right, s.PartBorder.Bottom],
                PartSource = s.PartSourceRect is { } rect ? [rect.X, rect.Y, rect.Width, rect.Height] : null,
                Skins = s.Skins?.Select(k => new HistorySkinDto { Id = ids.Of(k.Skin), Name = k.Name, Owner = k.Owner is { } o ? ids.Of(o) : 0 }).ToList(),
                ActiveSkin = s.ActiveSkin is { } active ? ids.Of(active) : 0,
                Effects = s.Effects.Count > 0 ? s.Effects.Select(EffectCodec.Write).ToList() : null,
                AutoLayout = s.AutoLayout is { } layout ? ProjectIO.GroupLayoutDto.From(layout) : null,
            }).ToList(),
            Frames = _frames.Select(f => new HistoryFrameDto
            {
                Id = ids.Of(f.Frame), DurationMs = f.Ms, Pivot = f.Pivot is { } p ? ProjectIO.PivotDto.From(p) : null, Name = f.Name,
            }).ToList(),
            Tags = _tags.Select(t => new HistoryTagDto
            {
                Id = ids.Of(t.Tag), Name = t.State.Name, From = t.State.From, To = t.State.To, Direction = t.State.Direction, Loop = t.State.Loop,
                Color = t.State.Color.ToHex(),
            }).ToList(),
            Outfits = _outfits.Select(o => new HistoryOutfitDto
            {
                Id = ids.Of(o.Outfit),
                Name = o.Name,
                Slots = o.Slots.Select(s => new[] { ids.Of(s.Group), ids.Of(s.Skin) }).ToList(),
                Parts = o.Parts.Select(s => new[] { ids.Of(s.Part), ids.Of(s.Skin) }).ToList(),
            }).ToList(),
            // 번호 순(사전 순서는 편집 내력마다 달라서 — 같은 상태는 같은 조각이 되게).
            Cels = _cels.Select(kv => new[] { ids.Of(kv.Key.Layer), ids.Of(kv.Key.Frame), kv.Key.Skin is { } skin ? ids.Of(skin) : 0, ids.Of(kv.Value) })
                .OrderBy(c => c[0]).ThenBy(c => c[1]).ThenBy(c => c[2]).ToList(),
            Sprite = ProjectIO.UnityDto.From(_sprite),
            Export = ProjectIO.ExportDto.From(_export, null),
            CommonRefs = Refs(_export.Common, ids),
            PartsRefs = _export.PartsOverride is { } po ? Refs(po, ids) : null,
        };

        private static HistorySheetRefsDto Refs(SheetOptions o, HistoryIdTable ids) => new()
        {
            ExcludedSkins = o.ExcludedSkins.Count > 0 ? o.ExcludedSkins.Select(ids.Of).ToList() : null,
            Outfits = o.Outfits.Count > 0 ? o.Outfits.Select(ids.Of).ToList() : null,
            ExcludedLayers = o.ExcludedLayers.Count > 0 ? o.ExcludedLayers.Select(ids.Of).ToList() : null,
        };

        public static Snapshot FromDto(HistorySnapshotDto d, HistoryResolver r)
        {
            var sprite = new SpriteSettings();
            d.Sprite.ApplyTo(sprite);
            var export = new ExportSettings();
            ProjectIO.ApplyExport(export, d.Export);
            ApplySheet(export.Common, d.Export.Sheet, d.CommonRefs, r);
            if (d.Export.PartsSheet is { } partsSheet)
            {
                export.PartsOverride = new SheetOptions();
                ApplySheet(export.PartsOverride, partsSheet, d.PartsRefs, r);
            }
            return new Snapshot
            {
                _w = d.W,
                _h = d.H,
                _layers = d.Layers.Select(l => new LayerState(
                    r.Layer(l.Id), l.Name, l.Visible, l.Locked, l.Opacity, l.Kind, l.Parent == 0 ? null : r.Layer(l.Parent),
                    l.Part is [var w, var h, _, _] ? new Size(w, h) : throw Corrupt(),
                    new Point(l.Part[2], l.Part[3]),
                    l.PartPivot?.ToPivot() ?? Pivot.Center,
                    l.PartBorder is [var bl, var bt, var br, var bb] ? new SpriteBorder(bl, bt, br, bb) : throw Corrupt(),
                    l.PartSource is [var sx, var sy, var sw, var sh] ? new Rectangle(sx, sy, sw, sh) : null,
                    l.Skins?.Select(s => (r.Skin(s.Id), s.Name, s.Owner == 0 ? (Layer?)null : r.Layer(s.Owner))).ToArray(),
                    l.ActiveSkin == 0 ? null : r.Skin(l.ActiveSkin),
                    l.Effects?.Select(EffectCodec.Read).ToArray() ?? [],
                    l.AutoLayout?.ToLayout())).ToArray(),
                _frames = d.Frames.Select(f => (r.Frame(f.Id), f.DurationMs, f.Pivot?.ToPivot(), f.Name)).ToArray(),
                _tags = d.Tags.Select(t => (r.Tag(t.Id), new AnimationTag(t.Name, t.From, t.To)
                {
                    Direction = t.Direction,
                    Loop = t.Loop,
                    Color = Color32.TryParseHex(t.Color, out var c) ? c : AnimationTag.DefaultColors[0],
                })).ToArray(),
                _outfits = d.Outfits.Select(o => (r.Outfit(o.Id), o.Name,
                    (IReadOnlyList<OutfitSlot>)o.Slots.Select(s => s is [var g, var k] ? new OutfitSlot(r.Layer(g), r.Skin(k)) : throw Corrupt()).ToArray(),
                    (IReadOnlyList<OutfitPart>)o.Parts.Select(s => s is [var p, var k] ? new OutfitPart(r.Layer(p), r.Skin(k)) : throw Corrupt()).ToArray()))
                    .ToArray(),
                _cels = d.Cels.Select(c => c is [var li, var fi, var si, var ii]
                    ? KeyValuePair.Create(new CelKey(r.Layer(li), r.Frame(fi), si == 0 ? null : r.Skin(si)), r.Image(ii))
                    : throw Corrupt()).ToArray(),
                _sprite = sprite,
                _export = export,
            };
        }

        private static void ApplySheet(SheetOptions o, ProjectIO.SheetDto? sheet, HistorySheetRefsDto? refs, HistoryResolver r)
        {
            sheet?.ApplyValues(o);
            o.ExcludedSkins = (refs?.ExcludedSkins ?? []).Select(r.Skin).ToArray();
            o.Outfits = (refs?.Outfits ?? []).Select(r.Outfit).ToArray();
            o.ExcludedLayers = (refs?.ExcludedLayers ?? []).Select(r.Layer).ToArray();
        }
    }

    /// <summary>
    /// 기록 번호 → 객체(읽을 때). 기준 상태의 것은 지금 프로젝트 객체, 나머지(지운 레이어·프레임 등)는 처음 볼 때 새로 만든다
    /// (값은 그 객체가 든 상태를 되돌릴 때 채워짐). 번호는 종류와 관계없이 하나의 순서다.
    /// </summary>
    private sealed class HistoryResolver
    {
        public Dictionary<int, object> Objects { get; } = new();
        public Func<int, PixelImage> LoadImage { get; set; } = _ => throw Corrupt();

        public void Bind(int id, object o)
        {
            if (id <= 0 || !Objects.TryAdd(id, o)) throw Corrupt();
        }

        public T Bound<T>(int id) where T : class => Objects.TryGetValue(id, out var o) && o is T t ? t : throw Corrupt();

        private T Get<T>(int id, Func<T> create) where T : class
        {
            if (Objects.TryGetValue(id, out var o)) return o as T ?? throw Corrupt();
            if (id <= 0) throw Corrupt();
            var made = create();
            Objects[id] = made;
            return made;
        }

        public Layer Layer(int id) => Get(id, () => new Layer(""));
        public Skin Skin(int id) => Get(id, () => new Skin(""));
        public Frame Frame(int id) => Get(id, () => new Frame());
        public AnimationTag Tag(int id) => Get(id, () => new AnimationTag("", 0, 0));
        public Outfit Outfit(int id) => Get(id, () => new Outfit("", [], []));
        public PixelImage Image(int id) => Get(id, () => LoadImage(id));
    }
}

/// <summary>기록에 쓰는 객체 번호(프로젝트가 살아 있는 동안 고정 — 같은 상태 사본은 늘 같은 JSON이 되어 조각을 다시 쓰지 않음)와 이미 쓴 조각 해시.</summary>
internal sealed class HistoryIdTable
{
    private readonly ConditionalWeakTable<object, StrongBox<int>> _ids = new();
    private readonly ConditionalWeakTable<object, string> _hashes = new();
    private int _last;

    public int Of(object o) => _ids.GetValue(o, _ => new StrongBox<int>(++_last)).Value;

    public void Set(object o, int id)
    {
        _ids.AddOrUpdate(o, new StrongBox<int>(id));
        _last = Math.Max(_last, id);
    }

    public string? HashOf(object o) => _hashes.TryGetValue(o, out var h) ? h : null;

    public void SetHash(object o, string hash) => _hashes.AddOrUpdate(o, hash);
}
