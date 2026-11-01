using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Gpixel.App;
using Gpixel.App.Localization;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using static GuideShots.ScreenDemo;

namespace GuideShots;

/// <summary>화면 구성: 타임라인, 고르기, 편집, 태그, 레이어 효과. 번호는 쪽(h3)마다 이어진다.</summary>
internal static class ScreenTimelineShots
{
    // TimelinePanel.TimelineGrid의 배치(이름 칸 폭, 태그 띠·머리글 높이, 셀 크기, 효과 줄 높이, 이름 칸 안 접기 단추·이름 x).
    private const int NameW = 270, TagH = 20, HeadH = 30, CellH = 36, CellW = 42, FxH = 16, ToggleX = 48, NameX = 64;
    // Build가 만드는 행(위→아래): 소품 그룹, 보석, 반짝이, 몸, 그림자.
    private const int RowProps = 0, RowGem = 1, RowSparkle = 2, RowBody = 3, RowShadow = 4;

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static Control Timeline(MainForm main) => ShotKit.Get<Control>(main, "_timeline");
    private static Control Grid(MainForm main) => Named(Timeline(main), "TimelineGrid");

    /// <summary>격자 좌표 → 창 좌표.</summary>
    private static Point At(MainForm main, double x, double y)
        => Grid(main).TranslatePoint(new Point(x, y), main) ?? throw new InvalidOperationException("grid not attached");

    /// <summary>행 <paramref name="row"/>의 격자 y(<paramref name="fxRows"/> = 그 위에 펼쳐진 효과 줄 수).</summary>
    private static double RowY(int row, int fxRows = 0) => TagH + HeadH + row * CellH + fxRows * FxH;

    private static Point FrameHead(MainForm main, int f) => At(main, NameW + f * CellW + CellW / 2.0, TagH + HeadH / 2.0);
    private static Point Cell(MainForm main, int row, int f, int fxRows = 0) => At(main, NameW + f * CellW + CellW / 2.0, RowY(row, fxRows) + CellH / 2.0);

    private static Rect GridRect(MainForm main, double x, double y, double w, double h)
    {
        var p = At(main, x, y);
        return new Rect(p.X, p.Y, w, h);
    }

    /// <summary>프레임 <paramref name="f0"/>~<paramref name="f1"/> × 행 <paramref name="r0"/>~<paramref name="r1"/> 셀 묶음.</summary>
    private static Rect Cells(MainForm main, int r0, int r1, int f0, int f1, int fxRows = 0)
        => GridRect(main, NameW + f0 * CellW, RowY(r0, fxRows), (f1 - f0 + 1) * CellW, (r1 - r0 + 1) * CellH);

    private static Rect TimelineRect(MainForm main) => ShotKit.Bounds(Timeline(main), main);

    /// <summary>타임라인 전체(프레임 6개까지 보이게 오른쪽을 줄임).</summary>
    private static Rect TimelineCrop(MainForm main, double width = 640)
    {
        var t = TimelineRect(main);
        return new Rect(t.X, t.Y, width, t.Height);
    }

    /// <summary>타임라인 칸을 높여(레이어 추가 줄·효과 줄까지 보이게) 연 메인 창.</summary>
    private static MainForm OpenTall(ShotKit k, int bottom = 330)
    {
        App.Settings.BottomHeight = bottom;
        return Open(k);
    }

    private static int LayerIndex(MainForm main, string name)
    {
        var p = Project(main);
        return p.IndexOf(p.Layers.First(l => l.Name == name));
    }

    // ───────────── 열린 메뉴·팝업 ─────────────

    private static List<Popup> OpenPopups()
        => ((List<Popup>)typeof(ShotKit).GetField("s_popups", Any)!.GetValue(null)!).Where(p => p.IsOpen && p.Host is not null).ToList();

    /// <summary>팝업 안(또는 창 안) 컨트롤의 <paramref name="top"/> 창 좌표 사각형.</summary>
    private static Rect InWindow(Window top, Control c)
    {
        var host = TopLevel.GetTopLevel(c) ?? throw new InvalidOperationException("detached");
        var local = ShotKit.Bounds(c, host);
        if (ReferenceEquals(host, top)) return local;
        var pos = host.PlatformImpl?.GetType().GetProperty("Position", Any)?.GetValue(host.PlatformImpl) is PixelPoint pp ? pp : default;
        return local.Translate(new Vector(pos.X - top.Position.X, pos.Y - top.Position.Y));
    }

