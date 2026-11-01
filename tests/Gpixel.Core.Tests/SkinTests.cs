using System.Drawing;
using System.IO.Compression;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>스킨(그룹 = 스킨 슬롯): 물려받기·합성, 실행취소, 저장·읽기(예전 파일 포함), 부위 아틀라스 이름·나누기, Sprite Library 내보내기.</summary>
public class SkinTests
{
    private static readonly Color32 Red = new(255, 0, 0);
    private static readonly Color32 Blue = new(0, 0, 255);
    private static readonly Color32 Green = new(0, 255, 0);

    private static void Paint(SpriteProject p, Layer layer, int frame, int x, int y, Color32 c)
    {
        p.BeginCelStroke(p.IndexOf(layer), frame).Set(x, y, c);
        Assert.True(p.CommitCelStroke("점"));
    }

    /// <summary>4×4, 그룹 "body"(Arm 2×2 @ (2,2), 위에 Head 2×2 @ (0,0)), 첫 스킨 Head·Arm (0,0)에 빨강.</summary>
    private static (SpriteProject P, Layer Group, Layer Head, Layer Arm) Body()
    {
        var p = new SpriteProject(4, 4);
        var g = p.AddGroup("body");
        var arm = p.AddPart(g, "Arm", 2, 2, new Point(2, 2));
        var head = p.AddPart(g, "Head", 2, 2, new Point(0, 0));
        Paint(p, head, 0, 0, 0, Red);
        Paint(p, arm, 0, 0, 0, Red);
        return (p, g, head, arm);
    }

    // ───────────── 모델 ─────────────

    [Fact]
    public void Flatten_UsesActiveSkin_EmptyCelsFallBackToFirstSkin()
    {
        var (p, g, head, arm) = Body();
        Assert.Equal([Layer.DefaultSkinName], g.Skins.Select(s => s.Name));
        Assert.Same(g.BaseSkin, g.ActiveSkin);

        var knight = p.AddSkin(g, "knight");
        Assert.Same(knight, g.ActiveSkin);
        // 새 스킨은 비어 있어 첫 스킨 그림을 물려받는다
        Assert.Equal(Red, p.Flatten(0)[0, 0]);
        Assert.Equal(Red, p.Flatten(0)[2, 2]);
        Assert.True(p.IsCelInherited(head, p.Frames[0]));
        Assert.Null(p.GetOwnCel(head, p.Frames[0]));

        // 물려받은 셀에 그리면 보이던 그림을 복사해 시작한다(copy-on-write)
        Paint(p, head, 0, 1, 1, Blue);
        var own = p.GetOwnCel(head, p.Frames[0])!;
        Assert.Equal((Red, Blue), (own[0, 0], own[1, 1]));
        Assert.False(p.IsCelInherited(head, p.Frames[0]));
        Assert.True(p.IsCelInherited(arm, p.Frames[0]));
        Paint(p, head, 0, 0, 0, Blue);
        Assert.Equal(Blue, p.Flatten(0)[0, 0]);
        Assert.Equal(Red, p.Flatten(0)[2, 2]); // Arm은 계속 물려받음
        Assert.Equal(Red, p.GetSkinCel(head, p.Frames[0], g.BaseSkin!)![0, 0]);
        Assert.Equal(Blue, p.GetSkinCel(head, p.Frames[0], knight)![0, 0]);

        // 바꾼 것 없는 스트로크는 기록하지 않고 물려받기 그대로
        int undo = p.History.UndoCount;
        p.BeginCelStroke(p.IndexOf(arm), 0);
        Assert.False(p.CommitCelStroke("빈"));
        Assert.Equal(undo, p.History.UndoCount);
        Assert.True(p.IsCelInherited(arm, p.Frames[0]));

        // 착장 바꾸기: 실행취소 기록 없음, 수정됨 표시
        p.IsDirty = false;
        p.SetActiveSkin(g, g.BaseSkin!);
        Assert.Equal(undo, p.History.UndoCount);
        Assert.True(p.IsDirty);
        Assert.Equal(Red, p.Flatten(0)[0, 0]);
        Assert.Equal(Red, p.FlattenGroup(g, 0)[0, 0]);

        // 셀 비우기 = 다시 물려받기, 숨긴 그룹은 스킨째 숨김
        p.SetActiveSkin(g, knight);
        p.SetCel(p.IndexOf(head), 0, null);
        Assert.Equal(Red, p.Flatten(0)[0, 0]);
        p.SetLayerProperties(p.IndexOf(g), visible: false);
        Assert.Equal(Color32.Transparent, p.Flatten(0)[0, 0]);
    }

