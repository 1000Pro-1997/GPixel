using System.Drawing;
using System.IO.Compression;
using Gpixel.Core.Edit;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.Core.Play;

namespace Gpixel.Core.Tests;

public class ProjectTests
{
    private static readonly Color32 Red = new(255, 0, 0);
    private static readonly Color32 Blue = new(0, 0, 255);

    private static void Paint(SpriteProject p, int layer, int frame, int x, int y, Color32 c, string label = "점")
    {
        var cel = p.BeginCelStroke(layer, frame);
        cel.Set(x, y, c);
        p.CommitCelStroke(label);
    }

    // ───────────── 셀·합성 ─────────────

    [Fact]
    public void Flatten_CompositesVisibleLayersWithOpacity()
    {
        var p = new SpriteProject(2, 1);
        p.AddLayer();
        Paint(p, 0, 0, 0, 0, Red);
        Paint(p, 0, 0, 1, 0, Red);
        Paint(p, 1, 0, 0, 0, Blue);
        Paint(p, 1, 0, 1, 0, Blue);
        p.SetLayerProperties(1, opacity: 128);

        var flat = p.Flatten(0);
        var c = flat.Get(0, 0);
        Assert.Equal(255, c.A);
        Assert.InRange(c.R, 125, 129);
        Assert.InRange(c.B, 126, 130);

        p.SetLayerProperties(1, visible: false);
        Assert.Equal(Red, p.Flatten(0).Get(0, 0));
        Assert.Equal(c, p.Flatten(0, includeHidden: true).Get(0, 0));

        // 아래 레이어가 비면 반투명 그대로
        var q = new SpriteProject(1, 1);
        Paint(q, 0, 0, 0, 0, Blue);
        q.SetLayerProperties(0, opacity: 128);
        Assert.Equal(Blue.WithAlpha(128), q.Flatten(0).Get(0, 0));

        var buffer = new PixelImage(1, 1);
        buffer.Set(0, 0, Red);
        q.FlattenInto(buffer, 0, layerFilter: _ => false);
        Assert.Equal(Color32.Transparent, buffer.Get(0, 0));
    }

    [Fact]
    public void Stroke_CreatesCel_UndoRemovesIt_CancelRestores()
    {
        var p = new SpriteProject(4, 4);
        Assert.Null(p.GetCel(0, 0));
        Paint(p, 0, 0, 1, 1, Red);
        Assert.NotNull(p.GetCel(0, 0));
        Assert.True(p.IsDirty);

        Assert.True(p.Undo());
        Assert.Null(p.GetCel(0, 0));
        Assert.True(p.Redo());
        Assert.Equal(Red, p.GetCel(0, 0)!.Get(1, 1));

        var cel = p.BeginCelStroke(0, 0);
        cel.Set(2, 2, Blue);
        p.CancelCelStroke();
        Assert.Equal(Color32.Transparent, p.GetCel(0, 0)!.Get(2, 2));

        p.BeginCelStroke(0, 0);
        Assert.False(p.CommitCelStroke("빈"));
        Assert.Equal(1, p.History.UndoCount);

        p.SetLayerProperties(0, locked: true);
        Assert.Throws<InvalidOperationException>(() => p.BeginCelStroke(0, 0));
    }

    // ───────────── 구조 편집 + 실행취소 ─────────────

    [Theory]
    [InlineData("item", "item_1")]
    [InlineData("item_1", "item_2")]
    [InlineData("item_009", "item_010")]
    [InlineData("a-99", "a-100")]
    [InlineData("tile00", "tile01")]
    [InlineData("7", "8")]
    public void NextNumberedName_IncrementsTrailingNumberKeepingWidth(string name, string expected)
        => Assert.Equal(expected, SpriteProject.NextNumberedName(name, _ => false));

    [Fact]
    public void NextNumberedName_SkipsTakenNames()
        => Assert.Equal("item_3", SpriteProject.NextNumberedName("item_1", n => n is "item_2"));

    [Fact]
    public void Layers_AddDuplicateMoveMergeDelete_WithUndo()
    {
        var p = new SpriteProject(2, 2);
        Paint(p, 0, 0, 0, 0, Red);
        var top = p.AddLayer(name: "위");
        Assert.Equal(1, p.IndexOf(top));
        Paint(p, 1, 0, 1, 1, Blue);

        var dup = p.DuplicateLayer(1);
        Assert.Equal(3, p.Layers.Count);
        Assert.Equal(Blue, p.GetCel(2, 0)!.Get(1, 1));
        Assert.NotSame(p.GetCel(1, 0), p.GetCel(2, 0));
        Assert.Equal("위_1", dup.Name);

        p.MoveLayer(2, 0);
        Assert.Same(dup, p.Layers[0]);
        p.Undo();
        Assert.Same(dup, p.Layers[2]);

        p.MergeLayerDown(2);
        Assert.Equal(2, p.Layers.Count);
        Assert.Equal(Blue, p.GetCel(1, 0)!.Get(1, 1));
        p.MergeLayerDown(1);
        Assert.Single(p.Layers);
        var merged = p.GetCel(0, 0)!;
        Assert.Equal(Red, merged.Get(0, 0));
        Assert.Equal(Blue, merged.Get(1, 1));

        p.Undo();
        p.Undo();
        Assert.Equal(3, p.Layers.Count);
        Assert.Equal(Color32.Transparent, p.GetCel(0, 0)!.Get(1, 1)); // 아래 레이어 셀은 원래 그대로
        Assert.Throws<InvalidOperationException>(() => new SpriteProject(1, 1).DeleteLayer(0));

        p.DeleteLayer(0);
        Assert.Equal(2, p.Layers.Count);
        p.Undo();
        Assert.Equal(Red, p.GetCel(0, 0)!.Get(0, 0));
    }