    /// <summary>열린 팝업들을 모두 감싸는 창 좌표 사각형.</summary>
    private static Rect? PopupArea(Window top)
    {
        Rect? all = null;
        foreach (var p in OpenPopups())
        {
            if (p.Child is not Control c || !c.IsVisible) continue;
            var r = InWindow(top, c);
            all = all is { } a ? a.Union(r) : r;
        }
        return all;
    }

    private static IEnumerable<MenuItem> MenuItems()
    {
        foreach (var p in OpenPopups())
            if (p.Child is Visual v)
                foreach (var m in ShotKit.All<MenuItem>(v))
                    yield return m;
    }

    private static string HeaderText(MenuItem m) => (m.Header switch
    {
        string s => s,
        TextBlock t => t.Text ?? "",
        _ => m.Header?.ToString() ?? "",
    }).Replace("_", "");

    /// <summary>머리글이 <paramref name="format"/>(형식 문자열이면 {0} 앞부분)으로 시작하는 열린 메뉴 항목.</summary>
    private static MenuItem MenuItem(string format)
    {
        string head = format.Split('{')[0].Replace("_", "");
        return MenuItems().FirstOrDefault(m => HeaderText(m).StartsWith(head, StringComparison.Ordinal))
               ?? throw new InvalidOperationException("no menu item " + head);
    }

    /// <summary>메뉴 맨 위의 선택 표시 줄.</summary>
    private static MenuItem SelectionLine() => MenuItems().First();

    private static Rect Union(Window top, params Control[] controls)
        => controls.Select(c => InWindow(top, c)).Aggregate((a, b) => a.Union(b));

    /// <summary>
    /// <paramref name="target"/>을 (<paramref name="selectFirst"/>면 먼저 눌러 고르고) 오른쪽 아래 끝에서 우클릭한다 — 메뉴가 누른 점 오른쪽에 떠서 누른 칸을 가리지 않게.
    /// </summary>
    private static void RightClick(ShotKit k, MainForm main, Rect target, bool selectFirst = true)
    {
        if (selectFirst) k.Click(main, target.Center);
        k.Click(main, new Point(target.Right - 4, target.Bottom - 4), MouseButton.Right, settleMs: 450);
    }

    /// <summary>레이어 이름 칸 왼쪽(이름 글자)에서 우클릭 — 메뉴가 fx·스킨 칩을 가리지 않게.</summary>
    private static void RightClickName(ShotKit k, MainForm main, int row, int fxRows = 0)
    {
        var p = At(main, 60, RowY(row, fxRows) + CellH / 2.0);
        k.Click(main, p);
        k.Click(main, p, MouseButton.Right, settleMs: 450);
    }

    /// <summary>번호 동그라미가 사각형 왼쪽 위에 붙으므로, 왼쪽을 <paramref name="dx"/>만큼 넓혀 동그라미가 표시한 것을 가리지 않게.</summary>
    private static Rect PadLeft(Rect r, double dx) => new(r.X - dx, r.Y, r.Width + dx, r.Height);

    /// <summary>열린 메뉴 + 누른 곳 + 타임라인 왼쪽 위(어디인지 보이게)를 담아 저장하고 메뉴를 닫는다.</summary>
    private static void SaveMenu(ShotKit k, MainForm main, string name, Rect target, IEnumerable<Mark> marks, int firstNumber)
    {
        var t = TimelineRect(main);
        var area = target.Union(new Rect(t.X, t.Y, 300, 110));
        if (PopupArea(main) is { } menu) area = area.Union(menu);
        area = area.Inflate(12).Intersect(new Rect(main.ClientSize));
        k.Save(main, name, crop: area, marks: marks, firstNumber: firstNumber);
        PopupMenus.CloseAll();
        k.Settle(200);
    }

    // ───────────── 타임라인 ─────────────

