using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Gpixel.App;
using Gpixel.App.Localization;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace GuideShots;

/// <summary>AI 그림 픽셀로 정리 창 샷의 준비: 짧은 최적화 예산, "AI가 만든 듯한" 원본(예시 그림을 고르지 않게 키우고 번지게 한 JPG), 후보·최적화 기다리기.</summary>
internal static class PixelateKit
{
    /// <summary>예시 문서 파일 이름(탭·파일 브라우저에 보임).</summary>
    public const string SampleFile = "Wolf-ear adventurer.png";

    public const string SourceName = "Wolf-ear adventurer AI";

    /// <summary>창 옵션을 새로: 불러오면 자동 최적화 끔, 설정 칸 펼침 여부, 창 크기, 최적화 프리셋은 앞의 두 개만 짧은 예산으로.</summary>
    public static PixelateOptions Options(bool expanded = false, int width = 1400, int height = 860)
    {
        var o = new PixelateOptions();
        o.Migrate();
        o.AutoOptimizeOnLoad = false;
        o.SettingsExpanded = expanded;
        o.Width = width;
        o.Height = height;
        for (int i = 0; i < o.OptimizePresets.Count; i++)
        {
            var p = o.OptimizePresets[i];
            p.Enabled = i < 2;
            p.Trials = 120;
            p.Seconds = 20;
        }
        App.Settings.Pixelate = o;
        return o;
    }

