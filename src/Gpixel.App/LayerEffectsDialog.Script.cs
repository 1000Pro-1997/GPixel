using Gpixel.App.Localization;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.App;

/// <summary>
/// 효과 창의 스크립트 효과(13.6): 종류 칸의 "새 스크립트"(빛 지나가기 예제로 시작)·"JS 파일 열기", 편집 창(<see cref="ScriptEditorWindow"/>)과 적용,
/// 목록의 오류 표시와 실행 상태 갱신. 이 컴퓨터에서 쓰거나 가져온 원문은 바로 실행을 허락한다(<see cref="ScriptTrustStore.TrustLocal"/>).
/// 원문을 고치면 새 해시로 프로젝트 스크립트 표에 들어가고, 쓰지 않게 된 예전 원문은 저장할 때 빠진다.
/// </summary>
public sealed partial class LayerEffectsDialog
{
    private const string ScriptMarkClass = "script-mark";

    /// <summary>열린 편집 창과 그 창이 고치는 효과(<see cref="ReplaceEffect"/>가 새 사본으로 따라감 — null이면 적용할 때 새 효과로 더함).</summary>
    private ScriptEditorWindow? _scriptEditor;
    private LayerEffect? _scriptEditorTarget;
    private bool _scriptEventsAttached;

    /// <summary>종류 칸(기본 효과 탭) 아래의 스크립트 효과 묶음.</summary>
    private Control BuildScriptSection()
    {
        var title = Ui.Row(Ui.Label(UiStrings.ScriptEffect_PaletteTitle, bold: true), Ui.InfoBadge(UiStrings.ScriptEffect_PaletteHelp));
        var create = Ui.Button(VectorIcon.FilePlus, UiStrings.ScriptEffect_New, (_, _) => AddNewScript());
        ToolTip.SetTip(create, UiStrings.ScriptEffect_NewTip);
        var open = Ui.Button(VectorIcon.FolderOpen, UiStrings.ScriptEffect_OpenFile, (_, _) => OpenScriptFile(-1));
        ToolTip.SetTip(open, UiStrings.ScriptEffect_OpenFileTip);
        var section = Ui.Stack(title, Ui.Row(create, open));
        section.Margin = new Thickness(2, 10, 2, 2);
        return section;
    }

    /// <summary>스크립트 실행 상태·신뢰가 바뀌면 상태 칸을 고치고, 결과가 달라질 수 있으면 모든 프레임을 다시 그린다(이벤트는 작업 스레드에서도 옴).</summary>
    private void AttachScriptEvents()
    {
        _scriptEventsAttached = true;
        ScriptEffectRuntime.StatusChanged += OnScriptStatusChanged;
        ScriptLibrary.Changed += OnScriptLibraryChanged;
        Closed += (_, _) =>
        {
            _scriptEventsAttached = false;
            ScriptEffectRuntime.StatusChanged -= OnScriptStatusChanged;
            ScriptLibrary.Changed -= OnScriptLibraryChanged;
            _scriptEditor?.Close();
        };
    }