    [Shot("screen-timeline")]
    public static void Timeline(ShotKit k)
    {
        var main = OpenTall(k);
        Build(k, main);
        var p = Project(main);
        var ctx = Ctx(main);
        // 그림자 레이어를 잠가 자물쇠가 잘 보이게.
        int shadow = LayerIndex(main, "Shadow");
        ctx.Structural(() => p.SetLayerProperties(shadow, locked: true));
        // 미리보기가 4번째 프레임을 재생 중인 모습(초록 외곽선).
        ShotKit.Set(Grid(main), "PlayingFrame", 3);
        k.Settle(300);

        string title = "Shadow (78%)";
        double nameW = AppTheme.MeasureText("Shadow", AppTheme.FontSize).Width;
        double fullW = AppTheme.MeasureText(title, AppTheme.FontSize).Width;
        double ghostX = NameW + 6 * CellW;
        k.Save(main, crop: TimelineCrop(main), marks:
        [
            GridRect(main, 2, RowY(RowGem), 22, CellH - 2),                           // 1 눈
            GridRect(main, 24, RowY(RowShadow), 22, CellH - 2),                       // 2 자물쇠
            GridRect(main, ToggleX + nameW + 2, RowY(RowShadow) + 8, fullW - nameW, 20), // 3 불투명도
            GridRect(main, ToggleX, RowY(RowProps) + 8, NameX - ToggleX, 20),         // 4 그룹 접기 삼각형
            GridRect(main, NameX + 8, RowY(RowGem) + 4, 110, 2 * CellH - 8),          // 5 부위(들여쓰기)
            GridRect(main, NameW + CellW, TagH, CellW, HeadH),                        // 6 프레임 번호·길이
            Cells(main, RowBody, RowBody, 5, 5),                                      // 7 썸네일
            PadLeft(GridRect(main, NameW, 0, 6 * CellW, TagH), 14),                   // 8 태그 띠
            GridRect(main, ghostX, TagH, CellW, HeadH),                               // 9 빈 프레임 추가
            GridRect(main, ghostX, RowY(0), CellW, 5 * CellH),                        // 10 마지막 프레임 복사
            GridRect(main, 100, RowY(5) + 6, 70, CellH - 12),                         // 11 레이어 추가
            GridRect(main, NameW, RowY(5), 6 * CellW, CellH),                         // 12 맨 아래 레이어 복사
            GridRect(main, NameW + 3 * CellW, TagH, CellW, HeadH + 5 * CellH),        // 13 재생 중인 프레임
        ]);
        ShotKit.Set(Grid(main), "PlayingFrame", -1);
        k.Settle(100);

        // 2: 프레임 머리글 우클릭(프레임 1~3을 고른 채) — 맨 위 선택 표시
        k.Click(main, FrameHead(main, 0));
        k.Click(main, FrameHead(main, 2), mods: RawInputModifiers.Shift);
        var head = GridRect(main, NameW + 2 * CellW, TagH, CellW, HeadH);
        RightClick(k, main, head, selectFirst: false);
        SaveMenu(k, main, "screen-timeline-2", head, [SelectionLine(), head], 14);

        // 3: 그림이 있는 셀 우클릭
        var cel = Cells(main, RowBody, RowBody, 3, 3);
        RightClick(k, main, cel);
        SaveMenu(k, main, "screen-timeline-3", cel, [cel], 16);

        // 4: 레이어 이름 우클릭
        var name = GridRect(main, 0, RowY(RowBody), NameW, CellH);
        RightClickName(k, main, RowBody);
        SaveMenu(k, main, "screen-timeline-4", name, [name], 17);

        // 5: 레이어 메뉴의 불투명도… 창
        k.Dialog(() => ShotKit.Call(Timeline(main), "SetLayerOpacity", shadow), w => k.Save(w, "screen-timeline-5"));

        // 6: 왼쪽 위 빈 칸 우클릭 — 새로 만들기
        var corner = GridRect(main, 0, TagH, NameW, HeadH);
        RightClick(k, main, corner, selectFirst: false);
        SaveMenu(k, main, "screen-timeline-6", corner, [corner], 18);
    }

    [Shot("screen-timeline-select")]
    public static void Select(ShotKit k)
    {
        var main = OpenTall(k);
        Build(k, main);
        k.Click(main, FrameHead(main, 1));
        k.Click(main, FrameHead(main, 3), mods: RawInputModifiers.Shift);
        k.Settle(300);
        var t = TimelineRect(main);
        var msg = ShotKit.Get<Control>(main, "_stMessage");
        var m = ShotKit.Bounds(msg, main);
        var crop = new Rect(t.X, t.Y, t.Width, m.Bottom - t.Y + 2);
        // 상태 표시줄 안내 글자만(번호가 첫 글자를 가리지 않게 왼쪽 빈 곳까지)
        Rect Status() => PadLeft(new Rect(m.X, m.Y, AppTheme.MeasureText(((TextBlock)msg).Text ?? "", AppTheme.FontSize).Width + 8, m.Height), 28);
        k.Save(main, crop: crop, marks: [GridRect(main, NameW + CellW, TagH, 3 * CellW, HeadH + 5 * CellH)]);

        // 2: 셀 묶음(몸·그림자 행 × 2~3 프레임)
        k.Click(main, Cell(main, RowBody, 1));
        k.Click(main, Cell(main, RowShadow, 2), mods: RawInputModifiers.Shift);
        k.Settle(300);
        k.Save(main, "screen-timeline-select-2", crop: crop, marks: [Cells(main, RowBody, RowShadow, 1, 2), Status()], firstNumber: 2);
    }