    [Fact]
    public void EquipmentSlot_EmptyFirstSkin_ShowsNothing_AndSlotsStack()
    {
        var (p, body, _, _) = Body();
        var armor = p.AddGroup("armor");
        var plate = p.AddPart(armor, "Plate", 2, 2, new Point(0, 0));
        p.RenameSkin(armor, armor.BaseSkin!, "없음");
        var iron = p.AddSkin(armor, "철갑");
        Paint(p, plate, 0, 0, 0, Green);
        Assert.Equal(Green, p.Flatten(0)[0, 0]); // 위 그룹(장비)이 몸 위에 겹친다
        p.SetActiveSkin(armor, armor.BaseSkin!);
        Assert.Equal(Red, p.Flatten(0)[0, 0]); // "없음" = 빈 첫 스킨 → 몸만
        Assert.Throws<ArgumentException>(() => p.RenameSkin(armor, iron, "없음"));
        Assert.Throws<ArgumentException>(() => p.RenameSkin(armor, iron, "  "));
        Assert.Throws<ArgumentException>(() => p.SetActiveSkin(body, iron));
        p.DeleteSkin(armor, iron);
        Assert.Throws<InvalidOperationException>(() => p.DeleteSkin(armor, armor.BaseSkin!));
        Assert.Equal("철갑 2", p.UniqueSkinName(p.Layers.First(l => l.Name == "armor"), "철갑 2"));
        Assert.Equal("없음 2", p.UniqueSkinName(armor, "없음"));
    }

    [Fact]
    public void SkinOperations_UndoRedo()
    {
        var (p, g, head, arm) = Body();
        var baseSkin = g.BaseSkin!;

        var a = p.AddSkin(g, "a");
        Paint(p, head, 0, 0, 0, Blue);
        Assert.True(p.Undo()); // 그림
        Assert.True(p.Undo()); // 스킨 추가
        Assert.Equal([Layer.DefaultSkinName], g.Skins.Select(s => s.Name));
        Assert.Same(baseSkin, g.ActiveSkin);
        Assert.True(p.Redo());
        Assert.Same(a, g.ActiveSkin);
        Assert.True(p.Redo());
        Assert.Equal(Blue, p.Flatten(0)[0, 0]);

        p.RenameSkin(g, a, "b");
        p.Undo();
        Assert.Equal("a", a.Name);
        p.Redo();
        Assert.Equal("b", a.Name);

        // 복제: 첫 스킨을 복제하면 모든 셀 사본
        var copy = p.AddSkin(g, null, baseSkin);
        Assert.Equal($"{Layer.DefaultSkinName} 복사", copy.Name);
        Assert.NotNull(p.GetOwnCel(arm, p.Frames[0], copy));
        Assert.NotSame(p.GetOwnCel(arm, p.Frames[0], baseSkin), p.GetOwnCel(arm, p.Frames[0], copy));
        p.Undo();

        // 순서: 첫 스킨이 바뀌면 새 첫 스킨이 기본이 된다
        p.MoveSkin(g, 1, 0);
        Assert.Equal(["b", Layer.DefaultSkinName], g.Skins.Select(s => s.Name));
        Assert.Same(a, g.BaseSkin);
        Assert.Equal(Blue, p.Flatten(0)[0, 0]);
        Assert.Equal(Color32.Transparent, p.Flatten(0)[2, 2]); // 새 기본(b)에는 Arm이 없다
        p.SetActiveSkin(g, baseSkin);
        Assert.Equal((Red, Red), (p.Flatten(0)[0, 0], p.Flatten(0)[2, 2]));
        p.Undo();
        Assert.Equal([Layer.DefaultSkinName, "b"], g.Skins.Select(s => s.Name));
        Assert.Same(baseSkin, g.ActiveSkin); // 스킨 구성이 같으면 지금 착장 유지
        Assert.Equal((Red, Red), (p.Flatten(0)[0, 0], p.Flatten(0)[2, 2]));

        // 삭제: 셀도 지우고, 실행취소하면 셀까지 돌아온다
        p.SetActiveSkin(g, a);
        p.DeleteSkin(g, a);
        Assert.Single(g.Skins);
        Assert.Same(baseSkin, g.ActiveSkin);
        Assert.Equal(Red, p.Flatten(0)[0, 0]);
        p.Undo();
        Assert.Same(a, g.ActiveSkin);
        Assert.Equal(Blue, p.Flatten(0)[0, 0]);

        // 첫 스킨 삭제: 다음 스킨이 기본(물려받던 Arm은 빈 셀)
        p.DeleteSkin(g, baseSkin);
        Assert.Same(a, g.BaseSkin);
        Assert.Equal((Blue, Color32.Transparent), (p.Flatten(0)[0, 0], p.Flatten(0)[2, 2]));
        p.Undo();
        Assert.Same(baseSkin, g.BaseSkin);
        Assert.Equal(Red, p.GetOwnCel(arm, p.Frames[0], baseSkin)![0, 0]);

        // 다른 편집을 실행취소해도 착장은 그대로
        p.SetActiveSkin(g, baseSkin);
        p.SetLayerProperties(p.IndexOf(head), name: "머리");
        p.SetActiveSkin(g, a);
        p.Undo();
        Assert.Same(a, g.ActiveSkin);
    }