    [Fact]
    public void Frames_AddDuplicateDeleteMove_AdjustTags_AndUndo()
    {
        var p = new SpriteProject(2, 2);
        p.AddFrame();
        p.AddFrame();
        p.AddFrame(); // 4프레임
        Paint(p, 0, 1, 0, 0, Red);
        var walk = p.AddTag("walk", 1, 2);
        var idle = p.AddTag("idle", 3, 3);

        p.DuplicateFrame(1); // 태그 안 → 늘어남
        Assert.Equal((1, 3), (walk.From, walk.To));
        Assert.Equal((4, 4), (idle.From, idle.To));
        Assert.Equal(Red, p.GetCel(0, 2)!.Get(0, 0));

        p.AddFrame(0); // 맨 앞 → 모두 밀림
        Assert.Equal((2, 4), (walk.From, walk.To));
        Assert.Equal(6, p.Frames.Count);

        p.DeleteFrame(5); // idle 유일 프레임 → 태그 삭제
        Assert.Null(p.FindTag("idle"));
        Assert.Equal(5, p.Frames.Count);

        var f2 = p.Frames[2];
        p.MoveFrame(2, 0);
        Assert.Same(f2, p.Frames[0]);
        Assert.Equal((2, 4), (walk.From, walk.To)); // 태그는 번호 기준

        // 모두 되돌리기
        p.Undo(); // move
        p.Undo(); // delete
        Assert.NotNull(p.FindTag("idle"));
        Assert.Same(idle, p.FindTag("idle"));
        Assert.Equal((5, 5), (idle.From, idle.To));
        p.Undo(); // add
        p.Undo(); // duplicate
        Assert.Equal(4, p.Frames.Count);
        Assert.Equal((1, 2), (walk.From, walk.To));
        Assert.Equal((3, 3), (idle.From, idle.To));
        p.Undo(); // idle tag
        p.Undo(); // walk tag
        Assert.Empty(p.Tags);
        p.Redo();
        Assert.Same(walk, p.Tags[0]);

        p.SetFrameDuration(0, 250);
        p.SetFramePivot(0, Pivot.BottomCenter);
        p.SetFrameName(0, " head ");
        Assert.Equal((250, (Pivot?)Pivot.BottomCenter, "head"), (p.Frames[0].DurationMs, p.Frames[0].Pivot, p.Frames[0].Name));
        p.Undo();
        p.Undo();
        p.Undo();
        Assert.Equal((Frame.DefaultDurationMs, (Pivot?)null, (string?)null), (p.Frames[0].DurationMs, p.Frames[0].Pivot, p.Frames[0].Name));
    }

