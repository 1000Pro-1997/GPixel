using System.Text.Json;
using Gpixel.Core.AiDraw;
using Gpixel.Core.Model;
using Gpixel.Core.Pixelate;
using Gpixel.Core.References;

namespace Gpixel.Core.Tests;

public class AiDrawTests
{
    private static readonly Color32 Red = new(210, 40, 40), Dark = new(30, 15, 15), White = new(255, 255, 255);

    // ───────────── 설정 ─────────────

    [Fact]
    public void Settings_choose_generate_direct_or_auto_checked_direct_across_providers()
    {
        var s = new AiDrawSettings();
        Assert.Equal(AiDrawMode.Generate, s.EffectiveMode("codex"));
        Assert.Equal(AiDrawMode.DirectAutoCheck, s.EffectiveMode("claude"));
        s.Mode = AiDrawMode.Direct;
        Assert.Equal(AiDrawMode.Direct, s.EffectiveMode("codex"));
        Assert.Equal(AiDrawMode.Direct, s.EffectiveMode("claude"));
        s.Mode = AiDrawMode.DirectAutoCheck;
        Assert.Equal(AiDrawMode.DirectAutoCheck, s.EffectiveMode("codex"));
        Assert.Equal(AiDrawMode.DirectAutoCheck, JsonSerializer.Deserialize<AiDrawSettings>(JsonSerializer.Serialize(s))!.EffectiveMode("claude"));
    }

    [Fact]
    public void Settings_frames_use_defaults_and_clamp()
    {
        var s = new AiDrawSettings();
        Assert.Equal(6, s.FramesFor(AiDrawActions.Walk));
        Assert.Equal(4, s.FramesFor(AiDrawActions.Idle));
        Assert.Equal(4, s.FramesFor("wave"));
        s.FrameCounts[AiDrawActions.Walk] = 40;
        Assert.Equal(AiDrawSettings.MaxFrames, s.FramesFor(AiDrawActions.Walk));
        s.FrameCounts.Remove(AiDrawActions.Attack);
        Assert.Equal(6, s.FramesFor(AiDrawActions.Attack));
        s.SizeTarget = 3;
        Assert.Equal(AiDrawSettings.MinSize, s.SizeTargetClamped);
        s.SizeTarget = 0;
        Assert.Equal(AiDrawSettings.DefaultSize, s.SizeTargetClamped);
    }

    [Fact]
    public void Sizes_are_hints_unless_result_size_correction_is_on()
    {
        var s = new AiDrawSettings { SizeTarget = 48 };
        Assert.False(s.ResizeResult);
        // 끔: 채팅 크기는 프롬프트 힌트만, 변환은 자동(0). 설정 크기는 쓰지 않음.
        Assert.Equal((64, 0), s.SizesFor(64));
        Assert.Equal((0, 0), s.SizesFor(null));
        Assert.Equal((AiDrawSettings.MaxSize, 0), s.SizesFor(999));
        // 켬: 채팅 크기, 없으면 설정 크기로 고정.
        s.ResizeResult = true;
        Assert.Equal((64, 64), s.SizesFor(64));
        Assert.Equal((48, 48), s.SizesFor(null));
        s.SizeTarget = 0;
        Assert.Equal((AiDrawSettings.DefaultSize, AiDrawSettings.DefaultSize), s.SizesFor(null));
    }

    // ───────────── 프레임 계획 ─────────────

    [Fact]
    public void Plan_without_action_is_one_frame()
    {
        var plan = AiDrawPlanner.Plan(null, 6, ["", "waving"]);
        var f = Assert.Single(plan);
        Assert.Equal(1, f.Count);
        Assert.Equal("waving", f.Pose);
    }

    [Fact]
    public void Plan_walk_six_uses_each_key_pose_once()
    {
        var plan = AiDrawPlanner.Plan("walk", 6, null);
        Assert.Equal(6, plan.Count);
        Assert.Equal(AiDrawActions.KeyPoses("walk"), plan.Select(f => f.Pose));
        Assert.All(plan, f => Assert.Equal(6, f.Count));
        Assert.Equal(Enumerable.Range(0, 6), plan.Select(f => f.Index));
    }