    [Fact]
    public void Structure_DuplicateGroupFrameAndResize_CarrySkinCels()
    {
        var (p, g, head, _) = Body();
        var k = p.AddSkin(g, "k");
        Paint(p, head, 0, 1, 0, Blue);

        var dup = p.DuplicateLayer(p.IndexOf(g));
        Assert.Equal(["기본", "k"], dup.Skins.Select(s => s.Name));
        Assert.NotSame(k, dup.Skins[1]);
        Assert.Same(dup.Skins[1], dup.ActiveSkin);
        var dupHead = p.ChildrenOf(dup).Single(l => l.Name.StartsWith("Head"));
        Assert.Equal(Blue, p.GetOwnCel(dupHead, p.Frames[0], dup.Skins[1])![1, 0]);

        p.DuplicateFrame(0);
        Assert.Equal(Blue, p.GetOwnCel(head, p.Frames[1], k)![1, 0]);
        p.ResizePart(p.IndexOf(head), 3, 2, Anchor.TopLeft);
        Assert.Equal(new Size(3, 2), p.GetOwnCel(head, p.Frames[0], k)!.Size);
        p.DeleteFrame(1);
        Assert.Single(p.Frames);
    }

    [Fact]
    public void PartSkinOverride_CompositesFallsBackSavesAndIgnoresUndo()
    {
        using var dir = new TempDir();
        var (p, g, head, arm) = Body();
        var baseSkin = g.BaseSkin!;
        var knight = p.AddSkin(g, "knight");
        Paint(p, head, 0, 0, 0, Blue);
        Paint(p, arm, 0, 0, 0, Green);
        var zombie = p.AddSkin(g, "zombie"); // 비어 있음 → 모두 기본을 물려받음
        Assert.Equal((Red, Red), (p.Flatten(0)[0, 0], p.Flatten(0)[2, 2]));

        // 머리만 knight: 부위 재정의 → 그룹 스킨(zombie, 빈 셀 → 기본)
        int undo = p.History.UndoCount;
        p.IsDirty = false;
        p.SetPartSkin(head, knight);
        Assert.True(p.IsDirty);
        Assert.Equal(undo, p.History.UndoCount);
        Assert.Same(knight, head.SkinOverride);
        Assert.Same(knight, head.ShownSkin);
        Assert.Same(zombie, arm.ShownSkin);
        Assert.Null(arm.SkinOverride);
        Assert.Equal((Blue, Red), (p.Flatten(0)[0, 0], p.Flatten(0)[2, 2]));
        Assert.Equal(Blue, p.FlattenGroup(g, 0)[0, 0]);
        Assert.Equal(Red, p.FlattenGroup(g, 0, zombie)[0, 0]); // 스킨을 주면 재정의 무시
        Assert.False(p.IsCelInherited(head, p.Frames[0]));
        Assert.True(p.IsCelInherited(arm, p.Frames[0]));

        // 그룹 스킨을 바꾸면 재정의 없는 부위만 따라감
        p.SetActiveSkin(g, knight);
        Assert.Equal((Blue, Green), (p.Flatten(0)[0, 0], p.Flatten(0)[2, 2]));
        p.SetActiveSkin(g, baseSkin);
        Assert.Equal((Blue, Red), (p.Flatten(0)[0, 0], p.Flatten(0)[2, 2]));

        // 재정의한 부위에 그리면 그 스킨 셀에 그린다(물려받은 셀이면 복사해 시작)
        p.SetPartSkin(arm, zombie);
        Paint(p, arm, 0, 1, 1, Blue);
        Assert.Equal((Red, Blue), (p.GetOwnCel(arm, p.Frames[0], zombie)![0, 0], p.GetOwnCel(arm, p.Frames[0], zombie)![1, 1]));
        Assert.Equal(Color32.Transparent, p.GetOwnCel(arm, p.Frames[0], baseSkin)![1, 1]); // 그룹 스킨(기본) 셀은 그대로

        // 저장 왕복
        var path = dir.File("o.gpixel");
        ProjectIO.Save(p, path);
        var back = ProjectIO.Load(path);
        var bg = back.Layers.Single(l => l.IsGroup);
        var bHead = back.Layers.Single(l => l.Name == "Head");
        var bArm = back.Layers.Single(l => l.Name == "Arm");
        Assert.Equal("knight", bHead.SkinOverride!.Name);
        Assert.Equal("zombie", bArm.SkinOverride!.Name);
        Assert.Same(bg.BaseSkin, bg.ActiveSkin);
        Assert.True(p.Flatten(0).ContentEquals(back.Flatten(0)));

        // 되돌리기: 재정의 지우기(그룹 따름), 기록 없음
        Assert.True(p.ResetPartSkins(g));
        Assert.False(p.ResetPartSkins(g));
        Assert.Null(head.SkinOverride);
        Assert.Equal((Red, Red), (p.Flatten(0)[0, 0], p.Flatten(0)[2, 2]));
        p.SetPartSkin(head, knight);
        p.SetPartSkin(head, null);
        Assert.Same(baseSkin, head.ShownSkin);

        // 지운 스킨을 가리키던 재정의는 그룹을 따르고, 스킨 삭제를 실행취소하면 돌아온다
        p.SetPartSkin(head, knight);
        p.DeleteSkin(g, knight);
        Assert.Null(head.SkinOverride);
        Assert.Equal(Red, p.Flatten(0)[0, 0]);
        p.Undo();
        Assert.Same(knight, head.SkinOverride);
        Assert.Equal(Blue, p.Flatten(0)[0, 0]);

        // 다른 편집 실행취소는 재정의를 건드리지 않는다
        p.SetLayerProperties(p.IndexOf(arm), name: "팔");
        p.SetPartSkin(arm, knight);
        p.Undo();
        Assert.Same(knight, arm.SkinOverride);

        // 그룹 복제: 재정의도 새 그룹의 같은 스킨으로
        var dup = p.DuplicateLayer(p.IndexOf(g));
        var dupHead = p.ChildrenOf(dup).Single(l => l.Name.StartsWith("Head"));
        Assert.Same(dup.Skins[1], dupHead.SkinOverride);

        Assert.Throws<ArgumentException>(() => p.SetPartSkin(g, null));
        Assert.Throws<ArgumentException>(() => p.SetPartSkin(head, dup.Skins[1]));
    }