    [Fact]
    public void Tags_ValidationEditAndPlaybackOrder()
    {
        var p = new SpriteProject(1, 1);
        for (int i = 0; i < 4; i++) p.AddFrame();
        var t = p.AddTag("run", 1, 3);
        Assert.Throws<ArgumentException>(() => p.AddTag("run", 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.AddTag("x", 3, 9));
        Assert.Equal("run_1", p.UniqueTagName("run"));

        Assert.Equal([1, 2, 3], t.GetPlaybackOrder());
        p.SetTag(0, direction: TagDirection.Reverse);
        Assert.Equal([3, 2, 1], t.GetPlaybackOrder());
        p.SetTag(0, direction: TagDirection.PingPong, name: "jump");
        Assert.Equal([1, 2, 3, 2], t.GetPlaybackOrder());
        Assert.Equal("jump", t.Name);
        p.Undo();
        Assert.Equal(("run", TagDirection.Reverse), (t.Name, t.Direction));

        var pp = new AnimationTag("a", 0, 0) { Direction = TagDirection.PingPong };
        Assert.Equal([0], pp.GetPlaybackOrder());
        p.RemoveTag(0);
        Assert.Empty(p.Tags);
    }

    [Fact]
    public void Edit_GroupsSettingsAndNestedCalls_AndRollsBackOnError()
    {
        var p = new SpriteProject(2, 2);
        p.Edit("설정", ProjectChange.Settings | ProjectChange.Frames, () =>
        {
            p.Sprite.PixelsPerUnit = 32;
            p.Export.Common.Trim = true;
            p.AddFrame();
            p.SetFrameDuration(1, 40);
        });
        Assert.Equal(1, p.History.UndoCount);
        p.Undo();
        Assert.Equal(16f, p.Sprite.PixelsPerUnit);
        Assert.False(p.Export.Common.Trim);
        Assert.Single(p.Frames);
        p.Redo();
        Assert.Equal(40, p.Frames[1].DurationMs);

        Assert.Throws<InvalidOperationException>(() => p.Edit("실패", ProjectChange.Frames, () =>
        {
            p.AddFrame();
            throw new InvalidOperationException("x");
        }));
        Assert.Equal(2, p.Frames.Count);
        Assert.Equal(1, p.History.UndoCount);

        var changes = new List<ProjectChange>();
        p.Changed += (_, e) => changes.Add(e.Change);
        p.AddLayer();
        Assert.Contains(ProjectChange.Layers, changes);
    }

    [Fact]
    public void ResizeAndRotateCanvas_KeepPivotOnSamePixel_AndUndo()
    {
        var p = new SpriteProject(4, 4);
        Paint(p, 0, 0, 0, 3, Red);
        p.Edit("피벗", ProjectChange.Settings, () => p.Sprite.DefaultPivot = Pivot.BottomCenter); // 픽셀 (2,4)
        p.ResizeCanvas(8, 6, Anchor.BottomRight);
        Assert.Equal((8, 6), (p.Width, p.Height));
        Assert.Equal(Red, p.GetCel(0, 0)!.Get(4, 5));
        var (px, py) = p.Sprite.DefaultPivot.ToLocalPixel(8, 6);
        Assert.Equal((6f, 6f), (px, py));
        p.Undo();
        Assert.Equal((4, 4), (p.Width, p.Height));
        Assert.Equal(Pivot.BottomCenter, p.Sprite.DefaultPivot);
        Assert.Equal(Red, p.GetCel(0, 0)!.Get(0, 3));

        p.RotateCanvas90(clockwise: true);
        Assert.Equal(Red, p.GetCel(0, 0)!.Get(0, 0));
        Assert.Equal(new Pivot(0f, 0.5f), p.Sprite.DefaultPivot); // 아래 가운데 → 왼쪽 가운데
        p.FlipCanvasHorizontal();
        Assert.Equal(Red, p.GetCel(0, 0)!.Get(3, 0));
        Assert.Equal(new Pivot(1f, 0.5f), p.Sprite.DefaultPivot);
    }

    /// <summary>UndoStack 시험용 동작(실행취소/다시 때 각 함수를 부름).</summary>
    private sealed class TestCommand(string label, Action undo, Action redo) : IUndoCommand
    {
        public string Label { get; } = label;
        public void Undo() => undo();
        public void Redo() => redo();
    }

    [Fact]
    public void UndoStack_CapacityAndRedo()
    {
        var stack = new UndoStack(capacity: 3);
        int value = 0;
        for (int i = 1; i <= 5; i++)
        {
            int v = i;
            value = v;
            stack.Push(new TestCommand($"{v}", () => value = v - 1, () => value = v));
        }
        Assert.Equal(3, stack.UndoCount);
        while (stack.Undo()) { }
        Assert.Equal(2, value);
        stack.Redo();
        Assert.Equal(3, value);
        stack.Push(new TestCommand("x", () => { }, () => { }));
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void UndoStack_LoweringCapacityDropsOldest_AndClamps()
    {
        var stack = new UndoStack(capacity: 10);
        int value = 0;
        for (int i = 1; i <= 5; i++)
        {
            int v = i;
            value = v;
            stack.Push(new TestCommand($"{v}", () => value = v - 1, () => value = v));
        }
        stack.Capacity = 2;
        Assert.Equal(2, stack.UndoCount);
        while (stack.Undo()) { }
        Assert.Equal(3, value); // 4·5만 남음
        stack.Capacity = 0;
        Assert.Equal(1, stack.Capacity);
        stack.Capacity = UndoStack.MaxCapacity + 1;
        Assert.Equal(UndoStack.MaxCapacity, stack.Capacity);
    }

    [Fact]
    public void UndoGroup_RevertsWholeTurnAndSurvivesCapacity()
    {
        var p = new SpriteProject(4, 4);
        p.History.Capacity = 100;
        p.BeginCelStroke(0, 0).Set(0, 0, Red);
        p.CommitCelStroke("사람");
        p.History.BeginGroup("AI");
        for (int i = 0; i < p.History.Capacity + 5; i++)
        {
            p.BeginCelStroke(0, 0).Set(1, 0, new Color32((byte)i, 0, 0));
            p.CommitCelStroke("점");
        }
        p.AddFrame();
        Assert.Equal("AI", p.History.UndoLabel);
        var group = p.History.EndGroup()!;
        Assert.Equal(1, p.History.UndoDepthOf(group));
        Assert.Equal(2, p.History.UndoCount);

        Assert.True(p.Undo());
        Assert.Single(p.Frames);
        Assert.Equal(Color32.Transparent, p.GetCel(0, 0)!.Get(1, 0));
        Assert.Equal(Red, p.GetCel(0, 0)!.Get(0, 0));
        Assert.Equal(1, p.History.RedoDepthOf(group));

        Assert.True(p.Redo());
        Assert.Equal(2, p.Frames.Count);
        Assert.Equal(new Color32(104, 0, 0), p.GetCel(0, 0)!.Get(1, 0));
    }

    [Fact]
    public void UndoGroup_UndoWhileOpenClosesGroupFirst()
    {
        var p = new SpriteProject(4, 4);
        p.History.BeginGroup("AI");
        p.AddFrame();
        p.AddFrame();
        Assert.True(p.Undo());
        Assert.False(p.History.IsGrouping);
        Assert.Single(p.Frames);
        Assert.Null(p.History.EndGroup());
        p.History.BeginGroup("빈 묶음");
        Assert.Null(p.History.EndGroup());
        Assert.False(p.History.CanUndo);
    }

    // ───────────── 파일 ─────────────

    private static SpriteProject MakeSample()
    {
        var p = new SpriteProject(8, 8);
        var top = p.AddLayer(name: "윤곽");
        p.SetLayerProperties(1, opacity: 200, locked: false);
        p.SetLayerProperties(0, visible: false);
        p.AddFrame();
        p.AddFrame();
        var img = p.BeginCelStroke(0, 0);
        img.FillRect(new Rectangle(1, 1, 3, 3), Red);
        p.CommitCelStroke("a");
        img = p.BeginCelStroke(1, 2);
        img.Set(7, 7, Blue);
        p.CommitCelStroke("b");
        p.SetFrameDuration(1, 33);
        p.SetFramePivot(2, new Pivot(0.25f, -0.5f));
        p.SetFrameName(1, "special");
        p.AddTag("walk_down", 0, 2, TagDirection.PingPong, loop: false);
        p.SetTag(0, color: new Color32(1, 2, 3));
        p.Edit("설정", ProjectChange.Settings, () =>
        {
            p.Sprite.PixelsPerUnit = 32;
            p.Sprite.DefaultPivot = Pivot.BottomCenter;
            p.Export.Common.Grid = SheetGrid.Columns;
            p.Export.Common.GridCount = 2;
            p.Export.Common.Spacing = 1;
            p.Export.Common.Trim = true;
            p.Export.Folder = "out";
            p.Export.WriteMeta = true;
            p.Export.Common.Order = GridOrder.ByPartTag;
        });
        Assert.NotNull(top);
        return p;
    }

    [Fact]
    public void ProjectIO_ZipRoundTrip()
    {
        using var dir = new TempDir();
        var path = dir.File("hero.gpixel");
        var p = MakeSample();
        ProjectIO.Save(p, path);
        Assert.Equal(path, p.Path);
        Assert.False(p.IsDirty);

        using (var zip = ZipFile.OpenRead(path))
        {
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
            Assert.Equal(["cels/L0_F0.png", "cels/L1_F2.png", "project.json"], names);
        }

        var back = ProjectIO.Load(path);
        Assert.Equal(path, back.Path);
        Assert.False(back.IsDirty);
        Assert.Equal((8, 8), (back.Width, back.Height));
        Assert.Equal(["레이어 1", "윤곽"], back.Layers.Select(l => l.Name));
        Assert.False(back.Layers[0].Visible);
        Assert.Equal(200, back.Layers[1].Opacity);
        Assert.Equal([100, 33, 100], back.Frames.Select(f => f.DurationMs));
        Assert.Equal(new Pivot(0.25f, -0.5f), back.Frames[2].Pivot);
        Assert.Equal("special", back.Frames[1].Name);
        var tag = Assert.Single(back.Tags);
        Assert.Equal(("walk_down", 0, 2, TagDirection.PingPong, false, new Color32(1, 2, 3)),
            (tag.Name, tag.From, tag.To, tag.Direction, tag.Loop, tag.Color));
        Assert.Equal(32f, back.Sprite.PixelsPerUnit);
        Assert.Equal(Pivot.BottomCenter, back.Sprite.DefaultPivot);
        var c = back.Export.Common;
        Assert.Equal((SheetGrid.Columns, 2, 1, true, "out", (bool?)true, GridOrder.ByPartTag),
            (c.Grid, c.GridCount, c.Spacing, c.Trim, back.Export.Folder, back.Export.WriteMeta, c.Order));
        for (int l = 0; l < 2; l++)
            for (int f = 0; f < 3; f++)
                Assert.Equal(p.GetCel(l, f) is null, back.GetCel(l, f) is null);
        Assert.True(p.GetCel(0, 0)!.ContentEquals(back.GetCel(0, 0)!));
        Assert.Equal(Blue, back.GetCel(1, 2)!.Get(7, 7));
        Assert.True(ProjectIO.LoadThumbnail(path).ContentEquals(back.Flatten(0)));
    }

    [Fact]
    public void ProjectIO_RobustLoad_MissingCelsUnknownFieldsAndNewerVersion()
    {
        static MemoryStream Zip(string json, params (string Name, byte[] Data)[] files)
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var w = new StreamWriter(zip.CreateEntry("project.json").Open())) w.Write(json);
                foreach (var (name, data) in files)
                    using (var s = zip.CreateEntry(name).Open()) s.Write(data);
            }
            ms.Position = 0;
            return ms;
        }