    [Shot("screen-timeline-edit")]
    public static void Edit(ShotKit k)
    {
        var main = OpenTall(k);
        Build(k, main);
        var p = Project(main);
        // 빈 셀(그림자 3번 프레임)을 만들어 "이전 프레임 그림 복사"가 켜지게
        Ctx(main).Structural(() => p.SetCel(LayerIndex(main, "Shadow"), 2, null));
        k.Settle(200);

        // 1: 셀 묶음 우클릭(복사해 둔 뒤라 붙여넣기도 켜짐)
        k.Click(main, Cell(main, RowBody, 1));
        k.Click(main, Cell(main, RowShadow, 2), mods: RawInputModifiers.Shift);
        ShotKit.Call(Timeline(main), "CopySelection", false);
        k.Settle(200);
        var block = Cells(main, RowBody, RowShadow, 1, 2);
        RightClick(k, main, block, selectFirst: false);
        var clip = Union(main, MenuItem(UiStrings.S_TimelinePanel_ShowCelMenu_6618250E5A), MenuItem(UiStrings.S_TimelinePanel_ShowCelMenu_8FF74D6DA3),
            MenuItem(UiStrings.S_TimelinePanel_ShowCelMenu_8E28DA59DD));
        SaveMenu(k, main, "screen-timeline-edit", block, [block, clip], 1);

        // 2: 빈 셀 우클릭 — 이전 프레임 그림 복사
        var empty = Cells(main, RowShadow, RowShadow, 2, 2);
        RightClick(k, main, empty);
        SaveMenu(k, main, "screen-timeline-edit-2", empty, [empty, MenuItem(UiStrings.S_TimelinePanel_ShowCelMenu_F578B13F0B)], 3);
    }

    [Shot("screen-tags")]
    public static void Tags(ShotKit k)
    {
        var main = OpenTall(k);
        Build(k, main);
        var p = Project(main);
        var ctx = Ctx(main);
        // hit 태그를 지우고 프레임 5~6으로 다시 만드는 순서를 보인다.
        ctx.Structural(() => p.RemoveTag(p.Tags.ToList().FindIndex(t => t.Name == "hit")));
        k.Settle(200);

        // 1: 프레임 5~6을 고르고 머리글 우클릭 — 태그 만들기
        k.Click(main, FrameHead(main, 4));
        k.Click(main, FrameHead(main, 5), mods: RawInputModifiers.Shift);
        var frames = GridRect(main, NameW + 4 * CellW, TagH, 2 * CellW, HeadH + 5 * CellH);
        RightClick(k, main, GridRect(main, NameW + 5 * CellW, TagH, CellW, HeadH), selectFirst: false);
        SaveMenu(k, main, "screen-tags", frames, [frames, MenuItem(UiStrings.S_TimelinePanel_ShowFrameMenu_478619FBFF)], 1);

        // 2: 태그 만들기 창
        k.Dialog(() => ShotKit.Call(Timeline(main), "NewTag"), w =>
        {
            var name = ShotKit.Get<ComboBox>(w, "_name");
            name.Text = "hit";
            ShotKit.Pump(200);
            var body = (Panel)name.Parent!;
            var label = body.Children[body.Children.IndexOf(name) - 1];
            k.Save(w, "screen-tags-2", marks:
            [
                Union(w, label, name),
                (Control)ShotKit.Get<Control>(w, "_from").Parent!,
                (Control)ShotKit.Get<Control>(w, "_direction").Parent!,
                (Control)ShotKit.Get<Control>(w, "_color").Parent!,
            ], firstNumber: 3);
        });
        ctx.Structural(() => p.AddTag("hit", 4, 5));
        k.Settle(200);

        // 3: 태그 상자와 우클릭 메뉴
        var tag = GridRect(main, NameW, 0, 4 * CellW, TagH);
        RightClick(k, main, tag);
        SaveMenu(k, main, "screen-tags-3", tag, [PadLeft(tag, 14), PopupArea(main)!.Value.Deflate(2)], 7);
    }