    // ───────────── 저장·읽기 ─────────────

    /// <summary>자동 저장의 사본: 원래 프로젝트를 저장한 것과 같은 파일이 되고, 사본을 만든 뒤 원래 프로젝트를 고쳐도 사본은 그대로다.</summary>
    [Fact]
    public void SaveSnapshot_WritesSameFile_AndIgnoresLaterEdits()
    {
        using var dir = new TempDir();
        var (p, g, head, _) = Body();
        p.AddFrame();
        p.AddSkin(g, "knight");
        Paint(p, head, 1, 1, 1, Blue);
        p.AddTag("walk", 0, 1);
        p.SetLayerEffects(p.IndexOf(head), [new OutlineEffect { Color = Green }]);
        var path = dir.File("hero.gpixel");

        static Dictionary<string, string> Entries(SpriteProject project)
        {
            using var ms = new MemoryStream();
            ProjectIO.Save(project, ms);
            ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            return zip.Entries.ToDictionary(e => e.FullName, e =>
            {
                using var s = e.Open();
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy.ToArray()));
            });
        }

        var expected = Entries(p);
        var snapshot = ProjectIO.SnapshotForSave(p, Path.GetFullPath(path));
        Assert.Equal(expected, Entries(snapshot));

        Paint(p, head, 0, 1, 0, Green);
        p.AddFrame();
        Assert.Equal(expected, Entries(snapshot));
    }

    [Fact]
    public void SaveLoad_RoundTripsSkins_AndOldFilesGetOneDefaultSkin()
    {
        using var dir = new TempDir();
        var (p, g, head, _) = Body();
        var k = p.AddSkin(g, "knight");
        Paint(p, head, 0, 1, 1, Blue);
        p.AddSkin(g, "없음");
        p.SetActiveSkin(g, k);
        var path = dir.File("hero.gpixel");
        ProjectIO.Save(p, path);
        using (var zip = ZipFile.OpenRead(path))
            Assert.Contains($"cels/L{p.IndexOf(head)}_F0_S1.png", zip.Entries.Select(e => e.FullName));

        var warnings = new List<string>();
        var back = ProjectIO.Load(path, warnings);
        Assert.Empty(warnings);
        var bg = back.Layers.Single(l => l.IsGroup);
        Assert.Equal(["기본", "knight", "없음"], bg.Skins.Select(s => s.Name));
        Assert.Equal("knight", bg.ActiveSkin!.Name);
        Assert.True(p.Flatten(0).ContentEquals(back.Flatten(0)));
        var bHead = back.Layers.Single(l => l.Name == "Head");
        Assert.Equal(Blue, back.GetOwnCel(bHead, back.Frames[0], bg.Skins[1])![1, 1]);
        Assert.Null(back.GetOwnCel(bHead, back.Frames[0], bg.Skins[2]));
        back.SetActiveSkin(bg, bg.Skins[2]);
        Assert.Equal(Red, back.Flatten(0)[0, 0]); // 빈 스킨은 첫 스킨을 물려받음

        // 예전(버전 2) 파일: 그룹마다 "기본" 스킨 하나, 없는 스킨 번호의 셀 파일은 무시
        var cel = new MemoryStream();
        var img = new PixelImage(2, 2);
        img.Set(0, 0, Red);
        PngIO.Save(img, cel);
        var old = new MemoryStream();
        using (var zip = new ZipArchive(old, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("project.json").Open()))
                w.Write("""
                    { "version": 2, "width": 4, "height": 4, "frames": [ {} ],
                      "layers": [ { "name": "Head", "kind": "Part", "parent": 1, "partWidth": 2, "partHeight": 2 },
                                  { "name": "몸", "kind": "Group" } ] }
                    """);
            foreach (var name in new[] { "cels/L0_F0.png", "cels/L0_F0_S1.png" })
                using (var s = zip.CreateEntry(name).Open()) s.Write(cel.ToArray());
        }
        old.Position = 0;
        warnings.Clear();
        var migrated = ProjectIO.Load(old, warnings);
        var mg = migrated.Layers[1];
        Assert.Equal([Layer.DefaultSkinName], mg.Skins.Select(s => s.Name));
        Assert.Same(mg.BaseSkin, mg.ActiveSkin);
        Assert.Equal(Red, migrated.Flatten(0)[0, 0]);
        Assert.Contains(warnings, w => w.Contains("L0_F0_S1"));
    }

    // ───────────── 내보내기 ─────────────

    /// <summary><see cref="Body"/> + 프레임 2(Head 프레임 1도 첫 스킨에 그림), 스킨 knight는 Head 프레임 1만 직접 그림. 부위 아틀라스 켬, 프레임 번호 이름.</summary>
    private static (SpriteProject P, Layer Group, Skin Knight) Rig()
    {
        var (p, g, head, _) = Body();
        p.AddFrame();
        Paint(p, head, 1, 0, 0, Red);
        var knight = p.AddSkin(g, "knight");
        Paint(p, head, 1, 1, 1, Blue);
        p.SetActiveSkin(g, g.BaseSkin!);
        p.Export.ExportParts = true;
        p.Export.Common.Naming = SpriteNaming.FrameNumber;
        p.Export.Common.Skins = SkinSource.Selected; // 부위 아틀라스: 모든 스킨
        return (p, g, knight);
    }

    [Fact]
    public void PartsAtlas_FirstSkinKeepsNames_OtherSkinsOnlyOwnCelsWithSeparator()
    {
        var (p, g, knight) = Rig();
        var names = ProjectExport.BuildPartsSheet(p, "x_parts").Entries.Select(e => e.Name).ToList();
        Assert.Equal(["Head", "Head_1", "Head_1@knight", "Arm"], names);
        var entry = ProjectExport.BuildPartsSheet(p, "x_parts").Entries.Single(e => e.Name == "Head_1@knight");
        Assert.Same(knight, entry.Skin);

        // 스킨이 없던 때와 첫 스킨 이름이 같다
        var plain = Body().P;
        plain.AddFrame();
        plain.BeginCelStroke(plain.IndexOf(plain.Layers.Single(l => l.Name == "Head")), 1).Set(0, 0, Red);
        plain.CommitCelStroke("점");
        plain.Export.Common.Naming = SpriteNaming.FrameNumber;
        Assert.Equal(["Head", "Head_1", "Arm"], ProjectExport.BuildPartsSheet(plain, "x_parts").Entries.Select(e => e.Name));

        // 프레임 시트는 지금 스킨으로 합성
        p.SetActiveSkin(g, knight);
        var sheet = ProjectExport.BuildSheet(p, "x");
        Assert.Equal(Blue, sheet.Image[sheet.Entries[1].Rect.X + 1, sheet.Entries[1].Rect.Y + 1]);

        // 스킨마다 따로 파일 (+ 부위마다)
        p.Export.Common.SplitSkins = true;
        var files = ProjectExport.BuildPartsSheets(p, "x_parts");
        Assert.Equal(["x_parts", "x_parts@knight"], files.Select(f => f.Name));
        Assert.Equal(["Head_1@knight"], files[1].Sheet.Entries.Select(e => e.Name));
        p.Export.Common.SplitParts = true;
        Assert.Equal(["x_parts_Head", "x_parts_Arm", "x_parts_Head@knight"], ProjectExport.BuildPartsSheets(p, "x_parts").Select(f => f.Name));
        var (_, parts) = ProjectExport.GetOutputPaths(p, "C:/x/x.gpixel", assumePartContent: false);
        Assert.Equal(3, parts.Count);
    }

    [Fact]
    public void NameHash_MatchesUnitySample()
    {
        // Unity 2D Animation 예제 Part Swap.spriteLib의 m_Hash 값
        Assert.Equal(61186950, SpriteLibraryAssetWriter.NameHash("Knight"));
        Assert.Equal(999990412, SpriteLibraryAssetWriter.NameHash("Body"));
        Assert.Equal(788631449, SpriteLibraryAssetWriter.NameHash("Left Arm"));
    }

    [Fact]
    public void SpriteLibrary_PerGroupSkin_ReferencesMetaIds_RecordedAndStableGuid()
    {
        using var dir = new TempDir();
        var path = dir.File("hero.gpixel");
        var (p, _, _) = Rig();
        p.Export.ExportFrames = false;
        p.Export.WriteSpriteLibrary = true;
        var result = ProjectIO.SaveWithExport(p, path).Export!;

        var atlas = Assert.Single(result.Parts);
        Assert.True(atlas.MetaWritten); // Unity Assets 밖이어도 Sprite Library를 쓰면 메타를 쓴다
        var meta = UnityMeta.Read(atlas.ImagePath + ".meta");
        long Id(string name) => meta.Sprites.Single(s => s.Name == name).InternalId;

        Assert.Equal([dir.File("hero_parts_body@기본.spriteLib"), dir.File("hero_parts_body@knight.spriteLib")], result.Libraries.Select(r => r.ImagePath));
        Assert.All(result.Libraries, r => Assert.True(File.Exists(r.ImagePath + ".meta")));

        foreach (var (lib, expectHead1) in result.Libraries.Zip(new[] { "Head_1", "Head_1@knight" }))
        {
            var root = MiniYaml.Parse(File.ReadAllText(lib.ImagePath))["MonoBehaviour"]!;
            var script = root["m_Script"]!;
            Assert.Equal(("11500000", SpriteLibraryAssetWriter.SourceAssetScriptGuid, "3"), (script["fileID"]!.Text, script["guid"]!.Text, script["type"]!.Text));
            var categories = root["m_Library"]!.Items;
            Assert.Equal(["Head", "Arm"], categories.Select(c => c["m_Name"]!.Text));
            Assert.All(categories, c => Assert.Equal(SpriteLibraryAssetWriter.NameHash(c["m_Name"]!.Text!).ToString(), c["m_Hash"]!.Text));
            var head = categories[0]["m_OverrideEntries"]!.Items;
            Assert.Equal(["0", "1"], head.Select(e => e["m_Name"]!.Text));
            Assert.Equal("2", categories[0]["m_EntryOverrideCount"]!.Text);
            void AssertRef(YamlNode label, string sprite)
            {
                foreach (var key in new[] { "m_Sprite", "m_SpriteOverride" })
                {
                    var r = label[key]!;
                    Assert.Equal((Id(sprite).ToString(), meta.Guid, "3"), (r["fileID"]!.Text, r["guid"]!.Text, r["type"]!.Text));
                }
            }
            AssertRef(head[0], "Head"); // knight는 프레임 0을 물려받아 첫 스킨 스프라이트를 가리킴
            AssertRef(head[1], expectHead1);
            AssertRef(categories[1]["m_OverrideEntries"]!.Items.Single(), "Arm");
        }

        var libMeta = File.ReadAllText(result.Libraries[0].ImagePath + ".meta");
        Assert.Contains("ScriptedImporter:", libMeta);
        Assert.Contains($"guid: {SpriteLibraryAssetWriter.ImporterScriptGuid}", libMeta);
        var guid = UnityMeta.Read(result.Libraries[0].ImagePath + ".meta").Guid;

        // 기록·등록 경로에 .spriteLib(+ .meta)
        Assert.Contains(result.Libraries[0].ImagePath, p.ExportedFiles!);
        Assert.Contains(result.Libraries[0].ImagePath + ".meta", p.ExportedFiles!);
        Assert.Contains(result.Libraries[1].ImagePath, ProjectIO.GetRegisteredExportPaths(path));

        // 다시 내보내도 guid 유지, 같은 내용이면 같은 파일
        var text = File.ReadAllText(result.Libraries[1].ImagePath);
        var again = ProjectIO.SaveWithExport(p, path).Export!;
        Assert.Equal(guid, UnityMeta.Read(again.Libraries[0].ImagePath + ".meta").Guid);
        Assert.Equal(text, File.ReadAllText(again.Libraries[1].ImagePath));

        // 끄면 옛 .spriteLib·.meta는 정리 대상
        p.Export.WriteSpriteLibrary = false;
        var off = ProjectIO.SaveWithExport(p, path).Export!;
        Assert.Empty(off.Libraries);
        Assert.Contains(result.Libraries[0].ImagePath, off.Stale);
        Assert.Contains(result.Libraries[0].ImagePath + ".meta", off.Stale);
    }

    [Fact]
    public void SpriteLibrary_LabelsByTag_AndEmptyEntryWhenNothingToShow()
    {
        using var dir = new TempDir();
        var p = new SpriteProject(4, 4);
        p.AddFrame();
        p.AddTag("walk", 0, 1);
        var armor = p.AddGroup("armor");
        var plate = p.AddPart(armor, "Plate", 2, 2);
        p.RenameSkin(armor, armor.BaseSkin!, "없음");
        p.AddSkin(armor, "iron");
        Paint(p, plate, 1, 0, 0, Green);
        p.Export.ExportParts = true;
        p.Export.WriteSpriteLibrary = true;
        p.Export.Common.Naming = SpriteNaming.ByTag;
        p.Export.Common.Skins = SkinSource.Selected;
        var libs = ProjectIO.SaveWithExport(p, dir.File("a.gpixel")).Export!.Libraries;
        Assert.Equal(["a_parts_armor@없음.spriteLib", "a_parts_armor@iron.spriteLib"], libs.Select(l => Path.GetFileName(l.ImagePath)));
        var none = MiniYaml.Parse(File.ReadAllText(libs[0].ImagePath))["MonoBehaviour"]!["m_Library"]!.Items.Single()["m_OverrideEntries"]!.Items.Single();
        Assert.Equal("walk_1", none["m_Name"]!.Text);
        Assert.Equal("0", none["m_SpriteOverride"]!["fileID"]!.Text); // "없음"은 빈 항목(아무것도 안 그림)
        var iron = MiniYaml.Parse(File.ReadAllText(libs[1].ImagePath))["MonoBehaviour"]!["m_Library"]!.Items.Single()["m_OverrideEntries"]!.Items.Single();
        Assert.NotEqual("0", iron["m_SpriteOverride"]!["fileID"]!.Text);
    }
}