        var celPng = new MemoryStream();
        var small = new PixelImage(2, 2);
        small.Set(1, 1, Red);
        PngIO.Save(small, celPng);

        var warnings = new List<string>();
        var p = ProjectIO.Load(Zip("""
            { "version": 1, "width": 4, "height": 3, "futureField": { "a": 1 },
              "layers": [ { "name": "A", "extra": true } ],
              "frames": [ {}, { "durationMs": 50 } ],
              "tags": [ { "name": "t", "from": 0, "to": 9 } ] }
            """,
            ("cels/L0_F1.png", celPng.ToArray()), ("cels/L5_F0.png", celPng.ToArray()), ("cels/L0_F0.png", [1, 2, 3])),
            warnings);
        Assert.Equal((4, 3), (p.Width, p.Height));
        Assert.Equal(2, p.Frames.Count);
        Assert.Null(p.GetCel(0, 0)); // 손상된 셀 → 빈 셀
        Assert.Equal(Red, p.GetCel(0, 1)!.Get(1, 1)); // 크기가 달라도 왼쪽 위에 맞춤
        Assert.Equal((0, 1), (p.Tags[0].From, p.Tags[0].To)); // 범위 밖은 잘림
        Assert.Equal(3, warnings.Count);

        var minimal = ProjectIO.Load(Zip("""{ "version": 1, "width": 2, "height": 2 }"""));
        Assert.Single(minimal.Layers);
        Assert.Single(minimal.Frames);