    [Fact]
    public void Plan_samples_key_poses_with_in_betweens_and_loops()
    {
        var keys = AiDrawActions.KeyPoses("walk");
        var plan = AiDrawPlanner.Plan("walk", 4, null);
        Assert.Equal(keys[0], plan[0].Pose);
        Assert.StartsWith("in-between", plan[1].Pose);
        Assert.Equal(keys[3], plan[2].Pose);
        Assert.Contains(keys[4], plan[3].Pose);
        Assert.Contains(keys[5], plan[3].Pose);
        // 되풀이 동작: 마지막 사이 자세는 첫 자세로 되돌아간다(대기 4열쇠, 7프레임 → 마지막 t = 3.43).
        var idle = AiDrawActions.KeyPoses("idle");
        var last = AiDrawPlanner.Plan("idle", 7, null)[^1].Pose;
        Assert.Equal($"in-between pose halfway from \"{idle[3]}\" to \"{idle[0]}\"", last);
    }

    [Fact]
    public void Plan_one_shot_runs_from_first_to_last_key()
    {
        var keys = AiDrawActions.KeyPoses("attack");
        var plan = AiDrawPlanner.Plan("attack", 3, null);
        Assert.Equal(keys[0], plan[0].Pose);
        Assert.Equal(keys[^1], plan[^1].Pose);
    }

    [Fact]
    public void Plan_given_poses_win_and_frame_count_is_clamped()
    {
        var plan = AiDrawPlanner.Plan("jump", 3, ["crouch low", "", "in the air"]);
        Assert.Equal("crouch low", plan[0].Pose);
        Assert.NotEqual("", plan[1].Pose);
        Assert.Equal("in the air", plan[2].Pose);
        Assert.Equal(AiDrawSettings.MaxFrames, AiDrawPlanner.Plan("walk", 99, null).Count);
        Assert.Equal(AiDrawSettings.MinFrames, AiDrawPlanner.Plan("walk", 1, null).Count);
    }

    [Fact]
    public void Plan_unknown_action_describes_frames()
    {
        var plan = AiDrawPlanner.Plan("wave", 3, null);
        Assert.Contains("frame 1 of 3", plan[0].Pose);
        Assert.Contains("\"wave\"", plan[2].Pose);
        Assert.Contains("looping", plan[2].Pose);
    }

    // ───────────── 프롬프트 ─────────────

    [Fact]
    public void Prompt_asks_for_clean_pixel_art_on_transparent_background()
    {
        var r = new AiDrawRequest { Subject = "a small knight in silver armor." };
        Assert.True(r.Transparent);
        var p = AiDrawPrompts.Frame(r, new AiDrawFrame(0, 1, ""), []);
        Assert.StartsWith("Pixel-art game character sprite: a small knight in silver armor.", p);
        Assert.Contains("1-pixel dark outline", p);
        Assert.Contains("no anti-aliasing", p);
        Assert.Contains("limited palette", p);
        Assert.Contains("full body", p);
        Assert.Contains("plain transparent background, no scenery", p);
        Assert.DoesNotContain("Animation", p);
        Assert.DoesNotContain("Attached images", p);
    }

    [Fact]
    public void Prompt_keeps_requested_background_and_size()
    {
        var r = new AiDrawRequest { Subject = "a red potion", Kind = "item", Background = "a wooden table", SizeTarget = 32 };
        Assert.False(r.Transparent);
        var p = AiDrawPrompts.Frame(r, new AiDrawFrame(0, 1, ""), []);
        Assert.Contains("game item icon", p);
        Assert.Contains("Background: a wooden table", p);
        Assert.DoesNotContain("transparent", p);
        Assert.Contains("about 32x32 pixels", p);
    }

