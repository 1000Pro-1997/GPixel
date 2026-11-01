using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Gpixel.Core.AiDraw;
using Gpixel.Core.Edit;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App.Ai;

/// <summary>
/// AI 채팅 창(메인 창에 딸린 도구 창). 사용자 컴퓨터의 AI CLI(Claude Code 또는 Codex, <see cref="AiProvider"/>)와 그 로그인을 쓰고,
/// AI는 편집기 안 MCP 서버(<see cref="McpServer"/>)의 도구로 열린 그림을 고친다.
/// AI마다 탭(<see cref="ChatTab"/>)이 따로 있어 대화·세션·입력·모델이 섞이지 않고, 머리줄 전환 단추는 보이는 탭만 바꾼다
/// (가려진 탭의 차례는 계속 돌고, 도는 동안 탭 이름에 표시). 한 캔버스를 함께 고치지 않도록 차례는 모든 탭을 통틀어 하나만 돈다.
/// 탭을 처음 볼 때 CLI 찾기 → 로그인 → 채팅 순서로 화면이 바뀐다. AI의 한 차례(turn)는 실행취소 한 단계로 묶이고,
/// 차례마다 캔버스 모습과 "이 단계 되돌리기/다시 적용" 단추가 붙는다.
/// 대화는 차례가 끝날 때마다 AI별로 저장되고(<see cref="ChatHistoryStore"/>), "새 대화"는 지난 대화 목록을 보여 주며(누르면 이어 감,
/// 입력란에 쓰면 새 대화), 탭마다 마지막으로 보던 대화(또는 목록)를 다시 열 때 되살린다. "?" 단추는 대화 오른쪽에 도움말 칸
/// (AI가 할 수 있는 일·예시·요령)을 연다 — 창이 그만큼 오른쪽으로 부드럽게 넓어지고(최대화면 대화 칸이 좁아짐) 대화는 그대로 쓸 수 있다.
/// 그 옆 설정 단추는 같은 자리에 그리기 설정 칸(그리는 방식·생성 옵션 — <c>ChatWindow.DrawSettings.cs</c>)을 열며, 둘 중 하나만 열린다.
/// 그림 생성 도구(<see cref="PixelTools.CallAsync"/>)의 진행은 진행 줄에, 결과는 결과 칸("변환 설정 보기" 단추)으로 붙는다.
/// </summary>
public sealed partial class ChatWindow : Window
{
    private static readonly IBrush ErrorBrush = AppTheme.Brush(Color.FromRgb(240, 110, 110));
    private static readonly IBrush UserBubbleBrush = AppTheme.Brush(Color.FromRgb(50, 66, 88));
    private static readonly FontFamily MonoFont = new("Menlo, Consolas, DejaVu Sans Mono, monospace");
    private const int SnapshotMax = 240;
    private const int CanvasAttachSide = 512;
    /// <summary>첨부 이미지의 긴 변 최대(이보다 크면 줄여서 PNG로 보냄).</summary>
    private const int AttachMaxSide = 1568;
    /// <summary>저장하는 첨부 미리보기의 긴 변 최대.</summary>
    private const int SavedThumbSide = 64;
    /// <summary>저장하는 캔버스 모습의 긴 변 최대(이보다 큰 그림은 모습을 남기지 않음 — 큰 그림을 저장하지 않도록).</summary>
    private const int SavedSnapshotSide = 256;
    /// <summary>저장하는 AI 그리기 결과 그림(프레임을 나란히)의 긴 변 최대.</summary>
    private const int SavedAiDrawSide = 1024;
    /// <summary>머리줄 모델 칸 너비: 자리가 넉넉하면 최대, 좁으면 최소까지 줄어든다(상태 글이 먼저 줄어듦).</summary>
    private const int ModelMinWidth = 100, ModelMaxWidth = 190;
    /// <summary>창 최소 너비: 머리줄(AI 탭·가장 좁은 모델 칸·새 대화·설정·?)이 어느 화면 언어에서도 잘리지 않는 너비.</summary>
    private const int WindowMinWidth = 390;
    /// <summary>도움말 칸 너비(왼쪽 테두리 포함).</summary>
    private const double HelpPanelWidth = 340;
    /// <summary>도움말 칸을 끝까지 열고 닫는 시간(ms). 가던 중에 되돌리면 남은 거리만큼만 걸린다.</summary>
    private const double HelpAnimationMs = 220;

    private readonly EditorContext _ctx;
    private readonly McpServer _mcp;
    private readonly AppSettings _settings;

    private readonly TextBlock _status;
    private readonly TabStrip _providerToggle;
    /// <summary>보이는 탭의 모델 칸을 담는 자리.</summary>
    private readonly Border _modelHost;
    private readonly Button _newChat;
    /// <summary>"?" 단추: 눌려 있으면(켜짐) 도움말 칸이 열려 있다.</summary>
    private readonly ToggleButton _help;
    /// <summary>그리기 설정 단추: 눌려 있으면 설정 칸이 열려 있다(도움말 칸과 같은 자리 — 둘 중 하나만).</summary>
    private readonly ToggleButton _drawSettings;
    private readonly IconView _drawSettingsIcon;
    /// <summary>오른쪽 칸 안의 도움말·설정(보이는 하나만).</summary>
    private readonly Control _helpPanel, _drawPanel;
    /// <summary>한 단추를 켜며 다른 단추를 끄는 중(그 끔으로 칸을 닫지 않게).</summary>
    private bool _panelSyncing;
    /// <summary>저장한 대화(<c>&lt;설정 폴더&gt;/Gpixel/Chats/&lt;AI id&gt;</c>).</summary>
    private readonly ChatHistoryStore _store = new(Path.Combine(Platform.AppDataDirectory, "Gpixel", "Chats"));
    private readonly Border _body;

    // ── 도움말 칸(창 오른쪽)
    /// <summary>대화 칸 | 도움말 칸 두 열.</summary>
    private readonly Grid _root;
    /// <summary>도움말 칸 자리: 너비가 0~<see cref="HelpPanelWidth"/>로 늘고 줄며, 안의 칸은 늘 같은 너비라 잘려 드러난다.</summary>
    private readonly Border _helpHost;
    private readonly ScrollViewer _helpScroll = new();
    private readonly DispatcherTimer _helpTimer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
    private readonly Stopwatch _helpClock = new();
    /// <summary>도움말 칸이 드러난 정도(0 = 닫힘, 1 = 다 열림)와, 움직이는 중이면 출발·도착 값과 걸리는 시간.</summary>
    private double _helpShown, _helpFrom, _helpTo, _helpMs;
    /// <summary>창 너비를 바꿔 열고 닫음(보통 창). false면 창은 그대로 두고 대화 칸이 좁아진다(최대화·전체 화면).</summary>
    private bool _helpResizes;
    /// <summary>도움말 칸 때문에 창이 넓어진 너비(DIP). 최대화 중에 열고 닫으면 보통 창으로 돌아올 때 맞춘다.</summary>
    private double _helpGrowth;
    /// <summary>창 너비를 바꾸는 동안 고정하는 대화 칸 너비(DIP).</summary>
    private double _chatWidth;
    /// <summary>
    /// 다 열렸을 때 화면 오른쪽 밖으로 나가지 않게 창을 왼쪽으로 옮기는 거리, 지금 옮겨 둔 거리(물리 픽셀),
    /// 닫힌 상태(옮기기 전)의 창 X.
    /// </summary>
    private int _helpShiftPx, _helpShiftNow, _helpClosedX;

    /// <summary>AI마다 탭 하나(<see cref="AiProvider.All"/> 순서).</summary>
    private readonly List<ChatTab> _tabs;
    private ChatTab _visible;
    private bool _shutdown, _ready, _opened;

