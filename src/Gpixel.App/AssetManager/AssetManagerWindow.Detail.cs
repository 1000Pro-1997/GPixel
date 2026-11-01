using System.Globalization;
using Gpixel.App.Localization;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;
using Gpixel.Core.Scripting;

namespace Gpixel.App;

/// <summary>에셋 관리자 오른쪽 상세와 동작(가져오기·내보내기, 켜기·끄기, 되돌리기, 삭제, 개인 에셋 고치기).</summary>
public sealed partial class AssetManagerWindow
{
    private static string F(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private void ShowDetail()
    {
        Detail.Children.Clear();
        switch (Selected)
        {
            case { Package: { } package }: PackageDetail(package); break;
            case { Asset: { } asset }: AssetDetail(asset); break;
            case { BuiltInEffect: { } effect }: BuiltInEffectDetail(effect); break;
            case { Deleted: { } deleted }: DeletedDetail(deleted); break;
            case { DeletedPersonal: { } deleted }: DeletedPersonalDetail(deleted); break;
            default: Detail.Children.Add(Dim(UiStrings.Assets_SelectHint)); break;
        }
    }

    private StackPanel Detail => _tabs.SelectedIndex == 3 ? _trashDetail : _detail;

    private void AddTitle(string text, bool hidden = false)
    {
        var title = TitleText(text);
        if (hidden) title.Foreground = HiddenTitleBrush;
        Detail.Children.Add(title);
    }

    private static TextBlock TitleText(string text)
        => new() { Text = text, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = AppTheme.TextBrush, TextWrapping = TextWrapping.Wrap };

    private void Facts(params (string Label, string? Value)[] facts) => Detail.Children.Add(FactsGrid(facts));

    /// <summary>이름: 값 표(값이 비면 그 줄을 뺀다).</summary>
    private static Grid FactsGrid(params (string Label, string? Value)[] facts)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2, 0, 4) };
        int row = 0;
        foreach (var (label, value) in facts.Where(f => !string.IsNullOrEmpty(f.Value)))
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = new TextBlock { Text = label, Foreground = AppTheme.DimTextBrush, Margin = new Thickness(0, 1, 12, 1) };
            var text = new SelectableTextBlock { Text = value, Foreground = AppTheme.TextBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) };
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
            row++;
        }
        return grid;
    }

    private void Actions(params Control?[] buttons) => Detail.Children.Add(ActionPanel(buttons));

    private static WrapPanel ActionPanel(params Control?[] buttons)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var button in buttons.OfType<Control>())
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            panel.Children.Add(button);
        }
        return panel;
    }

    private void Issues(IReadOnlyList<AssetIssue> issues)
    {
        if (issues.Count == 0) return;
        Detail.Children.Add(new TextBlock
        {
            Text = UiStrings.Assets_IssuesHeader + AppAssets.IssueText(issues),
            Foreground = AppTheme.WarningBrush,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void PackageDetail(InstalledPackage package)
    {
        var manifest = package.Manifest;
        AddTitle(package.Name);
        if (manifest?.Description is { Length: > 0 } description)
            Detail.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.TextBrush });
        Facts(
            (UiStrings.Assets_Status, package.IsBroken ? UiStrings.Assets_Broken : package.Enabled ? UiStrings.Assets_Enabled : UiStrings.Assets_Disabled),
            (UiStrings.Assets_Id, package.Id),
            (UiStrings.Assets_Version, package.Version),
            (UiStrings.Assets_Author, manifest?.Author.Name),
            (UiStrings.Assets_License, manifest?.License),
            (UiStrings.Assets_MinApp, manifest?.MinAppVersion),
            (UiStrings.Assets_Source, SourceText(package.State.Source)),
            (UiStrings.Assets_InstalledAt, package.State.InstalledUtc == default ? null : package.State.InstalledUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
            (UiStrings.Assets_Contents, manifest is null ? null : ContentsText(package)));
        if (package.Entries.Any(e => e.ScriptHashes.Count > 0)) ScriptNote();
        Issues(package.Issues);

        var toggle = Ui.Button(package.Enabled ? VectorIcon.EyeOff : VectorIcon.Eye, package.Enabled ? UiStrings.Assets_Disable : UiStrings.Assets_Enable,
            (_, _) => Act(() => AppAssets.Store.SetEnabled(package.Id, !package.Enabled)));
        ToolTip.SetTip(toggle, UiStrings.Assets_ToggleTip);
        var export = manifest is null ? null : Ui.Button(VectorIcon.Upload, UiStrings.Assets_Export, (_, _) => ExportPackage(package));
        var previous = package.State.PreviousVersions.FirstOrDefault();
        var rollback = previous is null ? null
            : Ui.Button(VectorIcon.Undo2, F(UiStrings.Assets_Rollback, previous), (_, _) => Act(() => AppAssets.Store.Rollback(package.Id)));
        var folder = Ui.Button(VectorIcon.FolderOpen, UiStrings.Assets_OpenFolder, (_, _) => Platform.OpenFolder(package.Folder));
        var web = manifest?.Author.Url is { } url ? Ui.Button(VectorIcon.ExternalLink, UiStrings.Assets_WebPage, (_, _) => Updater.OpenUrl(this, url)).Tip(url) : null;
        var licenseFile = AssetPackageValidator.RootDocuments.Where(name => name.StartsWith("LICENSE", StringComparison.Ordinal))
            .Select(name => Path.Combine(package.Folder, name)).FirstOrDefault(File.Exists);
        var license = licenseFile is null ? null : Ui.Button(VectorIcon.Info, UiStrings.Assets_ViewLicense, (_, _) => ShowLicense(package, licenseFile));
        var delete = package.IsOfficial ? null : Ui.Button(VectorIcon.Trash, UiStrings.Assets_Delete, (_, _) => DeletePackage(package));
        Actions(toggle, export, rollback, folder, web, license, delete);
    }

    private static string SourceText(string source) => source switch
    {
        "file" => UiStrings.Assets_SourceFile,
        AppAssets.RemoteSource => UiStrings.Assets_SourceRemote,
        _ => source,
    };

    /// <summary>종류마다 개수(효과 레시피, 그라데이션, 스크립트 효과 차례 — 없는 종류는 뺌).</summary>
    private static string ContentsText(InstalledPackage package)
        => string.Join(", ", package.Entries.GroupBy(e => e.Kind).OrderBy(group => KindOrder(group.Key)).Select(group => group.Key switch
        {
            AssetKinds.EffectRecipe => F(UiStrings.Assets_ContentsRecipes, group.Count()),
            AssetKinds.Gradient => F(UiStrings.Assets_ContentsGradients, group.Count()),
            AssetKinds.EffectScript => F(UiStrings.Assets_ContentsScripts, group.Count()),
            _ => F(UiStrings.Assets_ContentsOther, group.Key, group.Count()),
        }));

    /// <summary>목록·내용 글의 종류 차례.</summary>
    private static int KindOrder(string kind) => kind switch
    {
        AssetKinds.EffectRecipe => 0,
        AssetKinds.Gradient => 1,
        AssetKinds.EffectScript => 2,
        _ => 3,
    };

    internal static string KindText(string kind) => kind switch
    {
        AssetKinds.Gradient => UiStrings.Assets_KindGradient,
        AssetKinds.EffectScript => UiStrings.Assets_KindScript,
        _ => UiStrings.Assets_KindEffect,
    };

    /// <summary>스크립트가 든 패키지·에셋의 실행 허락 안내(주황 글씨).</summary>
    private void ScriptNote() => Detail.Children.Add(new TextBlock
    {
        Text = UiStrings.Assets_ScriptNote, Foreground = AppTheme.WarningBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
    });

    private void AssetDetail(AssetEntry entry)
    {
        AddTitle(entry.Name, hidden: !entry.Enabled);
        if (entry.Gradient is { } gradient)
            Detail.Children.Add(new GradientSwatch { Preset = gradient, Height = 48 });
        if (entry.EffectTemplate is not null || entry.PreviewPath is not null)
            Detail.Children.Add(new AssetPreviewView(entry));

        var package = entry.PackageId is { } id ? AppAssets.Catalog.FindPackage(id) : null;
        var scripts = entry.ScriptHashes;
        bool allowed = scripts.All(hash => ScriptLibrary.TrustOf(hash) == ScriptTrust.Trusted);
        Facts(
            (UiStrings.Assets_Kind, KindText(entry.Kind)),
            (UiStrings.Assets_Source, entry.IsBuiltIn ? UiStrings.Assets_SourceBuiltIn : entry.Origin == AssetOrigin.Personal ? UiStrings.Assets_SourcePersonal : SourceText(package?.State.Source ?? "file")),
            (UiStrings.Assets_Status, !entry.Enabled ? UiStrings.Assets_Disabled : null),
            (UiStrings.Assets_Author, package?.Manifest?.Author.Name ?? entry.Script?.Author),
            (UiStrings.Assets_Version, package?.Version),
            (UiStrings.Assets_MinApp, package?.Manifest?.MinAppVersion),
            (UiStrings.Assets_Id, entry.Origin == AssetOrigin.Package ? entry.Id : null),
            (UiStrings.Assets_ScriptHash, entry.Script is { } script ? script.Hash[..12] : null),
            (UiStrings.Assets_ScriptRun, scripts.Count == 0 ? null : allowed ? UiStrings.Assets_ScriptRunAllowed : UiStrings.Assets_ScriptRunNotAllowed));
        if (scripts.Count > 0 && !allowed) ScriptNote();
        if (entry.Script is { } declared)
            Detail.Children.Add(Dim(F(UiStrings.Assets_ScriptSettingCount, declared.Settings.Count)
                                     + (declared.Settings.Count > 0 ? ": " + string.Join(", ", declared.Settings.Select(s => s.Label)) : "")));

        if (entry.Recipe is { } recipe)
        {
            if (recipe.Description is { Length: > 0 } description)
                Detail.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.TextBrush });
            Detail.Children.Add(Ui.Label(F(UiStrings.Assets_EffectCount, recipe.Effects.Count), bold: true));
            foreach (var effect in recipe.Effects)
                Detail.Children.Add(Dim("  " + effect.DisplayName + (effect.ClipToPrevious ? UiStrings.EffectPreset_ClippedSuffix : "")));
            Detail.Children.Add(Dim(recipe.FrameRange == RecipeFrameRange.AllFrames ? UiStrings.Assets_FrameRangeAll : UiStrings.Assets_FrameRangeAdd));
            if (recipe.Parameters.Count > 0)
            {
                Detail.Children.Add(Ui.Label(F(UiStrings.RecipeParams_Count, recipe.Parameters.Count), bold: true));
                foreach (var parameter in recipe.Parameters)
                    Detail.Children.Add(Dim("  " + parameter.Name + " · " + RecipeParametersDialog.KindText(parameter)));
            }
        }

        var toggle = entry.IsBuiltIn
            ? Ui.Button(entry.Enabled ? VectorIcon.EyeOff : VectorIcon.Eye, entry.Enabled ? UiStrings.Assets_Disable : UiStrings.Assets_Enable,
                (_, _) => Act(() => AppAssets.Store.SetPersonalEnabled(entry.Id, !entry.Enabled)))
            : package is null ? null
                : Ui.Button(entry.Enabled ? VectorIcon.EyeOff : VectorIcon.Eye, entry.Enabled ? UiStrings.Assets_Disable : UiStrings.Assets_Enable,
                    (_, _) => Act(() => AppAssets.Store.SetPackageAssetEnabled(entry.Id, !entry.Enabled)));

        var favorite = entry.Kind == AssetKinds.Gradient
            ? Ui.Button(VectorIcon.Star, entry.Favorite ? UiStrings.GradientPreset_RemoveFavorite : UiStrings.GradientPreset_AddFavorite,
                (_, _) => Act(() => AppAssets.Store.SetFavorite(entry.Id, !entry.Favorite)))
            : null;
        if (favorite?.Content is StackPanel favoriteContent)
        {
            var star = Ui.Icon(VectorIcon.Star, filled: entry.Favorite);
            star.Foreground = AppTheme.FavoriteBrush;
            favoriteContent.Children[0] = star;
        }
        if (entry.Origin == AssetOrigin.Personal)
            Actions(
                toggle, favorite,
                entry.Script is not null ? null : Ui.Button(VectorIcon.Pencil, UiStrings.Assets_Rename, (_, _) => RenamePersonal(entry)),
                entry.Recipe is null ? null
                    : Ui.Button(VectorIcon.SlidersHorizontal, UiStrings.RecipeParams_Edit, (_, _) => RecipeParametersDialog.Edit(this, entry)).Tip(UiStrings.RecipeParams_EditTip),
                Ui.Button(VectorIcon.Upload, UiStrings.Assets_Export, (_, _) => ExportPersonal(entry)),
                Ui.Button(VectorIcon.Globe, UiStrings.Assets_Publish, (_, _) => PublishPersonal(entry)).Tip(UiStrings.Assets_PublishEntryTip),
                entry.IsBuiltIn ? null : Ui.Button(VectorIcon.Trash, UiStrings.Assets_Delete, (_, _) => DeletePersonal(entry)));
        else
            Actions(toggle, favorite, package is { IsOfficial: false }
                ? Ui.Button(VectorIcon.Trash, package.Entries.Count > 1 ? UiStrings.Assets_DeletePackage : UiStrings.Assets_Delete,
                    (_, _) => DeletePackage(package)) : null);
    }

    private void DeletedDetail(DeletedPackage deleted)
    {
        AddTitle(deleted.Name);
        Facts(
            (UiStrings.Assets_Id, deleted.Id),
            (UiStrings.Assets_Version, deleted.Version),
            (UiStrings.Assets_DeletedAt, deleted.DeletedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        Detail.Children.Add(Dim(F(UiStrings.Assets_DeletedNote, AssetStore.MaxDeletedItems)));
        Actions(Ui.Button(VectorIcon.Undo2, UiStrings.Assets_Restore, (_, _) =>
        {
            if (Act(() => AppAssets.Store.RestoreDeleted(deleted)))
            {
                ShowInstalled(deleted.Id);
            }
        }));
    }

    private void BuiltInEffectDetail(Gpixel.Core.Effects.EffectDefinition effect)
    {
        var ui = LayerEffectsDialog.UiOf(effect);
        bool enabled = AppAssets.Store.IsBuiltInEffectEnabled(effect.TypeId);
        AddTitle(ui.Name, hidden: !enabled);
        Detail.Children.Add(new AssetPreviewView(effect) { Height = 160 });
        Detail.Children.Add(Ui.Label(ui.Description));
        Facts((UiStrings.Assets_Source, UiStrings.Assets_SourceBuiltIn),
            (UiStrings.Assets_Status, enabled ? UiStrings.Assets_Enabled : UiStrings.Assets_Disabled),
            (UiStrings.Assets_Id, effect.TypeId));
        Actions(Ui.Button(enabled ? VectorIcon.EyeOff : VectorIcon.Eye, enabled ? UiStrings.Assets_Disable : UiStrings.Assets_Enable,
            (_, _) => Act(() => AppAssets.Store.SetBuiltInEffectEnabled(effect.TypeId, !enabled))));
    }

    private void DeletedPersonalDetail(DeletedPersonalAsset deleted)
    {
        AddTitle(deleted.Name);
        Facts((UiStrings.Assets_Kind, KindText(deleted.Kind)),
            (UiStrings.Assets_DeletedAt, deleted.DeletedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        Detail.Children.Add(Dim(F(UiStrings.Assets_DeletedNote, AssetStore.MaxDeletedItems)));
        Actions(Ui.Button(VectorIcon.Undo2, UiStrings.Assets_Restore, (_, _) =>
        {
            if (Act(() => AppAssets.Store.RestoreDeletedPersonal(deleted)))
            {
                _selectKey = "asset:" + deleted.Id;
                _filter.SelectedIndex = deleted.Kind == AssetKinds.Gradient ? 1 : 0;
                _showHidden.IsChecked = AppAssets.Catalog.Find(deleted.Id)?.Enabled == false;
                _tabs.SelectedIndex = 0;
                Refresh();
            }
        }));
    }

    // ───────────── 동작 ─────────────

    /// <summary>저장소를 바꾸고 실패하면 알린다(목록은 <see cref="AppAssets.Changed"/>로 다시 만들어진다).</summary>
    private bool Act(Action action) => AppAssets.Try(this, action, UiStrings.Assets_ActionError);

    private static FileFilter PackageFilter => new(UiStrings.Assets_FileType, "*" + AssetPackage.Extension);

    private void Import()
    {
        if (Ui.OpenFile(this, UiStrings.Assets_ImportTitle, null, PackageFilter) is not { } path) return;
        InstalledPackage? installed = null;
        if (!AppAssets.Try(this, () => installed = AppAssets.Store.Install(path), UiStrings.Assets_ImportError) || installed is null) return;
        _selectKey = "pkg:" + installed.Id;
        _search.Text = "";
        Refresh();
    }

    private void ExportPackage(InstalledPackage package)
    {
        string suggested = $"{package.Id}-{package.Version}{AssetPackage.Extension}";
        if (Ui.SaveFile(this, UiStrings.Assets_ExportTitle, null, suggested, AssetPackage.Extension, PackageFilter) is not { } path) return;
        Act(() =>
        {
            using var buffer = new MemoryStream();
            AppAssets.Store.ExportPackage(package.Id, buffer);
            File.WriteAllBytes(path, buffer.ToArray());
        });
    }

    /// <summary>
    /// 개인 에셋을 새 패키지로 내보낸다(개인 백업·전달용, 온라인 카탈로그에는 올릴 수 없음): <paramref name="single"/>이 있으면 그것만,
    /// 없으면 지금 종류 거르기에 맞는 개인 에셋 전부. 패키지 이름만 묻고, ID는 <c>personal.이름</c>, 버전 1.0.0, 라이선스 <c>LicenseRef-Personal</c>,
    /// 작성자는 개인(<c>personal</c>) + 게시 창에 기억한 표시 이름(없으면 이 컴퓨터 사용자 이름). 공개 게시는 <see cref="PublishPersonal"/>.
    /// </summary>
    private void ExportPersonal(AssetEntry? single)
    {
        var entries = single is not null ? [single] : AppAssets.Catalog.Personal.Where(Matches).ToList();
        if (entries.Count == 0)
        {
            Ui.Info(this, UiStrings.Assets_PersonalExportEmpty, UiStrings.Assets_ExportTitle);
            return;
        }
        string? name = InputDialog.Ask(this, UiStrings.Assets_ExportTitle, UiStrings.Assets_ExportNamePrompt, single?.Name ?? UiStrings.Assets_DefaultPackageName);
        if (name is null) return;
        string slug = AssetPackageBuilder.Slug(name, "assets");
        if (Ui.SaveFile(this, UiStrings.Assets_ExportTitle, null, slug + AssetPackage.Extension, AssetPackage.Extension, PackageFilter) is not { } path) return;
        string profile = App.Settings.AssetPublish.AuthorName;
        string user = AssetManifest.IsValidName(profile) ? profile.Trim()
            : Environment.UserName is { Length: > 0 } u && AssetManifest.IsValidName(u) ? u : "Gpixel";
        var header = new AssetManifest
        {
            Id = "personal." + slug,
            Version = "1.0.0",
            Name = name.Length > AssetManifest.MaxNameLength ? name[..AssetManifest.MaxNameLength] : name,
            Author = new AssetAuthor("personal", user),
            License = "LicenseRef-Personal",
            MinAppVersion = AppInfo.IsDevBuild ? "0.0.0" : AppInfo.VersionText,
        };
        Act(() =>
        {
            using var buffer = new MemoryStream();
            AppAssets.Store.ExportPersonal(entries.Select(e => e.Id), header, buffer, entry => AssetPreview.Png(entry.Data));
            File.WriteAllBytes(path, buffer.ToArray());
        });
    }

    /// <summary>
    /// 온라인에 올리기(게시 창): <paramref name="single"/>이 있으면 그것을, 없으면 목록에서 고른 개인 에셋(고른 것이 없으면 목록에 보이는 개인 에셋 전부)을
    /// 처음 체크해 연다. 게시 창 안에서 포함할 에셋을 더 고를 수 있다.
    /// </summary>
    private void PublishPersonal(AssetEntry? single)
    {
        var preselected = single is not null ? [single]
            : Selected?.Asset is { Origin: AssetOrigin.Personal } selected ? [selected]
            : AppAssets.Catalog.Personal.Where(Matches).ToList();
        AssetPublishDialog.Run(this, preselected.Select(entry => entry.Id));
    }

    private void DeletePackage(InstalledPackage package)
    {
        if (Ui.Ask(this, F(UiStrings.Assets_DeleteQuestion, package.Name, AssetStore.MaxDeletedItems), UiStrings.Assets_Delete,
                MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes) return;
        Act(() => AppAssets.Store.Delete(package.Id));
    }

    private void ShowLicense(InstalledPackage package, string file)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = ex.Message;
        }
        if (text.Length > 4000) text = text[..4000] + "…";
        Ui.Info(this, text, package.Name + " · " + UiStrings.Assets_License);
    }

    private void RenamePersonal(AssetEntry entry)
    {
        var siblings = AppAssets.Catalog.Personal.Where(e => e.Kind == entry.Kind && e.Id != entry.Id).Select(e => e.Name).ToList();
        string? name = InputDialog.Ask(this, UiStrings.Assets_Rename, UiStrings.Assets_NamePrompt, entry.Name, siblings)?.Trim();
        if (string.IsNullOrEmpty(name) || name == entry.Name) return;
        if (siblings.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Warn(this, F(UiStrings.Assets_DuplicateName, name), UiStrings.Assets_Rename);
            return;
        }
        Act(() =>
        {
            if (entry.Recipe is { } recipe) AppAssets.Store.SavePersonalRecipe(recipe with { Name = name });
            else if (entry.Gradient is { } gradient) AppAssets.Store.SavePersonalGradient(entry.Id, gradient with { Name = name });
        });
    }

    private void DeletePersonal(AssetEntry entry)
    {
        if (Ui.Ask(this, F(UiStrings.Assets_DeletePersonalQuestion, entry.Name, AssetStore.MaxDeletedItems), UiStrings.Assets_Delete,
                MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes) return;
        Act(() => AppAssets.Store.RemovePersonal(entry.Id));
    }

}