    [Fact]
    public void Prompt_for_animation_frame_names_pose_and_reference_roles_in_order()
    {
        var r = new AiDrawRequest { Subject = "a slime", Action = "walk" };
        var p = AiDrawPrompts.Frame(r, new AiDrawFrame(2, 6, "passing pose"), [AiDrawImageRole.FirstFrame, AiDrawImageRole.PreviousFrame]);
        Assert.Contains("Animation \"walk\", frame 3 of 6. Pose in this frame: passing pose.", p);
        Assert.Contains("side view facing right", p);
        int first = p.IndexOf("- Image 1: frame 1 of this animation", StringComparison.Ordinal);
        int prev = p.IndexOf("- Image 2: the previous frame", StringComparison.Ordinal);
        Assert.True(first >= 0 && prev > first);
    }

    [Fact]
    public void Prompt_style_reference_is_style_only()
    {
        var r = new AiDrawRequest { Subject = "the girl from the photo" };
        var p = AiDrawPrompts.Frame(r, new AiDrawFrame(0, 1, ""), [AiDrawImageRole.Subject, AiDrawImageRole.Style]);
        Assert.Contains("- Image 1: the subject to draw", p);
        Assert.Contains("- Image 2: pixel-art style reference only", p);
        Assert.Contains("do NOT copy its subject", p);
    }

    [Fact]
    public void Codex_instruction_calls_tool_once_with_transparency_flag()
    {
        var t = AiDrawPrompts.CodexInstruction("PROMPT", transparent: true, attachedImages: 0);
        Assert.Contains("exactly once", t);
        Assert.Contains("transparent_background set to true", t);
        Assert.EndsWith("Prompt:\nPROMPT", t);
        Assert.DoesNotContain("attached image", t);
        Assert.Contains("transparent_background set to false", AiDrawPrompts.CodexInstruction("P", false, 2));
        Assert.Contains("attached image", AiDrawPrompts.CodexInstruction("P", false, 2));
    }

    // ───────────── 스타일 참고 고르기 ─────────────

    private static ReferenceItem Ref(string id, string kind, int size, bool builtIn = false, int colors = 8)
        => new() { Id = id, Kind = kind, Width = size, Height = size, Colors = colors, BuiltIn = builtIn, Frames = 1 };

    [Fact]
    public void Style_pick_prefers_kind_then_size()
    {
        var items = new[] { Ref("sword", "item", 32), Ref("hero16", "character", 16), Ref("hero32", "character", 32), Ref("note", "other", 0) };
        Assert.Equal("hero32", AiDrawStyle.Pick(items, null, "character", 0)!.Id);
        Assert.Equal("hero16", AiDrawStyle.Pick(items, null, "character", 16)!.Id);
        Assert.Equal("sword", AiDrawStyle.Pick(items, null, "item", 0)!.Id);
    }

    [Fact]
    public void Style_pick_uses_text_scores_and_can_require_them()
    {
        var items = new[] { Ref("builtin/hero", "character", 32, builtIn: true), Ref("my/slime", "character", 16) };
        var scores = new Dictionary<string, double> { ["my/slime"] = 1.0 };
        Assert.Equal("my/slime", AiDrawStyle.Pick(items, scores, "character", 32)!.Id);
        Assert.Equal("my/slime", AiDrawStyle.Pick(items, scores, "character", 32, requireText: true)!.Id);
        Assert.Null(AiDrawStyle.Pick(items, null, "character", 32, requireText: true));
        Assert.Null(AiDrawStyle.Pick([], null, "character", 32));
    }

    [Fact]
    public void Style_pick_prefers_user_reference_on_tie()
    {
        var items = new[] { Ref("builtin/a", "character", 32, builtIn: true), Ref("user/b", "character", 32) };
        Assert.Equal("user/b", AiDrawStyle.Pick(items, null, "character", 32)!.Id);
    }

    // ───────────── 프레임 잇기·변환 ─────────────