    public ChatWindow(EditorContext ctx, McpServer mcp, AppSettings settings)
    {
        _ctx = ctx;
        _mcp = mcp;
        _settings = settings;

        ShowInTaskbar = false;
        MinWidth = WindowMinWidth;
        MinHeight = 360;
        Width = Math.Clamp(settings.ChatWidth, WindowMinWidth, 3000);
        Height = Math.Clamp(settings.ChatHeight, 360, 3000);
        // 기억한 위치가 지금 어느 화면에도 걸치지 않으면(모니터 분리 등) 메인 창 가운데로.
        var bounds = new PixelRect(settings.ChatX, settings.ChatY, (int)Width, (int)Height);
        if (settings.ChatX >= 0 && settings.ChatY >= 0 && Screens.All.Any(sc => sc.WorkingArea.Intersects(bounds)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(settings.ChatX, settings.ChatY);
        }
        else WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── 머리줄: 로그인 상태 · AI(Claude/Codex) 탭 · 모델 · 새 대화
        _status = new TextBlock
        {
            Foreground = AppTheme.DimTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0),
        };
        _tabs = [.. AiProvider.All.Select((p, i) => new ChatTab(this, p, i))];
        _visible = _tabs[AiProvider.All.ToList().IndexOf(AiProvider.FromId(settings.ChatProvider))];
        _providerToggle = new TabStrip(TabStripKind.Segmented)
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0),
        };
        // 글자 뒤 점 = 그 탭이 답을 쓰는 중(SetIconVisible로 켜고 끔)
        foreach (var p in AiProvider.All)
        {
            int i = _providerToggle.Add(p.Name, (g, r, b) => VectorIcons.Draw(g, VectorIcon.Dot, r, 8, b, filled: true),
                string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ChatWindow_02EFD3D4E7,p.Name,p.ProductName,p.AccountText), iconAfter: true);
            _providerToggle.SetIconVisible(i, false);
        }
        _providerToggle.SelectedIndex = _visible.Index;
        _providerToggle.SelectionChanged += (_, _) => OnProviderToggled();
        _modelHost = new Border { VerticalAlignment = VerticalAlignment.Center };
        _newChat = Ui.Button(UiStrings.S_ChatWindow_ChatWindow_ACBC4AE706, (_, _) => _visible.NewConversation());
        _newChat.Tip(UiStrings.Chat_NewChat_Tip);
        var helpIcon = Ui.Icon(VectorIcon.QuestionMark, 14);
        helpIcon.Margin = new Thickness(0, 1);
        _help = new ToggleButton
        {
            Content = helpIcon,
            Width = 28,
            Margin = new Thickness(0, 0, 2, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        }.Tip(UiStrings.Chat_Help_Tip);
        _help.IsCheckedChanged += (_, _) => OnSidePanelToggled(_help);
        _drawSettingsIcon = Ui.Icon(VectorIcon.SlidersHorizontal, 14);
        _drawSettingsIcon.Margin = new Thickness(0, 1);
        _drawSettings = new ToggleButton
        {
            Content = _drawSettingsIcon,
            Width = 28,
            Margin = new Thickness(0, 0, 2, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        }.Tip(UiStrings.AiDraw_Settings_Tip);
        _drawSettings.IsCheckedChanged += (_, _) => OnSidePanelToggled(_drawSettings);
        // 모델 칸은 상태 글보다 훨씬 큰 몫의 별 너비라, 넉넉하면 최대 너비이고 좁아지면 최소 너비까지 줄어든다.
        var header = new Grid { Margin = new Thickness(4, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.ColumnDefinitions.Add(new ColumnDefinition(20, GridUnitType.Star) { MinWidth = ModelMinWidth + 6, MaxWidth = ModelMaxWidth + 6 });
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.Children.Add(_status);
        Grid.SetColumn(_providerToggle, 1);
        header.Children.Add(_providerToggle);
        Grid.SetColumn(_modelHost, 2);
        header.Children.Add(_modelHost);
        Grid.SetColumn(_newChat, 3);
        header.Children.Add(_newChat);
        Grid.SetColumn(_drawSettings, 4);
        header.Children.Add(_drawSettings);
        Grid.SetColumn(_help, 5);
        header.Children.Add(_help);
        var headerBorder = new Border
        {
            Background = AppTheme.BackBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header,
        };
        DockPanel.SetDock(headerBorder, Dock.Top);

        _body = new Border { Background = AppTheme.PanelBrush };
        var chatColumn = new DockPanel { Children = { headerBorder, _body } };

        _helpPanel = BuildHelpPanel();
        _drawPanel = BuildDrawSettingsPanel();
        _helpHost = new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1, 0, 0, 0),
            ClipToBounds = true,
            IsVisible = false,
            Child = new Panel { Children = { _helpPanel, _drawPanel } },
        };
        Grid.SetColumn(_helpHost, 1);
        _root = new Grid { Children = { chatColumn, _helpHost } };
        _root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        _root.ColumnDefinitions.Add(new ColumnDefinition(0, GridUnitType.Pixel));
        Content = _root;
        _helpTimer.Tick += (_, _) => StepHelp();

        _ctx.UndoChanged += OnUndoChanged;
        _mcp.Tools.AiDrawProgress += OnAiDrawProgress;
        _mcp.Tools.AiDrawCompleted += OnAiDrawCompleted;
        _ctx.ProjectReplaced += OnProjectReplaced;
        PositionChanged += (_, _) => SaveBounds();
        Opened += (_, _) =>
        {
            _opened = true;
            _visible.Activate();
            // 도움말 내용은 처음 그릴 때(글자 배치) 오래 걸려, 미리 만들어 두어야 처음 "?"를 눌렀을 때도 칸이 끊김 없이 열린다.
            Dispatcher.UIThread.Post(PrepareHelp, DispatcherPriority.Background);
        };
        ShowTab(_visible);
        _ready = true;
    }

    /// <summary>
    /// 시험용(화면 점검 하네스): CLI 찾기·로그인 없이 탭마다 <paramref name="createSession"/>이 만든 세션으로 채팅 화면을 연다.
    /// 창을 띄우기 전에 불러야 한다(띄운 뒤에는 CLI 확인이 이미 시작됨).
    /// </summary>
    internal void ShowChatWithoutCli(Func<AiProvider, IChatSession> createSession)
    {
        foreach (var t in _tabs) t.ShowChatWithoutCli(createSession(t.Provider));
        RefreshHeader();
    }

    /// <summary>보이는 탭의 입력란에 글을 넣는다(보내지 않음 — 사용자가 보내기를 눌러야 함). "AI로 설명 붙이기"가 부른다.</summary>
    public void PrefillInput(string text) => _visible.Prefill(text);

    /// <summary>보이는 탭을 그 AI(설정 이름 <c>claude</c>·<c>codex</c>)로 바꾼다.</summary>
    public void SelectProvider(string providerId)
    {
        int i = AiProvider.All.ToList().IndexOf(AiProvider.FromId(providerId));
        if (i >= 0 && !ReferenceEquals(_tabs[i], _visible)) _providerToggle.SelectedIndex = i;
    }

    /// <summary>보이는 탭에 그림을 첨부한다(보내지 않음). 픽셀 아트면 정수 배로 키워 보낸다.</summary>
    public void AttachImage(PixelImage image, bool pixelArt) => _visible.AttachImage(image, pixelArt);

    /// <summary>AI 프로세스를 모두 끝낸다(앱을 닫을 때 메인 창이 부름). 진행 중인 차례는 멈춘다.</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        SaveBounds();
        foreach (var t in _tabs) t.StopTurn();
        _shutdown = true;
        _helpTimer.Stop();
        foreach (var t in _tabs) t.Close();
        _ctx.UndoChanged -= OnUndoChanged;
        _mcp.Tools.AiDrawProgress -= OnAiDrawProgress;
        _mcp.Tools.AiDrawCompleted -= OnAiDrawCompleted;
        _ctx.ProjectReplaced -= OnProjectReplaced;
    }

    protected override void OnClosed(EventArgs e)
    {
        Shutdown();
        base.OnClosed(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) SaveBounds();
        else if (change.Property == WindowStateProperty) OnWindowStateChanged();
    }

    // ───────────── 창 위치 ─────────────

    /// <summary>
    /// 창 위치·크기를 설정에 넣는다. 도움말 칸이 창을 넓혔으면 그 몫을 빼고, 화면 안에 두려고 왼쪽으로 옮겼으면 그만큼 되돌린
    /// 값(= 대화만 있는 창)을 넣는다 — 다시 열면 도움말 칸은 닫힌 채로 시작한다.
    /// </summary>
    private void SaveBounds()
    {
        if (!_ready || _shutdown || WindowState != WindowState.Normal || !IsVisible) return;
        _settings.ChatX = Math.Max(0, Position.X + _helpShiftNow);
        _settings.ChatY = Math.Max(0, Position.Y);
        double chatWidth = _helpResizes && _helpTimer.IsEnabled ? _chatWidth : ClientSize.Width - _helpGrowth;
        if (chatWidth > 0) _settings.ChatWidth = (int)Math.Round(chatWidth);
        if (ClientSize.Height > 0) _settings.ChatHeight = (int)Math.Round(ClientSize.Height);
    }

    // ───────────── 탭 ─────────────

    /// <summary>Claude ↔ Codex 단추: 보이는 탭만 바꾼다(어느 탭의 세션·차례도 멈추지 않음).</summary>
    private void OnProviderToggled()
    {
        if (_providerToggle.SelectedIndex < 0) return;
        var tab = _tabs[_providerToggle.SelectedIndex];
        if (ReferenceEquals(tab, _visible)) return;
        ShowTab(tab);
    }

    /// <summary>탭을 보이게 한다: 몸통·모델 칸·머리줄 상태를 그 탭 것으로. 창이 열려 있으면 처음 볼 때 확인을 시작한다.</summary>
    private void ShowTab(ChatTab tab)
    {
        _visible = tab;
        _settings.ChatProvider = tab.Provider.Id;
        Title = tab.Provider.Name;
        _modelHost.Child = tab.Model;
        _body.Child = tab.Root;
        RefreshHeader();
        RefreshDrawSettings();
        if (_opened) tab.Activate();
    }

    /// <summary>머리줄 상태 글·새 대화 단추를 보이는 탭에 맞춘다(도움말 칸은 탭과 상관없이 늘 열 수 있다).</summary>
    private void RefreshHeader()
    {
        _status.Text = _visible.StatusText;
        _newChat.IsEnabled = _visible.InChat;
    }

    /// <summary>차례가 도는 탭(없으면 null). 차례는 모든 탭을 통틀어 하나만 돈다.</summary>
    private ChatTab? RunningTab => _tabs.FirstOrDefault(t => t.IsRunning);

    /// <summary>"지금 그림만"(두 탭 공통 설정): 설정에 넣고 모든 탭의 단추를 맞춘다. 진행 중인 차례의 범위는 그대로(다음 메시지부터).</summary>
    private void SetCurrentFileOnly(bool on)
    {
        if (_settings.AiCurrentFileOnly == on) return;
        _settings.AiCurrentFileOnly = on;
        foreach (var t in _tabs) t.SyncScopeToggle(on);
    }

    /// <summary>어느 탭의 차례가 시작·끝남: 탭 이름의 작업 중 표시와 모든 탭의 보내기·되돌리기 단추를 고친다.</summary>
    private void OnTurnsChanged()
    {
        foreach (var t in _tabs)
        {
            _providerToggle.SetIconVisible(t.Index, t.IsRunning);
            t.UpdateInputState();
            t.RefreshSteps();
        }
    }

    private void OnProjectReplaced(object? sender, EventArgs e)
    {
        foreach (var t in _tabs) t.OnProjectReplaced();
    }

    private void OnUndoChanged(object? sender, EventArgs e)
    {
        foreach (var t in _tabs) t.RefreshSteps();
    }

    private string? ProjectFolder()
        => _ctx.Project?.Path is { } file && Path.GetDirectoryName(file) is { Length: > 0 } dir ? dir : null;

    // ───────────── 메시지 그리기 도우미 ─────────────

    /// <summary>답 글(마크다운) 보기. <see cref="MarkdownView.SetText"/>로 글을 넣는다.</summary>
    private MarkdownView AnswerView() => new(MonoFont, OpenLink);

    /// <summary>답 글 하나를 그린 보기.</summary>
    private MarkdownView AnswerView(string text)
    {
        var view = AnswerView();
        view.SetText(text);
        return view;
    }

    /// <summary>도구 인자 짧은 요약(legend 같은 긴 것은 뺌).</summary>
    private static string Summarize(JsonObject? input)
    {
        if (input is null) return "";
        var parts = new List<string>();
        foreach (var (key, value) in input)
        {
            if (key == "legend" || value is null) continue;
            string text = value switch
            {
                JsonArray a when key == "rows" => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_Summarize_473D2F6697,a.Count),
                JsonArray a => $"[{a.Count}]",
                JsonObject => "{…}",
                _ => Shorten(value.ToString(), 18),
            };
            parts.Add($"{key}={text}");
        }
        return Shorten(string.Join(" ", parts), 80);
    }

    private static string Shorten(string s, int max)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    // ───────────── 이미지 도우미 ─────────────

    /// <summary>비트맵을 PNG로(긴 변이 너무 크면 줄임).</summary>
    private static byte[] ToPng(Bitmap bmp)
    {
        var size = bmp.PixelSize;
        int longSide = Math.Max(size.Width, size.Height);
        Bitmap src = bmp;
        if (longSide > AttachMaxSide)
        {
            double k = (double)AttachMaxSide / longSide;
            src = bmp.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(size.Width * k)), Math.Max(1, (int)(size.Height * k))),
                BitmapInterpolationMode.HighQuality);
        }
        using var ms = new MemoryStream();
        src.Save(ms);
        if (!ReferenceEquals(src, bmp)) src.Dispose();
        return ms.ToArray();
    }

    private static Control Thumb(Bitmap bmp, bool pixelArt, int size)
    {
        var px = bmp.PixelSize;
        double k = Math.Min((double)size / Math.Max(1, px.Width), (double)size / Math.Max(1, px.Height));
        if (pixelArt && k > 1) k = Math.Floor(k);
        var image = new Image { Source = bmp, Width = Math.Max(1, px.Width * k), Height = Math.Max(1, px.Height * k), Stretch = Stretch.Fill };
        if (pixelArt) RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        return WithImageActions(new CheckerBackground { Child = image, Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left }, bmp);
    }

    /// <summary>
    /// 대화 속 그림: 한 번 누르면 크게 보기(<see cref="ShowChipFlyout"/>), 우클릭하면 이미지 저장·복사.
    /// </summary>
    private static Control WithImageActions(Control view, Bitmap bmp)
    {
        view.Cursor = new Cursor(StandardCursorType.Hand);
        view.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Handled = true;
            ShowChipFlyout(view, bmp, null);
        };
        AttachImageMenu(view, bmp);
        return view;
    }

    /// <summary>그림 우클릭 메뉴(이미지 저장…·이미지 복사). 저장·복사는 화면에 보이는 비트맵 그대로(픽셀 크기 그대로).</summary>
    private static void AttachImageMenu(Control view, Bitmap bmp)
        => PopupMenus.Attach(view, () => ImageActions.Menu(view, () => ToPixelImage(bmp), () => "gpixel-chat.png"));

    /// <summary>비트맵 → 픽셀 그림(PNG를 거침). 읽지 못하면 null.</summary>
    private static PixelImage? ToPixelImage(Bitmap bmp)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            return PngIO.Load(ms);
        }
        catch (Exception)
        {
            // 플랫폼 인코더·디코더가 던지는 예외 형식은 정해져 있지 않다 — 메뉴 항목을 끈다.
            return null;
        }
    }

    /// <summary>저장할 첨부 미리보기(긴 변 <see cref="SavedThumbSide"/> 이하 PNG, base64). 만들지 못하면 null.</summary>
    private static string? SavedThumb(Attachment a)
    {
        try
        {
            var px = a.Thumb.PixelSize;
            int longSide = Math.Max(px.Width, px.Height);
            Bitmap src = a.Thumb;
            if (longSide > SavedThumbSide)
            {
                double k = (double)SavedThumbSide / longSide;
                src = a.Thumb.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(px.Width * k)), Math.Max(1, (int)(px.Height * k))),
                    a.PixelArt ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
            }
            using var ms = new MemoryStream();
            src.Save(ms);
            if (!ReferenceEquals(src, a.Thumb)) src.Dispose();
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception)
        {
            // 플랫폼 그림 인코더가 던지는 예외 형식은 정해져 있지 않다 — 미리보기 없이 저장한다.
            return null;
        }
    }

    /// <summary>저장한 PNG(base64)를 비트맵으로. 깨졌으면 null.</summary>
    private static Bitmap? DecodeImage(string base64)
    {
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(base64));
            return new Bitmap(ms);
        }
        catch (Exception)
        {
            // 깨진 base64·PNG(디코더 예외 형식은 플랫폼마다 다름)
            return null;
        }
    }

    /// <summary>차례가 끝난 뒤의 캔버스 모습(작으면 정수 배로 키우고, 크면 <see cref="SnapshotMax"/>에 맞춰 줄임).</summary>
    private static Control SnapshotView(Bitmap bmp)
    {
        var px = bmp.PixelSize;
        int longSide = Math.Max(1, Math.Max(px.Width, px.Height));
        double scale = longSide <= SnapshotMax ? Math.Max(1, SnapshotMax / longSide) : (double)SnapshotMax / longSide;
        var image = new Image { Source = bmp, Width = px.Width * scale, Height = px.Height * scale, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        return WithImageActions(new Border
        {
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new CheckerBackground { Child = image },
        }, bmp);
    }

    /// <summary>지금 프레임의 보이는 레이어 합성(그룹은 부위를 배치한 대로).</summary>
    private PixelImage? Composite(SpriteProject p)
    {
        return p.Frames.Count == 0 ? null : p.Flatten(Math.Clamp(_ctx.CurrentFrame, 0, p.Frames.Count - 1));
    }

    /// <summary>정수 배 최근접 확대.</summary>
    private static PixelImage ScaleUp(PixelImage src, int n)
    {
        var dst = new PixelImage(src.Width * n, src.Height * n);
        for (int y = 0; y < dst.Height; y++)
        {
            int row = (y / n) * src.Width, dstRow = y * dst.Width;
            for (int x = 0; x < dst.Width; x++) dst.Pixels[dstRow + x] = src.Pixels[row + x / n];
        }
        return dst;
    }

    // ───────────── 작은 UI 도우미 ─────────────

    private static TextBlock TitleText(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = AppTheme.FontSize + 2,
        Foreground = AppTheme.TextBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock WrapText(string text, bool dim = false, bool error = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = error ? ErrorBrush : dim ? AppTheme.DimTextBrush : AppTheme.TextBrush,
    };

    /// <summary>복사 단추가 달린 명령 한 줄.</summary>
    private Control CommandBox(string title, string command)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new Border
        {
            Background = AppTheme.InputBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new SelectableTextBlock { Text = command, FontFamily = MonoFont, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.TextBrush },
        });
        var copy = Ui.Button(UiStrings.S_ChatWindow_CommandBox_05C2558491, (_, _) => CopyText(command));
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        return Ui.Group(title, grid);
    }

    /// <summary>누르면 브라우저로 여는 주소 + 복사 단추.</summary>
    private Control LinkRow(string url)
    {
        var link = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock
            {
                Text = url,
                Foreground = AppTheme.AccentBrush,
                TextDecorations = TextDecorations.Underline,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        link.Click += (_, _) => OpenLink(url);
        var copy = Ui.Button(UiStrings.S_ChatWindow_LinkRow_E4434318F0, (_, _) => CopyText(url));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(link);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        return grid;
    }

    /// <summary>주소를 기본 브라우저로 연다(실패하면 알림).</summary>
    private void OpenLink(string url)
    {
        try
        {
            AiCli.OpenUrl(url);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_LinkRow_5865E61E9C,ex.Message));
        }
    }

    private void CopyText(string text)
    {
        if (Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }

    // ───────────── 도움말 칸("?") ─────────────

    /// <summary>도움말 칸: 머리줄(제목 + 닫기) 아래 내용 스크롤. 내용은 처음 열 때 만든다(MCP 도구 목록을 읽음).</summary>
    private Control BuildHelpPanel()
    {
        var title = new TextBlock
        {
            Text = UiStrings.Chat_Help_Title,
            FontWeight = FontWeight.Bold,
            Foreground = AppTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0),
        };
        var close = Ui.IconButton(VectorIcon.X, UiStrings.Chat_Help_Close, (_, _) => _help.IsChecked = false, 28, 12);
        close.Margin = new Thickness(0, 0, 2, 0);
        Grid.SetColumn(close, 1);
        var header = new Border
        {
            Background = AppTheme.BackBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid { Margin = new Thickness(4, 3), ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { title, close } },
        };
        DockPanel.SetDock(header, Dock.Top);
        _helpScroll.Background = AppTheme.PanelBrush;
        _helpScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _helpScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // 칸 자리(_helpHost)가 늘고 주는 동안에도 내용은 늘 같은 너비라 다시 배치되지 않고 잘려 드러난다.
        return new DockPanel
        {
            Width = HelpPanelWidth - 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { header, _helpScroll },
        };
    }

    /// <summary>
    /// 도움말 내용을 만들고 미리 배치해 둔다(한 번만). 칸 자리는 닫혀 있어도 내용은 늘 같은 너비라, 열 때 다시 배치하지 않는다.
    /// 예시를 누르면 보이는 탭의 입력란에 들어간다(칸은 열린 채).
    /// </summary>
    private void PrepareHelp()
    {
        if (_shutdown || _helpScroll.Content is not null) return;
        _helpScroll.Content = BuildHelp(text => _visible.Prefill(text));
        _helpHost.Child!.Measure(new Avalonia.Size(HelpPanelWidth - 1, Math.Max(1, ClientSize.Height)));
    }

    /// <summary>
    /// "?" 단추가 켜지고 꺼짐: 도움말 칸을 <see cref="HelpAnimationMs"/>에 걸쳐 열거나 닫는다(빠르게 시작해 천천히 멈춤).
    /// 보통 창이면 대화 칸 너비는 그대로 두고 창을 오른쪽으로 넓히며, 다 열린 창이 화면 오른쪽 밖으로 나가면 그만큼 함께 왼쪽으로
    /// 옮긴다(닫으면 되돌림). 최대화·전체 화면이면 창은 두고 대화 칸을 좁힌다. 움직이는 중에 다시 누르면 지금 자리에서 되돌아간다.
    /// </summary>
    private void AnimateHelp(bool open)
    {
        if (_shutdown) return;
        if (open && _helpShown == 0)
        {
            PrepareHelp();
            _helpScroll.Offset = default;
        }
        bool wasResizing = _helpResizes && _helpTimer.IsEnabled;
        _helpResizes = WindowState == WindowState.Normal && IsVisible;
        if (_helpResizes)
        {
            // 움직이던 중이면 대화 칸 너비는 그대로(창 크기가 아직 따라오지 않았을 수 있음).
            if (!wasResizing) _chatWidth = ClientSize.Width - _helpGrowth;
            _helpClosedX = Position.X + _helpShiftNow;
            if (open) _helpShiftPx = HelpShift();
            MinWidth = WindowMinWidth;
        }
        _helpFrom = _helpShown;
        _helpTo = open ? 1 : 0;
        _helpMs = HelpAnimationMs * Math.Abs(_helpTo - _helpFrom);
        _helpClock.Restart();
        _helpTimer.Start();
    }

    /// <summary>움직임 한 걸음(초당 60번): 걸린 시간으로 드러난 정도를 정하고(ease-out), 끝나면 멈춘다.</summary>
    private void StepHelp()
    {
        double t = _helpMs <= 0 ? 1 : Math.Min(1, _helpClock.Elapsed.TotalMilliseconds / _helpMs);
        _helpShown = t >= 1 ? _helpTo : _helpFrom + (_helpTo - _helpFrom) * (1 - Math.Pow(1 - t, 3));
        if (t >= 1) _helpTimer.Stop();
        ApplyHelp();
        if (t < 1) return;
        if (_helpResizes) MinWidth = WindowMinWidth + _helpGrowth;
        if (_helpShown == 0) _helpShiftPx = 0;
        SaveBounds();
    }

    /// <summary>
    /// 드러난 정도(<see cref="_helpShown"/>)를 화면에 옮긴다. 창 너비를 바꾸는 동안은 대화 칸을 고정 너비로 두고 도움말 칸이 남는 자리를
    /// 차지하며(대화가 눌리지 않음), 멈춰 있으면 대화 칸이 남는 자리를 차지한다(사용자가 창 크기를 바꾸면 대화 칸이 늘고 줆).
    /// </summary>
    private void ApplyHelp()
    {
        double panel = HelpPanelWidth * _helpShown;
        _helpHost.IsVisible = _helpShown > 0;
        var columns = _root.ColumnDefinitions;
        bool moving = _helpResizes && _helpTimer.IsEnabled;
        columns[0].Width = moving ? new GridLength(_chatWidth) : new GridLength(1, GridUnitType.Star);
        columns[1].Width = moving ? new GridLength(1, GridUnitType.Star) : new GridLength(panel);
        if (!_helpResizes) return;
        _helpShiftNow = (int)Math.Round(_helpShiftPx * _helpShown);
        var position = new PixelPoint(_helpClosedX - _helpShiftNow, Position.Y);
        // 넓힐 때는 먼저 왼쪽으로 옮기고, 좁힐 때는 줄인 뒤 옮긴다(오른쪽 끝이 화면 밖으로 튀지 않게).
        bool growing = panel > _helpGrowth;
        if (growing && position != Position) Position = position;
        _helpGrowth = panel;
        Width = _chatWidth + panel;
        if (position != Position) Position = position;
    }

    /// <summary>
    /// 도움말 칸을 다 열었을 때 창 오른쪽 끝이 지금 화면 작업 영역 오른쪽을 넘는 만큼(물리 픽셀) — 그만큼 창을 왼쪽으로 옮긴다.
    /// 창 왼쪽 끝은 작업 영역 밖으로 밀지 않는다.
    /// </summary>
    private int HelpShift()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return 0;
        double frame = Math.Max(0, (FrameSize?.Width ?? ClientSize.Width) - ClientSize.Width);
        int right = _helpClosedX + (int)Math.Ceiling((_chatWidth + HelpPanelWidth + frame) * DesktopScaling);
        var area = screen.WorkingArea;
        return Math.Clamp(right - area.Right, 0, Math.Max(0, _helpClosedX - area.X));
    }

    /// <summary>
    /// 최대화·전체 화면이 되거나 풀림: 움직이던 도움말 칸은 끝 상태로 바로 맞추고, 최대화 중에는 창 대신 대화 칸을 좁힌다.
    /// 보통 창으로 돌아오면 창 너비(와 화면 안에 두려고 옮긴 위치)를 지금 도움말 칸에 맞춘다(대화 칸 너비는 그대로).
    /// </summary>
    private void OnWindowStateChanged()
    {
        if (!_ready || _shutdown) return;
        if (_helpTimer.IsEnabled)
        {
            _helpTimer.Stop();
            _helpShown = _helpTo;
        }
        _helpResizes = false;
        ApplyHelp();
        if (WindowState == WindowState.Normal)
            Dispatcher.UIThread.Post(() =>
            {
                if (_shutdown || WindowState != WindowState.Normal || _helpTimer.IsEnabled) return;
                _helpResizes = true;
                _chatWidth = ClientSize.Width - _helpGrowth;
                _helpClosedX = Position.X + _helpShiftNow;
                _helpShiftPx = _helpShown > 0 ? HelpShift() : 0;
                ApplyHelp();
                MinWidth = WindowMinWidth + _helpGrowth;
            }, DispatcherPriority.Background);
    }

    /// <summary>도움말의 도구 묶음: 이름·쉬운 설명(지금 언어)과 그 묶음의 MCP 도구 이름.</summary>
    private static (string Name, string Text, string[] Tools)[] HelpGroups() =>
    [
        (UiStrings.Chat_Help_GroupGenerate, UiStrings.Chat_Help_GroupGenerate_Text, ["generate_pixel_art"]),
        (UiStrings.Chat_Help_GroupView, UiStrings.Chat_Help_GroupView_Text, ["get_document", "get_image", "get_pixels"]),
        (UiStrings.Chat_Help_GroupDraw, UiStrings.Chat_Help_GroupDraw_Text, ["draw_pixels", "set_pixels", "draw_shape", "fill", "clear", "replace_color"]),
        (UiStrings.Chat_Help_GroupFrames, UiStrings.Chat_Help_GroupFrames_Text, ["add_frame", "delete_frame", "set_frame_duration", "add_tag"]),
        (UiStrings.Chat_Help_GroupLayers, UiStrings.Chat_Help_GroupLayers_Text, ["add_layer", "set_layer", "set_current"]),
        (UiStrings.Chat_Help_GroupParts, UiStrings.Chat_Help_GroupParts_Text, ["add_group", "add_part", "set_part"]),
        (UiStrings.Chat_Help_GroupSkins, UiStrings.Chat_Help_GroupSkins_Text, ["list_skins", "add_skin", "set_skin", "list_outfits", "save_outfit", "apply_outfit"]),
        (UiStrings.Chat_Help_GroupEffects, UiStrings.Chat_Help_GroupEffects_Text,
            ["list_effect_types", "get_effects", "add_effect", "set_effect", "remove_effect", "apply_effects", "expand_canvas_for_effects"]),
        (UiStrings.Chat_Help_GroupCanvas, UiStrings.Chat_Help_GroupCanvas_Text, ["resize_canvas", "set_palette", "set_colors"]),
        (UiStrings.Chat_Help_GroupFiles, UiStrings.Chat_Help_GroupFiles_Text, ["new_project", "save_project", "open_file"]),
        (UiStrings.Chat_Help_GroupReferences, UiStrings.Chat_Help_GroupReferences_Text,
            ["search_references", "get_reference", "get_style_rules", "add_reference", "set_reference_info"]),
    ];

    /// <summary>
    /// 도움말 내용: AI가 할 수 있는 일(MCP 서버가 실제로 내놓는 도구를 묶음별로 — 묶음에 없는 도구는 "그 밖의 도구"),
    /// 예시 부탁(누르면 <paramref name="useExample"/>), 잘 쓰는 요령, Discord 제보 단추.
    /// </summary>
    private Control BuildHelp(Action<string> useExample)
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(12, 10) };

        stack.Children.Add(TitleText(UiStrings.Chat_Help_ToolsTitle));
        stack.Children.Add(WrapText(UiStrings.Chat_Help_ToolsIntro, dim: true));
        var names = _mcp.Tools.ListTools().Select(t => t?["name"]?.GetValue<string>()).OfType<string>().ToList();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, text, tools) in HelpGroups())
        {
            var present = tools.Where(names.Contains).ToList();
            if (present.Count == 0) continue;
            shown.UnionWith(present);
            stack.Children.Add(HelpGroup(name, text, present));
        }
        if (names.Where(n => !shown.Contains(n)).ToList() is { Count: > 0 } other)
            stack.Children.Add(HelpGroup(UiStrings.Chat_Help_GroupOther, null, other));

        stack.Children.Add(TitleText(UiStrings.Chat_Help_ExamplesTitle));
        stack.Children.Add(WrapText(UiStrings.Chat_Help_ExamplesIntro, dim: true));
        string[] examples =
        [
            UiStrings.Chat_Help_Example1, UiStrings.Chat_Help_Example2, UiStrings.Chat_Help_Example3, UiStrings.Chat_Help_Example4,
            UiStrings.Chat_Help_Example5, UiStrings.Chat_Help_Example6, UiStrings.Chat_Help_Example7, UiStrings.Chat_Help_Example8,
            UiStrings.Chat_Help_Example9, UiStrings.Chat_Help_Example10,
        ];
        foreach (var example in examples)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = example, TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 5),
            }.Tip(UiStrings.Chat_Help_ExampleTip);
            button.Click += (_, _) => useExample(example);
            stack.Children.Add(button);
        }

        stack.Children.Add(TitleText(UiStrings.Chat_Help_TipsTitle));
        string[] tips =
        [
            UiStrings.Chat_Help_Tip1, UiStrings.Chat_Help_Tip2, UiStrings.Chat_Help_Tip3,
            UiStrings.Chat_Help_Tip4, UiStrings.Chat_Help_Tip5, UiStrings.Chat_Help_Tip6,
            UiStrings.Chat_Help_Tip7, UiStrings.Chat_Help_Tip8,
        ];
        foreach (var tip in tips)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new IconView(VectorIcon.Dot, AppTheme.AccentBrush) { Filled = true, Width = 8, Height = 8, Margin = new Thickness(2, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top });
            var text = WrapText(tip);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        var feedback = Ui.DiscordButton(UiStrings.Chat_Help_Feedback, (_, _) => OpenLink(AppInfo.DiscordUrl));
        feedback.HorizontalAlignment = HorizontalAlignment.Left;
        feedback.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(feedback);
        return stack;
    }

    /// <summary>도구 묶음 칸: 이름, 쉬운 설명, 도구 이름(고정폭).</summary>
    private static Control HelpGroup(string name, string? text, IReadOnlyList<string> tools)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.Bold, Foreground = AppTheme.TextBrush, TextWrapping = TextWrapping.Wrap });
        if (text is not null) stack.Children.Add(WrapText(text));
        stack.Children.Add(new SelectableTextBlock
        {
            Text = string.Join(", ", tools),
            FontFamily = MonoFont,
            FontSize = AppTheme.SmallFontSize,
            Foreground = AppTheme.DimTextBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        return new Border
        {
            Background = AppTheme.BackBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6),
            Child = stack,
        };
    }

    // ───────────── 내부 형식 ─────────────

    /// <summary>보낼 첨부 이미지(PNG 바이트 + 미리보기).</summary>
    private sealed record Attachment(byte[] Png, Bitmap Thumb, bool PixelArt);

    /// <summary>끝난 차례 하나의 실행취소 묶음과 그 단추.</summary>
    private sealed record UndoStep(SpriteProject Project, IUndoCommand Command, Button Button);

    /// <summary>
    /// 대화에 보이는 사용자 메시지 하나(= 차례 하나): 말풍선 줄(지울 때 여기부터 끝까지 뺌), 저장할 줄, 고치기·지우기 단추 줄,
    /// 다시 보낼 수 있는 첨부(이번 실행 중 보낸 것만, 저장한 대화는 없음), 그 차례의 실행취소 묶음(있으면).
    /// </summary>
    private sealed class UserTurn(Control row, ChatItem item, Control actions, IReadOnlyList<Attachment> attachments)
    {
        public Control Row { get; } = row;
        public ChatItem Item { get; } = item;
        public Control Actions { get; } = actions;
        public IReadOnlyList<Attachment> Attachments { get; } = attachments;
        public UndoStep? Step { get; set; }
    }

    /// <summary>진행 중인 차례.</summary>
    private sealed class Turn(string label)
    {
        public string Label { get; } = label;
        /// <summary>실행취소 묶음을 연 프로젝트.</summary>
        public SpriteProject? Project { get; set; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public MarkdownView? StreamHost { get; set; }
        /// <summary>스트리밍 글을 곧 다시 그리도록 걸어 둠(100ms에 한 번).</summary>
        public bool RenderPending { get; set; }
        public StringBuilder Stream { get; } = new();
        /// <summary>도구 호출 id → 그 줄과 저장할 대화 줄(실패하면 둘 다 고침).</summary>
        public Dictionary<string, (TextBlock Line, ChatItem Item)> Tools { get; } = new();
    }

    /// <summary>투명한 곳이 보이게 체커 무늬 위에 자식을 그리는 틀.</summary>
    private sealed class CheckerBackground : Decorator
    {
        public override void Render(DrawingContext g) => AppTheme.DrawChecker(g, new Rect(Bounds.Size), 6);
    }

    /// <summary>탭 가운데에 보이는 것: 지금 대화 / 지난 대화 목록.</summary>
    private enum TabView { Chat, List }

    /// <summary>
    /// AI 하나(Claude/Codex)의 탭: CLI 경로·로그인·세션·대화 글·입력란·첨부·모델 칸·실행취소 단계를 따로 가진다.
    /// 가려져 있어도 세션은 살아 있어, 도는 차례의 글은 계속 이 탭에 붙는다. 머리줄 상태는 보일 때만 창에 반영된다.
    /// 채팅 화면 가운데는 지금 대화 또는 지난 대화 목록이고 입력란은 늘 아래에 있다. 대화는 차례가 끝날 때마다 저장한다.
    /// </summary>
    private sealed partial class ChatTab
    {
        private readonly ChatWindow _w;

        private readonly DockPanel _chatPanel;
        /// <summary>채팅 화면 가운데(대화 또는 목록).</summary>
        private readonly Border _center = new();
        private readonly ScrollViewer _scroll;
        private readonly StackPanel _messages;
        private readonly ScrollViewer _listScroll;
        private readonly StackPanel _list;
        private readonly TextBlock _thinking;
        /// <summary>입력란 위에 그림 칩을 그리는 층(칩 글자 자리를 덮음 — <c>ChatWindow.ImageChips.cs</c>).</summary>
        private readonly Canvas _chipLayer;
        private readonly TextBox _input;
        private readonly Button _sendButton, _stopButton;
        private readonly ToggleButton _scopeToggle;

        /// <summary>"지금 그림만" 단추를 설정 값에 맞춘다(다른 탭에서 바꿨을 때).</summary>
        public void SyncScopeToggle(bool on)
        {
            if (_scopeToggle.IsChecked != on) _scopeToggle.IsChecked = on;
        }

        /// <summary>입력란의 그림(칩 번호 순서 — <see cref="ChatImageTokens"/>).</summary>
        private readonly List<Attachment> _attachments = new();
        private readonly List<UndoStep> _steps = new();
        /// <summary>지금 대화의 사용자 메시지(차례) 순서대로. 마지막 것에만 고치기·지우기 단추가 보인다.</summary>
        private readonly List<UserTurn> _userTurns = new();
        private readonly DispatcherTimer _terminalPoll = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _installPoll = new() { Interval = TimeSpan.FromSeconds(2) };
        /// <summary>차례 중 "생각 중…" 줄을 진행 상황(단계·받은 양·걸린 시간)으로 고쳐 쓴다.</summary>
        private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private DateTime _turnStart, _phaseStart;
        /// <summary>지금 단계: wait(응답 기다림) / thinking / tool_use(도구 호출 작성) / run(도구 실행) / text(답 쓰기).</summary>
        private string _phase = "wait";
        private string? _phaseTool;
        private int _phaseChars;
        /// <summary>그림 생성 도구가 알린 진행("프레임 2/6 생성 중") — 도구 실행 단계의 진행 줄에 보인다.</summary>
        private string? _aiDrawStatus;

        private string? _cliPath;
        private IChatSession? _session;
        private CliLogin? _login;
        /// <summary>모델 목록 칸(<see cref="Model"/>의 항목 순서).</summary>
        private IReadOnlyList<ModelChoice> _modelChoices = [];
        private Turn? _turn;
        /// <summary>가운데에 보이는 것.</summary>
        private TabView _view = TabView.List;
        /// <summary>지금 대화(저장한 적이 있거나 첫 메시지를 보낸 대화). null = 목록에서 아직 시작하지 않음.</summary>
        private SavedChat? _chat;
        /// <summary>지금 대화에 그린 줄(저장할 내용).</summary>
        private List<ChatItem> _items = new();
        /// <summary><see cref="_started"/>: 처음 보일 때 확인을 시작했음(다시 볼 때는 확인하지 않음). <see cref="_restored"/>: 마지막으로 보던 대화를 되살렸음.</summary>
        private bool _follow = true, _checking, _polling, _installChecking, _started, _fillingModels, _restored;

        public ChatTab(ChatWindow w, AiProvider provider, int index)
        {
            _w = w;
            Provider = provider;
            Index = index;

            Model = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(3),
                VerticalAlignment = VerticalAlignment.Center,
                // 좁아지면 고른 모델 이름은 말줄임(머리줄 단추가 밀려 잘리지 않게).
                SelectionBoxItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>(
                    (label, _) => new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis }, supportsRecycling: false),
            };
            Model.SelectionChanged += (_, _) => OnModelChanged();
            FillModels();

            // ── 채팅 화면
            _messages = new StackPanel { Spacing = 6, Margin = new Thickness(10, 8) };
            _scroll = Ui.ScrollHost(_messages);
            _scroll.ScrollChanged += OnScrollChanged;
            _thinking = new TextBlock
            {
                Text = UiStrings.S_ChatWindow_ChatTab_8F1426E012,
                Foreground = AppTheme.DimTextBrush,
                FontStyle = FontStyle.Italic,
                Margin = new Thickness(2, 2),
                TextWrapping = TextWrapping.Wrap,
            };

            _input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 54,
                MaxHeight = 160,
                Watermark = Ui.Shortcut(UiStrings.S_ChatWindow_ChatTab_95FEEA4ADC),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_input, ScrollBarVisibility.Auto);
            _input.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
            _chipLayer = new Canvas { ClipToBounds = false };
            SetupInlineImages();
            var imageButton = Ui.Button(UiStrings.S_ChatWindow_ChatTab_B428204BA3, (_, _) => AttachFile()).Tip(UiStrings.S_ChatWindow_ChatTab_046C36A571);
            var canvasButton = Ui.Button(UiStrings.S_ChatWindow_ChatTab_F9D4399092, (_, _) => AttachCanvas()).Tip(UiStrings.S_ChatWindow_ChatTab_B9C7852D0E);
            _scopeToggle = new ToggleButton
            {
                Content = UiStrings.Chat_FileOnly,
                IsChecked = _w._settings.AiCurrentFileOnly,
                Margin = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            }.Tip(UiStrings.Chat_FileOnly_Tip);
            _scopeToggle.IsCheckedChanged += (_, _) => _w.SetCurrentFileOnly(_scopeToggle.IsChecked == true);
            _sendButton = Ui.Button(UiStrings.S_ChatWindow_ChatTab_CF5843D1B0, (_, _) => Send()).Tip("Enter");
            _stopButton = Ui.Button(UiStrings.S_ChatWindow_ChatTab_1CB431966C, (_, _) => StopTurn());
            _stopButton.IsVisible = false;
            var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
            buttons.Children.Add(imageButton);
            Grid.SetColumn(canvasButton, 1);
            buttons.Children.Add(canvasButton);
            Grid.SetColumn(_scopeToggle, 2);
            buttons.Children.Add(_scopeToggle);
            Grid.SetColumn(_sendButton, 4);
            buttons.Children.Add(_sendButton);
            Grid.SetColumn(_stopButton, 5);
            buttons.Children.Add(_stopButton);
            var inputArea = new Border
            {
                Background = AppTheme.BackBrush,
                BorderBrush = AppTheme.BorderBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 6),
                Child = new StackPanel { Children = { new Panel { Children = { _input, _chipLayer } }, buttons } },
            };
            DockPanel.SetDock(inputArea, Dock.Bottom);
            _list = new StackPanel { Spacing = 4, Margin = new Thickness(10, 8) };
            _listScroll = Ui.ScrollHost(_list);
            _center.Child = _listScroll;
            _chatPanel = new DockPanel { Children = { inputArea, _center } };

            _terminalPoll.Tick += async (_, _) => await PollLoginAsync();
            _installPoll.Tick += async (_, _) => await PollInstallAsync();
            _progressTimer.Tick += (_, _) => UpdateProgress();
            SetHeader("", inChat: false);
        }

        public AiProvider Provider { get; }
        /// <summary>전환 단추에서의 번호(<see cref="AiProvider.All"/> 순서).</summary>
        public int Index { get; }
        /// <summary>탭 몸통: 확인 중 / 설치 안내 / 로그인 / 채팅 화면 중 하나를 담는다.</summary>
        public Border Root { get; } = new();
        /// <summary>이 AI의 모델 칸(보이는 동안 머리줄에 놓인다).</summary>
        public ComboBox Model { get; }
        /// <summary>머리줄 상태 글(로그인 계정·확인 중 등).</summary>
        public string StatusText { get; private set; } = "";
        /// <summary>채팅 화면인지(모델·새 대화를 쓸 수 있음).</summary>
        public bool InChat { get; private set; }
        public bool IsRunning => _turn is not null;

        private bool IsShown => ReferenceEquals(_w._visible, this);

        /// <summary>탭이 보이게 됨: 처음이면 확인(설치 → 로그인 → 채팅)을 시작하고, 채팅 중이면 입력란에 포커스.</summary>
        public void Activate()
        {
            if (!_started)
            {
                _started = true;
                Recheck();
            }
            else if (_turn is null && _cliPath != Provider.Locate(_w._settings)) Recheck();
            else if (ReferenceEquals(Root.Child, _chatPanel)) FocusInput();
        }

        /// <summary>창을 닫을 때: 로그인·세션을 끝낸다(차례는 먼저 <see cref="StopTurn"/>).</summary>
        public void Close()
        {
            _installPoll.Stop();
            LeaveLogin();
            _session?.Dispose();
            _session = null;
        }

        private void FocusInput() => Dispatcher.UIThread.Post(() => _input.Focus(), DispatcherPriority.Background);

        // ───────────── 상태 확인(설치 → 로그인 → 채팅) ─────────────

        private void SetHeader(string text, bool inChat)
        {
            StatusText = text;
            InChat = inChat;
            Model.IsEnabled = inChat;
            if (IsShown) _w.RefreshHeader();
        }

        private void SetBody(Control content) => Root.Child = content;

        private async Task CheckAsync()
        {
            if (_checking || _w._shutdown) return;
            _checking = true;
            try
            {
                LeaveLogin();
                SetHeader(UiStrings.S_ChatWindow_CheckAsync_F75B2590E0, inChat: false);
                SetBody(new TextBlock
                {
                    Text = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_CheckAsync_CA460A15B1,Provider.ProductName),
                    Foreground = AppTheme.DimTextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var path = Provider.Locate(_w._settings);
                if (path is null)
                {
                    if (_turn is not null) StopTurn();
                    _session?.Dispose();
                    _session = null;
                    _cliPath = null;
                    ShowNoCli();
                    return;
                }
                if (_cliPath != path)
                {
                    if (_turn is not null) StopTurn();
                    _session?.Dispose();
                    _session = null;
                }
                _cliPath = path;
                var status = await Provider.GetAuthStatusAsync(path);
                if (_w._shutdown) return;
                if (status.LoggedIn) ShowChat(status);
                else ShowLogin(status);
            }
            finally
            {
                _checking = false;
            }
        }

        private async void Recheck() => await CheckAsync();
        private async Task PollInstallAsync()
        {
            if (_installChecking || _checking || _w._shutdown) return;
            _installChecking = true;
            try
            {
                var path = Provider.Locate(_w._settings);
                if (path is null) return;
                if (_cliPath != path)
                {
                    await CheckAsync();
                    return;
                }
                var status = await Provider.GetAuthStatusAsync(path);
                if (!_w._shutdown && status.LoggedIn) ShowChat(status);
            }
            finally
            {
                _installChecking = false;
            }
        }

        private void StartInstall()
        {
            var steps = AiInstaller.Preview(Provider);
            if (Ui.Ask(_w, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_StartInstall_8776DA7BEF,Provider.ProductName,steps),
                    UiStrings.S_ChatWindow_StartInstall_BD9FDFD3C7, MessageButtons.OKCancel, MessageIcon.Question) != DialogResult.OK) return;
            if (!AiInstaller.OpenTerminal(Provider, out var error))
            {
                Ui.Error(_w, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_StartInstall_4B456B142A,error));
                return;
            }
            _installPoll.Start();
            ShowNoCli();
        }


        /// <summary>CLI가 없을 때: 설치 안내.</summary>
        private void ShowNoCli()
        {
            var p = Provider;
            SetHeader(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowNoCli_C0F440A767,p.ProductName), inChat: false);
            var stack = new StackPanel { Spacing = 8, Margin = new Thickness(14, 12) };
            stack.Children.Add(TitleText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowNoCli_EB50F0AD30,p.ProductName)));
            stack.Children.Add(WrapText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowNoCli_A6A8288124,p.ProductName,p.Command,p.AccountText) +
                                        UiStrings.S_ChatWindow_ShowNoCli_4D48263369));
            var install = Ui.Button(UiStrings.S_ChatWindow_ShowNoCli_ED06427F94, (_, _) => StartInstall());
            install.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(install);
            if (_installPoll.IsEnabled) stack.Children.Add(WrapText(UiStrings.S_ChatWindow_ShowNoCli_37191A6276, dim: true));
            else stack.Children.Add(WrapText(UiStrings.S_ChatWindow_ShowNoCli_3EA0647605, dim: true));
            foreach (var (title, command) in p.InstallCommands) stack.Children.Add(_w.CommandBox(title, command));
            var recheck = Ui.Button(UiStrings.S_ChatWindow_ShowNoCli_2736EF27CC, (_, _) => Recheck());
            recheck.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(recheck);

            var overridePath = p.GetPathOverride(_w._settings);
            var pathBox = new TextBox { Text = overridePath ?? "", Watermark = p.PathExample, Margin = new Thickness(0, 2) };
            var browse = Ui.Button(UiStrings.S_ChatWindow_ShowNoCli_F47FA9147B, (_, _) =>
            {
                if (Ui.OpenFile(_w, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowNoCli_3F2EDB54CC,p.Command), null) is { } picked) pathBox.Text = picked;
            });
            var apply = Ui.Button(UiStrings.S_ChatWindow_ShowNoCli_306897216F, (_, _) =>
            {
                var text = (pathBox.Text ?? "").Trim();
                p.SetPathOverride(_w._settings, text.Length == 0 ? null : text);
                Recheck();
            });
            var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            pathRow.Children.Add(pathBox);
            Grid.SetColumn(browse, 1);
            pathRow.Children.Add(browse);
            Grid.SetColumn(apply, 2);
            pathRow.Children.Add(apply);
            var pathStack = new StackPanel { Spacing = 4 };
            pathStack.Children.Add(WrapText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowNoCli_1CC476FE7A,p.Command,p.Command,p.Command), dim: true));
            pathStack.Children.Add(pathRow);
            if (!string.IsNullOrWhiteSpace(overridePath))
                pathStack.Children.Add(WrapText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowNoCli_F8C688F131,p.Command,overridePath), error: true));
            stack.Children.Add(Ui.Group(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowNoCli_09620464D8,p.Command), pathStack));
            SetBody(Ui.ScrollHost(stack));
        }

        /// <summary>로그인 안 됨: 숨은 로그인(주소 표시)·터미널 로그인·다시 확인.</summary>
        private void ShowLogin(AuthStatus status)
        {
            var p = Provider;
            SetHeader(UiStrings.S_ChatWindow_ShowLogin_3B08918AF3, inChat: false);
            var stack = new StackPanel { Spacing = 8, Margin = new Thickness(14, 12) };
            stack.Children.Add(TitleText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowLogin_9B4E400681,p.Name)));
            stack.Children.Add(WrapText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowLogin_4B41926EA7,p.AccountText,p.Name) +
                                        string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowLogin_7211786FF1,p.ProductName)));
            if (status.Error is { } err) stack.Children.Add(WrapText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowLogin_BDF84EA2D9,err), error: true));
            var info = new StackPanel { Spacing = 6 };
            var login = Ui.Button(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowLogin_86A2C2515E,p.Name), (_, _) => StartLogin(info));
            var terminal = Ui.Button(UiStrings.S_ChatWindow_ShowLogin_F93C9FEC46, (_, _) => StartTerminalLogin(info)).Tip(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ShowLogin_B35819C965,p.Command,string.Join(' ', p.LoginArgs)));
            var recheck = Ui.Button(UiStrings.S_ChatWindow_ShowLogin_E9BFEE873E, (_, _) => Recheck());
            stack.Children.Add(new WrapPanel { Children = { login, terminal, recheck } });
            stack.Children.Add(info);
            SetBody(Ui.ScrollHost(stack));
        }

        private void StartLogin(StackPanel info)
        {
            if (_cliPath is not { } path) return;
            LeaveLogin();
            info.Children.Clear();
            info.Children.Add(WrapText(UiStrings.S_ChatWindow_StartLogin_BDD2BF6309, dim: true));
            var urls = new StackPanel { Spacing = 4 };
            var output = WrapText("", dim: true);
            var codeBox = new TextBox { Watermark = UiStrings.S_ChatWindow_StartLogin_B79F003D82, Margin = new Thickness(0, 2) };
            var codeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            codeRow.Children.Add(codeBox);
            var takesCode = Provider.LoginTakesCode;
            var login = new CliLogin(Provider, path);
            var sendCode = Ui.Button(UiStrings.S_ChatWindow_StartLogin_B3F1AA5FE7, (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(codeBox.Text)) return;
                login.SendCode(codeBox.Text);
                codeBox.Text = "";
            });
            Grid.SetColumn(sendCode, 1);
            codeRow.Children.Add(sendCode);
            var codePanel = new StackPanel { Spacing = 2, IsVisible = false };
            codePanel.Children.Add(WrapText(UiStrings.S_ChatWindow_StartLogin_1F1673FEF2, dim: true));
            codePanel.Children.Add(codeRow);
            info.Children.AddRange([urls, codePanel, output]);

            login.UrlFound += url =>
            {
                urls.Children.Add(_w.LinkRow(url));
                codePanel.IsVisible = takesCode;
            };
            login.OutputLine += line => output.Text = line;
            login.Succeeded += st =>
            {
                if (!ReferenceEquals(_login, login)) return;
                _login = null;
                login.Dispose();
                ShowChat(st);
            };
            login.Failed += message =>
            {
                if (!ReferenceEquals(_login, login)) return;
                _login = null;
                login.Dispose();
                codePanel.IsVisible = false;
                info.Children.Add(WrapText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_StartLogin_6D8D76D688,message), error: true));
            };
            try
            {
                login.Start();
                _login = login;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                login.Dispose();
                info.Children.Add(WrapText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_StartLogin_80640A2D7A,Provider.Command,ex.Message), error: true));
            }
        }

        private void StartTerminalLogin(StackPanel info)
        {
            if (_cliPath is not { } path) return;
            LeaveLogin();
            info.Children.Clear();
            var command = $"{Provider.Command} {string.Join(' ', Provider.LoginArgs)}";
            if (!AiCli.OpenLoginTerminal(path, Provider.LoginArgs, out var error))
            {
                info.Children.Add(WrapText(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_StartTerminalLogin_B0C0F4D01D,error,command), error: true));
                return;
            }
            info.Children.Add(WrapText(UiStrings.S_ChatWindow_StartTerminalLogin_067B1B838D, dim: true));
            _terminalPoll.Start();
        }

        /// <summary>터미널 로그인 중: 2초마다 로그인 상태를 본다.</summary>
        private async Task PollLoginAsync()
        {
            if (_polling || _cliPath is not { } path) return;
            _polling = true;
            try
            {
                var status = await Provider.GetAuthStatusAsync(path);
                if (_terminalPoll.IsEnabled && status.LoggedIn && !_w._shutdown)
                {
                    _terminalPoll.Stop();
                    ShowChat(status);
                }
            }
            finally
            {
                _polling = false;
            }
        }

        /// <summary>로그인 화면을 떠남: 숨은 로그인 프로세스·상태 확인 타이머를 끝낸다.</summary>
        private void LeaveLogin()
        {
            _terminalPoll.Stop();
            _login?.Dispose();
            _login = null;
        }

        private void ShowChat(AuthStatus status)
        {
            _installPoll.Stop();
            LeaveLogin();
            if (_cliPath is not { } path) return;
            var who = status.Describe();
            SetHeader(who.Length > 0 ? who : UiStrings.S_ChatWindow_ShowChat_B46AD2AE0A, inChat: true);
            if (_session is null) AttachSession(Provider.CreateSession(path, _w._mcp));
            EnterChat();
        }

        /// <summary>시험용(<see cref="ChatWindow.ShowChatWithoutCli"/>): CLI 확인·로그인 없이 주어진 세션으로 채팅 화면을 연다.</summary>
        public void ShowChatWithoutCli(IChatSession session)
        {
            _started = true;
            _session?.Dispose();
            _session = null;
            AttachSession(session);
            SetHeader(Provider.Name, inChat: true);
            EnterChat();
        }

        /// <summary>새 세션을 붙인다. 열어 둔 저장 대화가 있으면 그 세션을 이어 가게 한다(CLI 경로가 바뀌어 세션을 새로 만들 때 등).</summary>
        private void AttachSession(IChatSession session)
        {
            _session = session;
            session.Model = CurrentModel();
            HookSession(session);
            if (_chat?.SessionId is { } id) session.Resume(id, _chat.SessionDirectory);
        }

        /// <summary>채팅 화면을 보인다. 처음이면 마지막으로 보던 대화(없거나 지워졌으면 목록)를 되살린다.</summary>
        private void EnterChat()
        {
            SetBody(_chatPanel);
            if (!_restored)
            {
                _restored = true;
                RestoreLastView();
            }
            UpdateInputState();
            if (IsShown) FocusInput();
        }

        // ───────────── 세션 이벤트(가려져 있어도 이 탭에 쓴다) ─────────────

        private void HookSession(IChatSession s)
        {
            var sessionPath = _cliPath;
            s.TextStarted += () =>
            {
                if (_turn is { } t) BeginText(t);
            };
            s.TextDelta += delta =>
            {
                if (_turn is not { } t) return;
                if (t.StreamHost is null) BeginText(t);
                t.Stream.Append(delta);
                ScheduleStreamRender(t);
            };
            s.AssistantText += text =>
            {
                if (_turn is not { } t || text.Length == 0) return;
                if (t.StreamHost is { } host)
                {
                    host.SetText(text);
                    t.StreamHost = null;
                    t.Stream.Clear();
                }
                else
                {
                    AddItem(_w.AnswerView(text));
                }
                Record(new ChatItem { Kind = ChatItemKind.Assistant, Text = text });
            };
            s.ToolUse += info =>
            {
                if (_turn is not { } t) return;
                SetPhase("run", info.Name);
                FinishStream(t);
                var item = new ChatItem { Kind = ChatItemKind.Tool, Text = $"• {info.Name}" + (Summarize(info.Input) is { Length: > 0 } sum ? $" · {sum}" : "") };
                var line = ToolLine(item.Text, error: false);
                if (info.Id.Length > 0) t.Tools[info.Id] = (line, item);
                AddItem(line);
                Record(item);
            };
            s.BlockStarted += (kind, name) => { if (_turn is not null) SetPhase(kind, name); };
            s.BlockProgress += chars => _phaseChars += chars;
            s.ToolResult += info => { if (_turn is not null && info.ToolUseId.Length > 0) SetPhase("wait", null); };
            s.ToolResult += info =>
            {
                if (_turn is not { } t || !info.IsError || !t.Tools.TryGetValue(info.ToolUseId, out var tool)) return;
                tool.Line.Foreground = ErrorBrush;
                if (info.Text.Length > 0) tool.Line.Text += $" — {info.Text}";
                tool.Item.Error = true;
                tool.Item.Text = tool.Line.Text ?? tool.Item.Text;
            };
            s.TurnCompleted += r =>
            {
                string? error = r.IsError ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_HookSession_E1F3DD4697,(string.IsNullOrWhiteSpace(r.Text) ? r.Subtype ?? UiStrings.S_ChatWindow_HookSession_365CF8E859 : r.Text)) : null;
                if (_turn is not null) EndTurn(error, r, cancelled: false);
                else if (error is not null) AddSystemLine(error, error: true);
                // 로그인이 풀렸으면 로그인 화면으로(대화 내용은 남아 있다가 다시 로그인하면 보인다).
                if (r.NeedsLogin) Recheck();
            };
            s.Exited += info =>
            {
                var msg = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_HookSession_5674519D81,Provider.Name,info.ExitCode);
                if (info.StderrTail.Length > 0) msg += "\n" + info.StderrTail;
                if (_turn is not null) EndTurn(msg, null, cancelled: false);
                else AddSystemLine(msg, error: true);
            };
            s.Notice += text => AddSystemLine(text, error: false);
            s.McpProblem += text => AddSystemLine(text + "\n" + UiStrings.FormatChat_McpCliPath(path: sessionPath ?? UiStrings.AiStatus_Unknown), error: true);
            s.ModelResolved += model =>
            {
                if (ReferenceEquals(s, _session) && Provider.RememberResolved(_w._settings, s.Model, model)) FillModels();
            };
        }

        // ───────────── 보내기·차례 ─────────────

        private string? CurrentModel()
            => _modelChoices.Count == 0 ? null : _modelChoices[Math.Clamp(Model.SelectedIndex, 0, _modelChoices.Count - 1)].Value;

        /// <summary>모델 목록을 이 AI의 칸으로 채우고 설정에 저장된 모델을 고른다(이름에 실제 버전이 붙으면 다시 부름).</summary>
        private void FillModels()
        {
            _fillingModels = true;
            try
            {
                _modelChoices = Provider.Models(_w._settings);
                Model.Items.Clear();
                foreach (var c in _modelChoices)
                {
                    var item = new ComboBoxItem { Content = c.Label };
                    if (c.Tip is { Length: > 0 } tip) item.Tip(tip);
                    Model.Items.Add(item);
                }
                var saved = Provider.GetModel(_w._settings);
                Model.SelectedIndex = Math.Max(0, _modelChoices.ToList().FindIndex(c => c.Value == saved));
                Model.Tip(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_FillModels_B806D71CEE,Provider.Name,Provider.ProductName));
            }
            finally
            {
                _fillingModels = false;
            }
        }

        private void OnModelChanged()
        {
            if (_fillingModels || Model.SelectedIndex < 0) return;
            var model = CurrentModel();
            if (Provider.GetModel(_w._settings) == model) return;
            Provider.SetModel(_w._settings, model);
            if (_session is null) return;
            if (_turn is not null) StopTurn();
            _session.Model = model;
            _session.Restart();
            AddSystemLine(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_OnModelChanged_C640BCC05A,_modelChoices[Model.SelectedIndex].Label), error: false);
        }

        /// <summary>"새 대화" 단추: 도는 차례를 멈추고 세션을 잊은 뒤 지난 대화 목록을 보인다(입력란에 쓰면 새 대화가 시작됨).</summary>
        public void NewConversation()
        {
            if (_turn is not null) StopTurn();
            _session?.Reset();
            ClearTranscript();
            _chat = null;
            ShowView(TabView.List);
            FocusInput();
        }

        private void Send()
        {
            if (_session is not { } session || _turn is not null || _w._shutdown) return;
            SyncInlineImages();
            // 입력란 글(칩 = 화면 언어 이름)을 저장할 모양(칩 이름 "Image")으로. 칩 번호 = 첨부 순서.
            var text = ChatImageTokens.Relabel((_input.Text ?? "").Trim(), ChatImageTokens.StoredLabel);
            if (text.Length == 0 && _attachments.Count == 0) return;
            if (_cliPath != Provider.Locate(_w._settings))
            {
                AddSystemLine(UiStrings.Chat_CliPathChanged, error: false, record: false);
                Recheck();
                return;
            }
            // 제목·차례 이름에 쓸 보이는 글.
            var plain = ChatImageTokens.Plain(text, UiStrings.Chat_Image_Label).Trim();
            // 목록에서 보내면 새 대화로.
            if (_view == TabView.List)
            {
                session.Reset();
                ClearTranscript();
                _chat = null;
                ShowView(TabView.Chat);
            }
            // 실행취소 묶음은 하나만 열 수 있으므로 다른 탭의 차례가 끝날 때까지 보내지 않는다.
            if (_w.RunningTab is { } other)
            {
                AddSystemLine(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_Send_2A1D03CC20,other.Provider.Name), error: false, record: false);
                return;
            }
            var attachments = _attachments.ToList();
            session.WorkingDirectory = _w.ProjectFolder();
            session.Model = CurrentModel();
            // "지금 그림만": 보내는 순간 열린 문서로 편집 범위를 정하고(도구가 막음), AI에게도 알린다.
            var ctx = _w._ctx;
            var scope = _w._settings.AiCurrentFileOnly && ctx.Project is { } sp ? new EditScope(sp) : null;
            _w._mcp.Tools.Scope = scope;
            // 마지막 차례를 지운 대화: CLI 세션에서는 차례를 뺄 수 없으므로 새 세션으로 보내고, 남은 대화 정리를 앞에 붙인다.
            var modelText = ChatImageTokens.ToModel(text);
            var message = scope is null ? modelText : UiStrings.FormatChat_FileOnly_Prefix(name: scope.Project.DisplayName, text: modelText);
            // 그리는 방식은 선택한 AI 탭의 실제 방식으로 고정해 모델에게 전하고, 보내기 성공 뒤 대화에도 기록한다.
            var drawMode = _w._settings.AiDraw.EffectiveMode(Provider.Id);
            message = AiCli.DrawingModeLine(drawMode, Provider.Id == AiDrawSettings.GeneratingProvider) + "\n" + message;
            // "팔레트 색만 사용": 처음부터 팔레트 색으로 그리게 활성 팔레트 색을 알린다(도구도 다른 색은 가장 가까운 팔레트 색으로 바꿈).
            if (_w._settings.AiDraw.PaletteOnly && new PaletteLock(_w._mcp.Tools.Host.Palette) is { IsEmpty: false } palette)
                message = AiCli.PaletteLine(palette) + "\n" + message;
            if (_chat is { NeedsRecap: true })
            {
                session.Reset();
                if (ChatRecap.Build(_items) is { } recap) message = ChatRecap.Compose(recap, message);
            }
            try
            {
                _w._mcp.Tools.TurnImages = [.. attachments.Select(a => (a.Png, a.PixelArt))];
                session.Send(message, attachments.Select(a => a.Png).ToList());
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _w._mcp.Tools.Scope = null;
                _w._mcp.Tools.TurnImages = [];
                AddSystemLine(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_Send_E1ACACFBF7,Provider.Command,ex.Message), error: true, record: false);
                return;
            }
            _input.Text = "";
            ClearAttachments();
            _follow = true;
            if (_chat is null)
            {
                var now = DateTime.UtcNow;
                _chat = new SavedChat
                {
                    Id = ChatHistoryStore.NewId(),
                    Provider = Provider.Id,
                    Title = ChatImageTokens.WithoutTokens(text).Length > 0 ? ChatHistoryStore.MakeTitle(plain) : UiStrings.Chat_History_ImageTitle,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };
                RememberOpen(_chat.Id);
            }
            // 차례를 모두 지운 대화에 다시 쓰면 제목도 이 메시지로.
            else if (!ChatRecap.HasTurns(_items)) _chat.Title = ChatImageTokens.WithoutTokens(text).Length > 0 ? ChatHistoryStore.MakeTitle(plain) : UiStrings.Chat_History_ImageTitle;
            var userItem = new ChatItem
            {
                Kind = ChatItemKind.User,
                Text = text,
                ImageCount = attachments.Count,
                // 칩 번호와 맞도록 미리보기를 만들지 못한 그림은 빈 글로 자리를 지킨다.
                Images = attachments.Count == 0 ? null : [.. attachments.Select(a => SavedThumb(a) ?? "")],
            };
            AddUserBubble(userItem, [.. attachments.Select(a => (Bitmap?)a.Thumb)], attachments);
            Record(userItem);
            AddSystemLine(UiStrings.FormatChat_DrawMode_Notice(mode: drawMode switch
            {
                AiDrawMode.Generate => UiStrings.AiDraw_Mode_Generate,
                AiDrawMode.DirectAutoCheck => UiStrings.AiDraw_Mode_AutoCheck,
                _ => UiStrings.AiDraw_Mode_Direct,
            }), error: false);
            if (scope is not null) AddSystemLine(UiStrings.FormatChat_FileOnly_Notice(name: scope.Project.DisplayName), error: false);

            var label = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_Send_9DB69B8394,Provider.Name,Shorten(plain.Length > 0 ? plain : UiStrings.S_ChatWindow_Send_A2D9AB8F67, 24));
            var turn = new Turn(label);
            if (_w._ctx.Project is { } p)
            {
                // 사용자가 하던 떠 있는 선택 편집은 이 차례에 섞이지 않게 먼저 확정한다.
                _w._ctx.CommitFloating();
                p.History.BeginGroup(label);
                turn.Project = p;
            }
            _turn = turn;
            _messages.Children.Add(_thinking);
            _turnStart = DateTime.UtcNow;
            SetPhase("wait", null);
            _progressTimer.Start();
            _w.OnTurnsChanged();
        }

        public void StopTurn()
        {
            if (_turn is null) return;
            _w._mcp.Tools.CancelAiDraw();
            _session?.Cancel();
            EndTurn(null, null, cancelled: true);
        }

        /// <summary>
        /// 차례를 마무리한다: 실행취소 묶음을 닫고, 바뀐 것이 있으면 캔버스 모습과 되돌리기 단추, 끝에 비용·시간. 그리고 대화를 저장한다.
        /// </summary>
        private void EndTurn(string? error, TurnResult? result, bool cancelled)
        {
            if (_turn is not { } turn) return;
            _turn = null;
            _w._mcp.Tools.Scope = null;
            _w._mcp.Tools.TurnImages = [];
            _aiDrawStatus = null;
            FinishStream(turn);
            _messages.Children.Remove(_thinking);
            _progressTimer.Stop();

            IUndoCommand? group = null;
            SpriteProject? project = turn.Project;
            if (project is not null && ReferenceEquals(project, _w._ctx.Project)) group = project.History.EndGroup();

            if (error is not null) AddSystemLine(error, error: true);
            if (group is not null && project is not null) AddSnapshot(project, group);

            var parts = new List<string>();
            if (cancelled) parts.Add(UiStrings.S_ChatWindow_EndTurn_D19A601695);
            if (result?.CostUsd is { } cost && cost > 0) parts.Add("$" + cost.ToString("0.000", CultureInfo.InvariantCulture));
            double ms = result?.DurationMs ?? turn.Clock.Elapsed.TotalMilliseconds;
            parts.Add((ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + UiStrings.S_ChatWindow_EndTurn_7EEAC40074);
            var footer = string.Join(" · ", parts);
            AddItem(FooterLine(footer));
            Record(new ChatItem { Kind = ChatItemKind.Footer, Text = footer });
            SaveChat();
            _w.OnTurnsChanged();
        }

        // ───────────── 저장한 대화 ─────────────

        /// <summary>대화 줄을 저장할 내용에 더한다(다음 저장 때 파일에 들어감).</summary>
        private void Record(ChatItem item) => _items.Add(item);

        /// <summary>
        /// 지금 대화를 저장한다(세션 ID·작업 폴더·모델·고친 시각 포함). 넘친 옛 대화는 보관소가 지운다.
        /// 새 세션 ID를 얻었으면 남은 대화 정리는 더 붙이지 않는다(<see cref="SavedChat.NeedsRecap"/>).
        /// </summary>
        private void SaveChat()
        {
            if (_chat is not { } chat) return;
            if (_session is { } s)
            {
                chat.Model = s.Model;
                chat.SessionId = s.SessionId;
                chat.SessionDirectory = s.SessionDirectory;
            }
            if (chat.SessionId is not null) chat.NeedsRecap = false;
            chat.UpdatedUtc = DateTime.UtcNow;
            chat.Items = _items;
            try
            {
                _w._store.Save(chat);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AddSystemLine(UiStrings.FormatChat_History_SaveFailed(message: ex.Message), error: true, record: false);
            }
        }

        /// <summary>이 탭에서 마지막으로 보던 대화를 설정에 적는다(null = 목록).</summary>
        private void RememberOpen(string? id)
        {
            var map = _w._settings.ChatLastOpen ??= new Dictionary<string, string>();
            if (id is null) map.Remove(Provider.Id);
            else map[Provider.Id] = id;
        }

        /// <summary>창을 열 때: 마지막으로 보던 대화가 남아 있으면 열고, 아니면(목록이었거나 지워짐) 목록.</summary>
        private void RestoreLastView()
        {
            if (_w._settings.ChatLastOpen?.GetValueOrDefault(Provider.Id) is { } id && _w._store.Load(Provider.Id, id) is { } chat) OpenChat(chat);
            else ShowView(TabView.List);
        }

        /// <summary>가운데를 바꾼다(목록이면 다시 읽어 그림).</summary>
        private void ShowView(TabView view)
        {
            _view = view;
            if (view == TabView.List)
            {
                RememberOpen(null);
                RefreshList();
                _center.Child = _listScroll;
            }
            else _center.Child = _scroll;
        }

        /// <summary>지금 대화 화면을 비운다(세션은 그대로).</summary>
        private void ClearTranscript()
        {
            _messages.Children.Clear();
            _steps.Clear();
            _userTurns.Clear();
            _items = new List<ChatItem>();
            _follow = true;
        }

        /// <summary>목록에서 고른 대화를 연다(그 사이 지워졌으면 목록을 다시 그림).</summary>
        private void OpenSaved(string id)
        {
            if (_w._store.Load(Provider.Id, id) is { } chat) OpenChat(chat);
            else RefreshList();
        }

        /// <summary>
        /// 저장한 대화를 연다: 줄을 다시 그리고(그때의 되돌리기 단추는 없음), 모델을 그 대화 것으로 고르고,
        /// 세션이 그 CLI 세션을 같은 작업 폴더에서 이어 가게 한다(세션 ID가 없으면 다음 메시지는 새 대화).
        /// </summary>
        private void OpenChat(SavedChat chat)
        {
            if (_turn is not null) StopTurn();
            ClearTranscript();
            _chat = chat;
            _items = chat.Items;
            foreach (var item in chat.Items) RenderSaved(item);
            SelectModel(chat.Model);
            if (_session is { } s)
            {
                s.Model = CurrentModel();
                if (chat.SessionId is { } sid) s.Resume(sid, chat.SessionDirectory);
                else s.Reset();
            }
            AddSystemLine(chat.NeedsRecap ? UiStrings.Chat_History_Recap
                : chat.SessionId is null ? UiStrings.Chat_History_NoSession : UiStrings.Chat_History_Opened, error: false, record: false);
            RememberOpen(chat.Id);
            ShowView(TabView.Chat);
            Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
            FocusInput();
        }

        /// <summary>
        /// 모델 칸을 <paramref name="model"/>로 고르고 설정에도 저장한다. 목록에 없는 모델(예전 별칭·직접 넣은 이름)이면
        /// 설정에 넣고 목록을 다시 채운다(AI마다 설정에 저장된 모델은 목록에 더해 보임).
        /// </summary>
        private void SelectModel(string? model)
        {
            if (CurrentModel() == model) return;
            Provider.SetModel(_w._settings, model);
            int index = _modelChoices.ToList().FindIndex(c => c.Value == model);
            if (index < 0)
            {
                FillModels();
                return;
            }
            _fillingModels = true;
            try
            {
                Model.SelectedIndex = index;
            }
            finally
            {
                _fillingModels = false;
            }
        }

        /// <summary>저장한 줄 하나를 그린다.</summary>
        private void RenderSaved(ChatItem item)
        {
            switch (item.Kind)
            {
                case ChatItemKind.User:
                    var images = new List<Bitmap?>();
                    foreach (var data in item.Images ?? []) images.Add(data.Length > 0 ? DecodeImage(data) : null);
                    AddUserBubble(item, images, []);
                    break;
                case ChatItemKind.Assistant:
                    AddItem(_w.AnswerView(item.Text));
                    break;
                case ChatItemKind.Tool:
                    AddItem(ToolLine(item.Text, item.Error));
                    break;
                case ChatItemKind.Notice:
                case ChatItemKind.Error:
                    AddItem(SystemLine(item.Text, item.Kind == ChatItemKind.Error));
                    break;
                case ChatItemKind.Snapshot:
                    if (item.Images is [var png, ..] && DecodeImage(png) is { } shot)
                        AddItem(new Border { Margin = new Thickness(2, 2, 0, 0), Child = SnapshotView(shot) });
                    break;
                case ChatItemKind.AiDraw:
                    AddItem(_w.AiDrawCard(item.Images is [var drawn, ..] ? DecodeImage(drawn) : null, item.Text,
                        item.Job is { } job && _w._mcp.Tools.Jobs.Exists(job) ? job : null));
                    break;
                case ChatItemKind.Footer:
                    AddItem(FooterLine(item.Text));
                    break;
            }
        }

        /// <summary>지난 대화 목록을 다시 그린다: 안내, 즐겨찾기(위에 고정) → 최근에 고친 순.</summary>
        private void RefreshList()
        {
            _list.Children.Clear();
            List<ChatSummary> chats;
            try
            {
                chats = _w._store.List(Provider.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                chats = [];
            }
            var intro = WrapText(chats.Count == 0 ? UiStrings.Chat_History_Empty : UiStrings.Chat_History_StartHint, dim: true);
            intro.Margin = new Thickness(2, 2, 2, 6);
            _list.Children.Add(intro);
            if (chats.Count == 0) return;
            _list.Children.Add(new TextBlock
            {
                Text = UiStrings.Chat_History_Title,
                FontWeight = FontWeight.Bold,
                Foreground = AppTheme.TextBrush,
                Margin = new Thickness(2, 0, 0, 2),
            });
            foreach (var c in chats) _list.Children.Add(ListRow(c));
        }

        /// <summary>목록 한 줄: 즐겨찾기 별 · 제목(누르면 열기)과 날짜·모델 · 지우기.</summary>
        private Control ListRow(ChatSummary c)
        {
            var star = new Button
            {
                Content = new IconView(VectorIcon.Star, c.Favorite ? AppTheme.FavoriteBrush : AppTheme.DimTextBrush, 1.8) { Filled = c.Favorite, Width = 15, Height = 15 },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            }.Tip(c.Favorite ? UiStrings.Chat_History_Unfavorite : UiStrings.Chat_History_Favorite);
            star.Click += (_, _) =>
            {
                try
                {
                    _w._store.SetFavorite(Provider.Id, c.Id, !c.Favorite);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Ui.Error(_w, UiStrings.FormatChat_History_SaveFailed(message: ex.Message));
                }
                RefreshList();
            };
            var title = new TextBlock
            {
                Text = c.Title.Length > 0 ? c.Title : UiStrings.Chat_History_ImageTitle,
                Foreground = AppTheme.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var meta = new TextBlock
            {
                Text = UiStrings.FormatChat_History_Meta(date: When(c.UpdatedUtc), model: ModelLabel(c.Model)),
                Foreground = AppTheme.DimTextBrush,
                FontSize = AppTheme.SmallFontSize,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var open = new Button
            {
                Content = new StackPanel { Spacing = 1, Children = { title, meta } },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand),
            }.Tip(UiStrings.Chat_History_Open);
            open.Click += (_, _) => OpenSaved(c.Id);
            var delete = Ui.IconButton(VectorIcon.Trash, UiStrings.Chat_History_Delete, (_, _) => DeleteSaved(c), size: 13);
            delete.Background = Brushes.Transparent;
            delete.BorderThickness = new Thickness(0);
            delete.Padding = new Thickness(5);
            delete.VerticalAlignment = VerticalAlignment.Center;
            delete.Focusable = false;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(star);
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);
            Grid.SetColumn(delete, 2);
            grid.Children.Add(delete);
            return new Border
            {
                Background = AppTheme.BackBrush,
                BorderBrush = AppTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = grid,
            };
        }

        private void DeleteSaved(ChatSummary c)
        {
            var title = c.Title.Length > 0 ? c.Title : UiStrings.Chat_History_ImageTitle;
            if (Ui.Ask(_w, UiStrings.FormatChat_History_DeleteAsk(title: title), UiStrings.Chat_History_DeleteTitle, MessageButtons.YesNo, MessageIcon.Question)
                != DialogResult.Yes) return;
            _w._store.Delete(Provider.Id, c.Id);
            RefreshList();
        }

        /// <summary>목록의 날짜: 오늘이면 시각, 올해면 월·일, 아니면 짧은 날짜.</summary>
        private static string When(DateTime utc)
        {
            var local = utc.ToLocalTime();
            var now = DateTime.Now;
            var culture = CultureInfo.CurrentCulture;
            return local.Date == now.Date ? local.ToString("t", culture) : local.Year == now.Year ? local.ToString("M", culture) : local.ToString("d", culture);
        }

        /// <summary>모델 값(null = 기본값)의 목록 이름.</summary>
        private string ModelLabel(string? model)
            => _modelChoices.FirstOrDefault(m => m.Value == model)?.Label ?? model ?? _modelChoices.FirstOrDefault()?.Label ?? "";

        /// <summary>
        /// 입력란에 글을 넣고(보내지 않음) 입력란에 포커스. 이미 넣은 그림 칩은 앞에 그대로 둔다(정리 창이 그림을 붙인 뒤 부탁 글을 채움).
        /// 도움말의 예시 부탁과 "AI로 설명 붙이기"가 부른다.
        /// </summary>
        public void Prefill(string text)
        {
            SyncInlineImages();
            var current = _input.Text ?? "";
            var chips = string.Join(" ", ChatImageTokens.Find(current).Select(t => current.Substring(t.Start, t.Length)));
            var full = chips.Length > 0 ? chips + " " + text : text;
            SetInputText(full, full.Length);
            SyncInlineImages();
            FocusInput();
        }

        /// <summary>
        /// 그림을 첨부한다(보내지 않음). 픽셀 아트면 작은 그림을 정수 배 최근접으로 키워(긴 변 <see cref="CanvasAttachSide"/> 안팎) 보내고,
        /// 아니면 긴 변이 <see cref="AttachMaxSide"/>를 넘을 때 줄여 보낸다.
        /// </summary>
        public void AttachImage(PixelImage img, bool pixelArt)
        {
            var bmp = ImageUtil.ToBitmap(img);
            if (!pixelArt && Math.Max(img.Width, img.Height) > AttachMaxSide)
            {
                AddAttachment(ToPng(bmp), bmp, pixelArt);
                return;
            }
            int n = pixelArt ? Math.Max(1, CanvasAttachSide / Math.Max(img.Width, img.Height)) : 1;
            using var ms = new MemoryStream();
            PngIO.Save(n > 1 ? ScaleUp(img, n) : img, ms);
            AddAttachment(ms.ToArray(), bmp, pixelArt);
        }

        /// <summary>차례 중에 다른 프로젝트가 열리면(new_project 등) 묶음을 새 프로젝트로 옮긴다.</summary>
        public void OnProjectReplaced()
        {
            if (_turn is { } turn)
            {
                turn.Project?.History.EndGroup();
                turn.Project = null;
                if (_w._ctx.Project is { } p)
                {
                    p.History.BeginGroup(turn.Label);
                    turn.Project = p;
                }
            }
            RefreshSteps();
        }

        /// <summary>보내기/정지 단추. 다른 탭의 차례가 돌면 보내기 도움말로 알린다.</summary>
        public void UpdateInputState()
        {
            bool running = _turn is not null;
            _sendButton.IsVisible = !running;
            _stopButton.IsVisible = running;
            var other = _w.RunningTab;
            _sendButton.Tip(other is null || ReferenceEquals(other, this)
                ? "Enter"
                : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateInputState_25BD68A0C8,other.Provider.Name));
            RefreshTurnActions();
        }

        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (HandleChipKey(e)) return;
            if (e.Key == Key.Enter)
            {
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0) return; // 줄바꿈(TextBox가 처리)
                e.Handled = true;
                // 한글 조합 중이던 글자가 먼저 들어가도록 한 박자 늦게 보낸다.
                Dispatcher.UIThread.Post(Send, DispatcherPriority.Background);
            }
            else if (e.Key == Key.V && Ui.HasCommand(e.KeyModifiers))
            {
                _ = PasteImageAsync();
            }
        }

        // ───────────── 메시지 목록 ─────────────

        /// <summary>그림 생성 진행: 진행 줄을 그 글로(도구 실행 단계).</summary>
        public void OnAiDrawProgress(string text)
        {
            if (_turn is null) return;
            _aiDrawStatus = text;
            if (_phase != "run")
            {
                _phase = "run";
                _phaseTool = "generate_pixel_art";
                _phaseStart = DateTime.UtcNow;
            }
            UpdateProgress();
        }

        /// <summary>그림 생성 결과 칸(결과 그림·결과 글·"변환 설정 보기")을 붙이고 저장할 내용에 넣는다(그림이 너무 크면 그림은 남기지 않음).</summary>
        public void OnAiDrawCompleted(AiDrawOutcome o)
        {
            string? png = null;
            if (Math.Max(o.Preview.Width, o.Preview.Height) <= SavedAiDrawSide)
            {
                using var ms = new MemoryStream();
                PngIO.Save(o.Preview, ms);
                png = Convert.ToBase64String(ms.ToArray());
            }
            AddItem(_w.AiDrawCard(ImageUtil.ToBitmap(o.Preview), o.Message, o.Job.Id));
            Record(new ChatItem { Kind = ChatItemKind.AiDraw, Text = o.Message, Images = png is null ? null : [png], Job = o.Job.Id });
        }

        /// <summary>진행 단계를 바꾼다(받은 양·단계 시간은 0부터).</summary>
        private void SetPhase(string phase, string? tool)
        {
            _phase = phase;
            _phaseTool = tool;
            _phaseChars = 0;
            _phaseStart = DateTime.UtcNow;
            if (phase != "run") _aiDrawStatus = null;
            UpdateProgress();
        }

        /// <summary>
        /// "생각 중…" 줄: 지금 하는 일(응답 기다림·생각·도구 호출 작성(받은 양)·도구 실행·답 쓰기) + 그 단계 시간 + 전체 시간.
        /// 큰 그림을 그리는 도구 호출은 작성에 오래 걸리므로 받은 양이 늘어나는 것으로 살아 있음을 보인다.
        /// </summary>
        private void UpdateProgress()
        {
            if (_turn is null) return;
            var now = DateTime.UtcNow;
            static string Time(TimeSpan t) => t.TotalMinutes >= 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateProgress_A70F7D76B8,(int)t.TotalMinutes,t.Seconds) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateProgress_ACBE1DF02D,t.Seconds);
            string Size(int chars) => chars >= 1024 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateProgress_60CB2685BB,chars / 1024.0) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateProgress_97F1F78A01,chars);
            string what = _phase switch
            {
                "thinking" => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateProgress_B750FB67DF,Size(_phaseChars)),
                "tool_use" => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateProgress_4C23FA2A5B,_phaseTool ?? "?",Size(_phaseChars)),
                "run" when _aiDrawStatus is { } drawing => drawing,
                "run" => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateProgress_07B7C8BA45,_phaseTool ?? "?"),
                "text" => UiStrings.S_ChatWindow_UpdateProgress_5CDDDE0C69,
                _ => UiStrings.S_ChatWindow_UpdateProgress_46A1800CB1,
            };
            _thinking.Text = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_UpdateProgress_A4C73F4DE5,what,Time(now - _phaseStart),Time(now - _turnStart));
        }

        /// <summary>메시지 목록 끝("생각 중…" 앞)에 붙인다.</summary>
        private void AddItem(Control c)
        {
            int idx = _messages.Children.IndexOf(_thinking);
            if (idx >= 0) _messages.Children.Insert(idx, c);
            else _messages.Children.Add(c);
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            {
                if (_follow) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
            }
            else if (e.OffsetDelta.Y != 0)
            {
                _follow = _scroll.Offset.Y + _scroll.Viewport.Height >= _scroll.Extent.Height - 24;
            }
        }

        /// <summary>
        /// 사용자 메시지 말풍선. 글에 그림 칩이 있으면 글 사이 그 자리에 칩(작은 미리보기 + 번호)을 넣고, 칩이 없는 예전 대화는 미리보기 줄,
        /// (미리보기를 남기지 못한 그림이 있으면) 그림 표시, 글 순서. 아래에 복사 단추는 항상 보이고 수정·삭제는 마지막 메시지에만 보인다.
        /// <paramref name="images"/> = 첨부 순서(칩 번호 − 1)의 미리보기(없으면 null), <paramref name="attachments"/> = 수정할 때 입력란에 되돌릴 첨부.
        /// 글은 AI 답처럼 끌어서 골라 Ctrl+C로 복사한다. 말풍선 안쪽 여백은 글 칸의 여백이라, 글자 바로 옆 여백에서 끌기 시작해도 골라진다.
        /// </summary>
        private void AddUserBubble(ChatItem item, IReadOnlyList<Bitmap?> images, IReadOnlyList<Attachment> attachments)
        {
            var stack = new StackPanel();
            var segments = ChatImageTokens.Split(item.Text);
            if (segments.Any(s => s.IsImage))
            {
                var chips = new List<(Control Chip, Bitmap Image)>();
                var text = new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = AppTheme.TextBrush,
                    Padding = new Thickness(8, 6),
                    Background = Brushes.Transparent,
                };
                foreach (var s in segments)
                {
                    if (!s.IsImage) text.Inlines!.Add(new Run(s.Text));
                    else
                    {
                        var thumb = s.Image <= images.Count ? images[s.Image - 1] : null;
                        var chip = _w.BubbleChip(s.Image, thumb);
                        text.Inlines!.Add(new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center });
                        if (thumb is not null) chips.Add((chip, thumb));
                    }
                }
                RouteBubbleChipEvents(text, chips);
                stack.Children.Add(text);
            }
            else
            {
                var thumbs = images.OfType<Bitmap>().Select(b => Thumb(b, pixelArt: true, 72)).ToList();
                if (thumbs.Count > 0)
                {
                    var wrap = new WrapPanel { Margin = new Thickness(8, 6, 8, 0) };
                    foreach (var t in thumbs) wrap.Children.Add(t);
                    stack.Children.Add(wrap);
                }
                int missing = item.ImageCount - thumbs.Count;
                if (missing > 0)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = UiStrings.FormatChat_History_ImagesMarker(count: missing),
                        Foreground = AppTheme.DimTextBrush,
                        FontSize = AppTheme.SmallFontSize,
                        Margin = new Thickness(8, stack.Children.Count > 0 ? 4 : 6, 8, 0),
                    });
                }
                if (item.Text.Length > 0)
                {
                    stack.Children.Add(new SelectableTextBlock
                    {
                        Text = item.Text,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = AppTheme.TextBrush,
                        Padding = new Thickness(8, stack.Children.Count > 0 ? 4 : 6, 8, 6),
                        // 투명 바탕이 있어야 글자 사이·여백에서도 눌린다(바탕이 없으면 글자 위에서만 끌기가 시작됨).
                        Background = Brushes.Transparent,
                    });
                }
                else if (stack.Children.Count > 0)
                {
                    var last = stack.Children[^1];
                    last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 6);
                }
            }
            var bubble = new Border
            {
                Background = UserBubbleBrush,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = stack,
            };
            var turnActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                IsVisible = false,
                Children =
                {
                    TurnButton(VectorIcon.Pencil, UiStrings.Chat_Turn_Edit_Tip, () => RemoveLastTurn(edit: true)),
                    TurnButton(VectorIcon.Trash, UiStrings.Chat_Turn_Delete_Tip, () => RemoveLastTurn(edit: false)),
                },
            };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children =
                {
                    TurnButton(VectorIcon.Copy, UiStrings.Chat_Turn_Copy_Tip, () => _w.CopyText(ChatImageTokens.WithoutTokens(item.Text))),
                    turnActions,
                },
            };
            var row = new StackPanel { Margin = new Thickness(40, 4, 0, 0), Children = { bubble, actions } };
            AddItem(row);
            _userTurns.Add(new UserTurn(row, item, turnActions, attachments));
            RefreshTurnActions();
        }

        /// <summary>말풍선 아래 작은 아이콘 단추(테 없음, 흐린 색).</summary>
        private static Button TurnButton(VectorIcon icon, string tip, Action click)
        {
            var b = Ui.IconButton(icon, tip, (_, _) => click(), size: 13);
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Foreground = AppTheme.DimTextBrush;
            b.Padding = new Thickness(5, 2);
            b.MinHeight = 0;
            b.Focusable = false;
            return b;
        }

        /// <summary>수정·삭제 단추: 마지막 메시지에만 보이고, 어느 탭이든 차례가 도는 동안은 꺼진다(실행취소 묶음이 열려 있음).</summary>
        private void RefreshTurnActions()
        {
            bool idle = _w.RunningTab is null;
            for (int i = 0; i < _userTurns.Count; i++)
            {
                var actions = _userTurns[i].Actions;
                actions.IsVisible = i == _userTurns.Count - 1;
                actions.IsEnabled = idle;
            }
        }

        /// <summary>
        /// 마지막 메시지 삭제(<paramref name="edit"/>면 수정): 그 메시지부터 끝까지(답·도구 줄·캔버스 모습·끝 줄)를 화면과 저장한 대화에서 뺀다.
        /// 그 차례가 바꾼 그림을 아직 되돌릴 수 있으면 함께 되돌릴지 묻는다(취소하면 아무것도 하지 않음).
        /// CLI 세션 기록에서는 차례를 뺄 수 없으므로 세션을 버리고, 다음 메시지는 새 세션 + 남은 대화 정리로 보낸다(<see cref="SavedChat.NeedsRecap"/>).
        /// 차례가 하나도 남지 않으면 저장한 대화 파일을 지운다(다음 메시지가 같은 대화로 다시 저장). 수정은 지운 글(과 이번 실행 중 보낸 첨부)을 입력란에 다시 넣는다.
        /// </summary>
        private void RemoveLastTurn(bool edit)
        {
            if (_turn is not null || _w.RunningTab is not null || _userTurns.Count == 0) return;
            var last = _userTurns[^1];
            if (last.Step is { } step && ReferenceEquals(_w._ctx.Project, step.Project) && step.Project.History.UndoDepthOf(step.Command) > 0)
            {
                var answer = Ui.Ask(_w, UiStrings.Chat_Turn_UndoAsk, edit ? UiStrings.Chat_Turn_Edit_Title : UiStrings.Chat_Turn_Delete_Title,
                    MessageButtons.YesNoCancel, MessageIcon.Question, okText: UiStrings.Chat_Turn_UndoAsk_Undo, noText: UiStrings.Chat_Turn_UndoAsk_Keep);
                if (answer is not (DialogResult.Yes or DialogResult.No)) return;
                if (answer == DialogResult.Yes && !UndoTurn(step)) return;
            }

            int at = _messages.Children.IndexOf(last.Row);
            if (at >= 0) _messages.Children.RemoveRange(at, _messages.Children.Count - at);
            _userTurns.RemoveAt(_userTurns.Count - 1);
            if (last.Step is { } removed) _steps.Remove(removed);
            ChatRecap.RemoveLastTurn(_items);
            _session?.Reset();
            bool hasTurns = ChatRecap.HasTurns(_items);
            if (_chat is { } chat)
            {
                chat.SessionId = null;
                chat.SessionDirectory = null;
                chat.NeedsRecap = hasTurns;
                if (hasTurns) SaveChat();
                else
                {
                    chat.Items = _items;
                    _w._store.Delete(Provider.Id, chat.Id);
                }
            }
            if (hasTurns) AddSystemLine(UiStrings.Chat_Turn_Removed, error: false, record: false);
            RefreshTurnActions();

            if (edit) RestoreInput(last.Item.Text, last.Attachments);
            FocusInput();
        }

        /// <summary>안내·오류 한 줄을 붙이고, <paramref name="record"/>면 저장할 내용에도 넣는다.</summary>
        private void AddSystemLine(string text, bool error, bool record = true)
        {
            AddItem(SystemLine(text, error));
            if (record) Record(new ChatItem { Kind = error ? ChatItemKind.Error : ChatItemKind.Notice, Text = text });
        }

        private static SelectableTextBlock SystemLine(string text, bool error) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = error ? ErrorBrush : AppTheme.DimTextBrush,
            FontSize = AppTheme.SmallFontSize,
            Margin = new Thickness(2, 0),
        };

        /// <summary>AI가 부른 도구 한 줄(실패면 오류 색).</summary>
        private static TextBlock ToolLine(string text, bool error) => new()
        {
            Text = text,
            Foreground = error ? ErrorBrush : AppTheme.DimTextBrush,
            FontSize = AppTheme.SmallFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0),
        };

        /// <summary>차례 끝 비용·시간 줄.</summary>
        private static TextBlock FooterLine(string text) => new()
        {
            Text = text,
            Foreground = AppTheme.DimTextBrush,
            FontSize = AppTheme.SmallFontSize - 1,
            Margin = new Thickness(2, 0, 0, 6),
        };

        /// <summary>스트리밍 답 글 블록을 새로 연다(앞 블록은 그대로 둠).</summary>
        private void BeginText(Turn t)
        {
            FinishStream(t);
            var host = _w.AnswerView();
            t.StreamHost = host;
            AddItem(host);
        }

        /// <summary>
        /// 스트리밍 글을 바로 그리고, 그 뒤 100ms 안에 더 온 글은 한 번에 모아 그린다(조각마다 마크다운을 다시 읽지 않게).
        /// 바뀌지 않은 앞쪽 블록은 <see cref="MarkdownView.SetText"/>가 그대로 둔다.
        /// </summary>
        private static void ScheduleStreamRender(Turn t)
        {
            if (t.RenderPending) return;
            t.StreamHost?.SetText(t.Stream.ToString());
            t.RenderPending = true;
            DispatcherTimer.RunOnce(() =>
            {
                t.RenderPending = false;
                t.StreamHost?.SetText(t.Stream.ToString());
            }, TimeSpan.FromMilliseconds(100));
        }

        /// <summary>스트리밍 중이던 글을 마무리한다(마지막 글까지 그림, 저장할 내용에 넣음).</summary>
        private void FinishStream(Turn t)
        {
            if (t.StreamHost is { } host)
            {
                var text = t.Stream.ToString();
                if (text.Length > 0)
                {
                    host.SetText(text);
                    Record(new ChatItem { Kind = ChatItemKind.Assistant, Text = text });
                }
            }
            t.StreamHost = null;
            t.Stream.Clear();
        }

        // ───────────── 실행취소 단계 ─────────────

        /// <summary>차례가 끝난 뒤 캔버스 모습과 "이 단계 되돌리기/다시 적용" 단추. 모습은 작은 그림이면 저장할 내용에도 넣는다(단추는 저장하지 않음).</summary>
        private void AddSnapshot(SpriteProject project, IUndoCommand group)
        {
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(2, 2, 0, 0) };
            if (_w.Composite(project) is { } img)
            {
                panel.Children.Add(SnapshotView(ImageUtil.ToBitmap(img)));
                if (Math.Max(img.Width, img.Height) <= SavedSnapshotSide)
                {
                    using var ms = new MemoryStream();
                    PngIO.Save(img, ms);
                    Record(new ChatItem { Kind = ChatItemKind.Snapshot, Images = [Convert.ToBase64String(ms.ToArray())] });
                }
            }
            var button = Ui.Button(UiStrings.S_ChatWindow_AddSnapshot_C5F0084B6D);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            var step = new UndoStep(project, group, button);
            button.Click += (_, _) => ToggleStep(step);
            _steps.Add(step);
            if (_userTurns.Count > 0) _userTurns[^1].Step = step;
            panel.Children.Add(button);
            AddItem(panel);
            RefreshStep(step);
        }

        private void ToggleStep(UndoStep step)
        {
            // 어느 탭이든 차례가 돌면 실행취소 묶음이 열려 있으므로 건드리지 않는다.
            if (_w.RunningTab is not null || !ReferenceEquals(_w._ctx.Project, step.Project)) return;
            var history = step.Project.History;
            if (history.UndoDepthOf(step.Command) > 0)
            {
                if (!UndoTurn(step)) return;
            }
            else if (history.RedoDepthOf(step.Command) == 1) _w._ctx.RedoStep();
            RefreshSteps();
        }

        /// <summary>차례의 실행취소 묶음을 되돌린다. 그 뒤에 다른 편집이 있으면 함께 되돌릴지 묻고, 거기서 취소하면 false.</summary>
        private bool UndoTurn(UndoStep step)
        {
            int depth = step.Project.History.UndoDepthOf(step.Command);
            if (depth > 1 && Ui.Ask(_w, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_ToggleStep_3758F11139,depth - 1), UiStrings.S_ChatWindow_ToggleStep_579568A52F, MessageButtons.OKCancel, MessageIcon.Question)
                != DialogResult.OK) return false;
            for (int i = 0; i < depth; i++)
            {
                if (!_w._ctx.UndoStep()) break;
            }
            return true;
        }

        public void RefreshSteps()
        {
            foreach (var s in _steps) RefreshStep(s);
        }

        private void RefreshStep(UndoStep step)
        {
            bool same = ReferenceEquals(_w._ctx.Project, step.Project);
            bool idle = _w.RunningTab is null;
            var history = step.Project.History;
            int undo = same ? history.UndoDepthOf(step.Command) : -1;
            int redo = same ? history.RedoDepthOf(step.Command) : -1;
            var b = step.Button;
            if (undo > 0)
            {
                b.Content = UiStrings.S_ChatWindow_RefreshStep_B7986869B0;
                b.IsEnabled = idle;
                b.Tip(undo > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ChatWindow_RefreshStep_8F1EF61EAD,undo - 1) : null);
            }
            else if (redo > 0)
            {
                b.Content = UiStrings.S_ChatWindow_RefreshStep_E659C76CB0;
                b.IsEnabled = idle && redo == 1;
                b.Tip(redo > 1 ? UiStrings.S_ChatWindow_RefreshStep_B9AA5F8B5A : null);
            }
            else
            {
                b.Content = UiStrings.S_ChatWindow_RefreshStep_B7986869B0;
                b.IsEnabled = false;
                b.Tip(same ? UiStrings.S_ChatWindow_RefreshStep_1CA90CA5FE : UiStrings.S_ChatWindow_RefreshStep_842B2F53BC);
            }
        }

        // ───────────── 첨부 ─────────────

        private void AttachFile()
        {
            var path = Ui.OpenFile(_w, UiStrings.S_ChatWindow_AttachFile_A024695555, null, new FileFilter(UiStrings.S_ChatWindow_AttachFile_1565F6E888, "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"));
            if (path is not null) AttachPath(path);
        }

        private void AttachCanvas()
        {
            if (_w._ctx.Project is not { } p || _w.Composite(p) is not { } img)
            {
                Ui.Warn(_w, UiStrings.S_ChatWindow_AttachCanvas_F7C294FC0C);
                return;
            }
            AttachImage(img, pixelArt: true);
        }

        /// <summary>⌘/Ctrl+V: 클립보드에 그림(또는 복사한 그림 파일)이 있으면 커서 자리에 칩으로 넣는다(글자 붙여넣기는 입력란이 그대로 함).</summary>
        private async Task PasteImageAsync()
        {
            if (_w.Clipboard is not { } clipboard) return;
            Bitmap? bmp;
            List<string> files = [];
            try
            {
                bmp = await clipboard.TryGetBitmapAsync();
                if (bmp is null && await clipboard.TryGetFilesAsync() is { } items)
                    files = [.. items.Select(f => f.TryGetLocalPath()).OfType<string>().Where(IsAttachable)];
            }
            catch (Exception)
            {
                // 플랫폼 클립보드가 그림 형식을 못 주면 무시(글자 붙여넣기만).
                return;
            }
            if (_w._shutdown) return;
            if (bmp is not null) AddAttachment(ToPng(bmp), bmp, pixelArt: false);
            foreach (var f in files) AttachPath(f);
        }
    }
}