    /// <summary>
    /// 예시 그림(49×98)을 칸마다 7~8px로 들쭉날쭉하게 키우고, 밝은 단색 배경 위에 살짝 번지게 한 뒤 JPG로 저장한다(AI가 만든 "픽셀 느낌" 그림 흉내).
    /// </summary>
    public static string MakeAiImage(ShotKit k)
    {
        var src = PixelateDialog.LoadImage(ShotKit.SamplePng) ?? throw new InvalidOperationException("sample unreadable");
        const double scale = 7.4;
        const int margin = 44;
        int[] MapAxis(int cells, out int size)
        {
            size = (int)Math.Round(cells * scale) + margin * 2;
            var map = new int[size];
            int cell = 0;
            for (int p = 0; p < size; p++)
            {
                double q = (p - margin) / scale;
                double edge = Math.Sin(cell * 1.7) * 0.18;
                int c = (int)Math.Floor(q + edge);
                map[p] = q < 0 || c >= cells ? -1 : Math.Clamp(c, 0, cells - 1);
                if (map[p] >= 0) cell = map[p];
            }
            return map;
        }
        var mx = MapAxis(src.Width, out int w);
        var my = MapAxis(src.Height, out int h);
        var bg = new Color32(238, 235, 228);
        var sharp = new double[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = mx[x] < 0 || my[y] < 0 ? Color32.Transparent : src[mx[x], my[y]];
                double a = c.A / 255.0;
                int i = (y * w + x) * 3;
                sharp[i] = c.R * a + bg.R * (1 - a);
                sharp[i + 1] = c.G * a + bg.G * (1 - a);
                sharp[i + 2] = c.B * a + bg.B * (1 - a);
            }
        var rnd = new Random(7);
        var img = new PixelImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0, sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                        double wt = (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1);
                        int i = (yy * w + xx) * 3;
                        r += sharp[i] * wt; g += sharp[i + 1] * wt; b += sharp[i + 2] * wt; sum += wt;
                    }
                double n = rnd.NextDouble() * 6 - 3;
                img[x, y] = new Color32(Byte(r / sum + n), Byte(g / sum + n), Byte(b / sum + n));
            }
        var path = Path.Combine(k.WorkDir, SourceName + ".jpg");
        ImageWriter.Save(img, path, ExportImageFormat.Jpg, 80);
        return path;
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>메인 창(예시 문서 열림)을 띄우고 AI 그림을 원본으로 정리 창을 연 뒤 첫 후보가 끝날 때까지 기다린다.</summary>
    public static PixelateDialog Open(ShotKit k, bool expanded = false, int width = 1400, int height = 860)
    {
        Options(expanded, width, height);
        var main = k.Main(k.CopySample(SampleFile));
        var path = MakeAiImage(k);
        ShotKit.Call(main, "PixelateFile", path);
        k.Settle(400);
        var dlg = k.Windows.OfType<PixelateDialog>().Last();
        WaitCandidates(k, dlg);
        return dlg;
    }

    public static void WaitCandidates(ShotKit k, PixelateDialog dlg, int timeoutMs = 120000)
    {
        var running = UiStrings.Pixelate_Status_Running;
        running = running[..running.IndexOf('{')];
        var status = ShotKit.Get<TextBlock>(dlg, "_status");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            k.Settle(200);
            if (ShotKit.Get<object?>(dlg, "_selected") is not null && !(status.Text ?? "").StartsWith(running, StringComparison.Ordinal)) break;
        }
        k.Settle(600);
    }

    public static void WaitOptimize(ShotKit k, PixelateDialog dlg, int timeoutMs = 240000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        k.Settle(300);
        while (sw.ElapsedMilliseconds < timeoutMs && ShotKit.Get<object?>(dlg, "_optCts") is not null) k.Settle(250);
        k.Settle(800);
    }

    /// <summary>설정 칸의 단계 접기 상자(머리글 글자로).</summary>
    public static Expander Section(PixelateDialog dlg, string title)
        => ShotKit.All<Expander>(dlg).First(e => e.Header is TextBlock t && t.Text == title);

    /// <summary>설정 칸에서 <paramref name="open"/> 단계만 펼치고 나머지는 접는다.</summary>
    public static void OnlyExpand(ShotKit k, PixelateDialog dlg, params string[] open)
    {
        foreach (var e in ShotKit.All<Expander>(dlg).Where(e => e.Header is TextBlock))
            e.IsExpanded = open.Contains(((TextBlock)e.Header!).Text);
        k.Settle(600);
    }

    /// <summary>설정 칸을 굴려 <paramref name="section"/>이 칸 맨 위에 오게 한다.</summary>
    public static void ScrollToTop(ShotKit k, Control section)
    {
        var scroll = section.FindAncestorOfType<ScrollViewer>() ?? throw new InvalidOperationException("no settings scroll");
        var content = (Visual)scroll.Content!;
        var y = section.TranslatePoint(new Point(0, 0), content)?.Y ?? 0;
        scroll.Offset = new Vector(0, Math.Max(0, y - 6));
        k.Settle(400);
    }

    /// <summary>팝업 안 컨트롤의 <paramref name="top"/> 창 좌표 사각형(팝업 위치는 플랫폼 구현의 Position — 공개 API가 아님).</summary>
    public static Rect PopupBounds(Control c, Window top)
    {
        var host = TopLevel.GetTopLevel(c) ?? throw new InvalidOperationException("detached");
        var local = ShotKit.Bounds(c, host);
        if (ReferenceEquals(host, top)) return local;
        var pos = host.PlatformImpl?.GetType().GetProperty("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(host.PlatformImpl) is PixelPoint p ? p : default;
        var origin = top.Position;
        return local.Translate(new Vector(pos.X - origin.X, pos.Y - origin.Y));
    }

    /// <summary>열려 있는 팝업(메뉴·목록·툴팁) 중 마지막 것.</summary>
    public static Popup LastOpenPopup()
    {
        var popups = (List<Popup>)typeof(ShotKit).GetField("s_popups", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return popups.LastOrDefault(p => p.IsOpen) ?? throw new InvalidOperationException("no open popup");
    }

    /// <summary>열린 콤보 상자 목록의 창 좌표 사각형.</summary>
    public static Rect DropDownBounds(ComboBox combo, Window top)
    {
        var popup = ShotKit.All<Popup>(combo).First();
        return PopupBounds((Control)popup.Child!, top);
    }

    /// <summary>컨트롤 둘레를 <paramref name="pad"/>만큼 넓힌 창 좌표 사각형(번호 동그라미가 글자를 덜 가리게).</summary>
    public static Rect Box(Control c, Visual top, double pad = 4) => ShotKit.Bounds(c, top).Inflate(pad);

    /// <summary>
    /// 글자 줄처럼 왼쪽 위 모서리에 번호가 얹히면 첫 글자를 가리는 컨트롤: 왼쪽으로 <paramref name="left"/>만큼 더 넓혀 번호가 글자 앞 빈 곳에 오게 한다.
    /// </summary>
    public static Rect LeftBox(Control c, Visual top, double left = 14, double pad = 2)
    {
        var r = ShotKit.Bounds(c, top).Inflate(pad);
        return new Rect(r.X - left, r.Y, r.Width + left, r.Height);
    }

    /// <summary>번호를 컨트롤 위 빈 곳에 얹도록 위로 <paramref name="up"/>만큼 더 넓힌 사각형(나란한 작은 단추들).</summary>
    public static Rect UpBox(Control c, Visual top, double up = 12, double pad = 2)
    {
        var r = ShotKit.Bounds(c, top).Inflate(pad);
        return new Rect(r.X, r.Y - up, r.Width, r.Height + up);
    }

    /// <summary>남는 너비까지 늘어난 글자 줄에서 실제 글자 부분만.</summary>
    public static Rect TextRect(TextBlock t, Visual top, double pad = 3)
    {
        var b = ShotKit.Bounds(t, top);
        return new Rect(b.X, b.Y, Math.Min(b.Width, t.TextLayout.WidthIncludingTrailingWhitespace + t.Padding.Left + t.Padding.Right), b.Height).Inflate(pad);
    }

    public static Rect Union(Visual top, params Control[] controls)
        => controls.Select(c => ShotKit.Bounds(c, top)).Aggregate((a, b) => a.Union(b));

    /// <summary>위 메뉴 <paramref name="index"/>(0 = 파일, 1 = 편집 …)를 열고 글자가 <paramref name="text"/>인 항목을 돌려준다.</summary>
    public static MenuItem OpenMenu(ShotKit k, MainForm main, int index, string text)
    {
        var top = ShotKit.Find<Menu>(main).Items.OfType<MenuItem>().ElementAt(index);
        k.Click(top, settleMs: 500);
        return ShotKit.All<MenuItem>(main).FirstOrDefault(m => m.Header is not null && m.IsVisible && ShotKit.All<TextBlock>(m).Any(t => t.Text == text))
               ?? throw new InvalidOperationException("no menu item " + text);
    }
}

internal static class PixelateShots
{
    // 여는 방법: AI 메뉴 명령(①), 원본 없이 연 빈 창(②~⑤), 편집 메뉴 명령(⑥).
    [Shot("pixelate-open")]
    public static void Open(ShotKit k)
    {
        PixelateKit.Options(width: 1000, height: 640);
        var main = k.Main(k.CopySample(PixelateKit.SampleFile));

        // ① AI > Pixelate AI image…
        var item = PixelateKit.OpenMenu(k, main, 5, MainStrings.Menu_Ai_Pixelate);
        var r = PixelateKit.PopupBounds(item, main);
        var ai = ShotKit.Find<Menu>(main).Items.OfType<MenuItem>().ElementAt(5);
        var aiBounds = ShotKit.Bounds(ai, main);
        k.Save(main, "pixelate-open", crop: new Rect(Math.Max(0, aiBounds.X - 120), 0, 560, Math.Min(900, r.Bottom + 140)), marks: [r]);
        k.Key(main, Avalonia.Input.Key.Escape);
        k.Settle(300);

        // ②~⑤ 빈 창: 가운데 안내, 파일 찾기, 꺼진 적용·자동 최적화.
        k.Command("AI/AI 그림 픽셀로 정리…");
        k.Settle(800);
        var dlg = k.Windows.OfType<PixelateDialog>().Last();
        k.Settle(500);
        var empty = ShotKit.Get<Control>(dlg, "_emptyState");
        var message = ShotKit.FindText(empty, Ui.Shortcut(UiStrings.Pixelate_Empty));
        var browse = ShotKit.FindText(empty, UiStrings.Pixelate_Browse, ancestorControl: true);
        var ok = ShotKit.Get<Button>(dlg, "OkButton");
        var run = ShotKit.Get<Button>(dlg, "_optRun");
        k.Save(dlg, "pixelate-open-2", marks: [PixelateKit.Box(message, dlg, 12), PixelateKit.Box(browse, dlg), PixelateKit.Box(ok, dlg), PixelateKit.Box(run, dlg)], firstNumber: 2);
        dlg.Close();
        k.Settle(400);

        // ⑥ AI > Pixelate current layer…
        var edit = PixelateKit.OpenMenu(k, main, 5, MainStrings.Menu_Ai_PixelateLayer);
        var er = PixelateKit.PopupBounds(edit, main);
        var editTop = ShotKit.Bounds(ShotKit.Find<Menu>(main).Items.OfType<MenuItem>().ElementAt(5), main);
        var menuRect = PixelateKit.PopupBounds((Control)PixelateKit.LastOpenPopup().Child!, main);
        k.Save(main, "pixelate-open-3", crop: new Rect(Math.Max(0, editTop.X - 40), 0, menuRect.Right + 60 - Math.Max(0, editTop.X - 40), Math.Min(900, Math.Max(er.Bottom + 40, menuRect.Bottom + 12))),
            marks: [er], firstNumber: 6);
        k.Key(main, Avalonia.Input.Key.Escape);
    }

    // 여는 방법: 파일 브라우저에서 그림 파일 우클릭 > Pixelate AI image…(⑦⑧), 원본이 들어온 창의 원본 바꾸기(⑨).
    [Shot("pixelate-open-browser")]
    public static void OpenFromBrowser(ShotKit k)
    {
        PixelateKit.Options();
        var sample = k.CopySample(PixelateKit.SampleFile);
        var jpg = PixelateKit.MakeAiImage(k);
        var main = k.Main(sample, folder: k.WorkDir);
        k.Settle(800);
        var browser = ShotKit.Get<FileBrowserPanel>(main, "_browser");
        var list = ShotKit.Get<ListBox>(browser, "_list");
        var container = ShotKit.All<ListBoxItem>(list).First(c => ShotKit.Get<string>(c.Tag!, "Path") == jpg);
        ShotKit.Call(browser, "SetSelection", (Func<object, bool>)(o => ReferenceEquals(o, container)));
        k.Settle(300);
        k.Click(container, Avalonia.Input.MouseButton.Right, settleMs: 600);
        var item = ShotKit.All<MenuItem>(main).FirstOrDefault(m => m.IsVisible && m.Header as string == UiStrings.Pixelate_MenuItem)
                   ?? throw new InvalidOperationException("no browser pixelate item");
        var r = PixelateKit.PopupBounds(item, main);
        var b = ShotKit.Bounds(container, main);
        var menuRect = PixelateKit.PopupBounds((Control)item.Parent!, main);
        var top = Math.Max(0, b.Y - 70);
        var area = new Rect(0, top, menuRect.Right + 16, menuRect.Bottom + 16 - top);
        k.Save(main, "pixelate-open-4", crop: area, marks: [container, r], firstNumber: 7);

        // 메뉴 항목을 눌러 그 파일을 원본으로 연 창의 원본 바꾸기 단추.
        k.Click(item, settleMs: 800);
        var dlg = k.Windows.OfType<PixelateDialog>().Last();
        PixelateKit.WaitCandidates(k, dlg);
        var replace = ShotKit.Get<Button>(dlg, "_replaceSource");
        var row = (Control)replace.Parent!;
        var rb = ShotKit.Bounds(row, dlg);
        k.Save(dlg, "pixelate-open-5", crop: new Rect(rb.X - 8, rb.Y - 90, 640, rb.Height + 104), marks: [PixelateKit.Box(replace, dlg)], firstNumber: 9);
    }

    // 설정 단계: 접힌 막대(①), 펼친 단계를 두 개씩(②~⑨), 외곽선 1px 맞추기 목록(⑪), 프레임 맞추기(⑫).
    [Shot("pixelate-settings")]
    public static void Settings(ShotKit k)
    {
        var dlg = PixelateKit.Open(k, expanded: false, height: 1000);
        ShotKit.Call(dlg, "StopPlayback");
        var toggle = ShotKit.Get<Button>(dlg, "_settingsToggle");
        k.Save(dlg, "pixelate-settings", crop: new Rect(0, 0, 640, 560), marks: [PixelateKit.Box(toggle, dlg, 2)]);

        ShotKit.Call(dlg, "SetSettingsExpanded", true, false);
        k.Settle(800);

        void Pair(string name, int first, string a, string? b, Func<Expander, Expander?, IEnumerable<Mark>> marks)
        {
            if (b is null) PixelateKit.OnlyExpand(k, dlg, a);
            else PixelateKit.OnlyExpand(k, dlg, a, b);
            var ea = PixelateKit.Section(dlg, a);
            var eb = b is null ? null : PixelateKit.Section(dlg, b);
            PixelateKit.ScrollToTop(k, ea);
            var area = ShotKit.Bounds(ea, dlg);
            if (eb is not null) area = area.Union(ShotKit.Bounds(eb, dlg));
            var scroll = ea.FindAncestorOfType<ScrollViewer>()!;
            var view = ShotKit.Bounds(scroll, dlg);
            var crop = new Rect(view.X - 8, area.Y - 14, view.Width + 16, Math.Min(area.Bottom, view.Bottom) - area.Y + 24);
            k.Save(dlg, name, crop: crop, marks: marks(ea, eb), firstNumber: first);
        }

        Pair("pixelate-settings-2", 2, UiStrings.Pixelate_Section_Background, UiStrings.Pixelate_Section_Frames, (a, b) => [a, b!]);
        Pair("pixelate-settings-3", 4, UiStrings.Pixelate_Section_Grid, UiStrings.Pixelate_Section_Size, (a, b) => [a, b!]);
        Pair("pixelate-settings-4", 6, UiStrings.Pixelate_Section_Downsample, UiStrings.Pixelate_Section_Palette, (a, b) =>
        {
            var distance = (Control)ShotKit.Get<ComboBox>(dlg, "_palDistance").Parent!;
            var shade = ShotKit.Get<CheckBox>(dlg, "_palShade");
            return [a, b!, PixelateKit.LeftBox(distance, dlg, 10), PixelateKit.LeftBox(shade, dlg, 10)];
        });

        // 정리 단계 + 외곽선 1px 맞추기 목록을 펼친 모습.
        PixelateKit.OnlyExpand(k, dlg, UiStrings.Pixelate_Section_Cleanup);
        var cleanup = PixelateKit.Section(dlg, UiStrings.Pixelate_Section_Cleanup);
        PixelateKit.ScrollToTop(k, cleanup);
        var fit = ShotKit.Get<ComboBox>(dlg, "_clFit");
        fit.IsDropDownOpen = true;
        k.Settle(500);
        var list = PixelateKit.DropDownBounds(fit, dlg);
        var cb = ShotKit.Bounds(cleanup, dlg);
        var view = ShotKit.Bounds(cleanup.FindAncestorOfType<ScrollViewer>()!, dlg);
        k.Save(dlg, "pixelate-settings-5", crop: new Rect(view.X - 8, cb.Y - 14, view.Width + 16, Math.Max(cb.Bottom, list.Bottom) - cb.Y + 24),
            marks: [cleanup, list], firstNumber: 10);
        fit.IsDropDownOpen = false;
        k.Settle(300);

        Pair("pixelate-settings-6", 12, UiStrings.Pixelate_Section_Consistency, null, (a, _) => [a]);
    }

    // 후보와 점수: 후보 단계 설정(①②), 후보 칸·방식 이름·점수·점수 내역(③~⑥), 후보 풍선의 점수 내역(⑦).
    [Shot("pixelate-candidates")]
    public static void Candidates(ShotKit k)
    {
        var dlg = PixelateKit.Open(k, expanded: true);
        ShotKit.Call(dlg, "StopPlayback");
        PixelateKit.OnlyExpand(k, dlg, UiStrings.Pixelate_Section_Candidates);
        var section = PixelateKit.Section(dlg, UiStrings.Pixelate_Section_Candidates);
        PixelateKit.ScrollToTop(k, section);
        var count = ShotKit.Get<NumericUpDown>(dlg, "_candCount");
        var vary = (Control)ShotKit.Get<CheckBox>(dlg, "_vScale").Parent!;
        k.SaveControl(section, "pixelate-candidates", pad: 14, marks: [PixelateKit.LeftBox((Control)count.Parent!, dlg, 10), PixelateKit.LeftBox(vary, dlg, 10)]);
        ShotKit.Call(dlg, "SetSettingsExpanded", false, false);
        k.Settle(600);

        var info = ShotKit.Get<TextBlock>(dlg, "_selectedInfo");
        var breakdown = ShotKit.Get<TextBlock>(dlg, "_breakdown");
        var tiles = ShotKit.Get<WrapPanel>(dlg, "_tiles");
        var scroll = tiles.FindAncestorOfType<ScrollViewer>()!;
        var tileList = tiles.Children.OfType<Border>().ToList();
        TextBlock Line(Border tile, int i) => ((StackPanel)tile.Child!).Children.OfType<TextBlock>().ElementAt(i);
        var thin = tileList.FirstOrDefault(t => Line(t, 2).Text == UiStrings.Pixelate_Fit_Thin) ?? tileList[Math.Min(3, tileList.Count - 1)];
        var title = Line(tileList[0], 0);
        var area = PixelateKit.Union(dlg, info, breakdown, scroll);
        k.Save(dlg, "pixelate-candidates-2", crop: new Rect(area.X - 40, area.Y - 4, area.Width + 48, area.Height + 12),
            marks: [scroll, PixelateKit.LeftBox(Line(thin, 2), dlg), PixelateKit.LeftBox(title, dlg), PixelateKit.LeftBox(breakdown, dlg, 26, 1)], firstNumber: 3);

        // 후보 칸 풍선(점수 항목별 내역).
        var tip = tileList[1];
        k.Hover(dlg, ShotKit.Center(tip, dlg), 200);
        ToolTip.SetIsOpen(tip, true);
        k.Settle(600);
        var popup = PixelateKit.LastOpenPopup();
        var pr = PixelateKit.PopupBounds((Control)popup.Child!, dlg);
        var tr = ShotKit.Bounds(tip, dlg);
        k.Save(dlg, "pixelate-candidates-3", crop: pr.Union(tr).Inflate(16), marks: [pr], firstNumber: 7);
        ToolTip.SetIsOpen(tip, false);
    }

    // 자동 최적화: 끝난 뒤 막대(①~⑥), 도는 중(⑦⑧), 최적화 설정 창(⑨~⑯).
    [Shot("pixelate-optimize")]
    public static void Optimize(ShotKit k)
    {
        var dlg = PixelateKit.Open(k);
        ShotKit.Call(dlg, "StopPlayback");
        var run = ShotKit.Get<Button>(dlg, "_optRun");
        var bar = (Control)run.Parent!;
        var tiles = ShotKit.Get<WrapPanel>(dlg, "_tiles");
        var scroll = tiles.FindAncestorOfType<ScrollViewer>()!;
        var settingsButton = ShotKit.All<Button>(bar).First(b => !ReferenceEquals(b, run) && b.IsVisible);
        var graph = ShotKit.Get<Control>(dlg, "_optGraph");
        var text = ShotKit.Get<TextBlock>(dlg, "_optText");
        var revert = ShotKit.Get<Button>(dlg, "_optRevert");
        var breakdown = ShotKit.Get<TextBlock>(dlg, "_breakdown");
        breakdown.Opacity = 0; // 바로 위 점수 내역 줄 끝이 잘려 보이지 않게.

        // 도는 중: 취소 단추와 진행 한 줄.
        ShotKit.Call(dlg, "StartOptimize", (object?)null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000 && ShotKit.Get<int>(dlg, "_optDone") < 40 && ShotKit.Get<object?>(dlg, "_optCts") is not null) k.Settle(50);
        if (ShotKit.Get<object?>(dlg, "_optCts") is null) throw new InvalidOperationException("optimize finished before the running shot");
        var barRect = ShotKit.Bounds(bar, dlg);
        var progress = PixelateKit.TextRect(text, dlg);
        k.Save(dlg, "pixelate-optimize-2", crop: new Rect(barRect.X - 6, barRect.Y - 32, progress.Right + 24 - barRect.X, barRect.Height + 40),
            marks: [PixelateKit.UpBox(run, dlg), PixelateKit.UpBox(graph, dlg), new Rect(progress.X, progress.Y - 12, progress.Width, progress.Height + 12)], firstNumber: 7);

        PixelateKit.WaitOptimize(k, dlg);
        var optimized = tiles.Children.OfType<Border>().FirstOrDefault(t => ((StackPanel)t.Child!).Children.OfType<TextBlock>().Any(x => x.Foreground == AppTheme.AccentBrush))
                        ?? throw new InvalidOperationException("no optimized tile");
        var marker = ((StackPanel)optimized.Child!).Children.OfType<TextBlock>().Last();
        var area = ShotKit.Bounds(bar, dlg).Union(ShotKit.Bounds(scroll, dlg));
        var line = PixelateKit.TextRect(text, dlg);
        k.Save(dlg, "pixelate-optimize", crop: new Rect(area.X - 24, area.Y - 32, area.Width + 30, area.Height + 38), marks:
        [
            PixelateKit.UpBox(run, dlg),
            PixelateKit.UpBox(settingsButton, dlg),
            PixelateKit.UpBox(graph, dlg),
            new Rect(line.X, line.Y - 12, line.Width, line.Height + 12),
            PixelateKit.UpBox(revert, dlg),
            PixelateKit.LeftBox(marker, dlg),
        ]);

        // 최적화 설정 창: 항목 체크·모두 선택/해제·예산·점수 가중치·시도 기록(펼침)·불러오면 자동 최적화·프리셋 목록·점수용 색 거리.
        ShotKit.Call(dlg, "OpenOptimizeSettings");
        k.Settle(800);
        var win = ShotKit.Get<Window>(dlg, "_optSettingsWindow");
        win.Height = 900;
        k.Settle(300);
        var history = ShotKit.All<Expander>(win).First(e => e.Header is TextBlock t && t.Text == UiStrings.Pixelate_Opt_History);
        history.IsExpanded = true;
        k.Settle(600);
        var panel = ShotKit.Find<OptimizePresetsPanel>(win);
        var checks = ShotKit.Get<System.Collections.IEnumerable>(panel, "_checks").Cast<object>()
            .Select(t => (Control)t.GetType().GetField("Item2")!.GetValue(t)!).Where(c => c.IsVisible).ToArray();
        var all = ShotKit.Get<Button>(panel, "_all");
        var trials = ShotKit.Get<NumericUpDown>(panel, "_trials");
        var seconds = ShotKit.Get<NumericUpDown>(panel, "_seconds");
        var weightsLabel = ShotKit.FindText(panel, UiStrings.Pixelate_Opt_ScoreWeights);
        var weights = ShotKit.Get<NumericUpDown[]>(panel, "_weights");
        var presetList = (Control)ShotKit.Get<ListBox>(panel, "_list").Parent!;
        var scoreDistance = (Control)ShotKit.Get<ComboBox>(panel, "_distance").Parent!;
        var auto = ShotKit.Get<CheckBox>(dlg, "_optAuto");
        k.Save(win, "pixelate-optimize-3", marks:
        [
            PixelateKit.Union(win, checks).Inflate(new Thickness(12, 3, 3, 3)),
            PixelateKit.LeftBox(all, win, 10),
            PixelateKit.Union(win, trials, seconds).Inflate(new Thickness(3, 14, 3, 3)),
            PixelateKit.Union(win, [weightsLabel, .. weights]).Inflate(new Thickness(12, 3, 4, 4)),
            PixelateKit.Box(history, win, 2),
            PixelateKit.UpBox(auto, win, 10),
            PixelateKit.Box(presetList, win, 2),
            PixelateKit.Box(scoreDistance, win, 2),
        ], firstNumber: 9);
    }

    // 원본과 미리보기: 전체 창(①~⑤), 원본 아래 막대(⑥~⑩), 칸 경계 지역 보정(⑪), 색 견본(⑫~⑭), 배경 남기기 설정(⑮⑯), 크게 보기(⑰~⑳).
    [Shot("pixelate-view")]
    public static void View(ShotKit k)
    {
        var dlg = PixelateKit.Open(k);
        ShotKit.Call(dlg, "StopPlayback");
        k.Settle(300);
        var source = ShotKit.Get<Control>(dlg, "_sourceView");
        var preview = ShotKit.Get<Control>(dlg, "_preview");
        var colors = ShotKit.Get<Control>(dlg, "_colors");
        var play = (Control)ShotKit.Get<Button>(dlg, "_playButton").Parent!;
        var strip = ShotKit.Get<Control>(dlg, "_strip").FindAncestorOfType<ScrollViewer>()!;
        var tiles = ShotKit.Get<WrapPanel>(dlg, "_tiles").FindAncestorOfType<ScrollViewer>()!;
        // 번호가 머리글 글자를 가리지 않게 왼쪽 빈 곳(접힌 설정 막대·두 칸 사이)에 얹는다. 원본 칸 테두리는 오른쪽을 조금 줄여 옆 칸 번호와 겹치지 않게.
        var sb = ShotKit.Bounds(source, dlg);
        var pr = PixelateKit.Union(dlg, play, strip);
        k.Save(dlg, "pixelate-view", marks:
        [
            new Rect(sb.X - 12, sb.Y, sb.Width - 10, sb.Height),
            PixelateKit.LeftBox(preview, dlg, 12, 0),
            PixelateKit.LeftBox(colors, dlg, 12, 1),
            new Rect(pr.X - 13, pr.Y - 1, pr.Width + 14, pr.Height + 2),
            tiles,
        ]);

        // 원본 아래 막대.
        var showGrid = ShotKit.Get<CheckBox>(dlg, "_showGrid");
        var showMask = ShotKit.Get<CheckBox>(dlg, "_showMask");
        var barAuto = ShotKit.Get<CheckBox>(dlg, "_barAuto");
        var barW = ShotKit.Get<Control>(dlg, "_barW");
        var barH = ShotKit.Get<Control>(dlg, "_barH");
        var aspect = ShotKit.Get<ToggleButton>(dlg, "_barAspect");
        var toolbar = (Control)showGrid.Parent!;
        var tb = ShotKit.Bounds(toolbar, dlg);
        k.Save(dlg, "pixelate-view-2", crop: new Rect(tb.X - 8, tb.Y - 36, tb.Width + 20, tb.Height + 46), marks:
        [
            PixelateKit.UpBox(showMask, dlg),
            PixelateKit.UpBox(barAuto, dlg),
            PixelateKit.UpBox(barW, dlg).Union(PixelateKit.UpBox(barH, dlg)),
            PixelateKit.UpBox(aspect, dlg),
            PixelateKit.UpBox(showGrid, dlg),
        ], firstNumber: 6);

        // 색 하나를 빼 둔 모습(우클릭과 같음): 뺀 견본은 끝에 흐리게, "색 빼기 1개 적용 중 · 모두 되살리기", 끝의 + (원본에서 색 추가).
        var selected = ShotKit.Get<object>(dlg, "_selected");
        var palette = ShotKit.Get<IEnumerable<Color32>>(ShotKit.Get<object>(selected, "Result"), "Palette").ToList();
        ShotKit.Call(dlg, "ToggleColor", palette[Math.Min(6, palette.Count - 1)]);
        k.Settle(500);
        var swatches = ShotKit.Get<WrapPanel>(colors, "_swatches");
        var add = ShotKit.Get<Control>(colors, "_add");
        var excluded = swatches.Children.OfType<Control>().Where(c => !ReferenceEquals(c, add)).Last();
        var notice = ShotKit.Get<Control>(colors, "_notice");
        var area = ShotKit.Bounds(colors, dlg).Union(ShotKit.Bounds(strip, dlg));
        k.Save(dlg, "pixelate-view-4", crop: new Rect(area.X - 34, area.Y - 40, Math.Min(area.Width, 420) + 46, area.Height + 48), marks:
        [
            PixelateKit.UpBox(excluded, dlg, 34),
            PixelateKit.Union(dlg, [.. ((Panel)notice).Children]).Inflate(new Thickness(16, 2, 3, 2)),
            PixelateKit.UpBox(add, dlg, 6),
        ], firstNumber: 12);
        ShotKit.Call(dlg, "RestoreAllColors");
        k.Settle(400);

        // 크게 보기: 결과 칸·원본 칸·격자 보기 단추·색 견본.
        ShotKit.Call(dlg, "OpenZoom");
        k.Settle(800);
        var zoom = ShotKit.Get<Control>(dlg, "_zoom");
        var pair = (Control)ShotKit.Get<object>(zoom, "View");
        var origin = ShotKit.Bounds(pair, dlg).TopLeft;
        var left = ShotKit.Get<Rect>(pair, "LeftPane").Translate(origin);
        var right = ShotKit.Get<Rect>(pair, "RightPane").Translate(origin);
        var gridButton = ShotKit.Get<ToggleButton>(zoom, "GridButton");
        var zoomColors = ShotKit.Get<Control>(dlg, "_zoomColors");
        k.Save(dlg, "pixelate-view-6", marks:
        [
            new Rect(left.X - 14, left.Y, left.Width - 6, left.Height),
            new Rect(right.X - 14, right.Y, right.Width + 14, right.Height),
            PixelateKit.Box(gridButton, dlg, 2),
            PixelateKit.LeftBox(zoomColors, dlg, 12),
        ], firstNumber: 17);
        ShotKit.Call(dlg, "CloseZoom");
        k.Settle(300);

        // 왼쪽 설정: 픽셀 격자의 칸 경계 지역 보정(⑪), 배경의 스티커 테두리 지우기·배경 남기기(⑮⑯).
        ShotKit.Call(dlg, "SetSettingsExpanded", true, false);
        k.Settle(800);
        PixelateKit.OnlyExpand(k, dlg, UiStrings.Pixelate_Section_Grid);
        var grid = PixelateKit.Section(dlg, UiStrings.Pixelate_Section_Grid);
        PixelateKit.ScrollToTop(k, grid);
        var local = ShotKit.Get<CheckBox>(dlg, "_grLocal");
        var minCell = (Control)ShotKit.Get<Control>(dlg, "_grLocalMinCell").Parent!;
        k.SaveControl(grid, "pixelate-view-3", pad: 14, marks: [PixelateKit.Union(dlg, local, minCell).Inflate(new Thickness(12, 4, 4, 4))], firstNumber: 11);

        PixelateKit.OnlyExpand(k, dlg, UiStrings.Pixelate_Section_Background);
        var bg = PixelateKit.Section(dlg, UiStrings.Pixelate_Section_Background);
        PixelateKit.ScrollToTop(k, bg);
        k.SaveControl(bg, "pixelate-view-5", pad: 14, marks: [PixelateKit.LeftBox(ShotKit.Get<CheckBox>(dlg, "_bgSticker"), dlg, 10), PixelateKit.LeftBox(ShotKit.Get<CheckBox>(dlg, "_bgKeep"), dlg, 10)], firstNumber: 15);
    }

    // 넣기: 넣기 단계(①~④) + 창 아래 단추(⑤⑥).
    [Shot("pixelate-output")]
    public static void Output(ShotKit k)
    {
        var dlg = PixelateKit.Open(k, expanded: true);
        PixelateKit.OnlyExpand(k, dlg, UiStrings.Pixelate_Section_Output);
        var section = PixelateKit.Section(dlg, UiStrings.Pixelate_Section_Output);
        PixelateKit.ScrollToTop(k, section);
        var layer = ShotKit.Get<RadioButton>(dlg, "_outLayer");
        var doc = ShotKit.Get<RadioButton>(dlg, "_outDoc");
        var duration = (Control)ShotKit.Get<Control>(dlg, "_outDuration").Parent!;
        var palette = ShotKit.Get<CheckBox>(dlg, "_outPalette");
        k.SaveControl(section, "pixelate-output", pad: 14, marks:
        [
            PixelateKit.LeftBox(layer, dlg, 10),
            PixelateKit.LeftBox(doc, dlg, 10),
            PixelateKit.LeftBox(duration, dlg, 10),
            PixelateKit.LeftBox(palette, dlg, 10),
        ]);
        var newTab = ShotKit.Get<Button>(dlg, "_newTab");
        var ok = ShotKit.Get<Button>(dlg, "OkButton");
        var row = (Control)newTab.Parent!;
        var rb = ShotKit.Bounds(row, dlg);
        k.Save(dlg, "pixelate-output-2", crop: new Rect(dlg.Bounds.Width - 520, rb.Y - 40, 520, rb.Height + 52),
            marks: [PixelateKit.Box(newTab, dlg, 2), PixelateKit.Box(ok, dlg, 2)], firstNumber: 5);
    }

    // AI로 다듬기: AI 단계(①~⑥) + 모델 목록(⑦).
    [Shot("pixelate-ai")]
    public static void Ai(ShotKit k)
    {
        var dlg = PixelateKit.Open(k, expanded: true);
        PixelateKit.OnlyExpand(k, dlg, UiStrings.Pixelate_Section_Ai);
        var section = PixelateKit.Section(dlg, UiStrings.Pixelate_Section_Ai);
        var touch = ShotKit.Get<CheckBox>(dlg, "_aiTouch");
        touch.IsChecked = true;
        k.Settle(300);
        PixelateKit.ScrollToTop(k, section);
        var prompt = ShotKit.Get<TextBox>(dlg, "_aiPrompt");
        var provider = ShotKit.Get<ComboBox>(dlg, "_aiProvider");
        var reference = ShotKit.Get<CheckBox>(dlg, "_aiReference");
        var rank = ShotKit.FindText(section, UiStrings.Pixelate_Ai_Rank, ancestorControl: true);
        var count = ShotKit.Get<Control>(dlg, "_aiRankCount");
        k.SaveControl(section, "pixelate-ai", pad: 14, marks:
        [
            PixelateKit.LeftBox(touch, dlg, 10),
            PixelateKit.LeftBox(prompt, dlg, 10, 1),
            PixelateKit.Box(provider, dlg, 2),
            PixelateKit.LeftBox(reference, dlg, 10),
            PixelateKit.LeftBox(rank, dlg, 10),
            PixelateKit.Box(count, dlg, 2),
        ]);

        provider.IsDropDownOpen = true;
        k.Settle(500);
        var list = PixelateKit.DropDownBounds(provider, dlg);
        var sb = ShotKit.Bounds(section, dlg);
        k.Save(dlg, "pixelate-ai-2", crop: new Rect(sb.X - 14, sb.Y - 14, sb.Width + 28, Math.Max(list.Bottom + 20, ShotKit.Bounds(provider, dlg).Bottom + 80) - sb.Y + 14),
            marks: [list], firstNumber: 7);
        provider.IsDropDownOpen = false;
    }

    // 프리셋: 목록(①)·저장(②)·삭제(③), 저장할 때 묻는 이름 창(④).
    [Shot("pixelate-presets")]
    public static void Presets(ShotKit k)
    {
        var dlg = PixelateKit.Open(k, expanded: true);
        PixelateKit.OnlyExpand(k, dlg);
        var combo = ShotKit.Get<ComboBox>(dlg, "_preset");
        combo.SelectedIndex = 0;
        k.Settle(800);
        combo.IsDropDownOpen = true;
        k.Settle(500);
        var row = (Control)combo.Parent!;
        var buttons = row.GetVisualChildren().OfType<Button>().ToList();
        var list = PixelateKit.DropDownBounds(combo, dlg);
        var b = ShotKit.Bounds(row, dlg);
        k.Save(dlg, "pixelate-presets", crop: new Rect(0, Math.Max(0, b.Y - 16), b.Right + 40, Math.Max(list.Bottom, b.Bottom) - b.Y + 40),
            marks: [list.Union(PixelateKit.Box(combo, dlg, 0)), PixelateKit.Box(buttons[0], dlg, 2), PixelateKit.Box(buttons[1], dlg, 2)]);
        combo.IsDropDownOpen = false;
        k.Settle(300);

        k.Dialog(() => ShotKit.Call(dlg, "SavePreset"), w =>
        {
            var box = ShotKit.Find<TextBox>(w);
            box.Text = "Chibi 48px";
            box.CaretIndex = box.Text.Length;
            ShotKit.Pump(300);
            k.Save(w, "pixelate-presets-2", marks: [PixelateKit.Box(box, w, 2)], firstNumber: 4);
        });
    }
}