    [Fact]
    public void Combine_pads_frames_to_common_size_side_by_side()
    {
        var a = new PixelImage(4, 4);
        a.Clear(White);
        var b = new PixelImage(2, 6);
        b.Pixels[1] = Red;
        var strip = AiDrawConversion.Combine([a, b]);
        Assert.Equal(8, strip.Width);
        Assert.Equal(6, strip.Height);
        // a(흰 바탕)는 가운데에, 남는 곳도 흰색(왼쪽 위 픽셀 색).
        Assert.Equal(White, strip.Get(0, 0));
        Assert.Equal(White, strip.Get(2, 3));
        // b(투명 바탕)는 가로 가운데(4 + 1): 빨강이 (6, 0), 남는 곳은 투명.
        Assert.Equal(Red, strip.Get(6, 0));
        Assert.Equal(0, strip.Get(4, 3).A);
        Assert.Same(a, AiDrawConversion.Combine([a]));
    }

    [Fact]
    public void Downscale_averages_blocks_weighted_by_alpha()
    {
        var img = new PixelImage(4, 2);
        img.Pixels[0] = Red;
        img.Pixels[1] = Red;
        img.Pixels[4] = Red;
        img.Pixels[5] = Red;
        img.Pixels[2] = Red; // 반쯤 칠한 칸
        var small = AiDrawConversion.Downscale(img, 2);
        Assert.Equal(2, small.Width);
        Assert.Equal(1, small.Height);
        Assert.Equal(Red, small.Pixels[0]);
        Assert.Equal(Red.R, small.Pixels[1].R);
        Assert.Equal(63, small.Pixels[1].A);
        Assert.Same(img, AiDrawConversion.Downscale(img, 8));
    }

    [Fact]
    public void Conversion_settings_split_frames_share_palette_and_fix_size()
    {
        var s = AiDrawConversion.Settings(new PixelateSettings(), 4, 48, removeBackground: true, keepOffsets: false);
        Assert.Equal(FrameSplitMode.Grid, s.Frames.Mode);
        Assert.Equal(4, s.Frames.Columns);
        Assert.Equal(1, s.Frames.Rows);
        Assert.Equal(FrameAlign.BottomCenter, s.Frames.Align);
        Assert.True(s.Palette.Shared);
        Assert.Equal(BackgroundMode.Auto, s.Background.Mode);
        Assert.Equal(OutputSizeMode.FixedSize, s.Size.Mode);
        Assert.Equal(48, s.Size.Width);
        Assert.Equal(OutlineFit.Shrink, s.Cleanup.OutlineFit);

        var one = AiDrawConversion.Settings(new PixelateSettings(), 1, 0, removeBackground: false, keepOffsets: true);
        Assert.Equal(FrameSplitMode.Single, one.Frames.Mode);
        Assert.Equal(BackgroundMode.None, one.Background.Mode);
        Assert.Equal(OutputSizeMode.FromScale, one.Size.Mode);
        Assert.Equal(FrameAlign.KeepOffsets, AiDrawConversion.Settings(new PixelateSettings(), 3, 0, true, keepOffsets: true).Frames.Align);
    }