    [Shot("screen-layer-effects")]
    public static void LayerEffects(ShotKit k)
    {
        // 커스텀 탭에 보일 개인 효과 레시피 둘.
        AppAssets.Store.SavePersonalRecipe(EffectRecipe.Capture("", "Gold outline", [new OutlineEffect { Color = new Color32(255, 230, 120, 255) }]));
        AppAssets.Store.SavePersonalRecipe(EffectRecipe.Capture("", "Hurt", [new HitFlashEffect { Color = new Color32(255, 80, 80, 255) }, new ShakeEffect()]));
        var main = OpenTall(k, 370);
        Build(k, main, effects: true);
        var p = Project(main);
        int body = LayerIndex(main, "Body");
        var layer = p.Layers[body];
        Ctx(main).SetCurrentFrame(4);
        k.Settle(300);
        var fx = ((int X, int W))ShotKit.Call(Grid(main), "FxBadge", layer)!;
        var fxBadge = GridRect(main, fx.X, RowY(RowBody) + 8, fx.W, 20);

        // 1: 레이어 이름 우클릭 — 효과…, 이름 칸의 fx
        RightClickName(k, main, RowBody);
        SaveMenu(k, main, "screen-layer-effects", fxBadge,
            [MenuItem(UiStrings.S_TimelinePanel_ShowLayerMenu_4C4B49E76B), PadLeft(fxBadge, 14)], 1);

        // 2~5: 효과 창(색 설정 = 2, 색 팝업 = 3, 전체 = 4, 커스텀 탭 = 5)
        k.Dialog(() => ShotKit.Call(Timeline(main), "EditLayerEffects", body, -1, null, null), w => EffectsDialog(k, w), settleMs: 900);

        // 6: 타임라인의 효과 줄
        var t = TimelineRect(main);
        var crop = new Rect(t.X, t.Y, 640, t.Height);
        var rows = GridRect(main, 0, RowY(RowBody + 1), NameW, 2 * FxH);
        var hitBox = GridRect(main, NameW + 4 * CellW, RowY(RowBody + 1, 1), 2 * CellW, FxH);
        k.Save(main, "screen-layer-effects-6", crop: crop, marks: [rows, PadLeft(hitBox, 14)], firstNumber: 16);

        // 7: 몸 셀 5~6을 골라 우클릭 — 효과 추가…
        k.Click(main, Cell(main, RowBody, 4));
        k.Click(main, Cell(main, RowBody, 5), mods: RawInputModifiers.Shift);
        var cels = Cells(main, RowBody, RowBody, 4, 5);
        RightClick(k, main, cels, selectFirst: false);
        SaveMenu(k, main, "screen-layer-effects-7", cels, [MenuItem(UiStrings.S_TimelinePanel_ShowCelMenu_21F847E58A)], 18);

        // 8: 효과 상자 우클릭
        RightClick(k, main, hitBox);
        SaveMenu(k, main, "screen-layer-effects-8", hitBox, [PadLeft(hitBox, 14)], 19);

        // 9: 상자 밖 빈 칸 우클릭 — 여기까지 늘리기·여기부터 복사본 추가
        var empty = GridRect(main, NameW + CellW, RowY(RowBody + 1, 1), CellW, FxH);
        RightClick(k, main, empty);
        var extra = Union(main, MenuItem(UiStrings.S_TimelinePanel_ShowEffectMenu_8640EB7B98), MenuItem(UiStrings.S_TimelinePanel_ShowEffectMenu_A8337F1B36));
        SaveMenu(k, main, "screen-layer-effects-9", empty, [extra], 20);
    }

