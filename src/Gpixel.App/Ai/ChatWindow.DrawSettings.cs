using Gpixel.Core.AiDraw;
using Gpixel.App.Localization;

namespace Gpixel.App.Ai;

// 그리기 설정 칸(머리줄 "?" 옆 슬라이더 단추): 도움말 칸과 같은 오른쪽 자리·움직임을 쓰고, 둘 중 하나만 열린다.
// 그리는 방식(그림 생성 → 픽셀 변환 / 직접 찍기 / 직접 찍기 + 자동 체크)과 고른 방식의 하위 옵션(그림 생성: 결과 크기·배경 지우기·자동 최적화·넣는 곳·스타일 참고·애니메이션,
// 직접 찍기: 팔레트 색만 사용)을 고친다. 선택지마다 한 줄 이름과 흐린 정보 아이콘만 보이고 설명은 마우스를 올리면 풍선으로 뜬다.
// 그림 생성은 Codex만 하므로 Claude 탭을 보고 있으면 생성 선택지 옆에 빨간 경고 아이콘(마우스를 올리면 까닭)이 보인다.
public sealed partial class ChatWindow
{
    /// <summary>결과 크기 칸의 고정 값(0 = 자동, 마지막 칸은 직접 입력).</summary>
    private static readonly int[] SizeChoices = [32, 48, 64];

    private RadioButton? _modeGenerate, _modeDirect, _modeAutoCheck;
    private Border? _modeWarning;
    private TextBlock? _codexStatus;
    private Control? _generateOptions;
    private ComboBox? _sizeCombo, _styleCombo, _styleRefCombo, _insertCombo, _animCombo;
    /// <summary>"프레임마다 따로"를 고르면 보이는 일관성 경고 아이콘.</summary>
    private Control? _animWarning;
    private NumericUpDown? _sizeCustom;
    private CheckBox? _removeBackground, _autoOptimize, _paletteOnly, _resizeResult;
    /// <summary>결과 크기 칸(결과 크기 보정이 켜졌을 때만 쓸 수 있음).</summary>
    private Control? _sizeRow;
    /// <summary>팔레트 색만 사용 줄(직접 찍기를 골랐을 때만 보임).</summary>
    private Control? _paletteRow;
    private readonly Dictionary<string, NumericUpDown> _frameBoxes = new();
    /// <summary>스타일 참고 "지정" 목록의 자료 id(목록 순서).</summary>
    private List<string> _styleRefIds = [];
    /// <summary>설정 칸 값을 채우는 중(바뀜 이벤트로 설정을 고치지 않게).</summary>
    private bool _drawFilling;
    private bool _codexChecking;

    /// <summary>그림 생성 진행·결과는 차례가 도는 탭에 붙인다(차례가 없으면 버림 — 멈춘 뒤 늦게 온 것).</summary>
    private void OnAiDrawProgress(string text) => RunningTab?.OnAiDrawProgress(text);

    private void OnAiDrawCompleted(AiDrawOutcome outcome) => RunningTab?.OnAiDrawCompleted(outcome);

    /// <summary>도움말·설정 단추가 켜지고 꺼짐: 하나를 켜면 다른 하나를 끄고 칸 내용만 바꾼다(열려 있으면 그대로, 닫혀 있으면 연다). 켜진 단추를 끄면 닫는다.</summary>
    private void OnSidePanelToggled(ToggleButton source)
    {
        if (_panelSyncing) return;
        var other = ReferenceEquals(source, _help) ? _drawSettings : _help;
        if (source.IsChecked == true)
        {
            if (other.IsChecked == true)
            {
                _panelSyncing = true;
                other.IsChecked = false;
                _panelSyncing = false;
            }
            bool help = ReferenceEquals(source, _help);
            _helpPanel.IsVisible = help;
            _drawPanel.IsVisible = !help;
            if (!help) RefreshDrawSettings(checkCodex: true);
            AnimateHelp(true);
        }
        else if (other.IsChecked != true) AnimateHelp(false);
        UpdateDrawSettingsButton();
    }

    /// <summary>설정 칸이 열려 있는 동안은 빨간 X(정리 창의 설정 단추처럼), 닫히면 슬라이더 아이콘.</summary>
    private void UpdateDrawSettingsButton()
    {
        bool open = _drawSettings.IsChecked == true;
        _drawSettingsIcon.Icon = open ? VectorIcon.X : VectorIcon.SlidersHorizontal;
        if (open) _drawSettingsIcon.Foreground = AppTheme.UnsavedBrush;
        else _drawSettingsIcon.ClearValue(IconView.ForegroundProperty);
        _drawSettings.Tip(open ? UiStrings.AiDraw_Settings_Close : UiStrings.AiDraw_Settings_Tip);
    }