    private void OnScriptStatusChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_scriptEventsAttached) RefreshScriptStatus();
    });

    private void OnScriptLibraryChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_scriptEventsAttached) return;
        _effectCache.Clear();
        RestartRenders();
        RefreshScriptStatus();
        // 커스텀 탭 상자의 미리보기·실행 허용 안내도 새로
        ReloadRecipes();
    });

    /// <summary>"새 스크립트": 빛 지나가기 예제 원문으로 효과를 더하고 편집 창을 연다.</summary>
    private void AddNewScript()
    {
        var hash = _project.AddScript(ScriptTemplates.Shine);
        ScriptTrustStore.TrustLocal(hash);
        AddScriptEffect(hash);
        OpenScriptEditor(Selected);
    }

    /// <summary>원문 <paramref name="hash"/>의 기본 설정 효과를 목록 끝에 더하고 고른다.</summary>
    private void AddScriptEffect(string hash)
    {
        var program = ScriptLibrary.Runnable(hash);
        Add([new ScriptEffect { ScriptHash = hash, Settings = program is null ? ScriptSettingValues.Empty : ScriptSettingValues.Normalized(program, null) }],
            _effects.Count);
    }

    /// <summary>
    /// JS 파일 열기: <paramref name="index"/>번이 스크립트 효과면 그 원문을 바꾸고, 아니면(-1) 새 효과로 더한다.
    /// 문법·선언 오류가 있으면 바꾸거나 더하지 않고 편집 창에 그 원문과 오류를 띄운다(고쳐서 적용하면 반영).
    /// </summary>
    private void OpenScriptFile(int index)
    {
        var path = Ui.OpenFile(this, UiStrings.ScriptEffect_OpenFileTitle, null, new FileFilter(UiStrings.ScriptEffect_FileFilter, "*.js"));
        if (path is null) return;
        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, UiStrings.FormatScriptEffect_ReadError(message: ex.Message));
            return;
        }
        var target = index >= 0 && index < _effects.Count ? _effects[index] as ScriptEffect : null;
        var errors = ApplyScriptSource(target, source, out _);
        if (errors.Count == 0) return;
        OpenScriptEditor(target is null ? -1 : index, source);
        _scriptEditor?.ShowErrors(errors);
    }

    /// <summary>
    /// 편집 창을 연다: <paramref name="index"/>번 효과의 원문(<paramref name="source"/>가 있으면 그 글), 효과가 없으면(-1) 적용할 때 새 효과로 더한다.
    /// 이미 열려 있으면 그 창을 닫고 새로 연다.
    /// </summary>
    private void OpenScriptEditor(int index, string? source = null)
    {
        var target = index >= 0 && index < _effects.Count ? _effects[index] as ScriptEffect : null;
        source ??= target is null ? null : ScriptLibrary.SourceOf(target.ScriptHash);
        if (source is null) return;
        _scriptEditor?.Close();
        _scriptEditorTarget = target;
        ScriptEditorWindow? editor = null;
        editor = new ScriptEditorWindow(target?.DisplayName ?? UiStrings.ScriptEffect_Name, source, text =>
        {
            var errors = ApplyScriptSource(_scriptEditorTarget as ScriptEffect, text, out var applied);
            if (applied is not null)
            {
                _scriptEditorTarget = applied;
                editor!.Title = UiStrings.FormatScriptEditor_Title(name: applied.DisplayName);
            }
            RefreshScriptStatus();
            return errors;
        });
        editor.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_scriptEditor, editor)) return;
            _scriptEditor = null;
            _scriptEditorTarget = null;
        };
        _scriptEditor = editor;
        editor.Show(this);
        RefreshScriptStatus();
    }

    /// <summary>
    /// 원문 적용: 해석해 오류가 있으면 그 목록을 돌려준다. 없으면 프로젝트 스크립트 표에 넣고 실행을 허락한 뒤, <paramref name="target"/>의 원문을 바꾸거나
    /// (설정은 새 schema에 맞춰 같은 키 값을 이어 씀) 대상이 없으면(목록에서 빠졌어도) 새 효과로 더한다. <paramref name="applied"/>는 바뀌거나 더해진 효과.
    /// </summary>
    private IReadOnlyList<ScriptError> ApplyScriptSource(ScriptEffect? target, string source, out LayerEffect? applied)
    {
        applied = null;
        var parsed = ScriptEffectProgram.Parse(source);
        if (!parsed.Success) return parsed.Errors;
        var hash = _project.AddScript(source);
        ScriptTrustStore.TrustLocal(hash);
        if (target is not null && IndexOfEffect(target) is var i and >= 0)
        {
            var program = ScriptLibrary.Runnable(hash)!;
            Update(i, target with { ScriptHash = hash, Settings = ScriptSettingValues.Normalized(program, target.Settings) });
            if (Selected == i) ShowSettings();
            else _list.SelectedIndex = i;
            applied = _effects[i];
        }
        else
        {
            AddScriptEffect(hash);
            applied = _effects[Selected];
        }
        return [];
    }

    /// <summary>
    /// <paramref name="index"/>번 스크립트 효과의 실패 기록을 지워 다시 계산하게 한다. 캔버스는 목록 값, 창 미리보기는 늘린 뒤 범위의 값으로 기록하므로 둘 다.
    /// </summary>
    private void ResumeScript(int index)
    {
        if (index < 0 || index >= _effects.Count || _effects[index] is not ScriptEffect script) return;
        ScriptEffectRuntime.Resume(_layer, script);
        if (VirtualEffects()[index] is ScriptEffect shown && shown != script) ScriptEffectRuntime.Resume(_layer, shown);
    }

    private int IndexOfEffect(LayerEffect effect) => _effects.FindIndex(e => ReferenceEquals(e, effect));

    /// <summary>
    /// 고른 스크립트 효과의 원문을 개인 스크립트 에셋으로 저장한다(실행을 허용한 원문만 — 개인 에셋은 이 컴퓨터에서 믿는 스크립트다).
    /// 같은 원문이 이미 개인 에셋이면 그대로 둔다. 커스텀 탭은 저장소 변경 알림으로 다시 만들어진다.
    /// </summary>
    private void SaveScriptAsset(ScriptEffect effect)
    {
        if (ScriptLibrary.SourceOf(effect.ScriptHash) is not { } source || ScriptLibrary.TrustOf(effect.ScriptHash) != ScriptTrust.Trusted) return;
        AppAssets.Try(this, () => AppAssets.Store.SavePersonalScript(source), UiStrings.ScriptEffect_SaveAssetError);
    }

    /// <summary>효과 줄마다 스크립트 효과가 그리지 못하는 상태면 경고 아이콘과 원인 풍선을 붙인다(묶음 머리 줄은 건너뜀).</summary>
    private void RefreshScriptMarks()
    {
        var shown = VirtualEffects();
        for (int i = 0; i < _list.Items.Count && i < _rows.Count; i++)
        {
            if (_list.Items[i] is not ListBoxItem { Content: StackPanel row }) continue;
            if (row.Children.FirstOrDefault(c => c.Classes.Contains(ScriptMarkClass)) is { } old) row.Children.Remove(old);
            if (_rows[i] is not { Recipe: null, First: var at } || at >= shown.Count) continue;
            if (shown[at] is not ScriptEffect script || ScriptEffectRuntime.StatusOf(_layer, script) is not { IsProblem: true } status) continue;
            var mark = WarningIcon();
            mark.Classes.Add(ScriptMarkClass);
            mark.Margin = new Thickness(4, 0, 0, 0);
            ToolTip.SetTip(mark, ScriptStatusText.Of(status));
            row.Children.Add(mark);
        }
    }

    private static IconView WarningIcon()
    {
        var icon = Ui.Icon(VectorIcon.CircleAlert, 13);
        icon.Foreground = AppTheme.WarningBrush;
        return icon;
    }
}