    /// <summary>효과 창: 색 설정(-2, ③~⑤), 색 팝업(-3), 전체(-4, ⑥~⑭), 커스텀 탭(-5, ⑮).</summary>
    private static void EffectsDialog(ShotKit k, Window w)
    {
        ShotKit.Pump(600);
        var tabs = ShotKit.Find<TabControl>(w);
        var custom = (TabItem)tabs.Items[1]!;
        var tiles = (Control)((TabItem)tabs.Items[0]!).Content!;
        var list = ShotKit.Get<ListBox>(w, "_list");
        var settings = ShotKit.Get<Control>(w, "_settings");
        var buttons = (Control)ShotKit.Get<Control>(w, "_remove").Parent!;
        var play = (Control)ShotKit.Get<Control>(w, "_play").Parent!;
        var preview = ShotKit.Get<Control>(w, "_preview");
        var strip = ShotKit.Get<Control>(w, "_strip");
        var expandRow = (Control)ShotKit.Get<Control>(w, "_expandCanvas").Parent!;
        var expandNote = ShotKit.Get<Control>(w, "_expandNote");
        var extend = ShotKit.Get<Control>(w, "_extendPanel");
        var addRow = (Control)ShotKit.Get<Control>(w, "_addAll").Parent!;
        var shared = InWindow(w, GroupOf(expandRow, list));

        // 4: 전체(번호가 글자·체크 상자를 가리지 않게 묶음 왼쪽 끝에 맞춘 사각형)
        var expand = Union(w, expandRow, expandNote);
        var ext = InWindow(w, extend);
        k.Save(w, "screen-layer-effects-4", marks:
        [
            tiles,
            custom,
            PadLeft(InWindow(w, addRow), 10),
            Union(w, list, buttons),
            GroupOf(settings, list),
            Union(w, play, preview),
            strip,
            new Rect(shared.X + 2, expand.Y - 2, shared.Width - 4, expand.Height + 4),
            new Rect(shared.X + 2, ext.Y - 2, shared.Width - 4, ext.Height + 4),
        ], firstNumber: 6);

        // 2: 피격 번쩍임의 설정 — 색 칸, 일반색/그라데이션 목록(펼침), 클리핑
        list.SelectedIndex = 1;
        ShotKit.Pump(400);
        var field = ShotKit.All<ColorField>(settings).First();
        var mode = ShotKit.All<ComboBox>((Control)field.Parent!).First();
        var clipBox = ShotKit.All<CheckBox>(settings).First(c => c.Content as string == UiStrings.EffectPreset_ClipToPrevious);
        var group = InWindow(w, GroupOf(settings, list));
        mode.IsDropDownOpen = true;
        ShotKit.Pump(400);
        var area = group;
        if (PopupArea(w) is { } drop) area = area.Union(drop);
        area = area.Inflate(10).Intersect(new Rect(w.ClientSize));
        k.Save(w, "screen-layer-effects-2", crop: area, marks: [PadLeft(InWindow(w, field), 14), mode, PadLeft(InWindow(w, clipBox), 14)], firstNumber: 3);
        mode.IsDropDownOpen = false;
        ShotKit.Pump(300);

        // 3: 색 칸을 누른 팝업
        k.Click(field.Children[0] as Control ?? field, settleMs: 0);
        ShotKit.Pump(600);
        var popup = PopupArea(w) ?? throw new InvalidOperationException("no color popup");
        k.Save(w, "screen-layer-effects-3", crop: popup.Union(InWindow(w, (Control)field.Parent!)).Inflate(8).Intersect(new Rect(w.ClientSize)));
        PopupMenus.CloseAll();
        ShotKit.Pump(300);

        // 5: 커스텀 탭 — 전체 효과 저장
        tabs.SelectedIndex = 1;
        ShotKit.Pump(500);
        var save = ShotKit.FindText(w, UiStrings.EffectPreset_SaveGroup, ancestorControl: true);
        var palette = ShotKit.Bounds((Control)tabs.Parent!, w);
        var last = ShotKit.Get<Panel>(w, "_customWrap").Children.Select(c => ShotKit.Bounds(c, w).Bottom).DefaultIfEmpty(palette.Y + 200).Max();
        k.Save(w, "screen-layer-effects-5", crop: new Rect(palette.X, palette.Y, palette.Width, last - palette.Y).Inflate(10).Intersect(new Rect(w.ClientSize)),
            marks: [save], firstNumber: 15);
    }

    /// <summary><paramref name="c"/>를 담은 묶음(<paramref name="sibling"/>과 같은 세로 줄에 놓인 Ui.Group 테두리).</summary>
    private static Control GroupOf(Control c, Control sibling)
        => c.GetVisualAncestors().OfType<Control>().First(a => ReferenceEquals(a.GetVisualParent(), sibling.GetVisualParent()));
}