    /// <summary>설정 칸: 머리줄(제목 + 닫기) 아래 내용 스크롤(도움말 칸과 같은 너비).</summary>
    private Control BuildDrawSettingsPanel()
    {
        var title = new TextBlock
        {
            Text = UiStrings.AiDraw_Settings_Title,
            FontWeight = FontWeight.Bold,
            Foreground = AppTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0),
        };
        var close = Ui.IconButton(VectorIcon.X, UiStrings.AiDraw_Settings_Close, (_, _) => _drawSettings.IsChecked = false, 28, 12);
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

        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(12, 10) };
        stack.Children.Add(TitleText(UiStrings.AiDraw_Mode_Title));
        _modeGenerate = new RadioButton { Content = UiStrings.AiDraw_Mode_Generate, GroupName = "aidraw-mode", Foreground = AppTheme.TextBrush };
        _modeDirect = new RadioButton { Content = UiStrings.AiDraw_Mode_Direct, GroupName = "aidraw-mode", Foreground = AppTheme.TextBrush };
        _modeAutoCheck = new RadioButton { Content = UiStrings.AiDraw_Mode_AutoCheck, GroupName = "aidraw-mode", Foreground = AppTheme.TextBrush };
        _modeWarning = AlertIcon(UiStrings.AiDraw_Mode_ClaudeWarning);
        _modeGenerate.IsCheckedChanged += (_, _) => OnDrawModeChanged();
        _modeDirect.IsCheckedChanged += (_, _) => OnDrawModeChanged();
        _modeAutoCheck.IsCheckedChanged += (_, _) => OnDrawModeChanged();
        stack.Children.Add(WithInfo(_modeGenerate, UiStrings.AiDraw_Mode_Generate_Text, _modeWarning));
        stack.Children.Add(WithInfo(_modeDirect, UiStrings.AiDraw_Mode_Direct_Text));
        stack.Children.Add(WithInfo(_modeAutoCheck, UiStrings.AiDraw_Mode_AutoCheck_Text));
        // 직접 찍기를 골랐을 때만: 팔레트 색만 사용.
        _paletteOnly = Ui.Check(UiStrings.AiDraw_PaletteOnly);
        _paletteOnly.IsCheckedChanged += (_, _) => OnDrawOptionChanged();
        _paletteRow = WithInfo(_paletteOnly, UiStrings.AiDraw_PaletteOnly_Text);
        _paletteRow.Margin = new Thickness(22, 0, 0, 0);
        stack.Children.Add(_paletteRow);
        _codexStatus = WrapText("", dim: true);
        stack.Children.Add(_codexStatus);

        // 그림 생성을 골랐을 때만: 생성 옵션.
        var options = new StackPanel { Spacing = 6 };
        options.Children.Add(new Border { Height = 1, Background = AppTheme.BorderBrush, Margin = new Thickness(0, 4) });
        options.Children.Add(TitleText(UiStrings.AiDraw_Options_Title));

        _resizeResult = Ui.Check(UiStrings.AiDraw_ResizeResult);
        _resizeResult.IsCheckedChanged += (_, _) => OnDrawOptionChanged();
        options.Children.Add(WithInfo(_resizeResult, UiStrings.AiDraw_ResizeResult_Text));
        _sizeCombo = Ui.Combo(120, "32", "48", "64", UiStrings.AiDraw_Size_Custom);
        _sizeCustom = Ui.Num(AiDrawSettings.MinSize, AiDrawSettings.MaxSize, 32, 64);
        _sizeCombo.SelectionChanged += (_, _) => OnDrawOptionChanged();
        _sizeCustom.ValueChanged += (_, _) => OnDrawOptionChanged();
        _sizeRow = OptionRow(UiStrings.AiDraw_Size, UiStrings.AiDraw_Size_Tip, Ui.Row(_sizeCombo, _sizeCustom));
        _sizeRow.Margin = new Thickness(22, 0, 0, 0);
        options.Children.Add(_sizeRow);

        _removeBackground = Ui.Check(UiStrings.AiDraw_RemoveBackground);
        _removeBackground.IsCheckedChanged += (_, _) => OnDrawOptionChanged();
        options.Children.Add(WithInfo(_removeBackground, UiStrings.AiDraw_RemoveBackground_Tip));
        _autoOptimize = Ui.Check(UiStrings.AiDraw_AutoOptimize);
        _autoOptimize.IsCheckedChanged += (_, _) => OnDrawOptionChanged();
        options.Children.Add(WithInfo(_autoOptimize, UiStrings.AiDraw_AutoOptimize_Tip));

        _insertCombo = Ui.Combo(150, UiStrings.AiDraw_Insert_Layer, UiStrings.AiDraw_Insert_Document);
        _insertCombo.SelectionChanged += (_, _) => OnDrawOptionChanged();
        options.Children.Add(OptionRow(UiStrings.AiDraw_Insert, null, _insertCombo));

        _styleCombo = Ui.Combo(150, UiStrings.AiDraw_Style_Auto, UiStrings.AiDraw_Style_None, UiStrings.AiDraw_Style_Specific);
        _styleCombo.SelectionChanged += (_, _) => OnDrawOptionChanged();
        options.Children.Add(OptionRow(UiStrings.AiDraw_Style, UiStrings.AiDraw_Style_Text, _styleCombo));
        _styleRefCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(3) };
        _styleRefCombo.SelectionChanged += (_, _) => OnDrawOptionChanged();
        options.Children.Add(_styleRefCombo);

        var framesTitle = new TextBlock { Text = UiStrings.AiDraw_Frames, FontWeight = FontWeight.Bold, Foreground = AppTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center };
        var framesHeader = WithInfo(framesTitle, UiStrings.AiDraw_Frames_Text);
        framesHeader.Margin = new Thickness(0, 4, 0, 0);
        options.Children.Add(framesHeader);
        _animCombo = Ui.Combo(170, UiStrings.AiDraw_Anim_Sheet, UiStrings.AiDraw_Anim_PerFrame);
        _animCombo.SelectionChanged += (_, _) => OnDrawOptionChanged();
        // "프레임마다 따로"를 고르면 콤보 옆에 빨간 경고 아이콘(마우스를 올리면 까닭).
        _animWarning = AlertIcon(UiStrings.AiDraw_Anim_PerFrame_Warning);
        options.Children.Add(OptionRow(UiStrings.AiDraw_Anim, UiStrings.AiDraw_Anim_Tip, Ui.Row(_animCombo, _animWarning)));
        var frames = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*,Auto") };
        int row = 0, col = 0;
        foreach (var action in AiDrawActions.All)
        {
            if (col == 0) frames.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = ActionName(action), Foreground = AppTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            var box = Ui.Num(AiDrawSettings.MinFrames, AiDrawSettings.MaxFrames, AiDrawActions.DefaultFrames(action), 56);
            box.ValueChanged += (_, _) => OnDrawOptionChanged();
            _frameBoxes[action] = box;
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col * 2);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col * 2 + 1);
            frames.Children.Add(label);
            frames.Children.Add(box);
            if (++col == 2) { col = 0; row++; }
        }
        options.Children.Add(frames);
        _generateOptions = options;
        stack.Children.Add(options);

        var scroll = new ScrollViewer
        {
            Background = AppTheme.PanelBrush,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack,
        };
        return new DockPanel
        {
            Width = HelpPanelWidth - 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
            Children = { header, scroll },
        };
    }

    /// <summary>
    /// 선택지 한 줄: 컨트롤, 덧붙일 것(경고 아이콘 등), 흐린 정보 아이콘. 설명(<paramref name="tip"/>)은 정보 아이콘과 컨트롤 모두의 풍선 — 패널에는 한 줄 이름만 보인다.
    /// </summary>
    private static StackPanel WithInfo(Control control, string tip, params Control[] extra)
    {
        control.Tip(tip);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(control);
        row.Children.Add(InfoIcon(tip));
        foreach (var c in extra) row.Children.Add(c);
        return row;
    }

    /// <summary>설명 풍선을 띄우는 흐린 정보 아이콘(Lucide info).</summary>
    private static Border InfoIcon(string tip) => new Border
    {
        Width = 18,
        Height = 18,
        Margin = new Thickness(4, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Help),
        Background = Brushes.Transparent,
        Child = new IconView(VectorIcon.Info, AppTheme.DimTextBrush, 2) { Width = 14, Height = 14 },
    }.Tip(tip);

    /// <summary>경고 풍선을 띄우는 빨간 경고 아이콘(Lucide circle-alert).</summary>
    private static Border AlertIcon(string tip) => new Border
    {
        Width = 18,
        Height = 18,
        Margin = new Thickness(4, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Help),
        Background = Brushes.Transparent,
        Child = new IconView(VectorIcon.CircleAlert, AppTheme.UnsavedBrush, 2.2) { Width = 16, Height = 16 },
    }.Tip(tip);

    /// <summary>이름 + 컨트롤 한 줄. 설명이 있으면 이름 옆에 정보 아이콘, 이름에도 같은 풍선.</summary>
    private static Control OptionRow(string name, string? tip, Control control)
    {
        var label = new TextBlock { Text = name, Foreground = AppTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center };
        var head = tip is null ? (Control)label : WithInfo(label, tip);
        head.MinWidth = 100;
        head.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { head, control } };
    }

    private static string ActionName(string action) => action switch
    {
        AiDrawActions.Idle => UiStrings.AiDraw_Action_Idle,
        AiDrawActions.Walk => UiStrings.AiDraw_Action_Walk,
        AiDrawActions.Run => UiStrings.AiDraw_Action_Run,
        AiDrawActions.Attack => UiStrings.AiDraw_Action_Attack,
        AiDrawActions.Jump => UiStrings.AiDraw_Action_Jump,
        AiDrawActions.Hurt => UiStrings.AiDraw_Action_Hurt,
        AiDrawActions.Death => UiStrings.AiDraw_Action_Death,
        _ => action,
    };

    /// <summary>
    /// 설정 칸을 설정 값과 보이는 탭에 맞춘다(Claude 탭이면 생성 선택지 옆 경고). <paramref name="checkCodex"/>면 Codex 설치·로그인 상태를 다시 본다.
    /// </summary>
    private void RefreshDrawSettings(bool checkCodex = false)
    {
        if (_modeGenerate is null || _drawPanel?.IsVisible != true) return;
        var o = _settings.AiDraw;
        _drawFilling = true;
        try
        {
            _modeGenerate.IsChecked = o.Mode == AiDrawMode.Generate;
            _modeDirect!.IsChecked = o.Mode == AiDrawMode.Direct;
            _modeAutoCheck!.IsChecked = o.Mode == AiDrawMode.DirectAutoCheck;
            _modeWarning!.IsVisible = _visible.Provider.Id != AiDrawSettings.GeneratingProvider;
            int size = o.SizeTargetClamped;
            int index = Array.IndexOf(SizeChoices, size);
            _sizeCombo!.SelectedIndex = index >= 0 ? index : SizeChoices.Length;
            _sizeCustom!.Value = size;
            _resizeResult!.IsChecked = o.ResizeResult;
            _removeBackground!.IsChecked = o.RemoveBackground;
            _paletteOnly!.IsChecked = o.PaletteOnly;
            _autoOptimize!.IsChecked = o.AutoOptimize;
            _insertCombo!.SelectedIndex = o.Insert == AiDrawInsert.NewDocument ? 1 : 0;
            _styleCombo!.SelectedIndex = (int)o.StyleReference;
            _animCombo!.SelectedIndex = o.AnimationMethod == AiDrawAnimationMethod.PerFrame ? 1 : 0;
            FillStyleReferences(o.StyleReferenceId);
            foreach (var (action, box) in _frameBoxes) box.Value = o.FramesFor(action);
        }
        finally
        {
            _drawFilling = false;
        }
        UpdateDrawOptionsEnabled();
        if (checkCodex) _ = CheckCodexAsync();
    }

    /// <summary>"지정" 목록: 참고 자료 보관소의 그림(사용자 자료 다음 기본 예시). 보관소를 열 수 없으면 비움.</summary>
    private void FillStyleReferences(string? selectedId)
    {
        _styleRefCombo!.Items.Clear();
        _styleRefIds = [];
        try
        {
            foreach (var item in _mcp.Tools.Host.References.AllItems.Where(i => i.IsImage))
            {
                _styleRefIds.Add(item.Id);
                _styleRefCombo.Items.Add($"{(item.Title.Length > 0 ? item.Title : item.Id)} ({item.Width}×{item.Height})");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 보관소를 열 수 없으면 "지정"은 고를 것이 없다.
        }
        _styleRefCombo.SelectedIndex = selectedId is null ? -1 : _styleRefIds.IndexOf(selectedId);
    }

    private void UpdateDrawOptionsEnabled()
    {
        if (_generateOptions is null) return;
        // 고른 방식의 하위 옵션만 보인다: 그림 생성 → 생성 옵션, 직접 찍기(자동 체크 포함) → 팔레트 색만 사용.
        bool generate = _settings.AiDraw.Mode == AiDrawMode.Generate;
        _generateOptions.IsVisible = generate;
        _paletteRow!.IsVisible = !generate;
        _sizeCustom!.IsVisible = _sizeCombo!.SelectedIndex == SizeChoices.Length;
        _sizeRow!.IsEnabled = _resizeResult!.On();
        _styleRefCombo!.IsVisible = _styleCombo!.SelectedIndex == (int)AiDrawStyleReference.Specific;
        _animWarning!.IsVisible = _animCombo!.SelectedIndex == 1;
    }

    private void OnDrawModeChanged()
    {
        if (_drawFilling || _modeGenerate is null) return;
        if (_modeGenerate.IsChecked == true) _settings.AiDraw.Mode = AiDrawMode.Generate;
        else if (_modeAutoCheck!.IsChecked == true) _settings.AiDraw.Mode = AiDrawMode.DirectAutoCheck;
        else if (_modeDirect!.IsChecked == true) _settings.AiDraw.Mode = AiDrawMode.Direct;
        else return;
        UpdateDrawOptionsEnabled();
    }

    private void OnDrawOptionChanged()
    {
        if (_drawFilling || _sizeCombo is null) return;
        var o = _settings.AiDraw;
        int si = _sizeCombo.SelectedIndex;
        o.SizeTarget = si >= 0 && si < SizeChoices.Length ? SizeChoices[si] : (int)_sizeCustom!.NumValue();
        o.ResizeResult = _resizeResult!.On();
        o.RemoveBackground = _removeBackground!.On();
        o.PaletteOnly = _paletteOnly!.On();
        o.AutoOptimize = _autoOptimize!.On();
        o.Insert = _insertCombo!.SelectedIndex == 1 ? AiDrawInsert.NewDocument : AiDrawInsert.NewLayer;
        o.StyleReference = (AiDrawStyleReference)Math.Max(0, _styleCombo!.SelectedIndex);
        o.AnimationMethod = _animCombo!.SelectedIndex == 1 ? AiDrawAnimationMethod.PerFrame : AiDrawAnimationMethod.SingleSheet;
        if (_styleRefCombo!.SelectedIndex is int ri && ri >= 0 && ri < _styleRefIds.Count) o.StyleReferenceId = _styleRefIds[ri];
        foreach (var (action, box) in _frameBoxes) o.FrameCounts[action] = (int)box.NumValue();
        UpdateDrawOptionsEnabled();
    }

    /// <summary>그림 생성에 쓰는 Codex CLI의 설치·로그인 상태를 설정 칸에 보인다.</summary>
    private async Task CheckCodexAsync()
    {
        if (_codexChecking || _codexStatus is null) return;
        _codexChecking = true;
        _codexStatus.Text = UiStrings.AiDraw_Codex_Checking;
        string text;
        try
        {
            var path = AiProvider.Codex.Locate(_settings);
            if (path is null) text = UiStrings.AiDraw_Codex_NotInstalled;
            else
            {
                var status = await AiProvider.Codex.GetAuthStatusAsync(path);
                text = status.LoggedIn ? UiStrings.AiDraw_Codex_Ready
                    : status.Error is null ? UiStrings.AiDraw_Codex_NotLoggedIn
                    : UiStrings.FormatAiDraw_Codex_Unknown(message: status.Error);
            }
        }
        finally
        {
            _codexChecking = false;
        }
        if (!_shutdown) _codexStatus.Text = text;
    }

    /// <summary>"변환 설정 보기": 생성 기록의 원본과 변환 설정으로 정리 창을 연다(기록이 지워졌으면 알림).</summary>
    private void OpenAiDrawJob(string jobId)
    {
        if (_mcp.Tools.Jobs.Load(jobId) is not { } loaded)
        {
            Ui.Error(this, UiStrings.AiDraw_Job_Missing);
            return;
        }
        var (job, source) = loaded;
        _mcp.Tools.Host.ShowPixelate(new PixelateInput(source, job.Name, null), job.Frames, job.ReadSettings());
    }

    /// <summary>AI 그리기 결과 칸: 결과 그림, 결과 글, "변환 설정 보기" 단추(기록이 있을 때).</summary>
    private Control AiDrawCard(Bitmap? image, string text, string? jobId)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(2, 2, 0, 0) };
        if (image is not null) panel.Children.Add(SnapshotView(image));
        if (text.Length > 0) panel.Children.Add(new SelectableTextBlock { Text = text, Foreground = AppTheme.DimTextBrush, TextWrapping = TextWrapping.Wrap });
        if (jobId is not null)
        {
            var button = Ui.Button(VectorIcon.SlidersHorizontal, UiStrings.AiDraw_ViewSettings, (_, _) => OpenAiDrawJob(jobId)).Tip(UiStrings.AiDraw_ViewSettings_Tip);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(button);
        }
        return panel;
    }
}