    /// <summary>16×16 원 스프라이트(빨강 + 1px 어두운 외곽선, 흰 바탕)를 <paramref name="scale"/>배로 키운 그림 — 생성 그림 흉내.</summary>
    private static PixelImage Generated(int scale, int shift)
    {
        var img = new PixelImage(24 * scale, 24 * scale);
        img.Clear(White);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                double d = Math.Sqrt((x - 7.5) * (x - 7.5) + (y - 7.5 - shift) * (y - 7.5 - shift));
                if (d > 7) continue;
                var c = d > 6 ? Dark : Red;
                img.FillRect(new System.Drawing.Rectangle((x + 4) * scale, (y + 4) * scale, scale, scale), c);
            }
        return img;
    }

    [Fact]
    public void Conversion_runs_frames_together_with_one_grid()
    {
        var strip = AiDrawConversion.Combine([Generated(8, 0), Generated(8, 1)]);
        var settings = AiDrawConversion.Settings(new PixelateSettings(), 2, 0, removeBackground: true, keepOffsets: false);
        var (result, used) = AiDrawConversion.Run(strip, settings, optimize: false, new PixelateOptimizeOptions());
        var frames = result.CommonFrames();
        Assert.Equal(2, frames.Count);
        Assert.Equal(frames[0].Size, frames[1].Size);
        Assert.InRange(frames[0].Width, 14, 22);
        Assert.Equal(settings, used);
        // 배경은 지워지고 스프라이트 색이 남는다.
        Assert.Equal(0, frames[0].Get(0, 0).A);
        Assert.Contains(frames[0].Pixels, p => p.A == 255 && p.R > 150 && p.G < 100);
    }

    [Fact]
    public void Conversion_without_sprite_throws()
    {
        var blank = new PixelImage(40, 40);
        blank.Clear(White);
        Assert.Throws<InvalidOperationException>(() =>
            AiDrawConversion.Run(blank, AiDrawConversion.Settings(new PixelateSettings(), 1, 0, true, false), false, new PixelateOptimizeOptions()));
    }

    // ───────────── 스프라이트 시트 ─────────────

    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(4, 4, 1)]
    [InlineData(6, 6, 1)]
    [InlineData(7, 4, 2)]
    [InlineData(8, 4, 2)]
    [InlineData(12, 6, 2)]
    public void Sheet_layout_uses_one_row_up_to_six_then_two_rows(int frames, int columns, int rows)
        => Assert.Equal(new AiDrawSheetLayout(columns, rows), AiDrawSheetLayout.For(frames));

    [Fact]
    public void Sheet_prompt_lists_frames_in_one_row()
    {
        var r = new AiDrawRequest { Subject = "a tiny knight", Action = "walk" };
        var plan = AiDrawPlanner.Plan("walk", 4, null);
        var p = AiDrawPrompts.Sheet(r, plan, AiDrawSheetLayout.For(4), []);
        Assert.Contains("single wide landscape image containing exactly 4 frames", p);
        Assert.Contains("\"walk\" cycle", p);
        Assert.Contains("ONE horizontal row, ordered left to right", p);
        Assert.Contains("same baseline", p);
        Assert.Contains("1: " + plan[0].Pose, p);
        Assert.Contains("4: " + plan[3].Pose.TrimEnd('.'), p);
        Assert.DoesNotContain("5: ", p);
        Assert.Contains("plain transparent background", p);
        Assert.Contains("no grid lines", p);
        Assert.Contains("No labels, no frame numbers", p);
        Assert.DoesNotContain("Attached images", p);
    }

    [Fact]
    public void Sheet_prompt_names_grid_for_many_frames_and_attached_roles()
    {
        var r = new AiDrawRequest { Subject = "a slime", Action = "attack" };
        var plan = AiDrawPlanner.Plan("attack", 7, null);
        var p = AiDrawPrompts.Sheet(r, plan, AiDrawSheetLayout.For(7), [AiDrawImageRole.Subject, AiDrawImageRole.Style]);
        Assert.Contains("exactly 7 frames", p);
        Assert.Contains("\"attack\" animation", p);
        Assert.Contains("a grid of 4 columns by 2 rows", p);
        Assert.Contains("frames 1-4 in the top row, frames 5-7 in the bottom row", p);
        Assert.Contains("7: ", p);
        Assert.Contains("- Image 1: the subject to draw", p);
        Assert.Contains("- Image 2: pixel-art style reference only", p);
        Assert.Contains("wide landscape size", AiDrawPrompts.CodexInstruction(p, true, 2, landscape: true));
        Assert.DoesNotContain("landscape size", AiDrawPrompts.CodexInstruction(p, true, 2));
    }

    [Fact]
    public void Sheet_split_keeps_auto_when_frame_count_matches()
    {
        var sheet = AiDrawConversion.Combine([Generated(4, 0), Generated(4, 1), Generated(4, 0), Generated(4, 1)]);
        var settings = AiDrawConversion.Settings(new PixelateSettings(), 4, 0, removeBackground: true, keepOffsets: false);
        var (src, used) = AiDrawConversion.PrepareSheet(sheet, settings, 4, AiDrawSheetLayout.For(4), keepOffsets: false);
        Assert.Equal(FrameSplitMode.Auto, used.Frames.Mode);
        Assert.Equal(FrameAlign.BottomCenter, used.Frames.Align);
        Assert.Equal(4, src.Split.Frames.Count);
        var (result, _) = AiDrawConversion.Run(src, used, optimize: false, new PixelateOptimizeOptions());
        var frames = result.CommonFrames();
        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.Equal(frames[0].Size, f.Size));
    }

    [Fact]
    public void Sheet_split_falls_back_to_grid_when_frames_touch()
    {
        var sheet = AiDrawConversion.Combine([Generated(4, 0), Generated(4, 0), Generated(4, 0), Generated(4, 0)]);
        // 모든 프레임을 잇는 바닥 띠: 빈 열이 없어 자동 나누기는 한 덩어리로 본다.
        sheet.FillRect(new System.Drawing.Rectangle(0, 20 * 4, sheet.Width, 8), Dark);
        var settings = AiDrawConversion.Settings(new PixelateSettings(), 4, 0, removeBackground: true, keepOffsets: true);
        var (src, used) = AiDrawConversion.PrepareSheet(sheet, settings, 4, AiDrawSheetLayout.For(4), keepOffsets: true);
        Assert.Equal(FrameSplitMode.Grid, used.Frames.Mode);
        Assert.Equal(4, used.Frames.Columns);
        Assert.Equal(1, used.Frames.Rows);
        Assert.Equal(FrameAlign.KeepOffsets, used.Frames.Align);
        Assert.Equal(4, src.Split.Frames.Count);
    }

    [Fact]
    public void Sheet_split_uses_two_row_grid_from_layout()
    {
        var row = AiDrawConversion.Combine([Generated(2, 0), Generated(2, 0), Generated(2, 0), Generated(2, 0)]);
        var sheet = new PixelImage(row.Width, row.Height * 2);
        sheet.Blit(row, 0, 0);
        sheet.Blit(row, 0, row.Height);
        sheet.FillRect(new System.Drawing.Rectangle(0, 20 * 2, sheet.Width, 4), Dark);
        sheet.FillRect(new System.Drawing.Rectangle(0, row.Height + 20 * 2, sheet.Width, 4), Dark);
        var settings = AiDrawConversion.Settings(new PixelateSettings(), 7, 0, true, false);
        var (_, used) = AiDrawConversion.PrepareSheet(sheet, settings, 7, AiDrawSheetLayout.For(7), keepOffsets: false);
        Assert.Equal(FrameSplitMode.Grid, used.Frames.Mode);
        Assert.Equal(4, used.Frames.Columns);
        Assert.Equal(2, used.Frames.Rows);
    }

    // ───────────── 생성 기록 ─────────────

    [Fact]
    public void Job_store_saves_loads_and_prunes()
    {
        using var dir = new TempDir();
        var store = new AiDrawJobStore(dir.Path);
        var source = Generated(2, 0);
        var settings = AiDrawConversion.Settings(new PixelateSettings(), 3, 32, true, false);
        string first = store.Create();
        Assert.False(store.Exists(first));
        store.Save(new AiDrawJob { Id = first, Name = "knight", Frames = 3, Action = "walk", Settings = PixelatePresets.SerializeSettings(settings, false) }, source);
        Assert.True(store.Exists(first));
        var loaded = store.Load(first);
        Assert.NotNull(loaded);
        Assert.Equal("knight", loaded.Value.Job.Name);
        Assert.Equal(3, loaded.Value.Job.Frames);
        Assert.Equal(settings.Key(), loaded.Value.Job.ReadSettings().Key());
        Assert.True(source.ContentEquals(loaded.Value.Source));
        Assert.Null(store.Load("missing"));

        // 이름 순(만든 때)으로 오래된 것부터 지우고 방금 것은 남긴다.
        Directory.CreateDirectory(Path.Combine(dir.Path, "00000000-old"));
        store.Prune(1, first);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "00000000-old")));
        Assert.True(store.Exists(first));
        Assert.Throws<ArgumentException>(() => store.DirectoryOf("../x"));
    }
}