        var newer = Assert.Throws<InvalidDataException>(() => ProjectIO.Load(Zip("""{ "version": 99, "width": 2, "height": 2 }""")));
        Assert.Contains("99", newer.Message);
        Assert.Throws<InvalidDataException>(() => ProjectIO.Load(new MemoryStream([1, 2, 3, 4])));
        Assert.Throws<InvalidDataException>(() => ProjectIO.Load(Zip("{ broken")));
    }

    // ───────────── 내보내기 ─────────────

    private static SpriteProject MakeWalkProject()
    {
        // 16x16 캔버스, 프레임 5개: walk 0..2(정방향), idle 3..4, 프레임마다 색이 다른 4x8 몸통(아래 가운데)
        var p = new SpriteProject(16, 16);
        for (int i = 1; i < 5; i++) p.AddFrame();
        for (int i = 0; i < 5; i++)
        {
            var cel = p.BeginCelStroke(0, i);
            cel.FillRect(new Rectangle(6, 8, 4, 8), new Color32((byte)(40 * i + 10), 0, 0));
            p.CommitCelStroke("몸");
        }
        p.AddTag("walk", 0, 2);
        p.AddTag("idle", 3, 4, TagDirection.PingPong);
        p.Edit("피벗", ProjectChange.Settings, () => p.Sprite.DefaultPivot = Pivot.BottomCenter);
        return p;
    }

    [Fact]
    public void Export_ColumnsLayout_NamesRectsAndAnimations()
    {
        var p = MakeWalkProject();
        p.Edit("열", ProjectChange.Settings, () => { p.Export.Common.Grid = SheetGrid.Columns; p.Export.Common.GridCount = 3; p.Export.Common.Spacing = 2; });
        var sheet = ProjectExport.BuildSheet(p, "hero");
        Assert.Equal((16 * 3 + 4, 16 * 2 + 2), (sheet.Image.Width, sheet.Image.Height));
        Assert.Equal(["walk_0", "walk_1", "walk_2", "idle_0", "idle_1"], sheet.Entries.Select(e => e.Name));
        Assert.Equal(new Rectangle(18, 0, 16, 16), sheet.Entries[1].Rect);
        Assert.Equal(new Rectangle(0, 18, 16, 16), sheet.Entries[3].Rect);
        Assert.All(sheet.Entries, e => Assert.Equal(Pivot.BottomCenter, e.Pivot));
        Assert.Equal(p.Flatten(4).Get(7, 10), sheet.Image.Get(18 + 7, 18 + 10));

        var doc = ProjectExport.BuildDocument(p, "hero");
        Assert.Equal(SpriteMode.Multiple, doc.Sprite.Mode);
        Assert.Equal(5, doc.Slices.Count);
        var idle = doc.FindAnimation("idle")!;
        Assert.Equal(["idle_0", "idle_1"], idle.Frames.Select(f => f.Slice)); // 2프레임 왕복은 양 끝만
        var walk = doc.FindAnimation("walk")!;
        Assert.Equal(["walk_0", "walk_1", "walk_2"], walk.Frames.Select(f => f.Slice));

        // 태그 없는 프레임 + 이름 재정의
        p.AddFrame();
        p.SetFrameName(1, "special");
        var names = ProjectExport.BuildSheet(p, "hero").Entries.Select(e => e.Name).ToList();
        Assert.Equal(["walk_0", "special", "walk_2", "idle_0", "idle_1", "hero_5"], names);
    }

    [Fact]
    public void Export_RowPerTag_Trim_PivotCorrection_AndSingleMode()
    {
        var p = MakeWalkProject();
        p.Edit("배치", ProjectChange.Settings, () =>
        {
            p.Export.Common.Order = GridOrder.ByPartTag; // 태그마다 한 줄(줄바꿈 없음)
            p.Export.Common.Grid = SheetGrid.Columns;
            p.Export.Common.GridCount = SheetOptions.MaxGridCount;
            p.Export.Common.Trim = true;
        });
        p.AddFrame(); // 태그 없는 빈 프레임 → 마지막 줄, 캔버스 전체
        var sheet = ProjectExport.BuildSheet(p, "hero");
        Assert.Equal(3, sheet.Entries.Select(e => e.Rect.Y).Distinct().Count());
        var first = sheet.Entries[0];
        Assert.Equal(new Rectangle(0, 0, 4, 8), first.Rect); // 4x8로 잘림
        Assert.Equal(new Pivot(0.5f, 0f), first.Pivot);       // 몸통 아래 가운데 = 원래 피벗 지점
        Assert.Equal(new Size(16, 16), sheet.CellSize);        // 빈 프레임은 잘리지 않음
        Assert.Equal(new Rectangle(0, 34, 16, 16), sheet.Entries[^1].Rect); // 줄 높이 16 + 새 프로젝트 기본 간격 1px씩
        Assert.Equal("hero_5", sheet.Entries[^1].Name);

        // 피벗이 잘린 영역 밖이면 0~1 밖 값
        p.SetFramePivot(0, Pivot.FromPreset(PivotPreset.TopCenter)); // 캔버스 (8,0) = 잘린 칸 위로 8px
        var off = ProjectExport.BuildSheet(p, "hero").Entries[0].Pivot;
        Assert.Equal(new Pivot(0.5f, 2f), off);

        p.Edit("모드", ProjectChange.Settings, () => { p.Sprite.Mode = SpriteMode.Single; p.Export.SingleFrameIndex = 3; });
        var single = ProjectExport.BuildDocument(p, "hero");
        Assert.Equal(SpriteMode.Single, single.Sprite.Mode);
        Assert.Empty(single.Slices);
        Assert.Equal((16, 16), (single.Width, single.Height));
        Assert.True(single.Image.ContentEquals(p.Flatten(3)));
        Assert.Equal(Pivot.BottomCenter, single.Sprite.DefaultPivot);
        var all = ProjectExport.BuildDocument(p, "hero", allFrames: true);
        Assert.Equal(6, all.Slices.Count);
        Assert.NotNull(all.FindAnimation("walk"));
    }

    [Fact]
    public void Export_WritesPngMetaSidecar_AndReexportKeepsGuidAndIds()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Assets", "Art"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "ProjectSettings"));
        var projectPath = Path.Combine(dir.Path, "Assets", "Art", "hero.gpixel");
        var p = MakeWalkProject();

        var saved = ProjectIO.SaveWithExport(p, projectPath);
        var export = Assert.Single(saved.Export!.Frames);
        Assert.Equal(Path.Combine(dir.Path, "Assets", "Art", "hero.png"), export.ImagePath);
        Assert.True(export.MetaWritten);
        Assert.Equal(5, export.SpriteCount);
        Assert.False(File.Exists(SidecarIO.GetPath(export.ImagePath))); // 편집 정보는 .gpixel에만

        var meta1 = UnityMeta.Read(export.ImagePath + ".meta");
        Assert.Equal(["walk_0", "walk_1", "walk_2", "idle_0", "idle_1"], meta1.Sprites.Select(s => s.Name));
        // 새 프로젝트 기본 칸 수 = 자동, 간격 1px: 16×16 다섯 칸 → 3×2(50×33). Unity 칸은 아래 왼쪽 원점.
        Assert.Equal(new Rectangle(17, 17, 16, 16), meta1.Sprites[1].UnityRect);
        Assert.Equal((50, 33), (PngIO.ReadSize(export.ImagePath).Width, PngIO.ReadSize(export.ImagePath).Height));

        // 예전 버전이 남긴 사이드카는 다시 내보낼 때 지운다(열 때 메타보다 먼저 읽혀 낡은 칸을 보여 주므로).
        File.WriteAllText(SidecarIO.GetPath(export.ImagePath), """{"version":1,"slices":[]}""");
        ProjectIO.SaveWithExport(p);
        Assert.False(File.Exists(SidecarIO.GetPath(export.ImagePath)));
        Assert.Equal(DocumentInfoSource.UnityMeta, DocumentIO.Load(export.ImagePath).Source);

        // 프레임 추가·태그 늘리고 다시 저장 → 같은 이름은 같은 guid·ID
        p.DuplicateFrame(2);
        ProjectIO.SaveWithExport(p);
        var meta2 = UnityMeta.Read(export.ImagePath + ".meta");
        Assert.Equal(meta1.Guid, meta2.Guid);
        Assert.Equal(6, meta2.Sprites.Count);
        var old = meta1.Sprites.ToDictionary(s => s.Name);
        foreach (var s in meta2.Sprites.Where(s => old.ContainsKey(s.Name)))
        {
            Assert.Equal(old[s.Name].SpriteId, s.SpriteId);
            Assert.Equal(old[s.Name].InternalId, s.InternalId);
        }
        Assert.Contains(meta2.Sprites, s => s.Name == "walk_3");

        // 내보내기 끄기
        p.Edit("끄기", ProjectChange.Settings, () => p.Export.ExportOnSave = false);
        Assert.Null(ProjectIO.SaveWithExport(p).Export);

        // Unity 밖 + 메타 없음 → 자동이면 메타를 쓰지 않음
        var plain = new SpriteProject(4, 4);
        var plainPath = dir.File("plain.gpixel");
        var r = Assert.Single(ProjectIO.SaveWithExport(plain, plainPath).Export!.Frames);
        Assert.False(r.MetaWritten);
        Assert.False(File.Exists(r.ImagePath + ".meta"));
    }

    [Fact]
    public void SaveJob_ProjectChangedWhileWriting_StaysDirty()
    {
        using var dir = new TempDir();
        var path = dir.File("busy.gpixel");
        var p = new SpriteProject(4, 4);
        p.IsDirty = true;

        // 작업 스레드가 쓰는 동안 UI 스레드에서 바뀐 것: 파일에 없는 변경이 있으니 수정됨이 남아야 한다
        var job = ProjectIO.BeginSave(p, path);
        p.AddFrame();
        job.Run();
        job.Finish();
        Assert.True(p.IsDirty);
        Assert.Equal(Path.GetFullPath(path), p.Path);

        // 바뀐 것이 없으면 꺼진다
        var again = ProjectIO.BeginSave(p);
        again.Run();
        again.Finish();
        Assert.False(p.IsDirty);
    }

    [Fact]
    public void Export_ImageFormat_WritesThatFormatWithMeta_PersistsAndRenames()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Assets"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "ProjectSettings"));
        var projectPath = Path.Combine(dir.Path, "Assets", "hero.gpixel");
        var p = MakeWalkProject();
        p.Edit("형식", ProjectChange.Settings, () => { p.Export.ImageFormat = ExportImageFormat.Jpg; p.Export.JpegQuality = 75; });

        var jpg = Assert.Single(ProjectIO.SaveWithExport(p, projectPath).Export!.Frames);
        Assert.Equal(Path.Combine(dir.Path, "Assets", "hero.jpg"), jpg.ImagePath);
        Assert.Equal([0xFF, 0xD8], File.ReadAllBytes(jpg.ImagePath)[..2]);
        Assert.True(jpg.MetaWritten);
        Assert.Equal(5, UnityMeta.Read(jpg.ImagePath + ".meta").Sprites.Count);
        Assert.False(File.Exists(Path.Combine(dir.Path, "Assets", "hero.png")));

        var loaded = ProjectIO.Load(projectPath);
        Assert.Equal((ExportImageFormat.Jpg, 75), (loaded.Export.ImageFormat, loaded.Export.JpegQuality));

        // 소유한 기본 이름 내보내기는 형식이 달라도 프로젝트와 함께 이름이 바뀐다.
        var renamed = Path.Combine(dir.Path, "Assets", "knight.gpixel");
        ProjectIO.RenameWithOwnedDefaultExport(projectPath, renamed);
        Assert.True(File.Exists(Path.Combine(dir.Path, "Assets", "knight.jpg")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "Assets", "knight.jpg.meta")));

        // TGA는 무손실(알파 포함): 헤더 뒤 첫 픽셀 = 왼쪽 위 BGRA
        p.Edit("형식", ProjectChange.Settings, () => p.Export.ImageFormat = ExportImageFormat.Tga);
        var tga = Assert.Single(ProjectIO.SaveWithExport(p, renamed).Export!.Frames);
        var bytes = File.ReadAllBytes(tga.ImagePath);
        var expected = ProjectExport.BuildSheet(p, "knight").Image.Get(0, 0);
        Assert.Equal([expected.B, expected.G, expected.R, expected.A], bytes[18..22]);
    }

    [Fact]
    public void RegisteredExports_CustomNamesAreFoundAndOpenTheirProject()
    {
        using var dir = new TempDir();
        var projectPath = dir.File("hero.gpixel");
        var p = new SpriteProject(4, 4);
        var g = p.AddGroup();
        p.AddPart(g, "Head", 2, 2);
        p.Edit("이름", ProjectChange.Settings, () =>
        {
            p.Export.ExportParts = true; // 새 프로젝트 기본은 프레임 시트만 — 둘 다 내보내기
            p.Export.FileName = "hero_sheet";
            p.Export.PartsFileName = "hero_atlas";
            p.Export.ImageFormat = ExportImageFormat.Tga;
        });
        ProjectIO.Save(p, projectPath);

        Assert.Equal([dir.File("hero_sheet.tga"), dir.File("hero_atlas.tga")], ProjectIO.GetRegisteredExportPaths(projectPath));
        Assert.Equal(projectPath, ProjectImport.FindProjectForImage(dir.File("hero_atlas.tga")));
        Assert.Equal(projectPath, ProjectImport.FindProjectForImage(dir.File("hero.png"))); // 같은 이름
        Assert.Null(ProjectImport.FindProjectForImage(dir.File("other.png")));

        p.Edit("끄기", ProjectChange.Settings, () => p.Export.ExportFrames = false);
        ProjectIO.Save(p, projectPath);
        Assert.Equal([dir.File("hero_atlas.tga")], ProjectIO.GetRegisteredExportPaths(projectPath));
        Assert.Null(ProjectImport.FindProjectForImage(dir.File("hero_sheet.tga")));
    }

    [Fact]
    public void RenameProject_MovesOnlyItsDefaultExportAndPreservesUnityGuid()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Assets"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "ProjectSettings"));
        var oldPath = Path.Combine(dir.Path, "Assets", "hero.gpixel");
        var newPath = Path.Combine(dir.Path, "Assets", "renamed.gpixel");
        var project = MakeWalkProject();
        ProjectIO.SaveWithExport(project, oldPath);
        var oldPng = Path.ChangeExtension(oldPath, ".png");
        var newPng = Path.ChangeExtension(newPath, ".png");
        var guid = UnityMeta.Read(oldPng + ".meta").Guid;
        File.WriteAllText(SidecarIO.GetPath(oldPng), "{}"); // 예전 버전이 남긴 사이드카도 함께 옮긴다
        Assert.Equal([oldPng, oldPng + ".meta", SidecarIO.GetPath(oldPng)],
            ProjectIO.GetOwnedDefaultExportPaths(oldPath));

        ProjectIO.RenameWithOwnedDefaultExport(oldPath, newPath);

        Assert.False(File.Exists(oldPath));
        Assert.False(File.Exists(oldPng));
        Assert.False(File.Exists(oldPng + ".meta"));
        Assert.False(File.Exists(SidecarIO.GetPath(oldPng)));
        Assert.True(File.Exists(newPath));
        Assert.True(File.Exists(newPng));
        Assert.True(File.Exists(SidecarIO.GetPath(newPng)));
        Assert.Equal(guid, UnityMeta.Read(newPng + ".meta").Guid);
        Assert.Equal([newPng, newPng + ".meta", SidecarIO.GetPath(newPng)],
            ProjectIO.GetOwnedDefaultExportPaths(newPath));
    }

    [Fact]
    public void RenameProject_CollisionLeavesAllFilesInPlace()
    {
        using var dir = new TempDir();
        var oldPath = dir.File("hero.gpixel");
        var newPath = dir.File("renamed.gpixel");
        ProjectIO.SaveWithExport(new SpriteProject(2, 2), oldPath);
        var oldPng = dir.File("hero.png");
        var newPng = dir.File("renamed.png");
        File.WriteAllText(newPng, "other image");

        Assert.Throws<IOException>(() => ProjectIO.RenameWithOwnedDefaultExport(oldPath, newPath));

        Assert.True(File.Exists(oldPath));
        Assert.True(File.Exists(oldPng));
        Assert.Equal("other image", File.ReadAllText(newPng));
        Assert.False(File.Exists(newPath));
    }

    [Theory]
    [InlineData(null, "custom")]
    [InlineData("sheets", null)]
    [InlineData("sheets", "custom")]
    public void RenameProject_CustomExportIsNotMovedOrSelectedForDeletion(string? folder, string? fileName)
    {
        using var dir = new TempDir();
        var oldPath = dir.File("hero.gpixel");
        var newPath = dir.File("renamed.gpixel");
        var project = new SpriteProject(2, 2);
        project.Edit("경로", ProjectChange.Settings,
            () => { project.Export.Folder = folder; project.Export.FileName = fileName; project.Export.WriteMeta = true; });
        ProjectIO.SaveWithExport(project, oldPath);
        var custom = ProjectExport.GetExportImagePath(project);
        var meta = File.ReadAllBytes(custom + ".meta");
        Assert.Empty(ProjectIO.GetOwnedDefaultExportPaths(oldPath));

        ProjectIO.RenameWithOwnedDefaultExport(oldPath, newPath);

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
        Assert.Empty(ProjectIO.GetOwnedDefaultExportPaths(newPath));
        Assert.True(File.Exists(custom));
        Assert.Equal(meta, File.ReadAllBytes(custom + ".meta"));
    }

    [Fact]
    public void RenameProject_RejectsOrphanDestinationMeta()
    {
        using var dir = new TempDir();
        var oldPath = dir.File("hero.gpixel");
        var newPath = dir.File("renamed.gpixel");
        ProjectIO.SaveWithExport(new SpriteProject(2, 2), oldPath);
        File.WriteAllText(dir.File("renamed.png.meta"), "unrelated");

        Assert.Throws<IOException>(() => ProjectIO.RenameWithOwnedDefaultExport(oldPath, newPath));

        Assert.True(File.Exists(oldPath));
        Assert.True(File.Exists(dir.File("hero.png")));
        Assert.False(File.Exists(newPath));
        Assert.Equal("unrelated", File.ReadAllText(dir.File("renamed.png.meta")));
    }

    [Fact]
    public void RenameProject_CaseOnlyNameChangeMovesExportAndMeta()
    {
        using var dir = new TempDir();
        var oldPath = dir.File("Hero.gpixel");
        var newPath = dir.File("hero.gpixel");
        var project = new SpriteProject(2, 2);
        project.Edit("메타", ProjectChange.Settings, () => project.Export.WriteMeta = true);
        ProjectIO.SaveWithExport(project, oldPath);
        var guid = UnityMeta.Read(dir.File("Hero.png.meta")).Guid;

        ProjectIO.RenameWithOwnedDefaultExport(oldPath, newPath);

        Assert.True(File.Exists(newPath));
        Assert.True(File.Exists(dir.File("hero.png")));
        Assert.Equal(guid, UnityMeta.Read(dir.File("hero.png.meta")).Guid);
        Assert.Equal("hero.png", Path.GetFileName(ProjectIO.GetOwnedDefaultExportPaths(newPath)[0]));
    }

    [Fact]
    public void DisabledExport_DoesNotClaimSameNamedUnrelatedFiles()
    {
        using var dir = new TempDir();
        var oldPath = dir.File("hero.gpixel");
        var newPath = dir.File("renamed.gpixel");
        var project = new SpriteProject(2, 2);
        project.Edit("끄기", ProjectChange.Settings, () => project.Export.ExportOnSave = false);
        ProjectIO.SaveWithExport(project, oldPath);
        var unrelated = dir.File("hero.png");
        File.WriteAllText(unrelated, "user image");

        Assert.Empty(ProjectIO.GetOwnedDefaultExportPaths(oldPath));
        ProjectIO.RenameWithOwnedDefaultExport(oldPath, newPath);

        Assert.True(File.Exists(newPath));
        Assert.Equal("user image", File.ReadAllText(unrelated));
        Assert.Empty(ProjectIO.GetOwnedDefaultExportPaths(newPath));
    }

    [Fact]
    public void ExportFolderAndFileNameOverride()
    {
        var p = new SpriteProject(2, 2);
        p.Edit("경로", ProjectChange.Settings, () => { p.Export.Folder = "../sheets"; p.Export.FileName = "char"; });
        var game = Path.Combine(Path.GetTempPath(), "game");
        var png = ProjectExport.GetExportImagePath(p, Path.Combine(game, "src", "hero.gpixel"));
        Assert.Equal(Path.GetFullPath(Path.Combine(game, "sheets", "char.png")), png);
        Assert.Throws<InvalidOperationException>(() => ProjectExport.GetExportImagePath(new SpriteProject(1, 1)));
    }

    // ───────────── 가져오기 ─────────────

    [Fact]
    public void Import_SheetToFrames_ThenExport_EqualsOriginalSlices()
    {
        using var dir = new TempDir();
        var png = dir.File("hero.png");
        // 원본 시트: 16x32 칸 4x2, 칸 이름 지정, 아래 가운데 피벗, 애니메이션 2개
        var doc = new Document(TestUtil.RandomImage(64, 64, opaque: true), png);
        doc.Sprite.Mode = SpriteMode.Multiple;
        doc.Sprite.PixelsPerUnit = 32;
        string[] names = ["d0", "d1", "d2", "d3", "u0", "u1", "u2", "u3"];
        for (int i = 0; i < 8; i++)
            doc.Slices.Add(new Slice(names[i], new Rectangle(i % 4 * 16, i / 4 * 32, 16, 32), Pivot.BottomCenter));
        var down = new AnimationClip("walk_down");
        foreach (var n in names[..4]) down.AddFrame(n, 120);
        var up = new AnimationClip("walk_up") { Loop = false };
        foreach (var n in names[4..]) up.AddFrame(n, 80);
        doc.Animations.Add(down);
        doc.Animations.Add(up);
        DocumentIO.Save(doc, png, writeMeta: true);
        TestUtil.WriteLegacySidecar(doc, png); // 태그는 예전 사이드카의 애니메이션에서
        var originalMeta = UnityMeta.Read(png + ".meta");

        var opened = ProjectImport.Open(png);
        Assert.True(opened.Imported);
        var p = opened.Project;
        Assert.Equal(dir.File("hero.gpixel"), p.Path);
        Assert.False(p.IsDirty);
        Assert.Equal((16, 32), (p.Width, p.Height));
        Assert.Equal(8, p.Frames.Count);
        Assert.Equal(Pivot.BottomCenter, p.Sprite.DefaultPivot);
        Assert.All(p.Frames, f => Assert.Null(f.Pivot));
        Assert.Equal(32f, p.Sprite.PixelsPerUnit);
        Assert.Equal((SheetGrid.Columns, 4), (p.Export.Common.Grid, p.Export.Common.GridCount));
        Assert.Equal(["walk_down", "walk_up"], p.Tags.Select(t => t.Name));
        Assert.Equal((0, 3), (p.Tags[0].From, p.Tags[0].To));
        Assert.Equal((4, 7), (p.Tags[1].From, p.Tags[1].To));
        Assert.False(p.Tags[1].Loop);
        Assert.Equal(120, p.Frames[0].DurationMs);
        Assert.Equal(80, p.Frames[7].DurationMs);
        Assert.True(p.Flatten(5).ContentEquals(doc.Image.Crop(new Rectangle(16, 32, 16, 32))));

        // 저장(.gpixel + 내보내기) → 시트·칸·guid·ID가 원본과 같다
        ProjectIO.SaveWithExport(p);
        Assert.True(File.Exists(dir.File("hero.gpixel")));
        var exported = PngIO.Load(png);
        Assert.True(exported.ContentEquals(doc.Image));
        var meta = UnityMeta.Read(png + ".meta");
        Assert.Equal(originalMeta.Guid, meta.Guid);
        Assert.Equal(originalMeta.Sprites.Select(s => (s.Name, s.UnityRect, s.Pivot, s.SpriteId, s.InternalId)),
            meta.Sprites.Select(s => (s.Name, s.UnityRect, s.Pivot, s.SpriteId, s.InternalId)));

        // 이제 PNG를 열면 .gpixel이 열린다
        var reopened = ProjectImport.Open(png);
        Assert.False(reopened.Imported);
        Assert.Equal(png, reopened.SourcePngPath);
        Assert.Equal(dir.File("hero.gpixel"), reopened.Project.Path);
    }

    [Fact]
    public void Import_DifferentSizesAndPivots_AlignsPivotsOnCanvas()
    {
        var img = new PixelImage(30, 20);
        img.FillRect(new Rectangle(0, 0, 10, 20), Red);
        img.FillRect(new Rectangle(10, 0, 20, 10), Blue);
        var doc = new Document(img, @"C:\x\mix.png");
        doc.Sprite.Mode = SpriteMode.Multiple;
        doc.Slices.Add(new Slice("tall", new Rectangle(0, 0, 10, 20), Pivot.BottomCenter)); // 피벗 (5,20)
        doc.Slices.Add(new Slice("wide", new Rectangle(10, 0, 20, 10), Pivot.BottomCenter)); // 피벗 (10,10)
        var p = ProjectImport.FromDocument(doc);
        Assert.Equal((20, 20), (p.Width, p.Height));
        Assert.Equal(Pivot.BottomCenter, p.Sprite.DefaultPivot); // 공통 점 (10,20)
        Assert.Equal(Red, p.Flatten(0).Get(5, 0));               // tall은 x 5~14
        Assert.Equal(Color32.Transparent, p.Flatten(0).Get(4, 0));
        Assert.Equal(Blue, p.Flatten(1).Get(0, 10));              // wide는 y 10~19
        Assert.Equal(Color32.Transparent, p.Flatten(1).Get(0, 9));
    }

    [Fact]
    public void Import_AnimationsBecomeContiguousTags()
    {
        var img = TestUtil.RandomImage(80, 16);
        var doc = new Document(img, @"C:\x\a.png");
        doc.Sprite.Mode = SpriteMode.Multiple;
        string[] n = ["a", "b", "c", "d", "e"];
        for (int i = 0; i < 5; i++) doc.Slices.Add(new Slice(n[i], new Rectangle(i * 16, 0, 16, 16), Pivot.Center));
        void Anim(string name, params string[] frames)
        {
            var clip = new AnimationClip(name);
            foreach (var f in frames) clip.AddFrame(f, 50);
            doc.Animations.Add(clip);
        }
        Anim("rev", "c", "b");          // 역방향 연속
        Anim("pp", "a", "b", "c", "b"); // 왕복
        Anim("scatter", "e", "d");      // 역방향 연속(d,e)
        Anim("jump", "a", "e");         // 떨어짐 + 서로 다름이지만 a는 pp 태그에 속함 → 복제
        Anim("loop", "d", "d");         // 반복 → 복제

        var p = ProjectImport.FromDocument(doc);
        var t = p.Tags.ToDictionary(x => x.Name);
        Assert.Equal((1, 2, TagDirection.Reverse), (t["rev"].From, t["rev"].To, t["rev"].Direction));
        Assert.Equal((0, 2, TagDirection.PingPong), (t["pp"].From, t["pp"].To, t["pp"].Direction));
        Assert.Equal((3, 4, TagDirection.Reverse), (t["scatter"].From, t["scatter"].To, t["scatter"].Direction));
        Assert.Equal((5, 6), (t["jump"].From, t["jump"].To));
        Assert.Equal((7, 8), (t["loop"].From, t["loop"].To));
        Assert.Equal(9, p.Frames.Count);
        Assert.True(p.Flatten(5).ContentEquals(p.Flatten(0))); // 복제된 a
        Assert.True(p.Flatten(8).ContentEquals(p.Flatten(3))); // 복제된 d

        // 다른 태그에 속하지 않은 떨어진 프레임은 재배치
        var doc2 = new Document(img, @"C:\x\b.png");
        doc2.Sprite.Mode = SpriteMode.Multiple;
        doc2.Slices.AddRange(doc.Slices.Select(s => s.Clone()));
        var clip2 = new AnimationClip("hop");
        clip2.AddFrame("e", 70);
        clip2.AddFrame("b", 70);
        doc2.Animations.Add(clip2);
        var q = ProjectImport.FromDocument(doc2);
        Assert.Equal(["a", "e", "b", "c", "d"], q.Frames.Select(f => f.Name));
        Assert.Equal((1, 2), (q.Tags[0].From, q.Tags[0].To));
        Assert.Equal(70, q.Frames[1].DurationMs);
    }

    [Fact]
    public void Import_PngWithoutSlices_IsOneFrame()
    {
        using var dir = new TempDir();
        var png = dir.File("tile.png");
        PngIO.Save(TestUtil.RandomImage(8, 4, opaque: true), png);
        var r = ProjectImport.Open(png, new SpriteSettings { PixelsPerUnit = 8 });
        Assert.True(r.Imported);
        Assert.Single(r.Project.Frames);
        Assert.Equal((8, 4), (r.Project.Width, r.Project.Height));
        Assert.Equal(8f, r.Project.Sprite.PixelsPerUnit);
        Assert.Equal(SpriteMode.Single, r.Project.Sprite.Mode);
        Assert.True(r.Project.Flatten(0).ContentEquals(PngIO.Load(png)));
    }

    // ───────────── 재생 ─────────────

    [Fact]
    public void Playback_FollowsTagDirection()
    {
        var p = MakeWalkProject();
        p.SetFrameDuration(1, 200);
        var walk = ProjectPlayback.BuildFrames(p, "walk");
        Assert.Equal([0, 1, 2], walk.Select(f => f.FrameIndex));
        Assert.Equal(200, walk[1].Ms);
        Assert.Equal(Pivot.BottomCenter, walk[0].Pivot);
        Assert.True(walk[2].Image.ContentEquals(p.Flatten(2)));

        p.SetTag(0, direction: TagDirection.Reverse);
        Assert.Equal([2, 1, 0], ProjectPlayback.BuildFrames(p, "walk").Select(f => f.FrameIndex));
        p.SetTag(0, direction: TagDirection.PingPong);
        var pp = ProjectPlayback.BuildFrames(p, "walk");
        Assert.Equal([0, 1, 2, 1], pp.Select(f => f.FrameIndex));
        Assert.Same(pp[1].Image, pp[3].Image);
        Assert.Empty(ProjectPlayback.BuildFrames(p, "없음"));
        Assert.Equal(5, ProjectPlayback.BuildAllFrames(p).Count);

        Assert.Equal(1, ProjectPlayback.FrameAt(pp, 150, loop: true));   // 0..100 | 100..300
        Assert.Equal(0, ProjectPlayback.FrameAt(pp, 600, loop: true));   // 합계 600 → 0
        Assert.Equal(3, ProjectPlayback.FrameAt(pp, 9999, loop: false));

        var td = new TopDownController();
        Assert.Equal(new AnimationChoice("idle", false), td.SelectAnimation(p.HasTag));
        p.AddTag("idle_down", 3, 3);
        Assert.Equal(new AnimationChoice("idle_down", false), td.SelectAnimation(p.HasTag));
    }
}
